//! The native reader holds a Procedure's stored VARIABLES as portable declarations, and refuses by
//! name what it cannot model. An object with no parts is never returned empty: it is refused by name, and a
//! Module (no source of its own) is exempt.
//!
//! These tests write objects through the same writer the Knowledge Base uses and read them back
//! through the reader, so the stored form is the real one (GeneXus XML, `ATTCUSTOMTYPE` codes).

#![forbid(unsafe_code)]

use gx_engine::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::kb::project_from_catalog;
use gx_engine::kb_ops;
use gx_engine::model::DataType;
use gx_engine::rebuild::{RebuildOptions, RebuildStatus, rebuild_all};
use gx_engine::udm::mock::{EntityVersionRecord, ModelEntityVersionRecord};
use gx_engine::udm::{UdmClient, UdmReader, UdmWriter};
use std::collections::BTreeMap;
use std::sync::Arc;

fn escape(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}

/// One declaration in the stored form. `type_fragment` is the `AttCustomType` XML (unescaped).
fn declaration(name: &str, type_fragment: Option<&str>, extra: &[(&str, &str)]) -> String {
    let mut props =
        String::from("<Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property>");
    if let Some(fragment) = type_fragment {
        props.push_str(&format!(
            "<Property><Name>ATTCUSTOMTYPE</Name><Value>{}</Value></Property>",
            escape(fragment)
        ));
    }
    for (key, value) in extra {
        props.push_str(&format!(
            "<Property><Name>{key}</Name><Value>{value}</Value></Property>"
        ));
    }
    format!("<Variable Name=\"{name}\"><Properties>{props}</Properties></Variable>")
}

fn typed(code: &str) -> String {
    format!("<AttCustomType><DataType>{code}</DataType></AttCustomType>")
}

fn sdt_typed(sdt_entity_id: i32) -> String {
    format!(
        "<AttCustomType><DataType>254</DataType><Guid>&lt;StructureTypeReference&gt;&lt;Type&gt;447527b5-9210-4523-898b-5dccb17be60a&lt;/Type&gt;&lt;Id&gt;{sdt_entity_id}&lt;/Id&gt;&lt;/StructureTypeReference&gt;</Guid></AttCustomType>"
    )
}

fn variables(declarations: &[String]) -> String {
    format!("<Variables>{}</Variables>", declarations.concat())
}

fn sdt(client: &UdmClient, name: &str) -> i32 {
    let mut object = KbObject::new(
        ObjectId::new(name).unwrap(),
        name,
        ObjectKind::StructuredDataType,
    );
    object.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: format!(
            r#"{{"root":{{"name":"{name}","attributes":[{{"name":"Id","is_key":true,"data_type":{{"kind":"numeric","precision":9,"scale":0}}}}]}}}}"#
        ),
    });
    UdmWriter::new(Arc::new(client.clone()))
        .create_object(&object)
        .expect("create SDT")
        .entity_id
}

fn procedure_with_variables(name: &str, variables_xml: &str) -> KbObject {
    let mut object = KbObject::new(ObjectId::new(name).unwrap(), name, ObjectKind::Procedure);
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&Page = 1\n".into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: variables_xml.into(),
    });
    object
}

fn write_and_read(client: &UdmClient, object: &KbObject) -> KbObject {
    UdmWriter::new(Arc::new(client.clone()))
        .create_object(object)
        .expect("create object");
    UdmReader::new(Arc::new(client.clone()))
        .read_object_by_name(&object.name)
        .expect("read object")
        .expect("object exists")
}

fn held_variables(object: &KbObject) -> Option<BTreeMap<String, DataType>> {
    let part = object.source_parts.iter().find(|p| p.name == "VARIABLES")?;
    Some(serde_json::from_str(&part.source).expect("the held part is the portable declarations"))
}

// ---- held as portable declarations ---------------------------------------------------------

#[test]
fn stored_declarations_are_held_as_portable_types_with_their_sdt_reference() {
    let client = UdmClient::new_in_memory();
    let sdt_id = sdt(&client, "Messages");
    let xml = variables(&[
        declaration("Page", Some(&typed("4")), &[("Length", "6")]),
        declaration(
            "Hits",
            Some(&typed("4")),
            &[("Length", "5"), ("Decimals", "2")],
        ),
        declaration("Time", Some(&typed("5")), &[("Length", "8")]),
        declaration("Today", Some(&typed("6")), &[]),
        declaration("Success", Some(&typed("15")), &[]),
        declaration(
            "Variable",
            Some(&typed("13")),
            &[("Length", "40"), ("AttMaxLen", "40")],
        ),
        declaration(
            "Messages",
            Some(&sdt_typed(sdt_id)),
            &[("DataTypeString", "Messages"), ("idBasedOn", "(none)")],
        ),
    ]);
    let read = write_and_read(&client, &procedure_with_variables("PTyped", &xml));

    assert_eq!(read.refusal, None, "every declaration is mapped");
    let held = held_variables(&read).expect("VARIABLES is held");
    assert_eq!(
        held["Page"],
        DataType::Numeric {
            precision: 6,
            scale: 0
        }
    );
    assert_eq!(
        held["Hits"],
        DataType::Numeric {
            precision: 5,
            scale: 2
        }
    );
    assert_eq!(held["Time"], DataType::Character { length: 8 });
    assert_eq!(held["Today"], DataType::Date {});
    assert_eq!(held["Success"], DataType::Boolean {});
    assert_eq!(held["Variable"], DataType::Varchar { length: 40 });
    assert_eq!(
        held["Messages"],
        DataType::Record {
            schema: "Messages".into()
        }
    );

    assert!(
        read.references.contains(&Reference {
            kind: ReferenceKind::Uses,
            target: ObjectId::new("Messages").unwrap(),
        }),
        "the SDT a variable names is an explicit Uses reference: {:?}",
        read.references
    );
}

// ---- refused by name, never held as a partial part -------------------------------------------

#[test]
fn an_unmapped_declaration_refuses_the_object_by_name_and_holds_no_variables_part() {
    let client = UdmClient::new_in_memory();
    let xml = variables(&[
        declaration("Page", Some(&typed("4")), &[("Length", "6")]),
        declaration("FilterTags", Some(&typed("255")), &[]),
    ]);
    let read = write_and_read(&client, &procedure_with_variables("PUnmapped", &xml));

    let reason = read.refusal.as_deref().expect("the object is refused");
    assert!(reason.contains("variable 'FilterTags'"), "{reason}");
    assert!(reason.contains("type code 255"), "{reason}");
    assert!(
        read.source_parts.iter().all(|p| p.name != "VARIABLES"),
        "no partial VARIABLES part is held"
    );
}

#[test]
fn a_collection_refuses_the_object_by_name() {
    let client = UdmClient::new_in_memory();
    let xml = variables(&[declaration(
        "Groups",
        Some(&typed("5")),
        &[("Length", "20"), ("AttCollection", "True")],
    )]);
    let read = write_and_read(&client, &procedure_with_variables("PCollection", &xml));

    let reason = read.refusal.as_deref().expect("the object is refused");
    assert!(reason.contains("variable 'Groups'"), "{reason}");
    assert!(reason.contains("collections are not mapped"), "{reason}");
    assert!(
        reason.contains("no captured SDK C# form for a collection variable"),
        "{reason}"
    );
}

// The stored row of Domain 51 (ObjectName) of KBTeste (GX18), read in full from its EntityVersion row.
const OBJECT_NAME_DOMAIN_ROW: &str = r##"<Properties><Property><Name>Name</Name><Value>ObjectName</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;13&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>256</Value></Property><Property><Name>AttMaxLen</Name><Value>256</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;

/// Seeds one Domain (entity type 33) with its stored row, as the Knowledge Base holds it.
fn seed_domain(client: &UdmClient, entity_id: i32, name: &str, row: &str) {
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 33,
            entity_id,
            entity_version_id: 1,
            name: name.to_string(),
        })
        .expect("model row of the domain");
    client
        .insert_entity_version(EntityVersionRecord {
            entity_type_id: 33,
            entity_id,
            entity_version_id: 1,
            data: Vec::new(),
            properties: Some(row.to_string()),
        })
        .expect("stored row of the domain");
}

#[test]
fn a_variable_based_on_a_typed_domain_takes_its_stored_type_and_a_basedon_reference() {
    // SDK (KBTeste, genexus_read part=Variables, structured): &GxObject : ObjectName is VARCHAR 256.
    let client = UdmClient::new_in_memory();
    seed_domain(&client, 51, "ObjectName", OBJECT_NAME_DOMAIN_ROW);
    let xml = variables(&[declaration(
        "GxObject",
        None,
        &[("idBasedOn", "00972a17-9975-449e-aab1-d26165d51393-51")],
    )]);
    let read = write_and_read(&client, &procedure_with_variables("PBasedOn", &xml));

    assert!(read.refusal.is_none(), "{:?}", read.refusal);
    let held = held_variables(&read).expect("the VARIABLES part is held");
    assert_eq!(held["GxObject"], DataType::Varchar { length: 256 });
    assert!(
        read.references
            .iter()
            .any(|r| r.kind == ReferenceKind::BasedOn),
        "{:?}",
        read.references
    );
}

#[test]
fn a_variable_based_on_a_domain_with_no_stored_type_refuses_the_object_by_name() {
    // No Domain 51 is in the Knowledge Base, so the variable has no type: the object is refused, named.
    let client = UdmClient::new_in_memory();
    let xml = variables(&[declaration(
        "GxObject",
        None,
        &[("idBasedOn", "00972a17-9975-449e-aab1-d26165d51393-51")],
    )]);
    let read = write_and_read(&client, &procedure_with_variables("PBasedOn", &xml));

    let reason = read.refusal.as_deref().expect("the object is refused");
    assert!(reason.contains("variable 'GxObject'"), "{reason}");
    assert!(
        reason.contains("the domain has no stored type this engine maps"),
        "{reason}"
    );
}

// ---- an object with no parts is never returned empty ----------------------------------------------

#[test]
fn a_procedure_with_no_parts_is_refused_by_name_and_a_module_with_no_parts_is_not() {
    let client = UdmClient::new_in_memory();
    let writer = UdmWriter::new(Arc::new(client.clone()));
    writer
        .create_object(&KbObject::new(
            ObjectId::new("PEmpty").unwrap(),
            "PEmpty",
            ObjectKind::Procedure,
        ))
        .expect("create empty procedure");
    writer
        .create_object(&KbObject::new(
            ObjectId::new("Folder").unwrap(),
            "Folder",
            ObjectKind::Module,
        ))
        .expect("create empty module");

    let reader = UdmReader::new(Arc::new(client.clone()));
    let procedure = reader
        .read_object_by_name("PEmpty")
        .unwrap()
        .expect("procedure exists");
    // An empty Procedure with GenerateObject True has no SOURCE part, so it is refused by name, not skipped.
    assert_eq!(
        procedure.not_generated, None,
        "{:?}",
        procedure.not_generated
    );
    assert!(
        procedure
            .refusal
            .as_deref()
            .is_some_and(|reason| reason.contains("no SOURCE part")),
        "{:?}",
        procedure.refusal
    );

    let module = reader
        .read_object_by_name("Folder")
        .unwrap()
        .expect("module exists");
    assert_eq!(
        module.refusal, None,
        "a Module has no source of its own and is exempt"
    );
}

#[test]
fn kb_ops_never_reads_a_refused_object_back_as_complete() {
    let client = UdmClient::new_in_memory();
    let xml = variables(&[declaration("FilterTags", Some(&typed("255")), &[])]);
    UdmWriter::new(Arc::new(client.clone()))
        .create_object(&procedure_with_variables("PRefusedRead", &xml))
        .expect("create object");

    let err = kb_ops::read_object(&client, "PRefusedRead", None)
        .expect_err("a refused object is not read back");
    assert!(err.contains("refused by the reader"), "{err}");
    assert!(err.contains("variable 'FilterTags'"), "{err}");
}

// ---- the catalog: a refused object stays known, and every build that needs it is refused -------

fn refused_procedure(name: &str) -> KbObject {
    let mut object = KbObject::new(ObjectId::new(name).unwrap(), name, ObjectKind::Procedure);
    object.refusal =
        Some("VARIABLES: variable 'FilterTags': type code 255 is not mapped by this engine".into());
    object
}

fn caller_of(name: &str, target: &str) -> KbObject {
    let mut object = KbObject::new(ObjectId::new(name).unwrap(), name, ObjectKind::Procedure);
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&x = 1\n".into(),
    });
    object.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new(target).unwrap(),
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

#[test]
fn rebuild_reports_a_refused_procedure_by_name_and_refuses_its_caller() {
    let catalog = catalog_of(vec![
        refused_procedure("PRefused"),
        caller_of("PCaller", "PRefused"),
    ]);
    let out = std::env::temp_dir().join(format!("gx_refused_rebuild_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&out);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_skip_compile(true);

    let report = rebuild_all(&catalog, &out, &options).expect("rebuild completes");

    assert_eq!(
        report.status,
        RebuildStatus::Failed,
        "a refused object is an error"
    );
    assert!(
        report
            .refused_objects
            .iter()
            .any(|r| r.name.eq_ignore_ascii_case("PRefused")
                && r.reason.contains("refused by the reader")
                && r.reason.contains("type code 255")),
        "the refused procedure is named with its reason: {:?}",
        report.refused_objects
    );
    assert!(
        report
            .refused_objects
            .iter()
            .any(|r| r.name.eq_ignore_ascii_case("PCaller")
                && r.reason.contains("refused by the reader")),
        "the caller that needs it is refused by name: {:?}",
        report.refused_objects
    );
    assert!(
        !out.join("PRefused.cs").exists(),
        "a refused object is never emitted"
    );
}

#[test]
fn a_build_that_reaches_a_refused_object_is_refused_by_name() {
    let catalog = catalog_of(vec![
        refused_procedure("PRefused"),
        caller_of("PCaller", "PRefused"),
    ]);

    let err = project_from_catalog(&catalog, &ObjectId::new("PCaller").unwrap(), None)
        .expect_err("a build that needs a refused object is refused");
    assert!(err.contains("refused by the reader"), "{err}");
    assert!(err.to_ascii_uppercase().contains("PREFUSED"), "{err}");

    let err = project_from_catalog(&catalog, &ObjectId::new("PRefused").unwrap(), None)
        .expect_err("a build that targets a refused object is refused");
    assert!(err.contains("refused by the reader"), "{err}");
}

#[test]
fn data_provider_reads_declared_output_and_decodes_its_stored_variables() {
    let client = Arc::new(UdmClient::new_in_memory());
    let output_id = sdt(&client, "ProviderOutput");
    let mut dp = procedure_with_variables(
        "TypedProvider",
        &variables(&[declaration(
            "Page",
            Some(&typed("4")),
            &[("Length", "9"), ("Decimals", "0")],
        )]),
    );
    dp.kind = ObjectKind::DataProvider;
    let identity = UdmWriter::new(client.clone()).create_object(&dp).unwrap();
    client.insert_entity_version(EntityVersionRecord {
        entity_type_id: identity.entity_type_id, entity_id: identity.entity_id, entity_version_id: identity.version_id,
        data: gx_engine::udm::decoder::encode_blob("", true).unwrap(),
        properties: Some(format!("<Properties><Property><Name>OutputSDT</Name><Value>447527b5-9210-4523-898b-5dccb17be60a-{output_id}</Value></Property></Properties>")),
    }).unwrap();
    let object = UdmReader::new(client)
        .read_object_by_name("TypedProvider")
        .unwrap()
        .unwrap();
    assert_eq!(object.refusal, None);
    let output: gx_engine::csharp::data_provider::DataProviderOutput = serde_json::from_str(
        &object
            .source_parts
            .iter()
            .find(|p| p.name == "DP_OUTPUT")
            .unwrap()
            .source,
    )
    .unwrap();
    assert_eq!(output.name, "ProviderOutput");
    assert_eq!(
        output.data_type,
        DataType::Record {
            schema: "ProviderOutput".into()
        }
    );
    let held: BTreeMap<String, DataType> = serde_json::from_str(
        &object
            .source_parts
            .iter()
            .find(|p| p.name == "VARIABLES")
            .unwrap()
            .source,
    )
    .unwrap();
    assert_eq!(
        held["Page"],
        DataType::Numeric {
            precision: 9,
            scale: 0
        }
    );
    assert!(
        object
            .references
            .iter()
            .any(|reference| reference.kind == ReferenceKind::Uses)
    );
}

#[test]
fn data_provider_without_output_metadata_is_refused_instead_of_inferred_from_source() {
    let client = Arc::new(UdmClient::new_in_memory());
    let mut dp = procedure_with_variables("MissingOutputProvider", "<Variables/>");
    dp.kind = ObjectKind::DataProvider;
    UdmWriter::new(client.clone()).create_object(&dp).unwrap();
    let object = UdmReader::new(client)
        .read_object_by_name("MissingOutputProvider")
        .unwrap()
        .unwrap();
    assert!(object.refusal.as_deref().unwrap().starts_with("OUTPUT:"));
    assert!(!object.source_parts.iter().any(|p| p.name == "DP_OUTPUT"));
}
