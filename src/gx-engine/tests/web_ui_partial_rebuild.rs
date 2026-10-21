//! Web UI and not-generated objects are not compile units (decision A).
//!
//! - A Rebuild All that skips web UI or lists a not-generated object reports `partial`, never
//!   `complete` or success, with separate counts and the reason for each object.
//! - A Procedure that needs a web UI or not-generated object, directly or through its dependency
//!   chain, is refused: an error, never built.
//! - A `kb-*` build or run (`project_from_catalog`) that targets or needs such an object is refused
//!   by name.

use gx_engine::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    kb::project_from_catalog,
    rebuild::{RebuildOptions, RebuildStatus, rebuild_all},
};
use std::fs;

fn id(value: &str) -> ObjectId {
    ObjectId::new(value).unwrap()
}

fn procedure(key: &str, name: &str, source: &str) -> KbObject {
    let mut object = KbObject::new(id(key), name, ObjectKind::Procedure);
    object.source_parts.push(SourcePart {
        name: "Source".into(),
        source: source.into(),
    });
    object
}

fn uses(object: &mut KbObject, target: &str) {
    object.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id(target),
    });
}

fn calls(object: &mut KbObject, target: &str) {
    object.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: id(target),
    });
}

fn transaction(key: &str, name: &str) -> KbObject {
    let mut object = KbObject::new(id(key), name, ObjectKind::Transaction);
    object.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: format!(
            r#"{{"root":{{"name":"{name}","attributes":[{{"name":"Id","is_key":true,"data_type":{{"kind":"numeric","precision":9,"scale":0}}}}]}}}}"#
        ),
    });
    object
}

fn catalog_of(objects: Vec<KbObject>) -> Catalog {
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &objects.into_iter().map(Edit::Upsert).collect::<Vec<_>>(),
        )
        .expect("catalog setup");
    catalog
}

fn temp_output(tag: &str) -> std::path::PathBuf {
    let dir = std::env::temp_dir().join(format!("gx_web_ui_{tag}_{}", std::process::id()));
    let _ = fs::remove_dir_all(&dir);
    dir
}

fn options() -> RebuildOptions {
    RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_skip_compile(true)
}

/// SYNTHETIC: stands in for the stored XML form of a Transaction STRUCTURE (`<Level Id=...>`).
/// It is not a copy of a stored row; it only has to be a part this build cannot read as JSON.
fn transaction_with_xml_structure(key: &str, name: &str) -> KbObject {
    let mut object = KbObject::new(id(key), name, ObjectKind::Transaction);
    object.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: format!(r#"<Structure Name="{name}"><Level Id="1"/></Structure>"#),
    });
    object
}

#[test]
fn an_unreadable_transaction_structure_is_reported_as_a_warning_not_skipped_silently() {
    let catalog = catalog_of(vec![transaction_with_xml_structure("customer", "Customer")]);
    let out = temp_output("txn_structure_warning");

    let report = rebuild_all(&catalog, &out, &options()).expect("rebuild completes");

    // The catalog keeps object names as stored, so the warning names Customer.
    assert!(
        report
            .warnings
            .iter()
            .any(|w| w.contains("Transaction 'Customer'")
                && w.contains("formula verification was skipped")),
        "the skipped formula check must be reported: {:?}",
        report.warnings
    );
    assert_eq!(report.skipped_count, 1);
    assert!(
        report.skipped_objects[0]
            .reason
            .starts_with("web UI, not generated: "),
        "{}",
        report.skipped_objects[0].reason
    );
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn inherited_keys_report_missing_runtime_checks_and_unapplied_ddl() {
    let mut child = transaction("child", "Child");
    let mut structure: gx_engine::transaction::TransactionStructure =
        serde_json::from_str(&child.source_parts[0].source).unwrap();
    structure.root.attributes[0].name = "ChildId".into();
    structure
        .key_references
        .push(gx_engine::transaction::KeyReference {
            attribute: "ChildId".into(),
            references: "Id".into(),
        });
    child.source_parts[0].source = serde_json::to_string(&structure).unwrap();
    let catalog = catalog_of(vec![child, transaction("parent", "Parent")]);
    let out = temp_output("inherited_key_warning");

    let report = rebuild_all(&catalog, &out, &options()).expect("rebuild completes");

    assert!(report.errors.is_empty(), "{:?}", report.errors);
    assert_eq!(report.status, RebuildStatus::Partial);
    assert_eq!(report.skipped_count, 2);
    assert_eq!(report.compiled_count, 0);
    let warnings: Vec<_> = report
        .warnings
        .iter()
        .filter(|warning| warning.contains("key-existence checks"))
        .collect();
    assert_eq!(warnings.len(), 1, "{:?}", report.warnings);
    let warning = warnings[0];
    assert!(warning.contains("Transaction 'Child'"), "{warning}");
    assert!(warning.contains("not generated"), "{warning}");
    assert!(warning.contains("metadata only"), "{warning}");
    assert!(
        warning.contains("no DDL is emitted or applied"),
        "{warning}"
    );
    assert!(
        !warning.contains("GXENGINE_"),
        "not a control marker: {warning}"
    );
    assert!(report.emitted_files.iter().all(|file| {
        !matches!(
            file.extension().and_then(|extension| extension.to_str()),
            Some("cs" | "sql")
        )
    }));
    let _ = fs::remove_dir_all(&out);
}

// ---- Rebuild All: partial reporting -------------------------------------------------------

#[test]
fn a_skipped_web_ui_object_makes_the_rebuild_partial_not_success() {
    let hello = procedure("hello", "Hello", "msg('hi', status)");
    let catalog = catalog_of(vec![hello, transaction("customer", "Customer")]);
    let out = temp_output("partial");

    let report = rebuild_all(&catalog, &out, &options()).expect("rebuild completes");

    assert!(report.errors.is_empty(), "errors: {:?}", report.errors);
    assert_eq!(report.status, RebuildStatus::Partial);
    assert!(
        !report.is_success(),
        "a run that skipped web UI must not report success"
    );
    assert_eq!(
        report.generated_count, 2,
        "only Hello is generated: its class and its wrapper"
    );
    assert_eq!(report.skipped_count, 1);
    assert_eq!(
        report.skipped_objects[0].name.to_ascii_uppercase(),
        "CUSTOMER"
    );
    assert_eq!(report.skipped_objects[0].kind, "Transaction");
    assert!(
        report.skipped_objects[0]
            .reason
            .starts_with("web UI, not generated: "),
        "{}",
        report.skipped_objects[0].reason
    );
    assert_eq!(report.not_generated_count, 0);
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn a_run_that_did_not_compile_is_not_complete() {
    // Skipping the build means nothing was compiled, so the run is not complete either.
    let catalog = catalog_of(vec![procedure("hello", "Hello", "msg('hi', status)")]);
    let out = temp_output("nobuild");

    let report = rebuild_all(&catalog, &out, &options()).expect("rebuild completes");

    assert!(report.skipped_objects.is_empty());
    assert_eq!(
        report.compiled_count, 0,
        "a build that did not run compiled nothing"
    );
    assert_eq!(report.status, RebuildStatus::Partial);
    assert!(!report.is_success());
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn a_data_provider_is_listed_as_not_generated_with_its_own_count() {
    let hello = procedure("hello", "Hello", "msg('hi', status)");
    let provider = KbObject::new(id("provider"), "ProviderOne", ObjectKind::DataProvider);
    let catalog = catalog_of(vec![hello, transaction("customer", "Customer"), provider]);
    let out = temp_output("dataprovider");

    let report = rebuild_all(&catalog, &out, &options()).expect("rebuild completes");

    assert!(report.errors.is_empty(), "errors: {:?}", report.errors);
    assert_eq!(report.status, RebuildStatus::Partial);
    // Web UI and not-generated objects have separate lists and separate counts.
    assert_eq!(report.skipped_count, 1, "only the Transaction is web UI");
    assert_eq!(report.not_generated_count, 1);
    assert_eq!(report.not_generated_objects.len(), 1);
    let listed = &report.not_generated_objects[0];
    assert_eq!(listed.name.to_ascii_uppercase(), "PROVIDERONE");
    assert_eq!(listed.kind, "DataProvider");
    assert_eq!(
        listed.reason,
        "DataProvider requires explicit output metadata"
    );
    assert!(
        report
            .skipped_objects
            .iter()
            .all(|skipped| skipped.kind != "DataProvider"),
        "a DataProvider is not listed as web UI"
    );
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn a_procedure_without_web_ui_or_not_generated_dependencies_is_not_refused() {
    let mut caller = procedure("caller", "Caller", "msg('hi', status)");
    calls(&mut caller, "hello");
    let catalog = catalog_of(vec![
        caller,
        procedure("hello", "Hello", "msg('hi', status)"),
    ]);
    let out = temp_output("clean");

    let report = rebuild_all(&catalog, &out, &options()).expect("rebuild completes");

    assert!(report.errors.is_empty(), "errors: {:?}", report.errors);
    assert_eq!(report.refused_count, 0);
    assert!(out.join("Caller.cs").exists());
    assert!(out.join("Hello.cs").exists());
    let _ = fs::remove_dir_all(&out);
}

// ---- Rebuild All: refusal of Procedures that need a web UI or not-generated object -------

/// A valid SDT structure (one scalar field) in the portable form the reader holds.
fn sdt_with_one_field(key: &str, name: &str) -> KbObject {
    let mut object = KbObject::new(id(key), name, ObjectKind::StructuredDataType);
    object.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{"fields": [{"node": "field", "name": "Code", "data_type": {"kind": "numeric", "precision": 4, "scale": 0}}]}"#.into(),
    });
    object
}

/// An SDT whose stored structure the reader refused (a real refusal reason, not an empty object).
fn sdt_refused_by_the_reader(key: &str, name: &str) -> KbObject {
    let mut object = KbObject::new(id(key), name, ObjectKind::StructuredDataType);
    object.refusal = Some("STRUCTURE: item 'Id' of 'Inner': VARCHAR has no stored length".into());
    object
}

#[test]
fn an_sdt_that_uses_a_refused_sdt_is_refused_by_name_not_emitted_with_a_dangling_type() {
    // Outer's structure holds an SDT-typed level of Inner. Inner is refused by the reader, so Outer
    // would name a class the build never generates. Outer is refused; Plain is still emitted.
    let mut outer = sdt_with_one_field("outer", "Outer");
    uses(&mut outer, "inner");
    // An SDT is built only when a generated object reaches it, so a Procedure uses Outer and another Plain.
    let mut uses_outer = procedure("uses_outer", "UsesOuter", "msg('x', status)");
    uses(&mut uses_outer, "outer");
    let mut uses_plain = procedure("uses_plain", "UsesPlain", "msg('x', status)");
    uses(&mut uses_plain, "plain");
    let catalog = catalog_of(vec![
        uses_outer,
        uses_plain,
        outer,
        sdt_refused_by_the_reader("inner", "Inner"),
        sdt_with_one_field("plain", "Plain"),
    ]);
    let out = temp_output("sdt_dependency");

    let report = rebuild_all(&catalog, &out, &options()).expect("rebuild completes");

    let refused: Vec<String> = report
        .refused_objects
        .iter()
        .map(|r| r.name.to_ascii_uppercase())
        .collect();
    assert!(
        refused.contains(&"OUTER".to_string()),
        "refused: {refused:?}"
    );
    assert!(
        refused.contains(&"INNER".to_string()),
        "refused: {refused:?}"
    );
    assert!(
        !out.join("SdtOuter.cs").exists(),
        "Outer must not be emitted"
    );
    assert!(
        !refused.contains(&"PLAIN".to_string()),
        "a valid SDT is not refused"
    );
    let outer_reason = report
        .refused_objects
        .iter()
        .find(|r| r.name.eq_ignore_ascii_case("Outer"))
        .map(|r| r.reason.clone())
        .unwrap_or_default();
    assert!(
        outer_reason.contains("Inner"),
        "Outer's refusal names Inner: {outer_reason}"
    );
}

#[test]
fn rebuild_refuses_a_procedure_that_needs_a_transaction() {
    let mut uses_customer = procedure("uses", "UsesCustomer", "msg('x', status)");
    uses(&mut uses_customer, "customer");
    let catalog = catalog_of(vec![uses_customer, transaction("customer", "Customer")]);
    let out = temp_output("refuse_trn");

    let report = rebuild_all(&catalog, &out, &options()).expect("rebuild completes");

    assert_eq!(
        report.status,
        RebuildStatus::Failed,
        "a refusal is an error"
    );
    assert!(!report.is_success());
    assert_eq!(report.refused_count, 1);
    assert_eq!(
        report.refused_objects[0].name.to_ascii_uppercase(),
        "USESCUSTOMER"
    );
    assert!(
        report.errors.iter().any(|error| {
            error.contains("refused to build Procedure")
                && error.to_ascii_uppercase().contains("USESCUSTOMER")
                && error.to_ascii_uppercase().contains("CUSTOMER")
                && error.contains("web UI, not generated")
        }),
        "the refusal must name the procedure and the Transaction: {:?}",
        report.errors
    );
    assert!(
        !out.join("UsesCustomer.cs").exists(),
        "a refused Procedure is never emitted as if it were built"
    );
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn rebuild_refuses_a_procedure_that_needs_a_transaction_through_a_called_procedure() {
    let mut caller = procedure("caller", "Caller", "msg('x', status)");
    calls(&mut caller, "callee");
    let mut callee = procedure("callee", "Callee", "msg('y', status)");
    uses(&mut callee, "customer");
    let catalog = catalog_of(vec![caller, callee, transaction("customer", "Customer")]);
    let out = temp_output("refuse_chain");

    let report = rebuild_all(&catalog, &out, &options()).expect("rebuild completes");

    assert_eq!(report.status, RebuildStatus::Failed);
    let refused: Vec<String> = report
        .refused_objects
        .iter()
        .map(|object| object.name.to_ascii_uppercase())
        .collect();
    assert!(
        refused.contains(&"CALLER".to_string()),
        "caller refused: {refused:?}"
    );
    assert!(
        refused.contains(&"CALLEE".to_string()),
        "callee refused: {refused:?}"
    );
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn rebuild_refuses_a_procedure_that_needs_a_data_provider() {
    let mut uses_provider = procedure("uses", "UsesProvider", "msg('x', status)");
    uses(&mut uses_provider, "provider");
    let provider = KbObject::new(id("provider"), "ProviderOne", ObjectKind::DataProvider);
    let catalog = catalog_of(vec![uses_provider, provider]);
    let out = temp_output("refuse_dp");

    let report = rebuild_all(&catalog, &out, &options()).expect("rebuild completes");

    assert_eq!(report.status, RebuildStatus::Failed);
    assert_eq!(report.refused_count, 1);
    assert!(
        report.errors.iter().any(|error| {
            error.contains("refused to build Procedure")
                && error.to_ascii_uppercase().contains("PROVIDERONE")
                && error.contains("no generator, not generated")
        }),
        "the refusal must name the DataProvider: {:?}",
        report.errors
    );
    let _ = fs::remove_dir_all(&out);
}

// ---- kb-* routing: project_from_catalog ---------------------------------------------------

#[test]
fn a_build_request_that_targets_a_transaction_is_refused() {
    let catalog = catalog_of(vec![transaction("customer", "Customer")]);

    let err = project_from_catalog(&catalog, &id("customer"), None)
        .expect_err("a Transaction entry must be refused");

    assert!(
        err.contains("web UI") && err.to_ascii_uppercase().contains("CUSTOMER"),
        "refusal must name the object and say it is web UI: {err}"
    );
}

#[test]
fn a_build_request_that_targets_a_webpanel_is_refused() {
    let web_panel = KbObject::new(id("wp"), "WebPanelOne", ObjectKind::WebPanel);
    let catalog = catalog_of(vec![web_panel]);

    let err = project_from_catalog(&catalog, &id("wp"), None)
        .expect_err("a WebPanel entry must be refused");

    assert!(
        err.contains("web UI") && err.to_ascii_uppercase().contains("WEBPANELONE"),
        "refusal must name the object and say it is web UI: {err}"
    );
}

#[test]
fn a_build_request_that_targets_a_data_provider_is_refused() {
    let provider = KbObject::new(id("provider"), "ProviderOne", ObjectKind::DataProvider);
    let catalog = catalog_of(vec![provider]);

    let err = project_from_catalog(&catalog, &id("provider"), None)
        .expect_err("a DataProvider entry must be refused");

    assert!(
        err.contains("no generator") && err.to_ascii_uppercase().contains("PROVIDERONE"),
        "refusal must name the object and say it has no generator: {err}"
    );
}

#[test]
fn a_build_request_that_targets_a_data_selector_or_data_view_is_refused() {
    for (key, kind) in [
        ("selector", ObjectKind::DataSelector),
        ("view", ObjectKind::DataView),
    ] {
        let object = KbObject::new(id(key), "Target", kind);
        let catalog = catalog_of(vec![object]);

        let err = project_from_catalog(&catalog, &id(key), None)
            .expect_err("a not-generated entry must be refused");

        assert!(err.contains("no generator"), "{kind:?} refusal: {err}");
    }
}

#[test]
fn a_procedure_that_uses_a_data_provider_is_refused_not_built() {
    let mut uses_provider = procedure("proc", "UsesProvider", "msg('x', status)");
    uses(&mut uses_provider, "provider");
    let provider = KbObject::new(id("provider"), "ProviderOne", ObjectKind::DataProvider);
    let catalog = catalog_of(vec![uses_provider, provider]);

    let err = project_from_catalog(&catalog, &id("proc"), None)
        .expect_err("a dependency on a DataProvider must be refused, never built");

    assert!(
        err.contains("no generator") && err.to_ascii_uppercase().contains("PROVIDERONE"),
        "refusal must name the DataProvider dependency: {err}"
    );
}

#[test]
fn a_procedure_that_uses_a_transaction_is_refused_not_built() {
    let mut uses_customer = procedure("proc", "UsesCustomer", "msg('x', status)");
    uses(&mut uses_customer, "customer");
    let catalog = catalog_of(vec![uses_customer, transaction("customer", "Customer")]);

    let err = project_from_catalog(&catalog, &id("proc"), None)
        .expect_err("a dependency on a Transaction must be refused, never built");

    assert!(
        err.contains("web UI") && err.to_ascii_uppercase().contains("CUSTOMER"),
        "refusal must name the Transaction dependency: {err}"
    );
}

// ---- Mutation-check gaps: each guard must be the thing a test depends on -------------------

#[test]
fn a_run_that_compiles_but_has_a_not_generated_object_is_still_partial() {
    // Only the not-generated object can make this run partial here: the project builds
    // (compiled_count == generated_count), so a failed build or a skip cannot explain it.
    if std::process::Command::new("dotnet")
        .arg("--version")
        .output()
        .is_err()
    {
        return; // dotnet CLI not installed: the build path cannot run
    }
    let hello = procedure("hello", "Hello", "msg('hi', status)");
    let provider = KbObject::new(id("provider"), "ProviderOne", ObjectKind::DataProvider);
    let catalog = catalog_of(vec![hello, provider]);
    let out = temp_output("notgen_compiled");

    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_assembly_name("NotGenCompiled");
    let report = rebuild_all(&catalog, &out, &options).expect("rebuild completes");

    assert!(report.errors.is_empty(), "errors: {:?}", report.errors);
    // Hello is two compile units (its class and its wrapper), both compiled.
    assert_eq!(report.generated_count, 2);
    assert_eq!(
        report.compiled_count, 2,
        "the project must build, or this test does not isolate the not-generated condition"
    );
    assert_eq!(report.skipped_count, 0);
    assert_eq!(report.not_generated_count, 1);
    assert_eq!(report.status, RebuildStatus::Partial);
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn a_domain_based_on_a_webpanel_is_refused_by_name_not_by_a_generic_message() {
    let mut caller = procedure("caller", "Caller", "msg('hi', status)");
    caller.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: id("code"),
    });
    let mut domain = KbObject::new(id("code"), "CustCode", ObjectKind::Domain);
    domain.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"kind":"numeric","precision":6,"scale":0}"#.into(),
    });
    domain.references.push(Reference {
        kind: ReferenceKind::BasedOn,
        target: id("page"),
    });
    let page = KbObject::new(id("page"), "PageOne", ObjectKind::WebPanel);
    let catalog = catalog_of(vec![caller, domain, page]);

    let err = project_from_catalog(&catalog, &id("caller"), None)
        .expect_err("a Domain that depends on a WebPanel must be refused");

    assert!(
        err.contains("web UI") && err.to_ascii_uppercase().contains("PAGEONE"),
        "the refusal must name the web UI dependency, not a generic kind error: {err}"
    );
}

#[test]
fn an_sdt_that_uses_a_transaction_is_refused_by_name_not_by_a_generic_message() {
    let mut caller = procedure("caller", "Caller", "msg('hi', status)");
    uses(&mut caller, "fields");
    let mut fields = KbObject::new(id("fields"), "Address", ObjectKind::StructuredDataType);
    fields.source_parts.push(SourcePart {
        name: "FIELDS".into(),
        source: "{}".into(),
    });
    fields.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("customer"),
    });
    let catalog = catalog_of(vec![caller, fields, transaction("customer", "Customer")]);

    let err = project_from_catalog(&catalog, &id("caller"), None)
        .expect_err("an SDT that uses a Transaction must be refused");

    assert!(
        err.contains("web UI") && err.to_ascii_uppercase().contains("CUSTOMER"),
        "the refusal must name the web UI dependency, not a generic kind error: {err}"
    );
}
