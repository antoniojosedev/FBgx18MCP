//! Snapshot cache for native Knowledge Base catalogs.
//!
//! A snapshot of a KB catalog is reused only when a change key read from that KB matches the key the
//! snapshot was written under. The key is three read-only aggregates over the version tables
//! (`EntityVersion`, `ModelEntityVersion` per model, `EntityVersionComposition`): row counts, maximum
//! timestamps and checksums of the row identities. It does not hash the version data bytes.
//!
//! Limit: an in-place edit that keeps every version row's identity and the row counts is not detected.
//! Every change observed on the reference KBs created a new version row, but that is an observed
//! convention, not a guarantee of the engine. A stale snapshot can only be served under that condition.
//! The weak key is therefore not shippable on its own; a content key is still to be built.
//!
//! Publication: one snapshot file per KB holds an envelope, the full key text and the catalog together.
//! A writer builds the whole envelope in a unique temporary file in the same directory and renames it over
//! the snapshot. A reader sees the previous envelope or the new one, never a catalog paired with another
//! key, and it uses a catalog only when the envelope's key equals its own key exactly.
//!
//! A snapshot is a cache, never a source of truth: a missing, unreadable or mismatched snapshot is
//! rebuilt from the KB, and the KB is never written.
//!
//! A snapshot keeps each object's parts and references in the order the KB reader produced them, so a
//! build from the snapshot is the same build as one from the live read (not re-sorted on load).

use std::fs::{self, File, OpenOptions};
use std::io::{ErrorKind, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use crate::catalog::{Catalog, MAX_CATALOG_BYTES};
use crate::udm::client::UdmClient;
use crate::udm::reader::UdmReader;

/// The change key of a native KB: three read-only aggregates, one sqlcmd call.
pub const CHANGE_KEY_SQL: &str = "SELECT COUNT(*), MAX(EntityVersionTimestamp), CHECKSUM_AGG(CHECKSUM(EntityTypeId, EntityId, EntityVersionId)) FROM EntityVersion; SELECT ModelId, COUNT(*), MAX(ModelEntityVersionTimestamp), CHECKSUM_AGG(CHECKSUM(EntityTypeId, EntityId, EntityVersionId, ModelEntityVersionName)) FROM ModelEntityVersion GROUP BY ModelId ORDER BY ModelId; SELECT COUNT(*) FROM EntityVersionComposition;";

/// What the cache did for one load.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CacheStatus {
    /// The snapshot matched the key and was loaded; the KB was not read for objects.
    Hit,
    /// No snapshot for this key: the KB was read and a snapshot written.
    Miss,
    /// A snapshot matched the key but could not be loaded: the KB was read and the snapshot rewritten.
    Replaced,
    /// The cache was not used; the KB was read directly. The reason is stated.
    Uncached(String),
}

/// The stages of publishing one snapshot file, reported to the observer of `publish_snapshot`. The
/// observer runs on the calling thread between the stages; it lets a test read the cache at each one.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PublishStage {
    /// The complete envelope is written and synced in a temporary file; the snapshot is unchanged.
    TempWritten,
    /// The temporary file has replaced the snapshot; readers now see the new envelope.
    Renamed,
}

/// Whether `load_native_kb` serves snapshots at all. Off: the change key above does not identify the catalog's
/// content (an in-place edit with unchanged identities and counts keeps the key), so a hit could serve stale
/// objects. It stays off until a content identity is in place and gated. The publication and key-matching
/// rules are tested through `load_or_build` regardless of this switch.
pub const CACHE_ENABLED: bool = false;

/// The status reported when the cache is requested but switched off.
pub const CACHE_DISABLED_REASON: &str =
    "catalog cache disabled: the change key does not identify catalog content yet";

/// Loads a native KB catalog. With the cache switched off, the KB is read directly and the status says why.
pub fn load_native_kb(
    kb_dir: &Path,
    cache_dir: &Path,
    design_model_id: i32,
) -> Result<(Catalog, CacheStatus), String> {
    let client = Arc::new(
        UdmClient::connect_kb(kb_dir)?
            .with_persistent_reads()
            .with_design_model_id(design_model_id),
    );
    if !CACHE_ENABLED {
        let _ = cache_dir;
        let catalog = UdmReader::new(client).read_catalog()?;
        return Ok((
            catalog,
            CacheStatus::Uncached(CACHE_DISABLED_REASON.to_string()),
        ));
    }
    let key = match client.execute_live_sql(CHANGE_KEY_SQL) {
        Ok(key) => key,
        Err(reason) => {
            let catalog = UdmReader::new(client).read_catalog()?;
            return Ok((catalog, CacheStatus::Uncached(reason)));
        }
    };
    load_or_build(cache_dir, kb_dir, &key, || {
        UdmReader::new(client).read_catalog()
    })
}

/// Returns the snapshot for `key` when it exists and loads; otherwise calls `build`, publishes the snapshot,
/// and returns the built catalog. A publish failure does not fail the load: the built catalog is returned
/// as `Uncached` with the reason.
pub fn load_or_build(
    cache_dir: &Path,
    kb_dir: &Path,
    key: &str,
    build: impl FnOnce() -> Result<Catalog, String>,
) -> Result<(Catalog, CacheStatus), String> {
    let mut status = CacheStatus::Miss;
    match read_snapshot(cache_dir, kb_dir, key) {
        Ok(Some(catalog)) => return Ok((catalog, CacheStatus::Hit)),
        Ok(None) => {}
        Err(_) => status = CacheStatus::Replaced,
    }
    let catalog = build()?;
    if let Err(reason) = publish_snapshot(cache_dir, kb_dir, key, &catalog, &mut |_| {}) {
        return Ok((catalog, CacheStatus::Uncached(reason)));
    }
    Ok((catalog, status))
}

/// Reads the snapshot for `key` without writing anything. `Ok(Some)` is a hit, `Ok(None)` a miss (no
/// snapshot, or one written under another key), and `Err` a snapshot that exists for this key or is
/// unreadable. The key check is exact: the envelope's embedded key text must equal the key text of this KB.
pub fn read_snapshot(
    cache_dir: &Path,
    kb_dir: &Path,
    key: &str,
) -> Result<Option<Catalog>, String> {
    let path = snapshot_path(cache_dir, kb_dir);
    let Some(text) = read_bounded(&path)? else {
        return Ok(None);
    };
    let (stored_key, snapshot) =
        decode_envelope(&text).map_err(|reason| format!("cache snapshot rejected: {reason}"))?;
    if stored_key != key_text(kb_dir, key) {
        return Ok(None);
    }
    Catalog::from_snapshot_text(snapshot).map(Some)
}

/// Publishes the snapshot of `catalog` under `key`: one envelope, written to a unique temporary file and
/// renamed over the snapshot. `observe` runs at each `PublishStage`. Earlier per-file snapshot files of
/// this KB are removed after the rename; a failure to remove them is ignored.
pub fn publish_snapshot(
    cache_dir: &Path,
    kb_dir: &Path,
    key: &str,
    catalog: &Catalog,
    observe: &mut dyn FnMut(PublishStage),
) -> Result<(), String> {
    fs::create_dir_all(cache_dir)
        .map_err(|error| format!("cannot create the catalog cache directory: {error}"))?;
    let snapshot = catalog.snapshot_text()?;
    let envelope = encode_envelope(&key_text(kb_dir, key), &snapshot);
    let path = snapshot_path(cache_dir, kb_dir);
    publish_file(&path, envelope.as_bytes(), observe)?;
    for legacy in legacy_paths(cache_dir, kb_dir) {
        let _ = fs::remove_file(legacy);
    }
    Ok(())
}

/// The one snapshot file for a KB. Its name derives from the canonical KB path; the envelope carries the
/// full key text, and a hit requires an exact match, so a name collision cannot serve a stale snapshot.
pub fn snapshot_path(cache_dir: &Path, kb_dir: &Path) -> PathBuf {
    cache_dir.join(format!("{}.snapshot.json", kb_stem(kb_dir)))
}

fn kb_stem(kb_dir: &Path) -> String {
    format!("kb-{:016x}", fnv1a64(canonical_kb(kb_dir).as_bytes()))
}

/// The per-file layout of earlier builds (a catalog file and a key file). Nothing reads them; they are
/// removed when a snapshot is published so they cannot be mistaken for current state.
fn legacy_paths(cache_dir: &Path, kb_dir: &Path) -> [PathBuf; 2] {
    let stem = kb_stem(kb_dir);
    [
        cache_dir.join(format!("{stem}.catalog.json")),
        cache_dir.join(format!("{stem}.key")),
    ]
}

fn canonical_kb(kb_dir: &Path) -> String {
    kb_dir
        .canonicalize()
        .unwrap_or_else(|_| kb_dir.to_path_buf())
        .to_string_lossy()
        .into_owned()
}

/// Version of the snapshot object schema. A snapshot written under another version is never served:
/// its objects may lack a field or decoded part this build reads (`DP_OUTPUT` and typed DataProvider
/// variables, v4: `module_path`), so a hit would keep an old classification. Bump it with any change to what a snapshot holds.
pub const SNAPSHOT_SCHEMA: &str = "catalog-snapshot-schema-7";

fn key_text(kb_dir: &Path, key: &str) -> String {
    format!("{SNAPSHOT_SCHEMA}\n{}\n{key}", canonical_kb(kb_dir))
}

const ENVELOPE_MAGIC: &str = "gx-catalog-snapshot-envelope-1";

/// Frames the key and the snapshot in one text: a magic line, the key's length in bytes, the key, then the
/// snapshot JSON. The length makes the key text unambiguous even though it contains line breaks.
fn encode_envelope(key: &str, snapshot: &str) -> String {
    format!("{ENVELOPE_MAGIC}\n{}\n{key}\n{snapshot}", key.len())
}

fn decode_envelope(text: &str) -> Result<(&str, &str), String> {
    let rest = text
        .strip_prefix(ENVELOPE_MAGIC)
        .and_then(|rest| rest.strip_prefix('\n'))
        .ok_or("the envelope header is missing")?;
    let (length, rest) = rest.split_once('\n').ok_or("the key length is missing")?;
    let length: usize = length
        .parse()
        .map_err(|_| "the key length is not a number")?;
    let key = rest
        .get(..length)
        .ok_or("the key is longer than the envelope")?;
    let snapshot = rest
        .get(length..)
        .and_then(|tail| tail.strip_prefix('\n'))
        .ok_or("the key is not followed by the snapshot")?;
    Ok((key, snapshot))
}

/// The largest envelope read: a catalog snapshot plus its header and key text.
const MAX_ENVELOPE_BYTES: u64 = MAX_CATALOG_BYTES as u64 + 64 * 1024;

fn read_bounded(path: &Path) -> Result<Option<String>, String> {
    let file = match File::open(path) {
        Ok(file) => file,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(format!("cannot open the cache snapshot: {error}")),
    };
    let mut bytes = Vec::new();
    file.take(MAX_ENVELOPE_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|error| format!("cannot read the cache snapshot: {error}"))?;
    if bytes.len() as u64 > MAX_ENVELOPE_BYTES {
        return Err("the cache snapshot exceeds its size limit".to_string());
    }
    String::from_utf8(bytes)
        .map(Some)
        .map_err(|error| format!("the cache snapshot is not UTF-8: {error}"))
}

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

/// Writes `bytes` to a temporary file beside `path`, then renames it over `path`. A failed write or rename
/// removes the temporary file and leaves `path` as it was.
fn publish_file(
    path: &Path,
    bytes: &[u8],
    observe: &mut dyn FnMut(PublishStage),
) -> Result<(), String> {
    let parent = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    let name = path
        .file_name()
        .ok_or("the cache snapshot path has no file name")?;
    let (temp_path, mut temp_file) = create_temp(parent, name)?;
    let staged = temp_file
        .write_all(bytes)
        .and_then(|()| temp_file.sync_all());
    drop(temp_file);
    if let Err(error) = staged {
        let _ = fs::remove_file(&temp_path);
        return Err(format!("cannot write the cache snapshot: {error}"));
    }
    observe(PublishStage::TempWritten);
    if let Err(error) = fs::rename(&temp_path, path) {
        let _ = fs::remove_file(&temp_path);
        return Err(format!("cannot replace the cache snapshot: {error}"));
    }
    observe(PublishStage::Renamed);
    Ok(())
}

fn create_temp(parent: &Path, name: &std::ffi::OsStr) -> Result<(PathBuf, File), String> {
    for _ in 0..32 {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let mut temp_name = name.to_os_string();
        temp_name.push(format!(".tmp.{}.{}", std::process::id(), sequence));
        let path = parent.join(temp_name);
        match OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(file) => return Ok((path, file)),
            Err(error) if error.kind() == ErrorKind::AlreadyExists => continue,
            Err(error) => {
                return Err(format!("cannot create a temporary cache snapshot: {error}"));
            }
        }
    }
    Err("cannot find a free temporary cache snapshot name".to_string())
}

/// FNV-1a, 64 bit: a stable file-name digest. Not a security measure; the envelope's key text is the check.
fn fnv1a64(bytes: &[u8]) -> u64 {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}
