//! The reader holds a WebPanel's stored settings (declared `Name`, `Description`, `MasterPage`,
//! `WEB_COMP`) as the synthetic settings part, read from the object's own version properties. A
//! panel whose properties are missing is refused by name, never emitted without them.

#![forbid(unsafe_code)]

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::udm::decoder::encode_blob;
use gx_engine::udm::{EntityVersionRecord, UdmClient, UdmReader, UdmWriter};
use gx_engine::webpanel_settings::{SETTINGS_PART, WebPanelSettings};
use std::sync::Arc;

const LIVEIMPORT_WEBFORM: &str = include_str!("fixtures/webform/liveimportwebpanel.webform.xml");
const PLAIN_VARIABLES: &str = include_str!("fixtures/webform/plain.variables.xml");

const LIVEIMPORT_PROPERTIES: &str = "<Properties><Property><Name>Name</Name><Value>LiveImportWebPanel</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property></Properties>";

const MASTER_PROPERTIES: &str = "<Properties><Property><Name>Name</Name><Value>MasterUnanimoSidebar</Value></Property><Property><Name>Description</Name><Value>Master Unanimo Sidebar</Value></Property><Property><Name>WEB_COMP</Name><Value>Master</Value></Property></Properties>";

/// Creates a WebPanel through the writer (its parts and identity), then gives its own version row
/// the stored `properties`, as the live KB holds them.
fn store_panel(client: &Arc<UdmClient>, name: &str, webform: &str, properties: Option<&str>) {
    let writer = UdmWriter::new(client.clone());
    let mut panel = KbObject::new(
        ObjectId::new(name.to_ascii_lowercase()).unwrap(),
        name,
        ObjectKind::WebPanel,
    );
    panel.source_parts.push(SourcePart {
        name: "WEBFORM".into(),
        source: webform.into(),
    });
    panel.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: PLAIN_VARIABLES.into(),
    });
    let identity = writer.create_object(&panel).expect("create panel");
    client
        .insert_entity_version(EntityVersionRecord {
            entity_type_id: identity.entity_type_id,
            entity_id: identity.entity_id,
            entity_version_id: identity.version_id,
            data: encode_blob("", true).unwrap(),
            properties: properties.map(str::to_string),
        })
        .expect("object version row");
}

fn settings_of(object: &KbObject) -> Option<WebPanelSettings> {
    object
        .source_parts
        .iter()
        .find(|part| part.name == SETTINGS_PART)
        .map(|part| WebPanelSettings::from_part(part).expect("settings part reads"))
}

#[test]
fn a_panel_reads_its_declared_name_from_the_stored_properties() {
    let client = Arc::new(UdmClient::new_in_memory());
    store_panel(
        &client,
        "LiveImportWebPanel",
        LIVEIMPORT_WEBFORM,
        Some(LIVEIMPORT_PROPERTIES),
    );

    let reader = UdmReader::new(client);
    let object = reader
        .read_object_by_name("LiveImportWebPanel")
        .expect("read")
        .expect("object exists");

    assert_eq!(
        object.refusal, None,
        "a panel with its properties is not refused"
    );
    let settings = settings_of(&object).expect("the settings part is held");
    assert_eq!(settings.declared_name, "LiveImportWebPanel");
    assert_eq!(settings.description, None);
    assert_eq!(settings.master_page_id, None);
    assert_eq!(settings.web_comp, None);
}

#[test]
fn a_master_page_reads_its_web_comp_and_description() {
    let client = Arc::new(UdmClient::new_in_memory());
    store_panel(
        &client,
        "MasterUnanimoSidebar",
        LIVEIMPORT_WEBFORM,
        Some(MASTER_PROPERTIES),
    );

    let reader = UdmReader::new(client);
    let object = reader
        .read_object_by_name("MasterUnanimoSidebar")
        .expect("read")
        .expect("object exists");

    let settings = settings_of(&object).expect("the settings part is held");
    assert_eq!(settings.declared_name, "MasterUnanimoSidebar");
    assert_eq!(
        settings.description.as_deref(),
        Some("Master Unanimo Sidebar")
    );
    assert_eq!(settings.web_comp.as_deref(), Some("Master"));
}

#[test]
fn a_panel_without_stored_properties_is_refused_by_name() {
    let client = Arc::new(UdmClient::new_in_memory());
    store_panel(&client, "LiveImportWebPanel", LIVEIMPORT_WEBFORM, None);

    let reader = UdmReader::new(client);
    let object = reader
        .read_object_by_name("LiveImportWebPanel")
        .expect("read")
        .expect("object exists");

    assert!(
        settings_of(&object).is_none(),
        "no settings part is held for a refused panel"
    );
    let reason = object.refusal.as_deref().expect("the panel is refused");
    assert!(reason.starts_with("PROPERTIES:"), "{reason}");
}
