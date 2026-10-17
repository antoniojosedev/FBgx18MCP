//! DataProvider AST, declarative parser, JSON evaluation engine, and SDT schema validation.
//!
//! GeneXus DataProviders are declarative objects producing hierarchical data structures (SDTs).
//! Supports `parm(...)` rule parsing, nested groups, collection groups, group properties,
//! comment stripping, parameter substitution, and catalog SDT validation.

use crate::catalog::{Catalog, ObjectKind};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

/// A property declared on a DataProvider group (e.g. `[Count = 10]`, `[Paging = 20]`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DpProperty {
    pub name: String,
    pub value: String,
}

impl DpProperty {
    pub fn new(name: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            value: value.into(),
        }
    }
}

/// An item within a DataProvider group.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum DpItem {
    Assignment { target: String, value: String },
    Group(DpGroup),
}

/// A declared group in a DataProvider hierarchy.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DpGroup {
    pub name: String,
    pub is_collection: bool,
    pub properties: Vec<DpProperty>,
    pub items: Vec<DpItem>,
}

impl DpGroup {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            is_collection: false,
            properties: Vec::new(),
            items: Vec::new(),
        }
    }

    /// Evaluates this group hierarchy against the given parameters, returning a JSON value.
    pub fn evaluate_to_json(
        &self,
        params: &HashMap<String, serde_json::Value>,
    ) -> Result<serde_json::Value, String> {
        if self.is_collection {
            let mut array = Vec::new();
            let mut has_child_groups = false;

            for item in &self.items {
                match item {
                    DpItem::Group(child_group) => {
                        has_child_groups = true;
                        let child_val = child_group.evaluate_to_json(params)?;
                        array.push(child_val);
                    }
                    DpItem::Assignment { .. } => {}
                }
            }

            // If there are no child groups but direct assignments in a collection group,
            // evaluate them into a single record in the collection.
            if !has_child_groups && !self.items.is_empty() {
                let mut map = serde_json::Map::new();
                for item in &self.items {
                    if let DpItem::Assignment { target, value } = item {
                        let eval_val = evaluate_expression(value, params)?;
                        map.insert(target.clone(), eval_val);
                    }
                }
                array.push(serde_json::Value::Object(map));
            }

            Ok(serde_json::Value::Array(array))
        } else {
            let mut map = serde_json::Map::new();

            for item in &self.items {
                match item {
                    DpItem::Assignment { target, value } => {
                        let eval_val = evaluate_expression(value, params)?;
                        map.insert(target.clone(), eval_val);
                    }
                    DpItem::Group(child_group) => {
                        let child_val = child_group.evaluate_to_json(params)?;
                        let key = child_group.name.clone();

                        if let Some(existing) = map.get_mut(&key) {
                            if let serde_json::Value::Array(arr) = existing {
                                arr.push(child_val);
                            } else {
                                let prev = existing.take();
                                *existing = serde_json::Value::Array(vec![prev, child_val]);
                            }
                        } else {
                            map.insert(key, child_val);
                        }
                    }
                }
            }

            Ok(serde_json::Value::Object(map))
        }
    }
}

/// A parsed GeneXus DataProvider definition.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DataProvider {
    pub name: String,
    pub parameters: Vec<String>,
    pub output_sdt: Option<String>,
    pub root_group: DpGroup,
}

impl DataProvider {
    /// Parses a DataProvider definition from source text.
    pub fn parse(name: &str, source: &str) -> Result<Self, String> {
        let cleaned = strip_comments(source)?;
        let mut cursor = ParserCursor::new(&cleaned);

        let mut parameters = Vec::new();
        let mut output_sdt = None;

        // Parse any top-level rules (like `parm(...)`) before groups
        while cursor.has_more() {
            cursor.skip_whitespace_and_semicolons();
            if cursor.peek_keyword_ignore_case("parm") {
                cursor.consume_identifier()?;
                cursor.skip_whitespace();
                if cursor.consume_if('(') {
                    let parm_args = cursor.read_balanced_until(')')?;
                    cursor.consume_if(')');
                    cursor.skip_whitespace();
                    cursor.consume_if(';');

                    parse_parm_arguments(&parm_args, &mut parameters, &mut output_sdt)?;
                    continue;
                }
            } else if cursor.has_more() && !cursor.is_group_start() {
                // Skip other non-group rule statements ending with ';'
                cursor.skip_until_semicolon();
                continue;
            }
            break;
        }

        cursor.skip_whitespace_and_semicolons();
        if !cursor.has_more() {
            return Err("DataProvider source contains no groups".into());
        }

        // Parse top-level group(s)
        let mut top_groups = Vec::new();
        while cursor.has_more() {
            cursor.skip_whitespace_and_semicolons();
            if !cursor.has_more() {
                break;
            }
            let group = parse_group(&mut cursor)?;
            top_groups.push(group);
        }

        let root_group = match top_groups.len() {
            0 => return Err("DataProvider source contains no groups".into()),
            1 => top_groups.remove(0),
            _ => {
                // Multiple top-level groups of the same name represent a top-level collection
                let first_name = top_groups[0].name.clone();
                let mut collection_group = DpGroup::new(first_name);
                collection_group.is_collection = true;
                collection_group.items = top_groups.into_iter().map(DpItem::Group).collect();
                collection_group
            }
        };

        Ok(DataProvider {
            name: name.to_string(),
            parameters,
            output_sdt,
            root_group,
        })
    }

    /// Evaluates the DataProvider hierarchy against given parameter values.
    pub fn evaluate_to_json(
        &self,
        params: &HashMap<String, serde_json::Value>,
    ) -> Result<serde_json::Value, String> {
        self.root_group.evaluate_to_json(params)
    }

    /// Validates that target assignment fields exist in the SDT schema if present in the catalog.
    pub fn validate_against_sdt(&self, catalog: &Catalog, sdt_name: &str) -> Result<(), String> {
        // Look up SDT in catalog
        let sdt_obj = catalog.objects().find(|o| {
            o.kind == ObjectKind::StructuredDataType
                && (o.name.eq_ignore_ascii_case(sdt_name)
                    || o.id.as_str().eq_ignore_ascii_case(sdt_name))
        });

        let Some(sdt_obj) = sdt_obj else {
            // "if present in the catalog" -> if absent, validation is a no-op
            return Ok(());
        };

        let valid_fields = extract_sdt_fields(sdt_obj)?;
        validate_group_fields(&self.root_group, &valid_fields, sdt_name)
    }
}

/// Strips single-line (`//`) and multi-line (`/* */`) comments, preserving string literals
/// and any comment delimiters or semicolons inside them.
pub fn strip_comments(source: &str) -> Result<String, String> {
    let chars: Vec<char> = source.chars().collect();
    let len = chars.len();
    let mut i = 0;
    let mut result = String::with_capacity(source.len());

    while i < len {
        let ch = chars[i];

        // Single-line comment //
        if ch == '/' && i + 1 < len && chars[i + 1] == '/' {
            i += 2;
            while i < len && chars[i] != '\n' && chars[i] != '\r' {
                i += 1;
            }
            continue;
        }

        // Multi-line comment /* */
        if ch == '/' && i + 1 < len && chars[i + 1] == '*' {
            i += 2;
            let mut closed = false;
            while i + 1 < len {
                if chars[i] == '*' && chars[i + 1] == '/' {
                    closed = true;
                    i += 2;
                    break;
                }
                i += 1;
            }
            if !closed {
                return Err("unclosed block comment in DataProvider source".into());
            }
            result.push(' ');
            continue;
        }

        // Single-quoted string literal '...'
        if ch == '\'' {
            result.push('\'');
            i += 1;
            let mut closed = false;
            while i < len {
                let c = chars[i];
                if c == '\'' {
                    // GeneXus escaped quote ''
                    if i + 1 < len && chars[i + 1] == '\'' {
                        result.push('\'');
                        result.push('\'');
                        i += 2;
                        continue;
                    }
                    result.push('\'');
                    closed = true;
                    i += 1;
                    break;
                }
                result.push(c);
                i += 1;
            }
            if !closed {
                return Err("unclosed single-quoted string literal in DataProvider source".into());
            }
            continue;
        }

        // Double-quoted string literal "..."
        if ch == '"' {
            result.push('"');
            i += 1;
            let mut closed = false;
            while i < len {
                let c = chars[i];
                if c == '\\' && i + 1 < len {
                    result.push('\\');
                    result.push(chars[i + 1]);
                    i += 2;
                    continue;
                }
                if c == '"' {
                    result.push('"');
                    closed = true;
                    i += 1;
                    break;
                }
                result.push(c);
                i += 1;
            }
            if !closed {
                return Err("unclosed double-quoted string literal in DataProvider source".into());
            }
            continue;
        }

        result.push(ch);
        i += 1;
    }

    Ok(result)
}

fn parse_parm_arguments(
    args_str: &str,
    parameters: &mut Vec<String>,
    output_sdt: &mut Option<String>,
) -> Result<(), String> {
    let args = split_comma_args(args_str)?;
    for arg in args {
        let trimmed = arg.trim();
        if trimmed.is_empty() {
            continue;
        }
        if let Some((mode_part, name_part)) = trimmed.split_once(':') {
            let mode = mode_part.trim().to_ascii_uppercase();
            let name = name_part.trim();
            if mode == "OUT" {
                let clean_name = name.strip_prefix('&').unwrap_or(name).trim().to_string();
                *output_sdt = Some(clean_name);
            } else {
                parameters.push(name.to_string());
            }
        } else if trimmed.to_ascii_uppercase().starts_with("OUT ") {
            let name = trimmed[4..].trim();
            let clean_name = name.strip_prefix('&').unwrap_or(name).trim().to_string();
            *output_sdt = Some(clean_name);
        } else {
            parameters.push(trimmed.to_string());
        }
    }
    Ok(())
}

fn split_comma_args(s: &str) -> Result<Vec<String>, String> {
    let mut args = Vec::new();
    let mut current = String::new();
    let chars: Vec<char> = s.chars().collect();
    let len = chars.len();
    let mut i = 0;
    let mut paren_depth = 0;

    while i < len {
        let ch = chars[i];
        if ch == '\'' {
            current.push('\'');
            i += 1;
            while i < len {
                let c = chars[i];
                if c == '\'' {
                    if i + 1 < len && chars[i + 1] == '\'' {
                        current.push('\'');
                        current.push('\'');
                        i += 2;
                        continue;
                    }
                    current.push('\'');
                    i += 1;
                    break;
                }
                current.push(c);
                i += 1;
            }
            continue;
        }
        if ch == '"' {
            current.push('"');
            i += 1;
            while i < len {
                let c = chars[i];
                if c == '\\' && i + 1 < len {
                    current.push('\\');
                    current.push(chars[i + 1]);
                    i += 2;
                    continue;
                }
                if c == '"' {
                    current.push('"');
                    i += 1;
                    break;
                }
                current.push(c);
                i += 1;
            }
            continue;
        }
        if ch == '(' {
            paren_depth += 1;
            current.push(ch);
            i += 1;
            continue;
        }
        if ch == ')' {
            if paren_depth > 0 {
                paren_depth -= 1;
            }
            current.push(ch);
            i += 1;
            continue;
        }
        if ch == ',' && paren_depth == 0 {
            args.push(current.trim().to_string());
            current.clear();
            i += 1;
            continue;
        }
        current.push(ch);
        i += 1;
    }

    let remaining = current.trim();
    if !remaining.is_empty() {
        args.push(remaining.to_string());
    }
    Ok(args)
}

fn evaluate_expression(
    expr: &str,
    params: &HashMap<String, serde_json::Value>,
) -> Result<serde_json::Value, String> {
    let trimmed = expr.trim();
    if trimmed.is_empty() {
        return Ok(serde_json::Value::Null);
    }

    // 1. Quoted string literals
    if (trimmed.starts_with('\'') && trimmed.ends_with('\'') && trimmed.len() >= 2)
        || (trimmed.starts_with('"') && trimmed.ends_with('"') && trimmed.len() >= 2)
    {
        let inner = &trimmed[1..trimmed.len() - 1];
        let is_single = trimmed.starts_with('\'');
        let mut unescaped = String::with_capacity(inner.len());
        let chars: Vec<char> = inner.chars().collect();
        let len = chars.len();
        let mut i = 0;

        while i < len {
            if is_single && chars[i] == '\'' && i + 1 < len && chars[i + 1] == '\'' {
                unescaped.push('\'');
                i += 2;
                continue;
            }
            if !is_single && chars[i] == '\\' && i + 1 < len {
                match chars[i + 1] {
                    'n' => unescaped.push('\n'),
                    'r' => unescaped.push('\r'),
                    't' => unescaped.push('\t'),
                    '"' => unescaped.push('"'),
                    '\\' => unescaped.push('\\'),
                    other => {
                        unescaped.push('\\');
                        unescaped.push(other);
                    }
                }
                i += 2;
                continue;
            }
            unescaped.push(chars[i]);
            i += 1;
        }
        return Ok(serde_json::Value::String(unescaped));
    }

    // 2. Boolean literals
    if trimmed.eq_ignore_ascii_case("true") {
        return Ok(serde_json::Value::Bool(true));
    }
    if trimmed.eq_ignore_ascii_case("false") {
        return Ok(serde_json::Value::Bool(false));
    }

    // 3. Null literal
    if trimmed.eq_ignore_ascii_case("null") {
        return Ok(serde_json::Value::Null);
    }

    // 4. Variables / Parameters: e.g. &ClientId or ClientId
    if let Some(var_name) = trimmed.strip_prefix('&') {
        if let Some(val) = params.get(trimmed) {
            return Ok(val.clone());
        }
        if let Some(val) = params.get(var_name) {
            return Ok(val.clone());
        }
        for (k, val) in params {
            let k_norm = k.strip_prefix('&').unwrap_or(k);
            if k_norm.eq_ignore_ascii_case(var_name) || k.eq_ignore_ascii_case(trimmed) {
                return Ok(val.clone());
            }
        }
        return Err(format!("unresolved parameter '{trimmed}'"));
    }

    if let Some(val) = params.get(trimmed) {
        return Ok(val.clone());
    }
    for (k, val) in params {
        let k_norm = k.strip_prefix('&').unwrap_or(k);
        if k_norm.eq_ignore_ascii_case(trimmed) {
            return Ok(val.clone());
        }
    }

    // 5. Integer literals
    if let Ok(int_val) = trimmed.parse::<i64>() {
        return Ok(serde_json::json!(int_val));
    }
    if let Ok(uint_val) = trimmed.parse::<u64>() {
        return Ok(serde_json::json!(uint_val));
    }

    // 6. Floating point literals
    if let Ok(float_val) = trimmed.parse::<f64>()
        && let Some(num) = serde_json::Number::from_f64(float_val)
    {
        return Ok(serde_json::Value::Number(num));
    }

    // Fallback: unquoted identifier/string
    Ok(serde_json::Value::String(trimmed.to_string()))
}

fn extract_sdt_fields(sdt_obj: &crate::catalog::KbObject) -> Result<HashSet<String>, String> {
    let mut fields = HashSet::new();
    let part = sdt_obj
        .source_parts
        .iter()
        .find(|p| p.name.eq_ignore_ascii_case("FIELDS"))
        .or_else(|| sdt_obj.source_parts.first());

    let Some(part) = part else {
        return Ok(fields);
    };

    let parsed: serde_json::Value = serde_json::from_str(&part.source)
        .map_err(|e| format!("invalid SDT fields JSON for '{}': {e}", sdt_obj.name))?;

    match parsed {
        serde_json::Value::Object(map) => {
            if let Some(serde_json::Value::Array(items)) = map.get("fields") {
                for item in items {
                    if let Some(name) = item.get("name").and_then(|n| n.as_str()) {
                        fields.insert(name.to_ascii_uppercase());
                    }
                }
            } else if let Some(serde_json::Value::Object(fields_map)) = map.get("fields") {
                for key in fields_map.keys() {
                    fields.insert(key.to_ascii_uppercase());
                }
            } else {
                for key in map.keys() {
                    fields.insert(key.to_ascii_uppercase());
                }
            }
        }
        serde_json::Value::Array(items) => {
            for item in items {
                if let Some(name) = item.get("name").and_then(|n| n.as_str()) {
                    fields.insert(name.to_ascii_uppercase());
                } else if let Some(s) = item.as_str() {
                    fields.insert(s.to_ascii_uppercase());
                }
            }
        }
        _ => {}
    }

    Ok(fields)
}

fn validate_group_fields(
    group: &DpGroup,
    valid_fields: &HashSet<String>,
    sdt_name: &str,
) -> Result<(), String> {
    for item in &group.items {
        match item {
            DpItem::Assignment { target, .. } => {
                let canonical = target.to_ascii_uppercase();
                if !valid_fields.contains(&canonical) {
                    return Err(format!(
                        "unknown field '{target}' in DataProvider, not found in SDT '{sdt_name}'"
                    ));
                }
            }
            DpItem::Group(child_group) => {
                // If the group itself is a collection, the child groups are records whose
                // assignments should be checked against valid fields
                if group.is_collection {
                    validate_group_fields(child_group, valid_fields, sdt_name)?;
                } else if valid_fields.contains(&child_group.name.to_ascii_uppercase()) {
                    // Nested field matches an SDT structure member
                } else {
                    // Check if child assignments match known fields
                    validate_group_fields(child_group, valid_fields, sdt_name)?;
                }
            }
        }
    }
    Ok(())
}

struct ParserCursor<'a> {
    chars: Vec<char>,
    pos: usize,
    _marker: std::marker::PhantomData<&'a ()>,
}

impl<'a> ParserCursor<'a> {
    fn new(input: &'a str) -> Self {
        Self {
            chars: input.chars().collect(),
            pos: 0,
            _marker: std::marker::PhantomData,
        }
    }

    fn has_more(&self) -> bool {
        self.pos < self.chars.len()
    }

    fn peek(&self) -> Option<char> {
        self.chars.get(self.pos).copied()
    }

    fn advance(&mut self) -> Option<char> {
        if self.pos < self.chars.len() {
            let ch = self.chars[self.pos];
            self.pos += 1;
            Some(ch)
        } else {
            None
        }
    }

    fn skip_whitespace(&mut self) {
        while self.pos < self.chars.len() && self.chars[self.pos].is_whitespace() {
            self.pos += 1;
        }
    }

    fn skip_whitespace_and_semicolons(&mut self) {
        while self.pos < self.chars.len()
            && (self.chars[self.pos].is_whitespace() || self.chars[self.pos] == ';')
        {
            self.pos += 1;
        }
    }

    fn consume_if(&mut self, expected: char) -> bool {
        self.skip_whitespace();
        if self.pos < self.chars.len() && self.chars[self.pos] == expected {
            self.pos += 1;
            true
        } else {
            false
        }
    }

    fn peek_keyword_ignore_case(&self, keyword: &str) -> bool {
        let mut idx = self.pos;
        while idx < self.chars.len() && self.chars[idx].is_whitespace() {
            idx += 1;
        }
        let kw_chars: Vec<char> = keyword.chars().collect();
        if idx + kw_chars.len() > self.chars.len() {
            return false;
        }
        for (i, kw_ch) in kw_chars.iter().enumerate() {
            if !self.chars[idx + i].eq_ignore_ascii_case(kw_ch) {
                return false;
            }
        }
        let after = idx + kw_chars.len();
        if after < self.chars.len() {
            let next_ch = self.chars[after];
            if next_ch.is_alphanumeric() || next_ch == '_' {
                return false;
            }
        }
        true
    }

    fn consume_identifier(&mut self) -> Result<String, String> {
        self.skip_whitespace();
        if self.pos >= self.chars.len() {
            return Err("expected identifier, reached end of input".into());
        }
        let start = self.pos;
        while self.pos < self.chars.len() {
            let ch = self.chars[self.pos];
            if ch.is_alphanumeric() || ch == '_' || ch == '&' || ch == '.' {
                self.pos += 1;
            } else {
                break;
            }
        }
        if start == self.pos {
            return Err(format!(
                "expected identifier at char '{}'",
                self.chars[self.pos]
            ));
        }
        Ok(self.chars[start..self.pos].iter().collect())
    }

    fn read_balanced_until(&mut self, closing: char) -> Result<String, String> {
        let mut depth = 0;
        let start = self.pos;
        while self.pos < self.chars.len() {
            let ch = self.chars[self.pos];
            if ch == '\'' {
                self.pos += 1;
                while self.pos < self.chars.len() {
                    if self.chars[self.pos] == '\'' {
                        if self.pos + 1 < self.chars.len() && self.chars[self.pos + 1] == '\'' {
                            self.pos += 2;
                            continue;
                        }
                        self.pos += 1;
                        break;
                    }
                    self.pos += 1;
                }
                continue;
            }
            if ch == '"' {
                self.pos += 1;
                while self.pos < self.chars.len() {
                    if self.chars[self.pos] == '\\' && self.pos + 1 < self.chars.len() {
                        self.pos += 2;
                        continue;
                    }
                    if self.chars[self.pos] == '"' {
                        self.pos += 1;
                        break;
                    }
                    self.pos += 1;
                }
                continue;
            }
            if ch == '(' || ch == '[' || ch == '{' {
                depth += 1;
            } else if ch == ')' || ch == ']' || ch == '}' {
                if depth == 0 && ch == closing {
                    return Ok(self.chars[start..self.pos].iter().collect());
                }
                if depth > 0 {
                    depth -= 1;
                }
            } else if ch == closing && depth == 0 {
                return Ok(self.chars[start..self.pos].iter().collect());
            }
            self.pos += 1;
        }
        if depth == 0 && closing == ')' {
            Ok(self.chars[start..self.pos].iter().collect())
        } else {
            Err(format!("unmatched closing '{closing}' in DataProvider"))
        }
    }

    fn skip_until_semicolon(&mut self) {
        while self.pos < self.chars.len() {
            let ch = self.chars[self.pos];
            self.pos += 1;
            if ch == ';' {
                break;
            }
        }
    }

    fn is_group_start(&self) -> bool {
        let mut idx = self.pos;
        while idx < self.chars.len() && self.chars[idx].is_whitespace() {
            idx += 1;
        }
        if idx >= self.chars.len() {
            return false;
        }
        if self.chars[idx] == '{' {
            return true;
        }
        // Scan identifier
        while idx < self.chars.len()
            && (self.chars[idx].is_alphanumeric() || self.chars[idx] == '_')
        {
            idx += 1;
        }
        while idx < self.chars.len() && self.chars[idx].is_whitespace() {
            idx += 1;
        }
        if idx < self.chars.len() && (self.chars[idx] == '{' || self.chars[idx] == '[') {
            return true;
        }
        // Could be "GroupName collection {"
        let remaining: String = self.chars[idx..].iter().take(15).collect();
        remaining
            .trim_start()
            .to_ascii_uppercase()
            .starts_with("COLLECTION")
    }
}

fn parse_group(cursor: &mut ParserCursor) -> Result<DpGroup, String> {
    cursor.skip_whitespace_and_semicolons();
    if !cursor.has_more() {
        return Err("expected group definition, found end of input".into());
    }

    let group_name = if cursor.peek() == Some('{') {
        "Root".to_string()
    } else {
        cursor.consume_identifier()?
    };

    let mut is_collection = false;
    let mut properties = Vec::new();

    loop {
        cursor.skip_whitespace();
        if cursor.consume_if('[') {
            let props_content = cursor.read_balanced_until(']')?;
            cursor.consume_if(']');
            let props = parse_group_properties(&props_content, &mut is_collection)?;
            properties.extend(props);
            continue;
        }

        if cursor.peek_keyword_ignore_case("collection") {
            cursor.consume_identifier()?;
            is_collection = true;
            continue;
        }

        break;
    }

    cursor.skip_whitespace();
    if !cursor.consume_if('{') {
        return Err(format!(
            "expected '{{' after group declaration '{group_name}'"
        ));
    }

    let mut items = Vec::new();

    while cursor.has_more() {
        cursor.skip_whitespace_and_semicolons();
        if !cursor.has_more() {
            return Err(format!("unclosed group '{group_name}', expected '}}'"));
        }
        if cursor.peek() == Some('}') {
            cursor.advance();
            break;
        }

        // Check if next item is a nested group or assignment
        if is_next_nested_group(cursor)? {
            let child = parse_group(cursor)?;
            items.push(DpItem::Group(child));
        } else {
            let assignment = parse_assignment(cursor)?;
            items.push(assignment);
        }
    }

    cursor.skip_whitespace();
    cursor.consume_if(';');

    Ok(DpGroup {
        name: group_name,
        is_collection,
        properties,
        items,
    })
}

fn parse_group_properties(
    content: &str,
    is_collection: &mut bool,
) -> Result<Vec<DpProperty>, String> {
    let mut props = Vec::new();
    let parts = split_comma_args(content)?;
    for part in parts {
        let trimmed = part.trim();
        if trimmed.is_empty() {
            continue;
        }
        if let Some((k, v)) = trimmed.split_once('=') {
            let name = k.trim().to_string();
            let value = v.trim().to_string();
            if name.eq_ignore_ascii_case("collection") {
                *is_collection = value.eq_ignore_ascii_case("true") || value == "1";
            }
            props.push(DpProperty::new(name, value));
        } else {
            let name = trimmed.to_string();
            if name.eq_ignore_ascii_case("collection") {
                *is_collection = true;
            }
            props.push(DpProperty::new(name, "true"));
        }
    }
    Ok(props)
}

fn is_next_nested_group(cursor: &ParserCursor) -> Result<bool, String> {
    let mut idx = cursor.pos;
    while idx < cursor.chars.len() && cursor.chars[idx].is_whitespace() {
        idx += 1;
    }
    if idx >= cursor.chars.len() {
        return Ok(false);
    }
    if cursor.chars[idx] == '{' {
        return Ok(true);
    }

    // Read candidate identifier
    let start = idx;
    while idx < cursor.chars.len()
        && (cursor.chars[idx].is_alphanumeric() || cursor.chars[idx] == '_')
    {
        idx += 1;
    }
    if start == idx {
        return Ok(false);
    }

    while idx < cursor.chars.len() && cursor.chars[idx].is_whitespace() {
        idx += 1;
    }
    if idx >= cursor.chars.len() {
        return Ok(false);
    }

    if cursor.chars[idx] == '{' || cursor.chars[idx] == '[' {
        return Ok(true);
    }

    // Check for "collection" keyword
    let remaining: String = cursor.chars[idx..].iter().take(12).collect();
    if remaining
        .trim_start()
        .to_ascii_uppercase()
        .starts_with("COLLECTION")
    {
        return Ok(true);
    }

    Ok(false)
}

fn parse_assignment(cursor: &mut ParserCursor) -> Result<DpItem, String> {
    let target = cursor.consume_identifier()?;
    cursor.skip_whitespace();
    if !cursor.consume_if('=') {
        return Err(format!("expected '=' after assignment target '{target}'"));
    }

    let mut value_chars = Vec::new();
    let mut in_single_quote = false;
    let mut in_double_quote = false;
    let mut paren_depth = 0;

    while cursor.has_more() {
        let ch = cursor.peek().unwrap();

        if !in_single_quote && !in_double_quote {
            if ch == '\'' {
                in_single_quote = true;
                value_chars.push(ch);
                cursor.advance();
                continue;
            }
            if ch == '"' {
                in_double_quote = true;
                value_chars.push(ch);
                cursor.advance();
                continue;
            }
            if ch == '(' {
                paren_depth += 1;
                value_chars.push(ch);
                cursor.advance();
                continue;
            }
            if ch == ')' {
                if paren_depth > 0 {
                    paren_depth -= 1;
                }
                value_chars.push(ch);
                cursor.advance();
                continue;
            }
            if paren_depth == 0 && (ch == ';' || ch == '\n' || ch == '\r' || ch == '}') {
                if ch == ';' {
                    cursor.advance();
                }
                break;
            }
        } else if in_single_quote {
            value_chars.push(ch);
            cursor.advance();
            if ch == '\'' {
                if cursor.peek() == Some('\'') {
                    value_chars.push('\'');
                    cursor.advance();
                } else {
                    in_single_quote = false;
                }
            }
            continue;
        } else if in_double_quote {
            value_chars.push(ch);
            cursor.advance();
            if ch == '\\' && cursor.has_more() {
                value_chars.push(cursor.advance().unwrap());
                continue;
            }
            if ch == '"' {
                in_double_quote = false;
            }
            continue;
        }

        value_chars.push(ch);
        cursor.advance();
    }

    let value: String = value_chars.into_iter().collect();
    let trimmed_value = value.trim().to_string();

    Ok(DpItem::Assignment {
        target,
        value: trimmed_value,
    })
}
