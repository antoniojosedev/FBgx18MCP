use crate::graph::CsrGraph;
use crate::model::{EdgeKind, EntityKind, EntityNode, RawEdge};
use crate::parser::{parse_genexus_source, ParseResult};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::error::Error;
use std::fs::{self, File};
use std::io::{BufReader, Read, Write};
use std::path::{Path, PathBuf};
use std::time::UNIX_EPOCH;
use walkdir::WalkDir;

const CACHE_FILE_NAME: &str = ".gxwire_cache.bin";

/// Layout marker, read before the payload so a cache written by a different
/// gxwire build is discarded instead of being misread. The Knowledge Base is
/// then reparsed in full, which is slow but never wrong.
const CACHE_FORMAT_VERSION: u32 = 2;

/// Parse output for one source file, together with the stat signature it was
/// produced from. The entry is reused verbatim while both still match, which
/// is what turns a one-object edit into a one-object reparse.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FileParse {
    pub path: String,
    pub stem: String,
    pub kind: EntityKind,
    pub mtime_ms: u64,
    pub size: u64,
    pub parse: ParseResult,
}

/// Parse entries keyed by absolute source path.
pub type ParseMap = HashMap<String, FileParse>;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CachedBundle {
    pub version: u32,
    pub graph: CsrGraph,
    pub parses: ParseMap,
}

pub fn get_cache_path(root: &Path) -> PathBuf {
    root.join(CACHE_FILE_NAME)
}

/// (mtime in milliseconds, size in bytes). Size is part of the signature so a
/// same-millisecond rewrite that changes length cannot slip through as fresh.
fn stat_signature(path: &Path) -> (u64, u64) {
    match fs::metadata(path) {
        Ok(meta) => {
            let mtime_ms = meta
                .modified()
                .ok()
                .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
                .map(|d| d.as_millis() as u64)
                .unwrap_or(0);
            (mtime_ms, meta.len())
        }
        // An unreadable file reparses on every run: slow, never stale.
        Err(_) => (0, 0),
    }
}

/// One supported source file found on disk, before it is read.
struct SourceFile {
    path: PathBuf,
    stem: String,
}

fn collect_source_files(dir: &Path) -> Vec<SourceFile> {
    let mut out = Vec::new();
    for entry in WalkDir::new(dir).into_iter().filter_map(|e| e.ok()) {
        // `file_type()` comes from the directory enumeration itself, whereas
        // `path.is_file()` costs a stat per entry. That stat is the single
        // biggest per-file overhead when indexing a large Knowledge Base.
        if !entry.file_type().is_file() {
            continue;
        }
        let path = entry.path();
        let ext = path.extension().and_then(|s| s.to_str()).unwrap_or("");
        if !is_supported_ext(ext) {
            continue;
        }
        out.push(SourceFile {
            path: path.to_path_buf(),
            stem: path
                .file_stem()
                .and_then(|s| s.to_str())
                .unwrap_or("")
                .to_string(),
        });
    }
    out
}

/// Reads and lexes one file. `signature` is passed in so the caller, which
/// already stat'ed the file to decide whether to reuse a cached entry, does not
/// stat it a second time.
fn parse_source(sf: &SourceFile, signature: (u64, u64)) -> FileParse {
    let (mtime_ms, size) = signature;
    let ext = sf.path.extension().and_then(|s| s.to_str()).unwrap_or("");
    let kind = detect_entity_kind(&sf.path, &sf.stem, ext);

    let parse = match fs::read_to_string(&sf.path) {
        Ok(content) => parse_genexus_source(&content),
        // Mirrors the previous builder, which left the node at its
        // constructor defaults when the file could not be read.
        Err(_) => ParseResult {
            complexity: 1,
            ..Default::default()
        },
    };

    FileParse {
        path: sf.path.to_string_lossy().to_string(),
        stem: sf.stem.clone(),
        kind,
        mtime_ms,
        size,
        parse,
    }
}

/// Assembles the graph from already-parsed files.
///
/// Two passes, so that every file is registered as a node before any reference
/// is resolved: a reference to an object defined later in the walk must resolve
/// to that object's real node rather than to a phantom. Unresolved references
/// get phantom nodes (`path: None`) that live only as long as the references
/// that create them. Node ids follow file order, so the same file set produces
/// the same graph regardless of which entries were reused from the cache.
fn assemble_graph(files: &[FileParse]) -> CsrGraph {
    let mut nodes = Vec::with_capacity(files.len());
    let mut raw_edges = Vec::new();
    let mut name_to_id: HashMap<String, u32> = HashMap::with_capacity(files.len());

    // Pass 1: one node per source file.
    for (idx, fp) in files.iter().enumerate() {
        let id = idx as u32;
        name_to_id.insert(fp.stem.to_ascii_lowercase(), id);
        nodes.push(EntityNode {
            id,
            name: fp.stem.clone(),
            kind: fp.kind,
            module: None,
            description: None,
            parm_rule: fp.parse.parm_rule.clone(),
            path: Some(fp.path.clone()),
            lines: fp.parse.lines_count,
            complexity: fp.parse.complexity,
        });
    }

    // Pass 2: resolve references, creating phantoms for targets with no file.
    // Bounded by the file-backed count so phantoms are not themselves scanned.
    for (idx, fp) in files.iter().enumerate() {
        for ref_sym in &fp.parse.references {
            let target_key = ref_sym.target_name.to_ascii_lowercase();
            let target_id = match name_to_id.get(&target_key) {
                Some(&id) => id,
                None => {
                    let new_id = nodes.len() as u32;
                    name_to_id.insert(target_key, new_id);
                    let kind = match ref_sym.kind {
                        EdgeKind::WritesAttr(_) | EdgeKind::ReadsAttr => EntityKind::Attribute,
                        _ => EntityKind::Unknown,
                    };
                    nodes.push(EntityNode {
                        id: new_id,
                        name: ref_sym.target_name.clone(),
                        kind,
                        module: None,
                        description: None,
                        parm_rule: None,
                        path: None,
                        lines: 0,
                        complexity: 1,
                    });
                    new_id
                }
            };

            raw_edges.push(RawEdge {
                from: idx as u32,
                to: target_id,
                kind: ref_sym.kind.clone(),
                line: ref_sym.line,
            });
        }
    }

    CsrGraph::build(nodes, &raw_edges)
}

/// One incremental pass: the graph and the parse map it was assembled from.
type Reindex = (CsrGraph, ParseMap);

/// Walks `root` once and reparses only the files whose stat signature moved.
///
/// `cached` is consumed so a reused entry is moved rather than cloned: cloning
/// it would reallocate every reference string in the Knowledge Base on every
/// call, which defeats the point of caching them.
///
/// Returns `None` when nothing moved, so an unchanged Knowledge Base answers
/// from the cached graph without touching the parse data at all. A single stat
/// per file replaces the previous two-walk freshness probe.
fn reindex(root: &Path, mut cached: ParseMap) -> Result<Option<Reindex>, Box<dyn Error>> {
    let sources = collect_source_files(root);
    let mut ordered: Vec<FileParse> = Vec::with_capacity(sources.len());
    let mut reparsed = 0usize;

    for sf in &sources {
        // The path is the cache key, and the node name is derived from it, so
        // a rename lands on a new key and is reparsed as a new object. The stem
        // needs no separate check: it cannot differ for the same path.
        let key = sf.path.to_string_lossy().to_string();
        let signature = stat_signature(&sf.path);

        let reusable = match cached.get(&key) {
            Some(previous) => {
                previous.mtime_ms == signature.0 && previous.size == signature.1
            }
            None => false,
        };

        if reusable {
            ordered.push(cached.remove(&key).expect("entry was just observed"));
        } else {
            ordered.push(parse_source(sf, signature));
            reparsed += 1;
        }
    }

    // `cached` has been drained entry by entry, so whatever is left is an object
    // the walk no longer found: a deleted file. Nothing moved and nothing was
    // deleted means the cached graph still stands.
    if reparsed == 0 && cached.is_empty() {
        return Ok(None);
    }

    let graph = assemble_graph(&ordered);
    let parses = ordered
        .into_iter()
        .map(|fp| (fp.path.clone(), fp))
        .collect::<ParseMap>();
    Ok(Some((graph, parses)))
}

/// Borrowed serialization view of the index, laid out exactly like
/// `CachedBundle` so both encode to the same bytes. Persisting through a
/// borrowed view avoids deep-copying the graph and every reference string in
/// it, which for a real Knowledge Base costs more than the write itself.
///
/// The two must be kept field-for-field identical: if they drift, every write
/// becomes unreadable and `load_cache` silently falls back to reparsing the
/// whole Knowledge Base. `warm_load_does_not_rewrite_the_index` fails loudly in
/// that case.
#[derive(Serialize)]
struct BundleRef<'a> {
    version: u32,
    graph: &'a CsrGraph,
    parses: &'a ParseMap,
}

/// Persists the graph together with the parse entries it was assembled from.
/// Persisting a graph on its own is deliberately not offered: the next load
/// would have no reuse state and would reparse the whole Knowledge Base.
pub fn save_index(graph: &CsrGraph, parses: &ParseMap, root: &Path) -> Result<(), Box<dyn Error>> {
    let view = BundleRef {
        version: CACHE_FORMAT_VERSION,
        graph,
        parses,
    };
    let encoded = bincode::serialize(&view)?;
    let mut file = File::create(get_cache_path(root))?;
    file.write_all(&encoded)?;
    Ok(())
}

/// Reads the index, rejecting any file whose layout marker is not ours.
///
/// The marker is validated before the payload is decoded: a cache written by a
/// different gxwire build has a different field layout, and decoding it as if it
/// were ours would interpret arbitrary bytes as collection lengths. `version` is
/// the bundle's first field, so both reads start at the same offset and the
/// header validation lines up exactly with the payload.
fn read_bundle(cache_path: &Path) -> Option<CachedBundle> {
    let bytes = fs::read(cache_path).ok()?;
    let header_bytes = bytes.get(..std::mem::size_of::<u32>())?;
    let version = bincode::deserialize::<VersionHeader>(header_bytes).ok()?;

    if version.version != CACHE_FORMAT_VERSION {
        return None;
    }

    // A truncated write fails here and falls back to a full reparse.
    bincode::deserialize::<CachedBundle>(&bytes).ok()
}

#[derive(Serialize, Deserialize)]
struct VersionHeader {
    version: u32,
}

/// Ingests directory containing GeneXus exported sources / text tree files,
/// returning the graph and the parse entries it was built from.
pub fn build_index(dir: &Path) -> Result<(CsrGraph, ParseMap), Box<dyn Error>> {
    // An empty cache can never serve a file, so this always parses what it finds.
    Ok(match reindex(dir, ParseMap::new())? {
        Some(result) => result,
        // Nothing on disk and nothing cached. A Knowledge Base with no exported
        // sources yet is a valid one, and its graph is empty - not an error.
        None => (assemble_graph(&[]), ParseMap::new()),
    })
}

pub fn build_graph_from_directory(dir: &Path) -> Result<CsrGraph, Box<dyn Error>> {
    Ok(build_index(dir)?.0)
}

/// Returns the graph for `root`, reindexing incrementally when the index is
/// stale and reparsing from scratch when it is missing or unreadable.
pub fn load_cache(root: &Path) -> Result<CsrGraph, Box<dyn Error>> {
    let cache_path = get_cache_path(root);

    if let Some(bundle) = read_bundle(&cache_path) {
        let CachedBundle { graph, parses, .. } = bundle;
        match reindex(root, parses)? {
            // Nothing on disk moved: the cached graph is already the truth.
            None => Ok(graph),
            Some((graph, parses)) => {
                // A failed write costs speed on the next run, not correctness.
                let _ = save_index(&graph, &parses, root);
                Ok(graph)
            }
        }
    } else {
        // No usable index: parse everything. Saving is best effort so a
        // read-only Knowledge Base still answers queries.
        let (graph, parses) = build_index(root)?;
        let _ = save_index(&graph, &parses, root);
        Ok(graph)
    }
}

#[inline]
fn is_supported_ext(ext: &str) -> bool {
    matches!(
        ext.to_ascii_lowercase().as_str(),
        "txt" | "gxd" | "xml" | "gx" | "prc" | "trn" | "wp" | "sdp"
    )
}

/// Best-effort object kind from a file name, used only when the file's own
/// content cannot say.
///
/// The old chain tested a single leading letter, so every Transaction whose
/// name began with `P` - `PessoaDadosFiscaisAdicionais` among them - was
/// reported as `kind: "Proc"`, and `callers` answered on the wrong object type
/// (#500). Two-letter prefixes are unambiguous in GeneXus exports and are
/// therefore checked first; a bare single-letter guess is no longer made,
/// because `P` alone cannot distinguish a Procedure from a Transaction named
/// after a person, and guessing wrong is worse than reporting `Unknown`.
///
/// Content detection remains authoritative for XML exports, where the element
/// name says the real kind.
fn kind_from_stem(file_stem: &str) -> EntityKind {
    if file_stem.starts_with("UT_") {
        return EntityKind::Test;
    }
    if file_stem.starts_with("Trn") {
        return EntityKind::Transaction;
    }
    if file_stem.starts_with("WP") {
        return EntityKind::WebPanel;
    }
    if file_stem.starts_with("SDP") {
        return EntityKind::Panel;
    }
    if file_stem.starts_with("WW") {
        return EntityKind::WorkWithPlus;
    }
    if file_stem.starts_with("Proc") {
        return EntityKind::Procedure;
    }
    if file_stem.starts_with("DS") {
        return EntityKind::DataSelector;
    }
    EntityKind::Unknown
}

fn detect_entity_kind(path: &Path, file_stem: &str, ext: &str) -> EntityKind {
    match ext.to_ascii_lowercase().as_str() {
        "prc" => EntityKind::Procedure,
        "trn" => EntityKind::Transaction,
        "wp" => EntityKind::WebPanel,
        "sdp" => EntityKind::Panel,
        "xml" => {
            if let Ok(file) = File::open(path) {
                let mut buf = [0u8; 512];
                let mut reader = BufReader::new(file);
                if let Ok(n) = reader.read(&mut buf) {
                    let head = String::from_utf8_lossy(&buf[..n]).to_ascii_lowercase();
                    if head.contains("<procedure") {
                        return EntityKind::Procedure;
                    } else if head.contains("<transaction") {
                        return EntityKind::Transaction;
                    } else if head.contains("<webpanel") {
                        return EntityKind::WebPanel;
                    } else if head.contains("<sdpanel") {
                        return EntityKind::Panel;
                    } else if head.contains("<workwith") {
                        return EntityKind::WorkWithPlus;
                    } else if head.contains("<table") {
                        return EntityKind::Table;
                    } else if head.contains("<attribute") {
                        return EntityKind::Attribute;
                    } else if head.contains("<domain") {
                        return EntityKind::Domain;
                    } else if head.contains("<sdt") {
                        return EntityKind::Sdt;
                    } else if head.contains("<dataselector") {
                        return EntityKind::DataSelector;
                    }
                }
            }
            kind_from_stem(file_stem)
        }
        _ => kind_from_stem(file_stem),
    }
}
