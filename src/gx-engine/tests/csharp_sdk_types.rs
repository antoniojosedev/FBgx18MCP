//! Structured Data Types and enumerated Domains are written in the SDK's own form, byte for byte.
//!
//! Fixtures (tests/fixtures/sdk_form): `<sdt>.structure.json` is the `STRUCTURE` part `gx-engine kb-read
//! C:/KBs/KBTeste <sdt>` holds, `type_Sdt<class>.cs` the files GeneXus 18 wrote for that SDT in KBTeste's
//! NetModel/Web (UTF-8, CRLF), `page.domain.xml` the stored row of KBTeste's `General.Page` Domain and
//! `gxdomainpage.cs` the file the SDK wrote for it.

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::csharp::domain::enum_domain_file;
use gx_engine::csharp::literals::sdk_file_bytes;
use gx_engine::csharp::sdt::SdtClass;
use gx_engine::csharp::sdt_sdk::sdk_files;
use gx_engine::udm::reader::DOMAIN_ENUM_PART;
use std::path::Path;

const FIXTURES: &str = "tests/fixtures/sdk_form";

/// The SDTs with an SDK file per class, and the classes each writes.
const SDTS: [(&str, &[&str]); 7] = [
    ("RustFreshSdt091817", &["SdtRustFreshSdt091817"]),
    ("RST54161700", &["SdtRST54161700"]),
    ("LiveSdtMember60744600", &["SdtLiveSdtMember60744600"]),
    ("RSC79223900", &["SdtRSC79223900"]),
    ("RCE06829400", &["SdtRCE06829400", "SdtRCE06829400_Entry"]),
    (
        "RSN03944700",
        &[
            "SdtRSN03944700",
            "SdtRSN03944700_Child",
            "SdtRSN03944700_RowsItem",
        ],
    ),
    (
        "NativeDeepSdt85299100",
        &[
            "SdtNativeDeepSdt85299100",
            "SdtNativeDeepSdt85299100_Child",
            "SdtNativeDeepSdt85299100_Child_Grandchild",
            "SdtNativeDeepSdt85299100_Child_NestedCollectionItem",
        ],
    ),
];

fn sdt(name: &str, structure: &str) -> SdtClass {
    let mut object = KbObject::new(
        ObjectId::new(name).unwrap(),
        name,
        ObjectKind::StructuredDataType,
    );
    object.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: structure.to_string(),
    });
    SdtClass::from_kb_object(&object).expect("SDT class")
}

/// The fixture structure with the XML namespace the reader adds (`KBTeste`, the KB's name).
fn structure(name: &str) -> String {
    let raw = std::fs::read_to_string(Path::new(FIXTURES).join(format!("{name}.structure.json")))
        .expect("structure fixture");
    let mut json: serde_json::Value = serde_json::from_str(&raw).expect("structure JSON");
    json["xml_namespace"] = "KBTeste".into();
    json.to_string()
}

#[test]
fn each_class_of_an_sdt_is_the_sdks_file() {
    for (name, classes) in SDTS {
        let files = sdk_files(&sdt(name, &structure(name))).expect("an evidenced shape");
        let names: Vec<String> = files.iter().map(|(file, _)| file.clone()).collect();
        let expected: Vec<String> = classes.iter().map(|c| format!("type_{c}.cs")).collect();
        assert_eq!(names, expected, "{name}: one file per class, holder first");
        for (file, text) in files {
            let sdk = std::fs::read(Path::new(FIXTURES).join(&file)).expect("SDK file");
            assert!(
                sdk_file_bytes(&text) == sdk,
                "{file} differs from the SDK's bytes"
            );
        }
    }
}

#[test]
fn an_sdt_outside_the_evidence_is_refused_by_name() {
    let with = |fields: &str| {
        sdt(
            "Probe",
            &format!(r#"{{"fields":{fields},"xml_namespace":"KBTeste"}}"#),
        )
    };
    let decimal = with(
        r#"[{"node":"field","name":"Price","data_type":{"kind":"numeric","precision":8,"scale":2}}]"#,
    );
    assert!(sdk_files(&decimal).unwrap_err().contains("Price"));
    let after_level = with(
        r#"[{"node":"level","name":"Child","collection":false,"item_name":null,"children":[{"node":"field","name":"A","data_type":{"kind":"boolean"}}]},{"node":"field","name":"Late","data_type":{"kind":"boolean"}}]"#,
    );
    assert!(sdk_files(&after_level).unwrap_err().contains("Late"));
    let no_namespace = sdt(
        "Probe",
        r#"{"fields":[{"node":"field","name":"A","data_type":{"kind":"boolean"}}]}"#,
    );
    assert!(sdk_files(&no_namespace).unwrap_err().contains("namespace"));
    let root_collection = sdt(
        "Probe",
        r#"{"fields":[{"node":"field","name":"A","data_type":{"kind":"boolean"}}],"root_collection":"Item","xml_namespace":"KBTeste"}"#,
    );
    assert!(
        sdk_files(&root_collection)
            .unwrap_err()
            .contains("collection root")
    );
}

fn domain(properties: &str) -> KbObject {
    let mut object = KbObject::new(ObjectId::new("Page").unwrap(), "Page", ObjectKind::Domain);
    object.module_path = vec!["General".into()];
    object.source_parts.push(SourcePart {
        name: DOMAIN_ENUM_PART.into(),
        source: properties.to_string(),
    });
    object
}

#[test]
fn an_enumerated_domain_is_the_sdks_values_class() {
    let row = std::fs::read_to_string(Path::new(FIXTURES).join("page.domain.xml")).unwrap();
    let (file, text) = enum_domain_file(&domain(row.trim()))
        .expect("an enumeration part")
        .expect("an evidenced shape");
    assert_eq!(file, "gxdomainpage.cs");
    let sdk = std::fs::read(Path::new(FIXTURES).join("gxdomainpage.cs")).unwrap();
    assert!(
        sdk_file_bytes(&text) == sdk,
        "gxdomainpage.cs differs from the SDK's bytes"
    );

    // A key wider than `short` or with decimals is not evidenced: refused by name, never guessed.
    let wide = row.replace(
        "<Name>Length</Name><Value>4</Value>",
        "<Name>Length</Name><Value>9</Value>",
    );
    assert!(
        enum_domain_file(&domain(wide.trim()))
            .unwrap()
            .unwrap_err()
            .contains("Page")
    );
    let decimals = row.replace(
        "<Name>Decimals</Name><Value>0</Value>",
        "<Name>Decimals</Name><Value>2</Value>",
    );
    assert!(enum_domain_file(&domain(decimals.trim())).unwrap().is_err());

    // A Domain without the part (not an enumeration, or a library interface) has no values class.
    let plain = KbObject::new(ObjectId::new("Plain").unwrap(), "Plain", ObjectKind::Domain);
    assert!(enum_domain_file(&plain).is_none());
}

/// A prompt scope fixture (`<prompt>.prompt_scope.json`: the scope the planner built for that KBTeste prompt,
/// dumped as JSON) as the template fields the script reads.
fn prompt_scope(stem: &str) -> gx_engine::csharp::template::Fields {
    use gx_engine::csharp::template::{Fields, Value};
    fn value(json: &serde_json::Value) -> Value {
        match json {
            serde_json::Value::String(text) => Value::Text(text.clone()),
            serde_json::Value::Bool(flag) => Value::Flag(*flag),
            serde_json::Value::Array(items) => Value::List(items.iter().map(fields).collect()),
            other => panic!("unexpected scope value {other}"),
        }
    }
    fn fields(json: &serde_json::Value) -> Fields {
        json.as_object()
            .expect("an object")
            .iter()
            .map(|(key, item)| (key.clone(), value(item)))
            .collect()
    }
    let raw = std::fs::read(Path::new(FIXTURES).join(format!("{stem}.prompt_scope.json"))).unwrap();
    fields(&serde_json::from_slice(&raw).expect("scope JSON"))
}

#[test]
fn a_selection_prompts_client_script_is_the_sdks_bytes() {
    // Three filters (numeric and VarChar), one filter, and Character filters.
    for stem in ["gx0010", "gx0040", "gx0060"] {
        let script =
            gx_engine::csharp::prompt_js::prompt_script(&prompt_scope(stem)).expect("script");
        let sdk = std::fs::read(Path::new(FIXTURES).join(format!("{stem}.js"))).unwrap();
        assert!(
            script.as_bytes() == sdk.as_slice(),
            "{stem}.js differs from the SDK's bytes"
        );
    }
}

/// SYNTHETIC: the settings `gxcfg.js` reads from a model's `GXCFG.WEB`, with KBTeste's values (its other
/// settings, credentials included, are left out).
const MODEL_SETTINGS: &str = r#"<?xml version="1.0" encoding="utf-8"?>
<configuration><appSettings>
<add key="DateFormat" value="MDY" />
<add key="YearLimit" value="1940" />
<add key="TimeAmPmFormat" value="12" />
<add key="StorageTimeZone" value="1" />
<add key="LANGUAGE" value="eng" />
<add key="DECIMAL_POINT" value="." />
<add key="GX_BUILD_NUMBER" value="239440" />
<add key="DocumentType" value="HTML5" />
</appSettings></configuration>"#;

#[test]
fn the_models_client_configuration_is_the_sdks_bytes() {
    let script = gx_engine::csharp::webform::client_config_script(MODEL_SETTINGS).unwrap();
    let sdk = std::fs::read(Path::new(FIXTURES).join("gxcfg.js")).unwrap();
    assert!(
        script.as_bytes() == sdk.as_slice(),
        "gxcfg.js differs from the SDK's bytes"
    );
}

#[test]
fn a_model_setting_absent_or_unmatched_refuses_the_client_configuration_by_name() {
    let missing = MODEL_SETTINGS.replace("<add key=\"YearLimit\" value=\"1940\" />", "");
    let reason = gx_engine::csharp::webform::client_config_script(&missing).unwrap_err();
    assert!(reason.contains("YearLimit"), "{reason}");
    let comma = MODEL_SETTINGS.replace("value=\".\"", "value=\",\"");
    let reason = gx_engine::csharp::webform::client_config_script(&comma).unwrap_err();
    assert!(reason.contains("DECIMAL_POINT=','"), "{reason}");
}
