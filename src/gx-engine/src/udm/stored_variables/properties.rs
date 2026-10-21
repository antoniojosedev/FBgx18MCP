//! Stored name/value property rows: the allowlists of declarations, SDT nodes and Domains, and the readers that apply them.

use super::type_codes::{attcustomtype, decode_type};
use super::*;

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
pub(super) const SDT_NODE_PROPERTIES: &[&str] = &[
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
pub(super) const DOMAIN_ROW_PROPERTIES: &[&str] = &[
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
pub(super) fn domain_property_map(raw: &str) -> Result<BTreeMap<String, String>, String> {
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
pub(super) fn properties(node: Node<'_, '_>) -> Result<BTreeMap<String, String>, String> {
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
pub(super) fn properties_of_block(
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
