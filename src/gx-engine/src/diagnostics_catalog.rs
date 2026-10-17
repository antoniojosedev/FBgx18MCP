//! Structured Diagnostics Model & Code Catalog Engine for `gx-engine`.
//!
//! Provides official GeneXus diagnostic codes, categories, severities,
//! structured location reporting, diagnostic collection bag, human-readable
//! terminal rendering (Rust/Clang-style with caret), and JSON serialization.

use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fmt;
use std::str::FromStr;

/// Severity level of an engine diagnostic.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum DiagnosticSeverity {
    Error,
    Warning,
    Information,
    Hint,
}

impl DiagnosticSeverity {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Error => "error",
            Self::Warning => "warning",
            Self::Information => "information",
            Self::Hint => "hint",
        }
    }
}

impl fmt::Display for DiagnosticSeverity {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.as_str())
    }
}

impl Serialize for DiagnosticSeverity {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        serializer.serialize_str(self.as_str())
    }
}

impl<'de> Deserialize<'de> for DiagnosticSeverity {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let s = String::deserialize(deserializer)?;
        match s.to_ascii_lowercase().as_str() {
            "error" => Ok(Self::Error),
            "warning" => Ok(Self::Warning),
            "information" | "info" => Ok(Self::Information),
            "hint" => Ok(Self::Hint),
            _ => Err(serde::de::Error::custom(format!(
                "Unknown diagnostic severity: '{s}'"
            ))),
        }
    }
}

/// Category classification of an engine diagnostic.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum DiagnosticCategory {
    Syntax,
    TypeCheck,
    Specification,
    Database,
    Reorganization,
    Naming,
    Security,
}

impl DiagnosticCategory {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Syntax => "syntax",
            Self::TypeCheck => "type_check",
            Self::Specification => "specification",
            Self::Database => "database",
            Self::Reorganization => "reorganization",
            Self::Naming => "naming",
            Self::Security => "security",
        }
    }
}

impl fmt::Display for DiagnosticCategory {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.as_str())
    }
}

impl Serialize for DiagnosticCategory {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        serializer.serialize_str(self.as_str())
    }
}

impl<'de> Deserialize<'de> for DiagnosticCategory {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let s = String::deserialize(deserializer)?;
        match s.to_ascii_lowercase().replace('-', "_").as_str() {
            "syntax" => Ok(Self::Syntax),
            "type_check" | "typecheck" => Ok(Self::TypeCheck),
            "specification" => Ok(Self::Specification),
            "database" => Ok(Self::Database),
            "reorganization" | "reorg" => Ok(Self::Reorganization),
            "naming" => Ok(Self::Naming),
            "security" => Ok(Self::Security),
            _ => Err(serde::de::Error::custom(format!(
                "Unknown diagnostic category: '{s}'"
            ))),
        }
    }
}

/// Official GeneXus diagnostic codes supported by the catalog engine.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum DiagnosticCode {
    /// "No relationship found between attributes"
    Spc0004,
    /// "Ambiguous base table"
    Spc0005,
    /// "Ambiguous object reference; qualified name required"
    Spc0006,
    /// "Type mismatch in expression or assignment"
    Spc0009,
    /// "Cannot convert between types"
    Spc0010,
    /// "Primary key attributes must be completely specified"
    Spc0023,
    /// "Primary key cannot be updated"
    Spc0034,
    /// "Variable not defined"
    Spc0051,
    /// "Circular definition detected"
    Spc0216,
    /// "Unique rule is not supported in this object"
    Src0295,
}

impl DiagnosticCode {
    pub const ALL: &[DiagnosticCode] = &[
        Self::Spc0004,
        Self::Spc0005,
        Self::Spc0006,
        Self::Spc0009,
        Self::Spc0010,
        Self::Spc0023,
        Self::Spc0034,
        Self::Spc0051,
        Self::Spc0216,
        Self::Src0295,
    ];

    pub fn all() -> &'static [DiagnosticCode] {
        Self::ALL
    }

    pub fn code(&self) -> &'static str {
        match self {
            Self::Spc0004 => "spc0004",
            Self::Spc0005 => "spc0005",
            Self::Spc0006 => "spc0006",
            Self::Spc0009 => "spc0009",
            Self::Spc0010 => "spc0010",
            Self::Spc0023 => "spc0023",
            Self::Spc0034 => "spc0034",
            Self::Spc0051 => "spc0051",
            Self::Spc0216 => "spc0216",
            Self::Src0295 => "src0295",
        }
    }

    pub fn as_str(&self) -> &'static str {
        self.code()
    }

    pub fn as_upper(&self) -> &'static str {
        match self {
            Self::Spc0004 => "SPC0004",
            Self::Spc0005 => "SPC0005",
            Self::Spc0006 => "SPC0006",
            Self::Spc0009 => "SPC0009",
            Self::Spc0010 => "SPC0010",
            Self::Spc0023 => "SPC0023",
            Self::Spc0034 => "SPC0034",
            Self::Spc0051 => "SPC0051",
            Self::Spc0216 => "SPC0216",
            Self::Src0295 => "SRC0295",
        }
    }

    pub fn description(&self) -> &'static str {
        match self {
            Self::Spc0004 => "No relationship found between attributes",
            Self::Spc0005 => "Ambiguous base table",
            Self::Spc0006 => "Ambiguous object reference; qualified name required",
            Self::Spc0009 => "Type mismatch in expression or assignment",
            Self::Spc0010 => "Cannot convert between types",
            Self::Spc0023 => "Primary key attributes must be completely specified",
            Self::Spc0034 => "Primary key cannot be updated",
            Self::Spc0051 => "Variable not defined",
            Self::Spc0216 => "Circular definition detected",
            Self::Src0295 => "Unique rule is not supported in this object",
        }
    }

    pub fn default_description(&self) -> &'static str {
        self.description()
    }

    pub fn category(&self) -> DiagnosticCategory {
        match self {
            Self::Spc0004 => DiagnosticCategory::Specification,
            Self::Spc0005 => DiagnosticCategory::Specification,
            Self::Spc0006 => DiagnosticCategory::Naming,
            Self::Spc0009 => DiagnosticCategory::TypeCheck,
            Self::Spc0010 => DiagnosticCategory::TypeCheck,
            Self::Spc0023 => DiagnosticCategory::Database,
            Self::Spc0034 => DiagnosticCategory::Database,
            Self::Spc0051 => DiagnosticCategory::Specification,
            Self::Spc0216 => DiagnosticCategory::Specification,
            Self::Src0295 => DiagnosticCategory::Syntax,
        }
    }

    pub fn default_category(&self) -> DiagnosticCategory {
        self.category()
    }

    pub fn default_severity(&self) -> DiagnosticSeverity {
        DiagnosticSeverity::Error
    }

    pub fn to_diagnostic(&self, message: impl Into<String>) -> EngineDiagnostic {
        EngineDiagnostic::from_code(*self, message, self.default_severity())
    }
}

impl fmt::Display for DiagnosticCode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.code())
    }
}

impl FromStr for DiagnosticCode {
    type Err = String;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        match s.trim().to_ascii_lowercase().as_str() {
            "spc0004" => Ok(Self::Spc0004),
            "spc0005" => Ok(Self::Spc0005),
            "spc0006" => Ok(Self::Spc0006),
            "spc0009" => Ok(Self::Spc0009),
            "spc0010" => Ok(Self::Spc0010),
            "spc0023" => Ok(Self::Spc0023),
            "spc0034" => Ok(Self::Spc0034),
            "spc0051" => Ok(Self::Spc0051),
            "spc0216" => Ok(Self::Spc0216),
            "src0295" => Ok(Self::Src0295),
            _ => Err(format!("Unknown GeneXus diagnostic code: '{s}'")),
        }
    }
}

impl Serialize for DiagnosticCode {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        serializer.serialize_str(self.code())
    }
}

impl<'de> Deserialize<'de> for DiagnosticCode {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let s = String::deserialize(deserializer)?;
        s.parse().map_err(serde::de::Error::custom)
    }
}

/// Source code coordinates where a diagnostic occurred.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SourceLocation {
    pub file: String,
    pub line: usize,
    pub column: usize,
    pub length: usize,
}

impl SourceLocation {
    pub fn new(file: impl Into<String>, line: usize, column: usize, length: usize) -> Self {
        Self {
            file: file.into(),
            line,
            column,
            length,
        }
    }
}

/// Structured diagnostic model produced by `gx-engine`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct EngineDiagnostic {
    pub code: String,
    pub message: String,
    pub severity: DiagnosticSeverity,
    pub category: DiagnosticCategory,
    pub location: Option<SourceLocation>,
    pub help_text: Option<String>,
}

impl EngineDiagnostic {
    pub fn new(
        code: impl Into<String>,
        message: impl Into<String>,
        severity: DiagnosticSeverity,
        category: DiagnosticCategory,
    ) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
            severity,
            category,
            location: None,
            help_text: None,
        }
    }

    pub fn from_code(
        code: DiagnosticCode,
        message: impl Into<String>,
        severity: DiagnosticSeverity,
    ) -> Self {
        Self {
            code: code.code().to_string(),
            message: message.into(),
            severity,
            category: code.default_category(),
            location: None,
            help_text: Some(code.default_description().to_string()),
        }
    }

    pub fn with_location(mut self, location: SourceLocation) -> Self {
        self.location = Some(location);
        self
    }

    pub fn with_help_text(mut self, help: impl Into<String>) -> Self {
        self.help_text = Some(help.into());
        self
    }

    pub fn to_json(&self) -> serde_json::Value {
        serde_json::to_value(self).unwrap_or(serde_json::Value::Null)
    }

    pub fn from_json(value: &serde_json::Value) -> Result<Self, String> {
        serde_json::from_value(value.clone()).map_err(|e| e.to_string())
    }
}

/// Collection bag of diagnostics with query, inspection, rendering, and serialization capabilities.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct DiagnosticBag {
    pub diagnostics: Vec<EngineDiagnostic>,
}

impl DiagnosticBag {
    pub fn new() -> Self {
        Self {
            diagnostics: Vec::new(),
        }
    }

    pub fn add(&mut self, diagnostic: EngineDiagnostic) {
        self.diagnostics.push(diagnostic);
    }

    pub fn add_error(&mut self, code: DiagnosticCode, msg: &str, loc: Option<SourceLocation>) {
        let mut diag = EngineDiagnostic::from_code(code, msg, DiagnosticSeverity::Error);
        diag.location = loc;
        self.diagnostics.push(diag);
    }

    pub fn add_warning(&mut self, code: DiagnosticCode, msg: &str, loc: Option<SourceLocation>) {
        let mut diag = EngineDiagnostic::from_code(code, msg, DiagnosticSeverity::Warning);
        diag.location = loc;
        self.diagnostics.push(diag);
    }

    pub fn add_info(&mut self, code: DiagnosticCode, msg: &str, loc: Option<SourceLocation>) {
        let mut diag = EngineDiagnostic::from_code(code, msg, DiagnosticSeverity::Information);
        diag.location = loc;
        self.diagnostics.push(diag);
    }

    pub fn add_hint(&mut self, code: DiagnosticCode, msg: &str, loc: Option<SourceLocation>) {
        let mut diag = EngineDiagnostic::from_code(code, msg, DiagnosticSeverity::Hint);
        diag.location = loc;
        self.diagnostics.push(diag);
    }

    pub fn has_errors(&self) -> bool {
        self.diagnostics
            .iter()
            .any(|d| d.severity == DiagnosticSeverity::Error)
    }

    pub fn error_count(&self) -> usize {
        self.diagnostics
            .iter()
            .filter(|d| d.severity == DiagnosticSeverity::Error)
            .count()
    }

    pub fn warning_count(&self) -> usize {
        self.diagnostics
            .iter()
            .filter(|d| d.severity == DiagnosticSeverity::Warning)
            .count()
    }

    pub fn len(&self) -> usize {
        self.diagnostics.len()
    }

    pub fn is_empty(&self) -> bool {
        self.diagnostics.is_empty()
    }

    pub fn errors(&self) -> Vec<&EngineDiagnostic> {
        self.filter_by_severity(DiagnosticSeverity::Error)
    }

    pub fn warnings(&self) -> Vec<&EngineDiagnostic> {
        self.filter_by_severity(DiagnosticSeverity::Warning)
    }

    pub fn filter_by_severity(&self, severity: DiagnosticSeverity) -> Vec<&EngineDiagnostic> {
        self.diagnostics
            .iter()
            .filter(|d| d.severity == severity)
            .collect()
    }

    pub fn filter_by_category(&self, category: DiagnosticCategory) -> Vec<&EngineDiagnostic> {
        self.diagnostics
            .iter()
            .filter(|d| d.category == category)
            .collect()
    }

    pub fn group_by_category(&self) -> BTreeMap<DiagnosticCategory, Vec<&EngineDiagnostic>> {
        let mut groups: BTreeMap<DiagnosticCategory, Vec<&EngineDiagnostic>> = BTreeMap::new();
        for diag in &self.diagnostics {
            groups.entry(diag.category).or_default().push(diag);
        }
        groups
    }

    /// Renders human-readable Rust/Clang-style terminal diagnostics with caret (`^`) pointing to column.
    pub fn render_text(&self, source_context: Option<&str>) -> String {
        if self.diagnostics.is_empty() {
            return String::new();
        }

        let source_lines: Option<Vec<&str>> = source_context.map(|s| s.lines().collect());
        let mut blocks = Vec::new();

        for diag in &self.diagnostics {
            let mut block = String::new();
            if let Some(loc) = &diag.location {
                block.push_str(&format!(
                    "{}:{}:{}: {}: [{}] {}",
                    loc.file, loc.line, loc.column, diag.severity, diag.code, diag.message
                ));

                if let Some(lines) = &source_lines
                    && loc.line > 0
                    && loc.line <= lines.len()
                {
                    let line_content = lines[loc.line - 1];
                    block.push('\n');
                    block.push_str(line_content);
                    block.push('\n');
                    let indent = " ".repeat(loc.column.saturating_sub(1));
                    let carets = if loc.length > 1 {
                        "^".repeat(loc.length)
                    } else {
                        "^".to_string()
                    };
                    block.push_str(&indent);
                    block.push_str(&carets);
                }
            } else {
                block.push_str(&format!(
                    "{}: [{}] {}",
                    diag.severity, diag.code, diag.message
                ));
            }

            if let Some(help) = &diag.help_text {
                block.push('\n');
                block.push_str(&format!("  help: {help}"));
            }

            blocks.push(block);
        }

        blocks.join("\n\n")
    }

    pub fn to_json(&self) -> serde_json::Value {
        serde_json::to_value(self).unwrap_or(serde_json::Value::Null)
    }

    pub fn from_json(value: &serde_json::Value) -> Result<DiagnosticBag, String> {
        serde_json::from_value(value.clone()).map_err(|e| e.to_string())
    }
}

impl IntoIterator for DiagnosticBag {
    type Item = EngineDiagnostic;
    type IntoIter = std::vec::IntoIter<EngineDiagnostic>;

    fn into_iter(self) -> Self::IntoIter {
        self.diagnostics.into_iter()
    }
}

impl<'a> IntoIterator for &'a DiagnosticBag {
    type Item = &'a EngineDiagnostic;
    type IntoIter = std::slice::Iter<'a, EngineDiagnostic>;

    fn into_iter(self) -> Self::IntoIter {
        self.diagnostics.iter()
    }
}
