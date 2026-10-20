//! Integration tests for the product-facing KB operations (`kb-read`,
//! `kb-query`, `kb-write`) that the MCP Gateway's engine backend invokes.

#![forbid(unsafe_code)]

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart};
use gx_engine::kb_ops::{
    kind_from_type_name, query_objects, read_object, type_name, write_object_part,
};
use gx_engine::udm::{AuthMode, KbConnectionInfo, UdmClient, UdmWriter};
use std::sync::Arc;

fn object(name: &str, kind: ObjectKind, parts: &[(&str, &str)]) -> KbObject {
    let mut object = KbObject::new(ObjectId::new(name).unwrap(), name, kind);
    for (part, source) in parts {
        object.source_parts.push(SourcePart {
            name: part.to_string(),
            source: source.to_string(),
        });
    }
    object
}

fn seeded_client() -> UdmClient {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));

    writer
        .create_object(&object(
            "PAlpha",
            ObjectKind::Procedure,
            &[("SOURCE", "// alpha"), ("RULES", "parm(&x);")],
        ))
        .expect("create PAlpha");

    let mut beta = object("PBeta", ObjectKind::Procedure, &[("SOURCE", "// beta")]);
    beta.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("PAlpha").unwrap(),
    });
    writer.create_object(&beta).expect("create PBeta");

    writer
        .create_object(&object(
            "TGamma",
            ObjectKind::Transaction,
            &[("STRUCTURE", "<Structure/>")],
        ))
        .expect("create TGamma");

    client
}

#[test]
fn read_serves_stored_parts_and_reports_missing_ones() {
    let client = seeded_client();

    let all = read_object(&client, "PAlpha", None).expect("read all parts");
    assert_eq!(all.name, "PAlpha");
    assert_eq!(all.object_type, "Procedure");
    let names: Vec<&str> = all.parts.iter().map(|part| part.name.as_str()).collect();
    assert!(names.contains(&"SOURCE"));
    assert!(names.contains(&"RULES"));
    assert!(all.missing_parts.is_empty());

    // Name and part lookup are case-insensitive; an absent part is reported,
    // never invented.
    let filtered = read_object(
        &client,
        "palpha",
        Some(&["source".to_string(), "Missing".to_string()]),
    )
    .expect("filtered read");
    assert_eq!(filtered.parts.len(), 1);
    assert_eq!(filtered.parts[0].name, "SOURCE");
    assert_eq!(filtered.parts[0].source, "// alpha");
    assert_eq!(filtered.missing_parts, vec!["Missing".to_string()]);
    assert!(filtered.available_parts.contains(&"SOURCE".to_string()));
    assert!(filtered.available_parts.contains(&"RULES".to_string()));
}

#[test]
fn read_of_unknown_object_is_an_error() {
    let client = seeded_client();
    let error = read_object(&client, "PDoesNotExist", None).unwrap_err();
    assert!(error.contains("PDoesNotExist"), "{error}");
    assert!(error.contains("not found"), "{error}");
}

#[test]
fn read_returns_plain_source_text_for_tokenized_parts() {
    let client = seeded_client();
    let all = read_object(&client, "PBeta", None).expect("read PBeta");
    assert_eq!(all.parts.len(), 1);
    assert_eq!(all.parts[0].name, "SOURCE");
    assert_eq!(all.parts[0].source, "// beta");
}

#[test]
fn query_filters_by_name_type_and_reference_with_paging() {
    let client = seeded_client();

    let by_name = query_objects(&client, Some("pbeta"), None, None, 0).expect("query by name");
    assert_eq!(by_name.total, 1);
    assert_eq!(by_name.objects[0].name, "PBeta");
    assert_eq!(by_name.objects[0].object_type, "Procedure");

    let procedures =
        query_objects(&client, None, Some("Procedure"), None, 0).expect("query by type");
    assert_eq!(procedures.total, 2);
    assert!(!procedures.truncated);
    let names: Vec<&str> = procedures
        .objects
        .iter()
        .map(|object| object.name.as_str())
        .collect();
    assert_eq!(names, vec!["PAlpha", "PBeta"]); // sorted by name

    let used_by = query_objects(&client, None, None, Some("PAlpha"), 0).expect("query usedby");
    assert_eq!(used_by.total, 1);
    assert_eq!(used_by.objects[0].name, "PBeta");

    let limited =
        query_objects(&client, None, Some("Procedure"), None, 1).expect("query with limit");
    assert_eq!(limited.objects.len(), 1);
    assert_eq!(limited.total, 2);
    assert!(limited.truncated);

    let unknown_type = query_objects(&client, None, Some("Gizmo"), None, 0).unwrap_err();
    assert!(unknown_type.contains("Gizmo"), "{unknown_type}");
}

#[test]
fn write_replaces_one_part_preserving_the_others_and_bumps_the_version() {
    let client = seeded_client();

    let result = write_object_part(&client, "PAlpha", "Source", "// alpha v2", None, None)
        .expect("write source");
    assert_eq!(result.name, "PAlpha");
    assert_eq!(result.object_type, "Procedure");
    assert_eq!(result.part, "Source");
    assert_eq!(result.version_id, 2);
    assert!(!result.created);
    assert_eq!(result.bytes, "// alpha v2".len());

    let after = read_object(&client, "PAlpha", None).expect("read after write");
    let source = after
        .parts
        .iter()
        .find(|part| part.name == "SOURCE")
        .expect("source part");
    assert_eq!(source.source, "// alpha v2");
    let rules = after
        .parts
        .iter()
        .find(|part| part.name == "RULES")
        .expect("rules part");
    assert_eq!(rules.source, "parm(&x);");
}

#[test]
fn write_of_unknown_part_is_rejected() {
    let client = seeded_client();
    let error =
        write_object_part(&client, "PAlpha", "Methods", "anything", None, None).unwrap_err();
    assert!(error.contains("Methods"), "{error}");
}

#[test]
fn write_refuses_a_type_mismatch_before_any_write() {
    let client = seeded_client();

    let error = write_object_part(
        &client,
        "PAlpha",
        "Source",
        "// x",
        None,
        Some(ObjectKind::WebPanel),
    )
    .unwrap_err();
    assert!(error.contains("Procedure"), "{error}");
    assert!(error.contains("WebPanel"), "{error}");

    // The refusal left the stored object untouched.
    let after = read_object(&client, "PAlpha", None).expect("read after refused write");
    let source = after
        .parts
        .iter()
        .find(|part| part.name == "SOURCE")
        .expect("source part");
    assert_eq!(source.source, "// alpha");
}

#[test]
fn write_requires_an_existing_object_unless_create_selects_a_kind() {
    let client = seeded_client();

    let error = write_object_part(&client, "PDelta", "Source", "// new", None, None).unwrap_err();
    assert!(error.contains("PDelta"), "{error}");

    let created = write_object_part(
        &client,
        "PDelta",
        "Source",
        "// new",
        Some(ObjectKind::Procedure),
        None,
    )
    .expect("create via write");
    assert!(created.created);
    assert_eq!(created.version_id, 1);

    let read = read_object(&client, "PDelta", None).expect("read created object");
    assert_eq!(read.parts[0].source, "// new");
}

#[test]
fn write_on_a_live_connection_is_refused_instead_of_lost() {
    // A live client reads a Knowledge Base database the in-process writer
    // cannot persist to. The write must be refused, never reported as saved.
    let client = UdmClient::from_connection_info(KbConnectionInfo {
        server_instance: "(LocalDB)\\MSSQLLocalDB".to_string(),
        db_name: "GX_KB_NotWritable".to_string(),
        auth_mode: AuthMode::Integrated,
        create_db_in_kb_folder: None,
        directory: None,
        data_file: None,
        log_file: None,
        host_name: None,
        detach_on_close: None,
    });

    let error = write_object_part(&client, "PAlpha", "Source", "// x", None, None).unwrap_err();
    assert!(error.contains("live"), "{error}");
    assert!(error.contains("not persist"), "{error}");
}

#[test]
fn type_name_mapping_round_trips() {
    assert_eq!(type_name(ObjectKind::Procedure), "Procedure");
    assert_eq!(type_name(ObjectKind::StructuredDataType), "SDT");
    assert_eq!(type_name(ObjectKind::WebPanel), "WebPanel");
    assert_eq!(
        kind_from_type_name("sdt"),
        Some(ObjectKind::StructuredDataType)
    );
    assert_eq!(
        kind_from_type_name("StructuredDataType"),
        Some(ObjectKind::StructuredDataType)
    );
    assert_eq!(kind_from_type_name("webpanel"), Some(ObjectKind::WebPanel));
    assert_eq!(kind_from_type_name("Gizmo"), None);
}
