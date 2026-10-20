//! The two KBTeste Transactions with the default web form (`TrnGroupProbeBase`,
//! `TrnGroupProbeSub`) are emitted as the SDK wrote them (`NetModel/Web/<name>.cs`, build stamps
//! normalised): a `Numeric(4)` key, a `Numeric(8)` and a `VarChar(40)` attribute, laid out from the
//! structure rather than the stored form. A default-form Transaction outside that shape is refused
//! by name.

#![forbid(unsafe_code)]

use gx_engine::catalog::{
    Catalog, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::csharp::transaction_form::plan_transaction_form;
use gx_engine::csharp::webform::{WebClass, WebPanelEnvironment};
use gx_engine::udm::client::AttributeFacts;
use gx_engine::udm::reader::WEBFORM_DEFAULT_PART;
use gx_engine::webpanel_settings::WebPanelSettings;
use std::collections::BTreeMap;

struct Case {
    name: &'static str,
    entity: i32,
    /// Key, Numeric(8) and VarChar(40) attribute names with their KB ids.
    attributes: [(&'static str, u32); 3],
    table_key: u32,
    prompt: &'static str,
    structure: &'static str,
    variables: &'static str,
    webform: &'static str,
    sdk: &'static str,
}

macro_rules! fixture {
    ($file:literal) => {
        include_str!(concat!("fixtures/transaction_default_form/", $file))
    };
}

const CASES: [Case; 2] = [
    Case {
        name: "TrnGroupProbeBase",
        entity: 2,
        attributes: [
            ("TrnGroupProbeBaseId", 4),
            ("GpBaseId", 6),
            ("GpBaseDesc", 7),
        ],
        table_key: 1,
        prompt: "Gx0010",
        structure: fixture!("trngroupprobebase.structure.txt"),
        variables: fixture!("trngroupprobebase.variables.txt"),
        webform: fixture!("trngroupprobebase.webform.txt"),
        sdk: fixture!("trngroupprobebase.cs"),
    },
    Case {
        name: "TrnGroupProbeSub",
        entity: 3,
        attributes: [("TrnGroupProbeSubId", 5), ("GpSubId", 8), ("GpSubDesc", 9)],
        table_key: 2,
        prompt: "Gx0020",
        structure: fixture!("trngroupprobesub.structure.txt"),
        variables: fixture!("trngroupprobesub.variables.txt"),
        webform: fixture!("trngroupprobesub.webform.txt"),
        sdk: fixture!("trngroupprobesub.cs"),
    },
];

fn part(name: &str, source: &str) -> SourcePart {
    SourcePart {
        name: name.into(),
        source: source.into(),
    }
}

fn settings(
    entity_id: i32,
    name: &str,
    master_page_id: Option<i32>,
    web_comp: Option<&str>,
) -> SourcePart {
    WebPanelSettings {
        entity_id,
        declared_name: name.into(),
        description: None,
        master_page_id,
        web_comp: web_comp.map(str::to_string),
    }
    .to_part()
    .unwrap()
}

fn transaction(case: &Case) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(case.name.to_ascii_lowercase()).unwrap(),
        case.name,
        ObjectKind::Transaction,
    );
    for (name, source) in [
        ("STRUCTURE", case.structure),
        ("VARIABLES", case.variables),
        ("EVENTS", ""),
        ("WEBFORM", case.webform),
        ("RULES", ""),
        (WEBFORM_DEFAULT_PART, "true"),
    ] {
        object.source_parts.push(part(name, source));
    }
    object
        .source_parts
        .push(settings(case.entity, case.name, None, None));
    for target in ["masterunanimosidebar", &case.prompt.to_ascii_lowercase()] {
        object.references.push(Reference {
            kind: ReferenceKind::Uses,
            target: ObjectId::new(target).unwrap(),
        });
    }
    object
}

fn master() -> KbObject {
    let mut master = KbObject::new(
        ObjectId::new("masterunanimosidebar").unwrap(),
        "MasterUnanimoSidebar",
        ObjectKind::WebPanel,
    );
    master.module_path = vec!["General".into(), "UI".into()];
    master
        .source_parts
        .push(settings(1, "MasterUnanimoSidebar", None, Some("Master")));
    master
}

fn prompt(case: &Case) -> KbObject {
    let key = case.attributes[0].0;
    let mut prompt = KbObject::new(
        ObjectId::new(case.prompt.to_ascii_lowercase()).unwrap(),
        case.prompt,
        ObjectKind::WebPanel,
    );
    prompt
        .source_parts
        .push(part("RULES", &format!("parm(out:&p{key});\norder({key});")));
    prompt
        .source_parts
        .push(settings(99, case.prompt, Some(1), None));
    prompt
}

fn env() -> WebPanelEnvironment {
    let mut attributes = BTreeMap::new();
    let mut table_keys = BTreeMap::new();
    for case in &CASES {
        for (name, id) in case.attributes {
            attributes.insert(
                name.to_string(),
                AttributeFacts {
                    id,
                    custom_title: false,
                },
            );
        }
        table_keys.insert(case.name.to_string(), case.table_key);
    }
    WebPanelEnvironment {
        theme: Some("KBTeste".into()),
        generator: Some("GeneXus .NET 18_0_10-184260".into()),
        master_classes: BTreeMap::from([(
            "MasterUnanimoSidebar".to_string(),
            "general.ui.masterunanimosidebar".to_string(),
        )]),
        build_number: Some("239440".into()),
        support_class: Some("// GxWebStd".into()),
        attributes,
        table_keys,
        ..WebPanelEnvironment::default()
    }
}

fn plan(case: &Case, object: &KbObject) -> Result<WebClass, String> {
    let catalog = Catalog::from_objects_map(
        [master(), prompt(case), object.clone()]
            .into_iter()
            .map(|o| (o.id.clone(), o))
            .collect(),
    );
    plan_transaction_form(catalog.get(&object.id).unwrap(), &catalog, &env())
        .map(WebClass::Transaction)
}

/// Replaces every `"?2026<digits>` build stamp with one token and drops carriage returns: the SDK
/// reads its clock per reference and writes CRLF, the fixtures are stored LF.
fn normalised(text: &str) -> String {
    let text = text.replace('\r', "");
    let mut out = String::with_capacity(text.len());
    let mut rest = text.as_str();
    while let Some(at) = rest.find("\"?2026") {
        out.push_str(&rest[..at]);
        let tail = &rest[at + 2..];
        let digits = tail.chars().take_while(char::is_ascii_digit).count();
        out.push_str("\"?STAMP");
        rest = &tail[digits..];
    }
    out.push_str(rest);
    out
}

fn with_part(object: &KbObject, name: &str, source: &str) -> KbObject {
    let mut changed = object.clone();
    changed.source_parts.retain(|p| p.name != name);
    changed.source_parts.push(part(name, source));
    changed
}

#[test]
fn the_kbteste_default_form_transactions_match_the_sdk_output() {
    for case in &CASES {
        let class = plan(case, &transaction(case))
            .unwrap_or_else(|reason| panic!("{} refused: {reason}", case.name));
        assert_eq!(
            class.file_name(),
            format!("{}.cs", case.name.to_ascii_lowercase())
        );
        let emitted = class.emit("202610181732488");
        assert_sdk_script(&class, case.name);
        assert!(!emitted.contains("@@"), "{}: placeholder left", case.name);
        assert_eq!(
            normalised(&emitted),
            normalised(case.sdk),
            "{} differs from the SDK output",
            case.name
        );
    }
}

#[test]
fn a_default_form_transaction_outside_the_matched_shape_is_refused_by_name() {
    let case = &CASES[0];
    let base = transaction(case);
    let refused = |object: &KbObject, wanted: &str| {
        let reason = plan(case, object).unwrap_err();
        assert!(reason.contains(wanted), "{reason}");
    };

    refused(
        &with_part(
            &base,
            "STRUCTURE",
            &case.structure.replace("\"length\":40", "\"length\":41"),
        ),
        "its default form is matched only for",
    );
    refused(
        &with_part(
            &base,
            "STRUCTURE",
            &case.structure.replace("\"precision\":8", "\"precision\":9"),
        ),
        "its default form is matched only for",
    );
    refused(
        &with_part(&base, "EVENTS", "Event 'Ping'\n   msg('live')\nEndEvent\n"),
        "it has events, which the default-form emitter is not matched for",
    );
    refused(&with_part(&base, "RULES", "error('x');"), "it has rules");
    refused(
        &with_part(&base, WEBFORM_DEFAULT_PART, "maybe"),
        "neither True nor False",
    );
    refused(
        &with_part(
            &base,
            "VARIABLES",
            &case.variables.replace("Name=\"Mode\"", "Name=\"Other\""),
        ),
        "standard Transaction variables",
    );
}

/// The client script the class writes is the SDK's, byte for byte (`tests/fixtures/sdk_form/<name>.js`,
/// copied from KBTeste `NetModel/Web` and stored unconverted).
fn assert_sdk_script(class: &WebClass, name: &str) {
    let (file_name, script) = class
        .script()
        .unwrap_or_else(|reason| panic!("{name}: script refused: {reason}"));
    assert_eq!(file_name, format!("{}.js", name.to_ascii_lowercase()));
    let sdk = std::fs::read(format!(
        "{}/tests/fixtures/sdk_form/{file_name}",
        env!("CARGO_MANIFEST_DIR")
    ))
    .unwrap();
    assert!(
        script.as_bytes() == sdk.as_slice(),
        "{name}: client script differs from the SDK's bytes"
    );
}
