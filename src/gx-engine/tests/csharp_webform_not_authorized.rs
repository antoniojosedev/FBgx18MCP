//! The security module's `NotAuthorized` WebPanel of KBTeste is emitted byte for byte as the SDK
//! wrote it (`NetModel/Web/general/security/notauthorized.cs`, build stamps normalised), and a panel
//! whose stored parts leave that shape is refused by name.

#![forbid(unsafe_code)]

use gx_engine::catalog::{
    Catalog, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::csharp::webform::{WebClass, WebPanelEnvironment, plan_web_class};
use gx_engine::webpanel_settings::WebPanelSettings;
use std::collections::BTreeMap;

const WEBFORM: &str = include_str!("fixtures/not_authorized/notauthorized.webform.txt");
const VARIABLES: &str = include_str!("fixtures/not_authorized/notauthorized.variables.txt");
const RULES: &str = include_str!("fixtures/not_authorized/notauthorized.rules.txt");
const CONDITIONS: &str = include_str!("fixtures/not_authorized/notauthorized.conditions.txt");
const EVENTS: &str = include_str!("fixtures/not_authorized/notauthorized.events.txt");
const SDK: &str = include_str!("fixtures/not_authorized/notauthorized.cs");

fn part(name: &str, source: &str) -> SourcePart {
    SourcePart {
        name: name.into(),
        source: source.into(),
    }
}

fn settings(entity_id: i32, name: &str, description: &str, web_comp: Option<&str>) -> SourcePart {
    WebPanelSettings {
        entity_id,
        declared_name: name.into(),
        description: Some(description.into()),
        master_page_id: None,
        web_comp: web_comp.map(str::to_string),
    }
    .to_part()
    .unwrap()
}

fn master() -> KbObject {
    let mut master = KbObject::new(
        ObjectId::new("masterunanimosidebar").unwrap(),
        "MasterUnanimoSidebar",
        ObjectKind::WebPanel,
    );
    master.module_path = vec!["General".into(), "UI".into()];
    master.source_parts.push(settings(
        3,
        "MasterUnanimoSidebar",
        "Master Unanimo Sidebar",
        Some("Master"),
    ));
    master
}

fn panel() -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new("notauthorized").unwrap(),
        "NotAuthorized",
        ObjectKind::WebPanel,
    );
    object.module_path = vec!["General".into(), "Security".into()];
    for (name, source) in [
        ("WEBFORM", WEBFORM),
        ("VARIABLES", VARIABLES),
        ("RULES", RULES),
        ("CONDITIONS", CONDITIONS),
        ("EVENTS", EVENTS),
    ] {
        object.source_parts.push(part(name, source));
    }
    object.source_parts.push(settings(
        64,
        "NotAuthorized",
        "Not Authorized web panel",
        None,
    ));
    object.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("masterunanimosidebar").unwrap(),
    });
    object
}

fn env() -> WebPanelEnvironment {
    WebPanelEnvironment {
        theme: Some("KBTeste".into()),
        generator: Some("GeneXus .NET 18_0_10-184260".into()),
        master_classes: BTreeMap::new(),
        build_number: Some("239440".into()),
        support_class: Some("namespace GeneXus.Programs { public class GxWebStd { } }".into()),
        design_classes: BTreeMap::new(),
        object_guids: BTreeMap::new(),
        attributes: BTreeMap::new(),
        table_keys: BTreeMap::new(),
        subtype_groups: BTreeMap::new(),
    }
}

fn plan(object: &KbObject) -> Result<WebClass, String> {
    let catalog = Catalog::from_objects_map(
        [master(), object.clone()]
            .into_iter()
            .map(|o| (o.id.clone(), o))
            .collect(),
    );
    plan_web_class(catalog.get(&object.id).unwrap(), &catalog, &env())
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
fn the_kbteste_not_authorized_panel_matches_the_sdk_output() {
    let class = plan(&panel()).unwrap_or_else(|reason| panic!("refused: {reason}"));
    let WebClass::NotAuthorized(panel) = class else {
        panic!("NotAuthorized did not plan as a not-authorized panel");
    };
    assert_eq!(panel.file_name(), "notauthorized.cs");
    let emitted = panel.emit("202610181732524");
    assert!(!emitted.contains("@@"), "placeholder left");
    assert_eq!(without_stamps(&emitted), without_stamps(SDK));
}

#[test]
fn a_panel_edited_out_of_the_shape_is_refused_by_name() {
    let base = panel();

    let events = format!("{EVENTS}\nEvent Refresh\nEndEvent\n");
    let reason = plan(&with_part(&base, "EVENTS", &events)).unwrap_err();
    assert!(
        reason.contains("not a Start event showing one message"),
        "{reason}"
    );

    let reason = plan(&with_part(
        &base,
        "RULES",
        "parm(in:&GxObject, out:&Other);",
    ))
    .unwrap_err();
    assert!(reason.contains("rules are not the single parm"), "{reason}");

    let reason = plan(&with_part(&base, "CONDITIONS", "GxObject = 'x';")).unwrap_err();
    assert!(reason.contains("conditions"), "{reason}");

    let variables = VARIABLES.replace("Name=\"GxObject\"", "Name=\"Other\"");
    assert_ne!(variables, VARIABLES, "the edit applies");
    let reason = plan(&with_part(&base, "VARIABLES", &variables)).unwrap_err();
    assert!(reason.contains("variables"), "{reason}");

    let webform = WEBFORM.replace("ErrViewer1", "ErrViewer2");
    assert_ne!(webform, WEBFORM, "the edit applies");
    let reason = plan(&with_part(&base, "WEBFORM", &webform)).unwrap_err();
    assert!(reason.contains("not-authorized layout"), "{reason}");
}

#[test]
fn the_title_and_message_are_read_from_the_panel() {
    let base = panel();
    let edited = with_part(
        &with_part(
            &base,
            "WEBFORM",
            &WEBFORM.replace("Not Authorized", "Access Denied"),
        ),
        "EVENTS",
        &EVENTS.replace(
            "You are not authorized to do this action",
            "Access is denied",
        ),
    );
    let WebClass::NotAuthorized(panel) = plan(&edited).expect("plans") else {
        panic!("not a not-authorized panel");
    };
    let emitted = panel.emit("1");
    assert!(emitted.contains("\"Access Denied\""));
    assert!(emitted.contains("GX_msglist.addItem(\"Access is denied\");"));
    assert!(!emitted.contains("You are not authorized"));
}
