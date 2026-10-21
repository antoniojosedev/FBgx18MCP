//! Decodes the stored `VARIABLES` part of a native Knowledge Base Procedure into the portable
//! `BTreeMap<String, DataType>` that the emitters read.
//!
//! The stored form is GeneXus XML: a `<Variables>` root holding `<StandardVariable>` and
//! `<Variable>` declarations. Each declaration has a `<Properties>` list of name/value pairs,
//! and its type is the escaped `ATTCUSTOMTYPE` fragment (`<AttCustomType><DataType>N</DataType>`).
//! This is not the exchange (XPZ) form, so the exchange parser is not reused and its allowlists
//! are not loosened.
//!
//! Every mapped type code was confirmed against the SDK's own declaration of the same variables
//! on the disposable KBs (see `docs/udm_storage_notes.md`). A declaration this module does not map
//! is refused by name: nothing is guessed, and nothing is dropped.

use std::collections::{BTreeMap, BTreeSet};

use roxmltree::{Document, Node, ParsingOptions};

use crate::model::DataType;

mod attributes;
mod properties;
mod type_codes;

use properties::properties;
use type_codes::{decode_type_in, number};

pub(crate) use attributes::{AttributeRow, attribute_row, resolve_attribute_type};
pub(crate) use properties::{domain_data_type, named_property, property_map};
pub(crate) use type_codes::{attcustomtype, decode_type, structure_ref};

/// The most declarations one object may carry, as the exchange parser bounds them.
pub(crate) const MAX_DECLARATIONS: usize = 1024;

/// The largest VARIABLES part this module reads; the reader's own part cap is smaller.
const MAX_XML_BYTES: usize = 8 * 1024 * 1024;

/// The type GUID of an SDT in a `StructureTypeReference`. Its `Id` is the SDT's entity id. The
/// value is confirmed by name: every SDT reference on KBTeste and KBTeste16 resolves to an SDT of
/// that name, and the reader checks the declared name against the model.
pub(crate) const SDT_TYPE_GUID: &str = "447527b5-9210-4523-898b-5dccb17be60a";

/// The type GUID of a nested SDT level in a `StructureTypeReference`. Its `Id` is the level's entity
/// id (an `SDTLevelEntity`), not an SDT's. Confirmed by the SDK: the Procedures `RTDX765000A` (`Row`,
/// level 219, `RSN03944700.RowsItem`), `RTM49305800` (`Row`, level 221, `RCE06829400.Entry`) and
/// `RTDA386800E` (`V`, level 225, `NativeDeepSdt85299100.Child.NestedCollectionItem`) declare the
/// matching SDK classes.
pub(crate) const SDT_LEVEL_TYPE_GUID: &str = "a76e9340-bdb9-445d-8f81-cfd4ddd0b0f3";

/// A stored SDT level a Procedure variable is typed by: the entity id of the SDT that holds it, its
/// class schema and dotted name (see `stored_sdt::StoredLevel`), and whether it is one item of a
/// collection. Only a collection item is mapped.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct NestedLevel {
    pub sdt_id: i32,
    pub schema: String,
    pub dotted: String,
    pub collection_item: bool,
}

/// The `idBasedOn` value GeneXus writes when a variable is not based on anything.
pub(crate) const NO_BASED_ON: &str = "(none)";

/// The only object type of a variable declaration.
const VARIABLE_OBJ_TYPE: &str = "id_OTYPE_VAR";

/// An SDT whose root is a collection (`AttCollection True` on the root level). A variable of that SDT is
/// the SDK's `GXBaseCollection<Sdt<name>_<item>>`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct CollectionRoot {
    /// The SDT's model name.
    pub(crate) name: String,
    /// The collection item name: the root's stored `idCollectionItemName`, or `<name>Item` when none is stored.
    pub(crate) item: String,
    /// The XML namespace the SDK passes to `GXBaseCollection`: the SDT's stored `ExternalNamespace`, else the
    /// Knowledge Base's name. `None` when neither can be read, and a variable of this SDT is then refused.
    pub(crate) namespace: Option<String>,
}

/// The decoded declarations of one Procedure.
#[derive(Debug, Default, PartialEq)]
pub(crate) struct Declarations {
    /// Variable name to its type, in the portable form the emitters read.
    pub(crate) variables: BTreeMap<String, DataType>,
    /// Entity ids of the SDTs the declarations use; each one is an explicit `Uses` reference.
    pub(crate) sdt_entity_ids: BTreeSet<i32>,
    /// Entity ids of the Domains the declarations are based on; each one is a `BasedOn` reference.
    pub(crate) domain_entity_ids: BTreeSet<i32>,
}

/// Decodes a stored VARIABLES part. `sdt_name` returns the model name of the SDT with the given
/// entity id, or `None` when no SDT of this Knowledge Base has that id. `domain_type` returns the
/// stored portable type of the Domain with the given entity id, or `None` when that Domain has no
/// type this engine evidences; a variable based on such a Domain is refused.
///
/// `level_of` returns the stored SDT level with the given entity id, or `None` when no SDT of this
/// Knowledge Base holds such a level; a level that cannot be read is an error.
///
/// The error names the variable it refused, so the caller can report the object and the variable.
///
/// Test-only: the reader calls `decode_with_collections`, which also reads the collection roots. This wrapper
/// keeps the unit tests' calls to the record-only form.
#[cfg(test)]
pub(crate) fn decode(
    xml: &str,
    sdt_name: &dyn Fn(i32) -> Option<String>,
    domain_type: &dyn Fn(i32) -> Option<DataType>,
    level_of: &dyn Fn(i32) -> Result<Option<NestedLevel>, String>,
) -> Result<Declarations, String> {
    decode_with_collections(xml, sdt_name, &|_| Ok(None), domain_type, level_of)
}

/// `decode`, reading also the SDTs whose root is a collection (`collection_of`). A variable typed by one of
/// those is `DataType::BaseCollection`; `sdt_name` still answers only for a record SDT.
pub(crate) fn decode_with_collections(
    xml: &str,
    sdt_name: &dyn Fn(i32) -> Option<String>,
    collection_of: &dyn Fn(i32) -> Result<Option<CollectionRoot>, String>,
    domain_type: &dyn Fn(i32) -> Option<DataType>,
    level_of: &dyn Fn(i32) -> Result<Option<NestedLevel>, String>,
) -> Result<Declarations, String> {
    if xml.len() > MAX_XML_BYTES {
        return Err(format!(
            "VARIABLES part is {} bytes, above the {MAX_XML_BYTES}-byte limit",
            xml.len()
        ));
    }
    let document = parse(xml)?;
    let root = document.root_element();
    if root.tag_name().name() != "Variables" {
        return Err(format!(
            "VARIABLES root is <{}>, expected <Variables>",
            root.tag_name().name()
        ));
    }
    if root
        .children()
        .any(|n| n.is_text() && !n.text().unwrap_or("").trim().is_empty())
    {
        return Err("VARIABLES root holds text outside its declarations".into());
    }

    let mut out = Declarations::default();
    let mut count = 0usize;
    for node in root.children().filter(|n| n.is_element()) {
        count += 1;
        if count > MAX_DECLARATIONS {
            return Err(format!("VARIABLES exceeds {MAX_DECLARATIONS} declarations"));
        }
        let tag = node.tag_name().name();
        if tag != "StandardVariable" && tag != "Variable" {
            return Err(format!("unsupported declaration <{tag}>"));
        }
        let name = node
            .attribute("Name")
            .map(str::trim)
            .filter(|n| !n.is_empty())
            .ok_or("a declaration has no Name")?
            .to_string();
        let (data_type, sdt_id, domain_id) =
            decode_declaration(node, sdt_name, collection_of, domain_type, level_of)
                .map_err(|e| format!("variable '{name}': {e}"))?;
        if out.variables.insert(name.clone(), data_type).is_some() {
            return Err(format!("variable '{name}' is declared twice"));
        }
        if let Some(id) = sdt_id {
            out.sdt_entity_ids.insert(id);
        }
        if let Some(id) = domain_id {
            out.domain_entity_ids.insert(id);
        }
    }
    Ok(out)
}

/// The `Id` of each declaration of a stored VARIABLES part, by its name. The SDK names a variable's
/// field `AV<Id><Name>` (`AV8Hits`, dch37961700.cs), so the emitters need the stored id. A
/// declaration without a numeric `Id` is refused: the field name would be guessed.
pub(crate) fn declaration_ids(xml: &str) -> Result<BTreeMap<String, u32>, String> {
    let document = parse(xml)?;
    let mut ids = BTreeMap::new();
    for node in document
        .root_element()
        .children()
        .filter(|n| n.is_element())
    {
        let name = node
            .attribute("Name")
            .map(str::trim)
            .filter(|n| !n.is_empty())
            .ok_or("a declaration has no Name")?;
        let id = node
            .attribute("Id")
            .and_then(|id| id.trim().parse::<u32>().ok())
            .ok_or_else(|| format!("variable '{name}' has no numeric Id"))?;
        ids.insert(name.to_string(), id);
    }
    Ok(ids)
}

pub(super) fn parse(xml: &str) -> Result<Document<'_>, String> {
    Document::parse_with_options(
        xml,
        ParsingOptions {
            allow_dtd: false,
            ..ParsingOptions::default()
        },
    )
    .map_err(|e| format!("XML is not well formed: {e}"))
}

/// The type of one declaration, the SDT entity id it uses when it is an SDT, and the Domain entity
/// id it is based on when it takes its type from a Domain.
fn decode_declaration(
    node: Node<'_, '_>,
    sdt_name: &dyn Fn(i32) -> Option<String>,
    collection_of: &dyn Fn(i32) -> Result<Option<CollectionRoot>, String>,
    domain_type: &dyn Fn(i32) -> Option<DataType>,
    level_of: &dyn Fn(i32) -> Result<Option<NestedLevel>, String>,
) -> Result<(DataType, Option<i32>, Option<i32>), String> {
    // `Documentation` is the stored WikiPage container of the declaration. It carries the author's
    // description (Modified, Revision, StorableContent), never a type, so it is not read. Any other
    // child is not part of the stored form this decoder knows and refuses the declaration.
    for child in node.children().filter(|n| n.is_element()) {
        match child.tag_name().name() {
            "Properties" | "Documentation" => {}
            other => return Err(format!("unsupported child <{other}>")),
        }
    }
    let props = properties(node)?;

    if let Some(obj_type) = props.get("OBJ_TYPE") {
        if obj_type != VARIABLE_OBJ_TYPE {
            return Err(format!("object type {obj_type} is not a variable"));
        }
    }
    match props.get("AttCollection").map(String::as_str) {
        None | Some("False") => {}
        Some("True") => {
            return Err("collections are not mapped: no captured SDK C# form for a collection variable (docs/udm_storage_notes.md)".into());
        }
        Some(other) => return Err(format!("invalid AttCollection value '{other}'")),
    }
    let based_on = props
        .get("idBasedOn")
        .map(String::as_str)
        .filter(|value| *value != NO_BASED_ON);

    let length = number(&props, "Length")?;
    if let (Some(max), Some(len)) = (number(&props, "AttMaxLen")?, length) {
        if max != len {
            return Err(format!("AttMaxLen {max} differs from Length {len}"));
        }
    }

    let Some(custom) = props
        .get("ATTCUSTOMTYPE")
        .map(String::as_str)
        .filter(|value| !value.trim().is_empty())
    else {
        return match based_on {
            // A based-on variable with no declared type takes the stored type of its Domain, as an
            // SDT item does. It declares no length of its own: the stored rows of both KBs carry none
            // (`ProcVarDomain.EmpresaID` and `IsAuthorized.GxObject` store only Name and idBasedOn),
            // and the SDK gives the domain's length. A variable that declares one is refused, since
            // which length wins is not evidenced.
            Some(target) => {
                if props.contains_key("Length")
                    || props.contains_key("AttMaxLen")
                    || props.contains_key("Decimals")
                {
                    return Err(format!(
                        "based on '{target}' with its own length or decimals is not mapped"
                    ));
                }
                let domain_id = crate::udm::stored_sdt::domain_id_of(target)?;
                let data_type = domain_type(domain_id).ok_or_else(|| {
                    format!("based on '{target}': the domain has no stored type this engine maps")
                })?;
                Ok((data_type, None, Some(domain_id)))
            }
            None => Err("the declaration has no type".into()),
        };
    };
    if let Some(target) = based_on {
        return Err(format!(
            "based on '{target}' together with a declared type is not mapped"
        ));
    }

    let (code, structure_reference) = attcustomtype(custom)?;
    let props = crate::udm::type_evidence::variable_props(&code, &props);
    if code == "254" {
        if let Some(reference) = structure_reference.as_deref() {
            let (type_guid, id) = structure_ref(reference)?;
            if type_guid.eq_ignore_ascii_case(SDT_LEVEL_TYPE_GUID) {
                return nested_level_declaration(id, &props, level_of);
            }
        }
    }
    let data_type = decode_type_in(
        &code,
        structure_reference.as_deref(),
        &props,
        sdt_name,
        collection_of,
    )?;
    let sdt_id = match &data_type {
        DataType::Record { .. } | DataType::BaseCollection { .. } => {
            let reference =
                structure_reference.ok_or("SDT declaration has no StructureTypeReference")?;
            Some(structure_ref(&reference)?.1)
        }
        _ => None,
    };
    Ok((data_type, sdt_id, None))
}

/// A declaration typed by a nested SDT level: its `Id` is the level's entity id. Only a level that is
/// one item of a collection is mapped; its class and dotted name come from the stored structure
/// (`level_of`). A declared `DataTypeString` must equal that dotted name. The level's SDT is returned
/// as the SDT this declaration uses, so the reader records a `Uses` reference to it.
fn nested_level_declaration(
    level_id: i32,
    props: &BTreeMap<String, String>,
    level_of: &dyn Fn(i32) -> Result<Option<NestedLevel>, String>,
) -> Result<(DataType, Option<i32>, Option<i32>), String> {
    if props.contains_key("Length") || props.contains_key("Decimals") {
        return Err("SDT declares a length".into());
    }
    let level = level_of(level_id)?.ok_or_else(|| {
        format!(
            "structure type {SDT_LEVEL_TYPE_GUID} id {level_id} is not a level of an SDT this engine maps"
        )
    })?;
    if !level.collection_item {
        return Err(format!(
            "nested level '{}' is not one item of a collection; only a collection item is mapped",
            level.dotted
        ));
    }
    if let Some(declared) = props.get("DataTypeString") {
        if !declared.eq_ignore_ascii_case(&level.dotted) {
            return Err(format!(
                "declared '{declared}' does not match level '{}'",
                level.dotted
            ));
        }
    }
    Ok((
        DataType::Record {
            schema: level.schema,
        },
        Some(level.sdt_id),
        None,
    ))
}

#[cfg(test)]
mod tests;
