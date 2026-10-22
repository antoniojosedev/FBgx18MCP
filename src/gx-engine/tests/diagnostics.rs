use gx_engine::diagnostics::{Diagnostic, DiagnosticCategory, SourceLocation};
use serde_json::json;

#[test]
fn parser_diagnostic_preserves_message_and_exposes_existing_one_based_location() {
    let parser_message = gx_engine::frontend::parse_body("Print(1 + )").unwrap_err();
    let diagnostic = Diagnostic::syntax_error(parser_message.clone());

    assert_eq!(diagnostic.category, DiagnosticCategory::Syntax);
    assert_eq!(diagnostic.message, parser_message);
    assert_eq!(
        diagnostic.location,
        Some(SourceLocation {
            line: 1,
            column: 11
        })
    );
    assert_eq!(
        serde_json::to_value(diagnostic).unwrap(),
        json!({
            "category": "syntax",
            "message": "line 1, column 11: expected an integer, variable, call, or parenthesized expression",
            "location": { "line": 1, "column": 11 }
        })
    );
}

#[test]
fn compile_error_category_comes_from_call_site_not_wrapped_parser_text() {
    let project = gx_engine::model::Project::from_json(
        &json!({
            "format_version": 1,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "source": "Print(1 + )"
            }]
        })
        .to_string(),
    )
    .unwrap();
    let error = gx_engine::compile(&project).unwrap_err();
    let diagnostic = Diagnostic::new(DiagnosticCategory::Compilation, error.clone());

    assert!(
        error.starts_with("procedure MAIN: line 1, column 11: "),
        "{error}"
    );
    assert_eq!(diagnostic.category, DiagnosticCategory::Compilation);
    assert_eq!(diagnostic.message, error);
    assert_eq!(diagnostic.location, None);
}

#[test]
fn generic_diagnostics_require_explicit_phase_and_do_not_parse_message_lookalikes() {
    let message = "line 2, column 3: compiler emitted this ordinary text";
    let diagnostic = Diagnostic::new(DiagnosticCategory::Compilation, message);

    assert_eq!(diagnostic.category, DiagnosticCategory::Compilation);
    assert_eq!(diagnostic.message, message);
    assert_eq!(diagnostic.location, None);
    assert_eq!(
        serde_json::to_value(diagnostic).unwrap(),
        json!({
            "category": "compilation",
            "message": message,
            "location": null
        })
    );
}

#[test]
fn parser_location_requires_the_exact_current_prefix_and_positive_coordinates() {
    for message in [
        "prefix: line 1, column 2: error",
        "line 0, column 2: error",
        "line 1, column 0: error",
        "line x, column 2: error",
        "line 1, column 2 error",
        "line 1, column 2: ",
    ] {
        let diagnostic = Diagnostic::syntax_error(message);
        assert_eq!(diagnostic.message, message);
        assert_eq!(diagnostic.location, None, "{message:?}");
    }
}

#[test]
fn categories_serialize_as_stable_phase_names_without_native_codes() {
    for (category, expected) in [
        (DiagnosticCategory::Usage, "usage"),
        (DiagnosticCategory::Input, "input"),
        (DiagnosticCategory::Syntax, "syntax"),
        (DiagnosticCategory::Compilation, "compilation"),
        (DiagnosticCategory::Build, "build"),
        (DiagnosticCategory::Execution, "execution"),
        (DiagnosticCategory::VersionScope, "version_scope"),
    ] {
        let value = serde_json::to_value(Diagnostic::new(category, "failure")).unwrap();
        assert_eq!(value["category"], expected);
        assert!(value.get("code").is_none());
    }
}
