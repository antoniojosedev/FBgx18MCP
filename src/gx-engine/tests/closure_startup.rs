//! The executable closure build: its project starts at the entry's core class, and a dependency the catalog
//! does not hold is a named refusal, never a silent skip.

use gx_engine::catalog::{
    Catalog, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::csharp::{CsprojConfig, CsprojEmitter};
use gx_engine::rebuild::{RebuildOptions, closure_startup_object, rebuild_all};
use std::collections::BTreeMap;

fn procedure(name: &str, source: &str) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(name.to_ascii_lowercase()).unwrap(),
        name,
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: source.into(),
    });
    object
}

#[test]
fn a_library_project_has_no_startup_object() {
    let xml = CsprojEmitter::emit(&CsprojConfig::net8("RebuildApp", false));

    assert!(xml.contains("<OutputType>Library</OutputType>"), "{xml}");
    assert!(!xml.contains("<StartupObject>"), "{xml}");
}

#[test]
fn an_executable_project_starts_at_the_named_class() {
    let config =
        CsprojConfig::net8("adcm37961700", false).as_executable("GeneXus.Programs.adcm37961700");
    let xml = CsprojEmitter::emit(&config);

    assert!(xml.contains("<OutputType>Exe</OutputType>"), "{xml}");
    assert!(
        xml.contains("<StartupObject>GeneXus.Programs.adcm37961700</StartupObject>"),
        "{xml}"
    );
}

#[test]
fn a_parameterless_procedure_starts_at_its_core_class() {
    let mut catalog = BTreeMap::new();
    let object = procedure("Probe", "msg('x', status)");
    let id = object.id.clone();
    catalog.insert(id.clone(), object);

    let startup = closure_startup_object(&Catalog::from_objects_map(catalog), &id)
        .expect("a parameterless Procedure has a Main");

    // The emitter names the core `a<name>` for a parameterless Procedure (see `ProcedureClass::is_split`).
    assert_eq!(startup, "GeneXus.Programs.aprobe");
}

#[test]
fn a_domain_has_no_entry_point() {
    let domain = KbObject::new(ObjectId::new("d").unwrap(), "D", ObjectKind::Domain);
    let id = domain.id.clone();
    let mut catalog = BTreeMap::new();
    catalog.insert(id.clone(), domain);

    let error = closure_startup_object(&Catalog::from_objects_map(catalog), &id)
        .expect_err("a domain has no Main");

    assert!(error.contains("only a Procedure has a Main"), "{error}");
}

#[test]
fn a_dependency_the_catalog_does_not_hold_is_refused_by_name() {
    let mut caller = procedure("Caller", "msg('x', status)");
    caller.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("ghost").unwrap(),
    });
    let mut catalog = BTreeMap::new();
    catalog.insert(caller.id.clone(), caller);
    let catalog = Catalog::from_objects_map(catalog);
    let out = std::env::temp_dir().join(format!("gx_missing_dep_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&out);

    let report = rebuild_all(
        &catalog,
        &out,
        &RebuildOptions::new("net8.0")
            .with_clean(true)
            .with_skip_compile(true),
    )
    .expect("rebuild completes");

    assert!(
        report.refused_objects.iter().any(|refused| refused
            .reason
            .contains("'ghost', which is not in the catalog")),
        "the missing dependency is refused by name: {:?}",
        report.refused_objects
    );
    assert!(!out.join("caller.cs").exists() && !out.join("Caller.cs").exists());
    let _ = std::fs::remove_dir_all(&out);
}
