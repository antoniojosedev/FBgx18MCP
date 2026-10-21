//! The class GeneXus 18 generates for the Unanimo design system's sidebar master page
//! (`General.UI.MasterUnanimoSidebar`): a header with the menu toggle and the logo, the
//! `GeneXusUnanimo.Sidebar` control fed by `General.UI.SidebarItemsDP`, and the content holder.
//!
//! The master is matched, not interpreted. Its events, rules, conditions, variables and layout must be
//! exactly the ones the design system writes. KB-local references (`<entity type>-<entity id>`) are
//! compared by what they name: a design-system class by its name, an image, an SDT or a domain by its
//! entity GUID, which a module's objects keep in every Knowledge Base. The name, description and
//! module are the only things read from the object; anything else is refused by name.
//!
//! The emitted class is `webform/unanimo_sidebar_master.cs.tmpl`, checked byte for byte (the build
//! stamp aside) against the SDK's output for the KBTeste master.

use crate::catalog::{Catalog, KbObject, ObjectKind};
use crate::csharp::template::{Fields, Template, Value};
use crate::csharp::webform::{
    ModuleScope, WebPanelEnvironment, is_identifier, literal_ok, part_text, settings_of,
    three_digits,
};

const TEMPLATE: &str = include_str!("webform/unanimo_sidebar_master.cs.tmpl");
/// The client script of the matched master (KBTeste `general/ui/masterunanimosidebar.js`).
const SCRIPT: &str = include_str!("webform/unanimo_sidebar_master.js.tmpl");
const LAYOUT: &str = include_str!("webform/unanimo_sidebar_master.layout.txt");
const VARIABLES: &str = include_str!("webform/unanimo_sidebar_master.variables.txt");

const EVENTS: &str = "Event Load
	//init sidebar properties
	SidebarMenu.Title = !\"Unanimo\"
	SidebarMenu.DistanceToTop = 60
	&sidebarItems = General.UI.SidebarItemsDP()
EndEvent

Event SidebarMenu.ItemClick
	&target = SidebarMenu.SelectedItemTarget
	Link(&target)
EndEvent

Event SidebarMenu.GetState
	if SidebarMenu.isCollapsed
		Content.Class = !\"expandible-container\" + !\" expanded\"
	else
		Content.Class = !\"expandible-container\"
	endif
EndEvent
";

/// The type GUID of a design-system class, as a layout names it.
const DESIGN_CLASS_TYPE: &str = "d52f9833-90c9-4096-8e42-27485741f78a";

/// A matched Unanimo sidebar master page, ready to emit.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UnanimoSidebarMaster {
    pub name: String,
    scope: Fields,
}

impl UnanimoSidebarMaster {
    pub fn file_name(&self) -> String {
        format!("{}.cs", self.name.to_ascii_lowercase())
    }

    /// The class text. `stamp` is the build stamp of its script references.
    pub fn emit(&self, stamp: &str) -> String {
        let mut scope = self.scope.clone();
        scope.insert("stamp".into(), stamp.into());
        Template::parse(TEMPLATE)
            .and_then(|template| template.render(&scope))
            .expect("the sidebar master template renders every scope the planner builds")
    }

    /// The client script the SDK writes beside the class (`masterunanimosidebar.js`), as (file name, text in the SDK's
    /// bytes): the matched shape's script, with its class path, theme and event ids.
    pub fn script(&self) -> Result<(String, String), String> {
        let mut scope = self.scope.clone();
        let Some(Value::Text(id)) = scope.get("id") else {
            return Err("its scope has no id".to_string());
        };
        let lid = id.to_ascii_lowercase();
        scope.insert("lid".into(), lid.into());
        Template::parse(SCRIPT)
            .and_then(|template| template.render(&scope))
            .map(|text| {
                (
                    format!("{}.js", self.name.to_ascii_lowercase()),
                    text.replace('\n', "\r\n"),
                )
            })
    }
}

/// Plans the Unanimo sidebar master, or says why the master page is not that one.
pub fn plan_unanimo_sidebar_master(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
) -> Result<UnanimoSidebarMaster, String> {
    if object.kind != ObjectKind::WebPanel {
        return Err("it is not a WebPanel".to_string());
    }
    let settings = settings_of(object)?;
    if settings.web_comp.as_deref() != Some("Master") || settings.master_page_id.is_some() {
        return Err("it is not a master page of its own".to_string());
    }
    let name = settings.declared_name.clone();
    if !is_identifier(&name) || !object.module_path.iter().all(|m| is_identifier(m)) {
        return Err(format!(
            "its name '{}' is not dotted C# names",
            ModuleScope::of(object).program_name
        ));
    }
    if normalized(part_text(object, "EVENTS").unwrap_or("")) != normalized(EVENTS) {
        return Err("its events are not the Unanimo sidebar master's".to_string());
    }
    for part in ["RULES", "CONDITIONS"] {
        if !part_text(object, part).unwrap_or("").trim().is_empty() {
            return Err(format!("it has {}", part.to_ascii_lowercase()));
        }
    }
    let layout = canonical(
        part_text(object, "WEBFORM").ok_or("it has no WEBFORM layout")?,
        env,
    )?;
    if normalized(&layout) != normalized(LAYOUT) {
        return Err("its layout is not the Unanimo sidebar master's".to_string());
    }
    let variables = canonical(part_text(object, "VARIABLES").unwrap_or(""), env)?;
    if normalized(&variables) != normalized(VARIABLES) {
        return Err("its variables are not the Unanimo sidebar master's".to_string());
    }
    // The Load event calls General.UI.SidebarItemsDP, which the class names by its namespace.
    let provider = catalog
        .objects()
        .filter(|o| {
            o.kind == ObjectKind::DataProvider
                && o.name.eq_ignore_ascii_case("SidebarItemsDP")
                && o.module_path.len() == 2
                && o.module_path[0].eq_ignore_ascii_case("General")
                && o.module_path[1].eq_ignore_ascii_case("UI")
        })
        .count();
    if provider != 1 {
        return Err(
            "its Load event calls General.UI.SidebarItemsDP, which the KB does not hold once"
                .to_string(),
        );
    }
    let description = settings
        .description
        .clone()
        .ok_or("it has no description")?;
    if !literal_ok(&description) {
        return Err(format!(
            "its description '{description}' has characters the emitter is not matched for"
        ));
    }
    let member = three_digits(settings.entity_id, 0)?;
    let module = ModuleScope::of(object);

    let mut scope = Fields::new();
    let mut text = |key: &str, value: String| {
        scope.insert(key.to_string(), Value::Text(value));
    };
    text("id", member[..2].to_string());
    text("class", name.to_ascii_lowercase());
    text("ns_suffix", module.namespace_suffix.clone());
    text("program_name", module.program_name.clone());
    text("help_path", module.program_name.replace('.', "/"));
    text("script_path", module.script_path.clone());
    text("desc", description);
    text("class_path", module.class_path.clone());
    // The client script names the theme; a run without `--web-theme` refuses the script by name.
    if let Some(theme) = env.theme.clone() {
        text("theme", theme);
    }
    Ok(UnanimoSidebarMaster { name, scope })
}

fn normalized(text: &str) -> String {
    text.split_whitespace().collect::<Vec<_>>().join(" ")
}

fn is_guid(text: &str) -> bool {
    text.len() == 36
        && text.char_indices().all(|(i, c)| match i {
            8 | 13 | 18 | 23 => c == '-',
            _ => c.is_ascii_hexdigit(),
        })
}

/// The stored text with its layout ids dropped and every KB-local reference replaced by what it
/// names: `class:<name>` for a design-system class, `guid:<entity guid>` for an image, an SDT or a
/// domain. Both reference forms are read: `<type guid>-<entity id>` and the escaped
/// `<Type>type guid</Type><Id>entity id</Id>` of an SDT type. An unresolved reference is an error.
pub fn canonical(text: &str, env: &WebPanelEnvironment) -> Result<String, String> {
    const ESCAPED_ID: &str = "&amp;lt;/Type&amp;gt;&amp;lt;Id&amp;gt;";
    let mut out = String::with_capacity(text.len());
    let mut rest = text;
    while !rest.is_empty() {
        if let Some(after) = rest.strip_prefix(" id=\"") {
            // A layout element id: dropped with its value.
            let end = after
                .find('"')
                .ok_or("a layout id attribute is not closed")?;
            rest = &after[end + 1..];
            continue;
        }
        if rest.len() >= 36 && rest.is_char_boundary(36) && is_guid(&rest[..36]) {
            let guid = &rest[..36];
            let tail = &rest[36..];
            let (digits_from, prefix_len) = if let Some(after) = tail.strip_prefix('-') {
                (after, 1)
            } else if let Some(after) = tail.strip_prefix(ESCAPED_ID) {
                (after, ESCAPED_ID.len())
            } else {
                ("", 0)
            };
            let digits = digits_from.chars().take_while(char::is_ascii_digit).count();
            if prefix_len > 0 && digits > 0 {
                let key = format!("{guid}-{}", &digits_from[..digits]);
                let resolved = if guid == DESIGN_CLASS_TYPE {
                    env.design_classes
                        .get(&key)
                        .map(|name| format!("class:{name}"))
                } else {
                    env.object_guids.get(&key).map(|id| format!("guid:{id}"))
                }
                .ok_or_else(|| format!("its reference '{key}' does not resolve in the KB"))?;
                out.push_str(&resolved);
                rest = &tail[prefix_len + digits..];
                continue;
            }
        }
        let c = rest.chars().next().expect("rest is not empty");
        out.push(c);
        rest = &rest[c.len_utf8()..];
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_template_parses() {
        Template::parse(TEMPLATE).expect("template parses");
    }

    #[test]
    fn references_resolve_by_what_they_name_and_ids_are_dropped() {
        let mut env = WebPanelEnvironment::default();
        env.design_classes
            .insert(format!("{DESIGN_CLASS_TYPE}-63"), "Content".into());
        env.object_guids.insert(
            "447527b5-9210-4523-898b-5dccb17be60a-58".into(),
            "e48ae171-a60a-4b3a-ab74-7b8a7ca37e42".into(),
        );
        let text = format!(
            "<t id=\"x-1\" class=\"{DESIGN_CLASS_TYPE}-63 Other\" v=\"&amp;lt;Type&amp;gt;447527b5-9210-4523-898b-5dccb17be60a&amp;lt;/Type&amp;gt;&amp;lt;Id&amp;gt;58&amp;lt;/Id&amp;gt;\"/>"
        );
        assert_eq!(
            canonical(&text, &env).unwrap(),
            "<t class=\"class:Content Other\" v=\"&amp;lt;Type&amp;gt;guid:e48ae171-a60a-4b3a-ab74-7b8a7ca37e42&amp;lt;/Id&amp;gt;\"/>"
        );
        let unresolved = format!("<t class=\"{DESIGN_CLASS_TYPE}-64\"/>");
        assert!(canonical(&unresolved, &env).unwrap_err().contains("-64"));
    }
}
