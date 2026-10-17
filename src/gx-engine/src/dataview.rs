//! DataView engine for external table mapping (Row H4).
//!
//! Provides data structures, DSL grammar parser, validation, and SQL query
//! generation for GeneXus Data Views mapping internal attributes to external
//! database tables, views, and columns.

use serde::{Deserialize, Serialize};
use std::collections::HashSet;

use crate::model::DataType;
use crate::table::SqlDialect;

/// Represents a field mapping from an internal GeneXus attribute to an external database column.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DataViewField {
    pub attribute_name: String,
    pub external_name: String,
    pub data_type: DataType,
}

/// Represents an external index definition in a DataView.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct DataViewIndex {
    pub name: String,
    #[serde(alias = "external")]
    pub external_name: String,
    #[serde(default, alias = "primary")]
    pub is_primary: bool,
    #[serde(default, alias = "unique")]
    pub is_unique: bool,
    #[serde(default)]
    pub keys: Vec<String>,
}

/// Represents a GeneXus DataView object mapping a GeneXus table to an external table or view.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct DataView {
    pub name: String,
    #[serde(alias = "table")]
    pub associated_table: String,
    #[serde(alias = "external", alias = "table_name")]
    pub external_name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub platform: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub datastore: Option<String>,
    #[serde(default)]
    pub fields: Vec<DataViewField>,
    #[serde(default)]
    pub indexes: Vec<DataViewIndex>,
}

impl DataView {
    /// Parses a DataView definition from either clean DSL or JSON text.
    pub fn parse(name: &str, source: &str) -> Result<Self, String> {
        let clean = strip_comments(source);
        let trimmed = clean.trim();

        if trimmed.is_empty() {
            return Err("DataView source cannot be empty".to_string());
        }

        // Support JSON deserialization if source starts with '{'
        if trimmed.starts_with('{') {
            let mut dv: DataView = serde_json::from_str(trimmed)
                .map_err(|e| format!("Failed to parse DataView JSON: {e}"))?;
            if dv.name.is_empty() && !name.is_empty() {
                dv.name = name.to_string();
            }
            dv.validate()?;
            return Ok(dv);
        }

        // DSL parsing: dataview <Name> { ... }
        let open_idx = trimmed
            .find('{')
            .ok_or_else(|| "DataView definition missing opening '{'".to_string())?;
        let close_idx = find_matching_brace(trimmed, open_idx)
            .ok_or_else(|| "Unmatched '{' in DataView definition".to_string())?;

        let header = trimmed[..open_idx].trim();
        let dsl_name = if let Some(rest) = header
            .strip_prefix("dataview")
            .or_else(|| header.strip_prefix("DATAVIEW"))
            .or_else(|| header.strip_prefix("DataView"))
        {
            rest.trim()
        } else {
            header
        };

        let mut dv = DataView::default();
        if !dsl_name.is_empty() {
            dv.name = dsl_name.to_string();
        } else if !name.is_empty() {
            dv.name = name.to_string();
        } else {
            dv.name = "DataView".to_string();
        }

        let inner = &trimmed[open_idx + 1..close_idx];
        parse_inner_dsl(inner, &mut dv)?;

        dv.validate()?;
        Ok(dv)
    }

    /// Maps an internal GeneXus attribute name to its external physical column name.
    pub fn map_attribute_to_column(&self, attribute_name: &str) -> Option<&str> {
        self.fields
            .iter()
            .find(|f| f.attribute_name == attribute_name)
            .or_else(|| {
                self.fields
                    .iter()
                    .find(|f| f.attribute_name.eq_ignore_ascii_case(attribute_name))
            })
            .map(|f| f.external_name.as_str())
    }

    /// Maps an external physical column name to its internal GeneXus attribute name.
    pub fn map_column_to_attribute(&self, column_name: &str) -> Option<&str> {
        self.fields
            .iter()
            .find(|f| f.external_name == column_name)
            .or_else(|| {
                self.fields
                    .iter()
                    .find(|f| f.external_name.eq_ignore_ascii_case(column_name))
            })
            .map(|f| f.attribute_name.as_str())
    }

    /// Returns a reference to the primary index if one is defined.
    pub fn primary_index(&self) -> Option<&DataViewIndex> {
        self.indexes.iter().find(|idx| idx.is_primary)
    }

    /// Renders an external physical SQL SELECT query for the specified internal attributes.
    ///
    /// Fails closed if any requested attribute is not mapped in the DataView or if no attributes
    /// are selected.
    pub fn render_external_select_query(
        &self,
        dialect: SqlDialect,
        selected_attributes: &[String],
        where_clause: Option<&str>,
    ) -> Result<String, String> {
        if selected_attributes.is_empty() {
            return Err(format!(
                "DataView '{}': at least one attribute must be selected for query",
                self.name
            ));
        }

        let mut columns = Vec::with_capacity(selected_attributes.len());
        for attr in selected_attributes {
            let col = self.map_attribute_to_column(attr).ok_or_else(|| {
                format!(
                    "Attribute '{}' is not mapped in DataView '{}'",
                    attr, self.name
                )
            })?;
            columns.push(quote_identifier(dialect, col));
        }

        let table = quote_identifier(dialect, &self.external_name);
        let mut query = format!("SELECT {} FROM {}", columns.join(", "), table);

        if let Some(w) = where_clause {
            let trimmed = w.trim();
            if !trimmed.is_empty() {
                query.push_str(" WHERE ");
                query.push_str(trimmed);
            }
        }

        Ok(query)
    }

    /// Validates the internal consistency and rules of the DataView.
    ///
    /// Fails closed if:
    /// - DataView name, associated table, or external table name is empty.
    /// - Duplicate attribute mappings exist.
    /// - Duplicate external column names exist.
    /// - Multiple primary indexes are defined.
    /// - Any index key references an attribute not mapped in `fields`.
    pub fn validate(&self) -> Result<(), String> {
        if self.name.trim().is_empty() {
            return Err("DataView name cannot be empty".to_string());
        }

        if self.associated_table.trim().is_empty() {
            return Err(format!(
                "DataView '{}': associated table cannot be empty",
                self.name
            ));
        }

        if self.external_name.trim().is_empty() {
            return Err(format!(
                "DataView '{}': external table name cannot be empty",
                self.name
            ));
        }

        let mut seen_attributes = HashSet::new();
        let mut seen_columns = HashSet::new();

        for field in &self.fields {
            let upper_attr = field.attribute_name.to_ascii_uppercase();
            if !seen_attributes.insert(upper_attr) {
                return Err(format!(
                    "DataView '{}': duplicate attribute mapping for '{}'",
                    self.name, field.attribute_name
                ));
            }

            let upper_col = field.external_name.to_ascii_uppercase();
            if !seen_columns.insert(upper_col) {
                return Err(format!(
                    "DataView '{}': duplicate external column mapping for '{}'",
                    self.name, field.external_name
                ));
            }
        }

        let primary_count = self.indexes.iter().filter(|i| i.is_primary).count();
        if primary_count > 1 {
            return Err(format!(
                "DataView '{}': cannot have multiple primary indexes (found {})",
                self.name, primary_count
            ));
        }

        for index in &self.indexes {
            for key in &index.keys {
                if !self
                    .fields
                    .iter()
                    .any(|f| f.attribute_name.eq_ignore_ascii_case(key))
                {
                    return Err(format!(
                        "DataView '{}': index '{}' references unmapped attribute '{}'",
                        self.name, index.name, key
                    ));
                }
            }
        }

        Ok(())
    }
}

/// Strips `//` single-line and `/* */` block comments from source text while preserving string literals.
pub fn strip_comments(source: &str) -> String {
    let mut result = String::with_capacity(source.len());
    let chars: Vec<char> = source.chars().collect();
    let len = chars.len();
    let mut i = 0;
    let mut in_single_quote = false;
    let mut in_double_quote = false;
    let mut escape = false;

    while i < len {
        let c = chars[i];
        let next = if i + 1 < len {
            Some(chars[i + 1])
        } else {
            None
        };

        if in_single_quote {
            result.push(c);
            if escape {
                escape = false;
            } else if c == '\\' {
                escape = true;
            } else if c == '\'' {
                in_single_quote = false;
            }
            i += 1;
            continue;
        }

        if in_double_quote {
            result.push(c);
            if escape {
                escape = false;
            } else if c == '\\' {
                escape = true;
            } else if c == '"' {
                in_double_quote = false;
            }
            i += 1;
            continue;
        }

        if c == '\'' {
            in_single_quote = true;
            result.push(c);
            i += 1;
            continue;
        }

        if c == '"' {
            in_double_quote = true;
            result.push(c);
            i += 1;
            continue;
        }

        if c == '/' && next == Some('/') {
            i += 2;
            while i < len && chars[i] != '\n' {
                i += 1;
            }
            if i < len && chars[i] == '\n' {
                result.push('\n');
                i += 1;
            }
            continue;
        }

        if c == '/' && next == Some('*') {
            i += 2;
            while i + 1 < len && !(chars[i] == '*' && chars[i + 1] == '/') {
                if chars[i] == '\n' {
                    result.push('\n');
                }
                i += 1;
            }
            if i + 1 < len {
                i += 2;
            }
            continue;
        }

        result.push(c);
        i += 1;
    }

    result
}

fn find_matching_brace(input: &str, open_idx: usize) -> Option<usize> {
    let mut depth = 0;
    for (i, c) in input[open_idx..].char_indices() {
        if c == '{' {
            depth += 1;
        } else if c == '}' {
            depth -= 1;
            if depth == 0 {
                return Some(open_idx + i);
            }
        }
    }
    None
}

fn parse_inner_dsl(inner: &str, dv: &mut DataView) -> Result<(), String> {
    let chars: Vec<char> = inner.chars().collect();
    let len = chars.len();
    let mut i = 0;
    let mut buffer = String::new();

    while i < len {
        let c = chars[i];

        if c == '{' {
            let block_header = buffer.trim().to_string();
            buffer.clear();
            let body_start = i + 1;
            let mut depth = 1usize;
            i += 1;
            while i < len && depth > 0 {
                if chars[i] == '{' {
                    depth += 1;
                } else if chars[i] == '}' {
                    depth -= 1;
                }
                if depth == 0 {
                    break;
                }
                i += 1;
            }
            if depth != 0 {
                return Err("Unmatched '{' in nested block".to_string());
            }
            let body_end = i;
            let body: String = chars[body_start..body_end].iter().collect();
            i += 1;

            if block_header.eq_ignore_ascii_case("fields") {
                parse_fields_block(&body, dv)?;
            } else if block_header.to_ascii_lowercase().starts_with("index") {
                let index_header = block_header[5..].trim();
                parse_index_block(index_header, &body, dv)?;
            } else {
                return Err(format!("Unrecognized block '{block_header}' in DataView"));
            }
            continue;
        }

        if c == ';' {
            let stmt = buffer.trim().to_string();
            buffer.clear();
            if !stmt.is_empty() {
                parse_top_level_statement(&stmt, dv)?;
            }
            i += 1;
            continue;
        }

        buffer.push(c);
        i += 1;
    }

    let remaining = buffer.trim();
    if !remaining.is_empty() {
        if remaining.contains('=') {
            parse_top_level_statement(remaining, dv)?;
        } else {
            return Err(format!(
                "Unexpected trailing token '{remaining}' in DataView"
            ));
        }
    }

    Ok(())
}

fn parse_top_level_statement(stmt: &str, dv: &mut DataView) -> Result<(), String> {
    let parts: Vec<&str> = stmt.splitn(2, '=').collect();
    if parts.len() != 2 {
        return Err(format!(
            "Invalid statement '{stmt}': expected 'key = value;'"
        ));
    }
    let key = parts[0].trim().to_ascii_lowercase();
    let val = parts[1].trim().trim_matches('"').trim_matches('\'').trim();

    match key.as_str() {
        "table" | "associated_table" => {
            dv.associated_table = val.to_string();
        }
        "external_name" | "external" | "table_name" => {
            dv.external_name = val.to_string();
        }
        "platform" => {
            dv.platform = Some(val.to_string());
        }
        "datastore" => {
            dv.datastore = Some(val.to_string());
        }
        _ => {
            return Err(format!("Unknown property '{key}' in DataView"));
        }
    }
    Ok(())
}

fn parse_fields_block(body: &str, dv: &mut DataView) -> Result<(), String> {
    for raw_line in body.split(';') {
        let line = raw_line.trim();
        if line.is_empty() {
            continue;
        }
        for item in line.lines() {
            let item = item.trim();
            if item.is_empty() {
                continue;
            }
            let arrow_parts: Vec<&str> = item.split("=>").collect();
            if arrow_parts.len() != 2 {
                return Err(format!(
                    "Invalid field mapping in DataView: expected '<attribute> => <column> : <type>', found '{item}'"
                ));
            }
            let attr_name = arrow_parts[0]
                .trim()
                .trim_matches('"')
                .trim_matches('\'')
                .trim();
            let right = arrow_parts[1].trim();

            let col_parts: Vec<&str> = right.splitn(2, ':').collect();
            if col_parts.len() != 2 {
                return Err(format!(
                    "Invalid column mapping in DataView: expected '<column> : <type>', found '{right}'"
                ));
            }
            let ext_col = col_parts[0]
                .trim()
                .trim_matches('"')
                .trim_matches('\'')
                .trim();
            let type_str = col_parts[1].trim();
            let data_type = crate::text_tree::parse_data_type(type_str)?;

            dv.fields.push(DataViewField {
                attribute_name: attr_name.to_string(),
                external_name: ext_col.to_string(),
                data_type,
            });
        }
    }
    Ok(())
}

fn parse_index_block(index_name: &str, body: &str, dv: &mut DataView) -> Result<(), String> {
    let name = index_name
        .trim()
        .trim_matches('"')
        .trim_matches('\'')
        .trim();
    if name.is_empty() {
        return Err("Index definition missing index name".to_string());
    }

    let mut index = DataViewIndex {
        name: name.to_string(),
        external_name: String::new(),
        is_primary: false,
        is_unique: false,
        keys: Vec::new(),
    };

    for raw_part in body.split(';') {
        let trimmed_part = raw_part.trim();
        if trimmed_part.is_empty() {
            continue;
        }
        for item in trimmed_part.lines() {
            let item = item.trim();
            if item.is_empty() {
                continue;
            }
            if item.contains('=') {
                let kv: Vec<&str> = item.splitn(2, '=').collect();
                if kv.len() != 2 {
                    return Err(format!("Invalid index property in '{item}'"));
                }
                let k = kv[0].trim().to_ascii_lowercase();
                let v = kv[1].trim().trim_matches('"').trim_matches('\'').trim();
                match k.as_str() {
                    "external" | "external_name" => {
                        index.external_name = v.to_string();
                    }
                    "primary" | "is_primary" => {
                        let is_prim = v.eq_ignore_ascii_case("true") || v == "1";
                        index.is_primary = is_prim;
                        if is_prim {
                            index.is_unique = true;
                        }
                    }
                    "unique" | "is_unique" => {
                        let is_uniq = v.eq_ignore_ascii_case("true") || v == "1";
                        index.is_unique = is_uniq;
                    }
                    _ => {
                        return Err(format!("Unknown index property '{k}'"));
                    }
                }
            } else {
                for key in item.split(',') {
                    let key = key.trim().trim_matches('"').trim_matches('\'').trim();
                    if !key.is_empty() {
                        index.keys.push(key.to_string());
                    }
                }
            }
        }
    }

    if index.external_name.is_empty() {
        index.external_name = index.name.clone();
    }

    dv.indexes.push(index);
    Ok(())
}

fn quote_identifier(dialect: SqlDialect, ident: &str) -> String {
    match dialect {
        SqlDialect::SqlServer => format!("[{ident}]"),
        SqlDialect::PostgreSql | SqlDialect::Oracle => format!("\"{ident}\""),
        SqlDialect::Ansi => ident.to_string(),
    }
}
