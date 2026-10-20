//! The class GeneXus 18 generates for a single-level Transaction, in the two shapes matched so far.
//!
//! - A web form the user laid out (`IsDefault` False): a numeric key, `Character(20)` attributes,
//!   the design-system form with one row per shown attribute, and the standard toolbar and
//!   buttons. An attribute the form does not show is still read and written: the SDK sends it as a
//!   hidden field guarded by the integrity hash, and the template does the same. The class is
//!   `webform/transaction_custom_form.cs.tmpl`, checked byte for byte (the build stamp aside)
//!   against the SDK's output for the three KBTeste Transactions of this shape: every attribute
//!   shown in structure order, every attribute shown in another order, and one attribute hidden.
//! - The default form (`IsDefault` True), which the SDK generates from the structure and the
//!   design system rather than from the stored layout: a `Numeric(4)` key, one `Numeric(8)` and
//!   one `VarChar(40)` attribute, no rules and no events. The class is
//!   `webform/transaction_default_form.cs.tmpl`, checked byte for byte against the SDK's output for
//!   the two KBTeste Transactions of this shape (`TrnGroupProbeBase`, `TrnGroupProbeSub`).
//!
//! - A default form whose structure is a `Numeric(4)` key alone, on either side of a subtype
//!   relation: the subtype side (`webform/transaction_subtype_key_form.cs.tmpl`, `Plan070Trn`,
//!   whose key's `SuperTypeKey` names the other's key) and the supertype side
//!   (`webform/transaction_supertype_key_form.cs.tmpl`, `Plan071Trn`). Each is checked against the
//!   one KBTeste Transaction of its role only.
//!
//! The Transaction is matched, not interpreted. Its structure, rules, events, variables and layout
//! must be exactly one of these shapes; anything else is refused by name.

use crate::catalog::{Catalog, KbObject, ObjectKind, ReferenceKind};
use crate::csharp::prompt::{canonical_layout, capitalized_lower};
use crate::csharp::template::{Fields, Template, Value};
use crate::csharp::transaction_js::{self, Shape};
use crate::csharp::webform::{
    WebPanelEnvironment, configured, is_identifier, literal_ok, master_class_of, master_names,
    part_text, settings_of, three_digits,
};
use crate::model::DataType;
use crate::transaction::{TransactionAttributeRef, TransactionStructure};
use crate::udm::reader::WEBFORM_DEFAULT_PART;

const CUSTOM_TEMPLATE: &str = include_str!("webform/transaction_custom_form.cs.tmpl");
const DEFAULT_TEMPLATE: &str = include_str!("webform/transaction_default_form.cs.tmpl");
const SUBTYPE_KEY_TEMPLATE: &str = include_str!("webform/transaction_subtype_key_form.cs.tmpl");
const SUPERTYPE_KEY_TEMPLATE: &str = include_str!("webform/transaction_supertype_key_form.cs.tmpl");

/// The longest physical name the SDK keeps for an attribute (`LiveMoveAttributeRulesDescript`).
const PHYSICAL_NAME_LIMIT: usize = 30;

/// The standard variables every Transaction declares, in id order.
const STANDARD_VARIABLES: [&str; 6] = ["Today", "Time", "Pgmname", "Pgmdesc", "GxRemove", "Mode"];

/// A matched Transaction, ready to emit.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TransactionForm {
    pub name: String,
    template: &'static str,
    scope: Fields,
    shape: Shape,
    /// The user events of a custom form: name and the literal message each shows.
    events: Vec<(String, String)>,
}

impl TransactionForm {
    pub fn file_name(&self) -> String {
        format!("{}.cs", self.name.to_ascii_lowercase())
    }

    /// The class text. `stamp` is the build stamp of its script references.
    pub fn emit(&self, stamp: &str) -> String {
        let mut scope = self.scope.clone();
        scope.insert("stamp".into(), stamp.into());
        Template::parse(self.template)
            .and_then(|template| template.render(&scope))
            .expect("the transaction templates render every scope the planner builds")
    }

    /// The client script's file name and bytes.
    pub fn script(&self) -> Result<(String, String), String> {
        transaction_js::script(&self.name, self.shape, &self.scope, &self.events)
    }
}

struct Member {
    name: String,
    id: u32,
    physical: String,
}

/// Plans a single-level Transaction's web class, or says why it is not a shape this emitter
/// matches.
pub fn plan_transaction_form(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
) -> Result<TransactionForm, String> {
    if object.kind != ObjectKind::Transaction {
        return Err("it is not a Transaction".to_string());
    }
    let settings = settings_of(object)?;
    let name = settings.declared_name.clone();
    if !is_identifier(&name) {
        return Err(format!("its name '{name}' is not a C# identifier"));
    }
    if settings.master_page_id.is_some() || settings.web_comp.is_some() {
        return Err("it sets a master page or a web-component property of its own".to_string());
    }
    if !object.module_path.is_empty() {
        return Err(format!(
            "it is held by module '{}'; only Transactions at the root module are matched",
            object.module_path.join(".")
        ));
    }
    let default_form = match part_text(object, WEBFORM_DEFAULT_PART) {
        Some("false") => false,
        Some("true") => true,
        Some(other) => {
            return Err(format!(
                "its web form's IsDefault property '{other}' is neither True nor False"
            ));
        }
        None => return Err("its web form's IsDefault property was not read".to_string()),
    };

    if default_form {
        let structure = single_level_structure(object)?;
        if structure.root.attributes.len() == 1 {
            return plan_key_only_form(object, catalog, env, settings.entity_id, &name, &structure);
        }
    }
    let structure = structure_of(object)?;
    let attributes = &structure.root.attributes;
    let (key, rest) = attributes
        .split_first()
        .ok_or("its structure has no attributes")?;
    if !key.is_key || rest.iter().any(|a| a.is_key) {
        return Err("its key is not exactly its first attribute".to_string());
    }
    check_kind(key, |t| {
        matches!(
            t,
            DataType::Numeric {
                precision: 4,
                scale: 0
            }
        )
    })?;
    if default_form {
        check_default_form_attributes(rest)?;
    } else {
        for attribute in rest {
            check_kind(attribute, |t| {
                matches!(t, DataType::Character { length: 20 })
            })?;
        }
    }
    check_unreferenced(object, &structure, catalog)?;

    let description = settings
        .description
        .clone()
        .map(Ok)
        .unwrap_or_else(|| title_words(&name).map(|words| words.join(" ")))?;
    if !literal_ok(&description) {
        return Err(format!(
            "its description '{description}' has characters the emitter is not matched for"
        ));
    }
    if !part_text(object, "RULES").unwrap_or("").trim().is_empty() {
        return Err("it has rules, which are not emitted yet".to_string());
    }
    let events = part_text(object, "EVENTS").unwrap_or("");
    if default_form {
        if !events.trim().is_empty() {
            return Err(
                "it has events, which the default-form emitter is not matched for".to_string(),
            );
        }
    } else {
        check_events(events)?;
    }
    check_variables(part_text(object, "VARIABLES").unwrap_or(""))?;

    let mut members = Vec::new();
    for attribute in attributes {
        let facts = env.attributes.get(&attribute.name).ok_or_else(|| {
            format!(
                "attribute '{}' is not among the KB's attributes",
                attribute.name
            )
        })?;
        if facts.custom_title {
            return Err(format!(
                "attribute '{}' stores a title of its own, which is not matched",
                attribute.name
            ));
        }
        // The custom forms matched so far name every attribute after the Transaction; the default
        // forms do not (`GpBaseId` in `TrnGroupProbeBase`).
        if !is_identifier(&attribute.name) || (!default_form && !attribute.name.starts_with(&name))
        {
            return Err(format!(
                "attribute '{}' is not named after the Transaction",
                attribute.name
            ));
        }
        members.push(Member {
            name: attribute.name.clone(),
            id: facts.id,
            physical: attribute.name.chars().take(PHYSICAL_NAME_LIMIT).collect(),
        });
    }
    let mut physical: Vec<&str> = members.iter().map(|m| m.physical.as_str()).collect();
    physical.sort_unstable();
    physical.dedup();
    if physical.len() != members.len() {
        return Err("two attributes share a physical name".to_string());
    }

    // The default form is generated from the structure: its stored layout is not what the SDK
    // lays out, so only a custom form's layout is checked.
    let shown = if default_form {
        Vec::new()
    } else {
        check_layout(object, &description, &members, env)?
    };

    let mut scope = environment_scope(object, catalog, env, settings.entity_id, &name, &key.name)?;
    let last_word = description
        .split_whitespace()
        .last()
        .ok_or("its description is empty")?
        .to_string();
    let item = |member: &Member, form: bool| -> Result<Fields, String> {
        let words = title_words(&member.name)?;
        let start = words
            .iter()
            .position(|word| *word == last_word)
            .ok_or_else(|| {
                format!(
                    "the title of '{}' does not hold '{last_word}', so its contextual title is not evidenced",
                    member.name
                )
            })?;
        let mut f = Fields::new();
        f.insert("id".into(), Value::Text(member.id.to_string()));
        f.insert("p".into(), Value::Text(member.physical.clone()));
        f.insert(
            "U".into(),
            Value::Text(member.physical.to_ascii_uppercase()),
        );
        f.insert("ct".into(), Value::Text(words[start..].join(" ")));
        f.insert("form".into(), Value::Flag(form));
        Ok(f)
    };
    let text = |scope: &mut Fields, key: &str, value: String| {
        scope.insert(key.to_string(), Value::Text(value));
    };
    let key_member = &members[0];
    let key_fields = item(key_member, true)?;
    text(&mut scope, "desc", description.clone());
    text(&mut scope, "k_id", key_member.id.to_string());
    text(&mut scope, "k_p", key_member.physical.clone());
    text(&mut scope, "k_U", key_member.physical.to_ascii_uppercase());
    text(&mut scope, "k_cap", capitalized_lower(&key_member.physical));
    if let Some(Value::Text(ct)) = key_fields.get("ct") {
        text(&mut scope, "k_ct", ct.clone());
    }

    if default_form {
        // One slot per attribute of the matched shape: `a1` the Numeric(8), `a2` the VarChar(40).
        for (slot, member) in ["a1", "a2"].iter().zip(&members[1..]) {
            let fields = item(member, true)?;
            for field in ["id", "p", "U", "ct"] {
                if let Some(Value::Text(value)) = fields.get(field) {
                    text(&mut scope, &format!("{slot}_{field}"), value.clone());
                }
            }
        }
        return Ok(TransactionForm {
            name,
            template: DEFAULT_TEMPLATE,
            scope,
            shape: Shape::Default,
            events: Vec::new(),
        });
    }

    let mut struct_order = Vec::new();
    let mut hidden = Vec::new();
    let mut shown_struct_order = Vec::new();
    for member in &members[1..] {
        let on_form = shown.contains(&member.id);
        let fields = item(member, on_form)?;
        if on_form {
            shown_struct_order.push(fields.clone());
        } else {
            hidden.push(fields.clone());
        }
        struct_order.push(fields);
    }
    let mut form_order = Vec::new();
    for (index, id) in shown.iter().enumerate() {
        let member = members[1..]
            .iter()
            .find(|m| m.id == *id)
            .expect("the layout check admits only the Transaction's attributes");
        let mut fields = item(member, true)?;
        fields.insert("focus".into(), Value::Text((39 + 5 * index).to_string()));
        form_order.push(fields);
    }
    let shown_count = form_order.len();
    let first_focus = match form_order.first().and_then(|f| f.get("p")) {
        Some(Value::Text(p)) => p.clone(),
        _ => key_member.physical.clone(),
    };
    text(&mut scope, "ffirst", first_focus);
    text(&mut scope, "btn1", (39 + 5 * shown_count).to_string());
    text(&mut scope, "btn2", (41 + 5 * shown_count).to_string());
    text(&mut scope, "btn3", (43 + 5 * shown_count).to_string());
    scope.insert("has_hidden".into(), Value::Flag(!hidden.is_empty()));
    scope.insert("sa".into(), Value::List(struct_order));
    scope.insert("fa".into(), Value::List(form_order));
    scope.insert("fsa".into(), Value::List(shown_struct_order));
    scope.insert("ha".into(), Value::List(hidden));
    Ok(TransactionForm {
        name,
        template: CUSTOM_TEMPLATE,
        scope,
        shape: Shape::Custom,
        events: user_events(part_text(object, "EVENTS").unwrap_or("")),
    })
}

/// The non-key attributes of the one default form matched: a `Numeric(8)` then a `VarChar(40)`.
fn check_default_form_attributes(rest: &[TransactionAttributeRef]) -> Result<(), String> {
    let shape = "its default form is matched only for a Numeric(4) key, a Numeric(8) and a VarChar(40) attribute";
    let [number, text] = rest else {
        return Err(format!("{shape}; it has {} non-key attributes", rest.len()));
    };
    check_kind(number, |t| {
        matches!(
            t,
            DataType::Numeric {
                precision: 8,
                scale: 0
            }
        )
    })
    .and_then(|()| check_kind(text, |t| matches!(t, DataType::Varchar { length: 40 })))
    .map_err(|reason| format!("{shape}: {reason}"))
}

/// The scope both shapes share: names, ids, the prompt, the master page and the KB's web
/// environment.
fn environment_scope(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
    entity_id: i32,
    name: &str,
    key: &str,
) -> Result<Fields, String> {
    let table_key = *env
        .table_keys
        .get(name)
        .ok_or_else(|| format!("its table '{name}' has no single stored TablePK"))?;
    if !(1..=9).contains(&table_key) {
        return Err(format!(
            "its table key {table_key} is not a single digit, which is not matched"
        ));
    }
    let prompt = prompt_of(object, catalog, key)?;
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
    let oid = three_digits(entity_id, 0)?[..2].to_string();

    let mut scope = Fields::new();
    for (key, value) in [
        ("name", name.to_string()),
        ("lname", name.to_ascii_lowercase()),
        ("oid", oid),
        ("tpk", table_key.to_string()),
        ("prompt", prompt),
        ("theme", theme),
        ("generator", generator),
        ("build", build),
        ("master_class", master_class),
    ] {
        scope.insert(key.to_string(), Value::Text(value));
    }
    Ok(scope)
}

/// A default-form Transaction whose structure is its `Numeric(4)` key alone, in one of the two
/// roles matched: the subtype side, whose key's `SuperTypeKey` names the key of another key-only
/// Transaction (`Plan070Trn`), and the supertype side, the Transaction that one names
/// (`Plan071Trn`). A key-only Transaction in neither role is refused: no SDK output has been
/// matched for it.
fn plan_key_only_form(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
    entity_id: i32,
    name: &str,
    structure: &TransactionStructure,
) -> Result<TransactionForm, String> {
    let key = &structure.root.attributes[0];
    if !key.is_key {
        return Err("its one attribute is not its key".to_string());
    }
    check_kind(key, is_numeric_4)?;
    let description = description_of(object, name)?;
    if !part_text(object, "RULES").unwrap_or("").trim().is_empty() {
        return Err("it has rules, which are not emitted yet".to_string());
    }
    if !part_text(object, "EVENTS").unwrap_or("").trim().is_empty() {
        return Err("it has events, which the default-form emitter is not matched for".to_string());
    }
    check_variables(part_text(object, "VARIABLES").unwrap_or(""))?;
    if key.name.len() > PHYSICAL_NAME_LIMIT {
        return Err(format!(
            "its key '{}' is longer than its physical name",
            key.name
        ));
    }
    let facts = attribute_facts(env, &key.name)?;

    let (template, related) = match structure.key_references.as_slice() {
        [reference] if reference.attribute == key.name => (
            SUBTYPE_KEY_TEMPLATE,
            subtype_side(
                object,
                catalog,
                env,
                structure,
                &key.name,
                &reference.references,
            )?,
        ),
        [] => (
            SUPERTYPE_KEY_TEMPLATE,
            supertype_side(object, catalog, env, &key.name)?,
        ),
        _ => {
            return Err("its key references are not the one subtype reference matched".to_string());
        }
    };

    let mut scope = environment_scope(object, catalog, env, entity_id, name, &key.name)?;
    scope.extend(related);
    for (field, value) in [
        ("desc", description.clone()),
        ("k_id", facts.id.to_string()),
        ("k_p", key.name.clone()),
        ("k_U", key.name.to_ascii_uppercase()),
        ("k_cap", capitalized_lower(&key.name)),
        ("k_ct", contextual_title(&key.name, &description)?),
    ] {
        scope.insert(field.to_string(), Value::Text(value));
    }
    Ok(TransactionForm {
        name: name.to_string(),
        template,
        scope,
        shape: Shape::KeyOnly,
        events: Vec::new(),
    })
}

/// The subtype side: the supertype it names, that supertype's prompt, and its subtype group.
fn subtype_side(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
    structure: &TransactionStructure,
    key: &str,
    supertype_key: &str,
) -> Result<Fields, String> {
    check_unreferenced(object, structure, catalog)?;
    let (other, other_key) = key_only_transaction_keyed(catalog, object, supertype_key)?;
    let group = env.subtype_groups.get(key).ok_or_else(|| {
        format!("its key '{key}' belongs to no single subtype group the KB names")
    })?;
    if !literal_ok(group) {
        return Err(format!(
            "its subtype group '{group}' has characters the emitter is not matched for"
        ));
    }
    let other_name = settings_of(other)?.declared_name;
    if !is_identifier(&other_name) || !env.table_keys.contains_key(&other_name) {
        return Err(format!(
            "its supertype's table '{other_name}' is not a stored table the emitter can name"
        ));
    }
    let mut related = Fields::new();
    related.insert(
        "r_prompt".into(),
        Value::Text(prompt_of(other, catalog, &other_key)?),
    );
    related.insert("r_name".into(), Value::Text(other_name));
    related.insert("r_p".into(), Value::Text(other_key));
    related.insert("group".into(), Value::Text(group.clone()));
    Ok(related)
}

/// The supertype side: the one key-only Transaction that names this key as its supertype.
fn supertype_side(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
    key: &str,
) -> Result<Fields, String> {
    let mut subtypes = Vec::new();
    for other in catalog.objects() {
        if other.kind != ObjectKind::Transaction || other.id == object.id {
            continue;
        }
        let theirs = single_level_structure(other).map_err(|reason| {
            format!(
                "Transaction '{}' cannot be read ({reason}), so a reference to this one cannot be ruled out",
                other.name
            )
        })?;
        if theirs.root.attributes.iter().any(|a| a.name == key) {
            return Err(format!(
                "Transaction '{}' also holds attribute '{key}'",
                other.name
            ));
        }
        if theirs.key_references.iter().any(|r| r.references == key) {
            subtypes.push((other, theirs));
        }
    }
    let [(subtype, theirs)] = subtypes.as_slice() else {
        return Err(
            "its default form is matched only for a key-only Transaction that exactly one other key-only Transaction names as its supertype"
                .to_string(),
        );
    };
    let [sub_key] = theirs.root.attributes.as_slice() else {
        return Err(format!(
            "the Transaction '{}' that names it as supertype is not key-only",
            subtype.name
        ));
    };
    let sub_name = settings_of(subtype)?.declared_name;
    if !is_identifier(&sub_name) || !env.table_keys.contains_key(&sub_name) {
        return Err(format!(
            "its subtype's table '{sub_name}' is not a stored table the emitter can name"
        ));
    }
    let sub_description = description_of(subtype, &sub_name)?;
    let sub_facts = attribute_facts(env, &sub_key.name)?;
    let mut related = Fields::new();
    related.insert("r_name".into(), Value::Text(sub_name));
    related.insert("r_p".into(), Value::Text(sub_key.name.clone()));
    related.insert("r_id".into(), Value::Text(sub_facts.id.to_string()));
    related.insert("r_desc".into(), Value::Text(sub_description));
    Ok(related)
}

pub(crate) fn is_numeric_4(t: &DataType) -> bool {
    matches!(
        t,
        DataType::Numeric {
            precision: 4,
            scale: 0
        }
    )
}

/// The stored description, or the title GeneXus derives from the name.
fn description_of(object: &KbObject, name: &str) -> Result<String, String> {
    let description = settings_of(object)?
        .description
        .map(Ok)
        .unwrap_or_else(|| title_words(name).map(|words| words.join(" ")))?;
    if literal_ok(&description) {
        Ok(description)
    } else {
        Err(format!(
            "the description '{description}' has characters the emitter is not matched for"
        ))
    }
}

/// An attribute's title from the last word of the Transaction's description on
/// (`Plan070TrnId` in `Plan070 Trn` -> `Trn Id`).
fn contextual_title(attribute: &str, description: &str) -> Result<String, String> {
    let last_word = description
        .split_whitespace()
        .last()
        .ok_or("its description is empty")?;
    let words = title_words(attribute)?;
    let start = words.iter().position(|w| w == last_word).ok_or_else(|| {
        format!(
            "the title of '{attribute}' does not hold '{last_word}', so its contextual title is not evidenced"
        )
    })?;
    Ok(words[start..].join(" "))
}

/// The facts of an attribute the form shows, refusing one with a title of its own.
pub(crate) fn attribute_facts<'e>(
    env: &'e WebPanelEnvironment,
    attribute: &str,
) -> Result<&'e crate::udm::client::AttributeFacts, String> {
    let facts = env
        .attributes
        .get(attribute)
        .ok_or_else(|| format!("attribute '{attribute}' is not among the KB's attributes"))?;
    if facts.custom_title {
        return Err(format!(
            "attribute '{attribute}' stores a title of its own, which is not matched"
        ));
    }
    Ok(facts)
}

/// The one other Transaction holding `key`, which must be that key alone, and the key's name.
fn key_only_transaction_keyed<'c>(
    catalog: &'c Catalog,
    object: &KbObject,
    key: &str,
) -> Result<(&'c KbObject, String), String> {
    let mut found = Vec::new();
    for other in catalog.objects() {
        if other.kind != ObjectKind::Transaction || other.id == object.id {
            continue;
        }
        let theirs = single_level_structure(other).map_err(|reason| {
            format!(
                "Transaction '{}' cannot be read ({reason}), so its supertype cannot be resolved",
                other.name
            )
        })?;
        if theirs.root.attributes.iter().any(|a| a.name == key) {
            found.push((other, theirs));
        }
    }
    let [(other, theirs)] = found.as_slice() else {
        return Err(format!(
            "its supertype key '{key}' is not held by exactly one other Transaction"
        ));
    };
    match theirs.root.attributes.as_slice() {
        [only] if only.is_key && theirs.key_references.is_empty() => {
            check_kind(only, is_numeric_4)?;
            Ok((*other, only.name.clone()))
        }
        _ => Err(format!(
            "its supertype Transaction '{}' is not a key-only Transaction",
            other.name
        )),
    }
}

fn structure_of(object: &KbObject) -> Result<TransactionStructure, String> {
    let structure = single_level_structure(object)?;
    if !structure.key_references.is_empty() {
        return Err("its structure carries key references".to_string());
    }
    Ok(structure)
}

/// The decoded structure of a single-level, non-business-component Transaction without structure
/// rules; key references are left to the caller.
pub(crate) fn single_level_structure(object: &KbObject) -> Result<TransactionStructure, String> {
    let text = part_text(object, "STRUCTURE").ok_or("it has no STRUCTURE part")?;
    let structure: TransactionStructure = serde_json::from_str(text)
        .map_err(|_| "its STRUCTURE is not the decoded form this build reads".to_string())?;
    if !structure.root.levels.is_empty() {
        return Err("it has nested levels".to_string());
    }
    if structure.is_business_component {
        return Err("it is a business component".to_string());
    }
    if !structure.rules.is_empty() {
        return Err("its structure carries rules".to_string());
    }
    Ok(structure)
}

fn check_kind(
    attribute: &TransactionAttributeRef,
    matched: impl Fn(&DataType) -> bool,
) -> Result<(), String> {
    if attribute.formula.is_some() {
        return Err(format!("attribute '{}' is a formula", attribute.name));
    }
    if attribute.is_nullable || attribute.is_foreign_key {
        return Err(format!(
            "attribute '{}' is nullable or a foreign key",
            attribute.name
        ));
    }
    match &attribute.data_type {
        Some(data_type) if matched(data_type) => Ok(()),
        Some(other) => Err(format!(
            "attribute '{}' has type {other:?}, which the transaction emitter is not matched for",
            attribute.name
        )),
        None => Err(format!("attribute '{}' has no stored type", attribute.name)),
    }
}

/// No other Transaction holds one of its attributes or names one in a key reference: the SDK then
/// writes referential-integrity checks this template does not have.
fn check_unreferenced(
    object: &KbObject,
    structure: &TransactionStructure,
    catalog: &Catalog,
) -> Result<(), String> {
    let own: Vec<&str> = structure
        .root
        .attributes
        .iter()
        .map(|a| a.name.as_str())
        .collect();
    for other in catalog.objects() {
        if other.kind != ObjectKind::Transaction || other.id == object.id {
            continue;
        }
        let text = part_text(other, "STRUCTURE").ok_or_else(|| {
            format!(
                "Transaction '{}' has no STRUCTURE, so a reference to this one cannot be ruled out",
                other.name
            )
        })?;
        let theirs: TransactionStructure = serde_json::from_str(text).map_err(|_| {
            format!(
                "Transaction '{}' has an undecoded STRUCTURE, so a reference to this one cannot be ruled out",
                other.name
            )
        })?;
        let mut levels = vec![&theirs.root];
        while let Some(level) = levels.pop() {
            if let Some(shared) = level
                .attributes
                .iter()
                .find(|a| own.contains(&a.name.as_str()))
            {
                return Err(format!(
                    "Transaction '{}' also holds attribute '{}'",
                    other.name, shared.name
                ));
            }
            levels.extend(level.levels.iter());
        }
        if let Some(reference) = theirs
            .key_references
            .iter()
            .find(|r| own.contains(&r.references.as_str()) || own.contains(&r.attribute.as_str()))
        {
            return Err(format!(
                "Transaction '{}' references attribute '{}'",
                other.name, reference.references
            ));
        }
    }
    Ok(())
}

/// The words of a title GeneXus derives from a name: a break before each capital that follows a
/// lower-case letter or a digit (`LiveMoveAttributeRules2Id` -> `Live Move Attribute Rules2 Id`).
/// Consecutive capitals are refused: no SDK output has been matched for them.
fn title_words(name: &str) -> Result<Vec<String>, String> {
    if name.is_empty() || !name.chars().all(|c| c.is_ascii_alphanumeric()) {
        return Err(format!(
            "'{name}' has characters the title rule is not matched for"
        ));
    }
    let chars: Vec<char> = name.chars().collect();
    let mut words = vec![String::new()];
    for (index, c) in chars.iter().enumerate() {
        if index > 0 && c.is_ascii_uppercase() {
            let previous = chars[index - 1];
            if previous.is_ascii_uppercase() {
                return Err(format!(
                    "'{name}' has consecutive capitals, which the title rule is not matched for"
                ));
            }
            words.push(String::new());
        }
        words.last_mut().expect("one word").push(*c);
    }
    Ok(words)
}

/// Events are only user events whose one statement shows a literal message. GeneXus runs such an
/// event in the browser (it is in the object's script, not its class), so the class has none.
fn check_events(events: &str) -> Result<(), String> {
    let code: Vec<&str> = events
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty() && !line.starts_with("//"))
        .collect();
    for chunk in code.chunks(3) {
        let ok = match chunk {
            [start, call, end] => {
                let user_event = start
                    .strip_prefix("Event '")
                    .and_then(|rest| rest.strip_suffix('\''))
                    .is_some_and(is_identifier);
                let message = call
                    .to_ascii_lowercase()
                    .strip_prefix("msg('")
                    .and_then(|rest| rest.strip_suffix("')").map(str::to_string))
                    .is_some_and(|m| !m.contains('\''));
                user_event && message && end.eq_ignore_ascii_case("EndEvent")
            }
            _ => false,
        };
        if !ok {
            return Err(
                "its events are not only user events showing a literal message, which run in the browser"
                    .to_string(),
            );
        }
    }
    Ok(())
}

/// The user events `check_events` admitted, with the message each shows.
fn user_events(events: &str) -> Vec<(String, String)> {
    let code: Vec<&str> = events
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty() && !line.starts_with("//"))
        .collect();
    code.chunks(3)
        .filter_map(|chunk| {
            let name = chunk[0].strip_prefix("Event '")?.strip_suffix('\'')?;
            let call = chunk.get(1)?;
            let message = call.get(5..call.len().checked_sub(2)?)?;
            // The script names a user event in upper case (`'LIVEMOVEATTRIBUTEEVENT'`).
            Some((name.to_ascii_uppercase(), message.to_string()))
        })
        .collect()
}

fn check_variables(text: &str) -> Result<(), String> {
    let document =
        roxmltree::Document::parse(text).map_err(|e| format!("its VARIABLES are not XML: {e}"))?;
    let found: Vec<(String, String, String)> = document
        .root_element()
        .children()
        .filter(|n| n.is_element())
        .map(|node| {
            (
                node.tag_name().name().to_string(),
                node.attribute("Id").unwrap_or("").to_string(),
                node.attribute("Name").unwrap_or("").to_string(),
            )
        })
        .collect();
    let expected: Vec<(String, String, String)> = STANDARD_VARIABLES
        .iter()
        .enumerate()
        .map(|(i, name)| {
            (
                "StandardVariable".to_string(),
                (i + 1).to_string(),
                name.to_string(),
            )
        })
        .collect();
    if found == expected {
        Ok(())
    } else {
        Err("its variables are not exactly the standard Transaction variables".to_string())
    }
}

/// The ids of the non-key attributes the form shows, in form order, after checking the layout is
/// the design-system Transaction form: title, toolbar, the key, one row per shown attribute, and
/// the confirm, cancel and delete buttons.
fn check_layout(
    object: &KbObject,
    description: &str,
    members: &[Member],
    env: &WebPanelEnvironment,
) -> Result<Vec<u32>, String> {
    let text = part_text(object, "WEBFORM").ok_or("it has no WEBFORM layout")?;
    let actual = canonical_layout(text, env)?;
    let mut shown = Vec::new();
    for line in &actual {
        if let Some(rest) = line.trim_start().strip_prefix("data attribute=att:") {
            let id: u32 = rest
                .split(' ')
                .next()
                .and_then(|id| id.parse().ok())
                .ok_or("a form attribute reference is not an attribute id")?;
            shown.push(id);
        }
    }
    let Some((first, rest)) = shown.split_first() else {
        return Err("its form shows no attribute".to_string());
    };
    if *first != members[0].id {
        return Err("its form does not show the key first".to_string());
    }
    let rest = rest.to_vec();
    let mut seen = rest.clone();
    seen.sort_unstable();
    seen.dedup();
    if seen.len() != rest.len()
        || rest
            .iter()
            .any(|id| !members[1..].iter().any(|m| m.id == *id))
    {
        return Err("its form shows an attribute twice or one of another table".to_string());
    }
    // The stored caption names the attribute in full (`=<Attribute>.ContextualTitle`).
    let name_of = |id: u32| -> String {
        let member = members.iter().find(|m| m.id == id).expect("checked above");
        member.name.clone()
    };
    if actual
        .first()
        .is_none_or(|line| !line.starts_with("GxMultiForm rootId="))
    {
        return Err("its layout is not a GxMultiForm".to_string());
    }
    let expected = expected_layout(description, shown[0], &rest, &name_of);
    if actual[1..] != expected[1..] {
        let at = (1..actual.len().max(expected.len()))
            .find(|i| actual.get(*i) != expected.get(*i))
            .unwrap_or(0);
        return Err(format!(
            "its layout is not the design-system Transaction form (element {at}: '{}' where the form has '{}')",
            actual.get(at).map_or("", |l| l.trim()),
            expected.get(at).map_or("", |l| l.trim())
        ));
    }
    Ok(rest)
}

fn expected_layout(
    description: &str,
    key: u32,
    shown: &[u32],
    name_of: &dyn Fn(u32) -> String,
) -> Vec<String> {
    // The FormContainer's sizes name one row more than it holds: the KBTeste forms all do, and the
    // SDK reads only the rows it lays out.
    let widths = vec!["[{\"width\":100}]"; shown.len() + 1].join(",");
    let mut l = vec![
        "GxMultiForm".to_string(),
        "  Form type=layout".into(),
        "    detail PanelType=Web".into(),
        "      layout".into(),
        "        table controlName=MainTable tableType=Responsive responsiveSizes=[{\"scale\":\"sm\",\"rows\":[[{\"width\":100,\"offset\":0}],[{\"width\":100,\"offset\":0}],[{\"width\":100,\"offset\":0}]]}]".into(),
        "          row".into(),
        "            cell".into(),
        "              table controlName=TitleContainer class=title-container tableType=Responsive responsiveSizes=[]".into(),
        "                row".into(),
        "                  cell".into(),
        format!("                    textblock controlName=Title caption={description} class=heading-01"),
        "                row".into(),
        "                  cell".into(),
        "                    errorviewer controlName=ErrorViewer".into(),
        "          row".into(),
        "            cell".into(),
        format!("              table controlName=FormContainer class=form-container tableType=Responsive responsiveSizes=[{{\"scale\":\"sm\",\"rows\":[[{{\"width\":75,\"offset\":25}}],[{{\"width\":100,\"offset\":0}}],{widths}]}}]"),
        "                row".into(),
        "                  cell cellControlName=ToolbarCell cellClass=form__toolbar-cell".into(),
        "                    actiongroup controlName=Toolbar".into(),
        "                row".into(),
        "                  cell cellClass=form__cell-advanced".into(),
        format!("                    data attribute=att:{key} labelCaption=={}.ContextualTitle", name_of(key)),
    ];
    for id in shown {
        l.extend([
            "                row".to_string(),
            "                  cell cellClass=form__cell".into(),
            format!(
                "                    data attribute=att:{id} labelCaption=={}.ContextualTitle",
                name_of(*id)
            ),
        ]);
    }
    l.extend(
        [
            "          row",
            "            cell cellClass=form__actions--fixed hAlign=Right vAlign=Middle",
            "              actiongroup controlName=FormButtons",
            "        actionGroups",
            "          actionGroup controlName=FormButtons class=",
            "            item",
            "              action controlName=btn_enter onClickEvent=Enter caption=GX_BtnEnter class=button button-primary",
            "            item",
            "              action controlName=btn_cancel onClickEvent=Cancel caption=GX_BtnCancel class=button button-tertiary",
            "            item",
            "              action controlName=btn_delete onClickEvent=Delete caption=GX_BtnDelete class=button button-tertiary",
            "          actionGroup controlName=Toolbar",
            "            item",
            "              actionGroup controlName=NavButtons",
            "                item",
            "                  action controlName=btn_first onClickEvent=First caption= class=button button-auxiliary ico__arrow-first",
            "                item",
            "                  action controlName=btn_previous onClickEvent=Previous caption= class=button button-auxiliary ico__arrow-prev",
            "                item",
            "                  action controlName=btn_next onClickEvent=Next caption= class=button button-auxiliary ico__arrow-next",
            "                item",
            "                  action controlName=btn_last onClickEvent=Last caption= class=button button-auxiliary ico__arrow-last",
            "                item",
            "                  action controlName=btn_select onClickEvent=Select caption=GX_BtnSelect class=button button-secondary",
        ]
        .map(str::to_string),
    );
    l
}

/// The selection prompt the Transaction opens from its Select button: the one non-master WebPanel
/// it references, whose rules return its key.
fn prompt_of(object: &KbObject, catalog: &Catalog, key: &str) -> Result<String, String> {
    let mut prompts = Vec::new();
    for reference in &object.references {
        let Some(target) = catalog.get(&reference.target) else {
            continue;
        };
        if target.kind != ObjectKind::WebPanel {
            continue;
        }
        let settings = settings_of(target)?;
        if settings.web_comp.is_some() {
            continue;
        }
        prompts.push((target, settings.declared_name));
    }
    prompts.sort_by(|a, b| a.1.cmp(&b.1));
    prompts.dedup_by(|a, b| a.1 == b.1);
    let returns_key = |prompt: &KbObject| {
        part_text(prompt, "RULES")
            .unwrap_or("")
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ")
            == format!("parm(out:&p{key}); order({key});")
    };
    // A Transaction whose key references another one also opens that one's prompt; its own is the
    // one that returns its key.
    let returning: Vec<_> = prompts.iter().filter(|(p, _)| returns_key(p)).collect();
    let (_, prompt_name) = match (prompts.as_slice(), returning.as_slice()) {
        (_, [only]) => *only,
        ([(_, name)], []) => {
            return Err(format!(
                "its prompt '{name}' does not return '{key}' as a selection prompt does"
            ));
        }
        _ => return Err("it does not reference exactly one selection prompt".to_string()),
    };
    if !is_identifier(prompt_name) {
        return Err(format!(
            "its prompt name '{prompt_name}' is not a C# identifier"
        ));
    }
    Ok(prompt_name.to_ascii_lowercase())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_template_parses() {
        Template::parse(CUSTOM_TEMPLATE).expect("the custom-form template parses");
        Template::parse(DEFAULT_TEMPLATE).expect("the default-form template parses");
    }

    #[test]
    fn titles_break_after_a_lower_case_letter_or_a_digit() {
        assert_eq!(
            title_words("LiveMoveAttributeRules2Id").unwrap().join(" "),
            "Live Move Attribute Rules2 Id"
        );
        assert!(title_words("ABCode").is_err());
        assert!(title_words("A_B").is_err());
    }

    #[test]
    fn only_browser_message_events_are_admitted() {
        assert!(check_events("").is_ok());
        assert!(check_events("Event 'Ping'\n   msg('live')\nEndEvent\n").is_ok());
        assert!(check_events("Event Start\n   msg('live')\nEndEvent\n").is_err());
        assert!(check_events("Event 'Ping'\n   &x = 1\nEndEvent\n").is_err());
        assert!(check_events("Event 'Ping'\n   msg('a')\n   msg('b')\nEndEvent\n").is_err());
    }
}
