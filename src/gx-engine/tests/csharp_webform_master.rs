//! A master page whose layout is one classed `MainTable` holding the `ContentHolder`, with no code,
//! is emitted in the SDK shape (`General.UI.MasterPrompt` of KBTeste), and panels using it resolve
//! its class path from the KB module path without configuration.

#![forbid(unsafe_code)]

use gx_engine::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::csharp::webform::{
    WebClass, WebPanelEnvironment, plan_master_page, plan_plain_webpanel, plan_web_class,
};
use gx_engine::webpanel_settings::WebPanelSettings;
use std::collections::BTreeMap;

const MASTER_WEBFORM: &str = include_str!("fixtures/webform/masterprompt.webform.xml");
/// The SDK output, line endings normalised. The SDK reads its clock twice and wrote `…250` and
/// `…251`; the emitter writes one generation stamp, so the second is normalised to the first.
const MASTER_SDK: &str = include_str!("fixtures/webform/masterprompt.cs");
const STAMP: &str = "20261018173250";
const LIVEIMPORT_WEBFORM: &str = include_str!("fixtures/webform/liveimportwebpanel.webform.xml");
const PLAIN_VARIABLES: &str = include_str!("fixtures/webform/plain.variables.xml");
const CLASS_TYPE: &str = "d52f9833-90c9-4096-8e42-27485741f78a";
const USER_VARIABLE: &str = r#"<Variables><Variable Id="5" Name="IsAuthorized"><Properties><Property><Name>Name</Name><Value>IsAuthorized</Value></Property></Properties></Variable></Variables>"#;

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

fn part(name: &str, source: &str) -> SourcePart {
    SourcePart {
        name: name.into(),
        source: source.into(),
    }
}

fn master_prompt(webform: &str) -> KbObject {
    let mut master = KbObject::new(
        ObjectId::new("masterprompt").unwrap(),
        "MasterPrompt",
        ObjectKind::WebPanel,
    );
    master.module_path = vec!["General".into(), "UI".into()];
    master.source_parts.push(part("WEBFORM", webform));
    master.source_parts.push(part("VARIABLES", USER_VARIABLE));
    master.source_parts.push(part("EVENTS", ""));
    master.source_parts.push(settings(
        2,
        "MasterPrompt",
        Some("Prompt Master Page"),
        Some("Master"),
    ));
    master
}

fn env() -> WebPanelEnvironment {
    WebPanelEnvironment {
        theme: Some("KBTeste".into()),
        generator: Some("GeneXus .NET 18_0_10-184260".into()),
        master_classes: BTreeMap::new(),
        build_number: Some("239440".into()),
        support_class: Some("namespace GeneXus.Programs { public class GxWebStd { } }".into()),
        design_classes: BTreeMap::from([
            (format!("{CLASS_TYPE}-443"), "ContainerFluid".to_string()),
            (format!("{CLASS_TYPE}-179"), "PromptMainTable".to_string()),
        ]),
        object_guids: BTreeMap::new(),
        attributes: BTreeMap::new(),
        table_keys: BTreeMap::new(),
        subtype_groups: BTreeMap::new(),
    }
}

#[test]
fn masterprompt_matches_the_sdk_output() {
    let plan = plan_master_page(&master_prompt(MASTER_WEBFORM), &env()).expect("master plans");
    assert_eq!(plan.form_number, "022");
    assert_eq!(plan.section_number, "020");
    assert_eq!(plan.table_class, "ContainerFluid PromptMainTable");
    assert_eq!(plan.scope.program_name, "General.UI.MasterPrompt");
    assert_eq!(plan.file_name(), "masterprompt.cs");
    let expected = MASTER_SDK.replace("?20261018173251", &format!("?{STAMP}"));
    let emitted = plan.emit(STAMP);
    assert!(!emitted.contains("@@"), "placeholder left");
    assert_eq!(emitted, expected);
}

#[test]
fn an_unresolved_design_class_refuses_the_master() {
    let mut env = env();
    env.design_classes.remove(&format!("{CLASS_TYPE}-179"));
    let reason = plan_master_page(&master_prompt(MASTER_WEBFORM), &env).unwrap_err();
    assert!(reason.contains(&format!("{CLASS_TYPE}-179")), "{reason}");
}

#[test]
fn a_master_with_code_or_other_controls_is_refused() {
    let mut with_events = master_prompt(MASTER_WEBFORM);
    with_events
        .source_parts
        .retain(|part| part.name != "EVENTS");
    with_events
        .source_parts
        .push(part("EVENTS", "Event Start\nEndEvent"));
    let reason = plan_master_page(&with_events, &env()).unwrap_err();
    assert!(reason.contains("events code"), "{reason}");

    let extra_cell = MASTER_WEBFORM.replacen(
        "<contentholder controlName=\"ContentHolder\" />",
        "<contentholder controlName=\"ContentHolder\" /><textblock controlName=\"X\" />",
        1,
    );
    assert_ne!(extra_cell, MASTER_WEBFORM, "fixture edit applied");
    let reason = plan_master_page(&master_prompt(&extra_cell), &env()).unwrap_err();
    assert!(reason.contains("exactly one 'contentholder'"), "{reason}");
}

#[test]
fn a_panel_resolves_its_master_class_path_from_the_module_path() {
    let mut panel = KbObject::new(
        ObjectId::new("liveimportwebpanel").unwrap(),
        "LiveImportWebPanel",
        ObjectKind::WebPanel,
    );
    panel.source_parts.push(part("WEBFORM", LIVEIMPORT_WEBFORM));
    panel.source_parts.push(part("VARIABLES", PLAIN_VARIABLES));
    panel
        .source_parts
        .push(settings(3, "LiveImportWebPanel", None, None));
    panel.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new("masterprompt").unwrap(),
    });
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[
                Edit::Upsert(master_prompt(MASTER_WEBFORM)),
                Edit::Upsert(panel.clone()),
            ],
        )
        .unwrap();
    let plain = plan_plain_webpanel(&panel, &catalog, &env()).expect("panel plans");
    assert_eq!(plain.master_class, "general.ui.masterprompt");

    let master = catalog
        .get(&ObjectId::new("masterprompt").unwrap())
        .unwrap();
    assert!(matches!(
        plan_web_class(master, &catalog, &env()),
        Ok(WebClass::Master(_))
    ));
}
