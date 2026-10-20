//! Counts in the Rebuild All report. `procedures_count` is every Procedure in the catalog, whether it is
//! buildable, refused or not generated. Refused SDTs are not Procedures and must not be counted as such;
//! they stay under `refused_count` with their names and reasons.

use gx_engine::catalog::{Catalog, KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::rebuild::{RebuildOptions, rebuild_all};
use std::collections::BTreeMap;

fn procedure(name: &str) -> KbObject {
    let mut object = KbObject::new(ObjectId::new(name).unwrap(), name, ObjectKind::Procedure);
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&Page = 1\n".into(),
    });
    object
}

fn refused(mut object: KbObject) -> KbObject {
    object.refusal = Some("synthetic refusal for the count test".into());
    object
}

#[test]
fn procedures_count_is_every_procedure_and_refused_sdts_are_not_procedures() {
    let mut objects = BTreeMap::new();
    let mut not_generated = procedure("PNotGenerated");
    not_generated.not_generated = Some(
        "GenerateObject is False in the Knowledge Base, so the SDK does not generate this procedure"
            .into(),
    );
    // An SDT is built only when a generated object reaches it; the refused Procedure (a root) reaches them.
    let mut refused_procedure = refused(procedure("PRefused"));
    for name in ["SRefusedOne", "SRefusedTwo", "SRefusedThree"] {
        refused_procedure
            .references
            .push(gx_engine::catalog::Reference {
                kind: gx_engine::catalog::ReferenceKind::Uses,
                target: ObjectId::new(name).unwrap(),
            });
    }
    for object in [procedure("PEmitted"), refused_procedure, not_generated] {
        objects.insert(object.id.clone(), object);
    }
    for name in ["SRefusedOne", "SRefusedTwo", "SRefusedThree"] {
        let sdt = refused(KbObject::new(
            ObjectId::new(name).unwrap(),
            name,
            ObjectKind::StructuredDataType,
        ));
        objects.insert(sdt.id.clone(), sdt);
    }
    let catalog = Catalog::from_objects_map(objects);

    let out = std::env::temp_dir().join(format!("gx_rebuild_counts_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&out);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_skip_compile(true);
    let report = rebuild_all(&catalog, &out, &options).expect("rebuild completes");
    let _ = std::fs::remove_dir_all(&out);

    assert_eq!(
        report.procedures_count, 3,
        "three Procedures are in the catalog, whatever their state"
    );
    assert_eq!(report.sdts_count, 0, "no SDT was emitted");
    assert_eq!(
        report.refused_count, 4,
        "one refused Procedure and three refused SDTs"
    );
    assert!(report.refused_objects.iter().any(|r| r.name == "PRefused"));
    assert_eq!(
        report
            .refused_objects
            .iter()
            .filter(|r| r.kind == "StructuredDataType")
            .count(),
        3,
        "each refused SDT is still reported by name"
    );
    assert!(
        report
            .skipped_objects
            .iter()
            .any(|s| { s.name == "PNotGenerated" && s.reason.starts_with("not generated:") })
    );
}
