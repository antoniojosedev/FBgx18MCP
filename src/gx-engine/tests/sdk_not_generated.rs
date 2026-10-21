//! A Procedure the Knowledge Base stores as not generated is skipped, not refused. The stored
//! `GenerateObject` value decides it (`ModelEntityProperty` joined to `Property: GenerateObject`). A Procedure
//! whose active version has no SOURCE part is refused by name, unless it is stored GenerateObject False. Every
//! other object is unchanged: an SDT with no parts is still refused, and a caller of a not-generated Procedure is
//! refused by name.

#![forbid(unsafe_code)]

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::rebuild::{RebuildOptions, rebuild_all};
use gx_engine::udm::{UdmClient, UdmReader, UdmWriter};
use std::sync::Arc;

/// A Procedure whose VARIABLES hold a type the engine does not map (code 255), so the reader refuses
/// it when the object is generated. The flag decides whether that refusal can happen at all.
fn refusing_procedure(name: &str) -> KbObject {
    let variables = concat!(
        "<Variables><Variable Name=\"FilterTags\"><Properties>",
        "<Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property>",
        "<Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;255&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property>",
        "</Properties></Variable></Variables>"
    );
    let mut object = KbObject::new(ObjectId::new(name).unwrap(), name, ObjectKind::Procedure);
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&Page = 1\n".into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: variables.into(),
    });
    object
}

/// Writes one Procedure and returns its entity id and entity type id in the in-memory Knowledge Base.
fn write(client: &UdmClient, object: &KbObject) -> (i32, i32) {
    let created = UdmWriter::new(Arc::new(client.clone()))
        .create_object(object)
        .expect("create object");
    (created.entity_id, created.entity_type_id)
}

fn read(client: &UdmClient, name: &str) -> KbObject {
    UdmReader::new(Arc::new(client.clone()))
        .read_object_by_name(name)
        .expect("read")
        .expect("object exists")
}

#[test]
fn a_stored_generate_object_false_procedure_is_not_generated_and_its_parts_do_not_refuse_it() {
    let client = UdmClient::new_in_memory();
    let (id, type_id) = write(&client, &refusing_procedure("PModule"));
    client.set_generate_flag(1, type_id, id, "False").unwrap();

    let object = UdmReader::new(Arc::new(client.clone()))
        .read_catalog()
        .expect("catalog reads")
        .get(&ObjectId::new("PModule").unwrap())
        .cloned()
        .expect("object is in the catalog");
    assert_eq!(
        object.refusal, None,
        "a not-generated procedure is not refused by its parts"
    );
    assert!(object.not_generated.is_some(), "it is marked not generated");
    assert!(
        object.source_parts.is_empty(),
        "its parts are not read on the build path"
    );
}

#[test]
fn a_stored_generate_object_true_procedure_is_generated_as_before() {
    let client = UdmClient::new_in_memory();
    let (id, type_id) = write(&client, &refusing_procedure("PGenerated"));
    client.set_generate_flag(1, type_id, id, "True").unwrap();

    let object = read(&client, "PGenerated");
    assert!(object.not_generated.is_none());
    assert!(
        object
            .refusal
            .as_deref()
            .is_some_and(|reason| reason.contains("255")),
        "the parts still refuse a generated procedure: {:?}",
        object.refusal
    );
}

#[test]
fn a_procedure_with_no_stored_generate_object_value_is_generated_as_before() {
    let client = UdmClient::new_in_memory();
    write(&client, &refusing_procedure("PNoValue"));

    let object = read(&client, "PNoValue");
    assert!(
        object.not_generated.is_none(),
        "no stored value keeps the procedure generated"
    );
    assert!(object.refusal.is_some());
}

#[test]
fn a_generate_object_value_that_is_neither_true_nor_false_is_refused_not_guessed() {
    let client = UdmClient::new_in_memory();
    let (id, type_id) = write(&client, &refusing_procedure("PMaybe"));
    client.set_generate_flag(1, type_id, id, "Maybe").unwrap();

    let object = read(&client, "PMaybe");
    assert!(
        object.not_generated.is_none(),
        "an unknown value is never read as not generated"
    );
    let reason = object.refusal.expect("refused");
    assert!(reason.contains("neither True nor False"), "{reason}");
}

#[test]
fn an_empty_procedure_and_an_empty_sdt_are_both_refused_by_name() {
    let client = UdmClient::new_in_memory();
    write(
        &client,
        &KbObject::new(
            ObjectId::new("PEmpty").unwrap(),
            "PEmpty",
            ObjectKind::Procedure,
        ),
    );
    write(
        &client,
        &KbObject::new(
            ObjectId::new("SEmpty").unwrap(),
            "SEmpty",
            ObjectKind::StructuredDataType,
        ),
    );

    // An empty Procedure with GenerateObject True has no SOURCE part, so it is refused by name, never skipped.
    let procedure = read(&client, "PEmpty");
    assert_eq!(
        procedure.not_generated, None,
        "{:?}",
        procedure.not_generated
    );
    assert!(
        procedure
            .refusal
            .as_deref()
            .is_some_and(|r| r.contains("no SOURCE part")),
        "{:?}",
        procedure.refusal
    );

    let sdt = read(&client, "SEmpty");
    assert!(
        sdt.refusal
            .as_deref()
            .is_some_and(|r| r.contains("has no parts")),
        "an empty SDT keeps its refusal: {:?}",
        sdt.refusal
    );
    assert_eq!(sdt.not_generated, None);
}

#[test]
fn a_procedure_with_a_rules_part_and_no_source_part_is_refused_by_name_not_skipped() {
    // The active version holds a part but no SOURCE part: it is refused by name, and never marked not generated.
    let client = UdmClient::new_in_memory();
    let mut object = KbObject::new(
        ObjectId::new("PRulesOnly").unwrap(),
        "PRulesOnly",
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "RULES".into(),
        source: "parm(in:&Page);\n".into(),
    });
    write(&client, &object);

    let procedure = read(&client, "PRulesOnly");
    assert_eq!(
        procedure.not_generated, None,
        "{:?}",
        procedure.not_generated
    );
    assert!(
        procedure
            .refusal
            .as_deref()
            .is_some_and(|r| r.contains("no SOURCE part")),
        "{:?}",
        procedure.refusal
    );
}

#[test]
fn an_empty_generate_object_false_procedure_keeps_its_stored_reason_and_is_not_refused() {
    let client = UdmClient::new_in_memory();
    let (id, type_id) = write(
        &client,
        &KbObject::new(
            ObjectId::new("PEmptyOff").unwrap(),
            "PEmptyOff",
            ObjectKind::Procedure,
        ),
    );
    client.set_generate_flag(1, type_id, id, "False").unwrap();

    let procedure = read(&client, "PEmptyOff");
    assert_eq!(procedure.refusal, None, "{:?}", procedure.refusal);
    assert!(
        procedure
            .not_generated
            .as_deref()
            .is_some_and(|r| r.starts_with("GenerateObject is False")),
        "the stored flag decides it before the parts are read: {:?}",
        procedure.not_generated
    );
}

#[test]
fn a_not_generated_procedure_is_skipped_by_the_rebuild_and_its_caller_is_refused_by_name() {
    let client = UdmClient::new_in_memory();
    let (module_id, module_type) = write(&client, &refusing_procedure("PModule"));
    client
        .set_generate_flag(1, module_type, module_id, "False")
        .unwrap();

    // A caller that calls the not-generated procedure.
    let mut caller = KbObject::new(
        ObjectId::new("PCaller").unwrap(),
        "PCaller",
        ObjectKind::Procedure,
    );
    caller.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "PModule()\n".into(),
    });
    caller.references.push(gx_engine::catalog::Reference {
        kind: gx_engine::catalog::ReferenceKind::Calls,
        target: ObjectId::new("PModule").unwrap(),
    });
    write(&client, &caller);

    let catalog = UdmReader::new(Arc::new(client.clone()))
        .read_catalog()
        .expect("catalog reads");
    let out = std::env::temp_dir().join(format!("gx_sdk_not_generated_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&out);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_skip_compile(true);
    let report = rebuild_all(&catalog, &out, &options).expect("rebuild completes");

    let skipped = report
        .skipped_objects
        .iter()
        .find(|s| s.name == "PModule")
        .expect("the not-generated procedure is skipped by name");
    assert!(
        skipped
            .reason
            .starts_with("not generated: GenerateObject is False"),
        "{}",
        skipped.reason
    );
    assert!(
        !report.refused_objects.iter().any(|r| r.name == "PModule"),
        "it is not refused"
    );
    let refused = report
        .refused_objects
        .iter()
        .find(|r| r.name == "PCaller")
        .expect("the caller is refused");
    assert!(
        refused
            .reason
            .starts_with("needs Procedure 'PModule' (not generated:"),
        "{}",
        refused.reason
    );
    assert!(
        report.errors.iter().any(|e| e.contains("PCaller")),
        "a refused caller is an error"
    );
    let _ = std::fs::remove_dir_all(&out);
}
