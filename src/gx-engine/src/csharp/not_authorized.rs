//! The class GeneXus 18 generates for the security module's "not authorized" WebPanel
//! (`General.Security.NotAuthorized`): a panel taking the denied object's name, whose layout is the
//! classic header, title and error viewer, and whose only code is a `Start` event showing one
//! message.
//!
//! The panel is matched, not interpreted. Its rules, events, conditions, variables and layout must
//! be exactly that shape; the title, the message, the description, the name and the module are the
//! only things read from it. Anything else is refused by name.
//!
//! The emitted class is `webform/not_authorized.cs.tmpl`, checked byte for byte (the build stamp
//! aside) against the SDK's output for the KBTeste panel.

use crate::catalog::{Catalog, KbObject, ObjectKind, ReferenceKind};
use crate::csharp::template::{Fields, Template, Value};
use crate::csharp::webform::{
    ModuleScope, WebPanelEnvironment, configured, is_identifier, literal_ok, master_class_of,
    master_names, part_text, settings_of, three_digits,
};

const TEMPLATE: &str = include_str!("webform/not_authorized.cs.tmpl");
/// The client script of the matched panel (KBTeste `general/security/notauthorized.js`).
const SCRIPT: &str = include_str!("webform/not_authorized.js.tmpl");

/// The type GUID of a Domain, as an `idBasedOn` names it.
const DOMAIN_TYPE: &str = "00972a17-9975-449e-aab1-d26165d51393";

/// The layout the security module writes, with `@@title@@` for its title.
const LAYOUT: &str = r#"<BODY class="Form" bottomMargin="0" leftMargin="0" topMargin="0" rightMargin="0" classref="">
  <gxWebComponent ControlName="HeaderBC" />
  <TABLE id="Table1" class="ViewTable" classref="">
    <TBODY>
      <TR>
        <TD colSpan="2">
          <gxTextBlock Class="" Caption="@@title@@" CaptionExpression="&lt;Tokens&gt;&lt;Token&gt;&lt;Type&gt;Constant&lt;/Type&gt;&lt;Data&gt;&lt;![CDATA[@@title@@]]&gt;&lt;/Data&gt;&lt;/Token&gt;&lt;/Tokens&gt;" id="TitleText" />
        </TD>
      </TR>
      <TR>
        <TD colSpan="2">
          <gxErrorViewer ControlName="ErrViewer1" />
        </TD>
      </TR>
      <TR>
        <TD colSpan="2" />
      </TR>
    </TBODY>
  </TABLE>
  <TABLE id="Table2" class="HorizontalSpace" classref="">
    <TBODY>
      <TR>
        <TD />
      </TR>
    </TBODY>
  </TABLE>
  <TABLE id="Table3" class="TableHorizontalLine" classref="">
    <TBODY>
      <TR>
        <TD />
      </TR>
    </TBODY>
  </TABLE>
</BODY>"#;

/// A matched "not authorized" panel, ready to emit.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NotAuthorizedPanel {
    pub name: String,
    scope: Fields,
}

impl NotAuthorizedPanel {
    pub fn file_name(&self) -> String {
        format!("{}.cs", self.name.to_ascii_lowercase())
    }

    /// The class text. `stamp` is the build stamp of its script references.
    pub fn emit(&self, stamp: &str) -> String {
        let mut scope = self.scope.clone();
        scope.insert("stamp".into(), stamp.into());
        Template::parse(TEMPLATE)
            .and_then(|template| template.render(&scope))
            .expect("the not-authorized template renders every scope the planner builds")
    }

    /// The client script the SDK writes beside the class (`notauthorized.js`), as (file name, text in the SDK's
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

/// Plans a "not authorized" panel, or says why the WebPanel is not one this emitter matches.
pub fn plan_not_authorized(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
) -> Result<NotAuthorizedPanel, String> {
    if object.kind != ObjectKind::WebPanel {
        return Err("it is not a WebPanel".to_string());
    }
    let settings = settings_of(object)?;
    if settings.web_comp.is_some() {
        return Err("it is a web component or a master page".to_string());
    }
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
    if normalized(part_text(object, "RULES").unwrap_or("")) != "parm(in:&GxObject);" {
        return Err("its rules are not the single parm(in:&GxObject)".to_string());
    }
    if !part_text(object, "CONDITIONS")
        .unwrap_or("")
        .trim()
        .is_empty()
    {
        return Err("it has conditions".to_string());
    }
    let message = start_message(part_text(object, "EVENTS").unwrap_or(""))?;
    check_variables(part_text(object, "VARIABLES").unwrap_or(""))?;
    let title = layout_title(part_text(object, "WEBFORM").ok_or("it has no WEBFORM layout")?)?;
    let description = settings
        .description
        .clone()
        .ok_or("it has no description")?;
    for (what, value) in [
        ("title", &title),
        ("message", &message),
        ("description", &description),
    ] {
        if !literal_ok(value) {
            return Err(format!(
                "its {what} '{value}' has characters the emitter is not matched for"
            ));
        }
    }

    let masters = master_names(
        object,
        catalog,
        &[ReferenceKind::Uses, ReferenceKind::Calls],
    )?;
    let master = match masters.as_slice() {
        [only] => only.clone(),
        _ => return Err("it does not use exactly one master page".to_string()),
    };
    let master_class = master_class_of(&master, catalog, env)?;
    let theme = configured(env.theme.as_deref(), "--web-theme", literal_ok)?;
    let generator = configured(env.generator.as_deref(), "--web-generator", literal_ok)?;
    let build = configured(
        env.build_number.as_deref(),
        "the build number in NetModel/Web/GXCFG.WEB",
        |value| value.chars().all(|c| c.is_ascii_digit()),
    )?;
    if env.support_class.is_none() {
        return Err(
            "its class calls the GxWebStd helper, which the KB does not provide (NetModel/Web/GxWebStd.cs is missing)"
                .to_string(),
        );
    }
    let member = three_digits(settings.entity_id, 0)?;
    let module = ModuleScope::of(object);

    let mut scope = Fields::new();
    let mut text = |key: &str, value: String| {
        scope.insert(key.to_string(), Value::Text(value));
    };
    text("id", member[..2].to_string());
    text("class", name.to_ascii_lowercase());
    text("class_path", module.class_path.clone());
    text("ns_suffix", module.namespace_suffix.clone());
    text("program_name", module.program_name.clone());
    text("help_path", module.program_name.replace('.', "/"));
    text("script_path", module.script_path.clone());
    text("desc", description);
    text("title", title);
    text("message", message);
    text("theme", theme);
    text("generator", generator);
    text("build", build);
    text("master_class", master_class);
    Ok(NotAuthorizedPanel { name, scope })
}

fn normalized(text: &str) -> String {
    text.split_whitespace().collect::<Vec<_>>().join(" ")
}

/// The message of the only event, `Start`, which shows it and does nothing else. Line comments
/// are not code.
fn start_message(events: &str) -> Result<String, String> {
    let code: Vec<&str> = events
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty() && !line.starts_with("//"))
        .collect();
    let [start, call, end] = code.as_slice() else {
        return Err("its events are not a Start event showing one message".to_string());
    };
    let message = call
        .strip_prefix("Msg('")
        .and_then(|rest| rest.strip_suffix("')"))
        .filter(|message| !message.contains('\''));
    match message {
        Some(message)
            if start.eq_ignore_ascii_case("Event Start")
                && end.eq_ignore_ascii_case("EndEvent") =>
        {
            Ok(message.to_string())
        }
        _ => Err("its events are not a Start event showing one message".to_string()),
    }
}

/// The variables are the four standard ones and `GxObject`, based on a Domain.
fn check_variables(text: &str) -> Result<(), String> {
    let document =
        roxmltree::Document::parse(text).map_err(|e| format!("its VARIABLES are not XML: {e}"))?;
    let mut found = Vec::new();
    for node in document
        .root_element()
        .children()
        .filter(|n| n.is_element())
    {
        let property = |wanted: &str| {
            node.descendants()
                .filter(|n| n.has_tag_name("Property"))
                .find(|p| {
                    p.children()
                        .find(|c| c.has_tag_name("Name"))
                        .and_then(|c| c.text())
                        == Some(wanted)
                })
                .and_then(|p| p.children().find(|c| c.has_tag_name("Value")))
                .and_then(|v| v.text())
                .map(str::to_string)
        };
        let based_on = property("idBasedOn");
        found.push((
            node.tag_name().name().to_string(),
            node.attribute("Id").unwrap_or("").to_string(),
            node.attribute("Name").unwrap_or("").to_string(),
            based_on,
        ));
    }
    found.sort_by_key(|(_, id, _, _)| id.parse::<u32>().unwrap_or(u32::MAX));
    let standard = ["Today", "Time", "Pgmname", "Pgmdesc"];
    let shape_ok =
        found.len() == 5
            && found[..4].iter().zip(standard).enumerate().all(
                |(i, ((tag, id, name, _), wanted))| {
                    tag == "StandardVariable" && *id == (i + 1).to_string() && name == wanted
                },
            )
            && found[4].0 == "Variable"
            && found[4].1 == "5"
            && found[4].2 == "GxObject"
            && found[4]
                .3
                .as_deref()
                .is_some_and(|based_on| based_on.starts_with(&format!("{DOMAIN_TYPE}-")));
    if shape_ok {
        Ok(())
    } else {
        Err("its variables are not the standard ones and GxObject based on a Domain".to_string())
    }
}

/// The title of the layout, which must otherwise be the security module's.
fn layout_title(webform: &str) -> Result<String, String> {
    let document =
        roxmltree::Document::parse(webform).map_err(|e| format!("its WEBFORM is not XML: {e}"))?;
    let title = document
        .descendants()
        .find(|n| n.has_tag_name("gxTextBlock") && n.attribute("id") == Some("TitleText"))
        .and_then(|n| n.attribute("Caption"))
        .ok_or("its layout has no TitleText")?
        .to_string();
    if title.contains(['<', '>', '&', '"', ']']) {
        return Err(format!("its title '{title}' is not matched"));
    }
    if normalized(webform) != normalized(&LAYOUT.replace("@@title@@", &title)) {
        return Err("its layout is not the security module's not-authorized layout".to_string());
    }
    Ok(title)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_template_parses() {
        Template::parse(TEMPLATE).expect("template parses");
    }

    #[test]
    fn the_start_message_is_read_and_anything_else_refused() {
        let events = "Event Start\n  //HeaderBC.Object = PageHeader.Create()\n  Msg('No access')\nEndevent\n";
        assert_eq!(start_message(events).unwrap(), "No access");
        assert!(start_message("Event Start\n Msg('a')\n Msg('b')\nEndevent").is_err());
        assert!(start_message("Event Refresh\n Msg('a')\nEndevent").is_err());
        assert!(start_message("").is_err());
    }
}
