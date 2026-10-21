//! The three KBTeste Transactions with a user-laid-out form (`LiveMoveAttribute`,
//! `LiveMoveAttributeRules`, `LiveMoveAttributeRules2`) are emitted byte for byte as the SDK wrote
//! them (`NetModel/Web/<name>.cs`, build stamps normalised): every attribute shown in structure
//! order, shown in another order, and one attribute hidden. A Transaction outside that shape is
//! refused by name.

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

const CLASS_TYPE: &str = "d52f9833-90c9-4096-8e42-27485741f78a";

struct Case {
    name: &'static str,
    entity: i32,
    first_attribute: u32,
    table_key: u32,
    prompt: &'static str,
    structure: &'static str,
    variables: &'static str,
    events: &'static str,
    webform: &'static str,
    sdk: &'static str,
}

macro_rules! fixture {
    ($file:literal) => {
        include_str!(concat!("fixtures/transaction_form/", $file))
    };
}

const CASES: [Case; 3] = [
    Case {
        name: "LiveMoveAttribute",
        entity: 7,
        first_attribute: 27,
        table_key: 6,
        prompt: "Gx0060",
        structure: fixture!("livemoveattribute.structure.txt"),
        variables: fixture!("livemoveattribute.variables.txt"),
        events: fixture!("livemoveattribute.events.txt"),
        webform: fixture!("livemoveattribute.webform.txt"),
        sdk: fixture!("livemoveattribute.cs"),
    },
    Case {
        name: "LiveMoveAttributeRules",
        entity: 8,
        first_attribute: 30,
        table_key: 7,
        prompt: "Gx0070",
        structure: fixture!("livemoveattributerules.structure.txt"),
        variables: fixture!("livemoveattributerules.variables.txt"),
        events: fixture!("livemoveattributerules.events.txt"),
        webform: fixture!("livemoveattributerules.webform.txt"),
        sdk: fixture!("livemoveattributerules.cs"),
    },
    Case {
        name: "LiveMoveAttributeRules2",
        entity: 9,
        first_attribute: 33,
        table_key: 8,
        prompt: "Gx0080",
        structure: fixture!("livemoveattributerules2.structure.txt"),
        variables: fixture!("livemoveattributerules2.variables.txt"),
        events: fixture!("livemoveattributerules2.events.txt"),
        webform: fixture!("livemoveattributerules2.webform.txt"),
        sdk: fixture!("livemoveattributerules2.cs"),
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
        ("EVENTS", case.events),
        ("WEBFORM", case.webform),
        ("RULES", ""),
        (WEBFORM_DEFAULT_PART, "false"),
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

fn prompt(case: &Case, key: &str) -> KbObject {
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
    let classes = [
        (80, "form__actions--fixed"),
        (81, "form__cell"),
        (83, "form__toolbar-cell"),
        (84, "form-container"),
        (119, "heading-01"),
        (125, "ico__arrow-first"),
        (126, "ico__arrow-last"),
        (127, "ico__arrow-next"),
        (128, "ico__arrow-prev"),
        (216, "title-container"),
        (262, "button"),
        (263, "button-primary"),
        (268, "button-secondary"),
        (270, "button-tertiary"),
        (299, "button-auxiliary"),
        (450, "form__cell-advanced"),
    ];
    let mut attributes = BTreeMap::new();
    let mut table_keys = BTreeMap::new();
    for case in &CASES {
        for (offset, suffix) in ["Id", "Code", "Description"].iter().enumerate() {
            attributes.insert(
                format!("{}{suffix}", case.name),
                AttributeFacts {
                    id: case.first_attribute + offset as u32,
                    custom_title: false,
                },
            );
        }
        table_keys.insert(case.name.to_string(), case.table_key);
    }
    // Rules2 stores Description before Code (ids 34 and 35).
    attributes
        .get_mut("LiveMoveAttributeRules2Description")
        .unwrap()
        .id = 34;
    attributes
        .get_mut("LiveMoveAttributeRules2Code")
        .unwrap()
        .id = 35;
    WebPanelEnvironment {
        theme: Some("KBTeste".into()),
        generator: Some("GeneXus .NET 18_0_10-184260".into()),
        master_classes: BTreeMap::from([(
            "MasterUnanimoSidebar".to_string(),
            "general.ui.masterunanimosidebar".to_string(),
        )]),
        build_number: Some("239440".into()),
        support_class: Some("// GxWebStd".into()),
        design_classes: classes
            .iter()
            .map(|(id, name)| (format!("{CLASS_TYPE}-{id}"), name.to_string()))
            .collect(),
        attributes,
        table_keys,
        ..WebPanelEnvironment::default()
    }
}

fn plan_with(
    object: &KbObject,
    others: Vec<KbObject>,
    env: &WebPanelEnvironment,
) -> Result<WebClass, String> {
    let catalog = Catalog::from_objects_map(
        others
            .into_iter()
            .chain([object.clone()])
            .map(|o| (o.id.clone(), o))
            .collect(),
    );
    plan_transaction_form(catalog.get(&object.id).unwrap(), &catalog, env)
        .map(WebClass::Transaction)
}

fn plan(case: &Case, object: &KbObject) -> Result<WebClass, String> {
    plan_with(
        object,
        vec![master(), prompt(case, &format!("{}Id", case.name))],
        &env(),
    )
}

/// Replaces every `"?2026<digits>` build stamp with one token. The SDK reads its clock per reference.
fn without_stamps(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut rest = text;
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
fn the_kbteste_custom_form_transactions_match_the_sdk_output() {
    for case in &CASES {
        let class = plan(case, &transaction(case))
            .unwrap_or_else(|reason| panic!("{} refused: {reason}", case.name));
        assert_eq!(
            class.file_name(),
            format!("{}.cs", case.name.to_ascii_lowercase())
        );
        let emitted = class.emit("202610150040586");
        assert_sdk_script(&class, case.name);
        assert!(!emitted.contains("@@"), "{}: placeholder left", case.name);
        assert_eq!(
            without_stamps(&emitted),
            without_stamps(case.sdk),
            "{} differs from the SDK output",
            case.name
        );
    }
}

#[test]
fn a_transaction_outside_the_matched_shape_is_refused_by_name() {
    let case = &CASES[1];
    let base = transaction(case);
    let refused = |object: &KbObject, wanted: &str| {
        let reason = plan(case, object).unwrap_err();
        assert!(reason.contains(wanted), "{reason}");
    };

    refused(
        &with_part(&base, WEBFORM_DEFAULT_PART, "true"),
        "default form",
    );
    let mut unread = base.clone();
    unread
        .source_parts
        .retain(|p| p.name != WEBFORM_DEFAULT_PART);
    refused(&unread, "IsDefault property was not read");
    refused(&with_part(&base, "RULES", "error('x');"), "it has rules");
    refused(
        &with_part(&base, "EVENTS", "Event Start\n  &Pgmname = 'x'\nEndEvent\n"),
        "its events are not only user events",
    );
    refused(
        &with_part(
            &base,
            "STRUCTURE",
            &case.structure.replace("\"length\":20", "\"length\":40"),
        ),
        "which the transaction emitter is not matched for",
    );
    refused(
        &with_part(
            &base,
            "WEBFORM",
            &case
                .webform
                .replace(&format!("{CLASS_TYPE}-81"), &format!("{CLASS_TYPE}-80")),
        ),
        "its layout is not the design-system Transaction form",
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

#[test]
fn a_transaction_another_one_references_or_an_attribute_with_its_own_title_is_refused() {
    let case = &CASES[1];
    let base = transaction(case);
    let key = format!("{}Id", case.name);

    let mut other = KbObject::new(
        ObjectId::new("other").unwrap(),
        "Other",
        ObjectKind::Transaction,
    );
    other.source_parts.push(part(
        "STRUCTURE",
        &format!("{{\"root\":{{\"name\":\"Other\",\"attributes\":[{{\"name\":\"OtherId\",\"is_key\":true,\"data_type\":{{\"kind\":\"numeric\",\"precision\":4,\"scale\":0}}}},{{\"name\":\"{key}\",\"is_key\":false,\"data_type\":{{\"kind\":\"numeric\",\"precision\":4,\"scale\":0}}}}]}}}}"),
    ));
    let reason = plan_with(&base, vec![master(), prompt(case, &key), other], &env()).unwrap_err();
    assert!(
        reason.contains("Transaction 'Other' also holds attribute"),
        "{reason}"
    );

    let mut titled = env();
    titled.attributes.get_mut(&key).unwrap().custom_title = true;
    let reason = plan_with(&base, vec![master(), prompt(case, &key)], &titled).unwrap_err();
    assert!(reason.contains("stores a title of its own"), "{reason}");

    let reason = plan_with(
        &base,
        vec![master(), prompt(case, "SomethingElseId")],
        &env(),
    )
    .unwrap_err();
    assert!(reason.contains("does not return"), "{reason}");

    let mut no_key = env();
    no_key.table_keys.clear();
    let reason = plan_with(&base, vec![master(), prompt(case, &key)], &no_key).unwrap_err();
    assert!(reason.contains("no single stored TablePK"), "{reason}");
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
