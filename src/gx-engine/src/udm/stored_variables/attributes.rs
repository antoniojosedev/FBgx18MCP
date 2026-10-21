//! Stored Attribute rows: their own type, or the type inherited through a `SuperTypeKey` chain.

use super::properties::properties_of_block;
use super::type_codes::{attcustomtype, decode_type};
use super::*;

/// The stored properties an Attribute object's row may carry. These are the names the evidenced rows
/// carry (KBTeste, GX18: `LiveMoveAttributeId` in versions 1 and 2, `LiveMoveAttributeCode`,
/// `LiveMoveAttributeDescription`): `Class` appears only on a primary-key version. A name outside the list
/// refuses the row.
pub(super) const ATTRIBUTE_ROW_PROPERTIES: &[&str] = &[
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
