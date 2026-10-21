//! The master page emitter: master resolution, the module scope of a class, and the matched `ContentHolder` master.

use super::layout::check_master_layout;
use super::*;

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
pub(super) fn master_of(
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
const MASTER_TEMPLATE: &str = include_str!("master_contentholder.cs.tmpl");

/// The client script of the matched master page (KBTeste `general/ui/masterprompt.js`).
const MASTER_SCRIPT: &str = include_str!("master_contentholder.js.tmpl");

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
