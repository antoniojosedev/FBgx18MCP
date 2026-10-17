//! Read-only compilation projection of bounded authored SDK XML exchanges.
//! Not a native KB decoder, lossless roundtrip/importer, or XPZ archive extractor.
use crate::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    model::{DataType, Project},
};
use roxmltree::{Document, Node, ParsingOptions};
use std::collections::{BTreeMap, BTreeSet};

const PROCEDURE: &str = "84a12160-f59b-4ad7-a683-ea4481ac23e9";
const SDT: &str = "447527b5-9210-4523-898b-5dccb17be60a";
const DOMAIN: &str = "00972a17-9975-449e-aab1-d26165d51393";
const ATTRIBUTE: &str = "adbb33c9-0906-4971-833c-998de27e0676";
const SOURCE: &str = "528d1c06-a9c2-420d-bd35-21dca83f12ff";
const RULES: &str = "9b0a32a3-de6d-4be1-a4dd-1b85d3741534";
const VARIABLES: &str = "e4c4ade7-53f0-4a56-bdfd-843735b66f47";
const STRUCTURE: &str = "5c2aa9da-8fc4-4b6b-ae02-8db4fa48976a";
pub const MAX_XML_BYTES: usize = 8 * 1024 * 1024;

fn child<'a, 'input>(
    node: Node<'a, 'input>,
    name: &str,
) -> Result<Option<Node<'a, 'input>>, String> {
    let mut found = node.children().filter(|n| n.has_tag_name(name));
    let result = found.next();
    if found.next().is_some() {
        return Err(format!("duplicate XML {name} element"));
    }
    Ok(result)
}

fn required<'a, 'input>(node: Node<'a, 'input>, name: &str) -> Result<Node<'a, 'input>, String> {
    child(node, name)?.ok_or_else(|| format!("missing XML {name} element"))
}

fn text(node: Node<'_, '_>) -> Result<String, String> {
    if node.children().any(|n| n.is_element()) {
        return Err("nested XML in scalar text".into());
    }
    Ok(node
        .children()
        .filter(|n| n.is_text())
        .filter_map(|n| n.text())
        .collect())
}

fn only_children(node: Node<'_, '_>, allowed: &[&str]) -> Result<(), String> {
    if node
        .children()
        .any(|n| n.is_element() && !allowed.contains(&n.tag_name().name()))
    {
        return Err("unsupported authored XML content".into());
    }
    Ok(())
}

fn properties(node: Node<'_, '_>) -> Result<BTreeMap<String, String>, String> {
    let mut result = BTreeMap::new();
    if let Some(properties) = child(node, "Properties")? {
        for property in properties.children().filter(|n| n.is_element()) {
            if !property.has_tag_name("Property") {
                return Err("unsupported XML property node".into());
            }
            only_children(property, &["Name", "Value"])?;
            let name = text(required(property, "Name")?)?.to_ascii_uppercase();
            let value = text(required(property, "Value")?)?;
            if result.insert(name, value).is_some() {
                return Err("duplicate XML property".into());
            }
        }
    }
    Ok(result)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ParsedTypeDefinition {
    Concrete(DataType),
    BasedOn(String),
}

impl From<ParsedTypeDefinition> for crate::kb::RawCatalogType {
    fn from(parsed: ParsedTypeDefinition) -> Self {
        match parsed {
            ParsedTypeDefinition::Concrete(dt) => crate::kb::RawCatalogType::Concrete(dt),
            ParsedTypeDefinition::BasedOn(target) => {
                crate::kb::RawCatalogType::BasedOnShort { target }
            }
        }
    }
}

fn strip_type_prefix(value: &str) -> String {
    let trimmed = value.trim();
    for prefix in &[
        "domain:",
        "Domain:",
        "dom:",
        "attribute:",
        "Attribute:",
        "att:",
    ] {
        if let Some(rest) = trimmed.strip_prefix(prefix) {
            return rest.trim().to_owned();
        }
    }
    trimmed.to_owned()
}

fn parse_type_definition(
    properties: &BTreeMap<String, String>,
) -> Result<ParsedTypeDefinition, String> {
    for name in properties.keys() {
        if !matches!(
            name.as_str(),
            "NAME"
                | "ATTCUSTOMTYPE"
                | "DATATYPESTRING"
                | "LENGTH"
                | "DECIMALS"
                | "ATTMAXLEN"
                | "ATTAVGLEN"
                | "ATTCOLLECTION"
                | "DOMAINBASEDON"
                | "BASEDON"
                | "IDBASEDON"
                | "ATTRIBUTEBASEDON"
                | "TYPE"
                | "DESCRIPTION"
                | "CALL_PROTOCOL"
                | "ISDEFAULT"
                | "ISMAIN"
        ) {
            return Err(format!("unsupported XML declaration property {name}"));
        }
    }
    match properties.get("ATTCOLLECTION") {
        None => {}
        Some(value) if value.trim().eq_ignore_ascii_case("false") => {}
        Some(value) if value.trim().eq_ignore_ascii_case("true") => {
            return Err("item-level SDT collections are not admitted".into());
        }
        Some(_) => return Err("invalid XML AttCollection boolean".into()),
    }
    for based_key in &["DOMAINBASEDON", "BASEDON", "IDBASEDON", "ATTRIBUTEBASEDON"] {
        if let Some(target) = properties.get(*based_key) {
            let stripped = strip_type_prefix(target);
            if !stripped.is_empty() {
                return Ok(ParsedTypeDefinition::BasedOn(stripped));
            }
        }
    }
    let kind = properties
        .get("ATTCUSTOMTYPE")
        .or_else(|| properties.get("TYPE"))
        .or_else(|| properties.get("DATATYPESTRING"))
        .ok_or("XML declaration has no type property")?;

    if let Some(schema) = kind
        .strip_prefix("dom:")
        .or_else(|| kind.strip_prefix("domain:"))
        .or_else(|| kind.strip_prefix("Domain:"))
    {
        return Ok(ParsedTypeDefinition::BasedOn(schema.trim().to_owned()));
    }
    if let Some(schema) = kind
        .strip_prefix("att:")
        .or_else(|| kind.strip_prefix("attribute:"))
        .or_else(|| kind.strip_prefix("Attribute:"))
    {
        return Ok(ParsedTypeDefinition::BasedOn(schema.trim().to_owned()));
    }
    if let Some(schema) = kind.strip_prefix("sdt:") {
        return Ok(ParsedTypeDefinition::Concrete(DataType::Record {
            schema: schema.trim().to_owned(),
        }));
    }

    let number = || -> Result<u32, String> {
        if let Some(val) = properties
            .get("LENGTH")
            .or_else(|| properties.get("ATTMAXLEN"))
        {
            val.parse().map_err(|_| "invalid XML length".into())
        } else {
            Err("missing XML length".into())
        }
    };

    let data_type = match kind.to_ascii_lowercase().as_str() {
        "bas:boolean" | "boolean" => DataType::Boolean {},
        "bas:date" | "date" => DataType::Date {},
        "bas:datetime" | "datetime" => DataType::DateTime {},
        "bas:character" | "character" => DataType::Character { length: number()? },
        "bas:varchar" | "varchar" => DataType::Varchar { length: number()? },
        "bas:numeric" | "numeric" => DataType::Numeric {
            precision: number()?
                .try_into()
                .map_err(|_| "numeric precision exceeds u8")?,
            scale: properties
                .get("DECIMALS")
                .map(|v| v.parse::<u8>())
                .transpose()
                .map_err(|_| "invalid XML decimals")?
                .unwrap_or(0),
        },
        _ => return Err(format!("unsupported XML datatype {kind}")),
    };
    crate::model::validate_data_type(&data_type)?;
    Ok(ParsedTypeDefinition::Concrete(data_type))
}

fn declaration_name(node: Node<'_, '_>, attribute: &str) -> Result<String, String> {
    let name = node
        .attribute(attribute)
        .ok_or("XML declaration has no name")?
        .to_ascii_uppercase();
    if let Some(property_name) = properties(node)?.get("NAME")
        && !property_name.eq_ignore_ascii_case(&name)
    {
        return Err("XML name attribute/property mismatch".into());
    }
    Ok(name)
}

fn declaration(
    node: Node<'_, '_>,
    attribute: &str,
) -> Result<(String, ParsedTypeDefinition), String> {
    only_children(node, &["Properties", "Documentation"])?;
    if let Some(documentation) = child(node, "Documentation")?
        && !text(documentation)?.trim().is_empty()
    {
        return Err("authored declaration documentation is not admitted".into());
    }
    let props = properties(node)?;
    Ok((
        declaration_name(node, attribute)?,
        parse_type_definition(&props)?,
    ))
}

fn declarations(
    node: Node<'_, '_>,
    tag: &str,
    attribute: &str,
) -> Result<BTreeMap<String, ParsedTypeDefinition>, String> {
    let mut result = BTreeMap::new();
    for item in node.children().filter(|n| n.is_element()) {
        if item.has_tag_name("Properties") {
            continue;
        }
        if !item.has_tag_name(tag) {
            return Err("unsupported nested XML declaration".into());
        }
        let (name, data_type) = declaration(item, attribute)?;
        if result.insert(name, data_type).is_some() {
            return Err("duplicate XML declaration".into());
        }
        if result.len() > 1024 {
            return Err("XML exceeds 1024 declarations".into());
        }
    }
    Ok(result)
}

#[derive(Debug)]
struct LevelMetadata {
    guid: String,
    name: String,
    collection: bool,
    collection_item_name: Option<String>,
}

#[derive(Debug)]
struct NestedSchema {
    id: ObjectId,
    schema_name: String,
    fields: BTreeMap<String, crate::kb::RawCatalogType>,
    // Direct child Level schemas this schema must declare as explicit Uses.
    uses: Vec<ObjectId>,
    // Direct Domain or Attribute dependencies this schema must declare as explicit BasedOn.
    based_on: Vec<ObjectId>,
}

#[derive(Debug)]
struct SdtProjection {
    fields: BTreeMap<String, crate::kb::RawCatalogType>,
    // Direct child Level schemas of the root Level.
    direct_uses: Vec<ObjectId>,
    // Direct Domain or Attribute dependencies of the root Level.
    direct_based_on: Vec<ObjectId>,
    // Every descendant Level schema, depth-first.
    nested: Vec<NestedSchema>,
}

// Shared across SDTs so synthetic identities and type aliases stay globally unique.
struct SdtProjectionContext<'a> {
    top_ids: BTreeSet<ObjectId>,
    top_names: BTreeSet<String>,
    seen_guids: BTreeSet<String>,
    synthetic_ids: BTreeSet<ObjectId>,
    synthetic_names: BTreeSet<String>,
    nested_aliases: BTreeMap<String, (String, ObjectId)>,
    identities: &'a BTreeMap<String, (ObjectId, ObjectKind)>,
}

impl<'a> SdtProjectionContext<'a> {
    fn new(
        top_ids: BTreeSet<ObjectId>,
        top_names: BTreeSet<String>,
        identities: &'a BTreeMap<String, (ObjectId, ObjectKind)>,
    ) -> Self {
        Self {
            top_ids,
            top_names,
            seen_guids: BTreeSet::new(),
            synthetic_ids: BTreeSet::new(),
            synthetic_names: BTreeSet::new(),
            nested_aliases: BTreeMap::new(),
            identities,
        }
    }
}

fn normalized_level_name(value: &str, field: &str) -> Result<String, String> {
    let value = value.trim();
    if !crate::model::is_identifier(value) {
        return Err(format!("{field} must be a nonempty ASCII identifier"));
    }
    Ok(value.to_ascii_uppercase())
}

fn canonical_guid(value: &str) -> Result<String, String> {
    let bytes = value.as_bytes();
    if bytes.len() != 36
        || !bytes.iter().enumerate().all(|(index, byte)| match index {
            8 | 13 | 18 | 23 => *byte == b'-',
            _ => byte.is_ascii_hexdigit(),
        })
    {
        return Err("SDT LevelInfo has an invalid GUID".into());
    }
    Ok(value.to_ascii_lowercase())
}

fn level_name(level: Node<'_, '_>) -> Result<String, String> {
    for attribute in level.attributes() {
        if attribute.name() != "Name" {
            return Err("unsupported SDT Level attribute".into());
        }
    }
    let name = normalized_level_name(
        level.attribute("Name").ok_or("SDT Level has no name")?,
        "SDT Level name",
    )?;
    let level_properties = properties(level)?;
    if level_properties.keys().any(|key| key != "NAME") {
        return Err("unsupported SDT Level properties".into());
    }
    if level_properties
        .get("NAME")
        .is_some_and(|property_name| !property_name.trim().eq_ignore_ascii_case(&name))
    {
        return Err("SDT Level name attribute/property mismatch".into());
    }
    Ok(name)
}

fn level_metadata(level: Node<'_, '_>, root: bool) -> Result<Option<LevelMetadata>, String> {
    let Some(info) = child(level, "LevelInfo")? else {
        return Ok(None);
    };
    for attribute in info.attributes() {
        if !matches!(
            attribute.name(),
            "guid" | "name" | "type" | "description" | "user"
        ) {
            return Err("unsupported SDT LevelInfo attribute".into());
        }
    }
    let guid = canonical_guid(
        info.attribute("guid")
            .filter(|value| !value.trim().is_empty())
            .ok_or("SDT LevelInfo has no GUID")?,
    )?;
    let name = normalized_level_name(
        info.attribute("name")
            .filter(|value| !value.trim().is_empty())
            .ok_or("SDT LevelInfo has no name")?,
        "SDT LevelInfo name",
    )?;
    only_children(info, &["Properties"])?;
    let level_properties = properties(info)?;
    for key in level_properties.keys() {
        if !matches!(key.as_str(), "NAME")
            && (root || !matches!(key.as_str(), "ATTCOLLECTION" | "IDCOLLECTIONITEMNAME"))
        {
            return Err("unsupported SDT LevelInfo property".into());
        }
    }
    if level_properties
        .get("NAME")
        .is_some_and(|property_name| !property_name.trim().eq_ignore_ascii_case(&name))
    {
        return Err("SDT LevelInfo name attribute/property mismatch".into());
    }
    let collection = match level_properties.get("ATTCOLLECTION") {
        Some(value) if value.trim().eq_ignore_ascii_case("true") => true,
        Some(value) if value.trim().eq_ignore_ascii_case("false") => false,
        Some(_) => return Err("invalid SDT AttCollection boolean".into()),
        None => false,
    };
    let explicit_item_name = level_properties.get("IDCOLLECTIONITEMNAME");
    if explicit_item_name.is_some() && !collection {
        return Err("idCollectionItemName requires AttCollection=True".into());
    }
    let collection_item_name = if collection {
        let item_name = match explicit_item_name {
            Some(value) => normalized_level_name(value, "SDT collection item name")?,
            None => normalized_level_name(&format!("{name}Item"), "SDT collection item name")?,
        };
        Some(item_name)
    } else {
        None
    };
    Ok(Some(LevelMetadata {
        guid,
        name,
        collection,
        collection_item_name,
    }))
}

/// One projected SDT `<Level>`: its scalar Items, the schema identities of its
/// direct child Levels, and every descendant schema depth-first.
struct ProjectedLevel {
    fields: BTreeMap<String, crate::kb::RawCatalogType>,
    direct_uses: Vec<ObjectId>,
    direct_based_on: Vec<ObjectId>,
    nested: Vec<NestedSchema>,
}

/// Projects one SDT `<Level>`: its scalar Items plus each nested `<Level>` as a
/// synthetic schema, recursively to the bounded XML depth. Native type aliases
/// are registered only where evidence exists: direct children of the root
/// register `Root.Name` (composite) or `Root.Item` (collection item), and
/// depth-2 collections register the full-path item alias `Root.Child.Item`.
/// Deeper collections and composites beyond depth 1 stay unregistered.
///
/// `depth` is this level's distance from the root (root = 0) and `path` is this
/// level's own dotted path from the root, used to build child aliases.
fn project_level<'a>(
    level: Node<'_, '_>,
    context: &mut SdtProjectionContext<'a>,
    depth: usize,
    path: &str,
    seen_level_names: &mut BTreeSet<String>,
) -> Result<ProjectedLevel, String> {
    only_children(level, &["Properties", "LevelInfo", "Item", "Level"])?;
    let mut fields: BTreeMap<String, crate::kb::RawCatalogType> = BTreeMap::new();
    let mut direct_uses = Vec::new();
    let mut direct_based_on = Vec::new();
    let mut nested = Vec::new();
    for member in level.children().filter(|node| node.is_element()) {
        if member.has_tag_name("Properties") || member.has_tag_name("LevelInfo") {
            continue;
        }
        if member.has_tag_name("Item") {
            let (name, parsed_type) = declaration(member, "name")?;
            match parsed_type {
                ParsedTypeDefinition::Concrete(data_type) => {
                    if matches!(
                        data_type,
                        DataType::Record { .. } | DataType::Collection { .. }
                    ) {
                        return Err("SDT levels may contain only scalar Items".into());
                    }
                    if fields
                        .insert(name, crate::kb::RawCatalogType::Concrete(data_type))
                        .is_some()
                    {
                        return Err("duplicate XML declaration".into());
                    }
                }
                ParsedTypeDefinition::BasedOn(target_name) => {
                    let key = target_name.to_ascii_uppercase();
                    let (target_id, kind) = context.identities.get(&key).ok_or_else(|| {
                        format!("unresolved XML SDT item BasedOn target {target_name}")
                    })?;
                    if *kind != ObjectKind::Domain && *kind != ObjectKind::Attribute {
                        return Err(format!(
                            "XML SDT item BasedOn target {target_name} is neither a Domain nor an Attribute"
                        ));
                    }
                    direct_based_on.push(target_id.clone());
                    if fields
                        .insert(
                            name,
                            crate::kb::RawCatalogType::BasedOnShort {
                                target: target_name,
                            },
                        )
                        .is_some()
                    {
                        return Err("duplicate XML declaration".into());
                    }
                }
            }
            if fields.len() > 1024 {
                return Err("XML exceeds 1024 declarations".into());
            }
            continue;
        }
        if !member.has_tag_name("Level") {
            return Err("unsupported SDT Level declaration".into());
        }
        let name = level_name(member)?;
        if !seen_level_names.insert(name.clone()) {
            return Err("duplicate SDT Level name".into());
        }
        let metadata = level_metadata(member, false)?.ok_or("nested SDT Level has no LevelInfo")?;
        if !metadata.name.eq_ignore_ascii_case(&name) {
            return Err("nested SDT Level and LevelInfo names must match".into());
        }
        reserve_level_guid(&metadata.guid, &context.top_ids, &mut context.seen_guids)?;
        let child_path = format!("{}.{}", path, name);
        let ProjectedLevel {
            fields: child_fields,
            direct_uses: child_uses,
            direct_based_on: child_based_on,
            nested: child_nested,
        } = project_level(member, context, depth + 1, &child_path, seen_level_names)?;
        if !child_fields.values().any(|raw_type| match raw_type {
            crate::kb::RawCatalogType::Concrete(dt) => {
                !matches!(dt, DataType::Record { .. } | DataType::Collection { .. })
            }
            crate::kb::RawCatalogType::BasedOnShort { .. }
            | crate::kb::RawCatalogType::BasedOnTagged { .. } => true,
        }) {
            return Err("nested SDT Level requires scalar Items".into());
        }

        let id = ObjectId::new(format!("level-{}", metadata.guid))?;
        let schema_name = synthetic_schema_name(&metadata.guid)?;
        let id_key = id.as_str().to_ascii_uppercase();
        let schema_key = schema_name.to_ascii_uppercase();
        if context.top_ids.contains(&id)
            || context
                .top_ids
                .iter()
                .any(|top_id| top_id.as_str().eq_ignore_ascii_case(&schema_name))
            || context.top_names.contains(&id_key)
            || context.top_names.contains(&schema_key)
            || !context.synthetic_ids.insert(id.clone())
            || !context.synthetic_names.insert(schema_key)
        {
            return Err("synthetic SDT identity or schema name collides".into());
        }
        let alias_name = if metadata.collection {
            // Collection item aliases are evidenced through depth 2 only.
            if depth < 2 {
                Some(
                    metadata
                        .collection_item_name
                        .as_deref()
                        .ok_or("collection Level has no item name")?,
                )
            } else {
                None
            }
        } else if depth == 0 {
            // Composite-level aliases are evidenced for direct children of the
            // root only.
            Some(metadata.name.as_str())
        } else {
            None
        };
        if let Some(alias_name) = alias_name {
            let alias = format!("{}.{}", path, alias_name);
            if context
                .nested_aliases
                .insert(alias, (schema_name.clone(), id.clone()))
                .is_some()
            {
                return Err("ambiguous nested SDT type alias".into());
            }
        }
        if fields
            .insert(
                name,
                if metadata.collection {
                    crate::kb::RawCatalogType::Concrete(DataType::Collection {
                        schema: schema_name.clone(),
                    })
                } else {
                    crate::kb::RawCatalogType::Concrete(DataType::Record {
                        schema: schema_name.clone(),
                    })
                },
            )
            .is_some()
        {
            return Err("duplicate SDT field name".into());
        }
        direct_uses.push(id.clone());
        nested.push(NestedSchema {
            id,
            schema_name,
            fields: child_fields,
            uses: child_uses,
            based_on: child_based_on,
        });
        nested.extend(child_nested);
    }
    Ok(ProjectedLevel {
        fields,
        direct_uses,
        direct_based_on,
        nested,
    })
}

fn synthetic_schema_name(guid: &str) -> Result<String, String> {
    let name = format!("GXL_{}", guid.to_ascii_uppercase().replace('-', "_"));
    if !crate::model::is_identifier(&name) {
        return Err("synthetic SDT schema name is invalid".into());
    }
    Ok(name)
}

fn reserve_level_guid(
    guid: &str,
    top_ids: &BTreeSet<ObjectId>,
    seen_guids: &mut BTreeSet<String>,
) -> Result<(), String> {
    let id = ObjectId::new(guid.to_owned())?;
    if top_ids.contains(&id) {
        return Err("SDT LevelInfo GUID collides with a top-level object ID".into());
    }
    if !seen_guids.insert(guid.to_owned()) {
        return Err("duplicate SDT LevelInfo GUID".into());
    }
    Ok(())
}

fn parse_sdt<'a>(
    node: Node<'_, '_>,
    object_name: &str,
    context: &mut SdtProjectionContext<'a>,
) -> Result<SdtProjection, String> {
    let mut parts = BTreeMap::new();
    for part in node.children().filter(|child| child.is_element()) {
        if part.has_tag_name("Properties") {
            continue;
        }
        if !part.has_tag_name("Part") {
            return Err("unsupported XML object child".into());
        }
        let key = part
            .attribute("type")
            .ok_or("XML part has no type")?
            .to_ascii_lowercase();
        if parts.insert(key, part).is_some() {
            return Err("duplicate XML object part".into());
        }
    }
    let structure = parts.remove(STRUCTURE).ok_or("XML SDT has no Structure")?;
    only_children(structure, &["Level", "Properties"])?;
    let root = required(structure, "Level")?;
    let root_name = level_name(root)?;
    if !root_name.eq_ignore_ascii_case(object_name) {
        return Err("root SDT Level name must match its object name".into());
    }
    let root_metadata = level_metadata(root, true)?;
    if root_metadata
        .as_ref()
        .is_some_and(|metadata| !metadata.name.eq_ignore_ascii_case(&root_name))
    {
        return Err("root SDT Level and LevelInfo names must match".into());
    }
    let has_nested_levels = root.children().any(|child| child.has_tag_name("Level"));
    if has_nested_levels && root_metadata.is_none() {
        return Err("nested SDT requires root LevelInfo metadata".into());
    }
    if let Some(metadata) = &root_metadata {
        reserve_level_guid(&metadata.guid, &context.top_ids, &mut context.seen_guids)?;
    }

    let mut level_names = BTreeSet::from([root_name.clone()]);
    let ProjectedLevel {
        fields,
        direct_uses,
        direct_based_on,
        nested,
    } = project_level(root, context, 0, &root_name, &mut level_names)?;
    if fields.is_empty() || fields.len() > 1024 {
        return Err("SDT requires 1..1024 fields".into());
    }
    for part in parts.values() {
        empty_metadata_part(*part)?;
    }
    Ok(SdtProjection {
        fields,
        direct_uses,
        direct_based_on,
        nested,
    })
}

// Metadata-only parts may be omitted from the compilation projection, but authored
// Layout/Conditions/Help/Documentation content is not silently treated as code-free.
fn empty_metadata_part(part: Node<'_, '_>) -> Result<(), String> {
    let known = matches!(
        part.attribute("type")
            .unwrap_or("")
            .to_ascii_lowercase()
            .as_str(),
        "c414ed00-8cc4-4f44-8820-4baf93547173"
            | "763f0d8b-d8ac-4db4-8dd4-de8979f2b5b9"
            | "ad3ca970-19d0-44e1-a7b7-db05556e820c"
            | "babf62c5-0111-49e9-a1c3-cc004d90900a"
    );
    if !known {
        return Err("unsupported XML object part".into());
    }
    if properties(part)?.keys().any(|key| key != "ISDEFAULT") {
        return Err("unsupported XML metadata properties".into());
    }
    for node in part.descendants().filter(|n| n.is_element()) {
        if !matches!(
            node.tag_name().name(),
            "Part"
                | "Properties"
                | "Property"
                | "Name"
                | "Value"
                | "Help"
                | "HelpItem"
                | "Language"
                | "Content"
        ) {
            return Err("authored XML metadata part is not supported".into());
        }
        if node.has_tag_name("Content") && !text(node)?.trim().is_empty() {
            return Err("authored XML Help is not admitted".into());
        }
    }
    Ok(())
}

pub fn catalog_from_xml(input: &str) -> Result<Catalog, String> {
    if input.len() > MAX_XML_BYTES {
        return Err("XML exceeds 8 MiB".into());
    }
    let document = Document::parse_with_options(
        input,
        ParsingOptions {
            allow_dtd: false,
            nodes_limit: 50_000,
            entity_resolver: None,
        },
    )
    .map_err(|e| format!("invalid SDK XML: {e}"))?;
    if document.descendants().any(|n| {
        n.is_element()
            && (n.tag_name().namespace().is_some() || n.ancestors().take(130).count() > 128)
    }) {
        return Err("XML namespaces or depth above 128 are not admitted".into());
    }
    let root = document.root_element();
    if !root.has_tag_name("ExportFile") {
        return Err("expected SDK ExportFile XML".into());
    }
    only_children(
        root,
        &[
            "KMW",
            "Source",
            "Objects",
            "Dependencies",
            "ObjectsIdentityMapping",
        ],
    )?;
    let objects = required(root, "Objects")?;
    let mut definitions = Vec::new();
    let mut identities = BTreeMap::new();
    let mut ids = BTreeSet::new();
    for object in objects.children().filter(|n| n.is_element()) {
        if !object.has_tag_name("Object") {
            return Err("unsupported XML Objects child".into());
        }
        let name = object.attribute("name").ok_or("XML object has no name")?;
        let kind = match object
            .attribute("type")
            .unwrap_or("")
            .to_ascii_lowercase()
            .as_str()
        {
            PROCEDURE => ObjectKind::Procedure,
            SDT => ObjectKind::StructuredDataType,
            DOMAIN => ObjectKind::Domain,
            ATTRIBUTE => ObjectKind::Attribute,
            _ => return Err("unsupported SDK XML object kind".into()),
        };
        let object_properties = properties(object)?;
        if kind == ObjectKind::Procedure {
            if object_properties
                .get("CALL_PROTOCOL")
                .is_some_and(|value| !value.eq_ignore_ascii_case("CLINE"))
            {
                return Err("XML call protocol must be CLINE when specified".into());
            }
            if object_properties.keys().any(|key| {
                !matches!(
                    key.as_str(),
                    "NAME" | "ISMAIN" | "CALL_PROTOCOL" | "ISDEFAULT"
                )
            }) {
                return Err("unsupported XML object properties".into());
            }
        } else if kind == ObjectKind::StructuredDataType {
            if object_properties
                .keys()
                .any(|key| !matches!(key.as_str(), "NAME" | "ISDEFAULT"))
            {
                return Err("unsupported XML object properties".into());
            }
        } else {
            for name in object_properties.keys() {
                if !matches!(
                    name.as_str(),
                    "NAME"
                        | "ATTCUSTOMTYPE"
                        | "DATATYPESTRING"
                        | "LENGTH"
                        | "DECIMALS"
                        | "ATTDECIMALS"
                        | "ATTMAXLEN"
                        | "ATTAVGLEN"
                        | "ATTCOLLECTION"
                        | "DOMAINBASEDON"
                        | "BASEDON"
                        | "IDBASEDON"
                        | "ATTRIBUTEBASEDON"
                        | "TYPE"
                        | "DESCRIPTION"
                        | "CALL_PROTOCOL"
                        | "ISDEFAULT"
                        | "ISMAIN"
                ) {
                    return Err(format!("unsupported XML declaration property {name}"));
                }
            }
        }
        if object
            .attribute("fullyQualifiedName")
            .is_some_and(|q| q != name)
        {
            return Err("qualified/module XML names are not admitted".into());
        }
        let id = ObjectId::new(object.attribute("guid").ok_or("XML object has no GUID")?)?;
        if !ids.insert(id.clone()) {
            return Err("duplicate XML object GUID".into());
        }
        if identities
            .insert(name.to_ascii_uppercase(), (id.clone(), kind))
            .is_some()
        {
            return Err("duplicate XML object name".into());
        }
        definitions.push((object, KbObject::new(id, name, kind)));
        if definitions.len() > 1152 {
            return Err("XML exceeds 1152 objects".into());
        }
    }
    let mut schema_count = identities
        .values()
        .filter(|(_, kind)| *kind == ObjectKind::StructuredDataType)
        .count();
    if schema_count > 128 {
        return Err("XML exceeds 128 SDT schemas".into());
    }
    let mut projection_context =
        SdtProjectionContext::new(ids, identities.keys().cloned().collect(), &identities);
    let mut sdt_projections = BTreeMap::new();
    let mut projected_object_count = definitions.len();
    for (node, object) in &definitions {
        if object.kind != ObjectKind::StructuredDataType {
            continue;
        }
        let projection = parse_sdt(*node, &object.name, &mut projection_context)?;
        schema_count += projection.nested.len();
        projected_object_count += projection.nested.len();
        if schema_count > 128 {
            return Err("XML exceeds 128 SDT schemas including nested Levels".into());
        }
        if projected_object_count > 1152 {
            return Err("XML exceeds 1152 objects including nested Levels".into());
        }
        sdt_projections.insert(object.id.clone(), projection);
    }

    let mut edits = Vec::new();
    for (node, mut object) in definitions {
        let mut parts = BTreeMap::new();
        for part in node.children().filter(|n| n.is_element()) {
            if part.has_tag_name("Properties") {
                continue;
            }
            if !part.has_tag_name("Part") {
                return Err("unsupported XML object child".into());
            }
            let key = part
                .attribute("type")
                .ok_or("XML part has no type")?
                .to_ascii_lowercase();
            if parts.insert(key, part).is_some() {
                return Err("duplicate XML object part".into());
            }
        }
        if object.kind == ObjectKind::StructuredDataType {
            parts.remove(STRUCTURE).ok_or("XML SDT has no Structure")?;
            let projection = sdt_projections
                .remove(&object.id)
                .ok_or("missing parsed XML SDT projection")?;
            for target in &projection.direct_uses {
                object.references.push(Reference {
                    kind: ReferenceKind::Uses,
                    target: target.clone(),
                });
            }
            for target in &projection.direct_based_on {
                object.references.push(Reference {
                    kind: ReferenceKind::BasedOn,
                    target: target.clone(),
                });
            }
            object.references.sort();
            object.references.dedup();
            object.source_parts.push(SourcePart {
                name: "Fields".into(),
                source: serde_json::to_string(&projection.fields).map_err(|e| e.to_string())?,
            });
            edits.push(Edit::Upsert(object));
            for nested in projection.nested {
                let mut nested_object = KbObject::new(
                    nested.id,
                    nested.schema_name,
                    ObjectKind::StructuredDataType,
                );
                for target in &nested.uses {
                    nested_object.references.push(Reference {
                        kind: ReferenceKind::Uses,
                        target: target.clone(),
                    });
                }
                for target in &nested.based_on {
                    nested_object.references.push(Reference {
                        kind: ReferenceKind::BasedOn,
                        target: target.clone(),
                    });
                }
                nested_object.references.sort();
                nested_object.references.dedup();
                nested_object.source_parts.push(SourcePart {
                    name: "Fields".into(),
                    source: serde_json::to_string(&nested.fields).map_err(|e| e.to_string())?,
                });
                edits.push(Edit::Upsert(nested_object));
            }
            continue;
        } else if object.kind == ObjectKind::Domain || object.kind == ObjectKind::Attribute {
            let mut object_props = properties(node)?;
            if !object_props.contains_key("ATTCUSTOMTYPE")
                && !object_props.contains_key("TYPE")
                && !object_props.contains_key("DATATYPESTRING")
                && !object_props.contains_key("DOMAINBASEDON")
                && !object_props.contains_key("BASEDON")
                && !object_props.contains_key("IDBASEDON")
                && !object_props.contains_key("ATTRIBUTEBASEDON")
            {
                for part in parts.values() {
                    let part_props = properties(*part)?;
                    if part_props.contains_key("ATTCUSTOMTYPE")
                        || part_props.contains_key("TYPE")
                        || part_props.contains_key("DATATYPESTRING")
                        || part_props.contains_key("DOMAINBASEDON")
                        || part_props.contains_key("BASEDON")
                        || part_props.contains_key("IDBASEDON")
                        || part_props.contains_key("ATTRIBUTEBASEDON")
                    {
                        object_props.extend(part_props);
                        break;
                    }
                }
            }
            let parsed_type = parse_type_definition(&object_props)?;
            let raw_type = match parsed_type {
                ParsedTypeDefinition::Concrete(data_type) => {
                    if matches!(
                        data_type,
                        DataType::Record { .. } | DataType::Collection { .. }
                    ) {
                        return Err(format!(
                            "XML {:?} cannot have record or collection data type",
                            object.kind
                        ));
                    }
                    crate::kb::RawCatalogType::Concrete(data_type)
                }
                ParsedTypeDefinition::BasedOn(target_name) => {
                    let key = target_name.to_ascii_uppercase();
                    let (target_id, target_kind) = identities.get(&key).ok_or_else(|| {
                        format!(
                            "unresolved XML {:?} BasedOn target {target_name}",
                            object.kind
                        )
                    })?;
                    if object.kind == ObjectKind::Domain && *target_kind != ObjectKind::Domain {
                        return Err(format!(
                            "XML domain {} cannot be based on non-domain {target_name}",
                            object.name
                        ));
                    }
                    if *target_kind != ObjectKind::Domain && *target_kind != ObjectKind::Attribute {
                        return Err(format!(
                            "XML {:?} BasedOn target {target_name} is neither a Domain nor an Attribute",
                            object.kind
                        ));
                    }
                    object.references.push(Reference {
                        kind: ReferenceKind::BasedOn,
                        target: target_id.clone(),
                    });
                    crate::kb::RawCatalogType::BasedOnShort {
                        target: target_name,
                    }
                }
            };
            for part in parts.values() {
                empty_metadata_part(*part)?;
            }
            object.references.sort();
            object.references.dedup();
            object.source_parts.push(SourcePart {
                name: "TYPE".into(),
                source: serde_json::to_string(&raw_type).map_err(|e| e.to_string())?,
            });
            edits.push(Edit::Upsert(object));
            continue;
        } else {
            let part = parts.remove(SOURCE).ok_or("XML Procedure has no Source")?;
            only_children(part, &["Source", "Properties"])?;
            let source = text(required(part, "Source")?)?;
            let body = crate::frontend::parse_body(&source)?;
            for callee in crate::dependencies::calls(&body) {
                if crate::frontend::is_typed_builtin(&callee) {
                    continue;
                }
                let (target, kind) = identities
                    .get(&callee)
                    .ok_or_else(|| format!("unresolved XML call {callee}"))?;
                if *kind != ObjectKind::Procedure {
                    return Err("XML call target is not a Procedure".into());
                }
                object.references.push(Reference {
                    kind: ReferenceKind::Calls,
                    target: target.clone(),
                });
            }
            object.source_parts.push(SourcePart {
                name: "Source".into(),
                source,
            });
            if let Some(part) = parts.remove(RULES) {
                only_children(part, &["Source", "Properties"])?;
                if let Some(source) = child(part, "Source")? {
                    let rules = text(source)?;
                    if !rules.trim().is_empty() {
                        crate::rules::parameters(&rules)?;
                        object.source_parts.push(SourcePart {
                            name: "Rules".into(),
                            source: rules,
                        });
                    }
                }
            }
            let raw_variables = match parts.remove(VARIABLES) {
                Some(part) => declarations(part, "Variable", "Name")?,
                None => BTreeMap::new(),
            };
            let mut variables: BTreeMap<String, crate::kb::RawCatalogType> = BTreeMap::new();
            for (name, parsed_type) in raw_variables {
                match parsed_type {
                    ParsedTypeDefinition::Concrete(data_type) => {
                        if let DataType::Record { ref schema } = data_type {
                            let key = schema.to_ascii_uppercase();
                            let target = if let Some((nested_schema, target)) =
                                projection_context.nested_aliases.get(&key)
                            {
                                variables.insert(
                                    name,
                                    crate::kb::RawCatalogType::Concrete(DataType::Record {
                                        schema: nested_schema.clone(),
                                    }),
                                );
                                target.clone()
                            } else {
                                let (target, kind) = identities
                                    .get(&key)
                                    .ok_or("unresolved XML SDT declaration")?;
                                if *kind != ObjectKind::StructuredDataType {
                                    return Err("XML SDT declaration targets a non-SDT".into());
                                }
                                variables
                                    .insert(name, crate::kb::RawCatalogType::Concrete(data_type));
                                target.clone()
                            };
                            object.references.push(Reference {
                                kind: ReferenceKind::Uses,
                                target,
                            });
                        } else {
                            variables.insert(name, crate::kb::RawCatalogType::Concrete(data_type));
                        }
                    }
                    ParsedTypeDefinition::BasedOn(target_name) => {
                        let key = target_name.to_ascii_uppercase();
                        let (target_id, kind) = identities.get(&key).ok_or_else(|| {
                            format!("unresolved XML variable BasedOn target {target_name}")
                        })?;
                        if *kind != ObjectKind::Domain && *kind != ObjectKind::Attribute {
                            return Err(format!(
                                "XML variable BasedOn target {target_name} is neither a Domain nor an Attribute"
                            ));
                        }
                        object.references.push(Reference {
                            kind: ReferenceKind::BasedOn,
                            target: target_id.clone(),
                        });
                        variables.insert(
                            name,
                            crate::kb::RawCatalogType::BasedOnShort {
                                target: target_name,
                            },
                        );
                    }
                }
            }
            object.references.sort();
            object.references.dedup();
            object.source_parts.push(SourcePart {
                name: "Variables".into(),
                source: serde_json::to_string(&variables).map_err(|e| e.to_string())?,
            });
        }
        for part in parts.values() {
            empty_metadata_part(*part)?;
        }
        edits.push(Edit::Upsert(object));
    }
    let mut catalog = Catalog::empty();
    catalog.apply(0, &edits)?;
    Ok(catalog)
}

pub fn project_from_xml(
    input: &str,
    entry: &str,
    year_max: Option<u16>,
) -> Result<Project, String> {
    let catalog = catalog_from_xml(input)?;
    let object = catalog
        .objects()
        .find(|o| o.name.eq_ignore_ascii_case(entry))
        .ok_or("XML entry was not found")?;
    crate::kb::project_from_catalog(&catalog, &object.id, year_max)
}
