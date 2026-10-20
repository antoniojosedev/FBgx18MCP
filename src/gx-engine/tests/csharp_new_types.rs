//! GUID, LONGVARCHAR, image (BITMAP) and geospatial types: their C# declarations and SDT members, and
//! the refusals for an expression form this build does not evidence. Each type is checked once in the
//! SDT emitter, once in a procedure and once in the table DDL mapping.

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::csharp::types::{to_csharp_default_value, to_csharp_type};
use gx_engine::csharp::{ProcedureClass, SdtClass};
use gx_engine::model::DataType;
use gx_engine::table::SqlDialect;

const VARIABLES: &str = r#"{
    "Uuid": {"kind": "guid"},
    "Message": {"kind": "long_varchar"},
    "Photo": {"kind": "image"},
    "Where": {"kind": "geospatial"}
}"#;

fn sdt_object(name: &str, structure_json: &str) -> KbObject {
    KbObject {
        id: ObjectId::new(format!("sdt-{name}")).expect("object id"),
        kind: ObjectKind::StructuredDataType,
        name: name.to_string(),
        source_parts: vec![SourcePart {
            name: "STRUCTURE".to_string(),
            source: structure_json.to_string(),
        }],
        references: Vec::new(),
        system_root: false,
        library: false,
        main: None,
        module_path: Vec::new(),
        refusal: None,
        not_generated: None,
    }
}

fn procedure(source: &str, variables: &str) -> ProcedureClass {
    let mut object = KbObject::new(
        ObjectId::new("new_type_proc").unwrap(),
        "NewTypeProc",
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: source.into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: variables.into(),
    });
    ProcedureClass::from_kb_object(&object).expect("the procedure parses")
}

#[test]
fn each_new_type_has_its_sdk_csharp_type_and_default() {
    assert_eq!(to_csharp_type(&DataType::LongVarchar {}), "string");
    assert_eq!(to_csharp_default_value(&DataType::LongVarchar {}), "\"\"");
    assert_eq!(to_csharp_type(&DataType::Image {}), "string");
    assert_eq!(to_csharp_default_value(&DataType::Image {}), "\"\"");
    assert_eq!(to_csharp_type(&DataType::Guid {}), "Guid");
    assert_eq!(to_csharp_default_value(&DataType::Guid {}), "Guid.Empty");
    assert_eq!(to_csharp_type(&DataType::Geospatial {}), "Geospatial");
    assert_eq!(
        to_csharp_default_value(&DataType::Geospatial {}),
        "new Geospatial(\"\")"
    );
}

#[test]
fn an_sdt_with_the_new_types_emits_their_members_and_the_image_companion() {
    let json = r#"{"fields":[
        {"node":"field","name":"UUID","data_type":{"kind":"guid"}},
        {"node":"field","name":"Message","data_type":{"kind":"long_varchar"}},
        {"node":"field","name":"Photo","data_type":{"kind":"image"}},
        {"node":"field","name":"Where","data_type":{"kind":"geospatial"}}
    ]}"#;
    let class = SdtClass::from_kb_object(&sdt_object("NewTypes", json)).expect("decodes");
    let code = class.emit();
    assert!(code.contains("public Guid gxTpr_Uuid"), "{code}");
    assert!(code.contains("public string gxTpr_Message"), "{code}");
    assert!(code.contains("public string gxTpr_Photo"), "{code}");
    assert!(code.contains("public Geospatial gxTpr_Where"), "{code}");
    // The SDK's `_gxi` companion of an image member (Properties.stg `add_gxi_property`): a public string
    // over a backing field initialised to "", whose setter marks the member dirty.
    assert!(code.contains("public string gxTpr_Photo_gxi"), "{code}");
    assert!(
        code.contains("gxTv_SdtNewTypes_Photo_gxi = value; SetDirty(\"Photo_gxi\")"),
        "{code}"
    );
    assert!(
        code.contains("protected string gxTv_SdtNewTypes_Photo_gxi = \"\";"),
        "{code}"
    );
    // Only the image member has a companion: exactly the property, its setter, the getter, the
    // SetDirty name and the backing field mention `_gxi`.
    assert_eq!(code.matches("_gxi").count(), 5, "{code}");
}

#[test]
fn a_longvarchar_variable_is_assigned_and_read_like_a_varchar() {
    let code = procedure("&Message = 'text'\n&Message = &Message\n", VARIABLES)
        .emit_checked()
        .expect("LONGVARCHAR is text in an expression");
    assert!(code.contains("private string AV_Message ;"), "{code}");
    assert!(code.contains("AV_Message = \"text\";"), "{code}");
    assert!(code.contains("AV_Message = AV_Message;"), "{code}");
}

#[test]
fn a_guid_image_or_geospatial_variable_read_or_written_is_refused_by_name() {
    for (source, name) in [
        ("&Uuid = &Uuid\n", "Uuid"),
        ("&Photo = &Photo\n", "Photo"),
        ("&Where = &Where\n", "Where"),
    ] {
        let error = procedure(source, VARIABLES)
            .emit_checked()
            .expect_err("no expression form is evidenced for this type");
        assert!(error.contains(name), "{name}: {error}");
        assert!(
            error.contains("no expression form") || error.contains("no assignment form"),
            "{error}"
        );
    }
}

#[test]
fn a_guid_read_into_a_text_variable_is_refused_by_name() {
    // The GUID is only read here: the assignment target is text, so the read itself is refused.
    let error = procedure("&Message = &Uuid\n", VARIABLES)
        .emit_checked()
        .expect_err("a GUID read has no evidenced expression form");
    assert!(error.contains("'Uuid'"), "{error}");
    assert!(error.contains("no expression form"), "{error}");
}

#[test]
fn a_new_type_column_has_no_evidenced_sql_type_in_any_dialect() {
    for dialect in [
        SqlDialect::SqlServer,
        SqlDialect::PostgreSql,
        SqlDialect::Oracle,
    ] {
        for data_type in [
            DataType::LongVarchar {},
            DataType::Guid {},
            DataType::Image {},
            DataType::Geospatial {},
        ] {
            let error = dialect
                .map_data_type(&data_type)
                .expect_err("no evidenced SQL column type");
            assert!(error.contains("no evidenced SQL column type"), "{error}");
        }
    }
}
