//! The default selection prompts of KBTeste (`Gx0010`, `Gx0040`, `Gx0080`) are emitted byte for byte
//! as the SDK wrote them (`NetModel/Web/gx00x0.cs`, line endings and build stamps normalised), and a
//! prompt whose stored parts leave the pattern's shape is refused by name.
//!
//! The three cover: numeric filters of 4 and 8 digits with a `VarChar(40)` (dynamic SQL), a single
//! key filter (static SQL, the key column returning on click), and `Character(20)` filters with the
//! description column last and a name past the 30-character physical limit.

#![forbid(unsafe_code)]

use gx_engine::catalog::{
    Catalog, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::csharp::webform::{WebClass, WebPanelEnvironment, plan_web_class};
use gx_engine::model::DataType;
use gx_engine::transaction::{TransactionAttributeRef, TransactionLevel, TransactionStructure};
use gx_engine::webpanel_settings::WebPanelSettings;
use std::collections::BTreeMap;

const CLASS_TYPE: &str = "d52f9833-90c9-4096-8e42-27485741f78a";
const MASTER_WEBFORM: &str = include_str!("fixtures/webform/masterprompt.webform.xml");

struct Fixture {
    name: &'static str,
    entity: i32,
    description: &'static str,
    webform: &'static str,
    variables: &'static str,
    rules: &'static str,
    conditions: &'static str,
    events: &'static str,
    sdk: &'static str,
}

macro_rules! fixture {
    ($name:literal, $file:literal, $entity:expr, $description:literal) => {
        Fixture {
            name: $name,
            entity: $entity,
            description: $description,
            webform: include_str!(concat!("fixtures/prompt/", $file, ".webform.txt")),
            variables: include_str!(concat!("fixtures/prompt/", $file, ".variables.txt")),
            rules: include_str!(concat!("fixtures/prompt/", $file, ".rules.txt")),
            conditions: include_str!(concat!("fixtures/prompt/", $file, ".conditions.txt")),
            events: include_str!(concat!("fixtures/prompt/", $file, ".events.txt")),
            sdk: include_str!(concat!("fixtures/prompt/", $file, ".cs")),
        }
    };
}

fn fixtures() -> Vec<Fixture> {
    vec![
        fixture!(
            "Gx0010",
            "gx0010",
            94,
            "Selection List Trn Group Probe Base"
        ),
        fixture!("Gx0040", "gx0040", 96, "Selection List Plan070 Trn"),
        fixture!(
            "Gx0080",
            "gx0080",
            100,
            "Selection List Live Move Attribute Rules2"
        ),
    ]
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
    description: &str,
    master_page_id: Option<i32>,
    web_comp: Option<&str>,
) -> SourcePart {
    WebPanelSettings {
        entity_id,
        declared_name: name.into(),
        description: Some(description.into()),
        master_page_id,
        web_comp: web_comp.map(str::to_string),
    }
    .to_part()
    .unwrap()
}

fn master() -> KbObject {
    let mut master = KbObject::new(
        ObjectId::new("masterprompt").unwrap(),
        "MasterPrompt",
        ObjectKind::WebPanel,
    );
    master.module_path = vec!["General".into(), "UI".into()];
    master.source_parts.push(part("WEBFORM", MASTER_WEBFORM));
    master.source_parts.push(settings(
        2,
        "MasterPrompt",
        "Prompt Master Page",
        None,
        Some("Master"),
    ));
    master
}

fn prompt(fixture: &Fixture) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(fixture.name.to_ascii_lowercase()).unwrap(),
        fixture.name,
        ObjectKind::WebPanel,
    );
    for (name, source) in [
        ("WEBFORM", fixture.webform),
        ("VARIABLES", fixture.variables),
        ("RULES", fixture.rules),
        ("CONDITIONS", fixture.conditions),
        ("EVENTS", fixture.events),
    ] {
        object.source_parts.push(part(name, source));
    }
    object.source_parts.push(settings(
        fixture.entity,
        fixture.name,
        fixture.description,
        Some(2),
        None,
    ));
    object.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("masterprompt").unwrap(),
    });
    object
}

fn attribute(name: &str, is_key: bool, data_type: DataType) -> TransactionAttributeRef {
    TransactionAttributeRef {
        name: name.into(),
        is_key,
        data_type: Some(data_type),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    }
}

fn number(precision: u8) -> DataType {
    DataType::Numeric {
        precision,
        scale: 0,
    }
}

fn transaction(name: &str, attributes: Vec<TransactionAttributeRef>) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(name.to_ascii_lowercase()).unwrap(),
        name,
        ObjectKind::Transaction,
    );
    let structure = TransactionStructure::new(TransactionLevel {
        name: name.into(),
        description: None,
        attributes,
        levels: Vec::new(),
    });
    object.source_parts.push(part(
        "STRUCTURE",
        &serde_json::to_string(&structure).unwrap(),
    ));
    object
}

fn transactions() -> Vec<KbObject> {
    vec![
        transaction(
            "TrnGroupProbeBase",
            vec![
                attribute("TrnGroupProbeBaseId", true, number(4)),
                attribute("GpBaseId", false, number(8)),
                attribute("GpBaseDesc", false, DataType::Varchar { length: 40 }),
            ],
        ),
        transaction(
            "Plan070Trn",
            vec![attribute("Plan070TrnId", true, number(4))],
        ),
        transaction(
            "LiveMoveAttributeRules2",
            vec![
                attribute("LiveMoveAttributeRules2Id", true, number(4)),
                attribute(
                    "LiveMoveAttributeRules2Description",
                    false,
                    DataType::Character { length: 20 },
                ),
                attribute(
                    "LiveMoveAttributeRules2Code",
                    false,
                    DataType::Character { length: 20 },
                ),
            ],
        ),
    ]
}

fn catalog(objects: Vec<KbObject>) -> Catalog {
    Catalog::from_objects_map(objects.into_iter().map(|o| (o.id.clone(), o)).collect())
}

fn env() -> WebPanelEnvironment {
    let classes = [
        (5, "AdvancedContainerItem"),
        (6, "AdvancedContainerVisible"),
        (27, "BtnCancel"),
        (47, "BtnToggle"),
        (153, "OptionalColumn"),
        (174, "PromptAdvancedBarCell"),
        (175, "PromptGrid"),
        (196, "SelectionAttribute"),
        (239, "WWFilterLabel"),
        (443, "ContainerFluid"),
        (472, "DescriptionAttribute"),
        (473, "WWColumn"),
        (485, "AdvancedContainerItemExpanded"),
        (486, "PromptContainer"),
        (488, "WWAdvancedLabel"),
        (489, "WWFiltersCell"),
        (491, "WWGridCell"),
        (493, "ToggleCell"),
        (494, "WWActionColumn"),
    ];
    WebPanelEnvironment {
        theme: Some("KBTeste".into()),
        generator: Some("GeneXus .NET 18_0_10-184260".into()),
        master_classes: BTreeMap::new(),
        build_number: Some("239440".into()),
        support_class: Some("namespace GeneXus.Programs { public class GxWebStd { } }".into()),
        design_classes: classes
            .iter()
            .map(|(id, name)| (format!("{CLASS_TYPE}-{id}"), name.to_string()))
            .collect(),
        object_guids: BTreeMap::new(),
        attributes: BTreeMap::new(),
        table_keys: BTreeMap::new(),
        subtype_groups: BTreeMap::new(),
    }
}

/// Replaces every `?<digits>"` build stamp with one token. The SDK reads its clock per reference.
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

fn plan(object: &KbObject, extra: Vec<KbObject>) -> Result<WebClass, String> {
    let mut objects = transactions();
    objects.push(master());
    objects.push(object.clone());
    objects.extend(extra);
    let catalog = catalog(objects);
    plan_web_class(catalog.get(&object.id).unwrap(), &catalog, &env())
}

#[test]
fn the_kbteste_prompts_match_the_sdk_output() {
    for fixture in fixtures() {
        let class = plan(&prompt(&fixture), Vec::new())
            .unwrap_or_else(|reason| panic!("{} refused: {reason}", fixture.name));
        let WebClass::Prompt(prompt) = class else {
            panic!("{} did not plan as a prompt", fixture.name);
        };
        assert_eq!(
            prompt.file_name(),
            format!("{}.cs", fixture.name.to_ascii_lowercase())
        );
        let emitted = prompt.emit("202610181732626");
        assert!(
            !emitted.contains("@@"),
            "{}: placeholder left",
            fixture.name
        );
        assert_eq!(
            without_stamps(&emitted),
            without_stamps(fixture.sdk),
            "{} differs from the SDK output",
            fixture.name
        );
    }
}

fn with_part(object: &KbObject, name: &str, source: &str) -> KbObject {
    let mut changed = object.clone();
    changed.source_parts.retain(|p| p.name != name);
    changed.source_parts.push(part(name, source));
    changed
}

#[test]
fn a_prompt_edited_out_of_the_pattern_is_refused_by_name() {
    let fixture = &fixtures()[0];
    let base = prompt(fixture);

    let events = format!("{}\nEvent Refresh\nEndEvent\n", fixture.events);
    let reason = plan(&with_part(&base, "EVENTS", &events), Vec::new()).unwrap_err();
    assert!(
        reason.contains("events are not the prompt pattern's"),
        "{reason}"
    );

    let conditions = fixture.conditions.replace("GpBaseId >=", "GpBaseId <=");
    let reason = plan(&with_part(&base, "CONDITIONS", &conditions), Vec::new()).unwrap_err();
    assert!(reason.contains("conditions"), "{reason}");

    let rules = fixture.rules.replace("order(", "noaccept(");
    let reason = plan(&with_part(&base, "RULES", &rules), Vec::new()).unwrap_err();
    assert!(reason.contains("rules"), "{reason}");

    let webform = fixture.webform.replacen(
        "MaxRows&lt;/Name&gt;&lt;Value&gt;10",
        "MaxRows&lt;/Name&gt;&lt;Value&gt;20",
        1,
    );
    assert_ne!(webform, fixture.webform, "the edit applies");
    let reason = plan(&with_part(&base, "WEBFORM", &webform), Vec::new()).unwrap_err();
    assert!(
        reason.contains("layout is not the prompt pattern's"),
        "{reason}"
    );
}

#[test]
fn an_unmatched_attribute_type_or_an_unresolved_class_is_refused() {
    let fixture = &fixtures()[0];
    let mut objects = transactions();
    objects.retain(|o| o.name != "TrnGroupProbeBase");
    let dated = transaction(
        "TrnGroupProbeBase",
        vec![
            attribute("TrnGroupProbeBaseId", true, number(4)),
            attribute("GpBaseId", false, DataType::Date {}),
            attribute("GpBaseDesc", false, DataType::Varchar { length: 40 }),
        ],
    );
    let object = prompt(fixture);
    objects.extend([master(), object.clone(), dated]);
    let catalog = catalog(objects);
    let reason = plan_web_class(catalog.get(&object.id).unwrap(), &catalog, &env()).unwrap_err();
    assert!(
        reason.contains("GpBaseId") && reason.contains("not matched"),
        "{reason}"
    );

    let full = catalog_with_prompt(&object);
    let mut env = env();
    env.design_classes.remove(&format!("{CLASS_TYPE}-472"));
    let reason = plan_web_class(full.get(&object.id).unwrap(), &full, &env).unwrap_err();
    assert!(reason.contains(&format!("{CLASS_TYPE}-472")), "{reason}");
}

fn catalog_with_prompt(object: &KbObject) -> Catalog {
    let mut objects = transactions();
    objects.push(master());
    objects.push(object.clone());
    catalog(objects)
}
