//! Query descriptors distinguish the SDK's WebPanel subtypes without changing catalog/codegen kinds.

use super::{ObjectKind, ObjectRow, UdmClient, type_name};

pub(super) fn query_object_type(
    client: &UdmClient,
    row: &ObjectRow,
) -> Result<&'static str, String> {
    if row.kind != ObjectKind::WebPanel {
        return Ok(type_name(row.kind));
    }
    let result = client
        .get_entity_version(row.entity_type_id, row.entity_id, row.entity_version_id)
        .and_then(|version| {
            let version = version.ok_or("the active version record is missing")?;
            webpanel_type(version.properties.as_deref())
        });
    result.map_err(|error| {
        format!(
            "cannot classify WebPanel '{}' ({}/{}/{}): {error}",
            row.name, row.entity_type_id, row.entity_id, row.entity_version_id
        )
    })
}

fn webpanel_type(properties: Option<&str>) -> Result<&'static str, String> {
    let text = properties.ok_or("stored properties are missing")?;
    let document = roxmltree::Document::parse(text)
        .map_err(|_| "stored properties are not valid XML".to_string())?;
    let root = document.root_element();
    if !root.has_tag_name("Properties") {
        return Err("stored properties have no Properties root".into());
    }
    let mut flag = None;
    for property in root.children().filter(|node| node.is_element()) {
        let names: Vec<_> = property
            .children()
            .filter(|node| node.has_tag_name("Name"))
            .collect();
        if !property.has_tag_name("Property")
            || names.len() != 1
            || names[0].children().any(|node| !node.is_text())
            || names[0].text().is_none_or(str::is_empty)
        {
            return Err("stored properties have a malformed Property name".into());
        }
        if names[0].text() != Some("WEB_COMP") {
            continue;
        }
        if flag.is_some() {
            return Err("WEB_COMP is stored more than once".into());
        }
        let values: Vec<_> = property
            .children()
            .filter(|node| node.has_tag_name("Value"))
            .collect();
        if values.len() != 1 || values[0].children().any(|node| !node.is_text()) {
            return Err("WEB_COMP has no single text Value".into());
        }
        flag = Some(values[0].text().unwrap_or_default());
    }
    // GX18 SELECT/SDK differential evidence: absent => WebPanel, Master => MasterPage,
    // Yes => WebComponent. No other spelling/value is established, including blank or No.
    match flag {
        None => Ok("WebPanel"),
        Some("Master") => Ok("MasterPage"),
        Some("Yes") => Ok("WebComponent"),
        Some(_) => Err("unsupported WEB_COMP value".into()),
    }
}
