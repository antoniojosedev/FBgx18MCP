//! The KBTeste Work With instance on `Trn353` is emitted as the SDK wrote it
//! (`NetModel/Web/<name>.cs`, build stamps and carriage returns normalised): the Transaction, its
//! selection panel `WWTrn353`, its view `ViewTrn353` and the view's General tab `Trn353General`.
//! An instance any of whose objects differs from what the pattern writes is refused by name.

#![forbid(unsafe_code)]

use gx_engine::catalog::{
    Catalog, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::csharp::transaction_form::plan_transaction_form;
use gx_engine::csharp::webform::{WebClass, WebPanelEnvironment, plan_web_class};
use gx_engine::csharp::work_with::plan_work_with;
use gx_engine::udm::client::AttributeFacts;
use gx_engine::udm::reader::WEBFORM_DEFAULT_PART;
use gx_engine::webpanel_settings::WebPanelSettings;
use std::collections::BTreeMap;

macro_rules! fixture {
    ($file:literal) => {
        include_str!(concat!("fixtures/work_with/", $file))
    };
}

fn part(name: &str, source: &str) -> SourcePart {
    SourcePart {
        name: name.into(),
        source: source.into(),
    }
}

fn settings(
    entity_id: i32,
    name: &str,
    description: Option<&str>,
    web_comp: Option<&str>,
) -> SourcePart {
    WebPanelSettings {
        entity_id,
        declared_name: name.into(),
        description: description.map(str::to_string),
        master_page_id: None,
        web_comp: web_comp.map(str::to_string),
    }
    .to_part()
    .unwrap()
}

fn object(name: &str, kind: ObjectKind, module: &[&str]) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(name.to_ascii_lowercase()).unwrap(),
        name,
        kind,
    );
    object.module_path = module.iter().map(|m| m.to_string()).collect();
    object
}

fn uses_master(object: &mut KbObject) {
    object.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new("masterunanimosidebar").unwrap(),
    });
}

fn transaction() -> KbObject {
    let mut trn = object("Trn353", ObjectKind::Transaction, &[]);
    for (name, source) in [
        ("STRUCTURE", fixture!("trn353.structure.txt")),
        ("RULES", fixture!("trn353.rules.txt")),
        ("EVENTS", fixture!("trn353.events.txt")),
        ("VARIABLES", fixture!("trn353.variables.txt")),
        ("WEBFORM", fixture!("trn353.webform.txt")),
        (WEBFORM_DEFAULT_PART, "true"),
    ] {
        trn.source_parts.push(part(name, source));
    }
    trn.source_parts.push(settings(40, "Trn353", None, None));
    uses_master(&mut trn);
    trn
}

fn panel(
    name: &str,
    entity: i32,
    description: Option<&str>,
    web_comp: Option<&str>,
    parts: [&str; 5],
) -> KbObject {
    let mut panel = object(name, ObjectKind::WebPanel, &[]);
    for (part_name, source) in ["CONDITIONS", "RULES", "EVENTS", "VARIABLES", "WEBFORM"]
        .into_iter()
        .zip(parts)
    {
        panel.source_parts.push(part(part_name, source));
    }
    panel
        .source_parts
        .push(settings(entity, name, description, web_comp));
    if web_comp.is_none() {
        uses_master(&mut panel);
    }
    panel
}

fn selection() -> KbObject {
    panel(
        "WWTrn353",
        66,
        Some("Trn353s"),
        None,
        [
            fixture!("wwtrn353.conditions.txt"),
            fixture!("wwtrn353.rules.txt"),
            fixture!("wwtrn353.events.txt"),
            fixture!("wwtrn353.variables.txt"),
            fixture!("wwtrn353.webform.txt"),
        ],
    )
}

fn view() -> KbObject {
    panel(
        "ViewTrn353",
        65,
        None,
        None,
        [
            fixture!("viewtrn353.conditions.txt"),
            fixture!("viewtrn353.rules.txt"),
            fixture!("viewtrn353.events.txt"),
            fixture!("viewtrn353.variables.txt"),
            fixture!("viewtrn353.webform.txt"),
        ],
    )
}

fn general() -> KbObject {
    panel(
        "Trn353General",
        67,
        None,
        Some("Yes"),
        [
            fixture!("trn353general.conditions.txt"),
            fixture!("trn353general.rules.txt"),
            fixture!("trn353general.events.txt"),
            fixture!("trn353general.variables.txt"),
            fixture!("trn353general.webform.txt"),
        ],
    )
}

/// The objects every instance class calls: the master page, the security module's two objects
/// and the transaction-context SDT.
fn library() -> Vec<KbObject> {
    let mut master = object(
        "MasterUnanimoSidebar",
        ObjectKind::WebPanel,
        &["General", "UI"],
    );
    master
        .source_parts
        .push(settings(1, "MasterUnanimoSidebar", None, Some("Master")));
    let mut not_authorized = object(
        "NotAuthorized",
        ObjectKind::WebPanel,
        &["General", "Security"],
    );
    not_authorized
        .source_parts
        .push(settings(2, "NotAuthorized", None, None));
    vec![
        master,
        object(
            "IsAuthorized",
            ObjectKind::Procedure,
            &["General", "Security"],
        ),
        not_authorized,
        object(
            "TransactionContext",
            ObjectKind::StructuredDataType,
            &["General", "UI"],
        ),
    ]
}

fn instance() -> Vec<KbObject> {
    vec![transaction(), selection(), view(), general()]
}

fn env() -> WebPanelEnvironment {
    let mut env = WebPanelEnvironment {
        theme: Some("KBTeste".into()),
        generator: Some("GeneXus .NET 18_0_10-184260".into()),
        master_classes: BTreeMap::from([(
            "MasterUnanimoSidebar".to_string(),
            "general.ui.masterunanimosidebar".to_string(),
        )]),
        build_number: Some("239440".into()),
        support_class: Some("// GxWebStd".into()),
        attributes: BTreeMap::from([(
            "Trn353Id".to_string(),
            AttributeFacts {
                id: 61,
                custom_title: false,
            },
        )]),
        table_keys: BTreeMap::from([("Trn353".to_string(), 9)]),
        ..WebPanelEnvironment::default()
    };
    for line in fixture!("environment.txt").lines() {
        let [kind, key, value] = line.split('\t').collect::<Vec<_>>()[..] else {
            panic!("bad environment line {line}");
        };
        let map = match kind {
            "class" => &mut env.design_classes,
            "guid" => &mut env.object_guids,
            other => panic!("unknown environment kind {other}"),
        };
        map.insert(key.to_string(), value.to_string());
    }
    env
}

/// Plans `target` the way rebuild-all does: a Transaction through the Transaction planners, a
/// WebPanel through `plan_web_class`.
fn plan_in(objects: Vec<KbObject>, target: &str) -> Result<WebClass, String> {
    let catalog = Catalog::from_objects_map(
        objects
            .into_iter()
            .chain(library())
            .map(|o| (o.id.clone(), o))
            .collect(),
    );
    let object = catalog
        .get(&ObjectId::new(target.to_ascii_lowercase()).unwrap())
        .unwrap();
    let env = env();
    if object.kind == ObjectKind::Transaction {
        if let Ok(form) = plan_transaction_form(object, &catalog, &env) {
            return Ok(WebClass::Transaction(form));
        }
        return plan_work_with(object, &catalog, &env).map(WebClass::WorkWith);
    }
    plan_web_class(object, &catalog, &env)
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

const TARGETS: [(&str, &str); 4] = [
    ("Trn353", fixture!("trn353.cs")),
    ("WWTrn353", fixture!("wwtrn353.cs")),
    ("ViewTrn353", fixture!("viewtrn353.cs")),
    ("Trn353General", fixture!("trn353general.cs")),
];

#[test]
fn the_kbteste_work_with_instance_matches_the_sdk_output() {
    for (target, sdk) in TARGETS {
        let class = plan_in(instance(), target)
            .unwrap_or_else(|reason| panic!("{target} refused: {reason}"));
        assert!(
            matches!(class, WebClass::WorkWith(_)),
            "{target} not a Work With class"
        );
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

fn refused(objects: Vec<KbObject>, target: &str) -> String {
    match plan_in(objects, target) {
        Ok(_) => panic!("{target} was planned"),
        Err(reason) => reason,
    }
}

#[test]
fn an_edited_object_refuses_the_whole_instance_by_name() {
    let mut objects = instance();
    let ww = &mut objects[1];
    let events = ww
        .source_parts
        .iter_mut()
        .find(|p| p.name == "EVENTS")
        .unwrap();
    events.source = events.source.replace("Grid.Rows = 10", "Grid.Rows = 20");
    for (target, _) in TARGETS {
        let reason = refused(objects.clone(), target);
        assert!(
            reason.contains("'WWTrn353' EVENTS is not what the Work With pattern writes"),
            "{target}: {reason}"
        );
    }
}

#[test]
fn a_missing_panel_refuses_the_instance() {
    let objects: Vec<KbObject> = instance()
        .into_iter()
        .filter(|o| o.name != "ViewTrn353")
        .collect();
    let reason = refused(objects, "Trn353");
    assert!(reason.contains("has no WebPanel 'ViewTrn353'"), "{reason}");
}

#[test]
fn a_key_held_by_another_transaction_is_refused() {
    let mut other = object("Trn353Child", ObjectKind::Transaction, &[]);
    other.source_parts.push(part(
        "STRUCTURE",
        r#"{"root":{"name":"Trn353Child","attributes":[{"name":"Trn353ChildId","is_key":true},{"name":"Trn353Id"}]}}"#,
    ));
    let mut objects = instance();
    objects.push(other);
    let reason = refused(objects, "WWTrn353");
    assert!(
        reason.contains("Transaction 'Trn353Child' also holds 'Trn353Id'"),
        "{reason}"
    );
}

#[test]
fn a_renamed_description_or_a_changed_key_is_refused() {
    let mut objects = instance();
    let ww = &mut objects[1];
    ww.source_parts
        .retain(|p| p.name != gx_engine::webpanel_settings::SETTINGS_PART);
    ww.source_parts
        .push(settings(66, "WWTrn353", Some("All Trn353"), None));
    let reason = refused(objects, "WWTrn353");
    assert!(reason.contains("is described 'All Trn353'"), "{reason}");

    let mut objects = instance();
    let structure = objects[0]
        .source_parts
        .iter_mut()
        .find(|p| p.name == "STRUCTURE")
        .unwrap();
    structure.source = structure
        .source
        .replace("\"precision\":4", "\"precision\":5");
    let reason = refused(objects, "Trn353");
    assert!(reason.contains("is not Numeric(4)"), "{reason}");
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
