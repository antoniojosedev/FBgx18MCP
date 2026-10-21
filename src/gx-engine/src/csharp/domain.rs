//! The values class the SDK writes for each enumerated Domain of the model (`gxdomain<name>.cs`).
//!
//! The shape is the SDK's file for KBTeste's `General.Page` (a `Numeric(4)` enumeration): a static table
//! from value to description, the sorted values, and the reverse map from name to value. Only the shape
//! that evidence covers is written: a numeric key of length 4 or less with no decimals (`short`). Any other
//! stored type is refused by name, never written in a guessed shape.

use crate::catalog::KbObject;
use crate::udm::reader::DOMAIN_ENUM_PART;
use crate::udm::stored_variables::named_property;

use super::types::module_namespace;

/// The usings of the SDK's values class (KBTeste `general/gxdomainpage.cs`), in its order.
const DOMAIN_IMPORTS: [&str; 16] = [
    "System",
    "System.Collections",
    "GeneXus.Utils",
    "GeneXus.Resources",
    "GeneXus.Application",
    "GeneXus.Metadata",
    "GeneXus.Cryptography",
    "GeneXus.Data.ADO",
    "GeneXus.Data.NTier",
    "GeneXus.Data.NTier.ADO",
    "GeneXus.XML",
    "GeneXus.Search",
    "GeneXus.Encryption",
    "GeneXus.Http.Client",
    "System.Xml.Serialization",
    "System.Runtime.Serialization",
];

/// One stored enumeration value.
#[derive(Debug, Clone, PartialEq, Eq)]
struct EnumValue {
    name: String,
    description: String,
    value: String,
}

/// The file name and text of an enumerated Domain's values class, or `None` when the object holds no
/// enumeration part. An enumeration whose stored type this emitter does not cover is an `Err` naming it.
pub fn enum_domain_file(object: &KbObject) -> Option<Result<(String, String), String>> {
    let part = object
        .source_parts
        .iter()
        .find(|part| part.name == DOMAIN_ENUM_PART)?;
    Some(
        emit(object, &part.source)
            .map_err(|error| format!("enumerated Domain '{}': {error}", object.name)),
    )
}

fn emit(object: &KbObject, properties: &str) -> Result<(String, String), String> {
    if named_property(properties, "ATTCUSTOMTYPE")?.is_some() {
        return Err("a stored ATTCUSTOMTYPE is not a shape the SDK evidence covers".into());
    }
    let decimals = named_property(properties, "Decimals")?.unwrap_or_else(|| "0".into());
    if decimals.trim() != "0" {
        return Err(format!(
            "Decimals {decimals} is not a shape the SDK evidence covers"
        ));
    }
    let length = named_property(properties, "Length")?
        .ok_or("the row stores no Length")?
        .trim()
        .parse::<u32>()
        .map_err(|_| "Length is not a whole number".to_string())?;
    if !(1..=4).contains(&length) {
        return Err(format!(
            "Numeric({length}) is not a shape the SDK evidence covers (only short keys)"
        ));
    }
    let raw = named_property(properties, "IDEnumDefinedValues")?
        .ok_or("the row stores no IDEnumDefinedValues")?;
    let values = enum_values(&raw)?;
    if values.is_empty() {
        return Err("the enumeration has no values".into());
    }
    for value in &values {
        value
            .value
            .trim()
            .parse::<i16>()
            .map_err(|_| format!("value '{}' is not a short", value.value))?;
    }

    let class = format!("gxdomain{}", object.name.to_ascii_lowercase());
    let mut out = String::new();
    for import in DOMAIN_IMPORTS {
        out.push_str(&format!("using {import};\n"));
    }
    out.push_str(&format!(
        "namespace {} {{\n",
        module_namespace(&object.module_path)
    ));
    out.push_str(&format!("   public class {class}\n   {{\n"));
    out.push_str("      private static Hashtable domain = new Hashtable();\n");
    out.push_str("      private static Hashtable domainMap;\n");
    out.push_str(&format!("      static {class} ()\n      {{\n"));
    for value in &values {
        out.push_str(&format!(
            "         domain[(short){}] = {};\n",
            value.value.trim(),
            text(&value.description)?
        ));
    }
    out.push_str("      }\n\n");
    out.push_str(
        "      public static string getDescription( IGxContext context ,\n                                           short key )\n      {\n         string value;\n         value = (string)(domain[key]==null?\"\":domain[key]);\n         return value ;\n      }\n\n",
    );
    out.push_str(
        "      public static GxSimpleCollection<short> getValues( )\n      {\n         GxSimpleCollection<short> value = new GxSimpleCollection<short>();\n         ArrayList aKeys = new ArrayList(domain.Keys);\n         aKeys.Sort();\n         foreach (short key in aKeys)\n         {\n            value.Add(key);\n         }\n         return value;\n      }\n\n",
    );
    out.push_str(
        "      public static short getValue( string key )\n      {\n         if(domainMap == null)\n         {\n            domainMap = new Hashtable();\n",
    );
    for value in &values {
        out.push_str(&format!(
            "            domainMap[{}] = (short){};\n",
            text(&value.name)?,
            value.value.trim()
        ));
    }
    out.push_str("         }\n         return (short)domainMap[key] ;\n      }\n\n   }\n\n}\n");
    Ok((format!("{class}.cs"), out))
}

/// A C# string literal for a stored name or description. Only printable ASCII is covered.
fn text(value: &str) -> Result<String, String> {
    if !value.chars().all(|c| c.is_ascii() && !c.is_ascii_control()) {
        return Err(format!(
            "'{value}' holds a character the SDK evidence does not cover"
        ));
    }
    Ok(format!(
        "\"{}\"",
        value.replace('\\', "\\\\").replace('"', "\\\"")
    ))
}

/// The stored values of `<EnumValues><Values><EnumValue>...`, in stored order.
fn enum_values(raw: &str) -> Result<Vec<EnumValue>, String> {
    let document = roxmltree::Document::parse(raw)
        .map_err(|e| format!("IDEnumDefinedValues is not XML: {e}"))?;
    let root = document.root_element();
    if root.tag_name().name() != "EnumValues" {
        return Err(format!(
            "IDEnumDefinedValues root is <{}>, expected <EnumValues>",
            root.tag_name().name()
        ));
    }
    let mut out = Vec::new();
    for values in root.children().filter(|n| n.is_element()) {
        if values.tag_name().name() != "Values" {
            return Err(format!(
                "unsupported element <{}>",
                values.tag_name().name()
            ));
        }
        for entry in values.children().filter(|n| n.is_element()) {
            if entry.tag_name().name() != "EnumValue" {
                return Err(format!("unsupported element <{}>", entry.tag_name().name()));
            }
            let mut name = None;
            let mut description = None;
            let mut value = None;
            for field in entry.children().filter(|n| n.is_element()) {
                let slot = match field.tag_name().name() {
                    "Name" => &mut name,
                    "Description" => &mut description,
                    "Value" => &mut value,
                    other => return Err(format!("unsupported enumeration field <{other}>")),
                };
                if slot
                    .replace(field.text().unwrap_or("").to_string())
                    .is_some()
                {
                    return Err(format!("repeated <{}>", field.tag_name().name()));
                }
            }
            out.push(EnumValue {
                name: name.ok_or("an enumeration value has no Name")?,
                description: description.ok_or("an enumeration value has no Description")?,
                value: value.ok_or("an enumeration value has no Value")?,
            });
        }
    }
    Ok(out)
}
