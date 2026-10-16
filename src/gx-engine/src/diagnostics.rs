//! Stable, portable-engine diagnostic structure.
//!
//! Existing engine boundaries return strings, so callers must assign a category
//! from the stage that produced the error. These categories describe gx-engine
//! phases only; they are not GeneXus SDK diagnostic codes or parity claims.
use serde::Serialize;

/// The engine phase that produced a diagnostic.
///
/// Keep these values phase-level: message text alone does not reliably encode
/// whether an error came from input validation, parsing, compilation, or a host
/// toolchain.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum DiagnosticCategory {
    /// Invalid CLI command or argument shape.
    Usage,
    /// File access, project/catalog/XML decoding, or input validation.
    Input,
    /// Authored procedure source rejected by the portable parser.
    Syntax,
    /// The `compile` operation failed; its string error does not split parsing,
    /// type checking, and portable source generation.
    Compilation,
    /// The build pipeline failed. The current build operation also calls
    /// `compile`, so its string error may originate before the host compiler.
    Build,
    /// Launching or executing the generated program failed.
    Execution,
}

/// One-based source coordinates when the existing parser error provides them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct SourceLocation {
    pub line: usize,
    pub column: usize,
}

/// Structured wrapper that preserves the original engine error verbatim.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Diagnostic {
    pub category: DiagnosticCategory,
    pub message: String,
    pub location: Option<SourceLocation>,
}

impl Diagnostic {
    /// Wrap an error with the category known by its caller.
    pub fn new(category: DiagnosticCategory, message: impl Into<String>) -> Self {
        Self {
            category,
            message: message.into(),
            location: None,
        }
    }

    /// Wrap the parser's current `line N, column N: ...` error format.
    ///
    /// The message remains byte-for-byte unchanged. A location is exposed only
    /// when the whole existing prefix is well formed and both coordinates are
    /// nonzero; no file, part, or native SDK code is inferred.
    pub fn syntax_error(message: impl Into<String>) -> Self {
        let message = message.into();
        let location = parse_parser_location(&message);
        Self {
            category: DiagnosticCategory::Syntax,
            message,
            location,
        }
    }
}

fn parse_parser_location(message: &str) -> Option<SourceLocation> {
    let rest = message.strip_prefix("line ")?;
    let (line, rest) = rest.split_once(", column ")?;
    let (column, detail) = rest.split_once(": ")?;
    if detail.is_empty() {
        return None;
    }
    let line = line.parse::<usize>().ok()?;
    let column = column.parse::<usize>().ok()?;
    if line == 0 || column == 0 {
        return None;
    }
    Some(SourceLocation { line, column })
}
