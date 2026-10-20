//! The Unanimo sidebar master of KBTeste (`General.UI.MasterUnanimoSidebar`) is emitted byte for byte
//! as the SDK wrote it (`NetModel/Web/general/ui/masterunanimosidebar.cs`, build stamps normalised),
//! its KB-local references are matched by what they name, and a master edited out of that shape is
//! refused by name.

#![forbid(unsafe_code)]

use gx_engine::catalog::{Catalog, KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::csharp::webform::{WebClass, WebPanelEnvironment, plan_web_class};
use gx_engine::webpanel_settings::WebPanelSettings;
use std::collections::BTreeMap;

const WEBFORM: &str = include_str!("fixtures/unanimo_master/masterunanimosidebar.webform.txt");
const VARIABLES: &str = include_str!("fixtures/unanimo_master/masterunanimosidebar.variables.txt");
const EVENTS: &str = include_str!("fixtures/unanimo_master/masterunanimosidebar.events.txt");
const SDK: &str = include_str!("fixtures/unanimo_master/masterunanimosidebar.cs");
const CLASS_TYPE: &str = "d52f9833-90c9-4096-8e42-27485741f78a";

fn part(name: &str, source: &str) -> SourcePart {
    SourcePart {
        name: name.into(),
        source: source.into(),
    }
}

fn master() -> KbObject {
    let mut master = KbObject::new(
        ObjectId::new("masterunanimosidebar").unwrap(),
        "MasterUnanimoSidebar",
        ObjectKind::WebPanel,
    );
    master.module_path = vec!["General".into(), "UI".into()];
    for (name, source) in [
        ("WEBFORM", WEBFORM),
        ("VARIABLES", VARIABLES),
        ("EVENTS", EVENTS),
        ("RULES", ""),
        ("CONDITIONS", ""),
    ] {
        master.source_parts.push(part(name, source));
    }
    master.source_parts.push(
        WebPanelSettings {
            entity_id: 1,
            declared_name: "MasterUnanimoSidebar".into(),
            description: Some("Master Unanimo Sidebar".into()),
            master_page_id: None,
            web_comp: Some("Master".into()),
        }
        .to_part()
        .unwrap(),
    );
    master
}

fn provider() -> KbObject {
    let mut provider = KbObject::new(
        ObjectId::new("sidebaritemsdp").unwrap(),
        "SidebarItemsDP",
        ObjectKind::DataProvider,
    );
    provider.module_path = vec!["General".into(), "UI".into()];
    provider
}

fn env() -> WebPanelEnvironment {
    let classes = [
        (63, "expandible-container"),
        (140, "MainContainer"),
        (337, "header__logo"),
        (338, "header__title"),
        (339, "HeaderContainer"),
        (362, "sidebar__toggle-ico"),
    ];
    WebPanelEnvironment {
        design_classes: classes
            .iter()
            .map(|(id, name)| (format!("{CLASS_TYPE}-{id}"), name.to_string()))
            .collect(),
        object_guids: BTreeMap::from([
            (
                "9fb193d9-64a4-4d30-b129-ff7c76830f7e-74".to_string(),
                "ef73725b-c186-4f57-94bc-867e0a183c3e".to_string(),
            ),
            (
                "9fb193d9-64a4-4d30-b129-ff7c76830f7e-46".to_string(),
                "194a18de-81a7-4ab6-8eb3-5191aa65f696".to_string(),
            ),
            (
                "447527b5-9210-4523-898b-5dccb17be60a-58".to_string(),
                "e48ae171-a60a-4b3a-ab74-7b8a7ca37e42".to_string(),
            ),
            (
                "00972a17-9975-449e-aab1-d26165d51393-8".to_string(),
                "b67b7a1b-60cd-4f53-8610-f94684abcdc7".to_string(),
            ),
        ]),
        ..WebPanelEnvironment::default()
    }
}

fn plan_with(
    object: &KbObject,
    objects: Vec<KbObject>,
    env: &WebPanelEnvironment,
) -> Result<WebClass, String> {
    let catalog = Catalog::from_objects_map(
        objects
            .into_iter()
            .chain([object.clone()])
            .map(|o| (o.id.clone(), o))
            .collect(),
    );
    plan_web_class(catalog.get(&object.id).unwrap(), &catalog, env)
}

fn plan(object: &KbObject) -> Result<WebClass, String> {
    plan_with(object, vec![provider()], &env())
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
fn the_kbteste_sidebar_master_matches_the_sdk_output() {
    let class = plan(&master()).unwrap_or_else(|reason| panic!("refused: {reason}"));
    let WebClass::UnanimoMaster(master) = class else {
        panic!("MasterUnanimoSidebar did not plan as the Unanimo sidebar master");
    };
    assert_eq!(master.file_name(), "masterunanimosidebar.cs");
    let emitted = master.emit("202610181732524");
    assert!(!emitted.contains("@@"), "placeholder left");
    assert_eq!(without_stamps(&emitted), without_stamps(SDK));
}

#[test]
fn a_master_edited_out_of_the_shape_is_refused_by_name() {
    let base = master();

    let events = EVENTS.replace("DistanceToTop = 60", "DistanceToTop = 80");
    assert_ne!(events, EVENTS, "the edit applies");
    let reason = plan(&with_part(&base, "EVENTS", &events)).unwrap_err();
    assert!(reason.contains("events are not the Unanimo"), "{reason}");

    let reason = plan(&with_part(&base, "RULES", "parm(in:&x);")).unwrap_err();
    assert!(reason.contains("it has rules"), "{reason}");

    let webform = WEBFORM.replace("Application Name", "My Application");
    assert_ne!(webform, WEBFORM, "the edit applies");
    let reason = plan(&with_part(&base, "WEBFORM", &webform)).unwrap_err();
    assert!(reason.contains("layout is not the Unanimo"), "{reason}");

    let variables = VARIABLES.replace("Name=\"target\"", "Name=\"destination\"");
    assert_ne!(variables, VARIABLES, "the edit applies");
    let reason = plan(&with_part(&base, "VARIABLES", &variables)).unwrap_err();
    assert!(reason.contains("variables are not the Unanimo"), "{reason}");
}

#[test]
fn a_reference_to_another_object_or_a_missing_provider_is_refused() {
    let base = master();

    // The logo names another image: same layout text, different entity GUID.
    let mut other_image = env();
    other_image.object_guids.insert(
        "9fb193d9-64a4-4d30-b129-ff7c76830f7e-46".into(),
        "00000000-0000-0000-0000-000000000000".into(),
    );
    let reason = plan_with(&base, vec![provider()], &other_image).unwrap_err();
    assert!(reason.contains("layout is not the Unanimo"), "{reason}");

    let mut unresolved = env();
    unresolved
        .design_classes
        .remove(&format!("{CLASS_TYPE}-63"));
    let reason = plan_with(&base, vec![provider()], &unresolved).unwrap_err();
    assert!(reason.contains(&format!("{CLASS_TYPE}-63")), "{reason}");

    let reason = plan_with(&base, Vec::new(), &env()).unwrap_err();
    assert!(reason.contains("SidebarItemsDP"), "{reason}");
}
