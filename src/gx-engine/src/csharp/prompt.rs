//! The class GeneXus 18 generates for a default selection prompt (`Gx0010` ...): the WebPanel the
//! Transaction's prompt pattern writes, with one filter per attribute, a grid of the same attributes
//! and a single `out:` parameter returning the key.
//!
//! The prompt is matched, not interpreted. Every stored part must be exactly what the pattern
//! writes for the attributes it names: the rules, the conditions and the events are regenerated and
//! compared, the layout is compared element by element with its design-system classes resolved, and
//! the variables are checked one by one. Anything else is refused by name, so a prompt a user has
//! edited is never emitted in the shape of the default one.
//!
//! The emitted class is `webform/prompt_selection.cs.tmpl`, checked byte for byte (the build stamp
//! aside) against the SDK's output for the seven KBTeste prompts: numeric keys of 4 digits, numeric
//! filters of 8 digits, `VarChar(40)` and `Character(20)` filters, one to three filters, and the
//! description column on the second or the last attribute.

use std::collections::BTreeMap;

use crate::catalog::{Catalog, KbObject, ObjectKind, ReferenceKind};
use crate::csharp::template::{Fields, Template, Value};
use crate::csharp::webform::{
    WebPanelEnvironment, configured, is_identifier, literal_ok, master_class_of, master_names,
    part_text, settings_of, three_digits,
};
use crate::model::DataType;
use crate::transaction::{TransactionAttributeRef, TransactionStructure};

const TEMPLATE: &str = include_str!("webform/prompt_selection.cs.tmpl");

/// The type GUID of an Attribute object, as an `idBasedOn` names it.
const ATTRIBUTE_TYPE: &str = "adbb33c9-0906-4971-833c-998de27e0676";
/// The type GUID of a design-system class, as a layout or an event names it.
const DESIGN_CLASS_TYPE: &str = "d52f9833-90c9-4096-8e42-27485741f78a";
/// The longest physical name the SDK keeps for an attribute (`LiveMoveAttributeRulesDescript`).
const PHYSICAL_NAME_LIMIT: usize = 30;

/// A matched selection prompt, ready to emit.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelectionPrompt {
    pub name: String,
    scope: Fields,
}

impl SelectionPrompt {
    pub fn file_name(&self) -> String {
        format!("{}.cs", self.name.to_ascii_lowercase())
    }

    /// The client script the SDK writes beside the class (`gx0010.js`), as (file name, text in the SDK's
    /// bytes). See `csharp::prompt_js`.
    pub fn script(&self) -> Result<(String, String), String> {
        super::prompt_js::prompt_script(&self.scope)
            .map(|text| (format!("{}.js", self.name.to_ascii_lowercase()), text))
    }

    /// The class text. `stamp` is the build stamp of its script references.
    pub fn emit(&self, stamp: &str) -> String {
        let mut scope = self.scope.clone();
        scope.insert("stamp".into(), stamp.into());
        Template::parse(TEMPLATE)
            .and_then(|template| template.render(&scope))
            .expect("the prompt template renders every scope the planner builds")
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum Kind {
    /// A numeric attribute without decimals, with its digit count.
    Number(u8),
    Character(u32),
    Varchar(u32),
}

#[derive(Debug, Clone)]
struct Filter {
    /// The attribute name as stored.
    name: String,
    variable_id: u32,
    attribute_id: u32,
    title: String,
    kind: Kind,
}

#[derive(Debug, Clone)]
struct GridColumn {
    description: bool,
    returns: bool,
    column_class: String,
}

struct Variables {
    advanced_id: u32,
    parameter_id: u32,
    parameter_attribute: u32,
    max_id: u32,
    filters: Vec<(u32, String, String, u32)>,
}

/// Plans a selection prompt, or says why the WebPanel is not one this emitter matches.
pub fn plan_selection_prompt(
    object: &KbObject,
    catalog: &Catalog,
    env: &WebPanelEnvironment,
) -> Result<SelectionPrompt, String> {
    if object.kind != ObjectKind::WebPanel {
        return Err("it is not a WebPanel".to_string());
    }
    let settings = settings_of(object)?;
    if settings.web_comp.is_some() {
        return Err("it is a web component or a master page, not a selection prompt".to_string());
    }
    let Some(master_id) = settings.master_page_id else {
        return Err("it names no master page, which a selection prompt does".to_string());
    };
    if !object.module_path.is_empty() {
        return Err(format!(
            "it is held by module '{}'; only prompts at the root module are matched",
            object.module_path.join(".")
        ));
    }
    let name = settings.declared_name.clone();
    if !is_identifier(&name) {
        return Err(format!("its name '{name}' is not a C# identifier"));
    }
    let masters = master_names(
        object,
        catalog,
        &[ReferenceKind::Uses, ReferenceKind::Calls],
    )?;
    let master = match masters.as_slice() {
        [only] => only.clone(),
        _ => {
            return Err(format!(
                "its MasterPage property {master_id} does not resolve to exactly one master page"
            ));
        }
    };
    let master_class = master_class_of(&master, catalog, env)?;

    let variables = read_variables(part_text(object, "VARIABLES").unwrap_or(""))?;
    let events = part_text(object, "EVENTS").unwrap_or("");
    let description = start_caption(events)?;
    if !literal_ok(&description) {
        return Err(format!(
            "its caption '{description}' has characters the emitter is not matched for"
        ));
    }
    let stored_description = settings.description.clone().unwrap_or_default();
    if stored_description != format!("Selection List {description}") {
        return Err(format!(
            "its description '{stored_description}' is not 'Selection List {description}'"
        ));
    }

    let key = variables
        .filters
        .first()
        .ok_or("it declares no filter variable")?
        .1
        .clone();
    if variables.filters[0].3 != variables.parameter_attribute {
        return Err(
            "its parameter and its first filter are based on different attributes".to_string(),
        );
    }
    let (transaction, structure) = transaction_of(&key, catalog)?;
    let mut filters = Vec::new();
    for (variable_id, attribute, title, attribute_id) in &variables.filters {
        let reference = structure
            .root
            .attributes
            .iter()
            .find(|a| &a.name == attribute)
            .ok_or_else(|| {
                format!("its filter attribute '{attribute}' is not an attribute of Transaction '{transaction}'")
            })?;
        filters.push(Filter {
            name: attribute.clone(),
            variable_id: *variable_id,
            attribute_id: *attribute_id,
            title: title.clone(),
            kind: kind_of(reference)?,
        });
        if !literal_ok(title) {
            return Err(format!(
                "its title '{title}' has characters the emitter is not matched for"
            ));
        }
    }
    if !matches!(filters[0].kind, Kind::Number(_)) {
        return Err("its key is not numeric; only numeric keys are matched".to_string());
    }

    let expected_rules = format!("parm(out:&p{key});\norder({key});");
    if normalized(part_text(object, "RULES").unwrap_or("")) != normalized(&expected_rules) {
        return Err("its rules are not the prompt pattern's".to_string());
    }
    if normalized(part_text(object, "CONDITIONS").unwrap_or(""))
        != normalized(&conditions(&filters))
    {
        return Err("its conditions are not the prompt pattern's".to_string());
    }
    if normalized(events) != normalized(&expected_events(&description, &filters)) {
        return Err("its events are not the prompt pattern's".to_string());
    }
    let columns = check_layout(object, &filters, env)?;

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
    let id = member[..2].to_string();
    let last_word = description
        .split_whitespace()
        .last()
        .ok_or("its caption is empty")?
        .to_string();

    let scope = build_scope(ScopeInput {
        name: &name,
        id: &id,
        table: &transaction,
        description: &description,
        filters: &filters,
        columns: &columns,
        variables: &variables,
        last_word: &last_word,
        theme,
        generator,
        build,
        master_class,
    })?;
    Ok(SelectionPrompt { name, scope })
}

fn normalized(text: &str) -> String {
    text.split_whitespace().collect::<Vec<_>>().join(" ")
}

fn kind_of(attribute: &TransactionAttributeRef) -> Result<Kind, String> {
    if attribute.formula.is_some() {
        return Err(format!("attribute '{}' is a formula", attribute.name));
    }
    match &attribute.data_type {
        Some(DataType::Numeric {
            precision,
            scale: 0,
        }) if (1..=9).contains(precision) => Ok(Kind::Number(*precision)),
        Some(DataType::Character { length }) if *length > 0 => Ok(Kind::Character(*length)),
        Some(DataType::Varchar { length }) if *length > 0 => Ok(Kind::Varchar(*length)),
        Some(other) => Err(format!(
            "attribute '{}' has type {other:?}, which the prompt emitter is not matched for",
            attribute.name
        )),
        None => Err(format!("attribute '{}' has no stored type", attribute.name)),
    }
}

/// The one single-level Transaction whose only key is `key`.
fn transaction_of(key: &str, catalog: &Catalog) -> Result<(String, TransactionStructure), String> {
    let mut found = Vec::new();
    for object in catalog.objects() {
        if object.kind != ObjectKind::Transaction {
            continue;
        }
        let Some(text) = part_text(object, "STRUCTURE") else {
            continue;
        };
        let Ok(structure) = serde_json::from_str::<TransactionStructure>(text) else {
            continue;
        };
        let keys: Vec<&str> = structure
            .root
            .attributes
            .iter()
            .filter(|a| a.is_key)
            .map(|a| a.name.as_str())
            .collect();
        if keys == [key] {
            found.push((object, structure));
        }
    }
    match found.as_slice() {
        [(object, structure)] => {
            if object.refusal.is_some() {
                return Err(format!("its Transaction '{}' is refused", object.name));
            }
            if !structure.root.levels.is_empty() {
                return Err(format!(
                    "its Transaction '{}' has nested levels",
                    object.name
                ));
            }
            Ok((structure.root.name.clone(), structure.clone()))
        }
        [] => Err(format!(
            "no decoded Transaction has '{key}' as its only key"
        )),
        _ => Err(format!(
            "several Transactions have '{key}' as their only key"
        )),
    }
}

fn start_caption(events: &str) -> Result<String, String> {
    let marker = "Format(\"GXSPC_SelectionList\", \"";
    let start = events
        .find(marker)
        .ok_or("its Start event does not set the selection-list caption")?
        + marker.len();
    let end = events[start..]
        .find('"')
        .ok_or("its caption is not a closed literal")?;
    Ok(events[start..start + end].to_string())
}

fn conditions(filters: &[Filter]) -> String {
    let mut out = format!("{0} >= &c{0} ;\n", filters[0].name);
    for filter in &filters[1..] {
        let op = match filter.kind {
            Kind::Number(_) => ">=",
            _ => "LIKE",
        };
        out.push_str(&format!(
            "{0} {op} &c{0} when not &c{0}.IsEmpty();\n",
            filter.name
        ));
    }
    out
}

fn expected_events(description: &str, filters: &[Filter]) -> String {
    let g = DESIGN_CLASS_TYPE;
    let mut out = format!(
        "Event Start\n\tForm.Caption = Format(\"GXSPC_SelectionList\", \"{description}\")\n\t&ADVANCED_LABEL_TEMPLATE = \"%1 <strong>%2</strong>\"\nEndEvent\n\n\
Event 'Toggle'\n\tif AdvancedContainer.Class = StyleClass:{g}AdvancedContainer\n\t\tAdvancedContainer.Class = StyleClass:{g}AdvancedContainer + !\" \" + StyleClass:{g}AdvancedContainerVisible\n\t\tBtnToggle.Class += !\" \" + StyleClass:{g}BtnToggleActive\n\telse\n\t\tAdvancedContainer.Class = StyleClass:{g}AdvancedContainer\n\t\tBtnToggle.Class = StyleClass:{g}BtnToggle\n\tendif\nEndevent\n"
    );
    for filter in filters {
        let n = &filter.name;
        out.push_str(&format!(
            "\nEvent Lbl{n}Filter.Click\n\tif {n}FilterContainer.Class = StyleClass:{g}AdvancedContainerItem\n\t\t{n}FilterContainer.Class = StyleClass:{g}AdvancedContainerItem + !\" \" + StyleClass:{g}AdvancedContainerItemExpanded\n\t\t&c{n}.Visible = true\n\telse\n\t\t{n}FilterContainer.Class = StyleClass:{g}AdvancedContainerItem\n\t\t&c{n}.Visible = false\n\tendif\nEndEvent\n"
        ));
    }
    out.push_str(&format!(
        "\nEvent Load\n\t&LinkSelection.FromImage(selectRow)\nEndEvent\n\nEvent Enter\n\t&p{0} = {0}\n\tReturn\nEndEvent\n",
        filters[0].name
    ));
    out
}

/// The `<Property><Name>..</Name><Value>..</Value></Property>` pairs of a properties node.
fn property_pairs(node: roxmltree::Node<'_, '_>) -> Result<Vec<(String, String)>, String> {
    let mut pairs = Vec::new();
    for property in node.children().filter(|n| n.is_element()) {
        if !property.has_tag_name("Property") {
            return Err(format!(
                "unexpected <{}> among properties",
                property.tag_name().name()
            ));
        }
        let text = |tag: &str| {
            property
                .children()
                .find(|c| c.has_tag_name(tag))
                .map(|c| c.text().unwrap_or("").to_string())
                .ok_or_else(|| format!("a property has no <{tag}>"))
        };
        pairs.push((text("Name")?, text("Value")?));
    }
    Ok(pairs)
}

fn attribute_of_based_on(value: &str) -> Result<u32, String> {
    value
        .strip_prefix(ATTRIBUTE_TYPE)
        .and_then(|rest| rest.strip_prefix('-'))
        .and_then(|id| id.parse().ok())
        .ok_or_else(|| format!("idBasedOn '{value}' is not an attribute"))
}

fn read_variables(text: &str) -> Result<Variables, String> {
    let document =
        roxmltree::Document::parse(text).map_err(|e| format!("its VARIABLES are not XML: {e}"))?;
    let mut link = None;
    let mut advanced = None;
    let mut parameter = None;
    let mut filters = Vec::new();
    let mut max_id = 0;
    for node in document
        .root_element()
        .children()
        .filter(|n| n.is_element())
    {
        let id: u32 = node
            .attribute("Id")
            .and_then(|id| id.parse().ok())
            .ok_or("a variable has no numeric Id")?;
        max_id = max_id.max(id);
        let props_node = node
            .children()
            .find(|c| c.has_tag_name("Properties"))
            .ok_or("a variable has no properties")?;
        let props: BTreeMap<String, String> = property_pairs(props_node)?.into_iter().collect();
        if node.has_tag_name("StandardVariable") {
            if props.get("IsStandardVariable").map(String::as_str) != Some("True") {
                return Err("a standard variable is not marked standard".to_string());
            }
            continue;
        }
        if !node.has_tag_name("Variable") {
            return Err(format!(
                "unexpected <{}> among variables",
                node.tag_name().name()
            ));
        }
        let name = props.get("Name").cloned().ok_or("a variable has no name")?;
        let allowed: &[&str] = match name.as_str() {
            "LinkSelection" | "ADVANCED_LABEL_TEMPLATE" => &["Name", "OBJ_TYPE", "ATTCUSTOMTYPE"],
            _ => &["Name", "Description", "OBJ_TYPE", "idBasedOn"],
        };
        if let Some(extra) = props.keys().find(|k| !allowed.contains(&k.as_str())) {
            return Err(format!(
                "variable '{name}' stores property {extra}, which is not matched"
            ));
        }
        let custom = props.get("ATTCUSTOMTYPE").map(String::as_str);
        match name.as_str() {
            "LinkSelection" => {
                if custom != Some("<AttCustomType><DataType>7</DataType></AttCustomType>") {
                    return Err("variable 'LinkSelection' is not an image".to_string());
                }
                link = Some(id);
            }
            "ADVANCED_LABEL_TEMPLATE" => {
                if custom != Some("<AttCustomType><DataType>5</DataType></AttCustomType>") {
                    return Err("variable 'ADVANCED_LABEL_TEMPLATE' is not a character".to_string());
                }
                advanced = Some(id);
            }
            _ => {
                let based = props
                    .get("idBasedOn")
                    .ok_or_else(|| format!("variable '{name}' is not based on an attribute"))?;
                let attribute = attribute_of_based_on(based)?;
                if let Some(attr) = name.strip_prefix('c') {
                    let title = props
                        .get("Description")
                        .cloned()
                        .ok_or_else(|| format!("variable '{name}' has no description"))?;
                    filters.push((id, attr.to_string(), title, attribute));
                } else if name.starts_with('p') && parameter.is_none() {
                    parameter = Some((id, name[1..].to_string(), attribute));
                } else {
                    return Err(format!(
                        "variable '{name}' is not one the prompt pattern declares"
                    ));
                }
            }
        }
    }
    let (parameter_id, parameter_name, parameter_attribute) =
        parameter.ok_or("it declares no parameter variable")?;
    if filters.first().map(|f| f.1.as_str()) != Some(parameter_name.as_str()) {
        return Err("its parameter is not its first filter's attribute".to_string());
    }
    let link_id = link.ok_or("it declares no LinkSelection")?;
    if link_id != 5 {
        return Err(format!("its LinkSelection has Id {link_id}, not 5"));
    }
    let mut expected_ids: Vec<u32> = (6..6 + filters.len() as u32).collect();
    expected_ids.push(6 + filters.len() as u32);
    let mut actual: Vec<u32> = filters.iter().map(|f| f.0).collect();
    actual.push(parameter_id);
    let advanced_id = advanced.ok_or("it declares no ADVANCED_LABEL_TEMPLATE")?;
    if actual != expected_ids || advanced_id != parameter_id + 1 || max_id != advanced_id {
        return Err("its variable ids are not in the prompt pattern's order".to_string());
    }
    Ok(Variables {
        advanced_id,
        parameter_id,
        parameter_attribute,
        max_id,
        filters,
    })
}

fn resolve_class(value: &str, env: &WebPanelEnvironment) -> Result<String, String> {
    value
        .split_whitespace()
        .map(|token| {
            if token.starts_with(DESIGN_CLASS_TYPE) {
                env.design_classes
                    .get(token)
                    .cloned()
                    .ok_or_else(|| format!("its layout names design-system class '{token}', which the KB does not resolve"))
            } else {
                Ok(token.to_string())
            }
        })
        .collect::<Result<Vec<_>, _>>()
        .map(|names| names.join(" "))
}

/// One line per element: its tag and its attributes but `id`, classes resolved and the pattern
/// properties decoded.
pub(crate) fn canonical_layout(
    text: &str,
    env: &WebPanelEnvironment,
) -> Result<Vec<String>, String> {
    let document =
        roxmltree::Document::parse(text).map_err(|e| format!("its WEBFORM is not XML: {e}"))?;
    let mut lines = Vec::new();
    fn walk(
        node: roxmltree::Node<'_, '_>,
        depth: usize,
        env: &WebPanelEnvironment,
        lines: &mut Vec<String>,
    ) -> Result<(), String> {
        let mut line = format!("{}{}", "  ".repeat(depth), node.tag_name().name());
        for attribute in node.attributes() {
            let name = attribute.name();
            if name == "id" {
                continue;
            }
            let value = match name {
                "class" | "cellClass" => resolve_class(attribute.value(), env)?,
                "PATTERN_ELEMENT_CUSTOM_PROPERTIES" => {
                    let inner = roxmltree::Document::parse(attribute.value())
                        .map_err(|e| format!("its layout properties are not XML: {e}"))?;
                    property_pairs(inner.root_element())?
                        .into_iter()
                        .map(|(k, v)| {
                            let v = if k == "ColumnClass" {
                                resolve_class(&v, env)?
                            } else {
                                v
                            };
                            Ok(format!("{k}={v}"))
                        })
                        .collect::<Result<Vec<_>, String>>()?
                        .join(";")
                }
                _ => attribute.value().to_string(),
            };
            let key = if name == "PATTERN_ELEMENT_CUSTOM_PROPERTIES" {
                "props"
            } else {
                name
            };
            line.push_str(&format!(" {key}={value}"));
        }
        lines.push(line);
        for child in node.children().filter(|n| n.is_element()) {
            walk(child, depth + 1, env, lines)?;
        }
        Ok(())
    }
    walk(document.root_element(), 0, env, &mut lines)?;
    Ok(lines)
}

fn expected_layout(filters: &[Filter], columns: &[GridColumn]) -> Vec<String> {
    let w100 = vec!["[{\"width\":100}]"; filters.len()].join(",");
    let mut l = vec![
        "GxMultiForm rootId=1 version=html:15.0.0;layout:17.11.0".to_string(),
        "  Form type=layout".into(),
        "    detail PanelType=Web".into(),
        "      layout".into(),
        "        table controlName=Main class=ContainerFluid PromptContainer tableType=Responsive responsiveSizes=[{\"scale\":\"sm\",\"rows\":[[{\"width\":25},{\"width\":75}]]}]".into(),
        "          row".into(),
        "            cell cellClass=PromptAdvancedBarCell".into(),
        format!("              table controlName=AdvancedContainer class=AdvancedContainerVisible tableType=Responsive responsiveSizes=[{{\"scale\":\"sm\",\"rows\":[{w100}]}}]"),
    ];
    for filter in filters {
        let n = &filter.name;
        l.extend([
            "                row".to_string(),
            "                  cell".into(),
            format!("                    table controlName={n}FilterContainer class=AdvancedContainerItem AdvancedContainerItemExpanded tableType=Responsive responsiveSizes=[{{\"scale\":\"sm\",\"rows\":[[{{\"width\":100}}],[{{\"width\":100}}]]}}]"),
            "                      row".into(),
            "                        cell".into(),
            format!("                          textblock controlName=Lbl{n}Filter caption=={n}.Title class=WWAdvancedLabel WWFilterLabel props=GxFormat=HTML"),
            "                      row".into(),
            "                        cell cellClass=WWFiltersCell".into(),
            format!("                          data attribute=var:{} labelPosition=None", filter.variable_id),
        ]);
    }
    l.extend([
        "            cell cellClass=WWGridCell".to_string(),
        "              table controlName=GridTable tableType=Responsive responsiveSizes=[{\"scale\":\"sm\",\"rows\":[[{\"visible\":false}],[{\"width\":100}],[{\"width\":100}]]},{\"scale\":\"xs\",\"rows\":[[{\"width\":100}]]},{\"scale\":\"md\",\"rows\":[[{\"visible\":false}]]},{\"scale\":\"lg\",\"rows\":[[{\"visible\":false}]]}]".into(),
        "                row".into(),
        "                  cell cellClass=ToggleCell".into(),
        "                    action controlName=BtnToggle onClickEvent='Toggle' caption=||| class=BtnToggle".into(),
        "                row".into(),
        "                  cell".into(),
        "                    simplegrid controlName=Grid1 class=PromptGrid props=MaxRows=10".into(),
        "                      item attribute=var:5 titleExp= class=SelectionAttribute props=ColumnClass=WWActionColumn;ReturnOnClick=True".into(),
    ]);
    for (filter, column) in filters.iter().zip(columns) {
        let class = if column.description {
            " class=DescriptionAttribute"
        } else {
            ""
        };
        let returns = if column.returns {
            ";ReturnOnClick=True;ColReadOnly=True"
        } else {
            ""
        };
        l.push(format!(
            "                      item attribute=att:{} titleExp=={}.ContextualTitle{class} props=ColumnClass={}{returns}",
            filter.attribute_id, filter.name, column.column_class
        ));
    }
    l.extend([
        "                row".to_string(),
        "                  cell".into(),
        "                    action controlName=btn_cancel onClickEvent=Cancel caption=GX_BtnCancel class=BtnCancel".into(),
    ]);
    l
}

/// Checks the layout is the prompt pattern's for `filters` and returns its grid columns. The
/// column choices the pattern varies (which column is the description and returns on click, which
/// is optional) are read from the layout and must be ones the emitter is matched for.
fn check_layout(
    object: &KbObject,
    filters: &[Filter],
    env: &WebPanelEnvironment,
) -> Result<Vec<GridColumn>, String> {
    let text = part_text(object, "WEBFORM").ok_or("it has no WEBFORM layout")?;
    let actual = canonical_layout(text, env)?;
    let mut columns = Vec::new();
    let items: Vec<&String> = actual
        .iter()
        .filter(|line| line.trim_start().starts_with("item attribute=att:"))
        .collect();
    if items.len() != filters.len() {
        return Err("its grid does not show exactly its filter attributes".to_string());
    }
    for line in items {
        let description = line.contains(" class=DescriptionAttribute ");
        let returns = line.ends_with(";ReturnOnClick=True;ColReadOnly=True");
        let column_class = line
            .split(" props=ColumnClass=")
            .nth(1)
            .map(|rest| {
                rest.trim_end_matches(";ReturnOnClick=True;ColReadOnly=True")
                    .to_string()
            })
            .ok_or("a grid column has no column class")?;
        if !matches!(
            column_class.as_str(),
            "WWColumn" | "WWColumn OptionalColumn"
        ) {
            return Err(format!(
                "a grid column has class '{column_class}', which is not matched"
            ));
        }
        if description && !returns {
            return Err("its description column does not return on click".to_string());
        }
        columns.push(GridColumn {
            description,
            returns,
            column_class,
        });
    }
    if actual != expected_layout(filters, &columns) {
        return Err("its layout is not the prompt pattern's".to_string());
    }
    Ok(columns)
}

struct ScopeInput<'a> {
    name: &'a str,
    id: &'a str,
    table: &'a str,
    description: &'a str,
    filters: &'a [Filter],
    columns: &'a [GridColumn],
    variables: &'a Variables,
    last_word: &'a str,
    theme: String,
    generator: String,
    build: String,
    master_class: String,
}

pub(crate) fn capitalized_lower(name: &str) -> String {
    let lower = name.to_ascii_lowercase();
    let mut chars = lower.chars();
    match chars.next() {
        Some(first) => first.to_ascii_uppercase().to_string() + chars.as_str(),
        None => String::new(),
    }
}

fn text(fields: &mut Fields, key: &str, value: impl Into<String>) {
    fields.insert(key.to_string(), Value::Text(value.into()));
}

fn flag(fields: &mut Fields, key: &str, value: bool) {
    fields.insert(key.to_string(), Value::Flag(value));
}

fn build_scope(input: ScopeInput<'_>) -> Result<Fields, String> {
    let n = input.filters.len();
    let mut items = Vec::new();
    for (k, (filter, column)) in input.filters.iter().zip(input.columns).enumerate() {
        let full = &filter.name;
        let physical: String = full.chars().take(PHYSICAL_NAME_LIMIT).collect();
        let v = filter.variable_id;
        let words: Vec<&str> = filter.title.split_whitespace().collect();
        let start = words
            .iter()
            .position(|word| *word == input.last_word)
            .ok_or_else(|| {
                format!(
                    "the title '{}' does not hold '{}', so its contextual title is not evidenced",
                    filter.title, input.last_word
                )
            })?;
        let mut f = Fields::new();
        text(&mut f, "cav", format!("AV{v}c{full}"));
        text(&mut f, "lcav", format!("lV{v}c{full}"));
        text(&mut f, "cname", format!("c{full}"));
        text(&mut f, "VCUP", format!("vC{}", full.to_ascii_uppercase()));
        text(
            &mut f,
            "edtav",
            format!("edtavC{}", full.to_ascii_lowercase()),
        );
        text(
            &mut f,
            "lbl",
            format!("lblLbl{}filter", full.to_ascii_lowercase()),
        );
        text(
            &mut f,
            "LBLUP",
            format!("LBL{}FILTER", full.to_ascii_uppercase()),
        );
        text(
            &mut f,
            "div",
            format!("div{}filtercontainer", capitalized_lower(full)),
        );
        text(
            &mut f,
            "DIVUP",
            format!("{}FILTERCONTAINER", full.to_ascii_uppercase()),
        );
        text(&mut f, "lower", full.to_ascii_lowercase());
        text(&mut f, "edt", format!("edt{physical}"));
        text(
            &mut f,
            "aname",
            format!("A{}{physical}", filter.attribute_id),
        );
        text(&mut f, "N", physical.clone());
        text(&mut f, "UP", physical.to_ascii_uppercase());
        text(
            &mut f,
            "valid",
            format!("Valid_{}", capitalized_lower(&physical)),
        );
        text(&mut f, "title", filter.title.clone());
        text(&mut f, "ctx", words[start..].join(" "));
        text(&mut f, "ev", (11 + k).to_string());
        text(&mut f, "ctl", (16 + 10 * k).to_string());
        text(&mut f, "idx", k.to_string());
        flag(&mut f, "first", k == 0);
        flag(&mut f, "opt", k > 0);
        let (ctype, len, num, char_kind) = match filter.kind {
            Kind::Number(digits) => (
                if digits <= 4 { "short" } else { "int" },
                u32::from(digits),
                true,
                false,
            ),
            Kind::Character(length) => ("string", length, false, true),
            Kind::Varchar(length) => ("string", length, false, false),
        };
        text(&mut f, "ctype", ctype);
        text(&mut f, "len", len.to_string());
        flag(&mut f, "num", num);
        flag(&mut f, "char", char_kind);
        flag(&mut f, "vchar", !num && !char_kind);
        flag(&mut f, "str", !num);
        flag(&mut f, "short", ctype == "short");
        flag(&mut f, "int", ctype == "int");
        if num {
            text(&mut f, "pic", format!("{}9", "Z".repeat(len as usize - 1)));
            text(&mut f, "max", "9".repeat(len as usize));
        } else {
            text(&mut f, "pic", "");
            text(&mut f, "max", "");
        }
        text(&mut f, "tc", ctype.to_ascii_uppercase());
        text(
            &mut f,
            "gxtype",
            match (ctype, char_kind) {
                ("short", _) => "Int16",
                ("int", _) => "Int32",
                (_, true) => "NChar",
                _ => "NVarChar",
            },
        );
        text(
            &mut f,
            "getter",
            match ctype {
                "short" => "Short",
                "int" => "Int",
                _ => "",
            },
        );
        text(
            &mut f,
            "pname_sql",
            if num {
                format!("AV{v}c{full}")
            } else {
                format!("lV{v}c{full}")
            },
        );
        flag(&mut f, "ret", column.returns);
        text(
            &mut f,
            "rocls",
            if column.description {
                "DescriptionAttribute"
            } else {
                "Attribute"
            },
        );
        text(&mut f, "colcls", column.column_class.clone());
        text(&mut f, "align", if num { "end" } else { "start" });
        items.push(f);
    }
    let get = |f: &Fields, key: &str| match f.get(key) {
        Some(Value::Text(t)) => t.clone(),
        _ => String::new(),
    };
    let is = |f: &Fields, key: &str| matches!(f.get(key), Some(Value::Flag(true)));

    let mut reversed = items.clone();
    reversed.reverse();
    for (i, f) in reversed.iter_mut().enumerate() {
        text(f, "res", i.to_string());
        let getres = if is(f, "num") {
            format!("get{}({})", get(f, "getter"), i + 1)
        } else if is(f, "char") {
            format!("getString({}, {})", i + 1, get(f, "len"))
        } else {
            format!("getVarchar({})", i + 1)
        };
        text(f, "getres", getres);
    }
    let opt: Vec<Fields> = items[1..].to_vec();
    let key = items[0].clone();

    let mut scope = Fields::new();
    text(&mut scope, "name", input.name);
    text(&mut scope, "lname", input.name.to_ascii_lowercase());
    text(&mut scope, "id", input.id);
    text(&mut scope, "lid", input.id.to_ascii_lowercase());
    text(&mut scope, "grid", (14 + 10 * n).to_string());
    text(&mut scope, "n", n.to_string());
    flag(&mut scope, "dyn", n > 1);
    text(&mut scope, "table", input.table);
    text(&mut scope, "desc", input.description);
    text(&mut scope, "theme", input.theme);
    text(&mut scope, "generator", input.generator);
    text(&mut scope, "build", input.build);
    text(&mut scope, "master_class", input.master_class);
    let key_name = input.filters[0].name.clone();
    text(
        &mut scope,
        "pav",
        format!("AV{}p{key_name}", input.variables.parameter_id),
    );
    text(&mut scope, "pname", format!("p{key_name}"));
    text(
        &mut scope,
        "PUP",
        format!("P{}", key_name.to_ascii_uppercase()),
    );
    text(&mut scope, "gxi", (input.variables.max_id + 1).to_string());
    text(&mut scope, "adv", input.variables.advanced_id.to_string());
    text(&mut scope, "ev_toggle", (11 + n).to_string());
    text(&mut scope, "ev_start", (12 + n).to_string());
    text(&mut scope, "ev_load", (13 + n).to_string());
    text(&mut scope, "ev_enter", (14 + n).to_string());
    text(&mut scope, "ctl_toggle", (11 + 10 * n).to_string());
    text(&mut scope, "ctl_cancel", (14 + 10 * n + n + 4).to_string());
    for (field, value) in &key {
        scope.insert(format!("k_{field}"), value.clone());
    }

    let nums: Vec<String> = opt
        .iter()
        .chain(opt.iter())
        .chain(std::iter::once(&key))
        .filter(|f| is(f, "num"))
        .map(|f| format!("TypeConstants.{}", get(f, "tc")))
        .collect();
    text(&mut scope, "dyn_types", nums.join(", "));
    text(
        &mut scope,
        "exec_args",
        items
            .iter()
            .map(|f| {
                if is(f, "num") {
                    get(f, "cav")
                } else {
                    get(f, "lcav")
                }
            })
            .collect::<Vec<_>>()
            .join(", "),
    );
    let mut dynlist = Vec::new();
    for (field, f) in opt
        .iter()
        .map(|f| ("cav", f))
        .chain(opt.iter().map(|f| ("aname", f)))
        .chain(std::iter::once(("cav", &key)))
    {
        let mut d = Fields::new();
        text(&mut d, "ctype", get(f, "ctype"));
        text(&mut d, "name", get(f, field));
        dynlist.push(d);
    }
    text(
        &mut scope,
        "dyn_casts",
        dynlist
            .iter()
            .enumerate()
            .map(|(i, d)| format!("({})dynConstraints[{i}]", get(d, "ctype")))
            .collect::<Vec<_>>()
            .join(" , "),
    );
    text(
        &mut scope,
        "iparms",
        items
            .iter()
            .map(|f| {
                let pic = if is(f, "num") {
                    format!(",\"pic\":\"{}\"", get(f, "pic"))
                } else {
                    String::new()
                };
                format!(
                    "{{\"av\":\"{}\",\"fld\":\"{}\"{pic}}}",
                    get(f, "cav"),
                    get(f, "VCUP")
                )
            })
            .collect::<Vec<_>>()
            .join(","),
    );
    text(
        &mut scope,
        "last_valid",
        get(items.last().expect("one filter"), "valid"),
    );
    text(
        &mut scope,
        "h_arrays",
        reversed
            .iter()
            .map(|f| format!("H00{}2_{}", input.id, get(f, "aname")))
            .collect::<Vec<_>>()
            .join(", "),
    );
    text(
        &mut scope,
        "select_cols",
        reversed
            .iter()
            .map(|f| format!("[{}]", get(f, "N")))
            .collect::<Vec<_>>()
            .join(", "),
    );
    let strf: Vec<Fields> = items.iter().filter(|f| is(f, "str")).cloned().collect();
    let optstr: Vec<Fields> = opt.iter().filter(|f| is(f, "str")).cloned().collect();
    scope.insert("filters".into(), Value::List(items.clone()));
    scope.insert("rfilters".into(), Value::List(reversed));
    scope.insert("opt".into(), Value::List(opt));
    scope.insert("rest".into(), Value::List(items[1..].to_vec()));
    scope.insert("strf".into(), Value::List(strf));
    scope.insert("optstr".into(), Value::List(optstr));
    scope.insert("dynlist".into(), Value::List(dynlist));
    Ok(scope)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_template_parses() {
        Template::parse(TEMPLATE).expect("the prompt template parses");
    }
}
