//! C# type mapping and identifier normalization for GeneXus constructs.

use crate::model::{DataType, ParamMode};

/// Standard imports matching official GeneXus IDE generated code.
pub const STANDARD_IMPORTS: &[&str] = &[
    "System",
    "System.Collections",
    "GeneXus.Utils",
    "GeneXus.Resources",
    "GeneXus.Application",
    "GeneXus.Metadata",
    "GeneXus.Cryptography",
    "com.genexus",
    "GeneXus.Data.ADO",
    "GeneXus.Data.NTier",
    "GeneXus.Data.NTier.ADO",
    "GeneXus.WebControls",
    "GeneXus.Http",
    "GeneXus.Procedure",
    "GeneXus.XML",
    "GeneXus.Search",
    "GeneXus.Encryption",
    "GeneXus.Http.Client",
    "System.Threading",
    "System.Xml.Serialization",
    "System.Runtime.Serialization",
];

/// Extended standard imports for Transaction and WebPanel C# classes.
pub const EXTENDED_DATA_IMPORTS: &[&str] = &[
    "System",
    "System.Collections",
    "GeneXus.Utils",
    "GeneXus.Resources",
    "GeneXus.Application",
    "GeneXus.Metadata",
    "GeneXus.Cryptography",
    "System.Data",
    "GeneXus.Data",
    "com.genexus",
    "GeneXus.Data.ADO",
    "GeneXus.Data.NTier",
    "GeneXus.Data.NTier.ADO",
    "GeneXus.WebControls",
    "GeneXus.Http",
    "GeneXus.XML",
    "GeneXus.Search",
    "GeneXus.Encryption",
    "GeneXus.Http.Client",
    "System.Xml.Serialization",
    "System.Runtime.Serialization",
];

/// Converts a GeneXus `DataType` to its corresponding C# type name.
/// The C# namespace of an object held by `module_path` (see `KbObject::module_path`):
/// `GeneXus.Programs` under the root module, else `GeneXus.Programs.<module>...` in lower case, as the
/// SDK writes it (`GeneXus.Programs.general.ui` for `General.UI`).
pub fn module_namespace(module_path: &[String]) -> String {
    let mut namespace = ROOT_NAMESPACE.to_string();
    for module in module_path {
        namespace.push('.');
        namespace.push_str(&module.to_ascii_lowercase());
    }
    namespace
}

/// The namespace of every class held by the root module.
pub const ROOT_NAMESPACE: &str = "GeneXus.Programs";

pub fn to_csharp_type(data_type: &DataType) -> String {
    match data_type {
        DataType::Boolean {} => "bool".to_string(),
        DataType::Character { .. } | DataType::Varchar { .. } | DataType::LongVarchar { .. } => {
            "string".to_string()
        }
        // BITMAP is a `string` in the SDK (an image path or URL; the image helpers take strings).
        DataType::Image {} => "string".to_string(),
        DataType::Guid {} => "Guid".to_string(),
        DataType::Geospatial {} => "Geospatial".to_string(),
        DataType::Date {} | DataType::DateTime {} => "DateTime".to_string(),
        DataType::Numeric { precision, scale } => {
            if *scale > 0 {
                "decimal".to_string()
            } else if *precision <= 4 {
                "short".to_string()
            } else if *precision <= 9 {
                "int".to_string()
            } else if *precision <= 18 {
                "long".to_string()
            } else {
                "decimal".to_string()
            }
        }
        DataType::Record { schema } => {
            if schema.starts_with("Sdt") {
                schema.clone()
            } else {
                format!("Sdt{schema}")
            }
        }
        DataType::Collection { schema } => {
            let item_type = if schema.starts_with("Sdt") {
                schema.clone()
            } else {
                format!("Sdt{schema}")
            };
            format!("GxSimpleCollection<{item_type}>")
        }
        // The SDK's collection of an SDT whose root is a collection (listprograms.cs: `GXBaseCollection<
        // GeneXus.Programs.general.ui.SdtProgramNames_ProgramName>`). The item class is `<SDT>_<item>`.
        DataType::BaseCollection { schema, .. } => {
            let item_type = if schema.starts_with("Sdt") {
                schema.clone()
            } else {
                format!("Sdt{schema}")
            };
            format!("GXBaseCollection<{item_type}>")
        }
    }
}

/// Returns the C# default literal or expression for a given `DataType`.
pub fn to_csharp_default_value(data_type: &DataType) -> String {
    match data_type {
        DataType::Boolean {} => "false".to_string(),
        DataType::Numeric { scale, .. } => {
            if *scale > 0 {
                "0m".to_string()
            } else {
                "0".to_string()
            }
        }
        DataType::Character { .. } | DataType::Varchar { .. } | DataType::LongVarchar { .. } => {
            "\"\"".to_string()
        }
        // The SDK's empty image is `""` (an SDT image member is initialised with `""`, SDTs.stg).
        DataType::Image {} => "\"\"".to_string(),
        // The SDK's empty GUID (Properties.stg and Rest.stg `guid` defaults).
        DataType::Guid {} => "Guid.Empty".to_string(),
        // The SDK initialises an empty geospatial member with `new Geospatial("")` (SDTs.stg).
        DataType::Geospatial {} => "new Geospatial(\"\")".to_string(),
        DataType::Date {} | DataType::DateTime {} => "DateTime.MinValue".to_string(),
        DataType::Record { .. } | DataType::Collection { .. } | DataType::BaseCollection { .. } => {
            "null".to_string()
        }
    }
}

/// Normalizes a variable name to GeneXus C# variable convention.
/// In GeneXus C#, variables are typically named `AV<id><name>` or `AV_<name>`.
/// If the name starts with `&`, it is trimmed.
/// If it already starts with `AV` or `aP` or `gx`, it is preserved.
pub fn to_csharp_var_name(name: &str) -> String {
    let trimmed = name.trim_start_matches('&');
    if trimmed.starts_with("AV") || trimmed.starts_with("aP") || trimmed.starts_with("gx") {
        trimmed.to_string()
    } else {
        format!("AV_{trimmed}")
    }
}

/// Normalizes an attribute name to GeneXus C# attribute convention (e.g. `A<Id><Name>` or `A_<Name>`).
pub fn to_csharp_attr_name(name: &str) -> String {
    let trimmed = name.trim_start_matches('&');
    if trimmed.starts_with('A')
        && trimmed.len() > 1
        && trimmed
            .chars()
            .nth(1)
            .is_some_and(|c| c.is_ascii_uppercase() || c.is_ascii_digit())
    {
        trimmed.to_string()
    } else {
        format!("A_{trimmed}")
    }
}

/// Strips prefixes to get a clean parameter name suffix (e.g. `Hits` from `&Hits` or `AV8Hits`).
pub fn clean_ident(name: &str) -> String {
    let trimmed = name.trim_start_matches('&');
    if let Some(rest) = trimmed.strip_prefix("AV_") {
        return rest.to_string();
    }
    if let Some(rest) = trimmed.strip_prefix("AV") {
        let without_digits = rest.trim_start_matches(|c: char| c.is_ascii_digit());
        if !without_digits.is_empty() {
            return without_digits.to_string();
        }
    }
    trimmed.to_string()
}

/// Generates parameter variable name such as `aP0_Hits`.
pub fn to_csharp_param_name(index: usize, name: &str) -> String {
    let clean = clean_ident(name);
    format!("aP{index}_{clean}")
}

/// Generates the C# parameter signature fragment, e.g. `ref decimal aP0_Hits` or `out decimal aP1_Result`.
pub fn to_csharp_param_signature(
    mode: ParamMode,
    data_type: &DataType,
    param_name: &str,
) -> String {
    let cs_type = to_csharp_type(data_type);
    match mode {
        ParamMode::In => format!("{cs_type} {param_name}"),
        ParamMode::Out => format!("out {cs_type} {param_name}"),
        ParamMode::InOut => format!("ref {cs_type} {param_name}"),
    }
}
