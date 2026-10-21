//! The settings a WebPanel keeps in its version properties (`EntityVersionProperties`): its declared
//! `Name`, its `Description`, an explicit `MasterPage` and `WEB_COMP` (`Master` for a master page).
//!
//! The reader reads them once per WebPanel and holds them as the synthetic source part
//! [`SETTINGS_PART`], so emitters decide from what the Knowledge Base stores, never from a guess.
//! The catalog stores object names in upper case; the declared name here keeps the case the KB uses,
//! which the generated code must reproduce. Consumers that write a catalog back must skip this part;
//! it is not a GeneXus part.

use crate::catalog::SourcePart;
use serde::{Deserialize, Serialize};

/// The synthetic source part that carries a WebPanel's stored settings as JSON.
pub const SETTINGS_PART: &str = "WEBPANEL_SETTINGS";

/// A WebPanel's stored settings.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct WebPanelSettings {
    /// The entity id of the object in its KB.
    pub entity_id: i32,
    /// The object's name as the KB declares it (the `Name` property), in its declared case.
    pub declared_name: String,
    /// The `Description` property, when set and not blank.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    /// The explicit `MasterPage` property (an entity id), when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub master_page_id: Option<i32>,
    /// The `WEB_COMP` property (`Master` for a master page), when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub web_comp: Option<String>,
}

impl WebPanelSettings {
    /// The settings as the synthetic source part the catalog carries.
    pub fn to_part(&self) -> Result<SourcePart, String> {
        Ok(SourcePart {
            name: SETTINGS_PART.to_string(),
            source: serde_json::to_string(self)
                .map_err(|e| format!("cannot encode WebPanel settings: {e}"))?,
        })
    }

    /// The settings held in a catalog part.
    pub fn from_part(part: &SourcePart) -> Result<Self, String> {
        serde_json::from_str(&part.source)
            .map_err(|e| format!("its stored settings are unreadable: {e}"))
    }
}

/// The settings of one stored WebPanel, from its `Properties` XML.
///
/// `properties` is the stored `EntityVersionProperties` text. The `Name` property is required: a
/// panel without a stored name has no declared case to reproduce. A value that is not XML, or a
/// `MasterPage` that is not an entity id, is an error.
pub fn settings_from_properties(
    entity_id: i32,
    properties: Option<&str>,
) -> Result<WebPanelSettings, String> {
    let text = properties
        .filter(|text| !text.trim().is_empty())
        .ok_or_else(|| "it has no stored properties, so no declared name".to_string())?;
    let document = roxmltree::Document::parse(text)
        .map_err(|e| format!("the stored properties are not XML: {e}"))?;
    let mut declared_name = None;
    let mut settings = WebPanelSettings {
        entity_id,
        declared_name: String::new(),
        description: None,
        master_page_id: None,
        web_comp: None,
    };
    for property in document
        .descendants()
        .filter(|node| node.has_tag_name("Property"))
    {
        let name = child_text(property, "Name");
        let value = child_text(property, "Value").unwrap_or_default();
        match name {
            Some("Name") => declared_name = non_empty(value),
            Some("Description") => settings.description = non_empty(value),
            Some("MasterPage") => {
                let id = value.trim().parse::<i32>().map_err(|_| {
                    format!("the MasterPage property '{value}' is not an entity id")
                })?;
                settings.master_page_id = Some(id);
            }
            Some("WEB_COMP") => settings.web_comp = non_empty(value),
            _ => {}
        }
    }
    settings.declared_name =
        declared_name.ok_or_else(|| "its stored properties hold no Name".to_string())?;
    Ok(settings)
}

fn child_text<'a>(node: roxmltree::Node<'a, '_>, tag: &str) -> Option<&'a str> {
    node.children()
        .find(|child| child.has_tag_name(tag))
        .map(|child| child.text().unwrap_or_default())
}

fn non_empty(value: &str) -> Option<String> {
    let trimmed = value.trim();
    (!trimmed.is_empty()).then(|| trimmed.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn settings_read_name_description_master_and_web_comp() {
        let props = r#"<Properties><Property><Name>Name</Name><Value>Gx0010</Value></Property><Property><Name>Description</Name><Value>Selection &amp; List</Value></Property><Property><Name>MasterPage</Name><Value>2</Value></Property></Properties>"#;
        let settings = settings_from_properties(94, Some(props)).unwrap();
        assert_eq!(settings.declared_name, "Gx0010");
        assert_eq!(settings.description.as_deref(), Some("Selection & List"));
        assert_eq!(settings.master_page_id, Some(2));
        assert_eq!(settings.web_comp, None);

        let master = settings_from_properties(
            1,
            Some("<Properties><Property><Name>Name</Name><Value>MasterUnanimoSidebar</Value></Property><Property><Name>WEB_COMP</Name><Value>Master</Value></Property></Properties>"),
        )
        .unwrap();
        assert_eq!(master.declared_name, "MasterUnanimoSidebar");
        assert_eq!(master.web_comp.as_deref(), Some("Master"));
    }

    #[test]
    fn a_panel_without_stored_properties_or_a_name_is_an_error() {
        assert!(settings_from_properties(3, None).is_err());
        assert!(settings_from_properties(3, Some("  ")).is_err());
        let no_name = "<Properties><Property><Name>WEB_COMP</Name><Value>Master</Value></Property></Properties>";
        assert!(
            settings_from_properties(3, Some(no_name))
                .unwrap_err()
                .contains("no Name")
        );
    }

    #[test]
    fn a_blank_description_is_no_description() {
        let props = "<Properties><Property><Name>Name</Name><Value>P</Value></Property><Property><Name>Description</Name><Value>   </Value></Property></Properties>";
        assert_eq!(
            settings_from_properties(3, Some(props))
                .unwrap()
                .description,
            None
        );
    }

    #[test]
    fn a_master_page_property_that_is_not_an_id_is_an_error() {
        let props = "<Properties><Property><Name>Name</Name><Value>P</Value></Property><Property><Name>MasterPage</Name><Value>abc</Value></Property></Properties>";
        assert!(settings_from_properties(1, Some(props)).is_err());
        assert!(settings_from_properties(1, Some("<not xml")).is_err());
    }

    #[test]
    fn settings_round_trip_through_the_catalog_part() {
        let settings = WebPanelSettings {
            entity_id: 4,
            declared_name: "TestVldWpf5317a".into(),
            description: Some("Demo".into()),
            master_page_id: Some(2),
            web_comp: None,
        };
        let part = settings.to_part().unwrap();
        assert_eq!(part.name, SETTINGS_PART);
        assert_eq!(WebPanelSettings::from_part(&part).unwrap(), settings);
    }
}
