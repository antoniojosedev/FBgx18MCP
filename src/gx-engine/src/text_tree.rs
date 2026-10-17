//! Text Tree Export & Import Engine for gx-engine (Issue #499).
//!
//! Enables exporting and importing an entire GeneXus Knowledge Base catalog
//! as clean text files in a structured tree (e.g. `.prc`, `.trn`, `.sdt`, `.dom`,
//! `.att`, `.dp`, `.ds`, `.dso`). This provides the file-system tree required
//! for static code indexing by GxWire without needing an SDK or live database.

use crate::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use crate::model::DataType;
use crate::transaction::{TransactionAttributeRef, TransactionLevel, TransactionStructure};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};
use std::fs;
use std::io;
use std::path::Path;

/// A single exported/imported text file in the Knowledge Base tree.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TextTreeFile {
    pub path: String,
    pub content: String,
    pub kind: ObjectKind,
}

impl TextTreeFile {
    pub fn new(path: impl Into<String>, content: impl Into<String>, kind: ObjectKind) -> Self {
        Self {
            path: path.into(),
            content: content.into(),
            kind,
        }
    }
}

/// A snapshot containing all files in a text tree.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct TextTreeSnapshot {
    pub files: Vec<TextTreeFile>,
}

impl TextTreeSnapshot {
    pub fn new(files: Vec<TextTreeFile>) -> Self {
        Self { files }
    }

    pub fn empty() -> Self {
        Self { files: Vec::new() }
    }

    pub fn get(&self, path: &str) -> Option<&TextTreeFile> {
        self.files
            .iter()
            .find(|f| f.path.eq_ignore_ascii_case(path))
    }

    pub fn find_by_name(&self, name: &str) -> Option<&TextTreeFile> {
        self.files.iter().find(|f| {
            Path::new(&f.path)
                .file_stem()
                .and_then(|s| s.to_str())
                .is_some_and(|stem| stem.eq_ignore_ascii_case(name))
        })
    }
}

/// Returns the conventional relative path for a catalog object.
pub fn path_for_object(name: &str, kind: ObjectKind) -> String {
    match kind {
        ObjectKind::Procedure => format!("Objects/Procedures/{name}.prc"),
        ObjectKind::Transaction => format!("Objects/Transactions/{name}.trn"),
        ObjectKind::StructuredDataType => format!("Definitions/SDTs/{name}.sdt"),
        ObjectKind::Domain => format!("Definitions/Domains/{name}.dom"),
        ObjectKind::Attribute => format!("Definitions/Attributes/{name}.att"),
        ObjectKind::DataProvider => format!("Objects/DataProviders/{name}.dp"),
        ObjectKind::DataSelector => format!("Objects/DataSelectors/{name}.ds"),
        ObjectKind::DataView => format!("Objects/DataViews/{name}.dv"),
        ObjectKind::DesignSystem => format!("Objects/DesignSystems/{name}.dso"),
        ObjectKind::Module => format!("Definitions/Modules/{name}.mod"),
        ObjectKind::Table => format!("Definitions/Tables/{name}.tbl"),
        ObjectKind::WebPanel => format!("Objects/WebPanels/{name}.wp"),
    }
}

/// Infers `ObjectKind` from a file path or extension.
pub fn infer_kind_from_path(path: &str) -> Result<ObjectKind, String> {
    let p = Path::new(path);
    let ext = p
        .extension()
        .and_then(|s| s.to_str())
        .ok_or_else(|| format!("file path '{path}' has no file extension"))?;
    match ext.to_ascii_lowercase().as_str() {
        "prc" => Ok(ObjectKind::Procedure),
        "trn" => Ok(ObjectKind::Transaction),
        "sdt" => Ok(ObjectKind::StructuredDataType),
        "dom" => Ok(ObjectKind::Domain),
        "att" => Ok(ObjectKind::Attribute),
        "dp" => Ok(ObjectKind::DataProvider),
        "ds" => Ok(ObjectKind::DataSelector),
        "dv" => Ok(ObjectKind::DataView),
        "dso" => Ok(ObjectKind::DesignSystem),
        "mod" => Ok(ObjectKind::Module),
        "tbl" => Ok(ObjectKind::Table),
        "wp" => Ok(ObjectKind::WebPanel),
        other => Err(format!(
            "unrecognized or corrupted file extension '.{other}' in path '{path}'"
        )),
    }
}

/// Extracts the object name from a file path.
pub fn extract_object_name_from_path(path: &str) -> Result<String, String> {
    let p = Path::new(path);
    let stem = p
        .file_stem()
        .and_then(|s| s.to_str())
        .ok_or_else(|| format!("cannot extract object name from path '{path}'"))?;
    if stem.is_empty() {
        return Err(format!("empty object name in path '{path}'"));
    }
    Ok(stem.to_string())
}

/// Formats a `DataType` into a canonical human-readable GeneXus representation.
pub fn format_data_type(dt: &DataType) -> String {
    match dt {
        DataType::Numeric { precision, scale } => {
            if *scale > 0 {
                format!("Numeric({precision}, {scale})")
            } else {
                format!("Numeric({precision}, 0)")
            }
        }
        DataType::Character { length } => format!("Character({length})"),
        DataType::Varchar { length } => format!("Varchar({length})"),
        DataType::Boolean {} => "Boolean".to_string(),
        DataType::Date {} => "Date".to_string(),
        DataType::DateTime {} => "DateTime".to_string(),
        DataType::Record { schema } => format!("Record({schema})"),
        DataType::Collection { schema } => format!("Collection({schema})"),
    }
}

/// Parses a human-readable GeneXus data type string or JSON data type.
pub fn parse_data_type(input: &str) -> Result<DataType, String> {
    let trimmed = input.trim().trim_end_matches(';').trim();
    let lower = trimmed.to_ascii_lowercase();

    if lower == "boolean" || lower == "bool" {
        return Ok(DataType::Boolean {});
    }
    if lower == "date" {
        return Ok(DataType::Date {});
    }
    if lower == "datetime" || lower == "date_time" {
        return Ok(DataType::DateTime {});
    }

    if let Some(rest) = lower
        .strip_prefix("numeric")
        .or_else(|| lower.strip_prefix("num"))
    {
        let args = extract_parenthesized(rest)?;
        let parts: Vec<&str> = args.split(',').map(|s| s.trim()).collect();
        match parts.len() {
            1 => {
                let precision = parts[0]
                    .parse::<u8>()
                    .map_err(|e| format!("invalid numeric precision: {e}"))?;
                return Ok(DataType::Numeric {
                    precision,
                    scale: 0,
                });
            }
            2 => {
                let precision = parts[0]
                    .parse::<u8>()
                    .map_err(|e| format!("invalid numeric precision: {e}"))?;
                let scale = parts[1]
                    .parse::<u8>()
                    .map_err(|e| format!("invalid numeric scale: {e}"))?;
                return Ok(DataType::Numeric { precision, scale });
            }
            _ => return Err("numeric type requires (precision) or (precision, scale)".into()),
        }
    }

    if let Some(rest) = lower
        .strip_prefix("character")
        .or_else(|| lower.strip_prefix("char"))
    {
        let args = extract_parenthesized(rest)?;
        let length = args
            .trim()
            .parse::<u32>()
            .map_err(|e| format!("invalid character length: {e}"))?;
        return Ok(DataType::Character { length });
    }

    if let Some(rest) = lower.strip_prefix("varchar") {
        let args = extract_parenthesized(rest)?;
        let length = args
            .trim()
            .parse::<u32>()
            .map_err(|e| format!("invalid varchar length: {e}"))?;
        return Ok(DataType::Varchar { length });
    }

    if let Some(rest) = lower.strip_prefix("record") {
        let schema = extract_parenthesized(rest)?;
        return Ok(DataType::Record {
            schema: schema.trim().to_string(),
        });
    }

    if let Some(rest) = lower.strip_prefix("collection") {
        let schema = extract_parenthesized(rest)?;
        return Ok(DataType::Collection {
            schema: schema.trim().to_string(),
        });
    }

    if trimmed.starts_with('{')
        && let Ok(dt) = serde_json::from_str::<DataType>(trimmed)
    {
        return Ok(dt);
    }

    Err(format!("unrecognized data type: '{input}'"))
}

fn extract_parenthesized(s: &str) -> Result<&str, String> {
    let s = s.trim();
    if s.starts_with('(') && s.ends_with(')') && s.len() >= 2 {
        Ok(&s[1..s.len() - 1])
    } else {
        Err(format!("expected parenthesized arguments in '{s}'"))
    }
}

/// Splits text content by section headers `=== Name ===`.
pub fn parse_sections(content: &str) -> Vec<(String, String)> {
    let mut sections = Vec::new();
    let mut current_name: Option<String> = None;
    let mut current_lines: Vec<&str> = Vec::new();

    for line in content.lines() {
        let trimmed = line.trim();
        if trimmed.starts_with("===") && trimmed.ends_with("===") && trimmed.len() > 6 {
            let name = trimmed.trim_matches('=').trim().to_ascii_uppercase();
            if let Some(prev_name) = current_name.take() {
                let section_text = current_lines.join("\n").trim().to_string();
                sections.push((prev_name, section_text));
                current_lines.clear();
            }
            current_name = Some(name);
        } else {
            current_lines.push(line);
        }
    }

    if let Some(prev_name) = current_name {
        let section_text = current_lines.join("\n").trim().to_string();
        sections.push((prev_name, section_text));
    } else if !current_lines.is_empty() {
        let text = current_lines.join("\n").trim().to_string();
        if !text.is_empty() {
            sections.push(("SOURCE".to_string(), text));
        }
    }

    sections
}

fn format_level(level: &TransactionLevel, indent_level: usize, out: &mut String) {
    let indent = "  ".repeat(indent_level);
    out.push_str(&format!("{indent}{}\n", level.name));
    out.push_str(&format!("{indent}{{\n"));

    for attr in &level.attributes {
        let key_prefix = if attr.is_key { "*" } else { " " };
        let mut line = format!("{indent}  {key_prefix}{}", attr.name);
        if let Some(dt) = &attr.data_type {
            line.push_str(&format!(" : {}", format_data_type(dt)));
        }
        if let Some(formula) = &attr.formula {
            line.push_str(&format!(" = {formula}"));
        }
        if attr.is_foreign_key {
            line.push_str(" [FK]");
        }
        out.push_str(&line);
        out.push('\n');
    }

    for sublevel in &level.levels {
        format_level(sublevel, indent_level + 1, out);
    }

    out.push_str(&format!("{indent}}}\n"));
}

/// Formats a `TransactionStructure` into clean, human-readable text.
pub fn format_transaction_structure(structure: &TransactionStructure) -> String {
    let mut out = String::new();
    format_level(&structure.root, 0, &mut out);
    out
}

/// Parses a `TransactionStructure` from clean text or JSON.
pub fn parse_transaction_structure(input: &str) -> Result<TransactionStructure, String> {
    let trimmed = input.trim();
    if trimmed.starts_with('{')
        && let Ok(structure) = serde_json::from_str::<TransactionStructure>(trimmed)
    {
        structure.validate()?;
        return Ok(structure);
    }

    // Filter out comments and blank lines
    let lines: Vec<&str> = input
        .lines()
        .map(|l| {
            let l = l.trim();
            if let Some((code, _)) = l.split_once("//") {
                code.trim()
            } else {
                l
            }
        })
        .filter(|l| !l.is_empty())
        .collect();

    if lines.is_empty() {
        return Err("empty transaction structure".into());
    }

    let (root_level, _) = parse_transaction_level_block(&lines, 0)?;
    let structure = TransactionStructure::new(root_level);
    structure.validate()?;
    Ok(structure)
}

fn parse_transaction_level_block(
    lines: &[&str],
    mut index: usize,
) -> Result<(TransactionLevel, usize), String> {
    if index >= lines.len() {
        return Err("unexpected end of transaction structure".into());
    }

    let first_line = lines[index];
    let level_name = parse_level_name(first_line)?;
    let mut level = TransactionLevel::new(&level_name);
    index += 1;

    if !first_line.contains('{') {
        if index < lines.len() && lines[index] == "{" {
            index += 1;
        } else {
            return Err(format!(
                "expected '{{' after level name '{level_name}', got '{:?}'",
                lines.get(index)
            ));
        }
    }

    while index < lines.len() {
        let line = lines[index];
        if line == "}" {
            index += 1;
            return Ok((level, index));
        }

        let is_sublevel = if line.contains('{') && !line.contains(':') && !line.starts_with('*') {
            true
        } else {
            index + 1 < lines.len()
                && lines[index + 1] == "{"
                && !line.contains(':')
                && !line.contains('=')
                && !line.starts_with('*')
        };

        if is_sublevel {
            let (sublevel, next_index) = parse_transaction_level_block(lines, index)?;
            level.levels.push(sublevel);
            index = next_index;
        } else {
            let attr = parse_attribute_ref(line)?;
            level.attributes.push(attr);
            index += 1;
        }
    }

    Err(format!(
        "unclosed level block for level '{}' (missing '}}')",
        level.name
    ))
}

fn parse_level_name(line: &str) -> Result<String, String> {
    let mut s = line.trim();
    if let Some(rest) = s
        .strip_prefix("Level ")
        .or_else(|| s.strip_prefix("level "))
    {
        s = rest.trim();
    }
    s = s.trim_end_matches('{').trim();
    if s.is_empty() {
        return Err(format!("invalid level name header in line: '{line}'"));
    }
    Ok(s.to_string())
}

fn parse_attribute_ref(line: &str) -> Result<TransactionAttributeRef, String> {
    let mut s = line.trim();
    if let Some(stripped) = s.strip_suffix(';') {
        s = stripped.trim();
    }
    let is_key = s.starts_with('*');
    if is_key {
        s = s[1..].trim();
    }

    let is_foreign_key = s.to_ascii_uppercase().contains("[FK]");
    let clean_s = if is_foreign_key {
        s.replace("[FK]", "").replace("[fk]", "")
    } else {
        s.to_string()
    };
    let s = clean_s.trim();

    let (before_formula, formula) = if let Some((left, right)) = s.split_once('=') {
        (left.trim(), Some(right.trim().to_string()))
    } else {
        (s, None)
    };

    let (name, data_type) = if let Some((left, right)) = before_formula.split_once(':') {
        let dt = parse_data_type(right.trim())?;
        (left.trim().to_string(), Some(dt))
    } else {
        (before_formula.trim().to_string(), None)
    };

    if name.is_empty() {
        return Err(format!("empty attribute name in line: '{line}'"));
    }

    Ok(TransactionAttributeRef {
        name,
        is_key,
        data_type,
        formula,
        is_nullable: false,
        is_foreign_key,
    })
}

/// Formats an SDT schema into clean text.
pub fn format_sdt(name: &str, fields_json: &str) -> String {
    let mut out = format!("SDT {name}\n{{\n");
    if let Ok(fields) =
        serde_json::from_str::<BTreeMap<String, crate::kb::RawCatalogType>>(fields_json)
    {
        for (field_name, raw_type) in fields {
            match raw_type {
                crate::kb::RawCatalogType::Concrete(dt) => {
                    out.push_str(&format!("  {field_name} : {}\n", format_data_type(&dt)));
                }
                crate::kb::RawCatalogType::BasedOnShort { target }
                | crate::kb::RawCatalogType::BasedOnTagged { target, .. } => {
                    out.push_str(&format!("  {field_name} : BasedOn({target})\n"));
                }
            }
        }
    } else if let Ok(fields) = serde_json::from_str::<BTreeMap<String, DataType>>(fields_json) {
        for (field_name, dt) in fields {
            out.push_str(&format!("  {field_name} : {}\n", format_data_type(&dt)));
        }
    } else {
        out.push_str(fields_json.trim());
        out.push('\n');
    }
    out.push_str("}\n");
    out
}

/// Parses an SDT schema from clean text or JSON into a JSON fields string and references.
pub fn parse_sdt_fields(content: &str) -> Result<(String, Vec<(ReferenceKind, String)>), String> {
    let trimmed = content.trim();
    if trimmed.starts_with('{')
        && let Ok(fields) =
            serde_json::from_str::<BTreeMap<String, crate::kb::RawCatalogType>>(trimmed)
    {
        let mut refs = Vec::new();
        for raw_type in fields.values() {
            match raw_type {
                crate::kb::RawCatalogType::BasedOnShort { target }
                | crate::kb::RawCatalogType::BasedOnTagged { target, .. } => {
                    refs.push((ReferenceKind::BasedOn, target.clone()));
                }
                crate::kb::RawCatalogType::Concrete(DataType::Record { schema })
                | crate::kb::RawCatalogType::Concrete(DataType::Collection { schema }) => {
                    refs.push((ReferenceKind::Uses, schema.clone()));
                }
                _ => {}
            }
        }
        let json = serde_json::to_string(&fields).map_err(|e| e.to_string())?;
        return Ok((json, refs));
    }

    let mut fields: BTreeMap<String, crate::kb::RawCatalogType> = BTreeMap::new();
    let mut refs = Vec::new();

    for line in content.lines() {
        let mut line = line.trim();
        if let Some((code, _)) = line.split_once("//") {
            line = code.trim();
        }
        if line.is_empty()
            || line.starts_with("SDT ")
            || line.starts_with("sdt ")
            || line == "{"
            || line == "}"
        {
            continue;
        }

        if let Some((field_name, type_str)) = line.split_once(':') {
            let field_name = field_name.trim();
            let type_str = type_str.trim();
            let lower_type = type_str.to_ascii_lowercase();

            if let Some(rest) = lower_type
                .strip_prefix("basedon")
                .or_else(|| lower_type.strip_prefix("based_on"))
            {
                let target = extract_parenthesized(rest)?.trim().to_string();
                refs.push((ReferenceKind::BasedOn, target.clone()));
                fields.insert(
                    field_name.to_string(),
                    crate::kb::RawCatalogType::BasedOnShort { target },
                );
            } else if let Some(rest) = lower_type.strip_prefix("uses") {
                let target = extract_parenthesized(rest)?.trim().to_string();
                refs.push((ReferenceKind::Uses, target.clone()));
                fields.insert(
                    field_name.to_string(),
                    crate::kb::RawCatalogType::Concrete(DataType::Record { schema: target }),
                );
            } else {
                let dt = parse_data_type(type_str)?;
                if let DataType::Record { schema } | DataType::Collection { schema } = &dt {
                    refs.push((ReferenceKind::Uses, schema.clone()));
                }
                fields.insert(
                    field_name.to_string(),
                    crate::kb::RawCatalogType::Concrete(dt),
                );
            }
        }
    }

    let json = serde_json::to_string(&fields).map_err(|e| e.to_string())?;
    Ok((json, refs))
}

/// Exports a single catalog object to clean text.
pub fn export_object_to_text(obj: &KbObject) -> Result<String, String> {
    match obj.kind {
        ObjectKind::Procedure => {
            let mut out = String::new();
            let mut sections = Vec::new();

            if let Some(rules) = obj.source_parts.iter().find(|p| p.name == "RULES")
                && !rules.source.trim().is_empty()
            {
                sections.push(("Rules", rules.source.trim()));
            }
            if let Some(vars) = obj.source_parts.iter().find(|p| p.name == "VARIABLES")
                && !vars.source.trim().is_empty()
            {
                sections.push(("Variables", vars.source.trim()));
            }
            if let Some(src) = obj.source_parts.iter().find(|p| p.name == "SOURCE")
                && !src.source.trim().is_empty()
            {
                sections.push(("Source", src.source.trim()));
            }

            for part in &obj.source_parts {
                if !matches!(part.name.as_str(), "RULES" | "VARIABLES" | "SOURCE")
                    && !part.source.trim().is_empty()
                {
                    sections.push((part.name.as_str(), part.source.trim()));
                }
            }

            for (i, (name, content)) in sections.iter().enumerate() {
                if i > 0 {
                    out.push('\n');
                }
                out.push_str(&format!("=== {name} ===\n"));
                out.push_str(content);
                out.push('\n');
            }

            Ok(out)
        }
        ObjectKind::Transaction => {
            let structure_part = obj
                .source_parts
                .iter()
                .find(|p| p.name == "STRUCTURE")
                .ok_or_else(|| format!("Transaction '{}' is missing STRUCTURE part", obj.name))?;

            let structure: TransactionStructure = serde_json::from_str(&structure_part.source)
                .map_err(|e| {
                    format!("invalid STRUCTURE part in Transaction '{}': {e}", obj.name)
                })?;

            let struct_text = format_transaction_structure(&structure);
            let rules_part = obj.source_parts.iter().find(|p| p.name == "RULES");

            if let Some(rules) = rules_part
                && !rules.source.trim().is_empty()
            {
                let mut out = String::new();
                out.push_str("=== Structure ===\n");
                out.push_str(&struct_text);
                out.push_str("\n=== Rules ===\n");
                out.push_str(rules.source.trim());
                out.push('\n');
                return Ok(out);
            }

            Ok(struct_text)
        }
        ObjectKind::StructuredDataType => {
            let fields_part = obj
                .source_parts
                .iter()
                .find(|p| p.name == "FIELDS")
                .ok_or_else(|| format!("SDT '{}' is missing FIELDS part", obj.name))?;

            Ok(format_sdt(&obj.name, &fields_part.source))
        }
        ObjectKind::Domain | ObjectKind::Attribute => {
            let type_part = obj
                .source_parts
                .iter()
                .find(|p| p.name == "TYPE")
                .ok_or_else(|| format!("{:?} '{}' is missing TYPE part", obj.kind, obj.name))?;

            if let Ok(raw_type) =
                serde_json::from_str::<crate::kb::RawCatalogType>(&type_part.source)
            {
                match raw_type {
                    crate::kb::RawCatalogType::Concrete(dt) => {
                        Ok(format!("Type: {}\n", format_data_type(&dt)))
                    }
                    crate::kb::RawCatalogType::BasedOnShort { target }
                    | crate::kb::RawCatalogType::BasedOnTagged { target, .. } => {
                        Ok(format!("BasedOn: {target}\n"))
                    }
                }
            } else if let Ok(dt) = serde_json::from_str::<DataType>(&type_part.source) {
                Ok(format!("Type: {}\n", format_data_type(&dt)))
            } else {
                Ok(format!("Type: {}\n", type_part.source.trim()))
            }
        }
        ObjectKind::DataProvider | ObjectKind::DesignSystem => {
            let source_part = obj
                .source_parts
                .iter()
                .find(|p| p.name == "SOURCE")
                .ok_or_else(|| format!("{:?} '{}' is missing SOURCE part", obj.kind, obj.name))?;
            Ok(source_part.source.clone())
        }
        ObjectKind::DataSelector => {
            let rules_part = obj.source_parts.iter().find(|p| p.name == "RULES");
            let conds_part = obj.source_parts.iter().find(|p| p.name == "CONDITIONS");
            let source_part = obj.source_parts.iter().find(|p| p.name == "SOURCE");

            if let (Some(r), Some(c)) = (rules_part, conds_part) {
                let mut out = String::new();
                out.push_str("=== Rules ===\n");
                out.push_str(r.source.trim());
                out.push_str("\n\n=== Conditions ===\n");
                out.push_str(c.source.trim());
                out.push('\n');
                Ok(out)
            } else if let Some(s) = source_part {
                Ok(s.source.clone())
            } else {
                Err(format!(
                    "DataSelector '{}' is missing source parts",
                    obj.name
                ))
            }
        }
        ObjectKind::Module | ObjectKind::Table | ObjectKind::WebPanel | ObjectKind::DataView => {
            if let Some(src) = obj.source_parts.first() {
                Ok(src.source.clone())
            } else {
                Ok(String::new())
            }
        }
    }
}

/// Converts an entire catalog into a `TextTreeSnapshot`.
pub fn export_catalog_to_text_tree(catalog: &Catalog) -> TextTreeSnapshot {
    let mut files = Vec::new();
    for obj in catalog.objects() {
        let path = path_for_object(&obj.name, obj.kind);
        if let Ok(content) = export_object_to_text(obj) {
            files.push(TextTreeFile::new(path, content, obj.kind));
        }
    }
    files.sort_by(|a, b| a.path.cmp(&b.path));
    TextTreeSnapshot::new(files)
}

/// Reconstructs a valid, fully typed `Catalog` from a `TextTreeSnapshot`.
pub fn import_text_tree_to_catalog(snapshot: &TextTreeSnapshot) -> Result<Catalog, String> {
    let mut parsed_objects = Vec::new();

    // 1. Parse each file into a KbObject candidate
    for file in &snapshot.files {
        let inferred_kind = infer_kind_from_path(&file.path)?;
        if file.kind != inferred_kind {
            return Err(format!(
                "file kind mismatch for '{}': declared {:?} but path indicates {:?}",
                file.path, file.kind, inferred_kind
            ));
        }

        let name = extract_object_name_from_path(&file.path)?;
        let id_val = name.to_ascii_lowercase();
        let id = ObjectId::new(id_val)?;
        let mut obj = KbObject::new(id, name, inferred_kind);

        match inferred_kind {
            ObjectKind::Procedure => {
                let sections = parse_sections(&file.content);
                for (sec_name, sec_content) in sections {
                    obj.source_parts.push(SourcePart {
                        name: sec_name,
                        source: sec_content,
                    });
                }
            }
            ObjectKind::Transaction => {
                let sections = parse_sections(&file.content);
                let struct_content = sections
                    .iter()
                    .find(|(n, _)| n == "STRUCTURE")
                    .map(|(_, c)| c.as_str())
                    .unwrap_or(&file.content);

                let structure = parse_transaction_structure(struct_content)?;
                let struct_json = serde_json::to_string(&structure).map_err(|e| e.to_string())?;
                obj.source_parts.push(SourcePart {
                    name: "STRUCTURE".into(),
                    source: struct_json,
                });

                if let Some((_, rules_content)) = sections.iter().find(|(n, _)| n == "RULES") {
                    obj.source_parts.push(SourcePart {
                        name: "RULES".into(),
                        source: rules_content.clone(),
                    });
                }
            }
            ObjectKind::StructuredDataType => {
                let (fields_json, refs) = parse_sdt_fields(&file.content)?;
                obj.source_parts.push(SourcePart {
                    name: "FIELDS".into(),
                    source: fields_json,
                });
                for (ref_kind, target) in refs {
                    if let Ok(target_id) = ObjectId::new(target.to_ascii_lowercase()) {
                        obj.references.push(Reference {
                            kind: ref_kind,
                            target: target_id,
                        });
                    }
                }
            }
            ObjectKind::Domain | ObjectKind::Attribute => {
                let trimmed = file.content.trim();
                let mut found_type = false;

                for line in trimmed.lines() {
                    let mut line = line.trim();
                    if let Some((code, _)) = line.split_once("//") {
                        line = code.trim();
                    }
                    if line.is_empty() {
                        continue;
                    }

                    let lower = line.to_ascii_lowercase();
                    if let Some(target) = lower
                        .strip_prefix("basedon:")
                        .or_else(|| lower.strip_prefix("based_on:"))
                    {
                        let target = target.trim();
                        obj.source_parts.push(SourcePart {
                            name: "TYPE".into(),
                            source: format!(r#"{{"based_on":"{target}"}}"#),
                        });
                        if let Ok(target_id) = ObjectId::new(target.to_ascii_lowercase()) {
                            obj.references.push(Reference {
                                kind: ReferenceKind::BasedOn,
                                target: target_id,
                            });
                        }
                        found_type = true;
                        break;
                    } else if let Some(type_str) = lower.strip_prefix("type:") {
                        let dt = parse_data_type(type_str.trim())?;
                        let dt_json = serde_json::to_string(&dt).map_err(|e| e.to_string())?;
                        obj.source_parts.push(SourcePart {
                            name: "TYPE".into(),
                            source: dt_json,
                        });
                        found_type = true;
                        break;
                    }
                }

                if !found_type {
                    if trimmed.starts_with('{') {
                        obj.source_parts.push(SourcePart {
                            name: "TYPE".into(),
                            source: trimmed.to_string(),
                        });
                        if let Ok(crate::kb::RawCatalogType::BasedOnShort { target }) =
                            serde_json::from_str(trimmed)
                            && let Ok(target_id) = ObjectId::new(target.to_ascii_lowercase())
                        {
                            obj.references.push(Reference {
                                kind: ReferenceKind::BasedOn,
                                target: target_id,
                            });
                        }
                    } else if let Ok(dt) = parse_data_type(trimmed) {
                        let dt_json = serde_json::to_string(&dt).map_err(|e| e.to_string())?;
                        obj.source_parts.push(SourcePart {
                            name: "TYPE".into(),
                            source: dt_json,
                        });
                    } else {
                        return Err(format!(
                            "cannot parse type definition for {:?} '{}': '{}'",
                            inferred_kind, obj.name, trimmed
                        ));
                    }
                }
            }
            ObjectKind::DataProvider => {
                obj.source_parts.push(SourcePart {
                    name: "SOURCE".into(),
                    source: file.content.clone(),
                });

                // Extract output SDT if parsed
                if let Ok(dp) = crate::dataprovider::DataProvider::parse(&obj.name, &file.content)
                    && let Some(sdt) = dp.output_sdt
                    && let Ok(sdt_id) = ObjectId::new(sdt.to_ascii_lowercase())
                {
                    obj.references.push(Reference {
                        kind: ReferenceKind::Uses,
                        target: sdt_id,
                    });
                }
            }
            ObjectKind::DataSelector => {
                let sections = parse_sections(&file.content);
                if sections.is_empty() {
                    obj.source_parts.push(SourcePart {
                        name: "SOURCE".into(),
                        source: file.content.clone(),
                    });
                } else {
                    for (name, content) in sections {
                        obj.source_parts.push(SourcePart {
                            name,
                            source: content,
                        });
                    }
                }
            }
            ObjectKind::DesignSystem => {
                obj.source_parts.push(SourcePart {
                    name: "SOURCE".into(),
                    source: file.content.clone(),
                });

                if let Ok(dso) = crate::design_system::parse(&obj.name, &file.content) {
                    for imp in dso.imports {
                        if let Ok(imp_id) = ObjectId::new(imp.to_ascii_lowercase()) {
                            obj.references.push(Reference {
                                kind: ReferenceKind::Uses,
                                target: imp_id,
                            });
                        }
                    }
                }
            }
            ObjectKind::Module
            | ObjectKind::Table
            | ObjectKind::WebPanel
            | ObjectKind::DataView => {
                obj.source_parts.push(SourcePart {
                    name: "SOURCE".into(),
                    source: file.content.clone(),
                });
            }
        }

        parsed_objects.push(obj);
    }

    // 2. Filter references to only keep targets existing in the imported set
    let available_ids: HashSet<ObjectId> = parsed_objects.iter().map(|o| o.id.clone()).collect();
    for obj in &mut parsed_objects {
        obj.references
            .retain(|reference| available_ids.contains(&reference.target));
    }

    // 3. Build and validate catalog
    let mut catalog = Catalog::empty();
    if parsed_objects.is_empty() {
        return Ok(catalog);
    }

    let edits: Vec<Edit> = parsed_objects.into_iter().map(Edit::Upsert).collect();
    catalog.apply(0, &edits)?;
    Ok(catalog)
}

/// Writes all files in a `TextTreeSnapshot` to disk under `root_dir`.
pub fn write_to_directory(snapshot: &TextTreeSnapshot, root_dir: &Path) -> Result<(), io::Error> {
    for file in &snapshot.files {
        let file_path = root_dir.join(&file.path);
        if let Some(parent) = file_path.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::write(&file_path, &file.content)?;
    }
    Ok(())
}

/// Recursively walks `root_dir` and reads all recognized GeneXus text files into a snapshot.
pub fn read_from_directory(root_dir: &Path) -> Result<TextTreeSnapshot, io::Error> {
    let mut files = Vec::new();
    walk_dir_recursive(root_dir, root_dir, &mut files)?;
    files.sort_by(|a, b| a.path.cmp(&b.path));
    Ok(TextTreeSnapshot::new(files))
}

fn walk_dir_recursive(
    current_dir: &Path,
    root_dir: &Path,
    files: &mut Vec<TextTreeFile>,
) -> Result<(), io::Error> {
    for entry in fs::read_dir(current_dir)? {
        let entry = entry?;
        let path = entry.path();
        if path.is_dir() {
            walk_dir_recursive(&path, root_dir, files)?;
        } else if path.is_file()
            && let Some(ext) = path.extension().and_then(|s| s.to_str())
        {
            let ext_lower = ext.to_ascii_lowercase();
            let recognized = matches!(
                ext_lower.as_str(),
                "prc" | "trn" | "sdt" | "dom" | "att" | "dp" | "ds" | "dso" | "mod" | "tbl" | "wp"
            );
            if recognized && let Ok(rel_path) = path.strip_prefix(root_dir) {
                let rel_path_str = rel_path.to_string_lossy().replace('\\', "/");
                if let Ok(kind) = infer_kind_from_path(&rel_path_str) {
                    let content = fs::read_to_string(&path)?;
                    files.push(TextTreeFile::new(rel_path_str, content, kind));
                }
            }
        }
    }
    Ok(())
}
