use gx_engine::formatter::{FormatOptions, LintSeverity, format_source, lint_source};

#[test]
fn test_formatter_normalizes_nested_indentation() {
    let unformatted = r#"
for each
if &CustomerId > 0
&Total = &Total + CustomerBalance
endif
endfor
"#;
    let options = FormatOptions::default();
    let formatted = format_source(unformatted, &options);

    let expected = r#"For each
    If &CustomerId > 0
        &Total = &Total + CustomerBalance
    Endif
Endfor"#;

    assert_eq!(formatted.trim(), expected);
}

#[test]
fn test_formatter_preserves_string_literal_casing() {
    let source = r#"msg("hello world ALL CAPS text", status)"#;
    let options = FormatOptions::default();
    let formatted = format_source(source, &options);
    assert!(formatted.contains("\"hello world ALL CAPS text\""));
}

#[test]
fn test_formatter_preserves_comments() {
    let source = "for each // this is a comment with FOR EACH in it\nendfor";
    let options = FormatOptions::default();
    let formatted = format_source(source, &options);
    assert!(formatted.contains("// this is a comment with FOR EACH in it"));
}

#[test]
fn test_formatter_uses_tabs_when_configured() {
    let source = "if &X == 1\n&Y = 2\nendif";
    let options = FormatOptions {
        use_tabs: true,
        ..Default::default()
    };
    let formatted = format_source(source, &options);
    assert!(formatted.contains("\t&Y = 2"));
}

#[test]
fn test_formatter_handles_do_case_and_otherwise() {
    let source = r#"
do case
case &Status == 1
&Desc = "Active"
case &Status == 2
&Desc = "Pending"
otherwise
&Desc = "Unknown"
endcase
"#;
    let options = FormatOptions::default();
    let formatted = format_source(source, &options);
    assert!(formatted.contains("Do Case"));
    assert!(formatted.contains("    Case &Status == 1"));
    assert!(formatted.contains("    Otherwise"));
    assert!(formatted.contains("Endcase"));
}

#[test]
fn test_linter_detects_unclosed_block() {
    let source = r#"
        For each
            &X = 1
    "#;
    let diags = lint_source(source);
    assert_eq!(diags.len(), 1);
    assert_eq!(diags[0].rule, "unclosed_block");
    assert_eq!(diags[0].severity, LintSeverity::Error);
    assert!(diags[0].message.contains("Unclosed For each"));
}

#[test]
fn test_linter_detects_mismatched_blocks() {
    let source = r#"
        If &Condition
            &X = 1
        Endfor
    "#;
    let diags = lint_source(source);
    assert!(diags.iter().any(|d| d.rule == "mismatched_block"));
}

#[test]
fn test_linter_detects_unreachable_code_after_return() {
    let source = r#"
        &X = 1
        Return
        &Y = 2
    "#;
    let diags = lint_source(source);
    assert!(diags.iter().any(|d| d.rule == "unreachable_code"));
}

#[test]
fn test_linter_detects_deprecated_syntax() {
    let source = r#"
        DynCall("MyProc", &Arg1)
    "#;
    let diags = lint_source(source);
    assert!(diags.iter().any(|d| d.rule == "deprecated_syntax"));
}

#[test]
fn test_linter_passes_valid_code_with_zero_diagnostics() {
    let source = r#"
        Sub 'Calculate'
            For each
                Where CustomerId = &CustomerId
                If Balance > 0
                    &Total = &Total + Balance
                Endif
            Endfor
        Endsub
    "#;
    let diags = lint_source(source);
    assert!(diags.is_empty(), "Expected 0 diagnostics, got: {:?}", diags);
}
