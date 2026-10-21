//! Snapshot cache behaviour, without a live KB: the build step is a counted closure.

use gx_engine::catalog::{
    Catalog, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::catalog_cache::{
    CacheStatus, PublishStage, load_or_build, publish_snapshot, read_snapshot, snapshot_path,
};
use std::cell::{Cell, RefCell};
use std::collections::BTreeMap;
use std::fs;
use std::io::Read;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

static SEQUENCE: AtomicU64 = AtomicU64::new(0);

fn scratch(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "gx_catalog_cache_{label}_{}_{}",
        std::process::id(),
        SEQUENCE.fetch_add(1, Ordering::SeqCst)
    ));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).unwrap();
    dir
}

fn catalog() -> Catalog {
    // The name's case must survive the snapshot: the emitter writes it into C# class names.
    let mut objects = BTreeMap::new();
    let id = ObjectId::new("procedure-1").unwrap();
    objects.insert(
        id.clone(),
        KbObject::new(id, "AddDeviceGroups", ObjectKind::Procedure),
    );
    Catalog::from_objects_map(objects)
}

#[test]
fn a_miss_builds_and_writes_a_snapshot_and_the_next_load_is_a_hit() {
    let root = scratch("hit");
    let (cache, kb) = (root.join("cache"), root.join("kb"));
    fs::create_dir_all(&kb).unwrap();
    let builds = Cell::new(0);
    let build = || {
        builds.set(builds.get() + 1);
        Ok(catalog())
    };

    let (first, status) = load_or_build(&cache, &kb, "key-A", build).unwrap();
    assert_eq!(status, CacheStatus::Miss);
    assert_eq!(builds.get(), 1);

    let (second, status) = load_or_build(&cache, &kb, "key-A", build).unwrap();
    assert_eq!(status, CacheStatus::Hit);
    assert_eq!(builds.get(), 1, "a hit must not read the KB objects");
    assert_eq!(second.to_json().unwrap(), first.to_json().unwrap());
    assert_eq!(
        second
            .get(&ObjectId::new("procedure-1").unwrap())
            .unwrap()
            .name,
        "AddDeviceGroups"
    );
    let _ = fs::remove_dir_all(&root);
}

#[test]
fn a_changed_key_misses_even_though_a_snapshot_exists() {
    let root = scratch("key");
    let (cache, kb) = (root.join("cache"), root.join("kb"));
    fs::create_dir_all(&kb).unwrap();
    let builds = Cell::new(0);
    let build = || {
        builds.set(builds.get() + 1);
        Ok(catalog())
    };
    load_or_build(&cache, &kb, "key-A", build).unwrap();

    let (_, status) = load_or_build(&cache, &kb, "key-B", build).unwrap();
    assert_eq!(status, CacheStatus::Miss);
    assert_eq!(builds.get(), 2);
    let _ = fs::remove_dir_all(&root);
}

#[test]
fn a_corrupt_snapshot_is_rebuilt_never_trusted() {
    let root = scratch("corrupt");
    let (cache, kb) = (root.join("cache"), root.join("kb"));
    fs::create_dir_all(&kb).unwrap();
    let builds = Cell::new(0);
    let build = || {
        builds.set(builds.get() + 1);
        Ok(catalog())
    };
    load_or_build(&cache, &kb, "key-A", build).unwrap();
    let path = snapshot_path(&cache, &kb);
    fs::write(&path, "{ not a catalog").unwrap();

    let (_, status) = load_or_build(&cache, &kb, "key-A", build).unwrap();
    assert_eq!(status, CacheStatus::Replaced);
    assert_eq!(builds.get(), 2);
    let (_, status) = load_or_build(&cache, &kb, "key-A", build).unwrap();
    assert_eq!(
        status,
        CacheStatus::Hit,
        "the rewritten snapshot is valid again"
    );
    let _ = fs::remove_dir_all(&root);
}

/// A catalog with one object whose name is the revision marker, so a reader can tell which one it got.
fn revision(name: &str) -> Catalog {
    let mut objects = BTreeMap::new();
    let id = ObjectId::new("revision-object-1").unwrap();
    objects.insert(id.clone(), KbObject::new(id, name, ObjectKind::Procedure));
    Catalog::from_objects_map(objects)
}

fn name_of(catalog: &Catalog) -> String {
    catalog
        .get(&ObjectId::new("revision-object-1").unwrap())
        .unwrap()
        .name
        .clone()
}

#[test]
fn a_snapshot_copied_from_another_knowledge_base_is_a_miss() {
    // The envelope carries the full key text, which includes the KB's canonical path: a file that
    // belongs to another KB cannot be served here, even if it sits at this KB's snapshot path.
    let root = scratch("foreign");
    let (cache, kb, other) = (root.join("cache"), root.join("kb"), root.join("other"));
    fs::create_dir_all(&kb).unwrap();
    fs::create_dir_all(&other).unwrap();
    load_or_build(&cache, &other, "key-A", || Ok(revision("Foreign"))).unwrap();
    fs::copy(snapshot_path(&cache, &other), snapshot_path(&cache, &kb)).unwrap();

    assert_eq!(read_snapshot(&cache, &kb, "key-A").unwrap(), None);
    let builds = Cell::new(0);
    let (catalog, status) = load_or_build(&cache, &kb, "key-A", || {
        builds.set(builds.get() + 1);
        Ok(revision("Local"))
    })
    .unwrap();
    assert_eq!(status, CacheStatus::Miss);
    assert_eq!(builds.get(), 1);
    assert_eq!(name_of(&catalog), "Local");
    let _ = fs::remove_dir_all(&root);
}

#[test]
fn snapshot_names_differ_between_knowledge_bases() {
    let cache = PathBuf::from("cache");
    let a = snapshot_path(&cache, &PathBuf::from("KBs/KBTeste"));
    let b = snapshot_path(&cache, &PathBuf::from("KBs/KBTeste16"));
    assert_ne!(a, b);
}

#[test]
fn a_reader_holding_the_old_key_never_receives_the_new_catalog_at_any_publish_stage() {
    // Deterministic failpoint: the observer reads the cache at each stage of publishing revision B under
    // its own key, while revision A is the cached snapshot. No thread and no sleep is involved.
    let root = scratch("failpoint");
    let (cache, kb) = (root.join("cache"), root.join("kb"));
    fs::create_dir_all(&kb).unwrap();
    load_or_build(&cache, &kb, "key-A", || Ok(revision("RevisionA"))).unwrap();

    let seen = RefCell::new(Vec::new());
    publish_snapshot(&cache, &kb, "key-B", &revision("RevisionB"), &mut |stage| {
        let old = read_snapshot(&cache, &kb, "key-A")
            .unwrap()
            .map(|c| name_of(&c));
        let new = read_snapshot(&cache, &kb, "key-B")
            .unwrap()
            .map(|c| name_of(&c));
        seen.borrow_mut().push((stage, old, new));
    })
    .unwrap();

    assert_eq!(
        *seen.borrow(),
        vec![
            (
                PublishStage::TempWritten,
                Some("RevisionA".to_string()),
                None
            ),
            (PublishStage::Renamed, None, Some("RevisionB".to_string())),
        ],
        "until the rename the old key sees A; after it the old key misses and only the new key sees B"
    );
    let _ = fs::remove_dir_all(&root);
}

#[test]
fn after_revision_b_is_published_a_request_for_the_old_key_rebuilds_and_never_returns_b() {
    // The reproduction in `C:/tmp/gxe-cache-review/repro.rs`: A is cached, then B is published, then A is
    // requested again. The old implementation returned B with status Hit.
    let root = scratch("repro");
    let (cache, kb) = (root.join("cache"), root.join("kb"));
    fs::create_dir_all(&kb).unwrap();
    load_or_build(&cache, &kb, "key-A", || Ok(revision("RevisionA"))).unwrap();
    publish_snapshot(&cache, &kb, "key-B", &revision("RevisionB"), &mut |_| {}).unwrap();

    let builds = Cell::new(0);
    let (catalog, status) = load_or_build(&cache, &kb, "key-A", || {
        builds.set(builds.get() + 1);
        Ok(revision("RevisionA"))
    })
    .unwrap();
    assert_eq!(status, CacheStatus::Miss);
    assert_eq!(builds.get(), 1, "the old key must rebuild from the KB");
    assert_eq!(name_of(&catalog), "RevisionA");
    let _ = fs::remove_dir_all(&root);
}

#[test]
fn a_handle_retained_on_revision_a_keeps_all_of_a_while_b_is_published() {
    // Atomic publication replaces the path with a new file: a handle opened on revision A before the publish
    // keeps reading all of A, and a fresh open gives B. An overwrite in place (a copy over the path) rewrites
    // A's own file under the retained handle. No thread and no sleep: the handle is opened and read at fixed
    // points around one publish.
    let root = scratch("retained_handle");
    let (cache, kb) = (root.join("cache"), root.join("kb"));
    fs::create_dir_all(&kb).unwrap();
    load_or_build(&cache, &kb, "key-A", || Ok(revision("RevisionA"))).unwrap();
    let path = snapshot_path(&cache, &kb);
    let a_bytes = fs::read(&path).unwrap();
    let mut retained = fs::File::open(&path).unwrap();

    publish_snapshot(&cache, &kb, "key-B", &revision("RevisionB"), &mut |_| {}).unwrap();

    let mut seen = Vec::new();
    retained.read_to_end(&mut seen).unwrap();
    drop(retained);
    assert_eq!(
        seen, a_bytes,
        "the handle opened before the publish must still read all of revision A"
    );
    assert_eq!(
        read_snapshot(&cache, &kb, "key-B")
            .unwrap()
            .map(|c| name_of(&c)),
        Some("RevisionB".to_string()),
        "a fresh open of the path must give revision B"
    );
    assert!(
        read_snapshot(&cache, &kb, "key-A").unwrap().is_none(),
        "the old key must no longer hit once B is published"
    );
    let _ = fs::remove_dir_all(&root);
}

#[test]
fn a_truncated_or_mis_framed_envelope_is_rebuilt_never_trusted() {
    let root = scratch("framing");
    let (cache, kb) = (root.join("cache"), root.join("kb"));
    fs::create_dir_all(&kb).unwrap();
    load_or_build(&cache, &kb, "key-A", || Ok(revision("RevisionA"))).unwrap();
    let path = snapshot_path(&cache, &kb);
    let full = fs::read_to_string(&path).unwrap();

    fs::write(&path, &full[..full.len() - 10]).unwrap();
    assert!(
        read_snapshot(&cache, &kb, "key-A").is_err(),
        "a truncated envelope is rejected"
    );
    let (_, status) = load_or_build(&cache, &kb, "key-A", || Ok(revision("RevisionA"))).unwrap();
    assert_eq!(status, CacheStatus::Replaced);

    fs::write(
        &path,
        "gx-catalog-snapshot-envelope-1\n99999\nshort key\n{}",
    )
    .unwrap();
    assert!(
        read_snapshot(&cache, &kb, "key-A").is_err(),
        "a key length past the end is rejected"
    );
    let (_, status) = load_or_build(&cache, &kb, "key-A", || Ok(revision("RevisionA"))).unwrap();
    assert_eq!(status, CacheStatus::Replaced);
    let _ = fs::remove_dir_all(&root);
}

#[test]
fn two_writers_and_a_reader_never_pair_a_key_with_another_catalog() {
    // Concurrency without timing: writers publish two revisions under their own keys while a reader
    // checks every catalog it gets against the key it asked for. A failed publish (for example a file
    // a reader holds open) leaves the previous envelope in place; that is not a mismatch.
    let root = scratch("stress");
    let (cache, kb) = (root.join("cache"), root.join("kb"));
    fs::create_dir_all(&kb).unwrap();
    let successes = AtomicU64::new(0);
    let mismatches = AtomicU64::new(0);
    std::thread::scope(|scope| {
        for (key, name) in [("key-A", "RevisionA"), ("key-B", "RevisionB")] {
            let (cache, kb, successes) = (&cache, &kb, &successes);
            scope.spawn(move || {
                for _ in 0..200 {
                    if publish_snapshot(cache, kb, key, &revision(name), &mut |_| {}).is_ok() {
                        successes.fetch_add(1, Ordering::SeqCst);
                    }
                }
            });
        }
        scope.spawn(|| {
            for _ in 0..2000 {
                for (key, name) in [("key-A", "RevisionA"), ("key-B", "RevisionB")] {
                    if let Ok(Some(catalog)) = read_snapshot(&cache, &kb, key)
                        && name_of(&catalog) != name
                    {
                        mismatches.fetch_add(1, Ordering::SeqCst);
                    }
                }
            }
        });
    });
    assert_eq!(
        mismatches.load(Ordering::SeqCst),
        0,
        "no reader sees a catalog under another key"
    );
    assert!(
        successes.load(Ordering::SeqCst) > 0,
        "some publish succeeded"
    );
    let _ = fs::remove_dir_all(&root);
}

/// A procedure whose parts and references are out of canonical order, as the KB reader can produce them.
fn out_of_order_catalog() -> Catalog {
    let main = ObjectId::new("main-1").unwrap();
    let mut procedure = KbObject::new(main.clone(), "Main", ObjectKind::Procedure);
    procedure.source_parts = vec![
        SourcePart {
            name: "VARIABLES".into(),
            source: "{}".into(),
        },
        SourcePart {
            name: "SOURCE".into(),
            source: "Print(1)".into(),
        },
        SourcePart {
            name: "RULES".into(),
            source: String::new(),
        },
    ];
    procedure.references = vec![
        Reference {
            kind: ReferenceKind::Calls,
            target: ObjectId::new("zeta-2").unwrap(),
        },
        Reference {
            kind: ReferenceKind::Uses,
            target: ObjectId::new("alpha-3").unwrap(),
        },
        Reference {
            kind: ReferenceKind::Calls,
            target: ObjectId::new("alpha-2").unwrap(),
        },
    ];
    let mut objects = BTreeMap::new();
    objects.insert(main, procedure);
    Catalog::from_objects_map(objects)
}

#[test]
fn a_snapshot_hit_is_the_live_build_exactly_order_included() {
    let root = scratch("order");
    let (cache, kb) = (root.join("cache"), root.join("kb"));
    fs::create_dir_all(&kb).unwrap();

    let (live, status) =
        load_or_build(&cache, &kb, "key-O", || Ok(out_of_order_catalog())).unwrap();
    assert_eq!(status, CacheStatus::Miss);

    let (cached, status) = load_or_build(&cache, &kb, "key-O", || -> Result<Catalog, String> {
        panic!("a hit must not rebuild from the KB")
    })
    .unwrap();
    assert_eq!(status, CacheStatus::Hit);
    assert_eq!(
        cached, live,
        "a build from the snapshot must equal the live build, order included"
    );
    let main = cached.get(&ObjectId::new("main-1").unwrap()).unwrap();
    let parts: Vec<&str> = main.source_parts.iter().map(|p| p.name.as_str()).collect();
    assert_eq!(parts, ["VARIABLES", "SOURCE", "RULES"]);
    let targets: Vec<&str> = main.references.iter().map(|r| r.target.as_str()).collect();
    assert_eq!(targets, ["zeta-2", "alpha-3", "alpha-2"]);
    let _ = fs::remove_dir_all(&root);
}
