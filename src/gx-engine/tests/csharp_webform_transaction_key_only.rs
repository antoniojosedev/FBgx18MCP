//! The two KBTeste key-only Transactions with the default web form are emitted as the SDK wrote
//! them (`NetModel/Web/<name>.cs`, build stamps and carriage returns normalised): `Plan070Trn`,
//! whose key's `SuperTypeKey` names `Plan071TrnId` (subtype group `Plan071 Grp`), and
//! `Plan071Trn`, the supertype it names. A key-only Transaction outside those two roles is refused
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

macro_rules! fixture {
    ($file:literal) => {
        include_str!(concat!("fixtures/transaction_key_only_form/", $file))
    };
}

const SUB_STRUCTURE: &str = fixture!("plan070trn.structure.txt");
const SUPER_STRUCTURE: &str = fixture!("plan071trn.structure.txt");

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

fn transaction(name: &str, entity: i32, structure: &str, prompts: &[&str]) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(name.to_ascii_lowercase()).unwrap(),
        name,
        ObjectKind::Transaction,
    );
    for (part_name, source) in [
        ("STRUCTURE", structure),
        ("VARIABLES", fixture!("plan070trn.variables.txt")),
        ("EVENTS", ""),
        ("RULES", ""),
        ("WEBFORM", ""),
        (WEBFORM_DEFAULT_PART, "true"),
    ] {
        object.source_parts.push(part(part_name, source));
    }
    object.source_parts.push(settings(entity, name, None, None));
    for target in std::iter::once("masterunanimosidebar").chain(prompts.iter().copied()) {
        object.references.push(Reference {
            kind: ReferenceKind::Uses,
            target: ObjectId::new(target.to_ascii_lowercase()).unwrap(),
        });
    }
    object
}

fn subtype() -> KbObject {
    transaction("Plan070Trn", 5, SUB_STRUCTURE, &["Gx0040", "Gx0050"])
}

fn supertype() -> KbObject {
    transaction("Plan071Trn", 6, SUPER_STRUCTURE, &["Gx0050"])
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

fn prompt(name: &str, key: &str) -> KbObject {
    let mut prompt = KbObject::new(
        ObjectId::new(name.to_ascii_lowercase()).unwrap(),
        name,
        ObjectKind::WebPanel,
    );
    prompt
        .source_parts
        .push(part("RULES", &format!("parm(out:&p{key});\norder({key});")));
    prompt.source_parts.push(settings(99, name, Some(1), None));
    prompt
}

fn env() -> WebPanelEnvironment {
    let facts = |id| AttributeFacts {
        id,
        custom_title: false,
    };
    WebPanelEnvironment {
        theme: Some("KBTeste".into()),
        generator: Some("GeneXus .NET 18_0_10-184260".into()),
        master_classes: BTreeMap::from([(
            "MasterUnanimoSidebar".to_string(),
            "general.ui.masterunanimosidebar".to_string(),
        )]),
        build_number: Some("239440".into()),
        support_class: Some("// GxWebStd".into()),
        attributes: BTreeMap::from([
            ("Plan070TrnId".to_string(), facts(11)),
            ("Plan071TrnId".to_string(), facts(12)),
        ]),
        table_keys: BTreeMap::from([("Plan070Trn".to_string(), 4), ("Plan071Trn".to_string(), 5)]),
        subtype_groups: BTreeMap::from([("Plan070TrnId".to_string(), "Plan071 Grp".to_string())]),
        ..WebPanelEnvironment::default()
    }
}

fn plan_in(
    objects: Vec<KbObject>,
    target: &str,
    env: &WebPanelEnvironment,
) -> Result<WebClass, String> {
    let catalog = Catalog::from_objects_map(
        objects
            .into_iter()
            .chain([
                master(),
                prompt("Gx0040", "Plan070TrnId"),
                prompt("Gx0050", "Plan071TrnId"),
            ])
            .map(|o| (o.id.clone(), o))
            .collect(),
    );
    let id = ObjectId::new(target.to_ascii_lowercase()).unwrap();
    plan_transaction_form(catalog.get(&id).unwrap(), &catalog, env).map(WebClass::Transaction)
}

/// Drops carriage returns and replaces every `"?2026<digits>` build stamp with one token.
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

#[test]
fn the_kbteste_subtype_and_supertype_key_forms_match_the_sdk_output() {
    for (target, sdk) in [
        ("Plan070Trn", fixture!("plan070trn.cs")),
        ("Plan071Trn", fixture!("plan071trn.cs")),
    ] {
        let class = plan_in(vec![subtype(), supertype()], target, &env())
            .unwrap_or_else(|reason| panic!("{target} refused: {reason}"));
        assert_eq!(
            class.file_name(),
            format!("{}.cs", target.to_ascii_lowercase())
        );
        let emitted = class.emit("202610181732488");
        assert!(!emitted.contains("@@"), "{target}: placeholder left");
        assert_sdk_script(&class, target);
        assert_eq!(
            normalised(&emitted),
            normalised(sdk),
            "{target} differs from the SDK output"
        );
    }
}

#[test]
fn a_key_only_transaction_outside_the_two_roles_is_refused_by_name() {
    // No Transaction names Plan071Trn's key as its supertype.
    let reason = plan_in(vec![supertype()], "Plan071Trn", &env()).unwrap_err();
    assert!(
        reason.contains("exactly one other key-only Transaction"),
        "{reason}"
    );

    // The subtype's key belongs to no group the KB names.
    let mut no_group = env();
    no_group.subtype_groups.clear();
    let reason = plan_in(vec![subtype(), supertype()], "Plan070Trn", &no_group).unwrap_err();
    assert!(reason.contains("no single subtype group"), "{reason}");

    // The supertype is not key-only.
    let wide = SUPER_STRUCTURE.replace(
        "\"scale\":0}}]",
        "\"scale\":0}},{\"name\":\"Plan071TrnName\",\"is_key\":false,\"data_type\":{\"kind\":\"varchar\",\"length\":40}}]",
    );
    assert_ne!(wide, SUPER_STRUCTURE, "the fixture shape changed");
    let reason = plan_in(
        vec![subtype(), transaction("Plan071Trn", 6, &wide, &["Gx0050"])],
        "Plan070Trn",
        &env(),
    )
    .unwrap_err();
    assert!(reason.contains("is not a key-only Transaction"), "{reason}");

    // A key-only Transaction with events.
    let mut with_events = subtype();
    with_events.source_parts.retain(|p| p.name != "EVENTS");
    with_events
        .source_parts
        .push(part("EVENTS", "Event 'Ping'\n   msg('live')\nEndEvent\n"));
    let reason = plan_in(vec![with_events, supertype()], "Plan070Trn", &env()).unwrap_err();
    assert!(reason.contains("it has events"), "{reason}");
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
