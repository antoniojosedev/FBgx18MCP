//! The rebuild targets only the compile units reachable from the objects a Knowledge Base generates. A GeneXus
//! library structured data type that nothing generated reaches is reported as not targeted: it is neither
//! emitted nor refused, even when this engine cannot decode it. A library structured data type a generated
//! object reaches is still refused, as before.

use gx_engine::catalog::{Catalog, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind};
use gx_engine::rebuild::{RebuildOptions, RebuildReport, rebuild_all};
use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

static SEQUENCE: AtomicU64 = AtomicU64::new(0);

fn object(id: &str, kind: ObjectKind) -> KbObject {
    KbObject::new(ObjectId::new(id).unwrap(), id, kind)
}

fn library_sdt(id: &str, refusal: Option<&str>) -> KbObject {
    let mut sdt = object(id, ObjectKind::StructuredDataType);
    sdt.library = true;
    sdt.refusal = refusal.map(str::to_string);
    sdt
}

fn user_sdt(id: &str, refusal: Option<&str>) -> KbObject {
    let mut sdt = object(id, ObjectKind::StructuredDataType);
    sdt.refusal = refusal.map(str::to_string);
    sdt
}

fn uses(mut object: KbObject, target: &str) -> KbObject {
    object.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new(target).unwrap(),
    });
    object
}

fn catalog(objects: Vec<KbObject>) -> Catalog {
    let map: BTreeMap<ObjectId, KbObject> = objects
        .into_iter()
        .map(|object| (object.id.clone(), object))
        .collect();
    Catalog::from_objects_map(map)
}

fn rebuild(catalog: &Catalog) -> RebuildReport {
    let sequence = SEQUENCE.fetch_add(1, Ordering::SeqCst);
    let output: PathBuf = std::env::temp_dir().join(format!(
        "gx-engine-reachability-{}-{sequence}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&output);
    let options = RebuildOptions::new("net8.0").with_skip_compile(true);
    let report = rebuild_all(catalog, &output, &options).expect("rebuild runs");
    let _ = std::fs::remove_dir_all(&output);
    report
}

#[test]
fn an_unreached_library_sdt_the_reader_refused_is_not_targeted_and_not_refused() {
    let report = rebuild(&catalog(vec![library_sdt(
        "event",
        Some("type code 45 is not mapped by this engine"),
    )]));

    assert_eq!(report.refused_count, 0, "{:?}", report.refused_objects);
    assert_eq!(report.not_targeted_count, 1);
    assert_eq!(report.not_targeted_objects[0].name, "event");
    assert!(
        report.errors.iter().all(|error| !error.contains("event")),
        "{:?}",
        report.errors
    );
}

#[test]
fn a_library_sdt_a_procedure_reaches_stays_refused() {
    let procedure = uses(object("main", ObjectKind::Procedure), "messages");
    let report = rebuild(&catalog(vec![
        procedure,
        library_sdt("messages", Some("type code 7 is not mapped by this engine")),
    ]));

    assert_eq!(report.not_targeted_count, 0);
    assert!(
        report
            .refused_objects
            .iter()
            .any(|refused| refused.name == "messages"),
        "{:?}",
        report.refused_objects
    );
    assert!(
        report
            .refused_objects
            .iter()
            .any(|refused| refused.name == "main"),
        "the procedure that needs the refused SDT is refused by name: {:?}",
        report.refused_objects
    );
}

#[test]
fn a_user_sdt_nothing_references_is_not_targeted_even_when_the_reader_refused_it() {
    // The SDK generates an SDT only when a generated object reaches it (KBTeste `SDTLiveTest`).
    let report = rebuild(&catalog(vec![user_sdt(
        "rustrecordprobe",
        Some("NUMERIC has no stored length"),
    )]));

    assert_eq!(report.not_targeted_count, 1);
    assert_eq!(report.refused_count, 0);
}

#[test]
fn a_catalog_without_the_library_flag_targets_an_sdt_only_when_reached() {
    // A snapshot written before the flag existed deserializes with `library: false`; reachability still
    // applies, so an SDT nothing reaches is not targeted, and a reached refused one is refused.
    let json =
        r#"{"id":"event","name":"event","kind":"structured_data_type","refusal":"type code 45"}"#;
    let sdt: KbObject = serde_json::from_str(json).expect("old object JSON parses");
    assert!(!sdt.library);
    let unreached = rebuild(&catalog(vec![sdt.clone()]));
    assert_eq!(unreached.not_targeted_count, 1);
    assert_eq!(unreached.refused_count, 0);
    let reached = rebuild(&catalog(vec![
        uses(object("main", ObjectKind::Procedure), "event"),
        sdt,
    ]));
    assert_eq!(reached.not_targeted_count, 0);
    assert!(reached.refused_objects.iter().any(|r| r.name == "event"));
}

#[test]
fn a_closure_build_with_nothing_to_generate_emits_no_project_and_is_partial() {
    // #675: `rebuild-all --object X --closure` on a Procedure stored as not main that nothing reaches. The
    // SDK does not generate it, so there is no class for `Main`: emitting a project that starts there made
    // `dotnet build` fail with CS1555. Nothing to generate means nothing to emit or compile.
    let mut procedure = object("adddevicegroups", ObjectKind::Procedure);
    procedure.main = Some(false);
    let sequence = SEQUENCE.fetch_add(1, Ordering::SeqCst);
    let output: PathBuf = std::env::temp_dir().join(format!(
        "gx-engine-reachability-{}-{sequence}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&output);
    let options = RebuildOptions::new("net8.0")
        .with_skip_compile(true)
        .with_startup_object("GeneXus.Programs.aadddevicegroups");
    let report = rebuild_all(&catalog(vec![procedure]), &output, &options).expect("rebuild runs");
    let emitted_project = output.join("RebuildApp.csproj").exists();
    let _ = std::fs::remove_dir_all(&output);

    assert_eq!(report.not_targeted_count, 1);
    assert_eq!(report.generated_count, 0);
    assert!(
        report.emitted_files.is_empty(),
        "{:?}",
        report.emitted_files
    );
    assert!(
        !emitted_project,
        "no project is written for nothing to build"
    );
    assert!(report.errors.is_empty(), "{:?}", report.errors);
    assert_eq!(report.status, gx_engine::rebuild::RebuildStatus::Partial);
    assert!(
        report
            .warnings
            .iter()
            .any(|w| w.contains("nothing to generate")),
        "the report says why nothing was built: {:?}",
        report.warnings
    );
}

#[test]
fn the_library_flag_round_trips_and_is_omitted_when_false() {
    let library = serde_json::to_string(&library_sdt("event", None)).unwrap();
    assert!(library.contains("\"library\":true"), "{library}");
    let user = serde_json::to_string(&user_sdt("probe", None)).unwrap();
    assert!(!user.contains("library"), "{user}");
    let back: KbObject = serde_json::from_str(&library).unwrap();
    assert!(back.library);
}
