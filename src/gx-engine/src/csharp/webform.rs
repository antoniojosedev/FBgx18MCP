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
fn section_block(rows: usize) -> String {
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

fn part<'a>(object: &'a KbObject, name: &str) -> Option<&'a crate::catalog::SourcePart> {
    object.source_parts.iter().find(|part| part.name == name)
}

pub(crate) fn part_text<'a>(object: &'a KbObject, name: &str) -> Option<&'a str> {
    part(object, name).map(|part| part.source.as_str())
}

fn check_no_code(object: &KbObject) -> Result<(), String> {
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

/// Checks the stored `WEBFORM` is one `MainTable` whose rows hold one empty cell each, and returns
/// the row count. The table shape is matched as a pair: no class with zero rows, or a class with
/// three rows. Any other element, attribute or content refuses the layout.
fn check_layout(object: &KbObject) -> Result<usize, String> {
    let text =
        part_text(object, "WEBFORM").ok_or_else(|| "it has no WEBFORM layout".to_string())?;
    let document =
        roxmltree::Document::parse(text).map_err(|e| format!("its WEBFORM is not XML: {e}"))?;
    let root = document.root_element();
    expect(root, "GxMultiForm", &["rootId", "version"])?;
    let form = single_child(root, "Form")?;
    expect(form, "Form", &["id", "type"])?;
    if form.attribute("type") != Some("layout") {
        return Err("its form is not a layout form".to_string());
    }
    let detail = single_child(form, "detail")?;
    expect(detail, "detail", &["PanelType"])?;
    if detail.attribute("PanelType") != Some("Web") {
        return Err("its layout is not a web panel layout".to_string());
    }
    let layout = single_child(detail, "layout")?;
    expect(layout, "layout", &["id"])?;
    let table = single_child(layout, "table")?;
    expect(table, "table", &["id", "controlName", "tableType", "class"])?;
    if table.attribute("controlName") != Some("MainTable")
        || table.attribute("tableType") != Some("Responsive")
    {
        return Err("its table is not the responsive MainTable".to_string());
    }
    let mut rows = 0;
    for row in element_children(table) {
        expect(row, "row", &["id"])?;
        let cell = single_child(row, "cell")?;
        expect(cell, "cell", &["id"])?;
        if element_children(cell).next().is_some() || has_text(cell) {
            return Err("a table cell holds content, which is not emitted yet".to_string());
        }
        rows += 1;
    }
    if has_text(table) || has_text(layout) || has_text(detail) || has_text(form) {
        return Err("its layout holds text outside the table".to_string());
    }
    let has_class = table.attribute("class").is_some();
    match (rows, has_class) {
        (0, false) | (3, true) => Ok(rows),
        (rows, has_class) => Err(format!(
            "its table has {rows} row(s) and {} class, a shape no SDK output has been matched for",
            if has_class { "a" } else { "no" }
        )),
    }
}

fn child_text<'a>(node: roxmltree::Node<'a, '_>, tag: &str) -> Option<&'a str> {
    node.children()
        .find(|child| child.has_tag_name(tag))
        .map(|child| child.text().unwrap_or_default())
}

fn element_children<'a, 'input>(
    node: roxmltree::Node<'a, 'input>,
) -> impl Iterator<Item = roxmltree::Node<'a, 'input>> {
    node.children().filter(|child| child.is_element())
}

fn has_text(node: roxmltree::Node<'_, '_>) -> bool {
    node.children()
        .any(|child| child.is_text() && child.text().is_some_and(|t| !t.trim().is_empty()))
}

fn expect(node: roxmltree::Node<'_, '_>, tag: &str, attributes: &[&str]) -> Result<(), String> {
    if !node.has_tag_name(tag) {
        return Err(format!(
            "unexpected element '{}' where '{tag}' belongs",
            node.tag_name().name()
        ));
    }
    if let Some(attribute) = node
        .attributes()
        .find(|attribute| !attributes.contains(&attribute.name()))
    {
        return Err(format!(
            "'{tag}' has attribute '{}', which is not emitted yet",
            attribute.name()
        ));
    }
    Ok(())
}

fn single_child<'a, 'input>(
    node: roxmltree::Node<'a, 'input>,
    tag: &str,
) -> Result<roxmltree::Node<'a, 'input>, String> {
    let children: Vec<_> = element_children(node).collect();
    match children.as_slice() {
        [only] if only.has_tag_name(tag) => Ok(*only),
        _ => Err(format!(
            "'{}' must hold exactly one '{tag}' element",
            node.tag_name().name()
        )),
    }
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

/// Declared names of the master WebPanels (`WEB_COMP` `Master`) the object references with one of
/// `kinds`, sorted and without duplicates.
pub(crate) fn master_names(
    object: &KbObject,
    catalog: &Catalog,
    kinds: &[ReferenceKind],
) -> Result<Vec<String>, String> {
    let mut masters: Vec<String> = Vec::new();
    for reference in &object.references {
        if !kinds.contains(&reference.kind) {
            continue;
        }
        let Some(target) = catalog.get(&reference.target) else {
            continue;
        };
        if target.kind != ObjectKind::WebPanel {
            continue;
        }
        let target_settings = settings_of(target)?;
        if target_settings.web_comp.as_deref() == Some("Master") {
            masters.push(target_settings.declared_name);
        }
    }
    masters.sort();
    masters.dedup();
    Ok(masters)
}

/// The master page the panel uses: the one WebPanel with `WEB_COMP` `Master` among its `Uses`
/// references, and its runtime class path from the configuration.
fn master_of(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
) -> Result<(String, String), String> {
    let masters = master_names(object, catalog, &[ReferenceKind::Uses])?;
    let name = match masters.as_slice() {
        [only] => only.clone(),
        [] => {
            return Err(
                "the KB records no master page for it (no reference to a master WebPanel)"
                    .to_string(),
            );
        }
        many => {
            return Err(format!(
                "the KB records {} master pages for it ({}); one is required",
                many.len(),
                many.join(", ")
            ));
        }
    };
    let class = master_class_of(&name, catalog, env)?;
    Ok((name, class))
}

/// The runtime class path of master page `name`: the configured one (`--web-master`), else the
/// master's module path read from the KB.
pub(crate) fn master_class_of(
    name: &str,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
) -> Result<String, String> {
    let configured = env
        .master_classes
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(name))
        .map(|(_, class)| class.clone());
    // Without configuration, the class path is the master's module path, read from the KB.
    let derived = || {
        catalog
            .objects()
            .find(|candidate| {
                candidate.kind == ObjectKind::WebPanel
                    && candidate.name.eq_ignore_ascii_case(name)
                    && !candidate.module_path.is_empty()
            })
            .map(|master| ModuleScope::of(master).class_path)
    };
    let class = configured.or_else(derived).ok_or_else(|| {
        format!(
            "its master page '{name}' has no runtime class path configured (--web-master {name}=<dotted.class.path>)"
        )
    })?;
    let class = &class;
    if !class.split('.').all(is_identifier) {
        return Err(format!(
            "the runtime class path '{class}' of master page '{name}' is not a dotted C# name"
        ));
    }
    Ok(class.clone())
}

/// The master-page class template: the GeneXus 18 output for a master whose layout is one classed
/// `MainTable` holding the `ContentHolder`, with no code and no user variables
/// (`webform/master_contentholder.cs.tmpl`, checked against `General.UI.MasterPrompt`).
const MASTER_TEMPLATE: &str = include_str!("webform/master_contentholder.cs.tmpl");
/// The client script of the matched master page (KBTeste `general/ui/masterprompt.js`).
const MASTER_SCRIPT: &str = include_str!("webform/master_contentholder.js.tmpl");

/// Where an object's class lives, from the Modules that hold it (see `KbObject::module_path`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModuleScope {
    /// `GetPgmname`: the module names and the object name, dotted (`General.UI.MasterPrompt`).
    pub program_name: String,
    /// The runtime class path, lower case (`general.ui.masterprompt`).
    pub class_path: String,
    /// The namespace after `GeneXus.Programs` (`.general.ui`), empty under the root module.
    pub namespace_suffix: String,
    /// The generated script, relative to the web root (`general/ui/masterprompt.js`).
    pub script_path: String,
}

impl ModuleScope {
    pub fn of(object: &KbObject) -> Self {
        let lower: Vec<String> = object
            .module_path
            .iter()
            .map(|module| module.to_ascii_lowercase())
            .collect();
        let class = object.name.to_ascii_lowercase();
        let join = |separator: &str, last: &str| {
            lower
                .iter()
                .map(String::as_str)
                .chain([last])
                .collect::<Vec<_>>()
                .join(separator)
        };
        Self {
            program_name: object
                .module_path
                .iter()
                .map(String::as_str)
                .chain([object.name.as_str()])
                .collect::<Vec<_>>()
                .join("."),
            class_path: join(".", &class),
            namespace_suffix: lower.iter().map(|m| format!(".{m}")).collect(),
            script_path: join("/", &format!("{class}.js")),
        }
    }
}

/// A master page whose class is fully determined by its stored shape: one classed `MainTable`
/// whose single cell holds the `ContentHolder`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MasterPage {
    pub name: String,
    pub scope: ModuleScope,
    pub description: String,
    pub form_number: String,
    pub section_number: String,
    /// The `MainTable` class names, resolved from the design system and space-separated.
    pub table_class: String,
    /// The design-system theme its client script names (`--web-theme`), when configured.
    pub theme: Option<String>,
}

impl MasterPage {
    pub fn file_name(&self) -> String {
        format!("{}.cs", self.name.to_ascii_lowercase())
    }

    pub fn emit(&self, stamp: &str) -> String {
        MASTER_TEMPLATE
            .replace("@@STAMP@@", stamp)
            .replace("@@JSPATH@@", &self.scope.script_path)
            .replace("@@NSSUFFIX@@", &self.scope.namespace_suffix)
            .replace("@@PGMNAME@@", &self.scope.program_name)
            .replace("@@DESC@@", &self.description)
            .replace("@@TABLECLASS@@", &self.table_class)
            .replace("@@CLASS@@", &self.name.to_ascii_lowercase())
            .replace("@@F@@", &self.form_number)
            .replace("@@W@@", &self.section_number)
    }

    /// The client script the SDK writes beside the class (`masterprompt.js`), as (file name, text in the
    /// SDK's bytes). The shape is the matched one (`MainTable` and a content holder).
    pub fn script(&self) -> Result<(String, String), String> {
        let theme = self
            .theme
            .as_deref()
            .ok_or("its script names the theme, and --web-theme is not configured")?;
        let text = MASTER_SCRIPT
            .replace("@@CLASSPATH@@", &self.scope.class_path)
            .replace("@@THEME@@", theme)
            .replace("@@ID@@", &self.form_number[..2].to_ascii_lowercase());
        Ok((
            format!("{}.js", self.name.to_ascii_lowercase()),
            text.replace('\n', "\r\n"),
        ))
    }
}

/// One emitted web class.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WebClass {
    Plain(PlainWebPanel),
    Master(MasterPage),
    Prompt(crate::csharp::prompt::SelectionPrompt),
    NotAuthorized(crate::csharp::not_authorized::NotAuthorizedPanel),
    UnanimoMaster(crate::csharp::unanimo_master::UnanimoSidebarMaster),
    Transaction(crate::csharp::transaction_form::TransactionForm),
    WorkWith(crate::csharp::work_with::WorkWithClass),
}

impl WebClass {
    pub fn file_name(&self) -> String {
        match self {
            Self::Plain(panel) => panel.file_name(),
            Self::Master(master) => master.file_name(),
            Self::Prompt(prompt) => prompt.file_name(),
            Self::NotAuthorized(panel) => panel.file_name(),
            Self::UnanimoMaster(master) => master.file_name(),
            Self::Transaction(transaction) => transaction.file_name(),
            Self::WorkWith(class) => class.file_name(),
        }
    }

    /// The client script the SDK writes beside the class, as (file name, text).
    pub fn script(&self) -> Result<(String, String), String> {
        match self {
            Self::Plain(panel) => panel.script(),
            Self::Prompt(prompt) => prompt.script(),
            Self::Master(master) => master.script(),
            Self::NotAuthorized(panel) => panel.script(),
            Self::UnanimoMaster(master) => master.script(),
            Self::Transaction(transaction) => transaction.script(),
            Self::WorkWith(class) => class.script(),
        }
    }

    pub fn emit(&self, stamp: &str) -> String {
        match self {
            Self::Plain(panel) => panel.emit(stamp),
            Self::Master(master) => master.emit(stamp),
            Self::Prompt(prompt) => prompt.emit(stamp),
            Self::NotAuthorized(panel) => panel.emit(stamp),
            Self::UnanimoMaster(master) => master.emit(stamp),
            Self::Transaction(transaction) => transaction.emit(stamp),
            Self::WorkWith(class) => class.emit(stamp),
        }
    }
}

/// Plans one WebPanel: a master page in a matched shape, else a plain panel. `Err` names the reason.
pub fn plan_web_class(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
) -> Result<WebClass, String> {
    if object.kind == ObjectKind::WebPanel
        && settings_of(object)?.web_comp.as_deref() == Some("Master")
    {
        return match plan_master_page(object, env) {
            Ok(master) => Ok(WebClass::Master(master)),
            Err(reason) => {
                crate::csharp::unanimo_master::plan_unanimo_sidebar_master(object, catalog, env)
                    .map(WebClass::UnanimoMaster)
                    .map_err(|unanimo| {
                        format!("{reason}; as the Unanimo sidebar master: {unanimo}")
                    })
            }
        };
    }
    if object.kind == ObjectKind::WebPanel && settings_of(object)?.master_page_id.is_some() {
        let prompt_reason = match crate::csharp::prompt::plan_selection_prompt(object, catalog, env)
        {
            Ok(prompt) => return Ok(WebClass::Prompt(prompt)),
            Err(reason) => reason,
        };
        return Err(match plan_plain_webpanel(object, catalog, env) {
            Err(reason) => format!("{reason}; as a selection prompt: {prompt_reason}"),
            Ok(_) => prompt_reason,
        });
    }
    match plan_plain_webpanel(object, catalog, env) {
        Ok(panel) => Ok(WebClass::Plain(panel)),
        Err(plain_reason) => {
            let not_authorized =
                match crate::csharp::not_authorized::plan_not_authorized(object, catalog, env) {
                    Ok(panel) => return Ok(WebClass::NotAuthorized(panel)),
                    Err(reason) => reason,
                };
            crate::csharp::work_with::plan_work_with(object, catalog, env)
                .map(WebClass::WorkWith)
                .map_err(|reason| {
                    format!(
                        "{plain_reason}; as a not-authorized panel: {not_authorized}; as a Work With panel: {reason}"
                    )
                })
        }
    }
}

/// Plans a master page (`WEB_COMP` `Master`) whose layout is one classed `MainTable` holding the
/// `ContentHolder` in its single cell, with no code and no user variables. Any other master is refused.
pub fn plan_master_page(
    object: &KbObject,
    env: &WebPanelEnvironment,
) -> Result<MasterPage, String> {
    let settings = settings_of(object)?;
    if settings.web_comp.as_deref() != Some("Master") {
        return Err("it is not a master page".to_string());
    }
    if settings.master_page_id.is_some() {
        return Err("it is a master page that names a master page of its own".to_string());
    }
    check_no_code(object)?;
    // With no code and a layout of the ContentHolder alone, nothing references a user variable, and
    // the SDK emits none (`MasterPrompt` declares `IsAuthorized`; its class has no `AV` member). The
    // VARIABLES part must still be well-formed.
    if let Some(text) = part_text(object, "VARIABLES") {
        roxmltree::Document::parse(text).map_err(|e| format!("its VARIABLES are not XML: {e}"))?;
    }
    let class_refs = check_master_layout(object)?;
    let table_class = resolve_classes(&class_refs, env)?;
    let name = settings.declared_name.clone();
    if !is_identifier(&name) {
        return Err(format!("its name '{name}' is not a C# identifier"));
    }
    if !object
        .module_path
        .iter()
        .all(|module| is_identifier(module))
    {
        return Err(format!(
            "its module path '{}' is not dotted C# names",
            object.module_path.join(".")
        ));
    }
    let description = match settings.description {
        Some(description) => description,
        None => describe_name(&name)?,
    };
    if !literal_ok(&description) {
        return Err(format!(
            "its description '{description}' has characters the emitter is not matched for"
        ));
    }
    Ok(MasterPage {
        scope: ModuleScope::of(object),
        name,
        description,
        form_number: three_digits(settings.entity_id, 2)?,
        section_number: three_digits(settings.entity_id, 0)?,
        table_class,
        theme: env.theme.clone(),
    })
}

/// The design-system class names of the space-separated references, in order.
fn resolve_classes(references: &str, env: &WebPanelEnvironment) -> Result<String, String> {
    let names = references
        .split_whitespace()
        .map(|reference| {
            env.design_classes
                .get(reference)
                .filter(|name| literal_ok(name) && !name.contains(' '))
                .cloned()
                .ok_or_else(|| {
                    format!("its layout names design-system class '{reference}', which the KB does not resolve")
                })
        })
        .collect::<Result<Vec<_>, _>>()?;
    if names.is_empty() {
        return Err("its MainTable has no class".to_string());
    }
    Ok(names.join(" "))
}

/// Checks the stored `WEBFORM` of a master page is one classed responsive `MainTable` with one row,
/// one cell, and the `ContentHolder` alone in it. Returns the table's class references.
fn check_master_layout(object: &KbObject) -> Result<String, String> {
    let text =
        part_text(object, "WEBFORM").ok_or_else(|| "it has no WEBFORM layout".to_string())?;
    let document =
        roxmltree::Document::parse(text).map_err(|e| format!("its WEBFORM is not XML: {e}"))?;
    let root = document.root_element();
    expect(root, "GxMultiForm", &["rootId", "version"])?;
    let form = single_child(root, "Form")?;
    expect(form, "Form", &["id", "type"])?;
    if form.attribute("type") != Some("layout") {
        return Err("its form is not a layout form".to_string());
    }
    let detail = single_child(form, "detail")?;
    expect(detail, "detail", &["PanelType"])?;
    if detail.attribute("PanelType") != Some("Web") {
        return Err("its layout is not a web panel layout".to_string());
    }
    let layout = single_child(detail, "layout")?;
    expect(layout, "layout", &["id"])?;
    let table = single_child(layout, "table")?;
    expect(table, "table", &["id", "controlName", "tableType", "class"])?;
    if table.attribute("controlName") != Some("MainTable")
        || table.attribute("tableType") != Some("Responsive")
    {
        return Err("its table is not the responsive MainTable".to_string());
    }
    let row = single_child(table, "row")?;
    expect(row, "row", &["id"])?;
    let cell = single_child(row, "cell")?;
    expect(cell, "cell", &["id"])?;
    let holder = single_child(cell, "contentholder")?;
    expect(holder, "contentholder", &["controlName"])?;
    if holder.attribute("controlName") != Some("ContentHolder")
        || element_children(holder).next().is_some()
    {
        return Err("its content holder is not the plain ContentHolder".to_string());
    }
    for node in [holder, cell, row, table, layout, detail, form] {
        if has_text(node) {
            return Err("its layout holds text outside the controls".to_string());
        }
    }
    table
        .attribute("class")
        .map(str::to_string)
        .ok_or_else(|| "its MainTable has no class".to_string())
}

/// The KB build number, from `NetModel/Web/GXCFG.WEB`. `None` when the file or key is absent or the
/// value is not digits.
pub fn web_build_number_from_kb(kb_dir: &Path) -> Option<String> {
    let path = kb_dir.join("NetModel").join("Web").join("GXCFG.WEB");
    let text = std::fs::read_to_string(path).ok()?;
    let document = roxmltree::Document::parse(text.trim_start_matches('\u{feff}')).ok()?;
    let value = document
        .descendants()
        .find(|node| node.has_tag_name("add") && node.attribute("key") == Some("GX_BUILD_NUMBER"))?
        .attribute("value")?;
    (!value.is_empty() && value.chars().all(|c| c.is_ascii_digit())).then(|| value.to_string())
}

/// The web model's client configuration script (`gxcfg.js`), from the model settings in
/// `NetModel/Web/GXCFG.WEB`. The text is matched, byte for byte, against the SDK's script for KBTeste; a
/// setting it needs that is absent, or a value outside the matched ones, is refused by name.
pub fn web_client_config_from_kb(kb_dir: &Path) -> Result<String, String> {
    let path = kb_dir.join("NetModel").join("Web").join("GXCFG.WEB");
    let text = std::fs::read_to_string(&path)
        .map_err(|e| format!("the model settings {} are not read: {e}", path.display()))?;
    client_config_script(&text)
}

/// [`web_client_config_from_kb`] over the text of `GXCFG.WEB`.
pub fn client_config_script(settings: &str) -> Result<String, String> {
    let document = roxmltree::Document::parse(settings.trim_start_matches('\u{feff}'))
        .map_err(|e| format!("the model settings are not XML: {e}"))?;
    let setting = |key: &str| -> Result<&str, String> {
        document
            .descendants()
            .find(|node| node.has_tag_name("add") && node.attribute("key") == Some(key))
            .and_then(|node| node.attribute("value"))
            .ok_or_else(|| format!("the model settings have no {key}"))
    };
    let matched = |key: &str, allowed: &[&str]| -> Result<String, String> {
        let value = setting(key)?;
        if allowed.contains(&value) {
            Ok(value.to_string())
        } else {
            Err(format!(
                "the model setting {key}='{value}' is not one the script is matched for"
            ))
        }
    };
    let build = setting("GX_BUILD_NUMBER")?;
    if build.is_empty() || !build.chars().all(|c| c.is_ascii_digit()) {
        return Err(format!(
            "the model setting GX_BUILD_NUMBER='{build}' is not a number"
        ));
    }
    let language = matched("LANGUAGE", &["eng"])?;
    let date_format = matched("DateFormat", &["MDY", "DMY", "YMD"])?;
    let time_format = matched("TimeAmPmFormat", &["12", "24"])?;
    let year_limit = setting("YearLimit")?;
    let century = year_limit
        .strip_prefix("19")
        .filter(|y| y.len() == 2 && y.chars().all(|c| c.is_ascii_digit()))
        .ok_or_else(|| format!("the model setting YearLimit='{year_limit}' is not a 19xx year"))?;
    // The thousands separator is not a setting: the SDK writes ',' beside the '.' decimal point.
    let decimal = matched("DECIMAL_POINT", &["."])?;
    let storage_time_zone = matched("StorageTimeZone", &["0", "1"])?;
    matched("DocumentType", &["HTML5"])?;
    let lines = [
        "gx.setStaticDirectory(gx.text.chr(47));".to_string(),
        "gx.setBlankWhenEmptyDate( false );".to_string(),
        String::new(),
        format!("gx.gxBuild =  {build} ;"),
        "gx.gen.net = true;".to_string(),
        format!("gx.setLanguageCode( '{language}' );"),
        format!("gx.setDateFormat( \"{date_format}\" );"),
        format!("gx.setTimeFormat( {time_format} );"),
        format!("gx.setCenturyFirstYear( {century} );"),
        format!("gx.setDecimalPoint( '{decimal}' );"),
        "gx.setThousandSeparator( ',' );".to_string(),
        "gx.TabOnPrompSelect =  false ;".to_string(),
        format!("gx.StorageTimeZone =  {storage_time_zone} ;"),
        "gx.datepickerImage=[];".to_string(),
        "gx.blankImage=\"blank.jpg\";".to_string(),
        "gx.ascImage=\"asc.gif\";".to_string(),
        "gx.descImage=\"desc.gif\";".to_string(),
        "gx.expandImage=\"expand.gif\";".to_string(),
        "gx.collapseImage=\"collapse.gif\";".to_string(),
        "gx.downloadImage=\"download.gif\";".to_string(),
        "gx.indicatorImage=\"indicator.gif\";".to_string(),
        "gx.promptImage=\"prompt.gif\";".to_string(),
        "gx.myLocationImage=\"locateMe.gif\";".to_string(),
        "gx.resizeImage=\"resize.gif\";".to_string(),
        "gx.csv.stopOnError = false;".to_string(),
        "gx.csv.dismissSeconds = 0;".to_string(),
        "gx.csv.messagePosition = 'right';".to_string(),
        "gx.csv.overlap = true;".to_string(),
        "gx.csv.oneAtAtime = true;".to_string(),
        "gx.HTML5 = true;".to_string(),
    ];
    Ok(lines.iter().map(|line| format!("{line}\r\n")).collect())
}

/// The KB's generated helper class `NetModel/Web/GxWebStd.cs`, verbatim. `None` when the file is
/// absent or unreadable.
pub fn web_support_from_kb(kb_dir: &Path) -> Option<String> {
    let path = kb_dir.join("NetModel").join("Web").join("GxWebStd.cs");
    std::fs::read_to_string(path)
        .ok()
        .filter(|text| !text.is_empty())
}

/// The generation stamp for a Unix time, as `yyyyMMddHHmmss` (UTC).
pub fn stamp_from_unix_seconds(seconds: u64) -> String {
    let days = (seconds / 86_400) as i64;
    let secs_of_day = seconds % 86_400;
    // Civil date from days since 1970-01-01 (proleptic Gregorian).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!(
        "{year:04}{month:02}{day:02}{:02}{:02}{:02}",
        secs_of_day / 3600,
        (secs_of_day % 3600) / 60,
        secs_of_day % 60
    )
}

/// The generation stamp for the current time.
pub fn current_stamp() -> String {
    let seconds = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    stamp_from_unix_seconds(seconds)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::catalog::{Edit, KbObject, ObjectId, Reference, SourcePart};

    /// The stored layout and variables of the two plain panels in KBTeste (`LiveImportWebPanel`,
    /// entity 3, three rows; `TestVldWpf5317a`, entity 4, no rows), copied from the KB.
    const LIVEIMPORT_WEBFORM: &str =
        include_str!("../../tests/fixtures/webform/liveimportwebpanel.webform.xml");
    const TESTVLD_WEBFORM: &str =
        include_str!("../../tests/fixtures/webform/testvldwpf5317a.webform.xml");
    const STORED_VARIABLES: &str = include_str!("../../tests/fixtures/webform/plain.variables.xml");
    /// The SDK's generated classes for the same two panels.
    const LIVEIMPORT_SDK: &str = include_str!("../../tests/fixtures/webform/liveimportwebpanel.cs");
    const TESTVLD_SDK: &str = include_str!("../../tests/fixtures/webform/testvldwpf5317a.cs");

    /// Stands in for the KB's `GxWebStd.cs`; the emitter only checks its presence.
    const SUPPORT_SOURCE: &str = "namespace GeneXus.Programs { public class GxWebStd { } }";

    const LIVEIMPORT_STAMP: &str = "202610150405993";
    const TESTVLD_STAMP: &str = "202610150405997";

    fn id(value: &str) -> ObjectId {
        ObjectId::new(value).unwrap()
    }

    fn settings_part(entity_id: i32, declared: &str, web_comp: Option<&str>) -> SourcePart {
        WebPanelSettings {
            entity_id,
            declared_name: declared.into(),
            description: None,
            master_page_id: None,
            web_comp: web_comp.map(str::to_string),
        }
        .to_part()
        .unwrap()
    }

    fn master_object() -> KbObject {
        let mut master = KbObject::new(
            id("masterunanimosidebar"),
            "MasterUnanimoSidebar",
            ObjectKind::WebPanel,
        );
        master
            .source_parts
            .push(settings_part(1, "MasterUnanimoSidebar", Some("Master")));
        master
    }

    /// A plain panel `name` with entity `entity_id`, its stored `webform`, and a `Uses` reference to
    /// the master.
    fn panel(name: &str, entity_id: i32, webform: &str) -> KbObject {
        let mut object = KbObject::new(id(&name.to_ascii_lowercase()), name, ObjectKind::WebPanel);
        object.source_parts.push(SourcePart {
            name: "WEBFORM".into(),
            source: webform.into(),
        });
        object.source_parts.push(SourcePart {
            name: "VARIABLES".into(),
            source: STORED_VARIABLES.into(),
        });
        object
            .source_parts
            .push(settings_part(entity_id, name, None));
        object.references.push(Reference {
            kind: ReferenceKind::Uses,
            target: id("masterunanimosidebar"),
        });
        object
    }

    /// A catalog holding `objects`, applied once.
    fn catalog_of(objects: Vec<KbObject>) -> Catalog {
        let mut catalog = Catalog::empty();
        let edits: Vec<Edit> = objects.into_iter().map(Edit::Upsert).collect();
        catalog.apply(0, &edits).expect("catalog setup");
        catalog
    }

    fn env() -> WebPanelEnvironment {
        let mut master_classes = BTreeMap::new();
        master_classes.insert(
            "MasterUnanimoSidebar".to_string(),
            "general.ui.masterunanimosidebar".to_string(),
        );
        WebPanelEnvironment {
            theme: Some("KBTeste".into()),
            generator: Some("GeneXus .NET 18_0_10-184260".into()),
            master_classes,
            build_number: Some("239440".into()),
            support_class: Some(SUPPORT_SOURCE.into()),
            design_classes: BTreeMap::new(),
            object_guids: BTreeMap::new(),
            attributes: BTreeMap::new(),
            table_keys: BTreeMap::new(),
            subtype_groups: BTreeMap::new(),
        }
    }

    fn plan_in(
        panel: &KbObject,
        objects: Vec<KbObject>,
        env: &WebPanelEnvironment,
    ) -> Result<PlainWebPanel, String> {
        let mut all = objects;
        all.push(panel.clone());
        plan_plain_webpanel(panel, &catalog_of(all), env)
    }

    fn plan(panel: &KbObject, env: &WebPanelEnvironment) -> Result<PlainWebPanel, String> {
        plan_in(panel, vec![master_object()], env)
    }

    fn liveimport() -> KbObject {
        panel("LiveImportWebPanel", 3, LIVEIMPORT_WEBFORM)
    }

    fn testvld() -> KbObject {
        panel("TestVldWpf5317a", 4, TESTVLD_WEBFORM)
    }

    #[test]
    fn liveimport_matches_the_sdk_output_byte_for_byte() {
        let plan = plan(&liveimport(), &env()).expect("plain panel plans");
        assert_eq!(plan.rows, 3);
        assert_eq!(plan.form_number, "032");
        assert_eq!(plan.section_number, "030");
        assert_eq!(plan.description, "Live Import Web Panel");
        assert_eq!(plan.emit(LIVEIMPORT_STAMP), LIVEIMPORT_SDK);
        assert_eq!(plan.file_name(), "liveimportwebpanel.cs");
    }

    #[test]
    fn testvld_matches_the_sdk_output_byte_for_byte() {
        let plan = plan(&testvld(), &env()).expect("plain panel plans");
        assert_eq!(plan.rows, 0);
        assert_eq!(plan.form_number, "042");
        assert_eq!(plan.section_number, "040");
        assert_eq!(plan.description, "Test Vld Wpf5317a");
        assert_eq!(plan.emit(TESTVLD_STAMP), TESTVLD_SDK);
    }

    #[test]
    fn emitted_text_has_no_placeholder_left() {
        let code = plan(&liveimport(), &env()).unwrap().emit(LIVEIMPORT_STAMP);
        assert!(!code.contains("@@"), "placeholder left in the output");
    }

    #[test]
    fn stored_description_replaces_the_name_split_when_set() {
        let mut object = liveimport();
        object.source_parts.retain(|p| p.name != SETTINGS_PART);
        object.source_parts.push(
            WebPanelSettings {
                entity_id: 3,
                declared_name: "LiveImportWebPanel".into(),
                description: Some("Import Demo".into()),
                master_page_id: None,
                web_comp: None,
            }
            .to_part()
            .unwrap(),
        );
        let plan = plan(&object, &env()).unwrap();
        assert_eq!(plan.description, "Import Demo");
        assert!(
            plan.emit(LIVEIMPORT_STAMP)
                .contains("return \"Import Demo\" ;")
        );
    }

    #[test]
    fn a_stored_description_with_quotes_or_placeholders_is_refused() {
        for bad in ["Quote \" here", "Inject @@F@@", "Amp & co"] {
            let mut object = liveimport();
            object.source_parts.retain(|p| p.name != SETTINGS_PART);
            object.source_parts.push(
                WebPanelSettings {
                    entity_id: 3,
                    declared_name: "LiveImportWebPanel".into(),
                    description: Some(bad.into()),
                    master_page_id: None,
                    web_comp: None,
                }
                .to_part()
                .unwrap(),
            );
            let reason = plan(&object, &env()).unwrap_err();
            assert!(reason.contains("not matched"), "{bad}: {reason}");
        }
    }

    #[test]
    fn description_splits_camel_case_and_refuses_unmatched_shapes() {
        assert_eq!(
            describe_name("LiveImportWebPanel").unwrap(),
            "Live Import Web Panel"
        );
        assert_eq!(
            describe_name("TestVldWpf5317a").unwrap(),
            "Test Vld Wpf5317a"
        );
        assert!(describe_name("WWTrn353").is_err(), "consecutive capitals");
        assert!(
            describe_name("Trn353General").is_err(),
            "digit before capital"
        );
        assert!(describe_name("With_Underscore").is_err());
    }

    #[test]
    fn member_numbers_follow_the_entity_id() {
        assert_eq!(three_digits(3, 2).unwrap(), "032");
        assert_eq!(three_digits(4, 2).unwrap(), "042");
        assert_eq!(three_digits(1, 0).unwrap(), "010");
        assert_eq!(three_digits(3, 0).unwrap(), "030");
        assert_eq!(three_digits(67, 0).unwrap(), "1V0", "Trn353General (WB1V0)");
        assert_eq!(three_digits(94, 2).unwrap(), "2M2", "Gx0010 (WS2M2)");
        assert!(
            three_digits(36 * 36, 2).is_err(),
            "no evidence beyond two base-36 digits"
        );
    }

    #[test]
    fn stamp_formats_the_civil_time() {
        assert_eq!(stamp_from_unix_seconds(1_700_000_000), "20231114221320");
        assert_eq!(stamp_from_unix_seconds(0), "19700101000000");
    }

    #[test]
    fn build_number_is_read_from_gxcfg() {
        let dir = std::env::temp_dir().join(format!("gx_webform_cfg_{}", std::process::id()));
        let web = dir.join("NetModel").join("Web");
        std::fs::create_dir_all(&web).unwrap();
        std::fs::write(
            web.join("GXCFG.WEB"),
            "<configuration><appSettings><add  key=\"GX_BUILD_NUMBER\" value=\"239440\"/></appSettings></configuration>",
        )
        .unwrap();
        assert_eq!(web_build_number_from_kb(&dir).as_deref(), Some("239440"));
        std::fs::remove_dir_all(&dir).ok();
        assert_eq!(web_build_number_from_kb(&dir), None);
    }

    #[test]
    fn unset_configuration_refuses_each_plain_panel_naming_the_option() {
        let base = env();
        let no_theme = WebPanelEnvironment {
            theme: None,
            ..base.clone()
        };
        assert!(
            plan(&liveimport(), &no_theme)
                .unwrap_err()
                .contains("--web-theme")
        );
        let no_generator = WebPanelEnvironment {
            generator: None,
            ..base.clone()
        };
        assert!(
            plan(&liveimport(), &no_generator)
                .unwrap_err()
                .contains("--web-generator")
        );
        let no_master = WebPanelEnvironment {
            master_classes: BTreeMap::new(),
            ..base.clone()
        };
        let reason = plan(&liveimport(), &no_master).unwrap_err();
        assert!(
            reason.contains("--web-master MasterUnanimoSidebar="),
            "{reason}"
        );
        let no_build = WebPanelEnvironment {
            build_number: None,
            ..base
        };
        assert!(
            plan(&liveimport(), &no_build)
                .unwrap_err()
                .contains("GXCFG")
        );
    }

    #[test]
    fn a_configured_value_with_unmatched_characters_is_refused() {
        let bad_theme = WebPanelEnvironment {
            theme: Some("Theme\"x".into()),
            ..env()
        };
        assert!(
            plan(&liveimport(), &bad_theme)
                .unwrap_err()
                .contains("--web-theme")
        );
        let bad_class = WebPanelEnvironment {
            master_classes: BTreeMap::from([(
                "MasterUnanimoSidebar".to_string(),
                "general.ui-master".to_string(),
            )]),
            ..env()
        };
        assert!(
            plan(&liveimport(), &bad_class)
                .unwrap_err()
                .contains("not a dotted C# name")
        );
    }

    #[test]
    fn a_panel_with_no_recorded_master_or_two_masters_is_refused() {
        let mut no_reference = liveimport();
        no_reference.references.clear();
        assert!(
            plan(&no_reference, &env())
                .unwrap_err()
                .contains("records no master page")
        );

        let mut second = KbObject::new(id("othermaster"), "OtherMaster", ObjectKind::WebPanel);
        second
            .source_parts
            .push(settings_part(9, "OtherMaster", Some("Master")));
        let mut two = liveimport();
        two.references.push(Reference {
            kind: ReferenceKind::Uses,
            target: id("othermaster"),
        });
        let reason = plan_in(&two, vec![master_object(), second], &env()).unwrap_err();
        assert!(reason.contains("2 master pages"), "{reason}");
    }

    #[test]
    fn explicit_master_page_and_master_layouts_are_refused() {
        let mut explicit = liveimport();
        explicit.source_parts.retain(|p| p.name != SETTINGS_PART);
        explicit.source_parts.push(
            WebPanelSettings {
                entity_id: 3,
                declared_name: "LiveImportWebPanel".into(),
                description: None,
                master_page_id: Some(2),
                web_comp: None,
            }
            .to_part()
            .unwrap(),
        );
        assert!(
            plan(&explicit, &env())
                .unwrap_err()
                .contains("MasterPage property 2")
        );

        let mut master = master_object();
        master.source_parts.retain(|p| p.name != SETTINGS_PART);
        master
            .source_parts
            .push(settings_part(1, "MasterUnanimoSidebar", Some("Master")));
        let reason = plan(&master, &env()).unwrap_err();
        assert!(
            reason.contains("master page 'MasterUnanimoSidebar'"),
            "{reason}"
        );
    }

    #[test]
    fn a_master_page_reference_is_refused_by_the_name_of_the_master_it_resolves_to() {
        let mut panel = liveimport();
        panel.references.clear();
        panel.source_parts.retain(|p| p.name != SETTINGS_PART);
        panel.source_parts.push(
            WebPanelSettings {
                entity_id: 3,
                declared_name: "LiveImportWebPanel".into(),
                description: None,
                master_page_id: Some(2),
                web_comp: None,
            }
            .to_part()
            .unwrap(),
        );
        let mut prompt = KbObject::new(id("masterprompt"), "MASTERPROMPT", ObjectKind::WebPanel);
        prompt
            .source_parts
            .push(settings_part(2, "MasterPrompt", Some("Master")));
        panel.references.push(Reference {
            kind: ReferenceKind::Calls,
            target: id("masterprompt"),
        });
        let reason = plan_in(&panel, vec![prompt], &env()).unwrap_err();
        assert!(
            reason.contains("names master page 'MasterPrompt' (MasterPage property 2)"),
            "{reason}"
        );
    }

    #[test]
    fn a_master_page_property_that_resolves_to_no_single_master_is_refused_without_a_guessed_name()
    {
        let mut panel = liveimport();
        panel.source_parts.retain(|p| p.name != SETTINGS_PART);
        panel.source_parts.push(
            WebPanelSettings {
                entity_id: 3,
                declared_name: "LiveImportWebPanel".into(),
                description: None,
                master_page_id: Some(2),
                web_comp: None,
            }
            .to_part()
            .unwrap(),
        );
        let mut second = KbObject::new(id("othermaster"), "OtherMaster", ObjectKind::WebPanel);
        second
            .source_parts
            .push(settings_part(9, "OtherMaster", Some("Master")));
        panel.references.push(Reference {
            kind: ReferenceKind::Uses,
            target: id("othermaster"),
        });
        let reason = plan_in(&panel, vec![master_object(), second], &env()).unwrap_err();
        assert!(
            reason.contains("names a master page (MasterPage property 2)"),
            "{reason}"
        );
        assert!(!reason.contains("'"), "no name may be guessed: {reason}");
    }

    #[test]
    fn code_user_variables_and_unmatched_layouts_are_refused() {
        let mut with_events = liveimport();
        with_events.source_parts.push(SourcePart {
            name: "EVENTS".into(),
            source: "event Start\n  Msg('x')\nendevent".into(),
        });
        assert!(plan(&with_events, &env()).unwrap_err().contains("events"));

        let mut user_variable = liveimport();
        user_variable.source_parts.retain(|p| p.name != "VARIABLES");
        user_variable.source_parts.push(SourcePart {
            name: "VARIABLES".into(),
            source: r#"<Variables><Variable Id="9" Name="&amp;Customer"/></Variables>"#.into(),
        });
        assert!(
            plan(&user_variable, &env())
                .unwrap_err()
                .contains("&Customer")
        );

        let with_content = LIVEIMPORT_WEBFORM.replacen(
            "<cell id=\"ef4dd512-4236-4fe7-a0fe-cd36ace476d0\" />",
            "<cell id=\"ef4dd512-4236-4fe7-a0fe-cd36ace476d0\"><control/></cell>",
            1,
        );
        assert!(
            plan(&panel("LiveImportWebPanel", 3, &with_content), &env())
                .unwrap_err()
                .contains("content")
        );
    }

    #[test]
    fn a_web_component_is_refused_as_a_component_not_as_a_master() {
        let mut component = liveimport();
        component.source_parts.retain(|p| p.name != SETTINGS_PART);
        component
            .source_parts
            .push(settings_part(3, "LiveImportWebPanel", Some("Yes")));
        let reason = plan(&component, &env()).unwrap_err();
        assert!(reason.contains("web component (WEB_COMP Yes)"), "{reason}");
        assert!(!reason.contains("master page"), "{reason}");
    }

    #[test]
    fn a_kb_without_the_gxwebstd_helper_refuses_the_panel_naming_it() {
        let no_helper = WebPanelEnvironment {
            support_class: None,
            ..env()
        };
        let reason = plan(&liveimport(), &no_helper).unwrap_err();
        assert!(reason.contains("GxWebStd"), "{reason}");
    }

    #[test]
    fn a_table_shape_outside_the_matched_pairs_is_refused() {
        // Three rows without a class: the class and the row count are matched as a pair.
        let without_class = LIVEIMPORT_WEBFORM.replacen(
            " class=\"d52f9833-90c9-4096-8e42-27485741f78a-453\"",
            "",
            1,
        );
        let reason = plan(&panel("LiveImportWebPanel", 3, &without_class), &env()).unwrap_err();
        assert!(
            reason.contains("no SDK output has been matched"),
            "{reason}"
        );

        // One row, with a class.
        let one_row = LIVEIMPORT_WEBFORM
            .replacen(
                "<row id=\"5666d13a-bfa8-4178-bf8b-8b99c6376ded\">\n            <cell id=\"7a8a62c3-3f73-454f-a120-5bb4eac4ff32\" />\n          </row>",
                "",
                1,
            )
            .replacen(
                "<row id=\"d6b5d597-11e4-474f-84ae-e1554e316f04\">\n            <cell id=\"13733249-9693-4d36-8cac-6b15df249f02\" />\n          </row>",
                "",
                1,
            );
        assert_ne!(one_row, LIVEIMPORT_WEBFORM, "the fixture edit applied");
        let reason = plan(&panel("LiveImportWebPanel", 3, &one_row), &env()).unwrap_err();
        assert!(reason.contains("1 row(s)"), "{reason}");
    }

    /// Mutation guard: the emitted section must keep the close-divs that balance the open ones. A
    /// change to `section_block` that drops one close fails this test.
    #[test]
    fn section_divs_are_balanced_for_every_matched_row_count() {
        for rows in [0, 3] {
            let block = section_block(rows);
            let opens = block.matches("gx_div_start(").count();
            let closes = block.matches("gx_div_end(").count();
            assert_eq!(opens, closes, "rows={rows}");
        }
    }
}
