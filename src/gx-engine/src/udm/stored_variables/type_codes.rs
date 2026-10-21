//! Stored type codes: the code map shared by the VARIABLES and SDT structure decoders, and the `ATTCUSTOMTYPE` fragment readers.

use super::*;

/// Largest NUMERIC precision the stored declarations use (NUMERIC(18,x) is the evidence maximum).
const MAX_NUMERIC_PRECISION: u32 = 18;

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
pub(super) fn decode_type_in(
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

pub(super) fn number(props: &BTreeMap<String, String>, key: &str) -> Result<Option<u32>, String> {
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
