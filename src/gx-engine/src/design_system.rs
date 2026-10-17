//! Design System Object (DSO) Tokens & Styles Engine for gx-engine.
//!
//! Provides parsing, token management, inheritance, and CSS rendering
//! for GeneXus Design System Objects.

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// A single token in a Design System Object.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DesignSystemToken {
    pub name: String,
    pub value: String,
    pub token_set: Option<String>,
}

impl DesignSystemToken {
    pub fn new(
        name: impl Into<String>,
        value: impl Into<String>,
        token_set: Option<impl Into<String>>,
    ) -> Self {
        Self {
            name: name.into(),
            value: value.into(),
            token_set: token_set.map(Into::into),
        }
    }
}

/// A set of tokens, typically grouped under a section like `#colors` or `#spacing`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TokenSet {
    pub name: String,
    pub tokens: HashMap<String, String>,
}

impl TokenSet {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            tokens: HashMap::new(),
        }
    }

    pub fn insert(&mut self, key: impl Into<String>, value: impl Into<String>) {
        self.tokens.insert(key.into(), value.into());
    }

    pub fn get(&self, key: &str) -> Option<&str> {
        self.tokens.get(key).map(|s| s.as_str())
    }
}

/// A CSS-like style declaration, e.g. `background-color: $colors.primary;`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StyleDeclaration {
    pub property: String,
    pub value: String,
}

impl StyleDeclaration {
    pub fn new(property: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            property: property.into(),
            value: value.into(),
        }
    }
}

/// A CSS-like style rule with a selector and declarations.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StyleRule {
    pub selector: String,
    pub declarations: Vec<StyleDeclaration>,
}

impl StyleRule {
    pub fn new(selector: impl Into<String>, declarations: Vec<StyleDeclaration>) -> Self {
        Self {
            selector: selector.into(),
            declarations,
        }
    }
}

/// A GeneXus Design System Object (DSO).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DesignSystemObject {
    pub name: String,
    pub imports: Vec<String>,
    pub token_sets: Vec<TokenSet>,
    pub rules: Vec<StyleRule>,
}

impl DesignSystemObject {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            imports: Vec::new(),
            token_sets: Vec::new(),
            rules: Vec::new(),
        }
    }

    /// Finds a token set by group name, accepting `#group` or `group` (case-insensitive).
    pub fn get_token_set(&self, name: &str) -> Option<&TokenSet> {
        let clean = name.trim_start_matches('#');
        self.token_sets.iter().find(|ts| {
            ts.name == name || ts.name.trim_start_matches('#').eq_ignore_ascii_case(clean)
        })
    }

    /// Returns a flat list of all tokens across all token sets.
    pub fn tokens(&self) -> Vec<DesignSystemToken> {
        let mut result = Vec::new();
        for ts in &self.token_sets {
            for (k, v) in &ts.tokens {
                result.push(DesignSystemToken {
                    name: k.clone(),
                    value: v.clone(),
                    token_set: Some(ts.name.clone()),
                });
            }
        }
        result
    }

    /// Parses a DSO source into an in-memory `DesignSystemObject`.
    pub fn parse(name: &str, source: &str) -> Result<Self, String> {
        parse(name, source)
    }

    /// Resolves a token reference (e.g. `$colors.primary` or `$primary`) into its literal value.
    ///
    /// If not found in `self`, falls back to `parent_dso` if provided.
    pub fn resolve_token(
        &self,
        token_ref: &str,
        parent_dso: Option<&DesignSystemObject>,
    ) -> Option<String> {
        self.resolve_token_depth(token_ref, parent_dso, 0)
    }

    fn resolve_token_depth(
        &self,
        token_ref: &str,
        parent_dso: Option<&DesignSystemObject>,
        depth: usize,
    ) -> Option<String> {
        if depth > 16 {
            return None; // Guard against recursive loops
        }

        let raw_val = self
            .find_raw_token(token_ref)
            .or_else(|| parent_dso.and_then(|p| p.find_raw_token(token_ref)))?;

        // If raw_val is itself a reference to another token (e.g. "$colors.base"), resolve recursively
        if raw_val.starts_with('$') {
            return self.resolve_token_depth(&raw_val, parent_dso, depth + 1);
        }

        Some(raw_val)
    }

    fn find_raw_token(&self, token_ref: &str) -> Option<String> {
        let clean = token_ref.trim_start_matches('$');
        if let Some(dot_idx) = clean.find('.') {
            let group = &clean[..dot_idx];
            let name = &clean[dot_idx + 1..];
            let clean_group = group.trim_start_matches('#');
            for ts in &self.token_sets {
                if ts.name == group
                    || ts.name == format!("#{group}")
                    || ts
                        .name
                        .trim_start_matches('#')
                        .eq_ignore_ascii_case(clean_group)
                {
                    if let Some(val) = ts.tokens.get(name) {
                        return Some(val.clone());
                    }
                    for (k, v) in &ts.tokens {
                        if k.eq_ignore_ascii_case(name) {
                            return Some(v.clone());
                        }
                    }
                }
            }
        } else {
            // Search all token sets for a token named `clean`
            for ts in &self.token_sets {
                if let Some(val) = ts.tokens.get(clean) {
                    return Some(val.clone());
                }
                for (k, v) in &ts.tokens {
                    if k.eq_ignore_ascii_case(clean) {
                        return Some(v.clone());
                    }
                }
            }
        }
        None
    }

    /// Substitutes all `$token` references in a text value. Fails closed if any token cannot be resolved.
    fn substitute_tokens(
        &self,
        value: &str,
        parent_dso: Option<&DesignSystemObject>,
    ) -> Result<String, String> {
        let mut result = String::with_capacity(value.len());
        let chars: Vec<char> = value.chars().collect();
        let len = chars.len();
        let mut i = 0;

        while i < len {
            if chars[i] == '$' {
                let start = i;
                i += 1;
                let mut dot_seen = false;
                while i < len {
                    let c = chars[i];
                    if c.is_ascii_alphanumeric() || c == '_' || c == '-' {
                        i += 1;
                    } else if c == '.'
                        && !dot_seen
                        && i + 1 < len
                        && (chars[i + 1].is_ascii_alphanumeric()
                            || chars[i + 1] == '_'
                            || chars[i + 1] == '-')
                    {
                        dot_seen = true;
                        i += 1;
                    } else {
                        break;
                    }
                }
                let token_ref: String = chars[start..i].iter().collect();
                if token_ref.len() > 1 {
                    if let Some(resolved) = self.resolve_token(&token_ref, parent_dso) {
                        result.push_str(&resolved);
                    } else {
                        return Err(format!("unresolved token: {}", token_ref));
                    }
                } else {
                    result.push('$');
                }
            } else {
                result.push(chars[i]);
                i += 1;
            }
        }

        Ok(result)
    }

    /// Renders clean, formatted CSS text by substituting all `$token` references.
    /// Fails closed if any referenced token cannot be resolved.
    pub fn render_css(&self, parent_dso: Option<&DesignSystemObject>) -> Result<String, String> {
        let mut css = String::new();
        for (i, rule) in self.rules.iter().enumerate() {
            if i > 0 {
                css.push('\n');
            }
            let selector = self.substitute_tokens(&rule.selector, parent_dso)?;
            css.push_str(&selector);
            css.push_str(" {\n");
            for decl in &rule.declarations {
                let resolved_value = self.substitute_tokens(&decl.value, parent_dso)?;
                css.push_str(&format!("    {}: {};\n", decl.property, resolved_value));
            }
            css.push_str("}\n");
        }
        Ok(css)
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
            } else {
                i = len;
            }
            continue;
        }

        result.push(c);
        i += 1;
    }

    result
}

/// Parses a DSO source into a `DesignSystemObject`.
pub fn parse(name: &str, source: &str) -> Result<DesignSystemObject, String> {
    let clean = strip_comments(source);
    let mut imports = Vec::new();
    let cleaned_without_imports = extract_imports_and_clean(&clean, &mut imports);

    let mut dso = DesignSystemObject::new(name);
    dso.imports = imports;

    let blocks = split_blocks(&cleaned_without_imports)?;

    for (header, body) in blocks {
        let trimmed_header = header.trim();
        if trimmed_header.starts_with("tokens") || trimmed_header.starts_with("tokens[") {
            parse_tokens_block(trimmed_header, &body, &mut dso)?;
        } else if trimmed_header.starts_with("styles") || trimmed_header.starts_with("styles(") {
            parse_styles_block(trimmed_header, &body, &mut dso)?;
        } else if trimmed_header.starts_with('#') {
            // Direct #group at top level
            parse_group_block(trimmed_header, &body, &mut dso);
        } else if !trimmed_header.is_empty() {
            // Direct style rule at top level
            parse_single_style_rule(trimmed_header, &body, &mut dso);
        }
    }

    if dso.name.is_empty() {
        dso.name = "DesignSystem".to_string();
    }

    Ok(dso)
}

fn extract_imports_and_clean(source: &str, imports: &mut Vec<String>) -> String {
    let mut output = String::with_capacity(source.len());
    let mut remaining = source;

    while let Some(import_idx) = remaining.find("@import") {
        output.push_str(&remaining[..import_idx]);
        let after_import = &remaining[import_idx + "@import".len()..];
        if let Some(semi_idx) = after_import.find(';') {
            let import_body = &after_import[..semi_idx];
            for raw_target in import_body.split(',') {
                let trimmed = raw_target
                    .trim()
                    .trim_matches('"')
                    .trim_matches('\'')
                    .trim();
                let clean_name = if let Some(stripped) = trimmed.strip_suffix(".css") {
                    stripped
                } else if let Some(stripped) = trimmed.strip_suffix(".styles") {
                    stripped
                } else if let Some(stripped) = trimmed.strip_suffix(".tokens") {
                    stripped
                } else {
                    trimmed
                };
                if !clean_name.is_empty() && !imports.iter().any(|i| i == clean_name) {
                    imports.push(clean_name.to_string());
                }
            }
            remaining = &after_import[semi_idx + 1..];
        } else {
            remaining = "";
            break;
        }
    }
    output.push_str(remaining);
    output
}

fn split_blocks(source: &str) -> Result<Vec<(String, String)>, String> {
    let mut blocks = Vec::new();
    let chars: Vec<char> = source.chars().collect();
    let len = chars.len();
    let mut i = 0;
    let mut depth = 0usize;
    let mut in_single_quote = false;
    let mut in_double_quote = false;
    let mut escape = false;

    let mut current_header = String::new();
    let mut current_body = String::new();

    while i < len {
        let c = chars[i];

        if in_single_quote {
            if depth > 0 {
                current_body.push(c);
            } else {
                current_header.push(c);
            }
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
            if depth > 0 {
                current_body.push(c);
            } else {
                current_header.push(c);
            }
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
            if depth > 0 {
                current_body.push(c);
            } else {
                current_header.push(c);
            }
            i += 1;
            continue;
        }

        if c == '"' {
            in_double_quote = true;
            if depth > 0 {
                current_body.push(c);
            } else {
                current_header.push(c);
            }
            i += 1;
            continue;
        }

        if c == '{' {
            if depth == 0 {
                current_body.clear();
            } else {
                current_body.push(c);
            }
            depth += 1;
        } else if c == '}' {
            if depth == 0 {
                return Err("unmatched closing brace '}' in design system source".to_string());
            }
            depth -= 1;
            if depth == 0 {
                blocks.push((
                    current_header.trim().to_string(),
                    current_body.trim().to_string(),
                ));
                current_header.clear();
                current_body.clear();
            } else {
                current_body.push(c);
            }
        } else if depth == 0 {
            current_header.push(c);
        } else {
            current_body.push(c);
        }

        i += 1;
    }

    if depth != 0 {
        return Err("unclosed opening brace '{' in design system source".to_string());
    }

    Ok(blocks)
}

fn parse_tokens_block(
    header: &str,
    body: &str,
    dso: &mut DesignSystemObject,
) -> Result<(), String> {
    // Extract imports from `tokens(...)` or `tokens Foo (Parent)`
    extract_parentheses_imports(header, &mut dso.imports);

    // Look for inner blocks (#group { ... })
    let inner_blocks = split_blocks(body)?;
    let mut parsed_any_group = false;

    for (inner_header, inner_body) in inner_blocks {
        let trimmed_inner = inner_header.trim();
        if trimmed_inner.starts_with('#') {
            parse_group_block(trimmed_inner, &inner_body, dso);
            parsed_any_group = true;
        }
    }

    if !parsed_any_group {
        // Direct declarations inside tokens block without a #group
        let declarations = parse_raw_declarations(body);
        if !declarations.is_empty() {
            let set_name = extract_name_after_keyword(header, "tokens")
                .unwrap_or_else(|| "default".to_string());
            let mut set = TokenSet::new(set_name);
            for (k, v) in declarations {
                set.insert(k, v);
            }
            dso.token_sets.push(set);
        }
    }

    Ok(())
}

fn parse_styles_block(
    header: &str,
    body: &str,
    dso: &mut DesignSystemObject,
) -> Result<(), String> {
    // Extract imports from `styles(ParentDSO)` or `styles DSO_Name (ParentDSO)`
    extract_parentheses_imports(header, &mut dso.imports);

    // If styles has `: ParentDSO`, extract as import
    if let Some(colon_pos) = header.find(':') {
        let parent_part = &header[colon_pos + 1..];
        for item in parent_part.split(',') {
            let trimmed = item.trim();
            if !trimmed.is_empty() && !dso.imports.iter().any(|i| i == trimmed) {
                dso.imports.push(trimmed.to_string());
            }
        }
    }

    // Extract DSO name if dso.name is empty
    if dso.name.is_empty()
        && let Some(name) = extract_name_after_keyword(header, "styles")
    {
        dso.name = name;
    }

    // Parse rules and any nested token groups inside styles block
    let inner_blocks = split_blocks(body)?;
    for (inner_header, inner_body) in inner_blocks {
        let trimmed_inner = inner_header.trim();
        if trimmed_inner.starts_with('#') {
            parse_group_block(trimmed_inner, &inner_body, dso);
        } else if !trimmed_inner.is_empty() {
            parse_single_style_rule(trimmed_inner, &inner_body, dso);
        }
    }

    Ok(())
}

fn parse_group_block(header: &str, body: &str, dso: &mut DesignSystemObject) {
    let group_name = header.trim().to_string();
    let declarations = parse_raw_declarations(body);

    if let Some(existing) = dso
        .token_sets
        .iter_mut()
        .find(|ts| ts.name.eq_ignore_ascii_case(&group_name))
    {
        for (k, v) in declarations {
            existing.insert(k, v);
        }
    } else {
        let mut set = TokenSet::new(group_name);
        for (k, v) in declarations {
            set.insert(k, v);
        }
        dso.token_sets.push(set);
    }
}

fn parse_single_style_rule(selector_str: &str, body: &str, dso: &mut DesignSystemObject) {
    let normalized_selector = selector_str
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .collect::<Vec<_>>()
        .join(" ");

    let mut declarations = Vec::new();
    for (prop, val) in parse_raw_declarations(body) {
        declarations.push(StyleDeclaration::new(prop, val));
    }

    dso.rules
        .push(StyleRule::new(normalized_selector, declarations));
}

fn extract_parentheses_imports(header: &str, imports: &mut Vec<String>) {
    if let Some(start) = header.find('(')
        && let Some(end) = header.rfind(')')
        && end > start
    {
        let inner = &header[start + 1..end];
        for item in inner.split(',') {
            let trimmed = item.trim().trim_matches('"').trim_matches('\'').trim();
            if !trimmed.is_empty() && !imports.iter().any(|i| i == trimmed) {
                imports.push(trimmed.to_string());
            }
        }
    }
}

fn extract_name_after_keyword(header: &str, keyword: &str) -> Option<String> {
    let stripped = header.strip_prefix(keyword)?.trim();
    let name_part = if let Some(paren_pos) = stripped.find('(') {
        &stripped[..paren_pos]
    } else if let Some(bracket_pos) = stripped.find('[') {
        if let Some(close_bracket) = stripped.find(']') {
            &stripped[bracket_pos + 1..close_bracket]
        } else {
            stripped
        }
    } else if let Some(colon_pos) = stripped.find(':') {
        &stripped[..colon_pos]
    } else {
        stripped
    };

    let name = name_part.trim();
    if name.is_empty() {
        None
    } else {
        Some(name.to_string())
    }
}

fn parse_raw_declarations(body: &str) -> Vec<(String, String)> {
    let mut declarations = Vec::new();
    let statements = split_statements(body);
    for stmt in statements {
        let trimmed = stmt.trim();
        if trimmed.is_empty() {
            continue;
        }
        if let Some(colon_pos) = find_colon_outside_quotes(trimmed) {
            let property = trimmed[..colon_pos].trim().to_string();
            let value = trimmed[colon_pos + 1..].trim().to_string();
            if !property.is_empty() {
                declarations.push((property, value));
            }
        }
    }
    declarations
}

fn split_statements(body: &str) -> Vec<String> {
    let mut statements = Vec::new();
    let mut current = String::new();
    let chars: Vec<char> = body.chars().collect();
    let len = chars.len();
    let mut i = 0;
    let mut in_single = false;
    let mut in_double = false;
    let mut escape = false;

    while i < len {
        let c = chars[i];

        if in_single {
            current.push(c);
            if escape {
                escape = false;
            } else if c == '\\' {
                escape = true;
            } else if c == '\'' {
                in_single = false;
            }
            i += 1;
            continue;
        }

        if in_double {
            current.push(c);
            if escape {
                escape = false;
            } else if c == '\\' {
                escape = true;
            } else if c == '"' {
                in_double = false;
            }
            i += 1;
            continue;
        }

        if c == '\'' {
            in_single = true;
            current.push(c);
            i += 1;
            continue;
        }

        if c == '"' {
            in_double = true;
            current.push(c);
            i += 1;
            continue;
        }

        if c == ';' {
            statements.push(current.trim().to_string());
            current.clear();
        } else {
            current.push(c);
        }

        i += 1;
    }

    let trailing = current.trim();
    if !trailing.is_empty() {
        statements.push(trailing.to_string());
    }

    statements
}

fn find_colon_outside_quotes(s: &str) -> Option<usize> {
    let chars: Vec<char> = s.chars().collect();
    let len = chars.len();
    let mut i = 0;
    let mut in_single = false;
    let mut in_double = false;
    let mut escape = false;

    while i < len {
        let c = chars[i];

        if in_single {
            if escape {
                escape = false;
            } else if c == '\\' {
                escape = true;
            } else if c == '\'' {
                in_single = false;
            }
            i += 1;
            continue;
        }

        if in_double {
            if escape {
                escape = false;
            } else if c == '\\' {
                escape = true;
            } else if c == '"' {
                in_double = false;
            }
            i += 1;
            continue;
        }

        if c == '\'' {
            in_single = true;
            i += 1;
            continue;
        }

        if c == '"' {
            in_double = true;
            i += 1;
            continue;
        }

        if c == ':' {
            let mut byte_idx = 0;
            for ch in chars[..i].iter() {
                byte_idx += ch.len_utf8();
            }
            return Some(byte_idx);
        }

        i += 1;
    }

    None
}
