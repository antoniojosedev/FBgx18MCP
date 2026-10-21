//! A Procedure's `VARIABLES` and `RULES` parts are read or refused, never silently dropped.
//!
//! The KB stores the variables of a Procedure as GeneXus XML. The portable emitter reads only the
//! portable JSON form. Before this guard the XML parse failed silently: the generated class then
//! referenced variables it never declared (`AV_SUCCESS` and the like), and the rebuild only found
//! that out through dotnet build.

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::csharp::ProcedureClass;
use gx_engine::rebuild::{RebuildOptions, rebuild_all};

fn procedure(name: &str, parts: &[(&str, &str)]) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(name.to_ascii_lowercase()).unwrap(),
        name,
        ObjectKind::Procedure,
    );
    for (part, source) in parts {
        object.source_parts.push(SourcePart {
            name: (*part).into(),
            source: (*source).into(),
        });
    }
    object
}

const GENEXUS_XML_VARIABLES: &str = r#"<Variables><StandardVariable Id="1" Name="Today"/><Variable Id="2" Name="Total"/></Variables>"#;

#[test]
fn genexus_xml_variables_are_an_error_not_a_silent_loss() {
    let object = procedure(
        "SendEvent",
        &[
            ("SOURCE", "&Total = 1"),
            ("VARIABLES", GENEXUS_XML_VARIABLES),
        ],
    );

    let error = ProcedureClass::from_kb_object(&object)
        .expect_err("a VARIABLES part that is not the portable form must be refused");

    assert!(
        error.contains("variables of 'SendEvent'"),
        "the error names the object and the part: {error}"
    );
}

#[test]
fn a_rules_part_that_does_not_parse_is_an_error_not_lost_parameters() {
    let object = procedure(
        "Broken",
        &[
            ("SOURCE", "msg('x', status)"),
            ("RULES", "parm(in:&Customer"),
        ],
    );

    let error = ProcedureClass::from_kb_object(&object)
        .expect_err("a RULES part that does not parse must be refused");

    assert!(error.contains("rules of 'Broken'"), "{error}");
}

#[test]
fn an_empty_variables_part_declares_nothing_and_is_not_an_error() {
    let object = procedure(
        "Plain",
        &[("SOURCE", "msg('x', status)"), ("VARIABLES", "   ")],
    );

    ProcedureClass::from_kb_object(&object).expect("an empty part declares no variables");
}

#[test]
fn portable_variables_are_still_declared_as_fields() {
    let object = procedure(
        "Counter",
        &[
            ("SOURCE", "&AV8Sum = 1"),
            (
                "VARIABLES",
                r#"{"AV8Sum":{"kind":"numeric","precision":10,"scale":0}}"#,
            ),
        ],
    );

    let class = ProcedureClass::from_kb_object(&object).expect("portable variables are read");
    let code = format!("{class:?}");
    assert!(code.contains("AV8Sum"), "the variable is declared: {code}");
}

#[test]
fn rebuild_reports_an_unreadable_variables_part_as_an_error() {
    let mut catalog = gx_engine::catalog::Catalog::empty();
    catalog
        .apply(
            0,
            &[gx_engine::catalog::Edit::Upsert(procedure(
                "SendEvent",
                &[
                    ("SOURCE", "&Total = 1"),
                    ("VARIABLES", GENEXUS_XML_VARIABLES),
                ],
            ))],
        )
        .expect("catalog setup");
    let out = std::env::temp_dir().join(format!("gx_proc_parts_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&out);

    let report = rebuild_all(
        &catalog,
        &out,
        &RebuildOptions::new("net8.0")
            .with_clean(true)
            .with_skip_compile(true),
    )
    .expect("rebuild completes");

    assert!(!report.is_success());
    assert!(
        report
            .errors
            .iter()
            .any(|error| error.contains("variables of 'SENDEVENT'")
                || error.contains("variables of 'SendEvent'")),
        "the rebuild reports the object: {:?}",
        report.errors
    );
    assert!(!out.join("SENDEVENT.cs").exists() && !out.join("SendEvent.cs").exists());
    let _ = std::fs::remove_dir_all(&out);
}
