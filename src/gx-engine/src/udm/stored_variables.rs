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

/// Largest NUMERIC precision the stored declarations use (NUMERIC(18,x) is the evidence maximum).
const MAX_NUMERIC_PRECISION: u32 = 18;

/// The properties a declaration may carry. Any other property is refused.
const ALLOWED_PROPERTIES: &[&str] = &[
    "Name",
    "OBJ_TYPE",
    "IsStandardVariable",
    "idIsAutoDefinedVariable",
    "ATTCUSTOMTYPE",
    "Length",
    "Decimals",
    "AttMaxLen",
    "AttAvgLen",
    "AttCollection",
    "idBasedOn",
    "DataTypeString",
];

/// The properties an SDT level or item row may carry, as the evidence KBs store them (the KBTeste
/// model export: 361 items and 111 levels). Each one is a real stored property; none of them changes
/// the portable type of a node. Anything else refuses the row.
pub(crate) const SDT_NODE_PROPERTIES: &[&str] = &[
    "Name",
    "OBJ_TYPE",
    "ATTCUSTOMTYPE",
    "Length",
    "Decimals",
    "AttMaxLen",
    "AttAvgLen",
    "AttCollection",
    "idBasedOn",
    "idCollectionItemName",
    "idJsonName",
    "idJsonInclude",
    "Description",
    "ControlType",
    "EmptyAsNull",
    "Signed",
    "AUTONUMBER",
    "AUTONUMBER_START",
    "AUTONUMBER_STEP",
    "AUTONUMBER_FORREPLICATION",
    "ATT_INITIAL_VALUE",
    "idDATEFORMAT",
];

/// The properties a stored Domain row may carry. A Domain row is not an SDT node: it also holds
/// its folder, its interface flag and its enumeration values. The names are the ones the KBTeste and
/// KBTeste16 Domain rows store (read in full with `CONVERT(VARCHAR(MAX), EntityVersionProperties, 2)`:
/// `Url`, `Email`, `Geolocation`, `Phone`, `ObjectName`, `Code`, `IDManual`, `Data`, `TargetType`,
/// `PlaybackState`, `StoreProductType`, `BeaconProximity`, `MessageTypes`, `RecentLinksOptions`). Any
/// other name still refuses the row, so an unseen property is never ignored silently.
pub(crate) const DOMAIN_ROW_PROPERTIES: &[&str] = &[
    "Name",
    "OBJ_TYPE",
    "ATTCUSTOMTYPE",
    "Length",
    "Decimals",
    "AttMaxLen",
    "AttRegEx",
    "AutoCorrection",
    "AutoCapitalization",
    "AutoResize",
    "GxHeight",
    "IDEnumDefinedValues",
    "ObjectIsInterface",
    "IsDefault",
    "FolderType",
    "FolderId",
];

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

fn parse(xml: &str) -> Result<Document<'_>, String> {
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

/// The portable type of one stored type code, with its stored length, decimals and (for an SDT
/// reference) the `StructureTypeReference`. This is the one code map both the VARIABLES decoder and
/// the SDT structure decoder read, so the two cannot disagree. A code this map does not hold is
/// refused by name.
pub(crate) fn decode_type(
    code: &str,
    structure_reference: Option<&str>,
    props: &BTreeMap<String, String>,
    sdt_name: &dyn Fn(i32) -> Option<String>,
) -> Result<DataType, String> {
    decode_type_in(code, structure_reference, props, sdt_name, &|_| Ok(None))
}

/// `decode_type`, reading also the SDTs whose root is a collection (`collection_of`).
pub(crate) fn decode_type_in(
    code: &str,
    structure_reference: Option<&str>,
    props: &BTreeMap<String, String>,
    sdt_name: &dyn Fn(i32) -> Option<String>,
    collection_of: &dyn Fn(i32) -> Result<Option<CollectionRoot>, String>,
) -> Result<DataType, String> {
    let length = number(props, "Length")?;
    let decimals = number(props, "Decimals")?;
    match code {
        "4" => {
            let precision = required_length(length, "NUMERIC")?;
            if precision > MAX_NUMERIC_PRECISION {
                return Err(format!(
                    "NUMERIC precision {precision} is above {MAX_NUMERIC_PRECISION}"
                ));
            }
            let scale = decimals.unwrap_or(0);
            if scale > precision {
                return Err(format!(
                    "NUMERIC scale {scale} is above precision {precision}"
                ));
            }
            Ok(DataType::Numeric {
                precision: precision as u8,
                scale: scale as u8,
            })
        }
        "5" => {
            no_decimals(decimals, "CHARACTER")?;
            let len = required_length(length, "CHARACTER")?;
            Ok(DataType::Character { length: len })
        }
        "6" => {
            no_decimals(decimals, "DATE")?;
            fixed_length(length, 8, "DATE")?;
            Ok(DataType::Date {})
        }
        "12" => {
            // DATETIME carries a stored Decimals on real rows (`DATETIME(8,5)` in the SDK). The
            // portable type is `DateTime` whatever the decimals, which the C# type does not depend on.
            fixed_length(length, 8, "DATETIME")?;
            Ok(DataType::DateTime {})
        }
        "13" => {
            // VARCHAR carries a stored Decimals on real rows (`VARCHAR(2048,250)`, `VARCHAR(100,2)`).
            // The portable type is `Varchar { length }` and the C# type is `string` whatever the
            // decimals, so the decimals are not part of the portable type.
            let len = required_length(length, "VARCHAR")?;
            Ok(DataType::Varchar { length: len })
        }
        "7" => {
            // BITMAP: the SDK types it `string` (Nemo type mapping, code 7 -> `string`, with an `_gxi`
            // companion). The stored width (`BITMAP(1000)`, `BITMAP(4)`) does not change the C# type. A
            // stored Decimals has no evidence, so it is refused.
            no_decimals(decimals, "BITMAP")?;
            Ok(DataType::Image {})
        }
        "8" => {
            // LONGVARCHAR: the SDK types it `string` (Nemo type mapping, code 8 -> `string`), as VARCHAR.
            // It has no length: the stored rows carry only the type code, and a stored width
            // (`LONGVARCHAR(9216,3072)` on another row) does not change the type, so it is not read.
            Ok(DataType::LongVarchar {})
        }
        "45" => {
            // GUID: `Guid` in the SDK (Nemo type mapping, code 45 -> `guid`; the SDK's `Guid.Empty`
            // default in the C# templates). The stored width (`GUID(4)`) does not change the type.
            no_decimals(decimals, "GUID")?;
            Ok(DataType::Guid {})
        }
        "102" | "103" | "104" | "105" => {
            // GEOGRAPHY, GEOPOINT, GEOLINE and GEOPOLYGON: all `Geospatial` in the SDK (Nemo type
            // mapping, codes 102 to 105 -> `geospatial`). The stored width (`GEOPOINT(50)`) does not change
            // the type. Only 102 and 103 have a declaration observed on a KB; 104 and 105 rest on the mapping.
            no_decimals(decimals, "GEO")?;
            Ok(DataType::Geospatial {})
        }
        "15" => {
            // A stored width of 0 is the absence of a width: the stored `Flag` of RSC79223900 and
            // the SDK's plain `Boolean` agree. A non-zero width would be a real declaration this
            // engine does not map, and is refused.
            if length.is_some_and(|l| l != 0) || decimals.is_some_and(|d| d != 0) {
                return Err("BOOLEAN declares a length".into());
            }
            Ok(DataType::Boolean {})
        }
        "254" => {
            if length.is_some() || decimals.is_some() {
                return Err("SDT declares a length".into());
            }
            let reference =
                structure_reference.ok_or("SDT declaration has no StructureTypeReference")?;
            let (type_guid, id) = structure_ref(reference)?;
            if !type_guid.eq_ignore_ascii_case(SDT_TYPE_GUID) {
                return Err(format!(
                    "structure type {type_guid} is not an SDT type this engine reads (nested SDT levels are not mapped)"
                ));
            }
            if let Some(schema) = sdt_name(id) {
                check_declared_sdt(props, &schema, id)?;
                return Ok(DataType::Record { schema });
            }
            let root = collection_of(id)?.ok_or_else(|| {
                format!("StructureTypeReference {id} is not an SDT this engine maps: it is missing, or it is a collection SDT this engine does not read")
            })?;
            check_declared_sdt(props, &root.name, id)?;
            let namespace = root.namespace.ok_or_else(|| {
                format!(
                    "collection SDT {id} '{}': its XML namespace could not be read",
                    root.name
                )
            })?;
            Ok(DataType::BaseCollection {
                schema: format!("{}_{}", root.name, root.item),
                item: root.item,
                namespace,
            })
        }
        other => Err(crate::udm::type_evidence::unmapped_code_message(other)),
    }
}

/// A declared `DataTypeString` must name the SDT the reference resolves to; a dotted name is a nested level.
fn check_declared_sdt(
    props: &BTreeMap<String, String>,
    schema: &str,
    id: i32,
) -> Result<(), String> {
    if let Some(declared) = props.get("DataTypeString") {
        if declared.contains('.') {
            return Err(format!("nested SDT level '{declared}' is not mapped"));
        }
        if !declared.eq_ignore_ascii_case(schema) {
            return Err(format!(
                "declared SDT '{declared}' does not match SDT {id} '{schema}'"
            ));
        }
    }
    Ok(())
}

/// The name/value properties of a stored row (an SDT level or item). The row's text is a
/// `<Properties>` block of `<Property>` pairs, the same form a declaration carries. The rules are
/// the declaration rules: allowed names only, no duplicate, a `Name` on every property.
pub(crate) fn property_map(raw: &str) -> Result<BTreeMap<String, String>, String> {
    let document = parse(raw)?;
    let root = document.root_element();
    if root.tag_name().name() != "Properties" {
        return Err(format!(
            "stored properties root is <{}>, expected <Properties>",
            root.tag_name().name()
        ));
    }
    let mut out = BTreeMap::new();
    for (key, value) in properties_of_block(root, SDT_NODE_PROPERTIES)? {
        if out.insert(key.clone(), value).is_some() {
            return Err(format!("property {key} is repeated"));
        }
    }
    Ok(out)
}

/// The portable type of a stored Domain row, or `None` when the row has no type this engine can
/// evidence. Only a typed domain (`ATTCUSTOMTYPE`) has one, decoded with the declaration code map,
/// so a variable-length one with no stored `Length` is refused by `decode_type` itself. An
/// enumeration (no `ATTCUSTOMTYPE`) has none: the SDK's length for an enumeration is not a stored
/// value (`PlaybackState` and `StoreProductType` store no length, yet the SDK gives them
/// `NUMERIC(4)`).
pub(crate) fn domain_data_type(raw: &str) -> Option<DataType> {
    let props = domain_property_map(raw).ok()?;
    let code = props.get("ATTCUSTOMTYPE")?;
    let (code, reference) = attcustomtype(code).ok()?;
    decode_type(&code, reference.as_deref(), &props, &|_| None).ok()
}

/// The stored properties an Attribute object's row may carry. These are the names the evidenced rows
/// carry (KBTeste, GX18: `LiveMoveAttributeId` in versions 1 and 2, `LiveMoveAttributeCode`,
/// `LiveMoveAttributeDescription`): `Class` appears only on a primary-key version. A name outside the list
/// refuses the row.
pub(crate) const ATTRIBUTE_ROW_PROPERTIES: &[&str] = &[
    "Name",
    "OBJ_TYPE",
    "ATTCUSTOMTYPE",
    "Length",
    "Decimals",
    "AttMaxLen",
    "AttAvgLen",
    "IsDefault",
    "Class",
    "SuperTypeKey",
];

/// The portable type of a stored Attribute row. Unlike a Domain row, an Attribute row must give a type: a
/// Transaction column takes its type from its attribute, and a caller refuses an attribute whose type is
/// not stored, naming it. A Character or Varchar with no stored length takes the length the SDK gives
/// such a row (`stored_sdt::evidenced_length`).
#[cfg(test)]
pub(crate) fn attribute_data_type(raw: &str) -> Result<DataType, String> {
    attribute_row(raw)?
        .data_type
        .ok_or_else(|| NO_STORED_TYPE.to_string())
}

/// The reason a stored Attribute row with neither its own type nor a `SuperTypeKey` has no type.
const NO_STORED_TYPE: &str = "the row stores no ATTCUSTOMTYPE, so its type is not stored";

/// A stored Attribute row: its own type, if the row stores one; the attribute id its `SuperTypeKey` names, if it
/// names one; and whether the row is a primary key (`Class=PRIMARY`).
pub(crate) struct AttributeRow {
    pub data_type: Option<DataType>,
    pub super_type_key: Option<i32>,
    pub is_primary_key: bool,
}

/// Reads a stored Attribute row. A `SuperTypeKey` is stored only on a primary-key version (`Class=PRIMARY`),
/// and its value is the id of the attribute it references (KBTeste: `Plan070TrnId` v3 names `12`, the key of
/// `Plan071TrnId`). A reference on a row that is not a primary key, or a value that is not a positive integer,
/// refuses the row. A row with neither its own type nor a reference has no type: the caller refuses it.
pub(crate) fn attribute_row(raw: &str) -> Result<AttributeRow, String> {
    let document = parse(raw)?;
    let root = document.root_element();
    if root.tag_name().name() != "Properties" {
        return Err(format!(
            "stored properties root is <{}>, expected <Properties>",
            root.tag_name().name()
        ));
    }
    let mut props = BTreeMap::new();
    for (key, value) in properties_of_block(root, ATTRIBUTE_ROW_PROPERTIES)? {
        if props.insert(key.clone(), value).is_some() {
            return Err(format!("property {key} is repeated"));
        }
    }
    let is_primary_key = props.get("Class").map(String::as_str) == Some("PRIMARY");
    let super_type_key = match props.get("SuperTypeKey") {
        None => None,
        Some(value) => {
            if !is_primary_key {
                return Err("SuperTypeKey is stored on a row that is not a primary key".to_string());
            }
            let id: i32 = value
                .trim()
                .parse()
                .ok()
                .filter(|id| *id > 0)
                .ok_or_else(|| format!("SuperTypeKey {value:?} is not a positive attribute id"))?;
            Some(id)
        }
    };
    let data_type = match props.get("ATTCUSTOMTYPE") {
        None => None,
        Some(stored) => {
            let (code, reference) = attcustomtype(stored)?;
            if reference.is_some() {
                return Err(format!(
                    "type code {code} carries a structure reference, which an attribute type does not map"
                ));
            }
            let sized = crate::udm::stored_sdt::evidenced_length(&code, &props);
            Some(decode_type(&code, None, &sized, &|_| None)?)
        }
    };
    Ok(AttributeRow {
        data_type,
        super_type_key,
        is_primary_key,
    })
}

/// The type of the attribute whose stored row is `row`, which has id `id`. `fetch` reads another attribute's row
/// at the version the model serves it, or says why it cannot.
///
/// A row that stores its own type has that type. A row that stores a `SuperTypeKey` and no type inherits the type
/// of the primary key it names. The chain is followed one link at a time, and only through untyped rows: each
/// link must be a primary key, and the chain must end at a typed primary key. A row that stores both a type and
/// a reference is refused, since which of the two holds is not evidenced. A row that stores neither is refused,
/// never given a default. A missing row, a row that is not a primary key, a link back to an attribute already on
/// the chain, and a chain that ends without a type are all refused, naming the attribute.
pub(crate) fn resolve_attribute_type(
    id: i32,
    row: &AttributeRow,
    fetch: &dyn Fn(i32) -> Result<AttributeRow, String>,
) -> Result<DataType, String> {
    let Some(mut referenced) = row.super_type_key else {
        return row
            .data_type
            .clone()
            .ok_or_else(|| NO_STORED_TYPE.to_string());
    };
    if row.data_type.is_some() {
        return Err(format!(
            "its row stores its own type and a SuperTypeKey naming attribute {referenced}, so its type is not evidenced"
        ));
    }
    let mut visited = BTreeSet::from([id]);
    loop {
        if !visited.insert(referenced) {
            return Err(format!(
                "its SuperTypeKey chain is cyclic: attribute {referenced} is reached twice"
            ));
        }
        let target = fetch(referenced).map_err(|reason| {
            format!(
                "its SuperTypeKey references attribute {referenced}, which cannot be read: {reason}"
            )
        })?;
        if !target.is_primary_key {
            return Err(format!(
                "its SuperTypeKey references attribute {referenced}, which is not a primary key"
            ));
        }
        match (target.data_type, target.super_type_key) {
            (Some(_), Some(_)) => {
                return Err(format!(
                    "its SuperTypeKey references attribute {referenced}, which stores its own type and a SuperTypeKey, so its type is not evidenced"
                ));
            }
            (Some(data_type), None) => return Ok(data_type),
            (None, Some(next)) => referenced = next,
            (None, None) => {
                return Err(format!(
                    "its SuperTypeKey references attribute {referenced}, which stores no type and no SuperTypeKey, so the inherited type is not stored"
                ));
            }
        }
    }
}

/// The value of one named property of a stored entity row (a `<Properties>` block of name/value pairs), or
/// `None` when the row does not carry it. Only that property is read: an entity row carries others this
/// decoder does not evaluate (a Knowledge Base root has `TranslateToLanguageId`, an SDT row `FolderType`), so
/// no allowlist applies. A repeated name is an error.
pub(crate) fn named_property(raw: &str, name: &str) -> Result<Option<String>, String> {
    let document = parse(raw)?;
    let root = document.root_element();
    if root.tag_name().name() != "Properties" {
        return Err(format!(
            "stored properties root is <{}>, expected <Properties>",
            root.tag_name().name()
        ));
    }
    let child_text = |pair: Node<'_, '_>, tag: &str| {
        pair.children()
            .find(|n| n.is_element() && n.tag_name().name() == tag)
            .map(|n| n.text().unwrap_or("").to_string())
            .unwrap_or_default()
    };
    let mut found = None;
    for pair in root
        .children()
        .filter(|n| n.is_element() && n.tag_name().name() == "Property")
    {
        if child_text(pair, "Name") != name {
            continue;
        }
        if found.replace(child_text(pair, "Value")).is_some() {
            return Err(format!("property {name} is repeated"));
        }
    }
    Ok(found)
}

/// The name/value properties of a stored Domain row. The row is read with its own allowlist
/// (`DOMAIN_ROW_PROPERTIES`), never the SDT-node one, so a Domain row with a folder or enumeration
/// property is not refused for carrying it. Duplicates and unknown names are errors.
pub(crate) fn domain_property_map(raw: &str) -> Result<BTreeMap<String, String>, String> {
    let document = parse(raw)?;
    let root = document.root_element();
    if root.tag_name().name() != "Properties" {
        return Err(format!(
            "stored properties root is <{}>, expected <Properties>",
            root.tag_name().name()
        ));
    }
    let mut out = BTreeMap::new();
    for (key, value) in properties_of_block(root, DOMAIN_ROW_PROPERTIES)? {
        if out.insert(key.clone(), value).is_some() {
            return Err(format!("property {key} is repeated"));
        }
    }
    Ok(out)
}

/// The name/value properties of one declaration. Duplicates and unknown names are errors.
fn properties(node: Node<'_, '_>) -> Result<BTreeMap<String, String>, String> {
    let mut out = BTreeMap::new();
    // Only the `Properties` block carries name/value pairs; `Documentation` is read elsewhere.
    for block in node
        .children()
        .filter(|n| n.is_element() && n.tag_name().name() == "Properties")
    {
        for (key, value) in properties_of_block(block, ALLOWED_PROPERTIES)? {
            if out.insert(key.clone(), value).is_some() {
                return Err(format!("property {key} is repeated"));
            }
        }
    }
    Ok(out)
}

/// The pairs of one `<Properties>` block.
fn properties_of_block(
    block: Node<'_, '_>,
    allowed: &[&str],
) -> Result<Vec<(String, String)>, String> {
    let mut out = Vec::new();
    for property in block.children().filter(|n| n.is_element()) {
        if property.tag_name().name() != "Property" {
            return Err(format!(
                "unsupported property element <{}>",
                property.tag_name().name()
            ));
        }
        let mut key: Option<String> = None;
        let mut value: Option<String> = None;
        for field in property.children().filter(|n| n.is_element()) {
            let slot = match field.tag_name().name() {
                "Name" => &mut key,
                "Value" => &mut value,
                other => return Err(format!("unsupported field <{other}>")),
            };
            if slot.is_some() {
                return Err(format!("repeated <{}>", field.tag_name().name()));
            }
            *slot = Some(field.text().unwrap_or("").to_string());
        }
        let key = key.ok_or("a property has no Name")?;
        if !allowed.contains(&key.as_str()) {
            return Err(format!("unsupported property {key}"));
        }
        out.push((key, value.unwrap_or_default()));
    }
    Ok(out)
}

fn number(props: &BTreeMap<String, String>, key: &str) -> Result<Option<u32>, String> {
    match props.get(key) {
        None => Ok(None),
        Some(value) => value
            .trim()
            .parse::<u32>()
            .map(Some)
            .map_err(|_| format!("{key} '{value}' is not a whole number")),
    }
}

fn required_length(length: Option<u32>, type_name: &str) -> Result<u32, String> {
    match length {
        Some(0) => Err(format!("{type_name} length 0 is not a declared length")),
        Some(len) => Ok(len),
        None => Err(format!("{type_name} has no stored length")),
    }
}

fn fixed_length(length: Option<u32>, expected: u32, type_name: &str) -> Result<(), String> {
    match length {
        None => Ok(()),
        Some(len) if len == expected => Ok(()),
        Some(len) => Err(format!(
            "{type_name} declares length {len}, expected {expected}"
        )),
    }
}

/// A type that holds no decimals: a stored `Decimals` of 0 is the same declaration (the stored form
/// writes it on real CHARACTER rows), and any other value is refused.
fn no_decimals(decimals: Option<u32>, type_name: &str) -> Result<(), String> {
    match decimals {
        None | Some(0) => Ok(()),
        Some(_) => Err(format!("{type_name} declares decimals")),
    }
}

/// The type code and the `StructureTypeReference` fragment of an `ATTCUSTOMTYPE` value.
pub(crate) fn attcustomtype(fragment: &str) -> Result<(String, Option<String>), String> {
    let document = parse(fragment)?;
    let root = document.root_element();
    if root.tag_name().name() != "AttCustomType" {
        return Err(format!(
            "type fragment root is <{}>, expected <AttCustomType>",
            root.tag_name().name()
        ));
    }
    let mut code: Option<String> = None;
    let mut reference: Option<String> = None;
    for child in root.children().filter(|n| n.is_element()) {
        match child.tag_name().name() {
            "DataType" => {
                if code.is_some() {
                    return Err("type fragment repeats DataType".into());
                }
                code = Some(child.text().unwrap_or("").trim().to_string());
            }
            "Guid" => {
                if reference.is_some() {
                    return Err("type fragment repeats Guid".into());
                }
                reference = Some(child.text().unwrap_or("").to_string());
            }
            other => return Err(format!("unsupported type element <{other}>")),
        }
    }
    let code = code.ok_or("type fragment has no DataType")?;
    if code.is_empty() || !code.bytes().all(|b| b.is_ascii_digit()) {
        return Err(format!("type code '{code}' is not a number"));
    }
    Ok((code, reference))
}

/// The SDT type GUID and entity id of a `StructureTypeReference` fragment.
pub(crate) fn structure_ref(fragment: &str) -> Result<(String, i32), String> {
    let document = parse(fragment)?;
    let root = document.root_element();
    if root.tag_name().name() != "StructureTypeReference" {
        return Err(format!(
            "structure reference root is <{}>",
            root.tag_name().name()
        ));
    }
    let mut type_guid: Option<String> = None;
    let mut id: Option<String> = None;
    for child in root.children().filter(|n| n.is_element()) {
        let slot = match child.tag_name().name() {
            "Type" => &mut type_guid,
            "Id" => &mut id,
            other => return Err(format!("unsupported structure reference element <{other}>")),
        };
        if slot.is_some() {
            return Err(format!(
                "structure reference repeats <{}>",
                child.tag_name().name()
            ));
        }
        *slot = Some(child.text().unwrap_or("").trim().to_string());
    }
    let type_guid = type_guid.ok_or("structure reference has no Type")?;
    let id = id.ok_or("structure reference has no Id")?;
    let id = id
        .parse::<i32>()
        .map_err(|_| format!("structure reference Id '{id}' is not a number"))?;
    Ok((type_guid, id))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sdt_names(id: i32) -> Option<String> {
        match id {
            179 => Some("LiveSdtMember60744600".into()),
            40 => Some("Messages".into()),
            _ => None,
        }
    }

    /// One declaration in the stored form, with the given property list and type fragment.
    fn decl(name: &str, props: &[(&str, &str)]) -> String {
        let mut out = format!("<Variable Name=\"{name}\"><Properties>");
        for (k, v) in props {
            out.push_str(&format!(
                "<Property><Name>{k}</Name><Value>{}</Value></Property>",
                xml_escape(v)
            ));
        }
        out.push_str("</Properties></Variable>");
        out
    }

    fn xml_escape(v: &str) -> String {
        v.replace('&', "&amp;")
            .replace('<', "&lt;")
            .replace('>', "&gt;")
    }

    fn typed(code: &str) -> String {
        format!("<AttCustomType><DataType>{code}</DataType></AttCustomType>")
    }

    fn document(parts: &[String]) -> String {
        format!("<Variables>{}</Variables>", parts.join(""))
    }

    fn base(code: &str) -> Vec<(&'static str, String)> {
        vec![
            ("OBJ_TYPE", "id_OTYPE_VAR".into()),
            ("ATTCUSTOMTYPE", typed(code)),
        ]
    }

    fn one(name: &str, extra: &[(&str, &str)], code: &str) -> Result<Declarations, String> {
        let mut props: Vec<(&str, String)> = base(code);
        for (k, v) in extra {
            props.push((*k, v.to_string()));
        }
        let borrowed: Vec<(&str, &str)> = props.iter().map(|(k, v)| (*k, v.as_str())).collect();
        decode(
            &document(&[decl(name, &borrowed)]),
            &sdt_names,
            &|_| None,
            &|_| Ok(None),
        )
    }

    #[test]
    fn evidenced_type_codes_map_to_their_engine_types() {
        let d = one("Page", &[("Length", "6")], "4").unwrap();
        assert_eq!(
            d.variables["Page"],
            DataType::Numeric {
                precision: 6,
                scale: 0
            }
        );

        let d = one("Hits", &[("Length", "5"), ("Decimals", "2")], "4").unwrap();
        assert_eq!(
            d.variables["Hits"],
            DataType::Numeric {
                precision: 5,
                scale: 2
            }
        );

        let d = one("Time", &[("Length", "8")], "5").unwrap();
        assert_eq!(d.variables["Time"], DataType::Character { length: 8 });

        let d = one("Today", &[], "6").unwrap();
        assert_eq!(d.variables["Today"], DataType::Date {});

        let d = one("Stamp", &[], "12").unwrap();
        assert_eq!(d.variables["Stamp"], DataType::DateTime {});

        let d = one("Variable", &[("Length", "40"), ("AttMaxLen", "40")], "13").unwrap();
        assert_eq!(d.variables["Variable"], DataType::Varchar { length: 40 });

        let d = one("Success", &[], "15").unwrap();
        assert_eq!(d.variables["Success"], DataType::Boolean {});
    }

    #[test]
    fn an_sdt_reference_maps_to_a_record_and_names_its_entity() {
        let reference = "&lt;StructureTypeReference&gt;&lt;Type&gt;447527b5-9210-4523-898b-5dccb17be60a&lt;/Type&gt;&lt;Id&gt;179&lt;/Id&gt;&lt;/StructureTypeReference&gt;";
        let fragment = format!(
            "<AttCustomType><DataType>254</DataType><Guid>{reference}</Guid></AttCustomType>"
        );
        let xml = document(&[decl(
            "Messages",
            &[
                ("OBJ_TYPE", "id_OTYPE_VAR"),
                ("ATTCUSTOMTYPE", &fragment),
                ("DataTypeString", "LiveSdtMember60744600"),
                ("idBasedOn", "(none)"),
            ],
        )]);
        let d = decode(&xml, &sdt_names, &|_| None, &|_| Ok(None)).unwrap();
        assert_eq!(
            d.variables["Messages"],
            DataType::Record {
                schema: "LiveSdtMember60744600".into()
            }
        );
        assert_eq!(
            d.sdt_entity_ids.iter().copied().collect::<Vec<_>>(),
            vec![179]
        );
    }

    fn collection_root(namespace: Option<&str>) -> impl Fn(i32) -> Option<CollectionRoot> + '_ {
        move |id| {
            (id == 147).then(|| CollectionRoot {
                name: "ProgramNames".into(),
                item: "ProgramName".into(),
                namespace: namespace.map(str::to_string),
            })
        }
    }

    #[test]
    fn a_variable_of_an_sdt_whose_root_is_a_collection_is_a_base_collection_of_its_item_class() {
        // ListPrograms (KBTeste): `ProgramNames` is typed by SDT 147, whose root is a collection of
        // `ProgramName` in namespace `KBTeste`. The SDK declares it `GXBaseCollection<SdtProgramNames_ProgramName>`.
        let reference = "&lt;StructureTypeReference&gt;&lt;Type&gt;447527b5-9210-4523-898b-5dccb17be60a&lt;/Type&gt;&lt;Id&gt;147&lt;/Id&gt;&lt;/StructureTypeReference&gt;";
        let fragment = format!(
            "<AttCustomType><DataType>254</DataType><Guid>{reference}</Guid></AttCustomType>"
        );
        let xml = document(&[decl(
            "ProgramNames",
            &[
                ("OBJ_TYPE", "id_OTYPE_VAR"),
                ("ATTCUSTOMTYPE", &fragment),
                ("idBasedOn", "(none)"),
            ],
        )]);
        let failure = decode_with_collections(
            &xml,
            &|_| None,
            &|_| Err("collection metadata unavailable".into()),
            &|_| None,
            &|_| Ok(None),
        )
        .unwrap_err();
        assert!(failure.contains("variable 'ProgramNames'"), "{failure}");
        assert!(
            failure.contains("collection metadata unavailable"),
            "{failure}"
        );
        let root = collection_root(Some("KBTeste"));
        let d = decode_with_collections(&xml, &|_| None, &|id| Ok(root(id)), &|_| None, &|_| {
            Ok(None)
        })
        .unwrap();
        assert_eq!(
            d.variables["ProgramNames"],
            DataType::BaseCollection {
                schema: "ProgramNames_ProgramName".into(),
                item: "ProgramName".into(),
                namespace: "KBTeste".into(),
            }
        );
        assert_eq!(
            d.sdt_entity_ids.iter().copied().collect::<Vec<_>>(),
            vec![147],
            "the collection SDT is a Uses reference"
        );
    }

    #[test]
    fn a_collection_sdt_whose_namespace_cannot_be_read_refuses_its_variable_by_name() {
        let reference = "&lt;StructureTypeReference&gt;&lt;Type&gt;447527b5-9210-4523-898b-5dccb17be60a&lt;/Type&gt;&lt;Id&gt;147&lt;/Id&gt;&lt;/StructureTypeReference&gt;";
        let fragment = format!(
            "<AttCustomType><DataType>254</DataType><Guid>{reference}</Guid></AttCustomType>"
        );
        let xml = document(&[decl(
            "ProgramNames",
            &[
                ("OBJ_TYPE", "id_OTYPE_VAR"),
                ("ATTCUSTOMTYPE", &fragment),
                ("idBasedOn", "(none)"),
            ],
        )]);
        let root = collection_root(None);
        let err = decode_with_collections(&xml, &|_| None, &|id| Ok(root(id)), &|_| None, &|_| {
            Ok(None)
        })
        .unwrap_err();
        assert!(err.contains("variable 'ProgramNames'"), "{err}");
        assert!(err.contains("XML namespace could not be read"), "{err}");
    }

    #[test]
    fn an_unmapped_code_is_refused_by_name_not_guessed() {
        let err = one("FilterTags", &[], "255").unwrap_err();
        assert!(err.contains("variable 'FilterTags'"), "{err}");
        assert!(err.contains("type code 255"), "{err}");
    }

    #[test]
    fn new_type_codes_map_to_their_engine_types() {
        // LONGVARCHAR stores only its type code; a stored width on another row is not read.
        let d = one("RefreshToken", &[], "8").unwrap();
        assert_eq!(d.variables["RefreshToken"], DataType::LongVarchar {});
        let d = one("Notes", &[("Length", "9216"), ("Decimals", "3072")], "8").unwrap();
        assert_eq!(d.variables["Notes"], DataType::LongVarchar {});

        let d = one("Photo", &[("Length", "1000")], "7").unwrap();
        assert_eq!(d.variables["Photo"], DataType::Image {});
        let d = one("UUID", &[("Length", "4")], "45").unwrap();
        assert_eq!(d.variables["UUID"], DataType::Guid {});
        for code in ["102", "103", "104", "105"] {
            let d = one("Where", &[("Length", "50")], code).unwrap();
            assert_eq!(d.variables["Where"], DataType::Geospatial {});
        }
    }

    #[test]
    fn new_type_codes_refuse_a_stored_decimals_by_name() {
        // No evidence gives a decimals meaning for these types, so a stored Decimals is refused.
        for (code, name) in [("7", "BITMAP"), ("45", "GUID"), ("103", "GEO")] {
            let err = one("X", &[("Decimals", "2")], code).unwrap_err();
            assert!(err.contains(name), "{code}: {err}");
        }
    }

    #[test]
    fn a_collection_is_refused_by_name() {
        let err = one(
            "Groups",
            &[("Length", "20"), ("AttCollection", "True")],
            "5",
        )
        .unwrap_err();
        assert!(err.contains("variable 'Groups'"), "{err}");
        assert!(err.contains("collections are not mapped"), "{err}");
    }

    #[test]
    fn a_based_on_declaration_is_refused_by_name() {
        let err = one(
            "GxObject",
            &[("idBasedOn", "00972a17-9975-449e-aab1-d26165d51393-51")],
            "5",
        )
        .unwrap_err();
        assert!(err.contains("variable 'GxObject'"), "{err}");
        assert!(
            err.contains("not mapped") || err.contains("not read"),
            "{err}"
        );

        // The same reference with no declared type is the NOCUSTOM case of the evidence KBs.
        let xml = document(&[decl(
            "GxObject",
            &[
                ("OBJ_TYPE", "id_OTYPE_VAR"),
                ("idBasedOn", "00972a17-9975-449e-aab1-d26165d51393-51"),
            ],
        )]);
        let err = decode(&xml, &sdt_names, &|_| None, &|_| Ok(None)).unwrap_err();
        assert!(err.contains("variable 'GxObject'"), "{err}");
        assert!(
            err.contains("the domain has no stored type this engine maps"),
            "{err}"
        );
    }

    #[test]
    fn a_nested_sdt_level_is_refused_by_name() {
        let reference = "&lt;StructureTypeReference&gt;&lt;Type&gt;a76e9340-bdb9-445d-8f81-cfd4ddd0b0f3&lt;/Type&gt;&lt;Id&gt;219&lt;/Id&gt;&lt;/StructureTypeReference&gt;";
        let fragment = format!(
            "<AttCustomType><DataType>254</DataType><Guid>{reference}</Guid></AttCustomType>"
        );
        let xml = document(&[decl(
            "Entry",
            &[
                ("OBJ_TYPE", "id_OTYPE_VAR"),
                ("ATTCUSTOMTYPE", &fragment),
                ("DataTypeString", "RCE06829400.Entry"),
            ],
        )]);
        let err = decode(&xml, &sdt_names, &|_| None, &|_| Ok(None)).unwrap_err();
        assert!(err.contains("variable 'Entry'"), "{err}");
        assert!(
            err.contains("is not a level of an SDT this engine maps"),
            "{err}"
        );
    }

    /// One stored level, as `level_of` returns it.
    fn level(sdt_id: i32, schema: &str, dotted: &str, collection_item: bool) -> NestedLevel {
        NestedLevel {
            sdt_id,
            schema: schema.into(),
            dotted: dotted.into(),
            collection_item,
        }
    }

    /// The `ATTCUSTOMTYPE` of a declaration typed by the nested level `id`, as the KB stores it.
    fn nested_fragment(id: i32) -> String {
        format!(
            "<AttCustomType><DataType>254</DataType><Guid>&lt;StructureTypeReference&gt;&lt;Type&gt;a76e9340-bdb9-445d-8f81-cfd4ddd0b0f3&lt;/Type&gt;&lt;Id&gt;{id}&lt;/Id&gt;&lt;/StructureTypeReference&gt;</Guid></AttCustomType>"
        )
    }

    /// The real `Row` of `RTDX765000A` (GX18 KBTeste): level 219, `RSN03944700.RowsItem`, a collection
    /// item with no stored item name. The SDT entity id 4321 is a placeholder; the level's stored id,
    /// collection flag and dotted name are the real ones.
    #[test]
    fn a_collection_item_level_is_typed_by_its_class_and_held_by_its_sdt() {
        let fragment = nested_fragment(219);
        let xml = document(&[decl(
            "Row",
            &[
                ("OBJ_TYPE", "id_OTYPE_VAR"),
                ("ATTCUSTOMTYPE", &fragment),
                ("DataTypeString", "RSN03944700.RowsItem"),
            ],
        )]);
        let levels = |id: i32| -> Result<Option<NestedLevel>, String> {
            Ok((id == 219)
                .then(|| level(4321, "RSN03944700_RowsItem", "RSN03944700.RowsItem", true)))
        };
        let d = decode(&xml, &sdt_names, &|_| None, &levels).expect("a collection item is mapped");
        assert_eq!(
            d.variables.get("Row"),
            Some(&DataType::Record {
                schema: "RSN03944700_RowsItem".into()
            })
        );
        assert_eq!(
            d.sdt_entity_ids.iter().copied().collect::<Vec<_>>(),
            vec![4321],
            "the variable uses the SDT that holds the level, not the level"
        );
    }

    /// The real `Entry` of `RCE06829400` (level 221), whose stored item name is `Entry`: the class is
    /// `RCE06829400_Entry`. The declaration carries no `DataTypeString`, as stored.
    #[test]
    fn a_collection_item_with_a_stored_item_name_takes_that_name() {
        let fragment = nested_fragment(221);
        let xml = document(&[decl(
            "Row",
            &[("OBJ_TYPE", "id_OTYPE_VAR"), ("ATTCUSTOMTYPE", &fragment)],
        )]);
        let levels = |id: i32| -> Result<Option<NestedLevel>, String> {
            Ok((id == 221).then(|| level(4322, "RCE06829400_Entry", "RCE06829400.Entry", true)))
        };
        let d = decode(&xml, &sdt_names, &|_| None, &levels).expect("decodes");
        assert_eq!(
            d.variables.get("Row"),
            Some(&DataType::Record {
                schema: "RCE06829400_Entry".into()
            })
        );
    }

    #[test]
    fn a_nested_level_that_is_not_a_collection_item_is_refused_by_name() {
        let fragment = nested_fragment(218);
        let xml = document(&[decl(
            "Child",
            &[("OBJ_TYPE", "id_OTYPE_VAR"), ("ATTCUSTOMTYPE", &fragment)],
        )]);
        let levels = |_: i32| -> Result<Option<NestedLevel>, String> {
            Ok(Some(level(
                4321,
                "RSN03944700_Child",
                "RSN03944700.Child",
                false,
            )))
        };
        let err = decode(&xml, &sdt_names, &|_| None, &levels).unwrap_err();
        assert!(err.contains("variable 'Child'"), "{err}");
        assert!(err.contains("is not one item of a collection"), "{err}");
    }

    #[test]
    fn a_declared_name_that_is_not_the_level_is_refused_by_name() {
        let fragment = nested_fragment(219);
        let xml = document(&[decl(
            "Row",
            &[
                ("OBJ_TYPE", "id_OTYPE_VAR"),
                ("ATTCUSTOMTYPE", &fragment),
                ("DataTypeString", "RSN03944700.Entry"),
            ],
        )]);
        let levels = |_: i32| -> Result<Option<NestedLevel>, String> {
            Ok(Some(level(
                4321,
                "RSN03944700_RowsItem",
                "RSN03944700.RowsItem",
                true,
            )))
        };
        let err = decode(&xml, &sdt_names, &|_| None, &levels).unwrap_err();
        assert!(
            err.contains("does not match level 'RSN03944700.RowsItem'"),
            "{err}"
        );
    }

    /// SYNTHETIC: a stored nested declaration that also declares a length. No stored row of this shape
    /// is known; the decoder refuses it as it refuses an SDT reference with a length.
    #[test]
    fn a_nested_level_declaration_with_a_length_is_refused_by_name() {
        let fragment = nested_fragment(219);
        let xml = document(&[decl(
            "Row",
            &[
                ("OBJ_TYPE", "id_OTYPE_VAR"),
                ("ATTCUSTOMTYPE", &fragment),
                ("Length", "8"),
            ],
        )]);
        let levels = |_: i32| -> Result<Option<NestedLevel>, String> {
            Ok(Some(level(
                4321,
                "RSN03944700_RowsItem",
                "RSN03944700.RowsItem",
                true,
            )))
        };
        let err = decode(&xml, &sdt_names, &|_| None, &levels).unwrap_err();
        assert!(err.contains("SDT declares a length"), "{err}");
    }

    /// A level read with an error is an error for the whole part, not a missing level.
    #[test]
    fn a_level_that_cannot_be_read_refuses_the_variable_with_that_error() {
        let fragment = nested_fragment(219);
        let xml = document(&[decl(
            "Row",
            &[("OBJ_TYPE", "id_OTYPE_VAR"), ("ATTCUSTOMTYPE", &fragment)],
        )]);
        let levels = |_: i32| -> Result<Option<NestedLevel>, String> {
            Err("the model could not be read".into())
        };
        let err = decode(&xml, &sdt_names, &|_| None, &levels).unwrap_err();
        assert!(err.contains("the model could not be read"), "{err}");
    }

    #[test]
    fn an_sdt_whose_declared_name_disagrees_with_the_model_is_refused() {
        let reference = "&lt;StructureTypeReference&gt;&lt;Type&gt;447527b5-9210-4523-898b-5dccb17be60a&lt;/Type&gt;&lt;Id&gt;179&lt;/Id&gt;&lt;/StructureTypeReference&gt;";
        let fragment = format!(
            "<AttCustomType><DataType>254</DataType><Guid>{reference}</Guid></AttCustomType>"
        );
        let xml = document(&[decl(
            "Messages",
            &[
                ("OBJ_TYPE", "id_OTYPE_VAR"),
                ("ATTCUSTOMTYPE", &fragment),
                ("DataTypeString", "SomeOtherSdt"),
            ],
        )]);
        let err = decode(&xml, &sdt_names, &|_| None, &|_| Ok(None)).unwrap_err();
        assert!(err.contains("does not match SDT 179"), "{err}");
    }

    #[test]
    fn an_sdt_id_that_is_not_an_sdt_of_the_model_is_refused() {
        let reference = "&lt;StructureTypeReference&gt;&lt;Type&gt;447527b5-9210-4523-898b-5dccb17be60a&lt;/Type&gt;&lt;Id&gt;999&lt;/Id&gt;&lt;/StructureTypeReference&gt;";
        let fragment = format!(
            "<AttCustomType><DataType>254</DataType><Guid>{reference}</Guid></AttCustomType>"
        );
        let xml = document(&[decl(
            "Missing",
            &[("OBJ_TYPE", "id_OTYPE_VAR"), ("ATTCUSTOMTYPE", &fragment)],
        )]);
        let err = decode(&xml, &sdt_names, &|_| None, &|_| Ok(None)).unwrap_err();
        assert!(err.contains("is not an SDT this engine maps"), "{err}");
    }

    #[test]
    fn a_character_without_a_stored_length_takes_the_sdk_length_of_twenty() {
        let d = one("FilterName", &[], "5").unwrap();
        assert_eq!(
            d.variables["FilterName"],
            DataType::Character { length: 20 }
        );
    }

    #[test]
    fn a_collection_without_a_stored_length_is_still_refused_as_a_collection() {
        // The stored Groups rows of AddDeviceGroups and RemoveDeviceGroups (both KBs) carry AttCollection
        // True. The CHARACTER(20) default must not reach them: the collection refusal runs first.
        let err = one("Groups", &[("AttCollection", "True")], "5").unwrap_err();
        assert!(err.contains("collections are not mapped"), "{err}");
    }

    #[test]
    fn an_unknown_property_is_refused_by_name() {
        let err = one("Page", &[("Length", "6"), ("SurpriseFlag", "1")], "4").unwrap_err();
        assert!(err.contains("unsupported property SurpriseFlag"), "{err}");
    }

    #[test]
    fn a_length_that_disagrees_with_its_max_length_is_refused() {
        let err = one("Page", &[("Length", "6"), ("AttMaxLen", "7")], "4").unwrap_err();
        assert!(err.contains("AttMaxLen 7 differs from Length 6"), "{err}");
    }

    #[test]
    fn a_numeric_scale_above_its_precision_is_refused() {
        let err = one("Bad", &[("Length", "2"), ("Decimals", "3")], "4").unwrap_err();
        assert!(err.contains("scale 3 is above precision 2"), "{err}");
    }

    #[test]
    fn the_stored_documentation_container_is_admitted_and_carries_no_type() {
        let wiki = "<Documentation><WikiPage><Modified>0001-01-01T00:00:00</Modified><Revision>0</Revision></WikiPage></Documentation>";
        let decl = format!(
            "<Variable Name=\"Page\">{wiki}<Properties><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>{}</Value></Property><Property><Name>Length</Name><Value>6</Value></Property></Properties></Variable>",
            xml_escape(&typed("4"))
        );
        let d = decode(&document(&[decl]), &sdt_names, &|_| None, &|_| Ok(None))
            .expect("Documentation is admitted");
        assert_eq!(
            d.variables["Page"],
            DataType::Numeric {
                precision: 6,
                scale: 0
            }
        );
    }

    #[test]
    fn an_unknown_child_of_a_declaration_is_still_refused_by_name() {
        let decl = format!(
            "<Variable Name=\"Page\"><Surprise/><Properties><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>{}</Value></Property></Properties></Variable>",
            xml_escape(&typed("4"))
        );
        let err = decode(&document(&[decl]), &sdt_names, &|_| None, &|_| Ok(None)).unwrap_err();
        assert!(err.contains("unsupported child <Surprise>"), "{err}");
    }

    #[test]
    fn a_date_with_a_length_other_than_eight_is_refused() {
        let err = one("Today", &[("Length", "10")], "6").unwrap_err();
        assert!(err.contains("DATE declares length 10, expected 8"), "{err}");
        let err = one("Stamp", &[("Length", "4")], "12").unwrap_err();
        assert!(
            err.contains("DATETIME declares length 4, expected 8"),
            "{err}"
        );
    }

    #[test]
    fn a_zero_width_boolean_is_accepted_and_a_real_width_is_refused() {
        // RSC79223900.Flag is stored with Length 0 and Decimals 0; the SDK prints a plain `Boolean`.
        let d = one("Flag", &[("Length", "0"), ("Decimals", "0")], "15").unwrap();
        assert_eq!(d.variables["Flag"], DataType::Boolean {});
        let err = one("Flag", &[("Length", "4")], "15").unwrap_err();
        assert!(err.contains("BOOLEAN declares a length"), "{err}");
    }

    #[test]
    fn the_sdt_row_property_set_is_wider_than_the_declaration_set_and_the_declaration_stays_strict()
    {
        // A stored SDT item carries `Description` and `idCollectionItemName` (real rows, the KBTeste
        // model export). The VARIABLES declaration set does not carry them, and still refuses them.
        let row = "<Properties><Property><Name>Name</Name><Value>Key</Value></Property>\
                   <Property><Name>Description</Name><Value>d</Value></Property>\
                   <Property><Name>idCollectionItemName</Name><Value>Item</Value></Property></Properties>";
        let props = property_map(row).expect("a real SDT row decodes");
        assert_eq!(props.get("Description").map(String::as_str), Some("d"));
        let decl = "<Variable Name=\"Key\"><Properties><Property><Name>Description</Name><Value>d</Value></Property></Properties></Variable>";
        let document = parse(decl).expect("parses");
        let error = properties(document.root_element()).unwrap_err();
        assert!(
            error.contains("unsupported property Description"),
            "{error}"
        );
    }

    #[test]
    fn stored_decimals_the_sdk_prints_are_accepted_where_they_do_not_change_the_type() {
        // Real stored rows: CHARACTER with Decimals 0 (Notification.Picture, 4 rows), DATETIME with
        // Decimals 5 (FacebookAccessToken.ExpirationDate, SDK `DATETIME(8,5)`), and VARCHAR with
        // Decimals (SDK `VARCHAR(2048,250)`). The portable type does not carry the decimals.
        let d = one("Picture", &[("Length", "100"), ("Decimals", "0")], "5").unwrap();
        assert_eq!(d.variables["Picture"], DataType::Character { length: 100 });
        let d = one(
            "ExpirationDate",
            &[("Length", "8"), ("Decimals", "5")],
            "12",
        )
        .unwrap();
        assert_eq!(d.variables["ExpirationDate"], DataType::DateTime {});
        let d = one("scope", &[("Length", "2048"), ("Decimals", "250")], "13").unwrap();
        assert_eq!(d.variables["scope"], DataType::Varchar { length: 2048 });
    }

    #[test]
    fn a_character_or_date_that_declares_decimals_is_refused() {
        let err = one("Time", &[("Length", "8"), ("Decimals", "2")], "5").unwrap_err();
        assert!(err.contains("CHARACTER declares decimals"), "{err}");
        let err = one("Today", &[("Decimals", "1")], "6").unwrap_err();
        assert!(err.contains("DATE declares decimals"), "{err}");
    }

    #[test]
    fn a_declaration_without_a_type_is_refused() {
        let xml = document(&[decl("Plain", &[("OBJ_TYPE", "id_OTYPE_VAR")])]);
        let err = decode(&xml, &sdt_names, &|_| None, &|_| Ok(None)).unwrap_err();
        assert!(err.contains("variable 'Plain'"), "{err}");
        assert!(err.contains("has no type"), "{err}");
    }

    #[test]
    fn a_duplicate_variable_name_is_refused() {
        let one_decl = decl(
            "Page",
            &[("OBJ_TYPE", "id_OTYPE_VAR"), ("ATTCUSTOMTYPE", &typed("6"))],
        );
        let err = decode(
            &document(&[one_decl.clone(), one_decl]),
            &sdt_names,
            &|_| None,
            &|_| Ok(None),
        )
        .unwrap_err();
        assert!(err.contains("declared twice"), "{err}");
    }

    #[test]
    fn a_root_that_is_not_variables_is_refused() {
        let err = decode("<Source>x</Source>", &sdt_names, &|_| None, &|_| Ok(None)).unwrap_err();
        assert!(err.contains("expected <Variables>"), "{err}");
    }

    #[test]
    fn a_standard_variable_decodes_like_a_variable() {
        let xml = format!(
            "<Variables><StandardVariable Name=\"Today\"><Properties>\
             <Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property>\
             <Property><Name>IsStandardVariable</Name><Value>True</Value></Property>\
             <Property><Name>ATTCUSTOMTYPE</Name><Value>{}</Value></Property>\
             </Properties></StandardVariable></Variables>",
            xml_escape(&typed("6"))
        );
        let d = decode(&xml, &sdt_names, &|_| None, &|_| Ok(None)).unwrap();
        assert_eq!(d.variables["Today"], DataType::Date {});
    }

    #[test]
    fn an_empty_variables_root_declares_nothing() {
        let d = decode("<Variables/>", &sdt_names, &|_| None, &|_| Ok(None)).unwrap();
        assert!(d.variables.is_empty());
        assert!(d.sdt_entity_ids.is_empty());
    }

    // Stored Domain rows of KBTeste (GX18), read in full: CONVERT(VARCHAR(MAX),
    // EntityVersionProperties, 2) of each Domain, not reconstructed.
    const DOMAIN_URL: &str = r##"<Properties><Property><Name>Name</Name><Value>Url</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;13&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>1000</Value></Property><Property><Name>AttMaxLen</Name><Value>1000</Value></Property><Property><Name>AttRegEx</Name><Value>^((?:[a-zA-Z]+:(//)?)?((?:(?:[a-zA-Z]([a-zA-Z0-9$\-_@&amp;+!*"'(),]|%[0-9a-fA-F]{2})*)(?:\.(?:([a-zA-Z0-9$\-_@&amp;+!*"'(),]|%[0-9a-fA-F]{2})*))*)|(?:(\d{1,3}\.){3}\d{1,3}))(?::\d+)?(?:/([a-zA-Z0-9$\-_@.&amp;+!*"'(),=;: ]|%[0-9a-fA-F]{2})+)*/?(?:[#?](?:[a-zA-Z0-9$\-_@.&amp;+!*"'(),=;: /]|%[0-9a-fA-F]{2})*)?)?\s*$</Value></Property><Property><Name>AutoCorrection</Name><Value>False</Value></Property><Property><Name>AutoCapitalization</Name><Value>None</Value></Property><Property><Name>AutoResize</Name><Value>False</Value></Property><Property><Name>GxHeight</Name><Value>1row</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
    const DOMAIN_EMAIL: &str = r##"<Properties><Property><Name>Name</Name><Value>Email</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;13&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>100</Value></Property><Property><Name>AttMaxLen</Name><Value>100</Value></Property><Property><Name>AttRegEx</Name><Value>^((\w+([-+.']\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*)|(\s*))$</Value></Property><Property><Name>AutoCorrection</Name><Value>False</Value></Property><Property><Name>AutoCapitalization</Name><Value>None</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
    const DOMAIN_GEOLOCATION: &str = r##"<Properties><Property><Name>Name</Name><Value>Geolocation</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;5&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>50</Value></Property><Property><Name>AttMaxLen</Name><Value>50</Value></Property><Property><Name>AttRegEx</Name><Value>^\s*(-?([0-8]?[0-9]\.{1}\d+|90\.{1}0+)\s*,\s*-?([0-9]{1,2}\.{1}\d+|1[0-7][0-9]\.{1}\d+|180\.{1}0+)\s*)?$</Value></Property><Property><Name>AutoCorrection</Name><Value>False</Value></Property><Property><Name>AutoCapitalization</Name><Value>None</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
    const DOMAIN_OBJECT_NAME: &str = r##"<Properties><Property><Name>Name</Name><Value>ObjectName</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;13&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>256</Value></Property><Property><Name>AttMaxLen</Name><Value>256</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
    const DOMAIN_BEACON_PROXIMITY: &str = r##"<Properties><Property><Name>Name</Name><Value>BeaconProximity</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>1</Value></Property><Property><Name>AttMaxLen</Name><Value>1</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;Unknown&lt;/Name&gt;&lt;Description&gt;Unknown&lt;/Description&gt;&lt;Value&gt;0&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Inmediate&lt;/Name&gt;&lt;Description&gt;Inmediate&lt;/Description&gt;&lt;Value&gt;1&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Near&lt;/Name&gt;&lt;Description&gt;Near&lt;/Description&gt;&lt;Value&gt;2&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Far&lt;/Name&gt;&lt;Description&gt;Far&lt;/Description&gt;&lt;Value&gt;3&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
    const DOMAIN_ID_MANUAL: &str = r##"<Properties><Property><Name>Name</Name><Value>IDManual</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>18</Value></Property><Property><Name>AttMaxLen</Name><Value>18</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>1</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
    const DOMAIN_DATA: &str = r##"<Properties><Property><Name>Name</Name><Value>Data</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;6&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>1</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
    const DOMAIN_PLAYBACK_STATE: &str = r##"<Properties><Property><Name>Name</Name><Value>PlaybackState</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;None&lt;/Name&gt;&lt;Description&gt;None&lt;/Description&gt;&lt;Value&gt;0&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Stopped&lt;/Name&gt;&lt;Description&gt;Stopped&lt;/Description&gt;&lt;Value&gt;1&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Paused&lt;/Name&gt;&lt;Description&gt;Paused&lt;/Description&gt;&lt;Value&gt;2&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Playing&lt;/Name&gt;&lt;Description&gt;Playing&lt;/Description&gt;&lt;Value&gt;3&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Error&lt;/Name&gt;&lt;Description&gt;Error&lt;/Description&gt;&lt;Value&gt;4&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Transitional&lt;/Name&gt;&lt;Description&gt;Transitional&lt;/Description&gt;&lt;Value&gt;5&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
    const DOMAIN_STORE_PRODUCT_TYPE: &str = r##"<Properties><Property><Name>Name</Name><Value>StoreProductType</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;Subscription&lt;/Name&gt;&lt;Description&gt;Subscription&lt;/Description&gt;&lt;Value&gt;2&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Product&lt;/Name&gt;&lt;Description&gt;Product&lt;/Description&gt;&lt;Value&gt;1&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>13</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
    // SYNTHETIC: the Url row with its Length property removed.
    const SYNTHETIC_URL_NO_LENGTH: &str = r##"<Properties><Property><Name>Name</Name><Value>Url</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;13&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>AttMaxLen</Name><Value>1000</Value></Property><Property><Name>AttRegEx</Name><Value>^((?:[a-zA-Z]+:(//)?)?((?:(?:[a-zA-Z]([a-zA-Z0-9$\-_@&amp;+!*"'(),]|%[0-9a-fA-F]{2})*)(?:\.(?:([a-zA-Z0-9$\-_@&amp;+!*"'(),]|%[0-9a-fA-F]{2})*))*)|(?:(\d{1,3}\.){3}\d{1,3}))(?::\d+)?(?:/([a-zA-Z0-9$\-_@.&amp;+!*"'(),=;: ]|%[0-9a-fA-F]{2})+)*/?(?:[#?](?:[a-zA-Z0-9$\-_@.&amp;+!*"'(),=;: /]|%[0-9a-fA-F]{2})*)?)?\s*$</Value></Property><Property><Name>AutoCorrection</Name><Value>False</Value></Property><Property><Name>AutoCapitalization</Name><Value>None</Value></Property><Property><Name>AutoResize</Name><Value>False</Value></Property><Property><Name>GxHeight</Name><Value>1row</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
    // SYNTHETIC: the Email row with one property name the Domain allowlist does not list.
    const SYNTHETIC_EMAIL_UNSEEN_PROPERTY: &str = r##"<Properties><Property><Name>Name</Name><Value>Email</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;13&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>100</Value></Property><Property><Name>AttMaxLen</Name><Value>100</Value></Property><Property><Name>AttRegEx</Name><Value>^((\w+([-+.']\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*)|(\s*))$</Value></Property><Property><Name>AutoCorrection</Name><Value>False</Value></Property><Property><Name>AutoCapitalization</Name><Value>None</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property><Property><Name>NotInTheList</Name><Value>1</Value></Property></Properties>"##;

    // The SDK's own resolved type of each domain is the expected value: it is read from the served
    // `sdtStructure` of a KB's SDT item based on the domain (`genexus_inspect include=["structure"]`),
    // or, for IDManual and Data, from a Procedure variable `&EmpresaID : IDManual` / `&DataLimite : Data`
    // (`genexus_read part=Variables`, structured `variables` array: NUMERIC 18 and DATE 8).

    #[test]
    fn a_typed_domain_takes_the_type_its_stored_row_declares_as_the_sdk_reads_it() {
        // Url: VARCHAR(1000) (OAuthAuthorizationConfig.AccessTokenUrl on GX18; LinkList.URL and
        // MediaItem.Uri on GX16).
        assert_eq!(
            domain_data_type(DOMAIN_URL),
            Some(DataType::Varchar { length: 1000 })
        );
        // Email: VARCHAR(100) (ContactInfo.EMail, GX16 and GX18).
        assert_eq!(
            domain_data_type(DOMAIN_EMAIL),
            Some(DataType::Varchar { length: 100 })
        );
        // Geolocation: CHARACTER(50) (GeolocationProximityAlert.GeoLocation, GX16 and GX18).
        assert_eq!(
            domain_data_type(DOMAIN_GEOLOCATION),
            Some(DataType::Character { length: 50 })
        );
        // ObjectName: VARCHAR(256) (TransactionContext.CallerObject, GX18).
        assert_eq!(
            domain_data_type(DOMAIN_OBJECT_NAME),
            Some(DataType::Varchar { length: 256 })
        );
        // BeaconProximity: NUMERIC(1) (BeaconState.Proximity, GX16 and GX18). An enumeration that
        // stores its code type and length.
        assert_eq!(
            domain_data_type(DOMAIN_BEACON_PROXIMITY),
            Some(DataType::Numeric {
                precision: 1,
                scale: 0
            })
        );
        // IDManual: NUMERIC(18) (Procedure variable &EmpresaID : IDManual, GX18).
        assert_eq!(
            domain_data_type(DOMAIN_ID_MANUAL),
            Some(DataType::Numeric {
                precision: 18,
                scale: 0
            })
        );
    }

    #[test]
    fn a_fixed_length_domain_takes_its_type_with_no_stored_length() {
        // Data: DATE(8) (Procedure variable &DataLimite : Data, GX18). DATE stores no Length.
        assert_eq!(domain_data_type(DOMAIN_DATA), Some(DataType::Date {}));
    }

    #[test]
    fn an_enumeration_with_no_stored_type_has_no_domain_type() {
        // PlaybackState and StoreProductType store no ATTCUSTOMTYPE and no Length. The SDK gives
        // their items NUMERIC(4), but that length is not a stored value, so no type is evidenced
        // and a based-on item of either stays refused.
        assert_eq!(domain_data_type(DOMAIN_PLAYBACK_STATE), None);
        assert_eq!(domain_data_type(DOMAIN_STORE_PRODUCT_TYPE), None);
    }

    #[test]
    fn a_variable_length_typed_domain_with_no_stored_length_has_no_type() {
        // SYNTHETIC: the Url row with its Length removed. decode_type refuses a VARCHAR with no
        // stored length, so the domain has no type; nothing is defaulted.
        assert_eq!(domain_data_type(SYNTHETIC_URL_NO_LENGTH), None);
    }

    #[test]
    fn a_domain_row_with_a_property_the_domain_list_does_not_name_has_no_type() {
        // SYNTHETIC: the Email row plus one property name that is not in DOMAIN_ROW_PROPERTIES. The
        // row is refused, not read with the unknown property ignored.
        assert_eq!(domain_data_type(SYNTHETIC_EMAIL_UNSEEN_PROPERTY), None);
        assert!(domain_property_map(SYNTHETIC_EMAIL_UNSEEN_PROPERTY).is_err());
    }

    // The decoded VARIABLES part of ProcVarDomain (KBTeste, GX18), read in full. Its three based-on
    // variables are EmpresaID and ClienteID (Domain 119, IDManual) and DataLimite (Domain 120, Data).
    const PROC_VAR_DOMAIN_VARIABLES: &str = r##"<Variables><Variable Id="8" Name="EmpresaID"><Properties><Property><Name>Name</Name><Value>EmpresaID</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>idBasedOn</Name><Value>00972a17-9975-449e-aab1-d26165d51393-119</Value></Property></Properties><Documentation>&lt;WikiPage&gt;&lt;Modified&gt;0001-01-01T00:00:00&lt;/Modified&gt;&lt;Revision&gt;0&lt;/Revision&gt;&lt;/WikiPage&gt;</Documentation></Variable><Variable Id="9" Name="ClienteID"><Properties><Property><Name>Name</Name><Value>ClienteID</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>idBasedOn</Name><Value>00972a17-9975-449e-aab1-d26165d51393-119</Value></Property></Properties><Documentation>&lt;WikiPage&gt;&lt;Modified&gt;0001-01-01T00:00:00&lt;/Modified&gt;&lt;Revision&gt;0&lt;/Revision&gt;&lt;/WikiPage&gt;</Documentation></Variable><Variable Id="10" Name="DataLimite"><Properties><Property><Name>Name</Name><Value>DataLimite</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>idBasedOn</Name><Value>00972a17-9975-449e-aab1-d26165d51393-120</Value></Property></Properties><Documentation>&lt;WikiPage&gt;&lt;Modified&gt;0001-01-01T00:00:00&lt;/Modified&gt;&lt;Revision&gt;0&lt;/Revision&gt;&lt;/WikiPage&gt;</Documentation></Variable><StandardVariable Id="1" Name="Today"><Properties><Property><Name>Name</Name><Value>Today</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>IsStandardVariable</Name><Value>True</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;6&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property></Properties><Documentation>&lt;WikiPage&gt;&lt;Modified&gt;0001-01-01T00:00:00&lt;/Modified&gt;&lt;Revision&gt;0&lt;/Revision&gt;&lt;/WikiPage&gt;</Documentation></StandardVariable><StandardVariable Id="2" Name="Time"><Properties><Property><Name>Name</Name><Value>Time</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>IsStandardVariable</Name><Value>True</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;5&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>8</Value></Property></Properties><Documentation>&lt;WikiPage&gt;&lt;Modified&gt;0001-01-01T00:00:00&lt;/Modified&gt;&lt;Revision&gt;0&lt;/Revision&gt;&lt;/WikiPage&gt;</Documentation></StandardVariable><StandardVariable Id="3" Name="Pgmname"><Properties><Property><Name>Name</Name><Value>Pgmname</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>IsStandardVariable</Name><Value>True</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;5&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>128</Value></Property></Properties><Documentation>&lt;WikiPage&gt;&lt;Modified&gt;0001-01-01T00:00:00&lt;/Modified&gt;&lt;Revision&gt;0&lt;/Revision&gt;&lt;/WikiPage&gt;</Documentation></StandardVariable><StandardVariable Id="4" Name="Pgmdesc"><Properties><Property><Name>Name</Name><Value>Pgmdesc</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>IsStandardVariable</Name><Value>True</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;5&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>256</Value></Property></Properties><Documentation>&lt;WikiPage&gt;&lt;Modified&gt;0001-01-01T00:00:00&lt;/Modified&gt;&lt;Revision&gt;0&lt;/Revision&gt;&lt;/WikiPage&gt;</Documentation></StandardVariable><StandardVariable Id="5" Name="Page"><Properties><Property><Name>Name</Name><Value>Page</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>IsStandardVariable</Name><Value>True</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>6</Value></Property></Properties><Documentation>&lt;WikiPage&gt;&lt;Modified&gt;0001-01-01T00:00:00&lt;/Modified&gt;&lt;Revision&gt;0&lt;/Revision&gt;&lt;/WikiPage&gt;</Documentation></StandardVariable><StandardVariable Id="6" Name="Line"><Properties><Property><Name>Name</Name><Value>Line</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>IsStandardVariable</Name><Value>True</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>6</Value></Property></Properties><Documentation>&lt;WikiPage&gt;&lt;Modified&gt;0001-01-01T00:00:00&lt;/Modified&gt;&lt;Revision&gt;0&lt;/Revision&gt;&lt;/WikiPage&gt;</Documentation></StandardVariable><StandardVariable Id="7" Name="Output"><Properties><Property><Name>Name</Name><Value>Output</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>IsStandardVariable</Name><Value>True</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;5&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>3</Value></Property></Properties><Documentation>&lt;WikiPage&gt;&lt;Modified&gt;0001-01-01T00:00:00&lt;/Modified&gt;&lt;Revision&gt;0&lt;/Revision&gt;&lt;/WikiPage&gt;</Documentation></StandardVariable></Variables>"##;

    // The domains the fixture uses, decoded from their own stored rows (see the domain tests above).
    fn domain_lookup(id: i32) -> Option<DataType> {
        match id {
            119 => domain_data_type(DOMAIN_ID_MANUAL),
            120 => domain_data_type(DOMAIN_DATA),
            _ => None,
        }
    }

    #[test]
    fn a_based_on_variable_takes_the_stored_type_of_its_domain_as_the_sdk_reads_it() {
        // SDK (genexus_read part=Variables, structured): &EmpresaID : IDManual is NUMERIC 18,
        // &ClienteID : IDManual is NUMERIC 18, &DataLimite : Data is DATE 8.
        let d = decode(
            PROC_VAR_DOMAIN_VARIABLES,
            &sdt_names,
            &domain_lookup,
            &|_| Ok(None),
        )
        .expect("decodes");
        assert_eq!(
            d.variables["EmpresaID"],
            DataType::Numeric {
                precision: 18,
                scale: 0
            }
        );
        assert_eq!(
            d.variables["ClienteID"],
            DataType::Numeric {
                precision: 18,
                scale: 0
            }
        );
        assert_eq!(d.variables["DataLimite"], DataType::Date {});
        // The standard variables are read as before.
        assert_eq!(d.variables["Time"], DataType::Character { length: 8 });
        assert_eq!(d.variables.len(), 10);
        // Each Domain the variables are based on is recorded, and no SDT is.
        assert_eq!(d.domain_entity_ids, BTreeSet::from([119, 120]));
        assert!(d.sdt_entity_ids.is_empty());
    }

    #[test]
    fn a_based_on_variable_whose_domain_has_no_stored_type_is_refused_by_name() {
        // With no type for Domain 119, the first based-on variable is refused, named.
        let err = decode(PROC_VAR_DOMAIN_VARIABLES, &sdt_names, &|_| None, &|_| {
            Ok(None)
        })
        .unwrap_err();
        assert!(err.contains("variable 'EmpresaID'"), "{err}");
        assert!(
            err.contains("the domain has no stored type this engine maps"),
            "{err}"
        );
    }

    #[test]
    fn a_based_on_variable_that_declares_its_own_length_is_refused() {
        // SYNTHETIC: a variable based on Domain 119 that also stores a Length. Which length wins is
        // not evidenced, so the declaration is refused rather than typed from either one.
        let xml = document(&[decl(
            "Code",
            &[
                ("OBJ_TYPE", "id_OTYPE_VAR"),
                ("idBasedOn", "00972a17-9975-449e-aab1-d26165d51393-119"),
                ("Length", "18"),
            ],
        )]);
        let err = decode(&xml, &sdt_names, &domain_lookup, &|_| Ok(None)).unwrap_err();
        assert!(err.contains("variable 'Code'"), "{err}");
        assert!(
            err.contains("with its own length or decimals is not mapped"),
            "{err}"
        );
    }

    // The stored Attribute rows of LiveMoveAttribute (EntityType 27, Ids 27-29), read read-only from
    // GX_KB_KBTeste. Version 2 of the key carries `Class=PRIMARY`; the other rows carry no `Class`.
    const ATTRIBUTE_KEY_V1: &str = r#"<Properties><Property><Name>Name</Name><Value>LiveMoveAttributeId</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_ATT</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>4</Value></Property><Property><Name>Decimals</Name><Value>0</Value></Property><Property><Name>AttMaxLen</Name><Value>4</Value></Property><Property><Name>AttAvgLen</Name><Value>0</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"#;
    const ATTRIBUTE_KEY_V2_PRIMARY: &str = r#"<Properties><Property><Name>Name</Name><Value>LiveMoveAttributeId</Value></Property><Property><Name>Class</Name><Value>PRIMARY</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_ATT</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>4</Value></Property><Property><Name>Decimals</Name><Value>0</Value></Property><Property><Name>AttMaxLen</Name><Value>4</Value></Property><Property><Name>AttAvgLen</Name><Value>0</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"#;
    const ATTRIBUTE_CHARACTER_NO_LENGTH: &str = r#"<Properties><Property><Name>Name</Name><Value>LiveMoveAttributeCode</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_ATT</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;5&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"#;

    #[test]
    fn an_attribute_key_is_numeric_from_its_stored_length_in_every_version() {
        // The SDK's `ParDef` for LiveMoveAttribute gives the key `Int16(4,0)`.
        let numeric = DataType::Numeric {
            precision: 4,
            scale: 0,
        };
        assert_eq!(attribute_data_type(ATTRIBUTE_KEY_V1), Ok(numeric.clone()));
        assert_eq!(attribute_data_type(ATTRIBUTE_KEY_V2_PRIMARY), Ok(numeric));
    }

    #[test]
    fn an_attribute_character_with_no_stored_length_takes_the_sdk_length() {
        // The SDK gives `LiveMoveAttributeCode` `NChar(20)`; the row stores no Length, AttMaxLen or Decimals.
        assert_eq!(
            attribute_data_type(ATTRIBUTE_CHARACTER_NO_LENGTH),
            Ok(DataType::Character { length: 20 })
        );
    }

    #[test]
    fn an_attribute_row_with_no_stored_type_is_refused() {
        let untyped = ATTRIBUTE_CHARACTER_NO_LENGTH.replace(
            "<Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;5&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property>",
            "",
        );
        let err = attribute_data_type(&untyped).unwrap_err();
        assert!(err.contains("no ATTCUSTOMTYPE"), "{err}");
    }

    #[test]
    fn an_attribute_row_with_an_unknown_property_is_refused() {
        let unknown = ATTRIBUTE_KEY_V1.replace(
            "<Property><Name>IsDefault</Name>",
            "<Property><Name>Mystery</Name><Value>1</Value></Property><Property><Name>IsDefault</Name>",
        );
        assert!(attribute_data_type(&unknown).is_err());
    }

    // The stored primary-key version 3 of Plan070TrnId (KBTeste, EntityType 27, Id 11): its SuperTypeKey names
    // attribute 12, the key of Plan071TrnId.
    const ATTRIBUTE_SUPER_TYPE_KEY_V3: &str = r#"<Properties><Property><Name>Name</Name><Value>Plan070TrnId</Value></Property><Property><Name>Class</Name><Value>PRIMARY</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_ATT</Value></Property><Property><Name>SuperTypeKey</Name><Value>12</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"#;

    #[test]
    fn a_primary_key_super_type_key_is_read_with_the_row_that_stores_it() {
        // The stored v3 row stores no ATTCUSTOMTYPE: it decodes with no type of its own, and its SuperTypeKey
        // names attribute 12. Its type is inherited from that key (see the resolver tests below), never guessed.
        let untyped =
            attribute_row(ATTRIBUTE_SUPER_TYPE_KEY_V3).expect("the untyped key row decodes");
        assert_eq!(untyped.data_type, None);
        assert_eq!(untyped.super_type_key, Some(12));
        assert!(untyped.is_primary_key);

        let typed = ATTRIBUTE_SUPER_TYPE_KEY_V3.replace(
            "<Property><Name>IsDefault</Name>",
            "<Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>4</Value></Property><Property><Name>Decimals</Name><Value>0</Value></Property><Property><Name>IsDefault</Name>",
        );
        let row = attribute_row(&typed).expect("the typed row decodes");
        assert_eq!(row.super_type_key, Some(12));
        assert_eq!(
            row.data_type,
            Some(DataType::Numeric {
                precision: 4,
                scale: 0
            })
        );
    }

    #[test]
    fn a_super_type_key_on_a_row_that_is_not_a_primary_key_is_refused() {
        // Give the row its stored type first, so the only defect left is the missing Class=PRIMARY.
        let typed = ATTRIBUTE_SUPER_TYPE_KEY_V3.replace(
            "<Property><Name>IsDefault</Name>",
            "<Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>4</Value></Property><Property><Name>Decimals</Name><Value>0</Value></Property><Property><Name>IsDefault</Name>",
        );
        let not_primary = typed.replace(
            "<Property><Name>Class</Name><Value>PRIMARY</Value></Property>",
            "",
        );
        let err = attribute_row(&not_primary).err().expect("refused");
        assert!(err.contains("not a primary key"), "{err}");
    }

    #[test]
    fn a_super_type_key_that_is_not_a_positive_id_is_refused() {
        let bad = ATTRIBUTE_KEY_V2_PRIMARY.replace(
            "<Property><Name>IsDefault</Name>",
            "<Property><Name>SuperTypeKey</Name><Value>zero</Value></Property><Property><Name>IsDefault</Name>",
        );
        let err = attribute_row(&bad).err().expect("refused");
        assert!(err.contains("not a positive attribute id"), "{err}");
    }

    fn key_row(data_type: Option<DataType>, super_type_key: Option<i32>) -> AttributeRow {
        AttributeRow {
            data_type,
            super_type_key,
            is_primary_key: true,
        }
    }

    fn numeric_4_0() -> DataType {
        DataType::Numeric {
            precision: 4,
            scale: 0,
        }
    }

    /// Plan070TrnId (11, untyped, SuperTypeKey 12) inherits the stored type of Plan071TrnId (12), NUMERIC(4,0).
    #[test]
    fn an_untyped_key_inherits_the_type_of_the_primary_key_it_references() {
        let start = attribute_row(ATTRIBUTE_SUPER_TYPE_KEY_V3).expect("decodes");
        let fetch = |id: i32| -> Result<AttributeRow, String> {
            match id {
                12 => Ok(key_row(Some(numeric_4_0()), None)),
                other => Err(format!("no row for attribute {other}")),
            }
        };
        assert_eq!(
            resolve_attribute_type(11, &start, &fetch),
            Ok(numeric_4_0())
        );
    }

    /// A chain of untyped keys is followed one link at a time, and the first stored type ends it.
    #[test]
    fn an_inherited_type_follows_untyped_links_and_stops_at_the_first_stored_type() {
        let start = key_row(None, Some(12));
        let fetch = |id: i32| -> Result<AttributeRow, String> {
            match id {
                12 => Ok(key_row(None, Some(13))),
                13 => Ok(key_row(Some(DataType::Character { length: 6 }), None)),
                other => Err(format!("no row for attribute {other}")),
            }
        };
        assert_eq!(
            resolve_attribute_type(11, &start, &fetch),
            Ok(DataType::Character { length: 6 })
        );
    }

    /// A missing referenced row refuses the type; no other version of attribute 12 is read instead.
    #[test]
    fn a_missing_referenced_key_refuses_the_type_naming_it() {
        let start = attribute_row(ATTRIBUTE_SUPER_TYPE_KEY_V3).expect("decodes");
        let fetch = |id: i32| -> Result<AttributeRow, String> {
            Err(format!("its Attribute object {id} is not in the catalog"))
        };
        let err = resolve_attribute_type(11, &start, &fetch).unwrap_err();
        assert!(
            err.contains("references attribute 12, which cannot be read"),
            "{err}"
        );
    }

    #[test]
    fn a_referenced_row_that_is_not_a_primary_key_refuses_the_type() {
        let start = attribute_row(ATTRIBUTE_SUPER_TYPE_KEY_V3).expect("decodes");
        let fetch = |_: i32| -> Result<AttributeRow, String> {
            Ok(AttributeRow {
                data_type: Some(numeric_4_0()),
                super_type_key: None,
                is_primary_key: false,
            })
        };
        let err = resolve_attribute_type(11, &start, &fetch).unwrap_err();
        assert!(err.contains("which is not a primary key"), "{err}");
    }

    /// Control for the shape of attributes 39 and 40 (no type, no link): the type is refused, never defaulted.
    #[test]
    fn an_untyped_key_with_no_link_is_refused_and_never_defaulted() {
        let stored = key_row(None, None);
        let fetch =
            |id: i32| -> Result<AttributeRow, String> { Err(format!("no row for attribute {id}")) };
        let err = resolve_attribute_type(39, &stored, &fetch).unwrap_err();
        assert!(err.contains("no ATTCUSTOMTYPE"), "{err}");
    }

    #[test]
    fn a_row_that_stores_its_own_type_and_a_link_is_refused_as_not_evidenced() {
        let both = key_row(Some(numeric_4_0()), Some(12));
        let fetch =
            |id: i32| -> Result<AttributeRow, String> { Err(format!("no row for attribute {id}")) };
        let err = resolve_attribute_type(11, &both, &fetch).unwrap_err();
        assert!(err.contains("not evidenced"), "{err}");
    }

    /// 11 -> 12 -> 11 is a cycle: the chain is refused, not followed around.
    #[test]
    fn a_cyclic_key_chain_is_refused() {
        let start = key_row(None, Some(12));
        let fetch = |id: i32| -> Result<AttributeRow, String> {
            match id {
                12 => Ok(key_row(None, Some(11))),
                other => Err(format!("no row for attribute {other}")),
            }
        };
        let err = resolve_attribute_type(11, &start, &fetch).unwrap_err();
        assert!(err.contains("cyclic"), "{err}");
    }

    #[test]
    fn a_chain_that_ends_without_a_type_is_refused_not_defaulted() {
        let start = key_row(None, Some(12));
        let fetch = |id: i32| -> Result<AttributeRow, String> {
            match id {
                12 => Ok(key_row(None, None)),
                other => Err(format!("no row for attribute {other}")),
            }
        };
        let err = resolve_attribute_type(11, &start, &fetch).unwrap_err();
        assert!(
            err.contains("attribute 12") && err.contains("inherited type is not stored"),
            "{err}"
        );
    }
}
