use gx_engine::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

fn id(value: &str) -> ObjectId {
    ObjectId::new(value).expect("valid test identity")
}

fn object(identity: &str, name: &str, kind: ObjectKind) -> KbObject {
    KbObject::new(id(identity), name, kind)
}

fn temp_dir() -> PathBuf {
    let path = std::env::temp_dir().join(format!(
        "gx-engine-catalog-test-{}-{}",
        std::process::id(),
        TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir(&path).expect("create test directory");
    path
}

#[test]
fn rejects_malformed_and_duplicate_identities_or_names() {
    assert!(ObjectId::new("bad/id").is_err());
    assert!(
        Catalog::from_json("{")
            .unwrap_err()
            .contains("invalid catalog JSON")
    );

    let duplicate_ids = r#"{"format_version":1,"revision":0,"objects":[
      {"id":"same","name":"FIRST","kind":"procedure"},
      {"id":"SAME","name":"SECOND","kind":"transaction"}] }"#;
    assert!(
        Catalog::from_json(duplicate_ids)
            .unwrap_err()
            .contains("duplicate object identity")
    );

    let duplicate_names = r#"{"format_version":1,"revision":0,"objects":[
      {"id":"one","name":"Thing","kind":"procedure"},
      {"id":"two","name":"THING","kind":"transaction"}] }"#;
    assert!(
        Catalog::from_json(duplicate_names)
            .unwrap_err()
            .contains("duplicate object name")
    );

    let unknown_field = r#"{"format_version":1,"revision":0,"objects":[],"native_path":"ignored"}"#;
    assert!(
        Catalog::from_json(unknown_field)
            .unwrap_err()
            .contains("unknown field")
    );
}

#[test]
fn rename_keeps_identity_and_references_and_orders_dependencies_first() {
    let mut caller = object("caller", "Caller", ObjectKind::Procedure);
    caller.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("target"),
    });
    let target = object("target", "Target", ObjectKind::Procedure);
    let mut catalog = Catalog::empty();
    assert_eq!(
        catalog.apply(0, &[Edit::Upsert(caller), Edit::Upsert(target)]),
        Ok(1)
    );

    assert_eq!(
        catalog.apply(
            1,
            &[Edit::Rename {
                id: id("TARGET"),
                name: "RenamedTarget".into(),
            }]
        ),
        Ok(2)
    );
    let renamed = catalog.get(&id("target")).expect("same stable identity");
    assert_eq!(renamed.name, "RENAMEDTARGET");
    assert_eq!(renamed.id, id("target"));
    assert_eq!(
        catalog.get(&id("caller")).unwrap().references[0].target,
        id("target")
    );
    assert_eq!(
        catalog.dependency_order().unwrap(),
        [id("target"), id("caller")]
    );
}

#[test]
fn transaction_rolls_back_on_stale_revision_invalid_names_and_dangling_references() {
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[Edit::Upsert(object("one", "One", ObjectKind::Procedure))],
        )
        .unwrap();
    let original = catalog.to_json().unwrap();

    assert!(
        catalog
            .apply(
                0,
                &[Edit::Rename {
                    id: id("one"),
                    name: "Renamed".into(),
                }]
            )
            .unwrap_err()
            .contains("stale catalog revision")
    );
    assert_eq!(catalog.to_json().unwrap(), original);

    assert!(
        catalog
            .apply(
                1,
                &[Edit::Rename {
                    id: id("one"),
                    name: "not a GeneXus identifier".into(),
                }]
            )
            .is_err()
    );
    assert_eq!(catalog.to_json().unwrap(), original);

    assert!(catalog.apply(1, &[Edit::Remove { id: id("one") }]).is_ok());
}

#[test]
fn dangling_reference_and_inbound_delete_are_rejected_without_partial_commit() {
    let mut catalog = Catalog::empty();
    let mut caller = object("caller", "Caller", ObjectKind::Procedure);
    caller.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("missing"),
    });
    assert!(catalog.apply(0, &[Edit::Upsert(caller)]).is_err());
    assert_eq!(catalog.revision(), 0);
    assert_eq!(catalog.objects().count(), 0);

    let mut caller = object("caller", "Caller", ObjectKind::Procedure);
    caller.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("target"),
    });
    catalog
        .apply(
            0,
            &[
                Edit::Upsert(caller),
                Edit::Upsert(object("target", "Target", ObjectKind::Procedure)),
            ],
        )
        .unwrap();
    let before = catalog.to_json().unwrap();
    assert!(
        catalog
            .apply(1, &[Edit::Remove { id: id("target") }])
            .is_err()
    );
    assert_eq!(catalog.to_json().unwrap(), before);

    catalog
        .apply(
            1,
            &[Edit::SetReferences {
                id: id("caller"),
                references: Vec::new(),
            }],
        )
        .unwrap();
    catalog
        .apply(2, &[Edit::Remove { id: id("target") }])
        .unwrap();
    assert!(catalog.get(&id("target")).is_none());
}

#[test]
fn cycles_are_preserved_but_dependency_order_reports_blocked_objects() {
    let mut left = object("left", "Left", ObjectKind::Procedure);
    left.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("right"),
    });
    let mut right = object("right", "Right", ObjectKind::Procedure);
    right.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id("left"),
    });
    let mut downstream = object("downstream", "Downstream", ObjectKind::Procedure);
    downstream.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("left"),
    });

    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[
                Edit::Upsert(downstream),
                Edit::Upsert(right),
                Edit::Upsert(left),
            ],
        )
        .unwrap();
    assert_eq!(catalog.objects().count(), 3);
    assert_eq!(
        catalog.dependency_order().unwrap_err(),
        [id("downstream"), id("left"), id("right")]
    );
}

#[test]
fn source_parts_and_references_serialize_deterministically() {
    let mut a = object("a", "Alpha", ObjectKind::StructuredDataType);
    a.source_parts = vec![
        SourcePart {
            name: "Rules".into(),
            source: "Rule 2".into(),
        },
        SourcePart {
            name: "Source".into(),
            source: "Rule 1".into(),
        },
    ];
    a.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("b"),
    });
    let b = object("b", "Beta", ObjectKind::Domain);

    let mut first = Catalog::empty();
    first
        .apply(0, &[Edit::Upsert(a.clone()), Edit::Upsert(b.clone())])
        .unwrap();
    let mut second = Catalog::empty();
    let mut reversed_a = a;
    reversed_a.source_parts.reverse();
    second
        .apply(0, &[Edit::Upsert(b), Edit::Upsert(reversed_a)])
        .unwrap();

    let json = first.to_json().unwrap();
    assert_eq!(json, second.to_json().unwrap());
    let loaded = Catalog::from_json(&json).unwrap();
    assert_eq!(loaded.to_json().unwrap(), json);
}

#[test]
fn save_and_load_round_trip_and_replace_an_existing_snapshot() {
    let dir = temp_dir();
    let path = dir.join("portable-kb.json");
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[Edit::Upsert(object("one", "One", ObjectKind::Procedure))],
        )
        .unwrap();
    catalog.save_atomic(&path).unwrap();
    let first_snapshot = fs::read(&path).unwrap();
    assert_eq!(
        Catalog::load(&path).unwrap().to_json().unwrap(),
        catalog.to_json().unwrap()
    );

    catalog
        .apply(
            1,
            &[Edit::SetSourcePart {
                id: id("one"),
                name: "source".into(),
                source: Some("Print(1)".into()),
            }],
        )
        .unwrap();
    catalog.save_atomic(&path).unwrap();
    assert_ne!(fs::read(&path).unwrap(), first_snapshot);
    assert_eq!(
        Catalog::load(&path).unwrap().to_json().unwrap(),
        catalog.to_json().unwrap()
    );
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn rejects_duplicate_parts_references_and_oversize_json() {
    let mut duplicate_parts = object("one", "One", ObjectKind::Procedure);
    duplicate_parts.source_parts = vec![
        SourcePart {
            name: "source".into(),
            source: "a".into(),
        },
        SourcePart {
            name: "SOURCE".into(),
            source: "b".into(),
        },
    ];
    let mut catalog = Catalog::empty();
    assert!(catalog.apply(0, &[Edit::Upsert(duplicate_parts)]).is_err());
    assert_eq!(catalog.revision(), 0);

    let huge = format!(
        "{{\"format_version\":1,\"revision\":0,\"objects\":[]}}{}",
        " ".repeat(8 * 1024 * 1024)
    );
    assert!(
        Catalog::from_json(&huge)
            .unwrap_err()
            .contains("exceeds the")
    );
}
