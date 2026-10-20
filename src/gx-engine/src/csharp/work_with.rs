//! The classes GeneXus 18 generates for a Work With pattern instance on a key-only Transaction:
//! the Transaction itself (`Trn353`), its selection panel (`WWTrn353`), its view panel
//! (`ViewTrn353`) and the view's General tab component (`Trn353General`).
//!
//! The instance is matched, not interpreted. Each of the four objects must store exactly the
//! rules, conditions, events, variables and layout the pattern writes for a Transaction whose
//! structure is its `Numeric(4)` key alone, named `<Transaction>Id`. The stored parts are compared
//! with `webform/work_with_<role>.parts.txt` after the Transaction name and the key's entity id are
//! put back, layout ids are dropped, and every KB-local reference (design-system class, SDT, SDT
//! level, domain) is replaced by what it names (see [`canonical`]). Anything else is refused by
//! name, and a single object of the instance that is edited refuses all four.
//!
//! Each class is `webform/work_with_<role>.cs.tmpl`, checked byte for byte (the build stamp aside)
//! against the SDK's output for the one KBTeste instance (`Trn353`); no other instance has been
//! compared.

use crate::catalog::{Catalog, KbObject, ObjectKind, ReferenceKind};
use crate::csharp::prompt::capitalized_lower;
use crate::csharp::template::{Fields, Template, Value};
use crate::csharp::transaction_form::{attribute_facts, is_numeric_4, single_level_structure};
use crate::csharp::unanimo_master::canonical;
use crate::csharp::webform::{
    WebPanelEnvironment, configured, describe_name, is_identifier, literal_ok, master_class_of,
    master_names, part_text, settings_of, three_digits,
};
use crate::udm::reader::WEBFORM_DEFAULT_PART;

/// The type GUID of an Attribute, as an `idBasedOn` names it.
const ATTRIBUTE_REF: &str = "adbb33c9-0906-4971-833c-998de27e0676-";

/// The longest physical name the SDK keeps for an attribute.
const PHYSICAL_NAME_LIMIT: usize = 30;

/// One object of the instance.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Role {
    Transaction,
    Selection,
    View,
    General,
}

impl Role {
    const ALL: [Role; 4] = [
        Role::Transaction,
        Role::Selection,
        Role::View,
        Role::General,
    ];

    fn object_name(self, base: &str) -> String {
        match self {
            Role::Transaction => base.to_string(),
            Role::Selection => format!("WW{base}"),
            Role::View => format!("View{base}"),
            Role::General => format!("{base}General"),
        }
    }

    fn kind(self) -> ObjectKind {
        match self {
            Role::Transaction => ObjectKind::Transaction,
            _ => ObjectKind::WebPanel,
        }
    }

    fn template(self) -> &'static str {
        match self {
            Role::Transaction => include_str!("webform/work_with_transaction.cs.tmpl"),
            Role::Selection => include_str!("webform/work_with_selection.cs.tmpl"),
            Role::View => include_str!("webform/work_with_view.cs.tmpl"),
            Role::General => include_str!("webform/work_with_general.cs.tmpl"),
        }
    }

    fn script_template(self) -> &'static str {
        match self {
            Role::Transaction => include_str!("webform/work_with_transaction.js.tmpl"),
            Role::Selection => include_str!("webform/work_with_selection.js.tmpl"),
            Role::View => include_str!("webform/work_with_view.js.tmpl"),
            Role::General => include_str!("webform/work_with_general.js.tmpl"),
        }
    }

    fn stored_parts(self) -> &'static str {
        match self {
            Role::Transaction => include_str!("webform/work_with_transaction.parts.txt"),
            Role::Selection => include_str!("webform/work_with_selection.parts.txt"),
            Role::View => include_str!("webform/work_with_view.parts.txt"),
            Role::General => include_str!("webform/work_with_general.parts.txt"),
        }
    }

    fn part_names(self) -> &'static [&'static str] {
        match self {
            Role::Transaction => &["RULES", "EVENTS", "VARIABLES", "WEBFORM"],
            _ => &["CONDITIONS", "RULES", "EVENTS", "VARIABLES", "WEBFORM"],
        }
    }

    /// The description the pattern gives the object (`GetPgmdesc`).
    fn description(self, base: &str) -> String {
        match self {
            Role::Transaction => base.to_string(),
            Role::Selection => format!("{base}s"),
            Role::View => format!("View {base}"),
            Role::General => format!("{base} General"),
        }
    }

    /// Whether the class loads a master page (the General tab is a web component and does not).
    fn has_master(self) -> bool {
        self != Role::General
    }
}

/// A matched object of a Work With instance, ready to emit.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorkWithClass {
    pub name: String,
    pub role: Role,
    scope: Fields,
}

impl WorkWithClass {
    pub fn file_name(&self) -> String {
        format!("{}.cs", self.name.to_ascii_lowercase())
    }

    /// The class text. `stamp` is the build stamp of its script references.
    pub fn emit(&self, stamp: &str) -> String {
        let mut scope = self.scope.clone();
        scope.insert("stamp".into(), stamp.into());
        Template::parse(self.role.template())
            .and_then(|template| template.render(&scope))
            .expect("the work-with templates render every scope the planner builds")
    }

    /// The client script's file name and bytes: the SDK's script for the role, CRLF line ends, and the
    /// CR CR LF the SDK writes after one edit control's `ip:[...],` (the template's `@@cr@@`).
    pub fn script(&self) -> Result<(String, String), String> {
        let mut scope = self.scope.clone();
        scope.insert("cr".into(), "\r".into());
        let text = Template::parse(self.role.script_template())
            .and_then(|template| template.render(&scope))?;
        Ok((
            format!("{}.js", self.name.to_ascii_lowercase()),
            text.replace('\n', "\r\n"),
        ))
    }

    /// The Transaction's name when this is the instance's Transaction.
    pub fn transaction_name(&self) -> Option<&str> {
        (self.role == Role::Transaction).then_some(self.name.as_str())
    }
}

/// Plans one object of a Work With instance, or says why it is not one this emitter matches.
pub fn plan_work_with(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
) -> Result<WorkWithClass, String> {
    let settings = settings_of(object)?;
    let name = settings.declared_name.clone();
    let (role, base) = role_of(object, &name, catalog)?;
    let transaction = find(catalog, ObjectKind::Transaction, &base)?;
    let key = check_transaction(transaction, &base, catalog)?;
    let facts = attribute_facts(env, &key)?;

    let mut members = Vec::new();
    for role in Role::ALL {
        let member = if role == Role::Transaction {
            transaction
        } else {
            find(catalog, role.kind(), &role.object_name(&base))?
        };
        check_member(member, role, &base, facts.id, env)?;
        members.push((role, member));
    }
    for required in [
        (
            ObjectKind::Procedure,
            "IsAuthorized",
            ["General", "Security"],
        ),
        (
            ObjectKind::WebPanel,
            "NotAuthorized",
            ["General", "Security"],
        ),
        (
            ObjectKind::StructuredDataType,
            "TransactionContext",
            ["General", "UI"],
        ),
    ] {
        let (kind, wanted, module) = required;
        let found = catalog.objects().any(|o| {
            o.kind == kind && o.name.eq_ignore_ascii_case(wanted) && o.module_path == module
        });
        if !found {
            return Err(format!(
                "its classes call {}.{wanted}, which the KB does not hold",
                module.join(".")
            ));
        }
    }

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
    let oid = three_digits(settings.entity_id, 0)?[..2].to_string();

    let mut scope = Fields::new();
    let mut text = |field: &str, value: String| {
        scope.insert(field.to_string(), Value::Text(value));
    };
    text("name", base.clone());
    text("lname", base.to_ascii_lowercase());
    text("oid", oid.clone());
    text("loid", oid.to_ascii_lowercase());
    text("k_id", facts.id.to_string());
    text("k_p", key.clone());
    text("k_U", key.to_ascii_uppercase());
    text("k_cap", capitalized_lower(&key));
    text("theme", theme);
    text("generator", generator);
    text("build", build);
    if role == Role::Transaction {
        let table_key = *env
            .table_keys
            .get(&base)
            .ok_or_else(|| format!("its table '{base}' has no single stored TablePK"))?;
        if !(1..=9).contains(&table_key) {
            return Err(format!(
                "its table key {table_key} is not a single digit, which is not matched"
            ));
        }
        text("tpk", table_key.to_string());
    }
    if role.has_master() {
        let masters = master_names(
            object,
            catalog,
            &[ReferenceKind::Uses, ReferenceKind::Calls],
        )?;
        let master = match masters.as_slice() {
            [only] => only.clone(),
            _ => return Err("it does not use exactly one master page".to_string()),
        };
        text("master_class", master_class_of(&master, catalog, env)?);
    }
    Ok(WorkWithClass { name, role, scope })
}

/// The role the object plays and the name of the Transaction the instance is on.
fn role_of(object: &KbObject, name: &str, catalog: &Catalog) -> Result<(Role, String), String> {
    if object.kind == ObjectKind::Transaction {
        return Ok((Role::Transaction, name.to_string()));
    }
    if object.kind != ObjectKind::WebPanel {
        return Err("it is neither a Transaction nor a WebPanel".to_string());
    }
    let candidates = [
        (Role::Selection, name.strip_prefix("WW")),
        (Role::View, name.strip_prefix("View")),
        (Role::General, name.strip_suffix("General")),
    ];
    let found: Vec<(Role, String)> = candidates
        .into_iter()
        .filter_map(|(role, base)| base.map(|base| (role, base.to_string())))
        .filter(|(_, base)| {
            catalog
                .objects()
                .any(|o| o.kind == ObjectKind::Transaction && o.name == *base)
        })
        .collect();
    match found.as_slice() {
        [(role, base)] => Ok((*role, base.clone())),
        [] => Err(
            "its name is not WW<Transaction>, View<Transaction> or <Transaction>General"
                .to_string(),
        ),
        _ => Err("its name fits more than one Work With role".to_string()),
    }
}

/// The one object of `kind` named `name` at the root module.
fn find<'c>(catalog: &'c Catalog, kind: ObjectKind, name: &str) -> Result<&'c KbObject, String> {
    let found: Vec<&KbObject> = catalog
        .objects()
        .filter(|o| o.kind == kind && o.name == name)
        .collect();
    match found.as_slice() {
        [only] if only.module_path.is_empty() => Ok(only),
        [_] => Err(format!(
            "'{name}' is held by a module; only instances at the root module are matched"
        )),
        [] => Err(format!("the Work With instance has no {kind:?} '{name}'")),
        _ => Err(format!("more than one {kind:?} is named '{name}'")),
    }
}

/// The Transaction's structure is its `Numeric(4)` key alone, named `<Transaction>Id`, held by no
/// other Transaction. Returns the key's name.
fn check_transaction(
    transaction: &KbObject,
    base: &str,
    catalog: &Catalog,
) -> Result<String, String> {
    if !is_identifier(base) {
        return Err(format!(
            "its Transaction name '{base}' is not a C# identifier"
        ));
    }
    let structure = single_level_structure(transaction)
        .map_err(|reason| format!("Transaction '{base}': {reason}"))?;
    let key_name = format!("{base}Id");
    let [key] = structure.root.attributes.as_slice() else {
        return Err(format!(
            "Transaction '{base}' is not its key alone, the only structure matched"
        ));
    };
    if !key.is_key || key.name != key_name || !structure.key_references.is_empty() {
        return Err(format!(
            "Transaction '{base}' is not keyed by '{key_name}' alone"
        ));
    }
    if !key.data_type.as_ref().is_some_and(is_numeric_4) {
        return Err(format!("the key '{key_name}' is not Numeric(4)"));
    }
    if key_name.len() > PHYSICAL_NAME_LIMIT {
        return Err(format!(
            "the key '{key_name}' is longer than its physical name"
        ));
    }
    let quoted = format!("\"{key_name}\"");
    if let Some(other) = catalog.objects().find(|o| {
        o.kind == ObjectKind::Transaction
            && o.id != transaction.id
            && part_text(o, "STRUCTURE").is_some_and(|s| s.contains(&quoted))
    }) {
        return Err(format!(
            "Transaction '{}' also holds '{key_name}', so referential checks would be generated",
            other.name
        ));
    }
    Ok(key_name)
}

/// One object of the instance stores exactly what the pattern writes.
fn check_member(
    object: &KbObject,
    role: Role,
    base: &str,
    key_id: u32,
    env: &WebPanelEnvironment,
) -> Result<(), String> {
    let name = role.object_name(base);
    let settings = settings_of(object)?;
    if settings.declared_name != name {
        return Err(format!(
            "'{name}' declares the name '{}'",
            settings.declared_name
        ));
    }
    if settings.master_page_id.is_some() {
        return Err(format!("'{name}' names a master page of its own"));
    }
    let web_comp = match role {
        Role::General => Some("Yes"),
        _ => None,
    };
    if settings.web_comp.as_deref() != web_comp {
        return Err(format!(
            "'{name}' has a web-component property the pattern does not write"
        ));
    }
    // The SDK describes `Trn353General` as `Trn353 General`, a split before a capital after a
    // digit that `describe_name` refuses in general; the General tab is matched on that evidence.
    let description = match settings.description.clone() {
        Some(description) => description,
        None if role == Role::General => role.description(base),
        None => describe_name(&name)?,
    };
    if description != role.description(base) {
        return Err(format!(
            "'{name}' is described '{description}', not '{}'",
            role.description(base)
        ));
    }
    // The pattern sets the Transaction's form to its default (`IsDefault` True, built from the
    // pattern's `TrnDefaultWebForm.dkt`); the stored layout is compared all the same.
    if role == Role::Transaction && part_text(object, WEBFORM_DEFAULT_PART) != Some("true") {
        return Err(format!(
            "'{name}' does not keep the pattern's default web form"
        ));
    }
    let expected = role
        .stored_parts()
        .replace("@@name@@", base)
        .replace("@@k_id@@", &key_id.to_string());
    let mut stored = Vec::new();
    for part in role.part_names() {
        let text = part_text(object, part).unwrap_or("").replace('\r', "");
        let text = canonical(&text.replace(ATTRIBUTE_REF, "attribute:"), env)
            .map_err(|reason| format!("'{name}' {part}: {reason}"))?;
        stored.push(format!("@@@ {part}\n{text}"));
    }
    let stored = stored.join("\n");
    if stored != expected {
        let differs = expected
            .split("\n@@@ ")
            .zip(stored.split("\n@@@ "))
            .find(|(a, b)| a != b)
            .and_then(|(a, _)| a.trim_start_matches("@@@ ").lines().next())
            .unwrap_or("its parts");
        return Err(format!(
            "'{name}' {differs} is not what the Work With pattern writes"
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_template_parses() {
        for role in Role::ALL {
            Template::parse(role.template()).expect("template parses");
            assert!(role.stored_parts().starts_with("@@@ "));
        }
    }

    #[test]
    fn roles_name_their_objects_after_the_transaction() {
        assert_eq!(Role::Selection.object_name("Trn353"), "WWTrn353");
        assert_eq!(Role::View.object_name("Trn353"), "ViewTrn353");
        assert_eq!(Role::General.object_name("Trn353"), "Trn353General");
        assert_eq!(Role::View.description("Trn353"), "View Trn353");
    }
}
