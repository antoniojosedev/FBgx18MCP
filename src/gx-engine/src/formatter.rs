//! Code Formatting and Linting Engine (Row C5).
//!
//! Provides AST-aware indentation formatting, keyword canonicalization,
//! string/comment preservation, and static linting rules for GeneXus procedure
//! and event source code.

use serde::{Deserialize, Serialize};

/// Severity of a lint diagnostic issue.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum LintSeverity {
    Info,
    Warning,
    Error,
}

/// A diagnostic message produced by the linter.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LintDiagnostic {
    pub rule: String,
    pub severity: LintSeverity,
    pub message: String,
    pub line: usize,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub column: Option<usize>,
}

/// Options configuring the code formatter.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FormatOptions {
    pub indent_size: usize,
    pub use_tabs: bool,
    pub canonicalize_keywords: bool,
    pub trim_trailing_whitespace: bool,
}

impl Default for FormatOptions {
    fn default() -> Self {
        Self {
            indent_size: 4,
            use_tabs: false,
            canonicalize_keywords: true,
            trim_trailing_whitespace: true,
        }
    }
}

/// Formats GeneXus source code according to the provided options.
pub fn format_source(source: &str, options: &FormatOptions) -> String {
    let mut formatted_lines = Vec::new();
    let mut current_indent: usize = 0;

    let lines: Vec<&str> = source.lines().collect();

    for line in lines {
        let trimmed = line.trim();

        if trimmed.is_empty() {
            formatted_lines.push(String::new());
            continue;
        }

        // Check if current line is a closing block that should de-indent *before* printing
        let is_closing = is_block_close(trimmed);
        if is_closing && current_indent > 0 {
            current_indent -= 1;
        }

        let is_mid_block = is_block_mid(trimmed);
        let effective_indent = if is_mid_block && current_indent > 0 {
            current_indent - 1
        } else {
            current_indent
        };

        let indent_str = if options.use_tabs {
            "\t".repeat(effective_indent)
        } else {
            " ".repeat(effective_indent * options.indent_size)
        };

        let processed_line = if options.canonicalize_keywords {
            canonicalize_line(trimmed)
        } else {
            trimmed.to_string()
        };

        let mut line_out = format!("{}{}", indent_str, processed_line);
        if options.trim_trailing_whitespace {
            line_out = line_out.trim_end().to_string();
        }
        formatted_lines.push(line_out);

        // Check if current line is an opening block that should indent *after* printing
        if is_block_open(trimmed) {
            current_indent += 1;
        }
    }

    formatted_lines.join("\n")
}

/// Runs lint rules over the provided GeneXus source.
pub fn lint_source(source: &str) -> Vec<LintDiagnostic> {
    let mut diagnostics = Vec::new();
    let mut block_stack: Vec<(&'static str, usize)> = Vec::new();
    let mut seen_unreachable = false;

    let lines: Vec<&str> = source.lines().collect();

    for (idx, line) in lines.iter().enumerate() {
        let line_num = idx + 1;
        let trimmed = line.trim();

        if trimmed.is_empty() || trimmed.starts_with("//") {
            continue;
        }

        let upper = trimmed.to_ascii_uppercase();

        // Check unreachable code
        if seen_unreachable && !is_block_close(trimmed) && !is_block_mid(trimmed) {
            diagnostics.push(LintDiagnostic {
                rule: "unreachable_code".to_string(),
                severity: LintSeverity::Warning,
                message: "Unreachable code detected following Return or Exit".to_string(),
                line: line_num,
                column: Some(1),
            });
            seen_unreachable = false;
        }

        if upper == "RETURN" || upper == "EXIT" {
            seen_unreachable = true;
        }

        // Deprecated functions check
        if upper.contains("DYNCALL(") || upper.contains("DYNCALL ") {
            diagnostics.push(LintDiagnostic {
                rule: "deprecated_syntax".to_string(),
                severity: LintSeverity::Warning,
                message: "DynCall is deprecated; prefer strongly typed Call or UDP".to_string(),
                line: line_num,
                column: Some(1),
            });
        }

        // Block tracking
        if is_for_each(trimmed) {
            block_stack.push(("For each", line_num));
            seen_unreachable = false;
        } else if is_if(trimmed) {
            block_stack.push(("If", line_num));
            seen_unreachable = false;
        } else if is_do_case(trimmed) {
            block_stack.push(("Do Case", line_num));
            seen_unreachable = false;
        } else if is_sub(trimmed) {
            block_stack.push(("Sub", line_num));
            seen_unreachable = false;
        } else if is_do_while(trimmed) {
            block_stack.push(("Do While", line_num));
            seen_unreachable = false;
        } else if upper == "ENDFOR" {
            seen_unreachable = false;
            match block_stack.pop() {
                Some(("For each", _)) => {}
                Some((expected, start_line)) => {
                    diagnostics.push(LintDiagnostic {
                        rule: "mismatched_block".to_string(),
                        severity: LintSeverity::Error,
                        message: format!(
                            "Expected matching End{} for {} started on line {}",
                            expected, expected, start_line
                        ),
                        line: line_num,
                        column: Some(1),
                    });
                }
                None => {
                    diagnostics.push(LintDiagnostic {
                        rule: "unmatched_close".to_string(),
                        severity: LintSeverity::Error,
                        message: "Unexpected EndFor with no matching For each".to_string(),
                        line: line_num,
                        column: Some(1),
                    });
                }
            }
        } else if upper == "ENDIF" {
            seen_unreachable = false;
            match block_stack.pop() {
                Some(("If", _)) => {}
                Some((expected, start_line)) => {
                    diagnostics.push(LintDiagnostic {
                        rule: "mismatched_block".to_string(),
                        severity: LintSeverity::Error,
                        message: format!(
                            "Expected matching End{} for {} started on line {}",
                            expected, expected, start_line
                        ),
                        line: line_num,
                        column: Some(1),
                    });
                }
                None => {
                    diagnostics.push(LintDiagnostic {
                        rule: "unmatched_close".to_string(),
                        severity: LintSeverity::Error,
                        message: "Unexpected EndIf with no matching If".to_string(),
                        line: line_num,
                        column: Some(1),
                    });
                }
            }
        } else if upper == "ENDCASE" {
            seen_unreachable = false;
            match block_stack.pop() {
                Some(("Do Case", _)) => {}
                Some((expected, start_line)) => {
                    diagnostics.push(LintDiagnostic {
                        rule: "mismatched_block".to_string(),
                        severity: LintSeverity::Error,
                        message: format!(
                            "Expected matching End{} for {} started on line {}",
                            expected, expected, start_line
                        ),
                        line: line_num,
                        column: Some(1),
                    });
                }
                None => {
                    diagnostics.push(LintDiagnostic {
                        rule: "unmatched_close".to_string(),
                        severity: LintSeverity::Error,
                        message: "Unexpected EndCase with no matching Do Case".to_string(),
                        line: line_num,
                        column: Some(1),
                    });
                }
            }
        } else if upper == "ENDSUB" {
            seen_unreachable = false;
            match block_stack.pop() {
                Some(("Sub", _)) => {}
                Some((expected, start_line)) => {
                    diagnostics.push(LintDiagnostic {
                        rule: "mismatched_block".to_string(),
                        severity: LintSeverity::Error,
                        message: format!(
                            "Expected matching End{} for {} started on line {}",
                            expected, expected, start_line
                        ),
                        line: line_num,
                        column: Some(1),
                    });
                }
                None => {
                    diagnostics.push(LintDiagnostic {
                        rule: "unmatched_close".to_string(),
                        severity: LintSeverity::Error,
                        message: "Unexpected EndSub with no matching Sub".to_string(),
                        line: line_num,
                        column: Some(1),
                    });
                }
            }
        }
    }

    // Check for unclosed blocks remaining on the stack
    for (block_name, start_line) in block_stack {
        diagnostics.push(LintDiagnostic {
            rule: "unclosed_block".to_string(),
            severity: LintSeverity::Error,
            message: format!(
                "Unclosed {} block started on line {}",
                block_name, start_line
            ),
            line: start_line,
            column: Some(1),
        });
    }

    diagnostics
}

fn is_for_each(line: &str) -> bool {
    let u = line.to_ascii_uppercase();
    u == "FOR EACH" || u.starts_with("FOR EACH ")
}

fn is_if(line: &str) -> bool {
    let u = line.to_ascii_uppercase();
    u == "IF" || u.starts_with("IF ")
}

fn is_do_case(line: &str) -> bool {
    line.eq_ignore_ascii_case("DO CASE")
}

fn is_sub(line: &str) -> bool {
    let u = line.to_ascii_uppercase();
    u.starts_with("SUB ")
}

fn is_do_while(line: &str) -> bool {
    let u = line.to_ascii_uppercase();
    u.starts_with("DO WHILE ")
}

fn is_block_open(line: &str) -> bool {
    let u = line.to_ascii_uppercase();
    is_for_each(line)
        || is_if(line)
        || is_do_case(line)
        || is_sub(line)
        || is_do_while(line)
        || (u.starts_with("EVENT ") && !u.contains("ENDEVENT"))
        || u.starts_with("NEW") && !u.contains("ENDNEW")
}

fn is_block_close(line: &str) -> bool {
    let u = line.to_ascii_uppercase();
    u == "ENDFOR"
        || u == "ENDIF"
        || u == "ENDCASE"
        || u == "ENDSUB"
        || u == "ENDEVENT"
        || u == "ENDNEW"
}

fn is_block_mid(line: &str) -> bool {
    let u = line.to_ascii_uppercase();
    u == "ELSE" || u.starts_with("ELSEIF ") || u == "WHEN NONE"
}

/// Canonicalizes keywords in a single line while preserving string literals and comments.
fn canonicalize_line(line: &str) -> String {
    let mut result = String::with_capacity(line.len());
    let mut chars = line.chars().peekable();

    while let Some(ch) = chars.next() {
        if ch == '"' || ch == '\'' {
            // String literal: copy verbatim until closing quote
            result.push(ch);
            let quote = ch;
            for c in chars.by_ref() {
                result.push(c);
                if c == quote {
                    break;
                }
            }
        } else if ch == '/' && chars.peek() == Some(&'/') {
            // Single-line comment: copy the remainder of the line verbatim
            result.push(ch);
            for c in chars.by_ref() {
                result.push(c);
            }
            break;
        } else if ch.is_alphabetic() || ch == '&' {
            let mut word = String::new();
            word.push(ch);
            while let Some(&c) = chars.peek() {
                if c.is_alphanumeric() || c == '_' || c == '.' {
                    word.push(c);
                    chars.next();
                } else {
                    break;
                }
            }
            result.push_str(&canonicalize_word(&word));
        } else {
            result.push(ch);
        }
    }

    result
}

fn canonicalize_word(word: &str) -> String {
    if word.starts_with('&') {
        return word.to_string(); // Preserve variable names
    }

    match word.to_ascii_lowercase().as_str() {
        "for" => "For".to_string(),
        "each" => "each".to_string(),
        "endfor" => "Endfor".to_string(),
        "if" => "If".to_string(),
        "else" => "Else".to_string(),
        "elseif" => "ElseIf".to_string(),
        "endif" => "Endif".to_string(),
        "do" => "Do".to_string(),
        "case" => "Case".to_string(),
        "otherwise" => "Otherwise".to_string(),
        "endcase" => "Endcase".to_string(),
        "sub" => "Sub".to_string(),
        "endsub" => "Endsub".to_string(),
        "return" => "Return".to_string(),
        "commit" => "Commit".to_string(),
        "rollback" => "Rollback".to_string(),
        "exit" => "Exit".to_string(),
        "print" => "Print".to_string(),
        "msg" => "Msg".to_string(),
        "where" => "Where".to_string(),
        "order" => "Order".to_string(),
        "defined_by" => "defined_by".to_string(),
        "when" => "When".to_string(),
        "none" => "None".to_string(),
        "event" => "Event".to_string(),
        "endevent" => "EndEvent".to_string(),
        "new" => "New".to_string(),
        "endnew" => "Endnew".to_string(),
        _ => word.to_string(),
    }
}
