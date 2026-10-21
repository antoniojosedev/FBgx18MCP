//! SDK-shaped `GXDataArea` emitter for plain WebPanels.
//!
//! A plain WebPanel has no events, rules or conditions, no user variables, and a stored layout that
//! is one empty `MainTable`. Its generated class has the shape of the GeneXus 18 output for that
//! case (`webform/plain_gxdataarea.cs.tmpl`, checked byte-for-byte against the SDK output in the
//! tests). Every fact the output depends on comes from the Knowledge Base or from explicit
//! configuration. A WebPanel is refused by name, with the reason, when any of them is missing:
//!
//! - the master page is the master WebPanel (`WEB_COMP` `Master`) the panel references as `Uses` in
//!   the KB. A panel with an explicit `MasterPage` property is refused: master-page emission is not
//!   implemented;
//! - the description is the stored `Description` when set, otherwise the object name split at
//!   lower-to-upper case boundaries. A stored `Description` is accepted only with the characters
//!   matched by [`literal_ok`];
//! - the form and section numbers are `entity id * 10 + 2` and `entity id * 10`, three digits;
//! - the build number is `GX_BUILD_NUMBER` in `NetModel/Web/GXCFG.WEB`;
//! - the theme, the master page's runtime class path and the generator banner are explicit
//!   configuration ([`WebPanelEnvironment`]). When one is unset, the refusal names its option.
//!
//! The layouts matched against SDK output are an empty table with no class (`Table`, zero rows) and
//! a table with a class and three rows (`TableGrid`). Any other shape is refused until it is matched.

use crate::catalog::{Catalog, KbObject, ObjectKind, ReferenceKind};
use crate::webpanel_settings::{SETTINGS_PART, WebPanelSettings};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::Path;

mod layout;
mod master;
mod web_class;
mod web_config;

use layout::{check_layout, child_text};
use master::master_of;
pub use master::{MasterPage, ModuleScope, plan_master_page};
pub(crate) use master::{master_class_of, master_names};
pub use web_class::{WebClass, plan_web_class};
pub use web_config::{
    client_config_script, current_stamp, differs_only_in_stamp, generator_banner,
    product_version_from_pe, stamp_from_unix_seconds, web_build_number_from_kb,
    web_client_config_from_kb, web_generator_from_install, web_support_from_kb,
};

/// The class template: GeneXus 18 web panel structure with `@@NAME@@` placeholders.
const TEMPLATE: &str = include_str!("webform/plain_gxdataarea.cs.tmpl");

/// The client script of a plain panel (KBTeste `liveimportwebpanel.js`, `testvldwpf5317a.js`).
const PLAIN_SCRIPT: &str = include_str!("webform/plain_gxdataarea.js.tmpl");

/// Standard variables every web panel declares. They are not user code and emit nothing.
const STANDARD_VARIABLES: [&str; 4] = ["Today", "Time", "Pgmname", "Pgmdesc"];

/// Explicit configuration for plain WebPanels. Every field is required; an unset field refuses every
/// plain panel and the refusal names the option.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct WebPanelEnvironment {
    /// The design-system theme the panel sets as its default (`--web-theme`).
    pub theme: Option<String>,
    /// The generator banner written into the panel metadata (`--web-generator`).
    pub generator: Option<String>,
    /// The runtime class path of each master page, by object name (`--web-master Name=dotted.path`).
    pub master_classes: BTreeMap<String, String>,
    /// The KB build number, from [`web_build_number_from_kb`].
    pub build_number: Option<String>,
    /// The KB's own generated helper `NetModel/Web/GxWebStd.cs` (see [`web_support_from_kb`]). Every
    /// plain panel calls it, so a KB without it refuses its panels.
    pub support_class: Option<String>,
    /// The design-system classes of the KB, by the reference a layout stores
    /// (`<DesignSystemClass type guid>-<entity id>`) to the class name (see
    /// `UdmClient::design_system_classes`). A layout naming a class outside it is refused.
    #[serde(default)]
    pub design_classes: BTreeMap<String, String>,
    /// The entity GUID of each Image, SDT, SDT level and Domain of the KB, by the reference a stored part uses
    /// (`<entity type guid>-<entity id>`; see `UdmClient::object_guids`).
    #[serde(default)]
    pub object_guids: BTreeMap<String, String>,
    /// The Attributes of the KB by declared name (see `UdmClient::attribute_facts`). A
    /// Transaction naming an attribute outside it is refused.
    #[serde(default)]
    pub attributes: BTreeMap<String, crate::udm::client::AttributeFacts>,
    /// The `TablePK` of each Table by name (see `UdmClient::table_keys`).
    #[serde(default)]
    pub table_keys: BTreeMap<String, u32>,
    /// The description of each attribute's subtype group by attribute name (see
    /// `UdmClient::subtype_groups`).
    #[serde(default)]
    pub subtype_groups: BTreeMap<String, String>,
}

/// A plain WebPanel whose class is fully determined by its stored shape and the configuration.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlainWebPanel {
    /// The object name, as declared (`GetPgmname`).
    pub name: String,
    /// The description (`GetPgmdesc` and the form caption).
    pub description: String,
    /// The three-digit form number (`entity id * 10 + 2`).
    pub form_number: String,
    /// The three-digit section number (`entity id * 10`).
    pub section_number: String,
    /// The number of rows of the `MainTable`; each row holds one empty cell.
    pub rows: usize,
    /// The master page's object name.
    pub master_name: String,
    /// The master page's runtime class path (`general.ui.masterunanimosidebar`).
    pub master_class: String,
    /// The default theme.
    pub theme: String,
    /// The generator banner.
    pub generator: String,
    /// The KB build number.
    pub build_number: String,
}

impl PlainWebPanel {
    /// The class name: the object name in lower case, as GeneXus names the generated class.
    pub fn class_name(&self) -> String {
        self.name.to_ascii_lowercase()
    }

    /// The generated file name.
    pub fn file_name(&self) -> String {
        format!("{}.cs", self.class_name())
    }

    /// The generated C# class. `stamp` is the generation token the SDK writes into the panel's
    /// script URLs (`?yyyyMMddHHmmss`); it is per generation, so the caller supplies it.
    pub fn emit(&self, stamp: &str) -> String {
        TEMPLATE
            .replace("@@SECTION@@\n", &section_block(self.rows))
            .replace("@@CLASS@@", &self.class_name())
            .replace("@@PGMNAME@@", &self.name)
            .replace("@@DESC@@", &self.description)
            .replace("@@MASTER@@", &self.master_class)
            .replace("@@GENERATOR@@", &self.generator)
            .replace("@@STAMP@@", stamp)
            .replace("@@THEME@@", &self.theme)
            .replace("@@BUILD@@", &self.build_number)
            .replace("@@F@@", &self.form_number)
            .replace("@@W@@", &self.section_number)
    }

    /// The client script the SDK writes beside the class (`liveimportwebpanel.js`), as (file name, text in
    /// the SDK's bytes): the `MainTable` control and one empty control per row and per cell.
    pub fn script(&self) -> Result<(String, String), String> {
        let class = self.class_name();
        let id = self.form_number[..2].to_ascii_lowercase();
        let last = 3 + 2 * self.rows;
        let ids: Vec<String> = (2..=last).map(|id| id.to_string()).collect();
        let mut controls = String::new();
        for control in 2..=last {
            let field = if control == 3 { "MAINTABLE" } else { "" };
            controls.push_str(&format!(
                "   GXValidFnc[{control}]={{ id: {control}, fld:\"{field}\",grid:0}};\n"
            ));
        }
        let text = PLAIN_SCRIPT
            .replace("@@CONTROLS@@\n", &controls)
            .replace("@@CLASS@@", &class)
            .replace("@@THEME@@", &self.theme)
            .replace("@@ID@@", &id)
            .replace("@@IDS@@", &ids.join(","))
            .replace("@@LAST@@", &last.to_string());
        Ok((format!("{class}.js"), text.replace('\n', "\r\n")))
    }
}

/// The section markup of the `WB` method: the `Section` div, the table div (`Table` when empty,
/// `TableGrid` otherwise), one `row` and one `col-xs-12` div per row, and the closing divs.
pub(super) fn section_block(rows: usize) -> String {
    let mut out = String::new();
    out.push_str(concat!(
        "            /* Div Control */\n",
        "            GxWebStd.gx_div_start( context, \"\", 1, 0, \"px\", 0, \"px\", \"Section\", \"start\", \"top\", \" \"+\"data-gx-base-lib=\\\"none\\\"\"+\" \"+\"data-abstract-form\"+\" \", \"\", \"div\");\n",
    ));
    let table = if rows == 0 { "Table" } else { "TableGrid" };
    out.push_str("            /* Div Control */\n");
    out.push_str(&format!(
        "            GxWebStd.gx_div_start( context, divMaintable_Internalname, 1, 0, \"px\", 0, \"px\", \"{table}\", \"start\", \"top\", \"\", \"\", \"div\");\n"
    ));
    for _ in 0..rows {
        out.push_str(concat!(
            "            /* Div Control */\n",
            "            GxWebStd.gx_div_start( context, \"\", 1, 0, \"px\", 0, \"px\", \"row\", \"start\", \"top\", \"\", \"\", \"div\");\n",
            "            /* Div Control */\n",
            "            GxWebStd.gx_div_start( context, \"\", 1, 0, \"px\", 0, \"px\", \"col-xs-12\", \"start\", \"top\", \"\", \"\", \"div\");\n",
            "            GxWebStd.gx_div_end( context, \"start\", \"top\", \"div\");\n",
            "            GxWebStd.gx_div_end( context, \"start\", \"top\", \"div\");\n",
        ));
    }
    out.push_str("            GxWebStd.gx_div_end( context, \"start\", \"top\", \"div\");\n");
    out.push_str("            GxWebStd.gx_div_end( context, \"start\", \"top\", \"div\");\n");
    out
}

/// Plans the emission of one WebPanel. `Err` is the refusal reason, naming what blocks it.
pub fn plan_plain_webpanel(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
) -> Result<PlainWebPanel, String> {
    if object.kind != ObjectKind::WebPanel {
        return Err("it is not a WebPanel".to_string());
    }
    let settings = settings_of(object)?;
    if let Some(id) = settings.master_page_id {
        // The KB stores a `MasterPage` link as a `Calls` edge (ReferenceType 1), so both kinds
        // count here. Only a single master among them is named; several stay unnamed.
        let masters = master_names(
            object,
            catalog,
            &[ReferenceKind::Uses, ReferenceKind::Calls],
        )?;
        return Err(match masters.as_slice() {
            [name] => format!(
                "it names master page '{name}' (MasterPage property {id}); a panel with an explicit MasterPage property is not emitted yet"
            ),
            _ => format!(
                "it names a master page (MasterPage property {id}) that its references do not resolve to exactly one master; a panel with an explicit MasterPage property is not emitted yet"
            ),
        });
    }
    match settings.web_comp.as_deref() {
        Some("Master") => {
            return Err(format!(
                "it is master page '{}' (WEB_COMP Master); master-page layouts are not emitted yet",
                settings.declared_name
            ));
        }
        Some(comp) => {
            return Err(format!(
                "it is a web component (WEB_COMP {comp}), not a plain panel; its class is not emitted yet"
            ));
        }
        None => {}
    }
    check_no_code(object)?;
    check_variables(object)?;
    let rows = check_layout(object)?;
    if env.support_class.is_none() {
        return Err(
            "its class calls the GxWebStd helper, which the KB does not provide (NetModel/Web/GxWebStd.cs is missing)"
                .to_string(),
        );
    }
    let name = settings.declared_name.clone();
    if !is_identifier(&name) {
        return Err(format!("its name '{name}' is not a C# identifier"));
    }
    let form_number = three_digits(settings.entity_id, 2)?;
    let section_number = three_digits(settings.entity_id, 0)?;
    let description = match settings.description {
        Some(description) => description,
        None => describe_name(&name)?,
    };
    if !literal_ok(&description) {
        return Err(format!(
            "its description '{description}' has characters the emitter is not matched for"
        ));
    }
    let (master_name, master_class) = master_of(object, catalog, env)?;
    let theme = configured(env.theme.as_deref(), "--web-theme", literal_ok)?;
    let generator = configured(env.generator.as_deref(), "--web-generator", literal_ok)?;
    let build_number = configured(
        env.build_number.as_deref(),
        "the build number in NetModel/Web/GXCFG.WEB",
        |value| value.chars().all(|c| c.is_ascii_digit()),
    )?;
    Ok(PlainWebPanel {
        name,
        description,
        form_number,
        section_number,
        rows,
        master_name,
        master_class,
        theme,
        generator,
        build_number,
    })
}

/// A configured value, which must be present and pass `check`. The reason names the option.
pub(crate) fn configured(
    value: Option<&str>,
    option: &str,
    check: impl Fn(&str) -> bool,
) -> Result<String, String> {
    let value = value
        .filter(|value| !value.is_empty())
        .ok_or_else(|| format!("not configured: {option} is required to emit a plain WebPanel"))?;
    if !check(value) {
        return Err(format!(
            "{option} '{value}' has characters the emitter is not matched for"
        ));
    }
    Ok(value.to_string())
}

/// The characters a stored or configured value may carry into a C# string literal and into the
/// generated text. Quotes, backslashes, `@` (the template placeholder marker) and `&` are refused.
pub fn literal_ok(value: &str) -> bool {
    !value.is_empty()
        && value.chars().all(|c| {
            c.is_ascii_alphanumeric()
                || matches!(c, ' ' | '.' | '_' | '-' | ',' | '(' | ')' | '/' | ':')
        })
}

pub(crate) fn is_identifier(name: &str) -> bool {
    let mut chars = name.chars();
    matches!(chars.next(), Some(c) if c.is_ascii_alphabetic() || c == '_')
        && chars.all(|c| c.is_ascii_alphanumeric() || c == '_')
}

pub(crate) fn settings_of(object: &KbObject) -> Result<WebPanelSettings, String> {
    let part = part(object, SETTINGS_PART)
        .ok_or_else(|| "its stored properties were not read".to_string())?;
    WebPanelSettings::from_part(part)
}

pub(super) fn part<'a>(object: &'a KbObject, name: &str) -> Option<&'a crate::catalog::SourcePart> {
    object.source_parts.iter().find(|part| part.name == name)
}

pub(crate) fn part_text<'a>(object: &'a KbObject, name: &str) -> Option<&'a str> {
    part(object, name).map(|part| part.source.as_str())
}

pub(super) fn check_no_code(object: &KbObject) -> Result<(), String> {
    for name in ["EVENTS", "RULES", "CONDITIONS"] {
        if part_text(object, name).is_some_and(|text| !text.trim().is_empty()) {
            return Err(format!(
                "it has {} code, which is not emitted yet",
                name.to_ascii_lowercase()
            ));
        }
    }
    Ok(())
}

fn check_variables(object: &KbObject) -> Result<(), String> {
    let Some(text) = part_text(object, "VARIABLES") else {
        return Ok(());
    };
    let document =
        roxmltree::Document::parse(text).map_err(|e| format!("its VARIABLES are not XML: {e}"))?;
    for node in document
        .root_element()
        .children()
        .filter(|n| n.is_element())
    {
        let name = node
            .children()
            .find(|c| c.has_tag_name("Properties"))
            .and_then(|props| {
                props
                    .children()
                    .find(|p| p.has_tag_name("Property") && child_text(*p, "Name") == Some("Name"))
            })
            .and_then(|p| child_text(p, "Value"))
            .or_else(|| node.attribute("Name"))
            .unwrap_or("?");
        let standard = node.has_tag_name("StandardVariable")
            && STANDARD_VARIABLES
                .iter()
                .any(|standard| standard.eq_ignore_ascii_case(name));
        if !standard {
            return Err(format!(
                "it declares variable '{name}', and user variables are not emitted yet"
            ));
        }
    }
    Ok(())
}

/// The three-character number the SDK gives the generated members: the entity id as two upper-case
/// base-36 digits, then `offset`. Matched on entity ids 1-4 (`010`-`042`), 67 (`1V0`) and 94 (`2M2`); an
/// id beyond two base-36 digits is refused.
pub(crate) fn three_digits(entity_id: i32, offset: i32) -> Result<String, String> {
    const DIGITS: &[u8; 36] = b"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    if !(0..36 * 36).contains(&entity_id) || !(0..10).contains(&offset) {
        return Err(format!(
            "entity id {entity_id} has no matched three-character member number"
        ));
    }
    let (high, low) = ((entity_id / 36) as usize, (entity_id % 36) as usize);
    Ok(format!(
        "{}{}{offset}",
        DIGITS[high] as char, DIGITS[low] as char
    ))
}

/// The description the SDK derives from the object name: the name split before each upper-case
/// letter that follows a lower-case letter (`TestVldWpf5317a` -> `Test Vld Wpf5317a`). Names with
/// consecutive capitals, a capital after a digit, or non-alphanumeric characters are refused, since
/// no SDK output has been matched for them.
pub fn describe_name(name: &str) -> Result<String, String> {
    if name.is_empty() || !name.chars().all(|c| c.is_ascii_alphanumeric()) {
        return Err(format!(
            "its name '{name}' has characters the description rule is not matched for"
        ));
    }
    let chars: Vec<char> = name.chars().collect();
    let mut out = String::new();
    for (index, c) in chars.iter().enumerate() {
        if index > 0 && c.is_ascii_uppercase() {
            let previous = chars[index - 1];
            if previous.is_ascii_lowercase() {
                out.push(' ');
            } else {
                return Err(format!(
                    "its name '{name}' has a capital after '{previous}', which the description rule is not matched for"
                ));
            }
        }
        out.push(*c);
    }
    Ok(out)
}

#[cfg(test)]
mod tests;
