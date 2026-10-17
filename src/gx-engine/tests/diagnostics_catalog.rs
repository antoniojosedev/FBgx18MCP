use gx_engine::diagnostics_catalog::{
    DiagnosticBag, DiagnosticCategory, DiagnosticCode, DiagnosticSeverity, EngineDiagnostic,
    SourceLocation,
};
use serde_json::json;

#[test]
fn test_diagnostic_codes_catalog_metadata() {
    let all = DiagnosticCode::all();
    assert_eq!(all.len(), 10);

    let expected_metadata = [
        (
            DiagnosticCode::Spc0004,
            "spc0004",
            "SPC0004",
            "No relationship found between attributes",
            DiagnosticCategory::Specification,
        ),
        (
            DiagnosticCode::Spc0005,
            "spc0005",
            "SPC0005",
            "Ambiguous base table",
            DiagnosticCategory::Specification,
        ),
        (
            DiagnosticCode::Spc0006,
            "spc0006",
            "SPC0006",
            "Ambiguous object reference; qualified name required",
            DiagnosticCategory::Naming,
        ),
        (
            DiagnosticCode::Spc0009,
            "spc0009",
            "SPC0009",
            "Type mismatch in expression or assignment",
            DiagnosticCategory::TypeCheck,
        ),
        (
            DiagnosticCode::Spc0010,
            "spc0010",
            "SPC0010",
            "Cannot convert between types",
            DiagnosticCategory::TypeCheck,
        ),
        (
            DiagnosticCode::Spc0023,
            "spc0023",
            "SPC0023",
            "Primary key attributes must be completely specified",
            DiagnosticCategory::Database,
        ),
        (
            DiagnosticCode::Spc0034,
            "spc0034",
            "SPC0034",
            "Primary key cannot be updated",
            DiagnosticCategory::Database,
        ),
        (
            DiagnosticCode::Spc0051,
            "spc0051",
            "SPC0051",
            "Variable not defined",
            DiagnosticCategory::Specification,
        ),
        (
            DiagnosticCode::Spc0216,
            "spc0216",
            "SPC0216",
            "Circular definition detected",
            DiagnosticCategory::Specification,
        ),
        (
            DiagnosticCode::Src0295,
            "src0295",
            "SRC0295",
            "Unique rule is not supported in this object",
            DiagnosticCategory::Syntax,
        ),
    ];

    for (code, code_str, code_upper, description, category) in expected_metadata {
        assert_eq!(code.code(), code_str);
        assert_eq!(code.as_str(), code_str);
        assert_eq!(code.as_upper(), code_upper);
        assert_eq!(code.description(), description);
        assert_eq!(code.default_description(), description);
        assert_eq!(code.category(), category);
        assert_eq!(code.default_category(), category);
        assert_eq!(code.default_severity(), DiagnosticSeverity::Error);

        // Display trait
        assert_eq!(format!("{code}"), code_str);

        // Case-insensitive FromStr parsing
        assert_eq!(code_str.parse::<DiagnosticCode>().unwrap(), code);
        assert_eq!(code_upper.parse::<DiagnosticCode>().unwrap(), code);
    }

    // Invalid code parsing
    assert!("spc9999".parse::<DiagnosticCode>().is_err());
    assert!("invalid".parse::<DiagnosticCode>().is_err());
}

#[test]
fn test_diagnostic_bag_error_and_warning_counts() {
    let mut bag = DiagnosticBag::new();
    assert_eq!(bag.error_count(), 0);
    assert_eq!(bag.warning_count(), 0);
    assert_eq!(bag.len(), 0);
    assert!(bag.is_empty());

    // Add 2 errors
    bag.add_error(
        DiagnosticCode::Spc0004,
        "No relationship found between attributes CustomerId and InvoiceTotal",
        Some(SourceLocation::new("Invoice.trn", 10, 5, 1)),
    );
    bag.add_error(
        DiagnosticCode::Spc0009,
        "Type mismatch in assignment to &Total",
        Some(SourceLocation::new("Invoice.trn", 14, 9, 6)),
    );

    // Add 3 warnings
    bag.add_warning(
        DiagnosticCode::Spc0005,
        "Ambiguous base table defaulted to Orders",
        Some(SourceLocation::new("Reports.prc", 22, 1, 8)),
    );
    bag.add_warning(
        DiagnosticCode::Spc0006,
        "Ambiguous object reference 'Item'; qualified name recommended",
        None,
    );
    bag.add_warning(
        DiagnosticCode::Spc0051,
        "Variable &Temp undefined in current scope",
        None,
    );

    assert_eq!(bag.error_count(), 2);
    assert_eq!(bag.warning_count(), 3);
    assert_eq!(bag.len(), 5);
    assert!(!bag.is_empty());
    assert!(bag.has_errors());
}

#[test]
fn test_has_errors_fail_closed() {
    let mut bag = DiagnosticBag::new();
    assert!(!bag.has_errors());

    // Warnings, info, and hints must NOT trigger has_errors
    bag.add_warning(
        DiagnosticCode::Spc0005,
        "Ambiguous base table warning",
        None,
    );
    assert!(!bag.has_errors());

    bag.add_info(
        DiagnosticCode::Spc0006,
        "Informational notice about qualification",
        None,
    );
    assert!(!bag.has_errors());

    bag.add_hint(
        DiagnosticCode::Spc0051,
        "Hint: define variable in properties",
        None,
    );
    assert!(!bag.has_errors());
    assert_eq!(bag.error_count(), 0);

    // Adding a single error must immediately fail closed
    bag.add_error(
        DiagnosticCode::Spc0216,
        "Circular definition detected between Customer and Order",
        None,
    );
    assert!(bag.has_errors());
    assert_eq!(bag.error_count(), 1);
}

#[test]
fn test_terminal_rendering_with_source_line_and_caret() {
    let mut bag = DiagnosticBag::new();
    bag.add_error(
        DiagnosticCode::Spc0004,
        "No relationship found between attributes",
        Some(SourceLocation::new("Customer.trn", 2, 5, 1)),
    );

    let source_context = "Procedure Customer\n    CustomerId = 100\nEndProcedure";
    let rendered = bag.render_text(Some(source_context));

    assert!(
        rendered.contains(
            "Customer.trn:2:5: error: [spc0004] No relationship found between attributes"
        ),
        "expected clang-style header: {rendered}"
    );
    assert!(
        rendered.contains("    CustomerId = 100"),
        "expected source line: {rendered}"
    );
    assert!(
        rendered.contains("    ^"),
        "expected 4-space caret: {rendered}"
    );
    assert!(
        rendered.contains("help: No relationship found between attributes"),
        "expected help line: {rendered}"
    );

    // Multi-column caret length
    let mut bag_multi = DiagnosticBag::new();
    bag_multi.add_error(
        DiagnosticCode::Spc0009,
        "Type mismatch in expression",
        Some(SourceLocation::new("Expr.prc", 1, 5, 4)),
    );
    let rendered_multi = bag_multi.render_text(Some("&A = &B + &C"));
    assert!(
        rendered_multi.contains("    ^^^^"),
        "expected 4 carets: {rendered_multi}"
    );
}

#[test]
fn test_terminal_rendering_without_source_location() {
    let mut bag = DiagnosticBag::new();
    bag.add_error(DiagnosticCode::Spc0005, "Ambiguous base table", None);

    // Without source context
    let rendered_no_ctx = bag.render_text(None);
    assert_eq!(
        rendered_no_ctx,
        "error: [spc0005] Ambiguous base table\n  help: Ambiguous base table"
    );
    assert!(!rendered_no_ctx.contains('^'));

    // With source context provided, but diagnostic has no location
    let rendered_with_ctx = bag.render_text(Some("line 1\nline 2"));
    assert_eq!(
        rendered_with_ctx,
        "error: [spc0005] Ambiguous base table\n  help: Ambiguous base table"
    );
    assert!(!rendered_with_ctx.contains('^'));

    // Empty bag rendering
    let empty_bag = DiagnosticBag::new();
    assert_eq!(empty_bag.render_text(None), "");
    assert_eq!(empty_bag.render_text(Some("source")), "");
}

#[test]
fn test_diagnostic_bag_json_serialization_and_deserialization() {
    let mut bag = DiagnosticBag::new();
    bag.add_error(
        DiagnosticCode::Spc0004,
        "No relationship found between attributes",
        Some(SourceLocation::new("Invoice.trn", 12, 5, 1)),
    );
    bag.add_warning(DiagnosticCode::Spc0006, "Ambiguous object reference", None);

    let json_val = bag.to_json();
    assert!(json_val.is_object());
    let diagnostics_arr = json_val
        .get("diagnostics")
        .and_then(|v| v.as_array())
        .unwrap();
    assert_eq!(diagnostics_arr.len(), 2);

    assert_eq!(diagnostics_arr[0]["code"], "spc0004");
    assert_eq!(diagnostics_arr[0]["severity"], "error");
    assert_eq!(diagnostics_arr[0]["category"], "specification");
    assert_eq!(diagnostics_arr[0]["location"]["file"], "Invoice.trn");
    assert_eq!(diagnostics_arr[0]["location"]["line"], 12);
    assert_eq!(diagnostics_arr[0]["location"]["column"], 5);
    assert_eq!(diagnostics_arr[0]["location"]["length"], 1);

    assert_eq!(diagnostics_arr[1]["code"], "spc0006");
    assert_eq!(diagnostics_arr[1]["severity"], "warning");
    assert_eq!(diagnostics_arr[1]["category"], "naming");
    assert!(diagnostics_arr[1]["location"].is_null());

    // Roundtrip deserialization
    let restored = DiagnosticBag::from_json(&json_val).expect("valid deserialization");
    assert_eq!(restored, bag);

    // Deserialization failure on invalid shape
    let invalid_json = json!({ "diagnostics": "not-an-array" });
    assert!(DiagnosticBag::from_json(&invalid_json).is_err());
}

#[test]
fn test_severity_filtering() {
    let mut bag = DiagnosticBag::new();
    bag.add_error(DiagnosticCode::Spc0004, "Error 1", None);
    bag.add_error(DiagnosticCode::Spc0009, "Error 2", None);
    bag.add_warning(DiagnosticCode::Spc0005, "Warning 1", None);
    bag.add_warning(DiagnosticCode::Spc0006, "Warning 2", None);
    bag.add_info(DiagnosticCode::Spc0051, "Info 1", None);
    bag.add_hint(DiagnosticCode::Src0295, "Hint 1", None);

    assert_eq!(bag.errors().len(), 2);
    assert_eq!(bag.warnings().len(), 2);
    assert_eq!(bag.filter_by_severity(DiagnosticSeverity::Error).len(), 2);
    assert_eq!(bag.filter_by_severity(DiagnosticSeverity::Warning).len(), 2);
    assert_eq!(
        bag.filter_by_severity(DiagnosticSeverity::Information)
            .len(),
        1
    );
    assert_eq!(bag.filter_by_severity(DiagnosticSeverity::Hint).len(), 1);

    let error_messages: Vec<&str> = bag.errors().iter().map(|d| d.message.as_str()).collect();
    assert_eq!(error_messages, vec!["Error 1", "Error 2"]);

    let warning_messages: Vec<&str> = bag.warnings().iter().map(|d| d.message.as_str()).collect();
    assert_eq!(warning_messages, vec!["Warning 1", "Warning 2"]);
}

#[test]
fn test_category_grouping() {
    let mut bag = DiagnosticBag::new();
    bag.add_error(DiagnosticCode::Src0295, "Syntax unique error", None);
    bag.add_error(DiagnosticCode::Spc0009, "Type mismatch", None);
    bag.add_error(DiagnosticCode::Spc0004, "Spec relation error", None);
    bag.add_warning(DiagnosticCode::Spc0005, "Spec ambiguous base", None);
    bag.add_error(DiagnosticCode::Spc0023, "DB primary key incomplete", None);
    bag.add_warning(DiagnosticCode::Spc0006, "Naming ambiguity", None);

    // Custom engine diagnostics covering Reorganization and Security
    bag.add(EngineDiagnostic::new(
        "reorg0001",
        "Table drop requires manual confirmation",
        DiagnosticSeverity::Warning,
        DiagnosticCategory::Reorganization,
    ));
    bag.add(EngineDiagnostic::new(
        "sec0001",
        "Hardcoded credentials detected in service call",
        DiagnosticSeverity::Error,
        DiagnosticCategory::Security,
    ));

    let groups = bag.group_by_category();
    assert_eq!(groups.len(), 7); // all 7 categories represented!

    assert_eq!(groups.get(&DiagnosticCategory::Syntax).unwrap().len(), 1);
    assert_eq!(groups.get(&DiagnosticCategory::TypeCheck).unwrap().len(), 1);
    assert_eq!(
        groups
            .get(&DiagnosticCategory::Specification)
            .unwrap()
            .len(),
        2
    );
    assert_eq!(groups.get(&DiagnosticCategory::Database).unwrap().len(), 1);
    assert_eq!(groups.get(&DiagnosticCategory::Naming).unwrap().len(), 1);
    assert_eq!(
        groups
            .get(&DiagnosticCategory::Reorganization)
            .unwrap()
            .len(),
        1
    );
    assert_eq!(groups.get(&DiagnosticCategory::Security).unwrap().len(), 1);

    let spec_diags = bag.filter_by_category(DiagnosticCategory::Specification);
    assert_eq!(spec_diags.len(), 2);
}

#[test]
fn test_spc0004_and_spc0005_diagnostic_instantiation() {
    // Spc0004
    let diag_0004 = DiagnosticCode::Spc0004
        .to_diagnostic("No relationship found between attributes: [CustomerId, OrderTotal]");
    assert_eq!(diag_0004.code, "spc0004");
    assert_eq!(diag_0004.severity, DiagnosticSeverity::Error);
    assert_eq!(diag_0004.category, DiagnosticCategory::Specification);
    assert_eq!(
        diag_0004.help_text.as_deref(),
        Some("No relationship found between attributes")
    );

    // Spc0005
    let diag_0005 = DiagnosticCode::Spc0005.to_diagnostic(
        "Ambiguous base table for attributes [InvoiceDate]; candidate tables: [Invoice, Order]",
    );
    assert_eq!(diag_0005.code, "spc0005");
    assert_eq!(diag_0005.severity, DiagnosticSeverity::Error);
    assert_eq!(diag_0005.category, DiagnosticCategory::Specification);
    assert_eq!(diag_0005.help_text.as_deref(), Some("Ambiguous base table"));

    let mut bag = DiagnosticBag::new();
    bag.add(diag_0004.with_location(SourceLocation::new("Orders.trn", 5, 1, 10)));
    bag.add(diag_0005);

    assert_eq!(bag.error_count(), 2);
    assert_eq!(bag.warning_count(), 0);

    let rendered = bag.render_text(Some("1\n2\n3\n4\nOrders = 1\n"));
    assert!(rendered.contains("Orders.trn:5:1: error: [spc0004]"));
    assert!(rendered.contains("error: [spc0005]"));

    let json_val = bag.to_json();
    assert_eq!(json_val["diagnostics"][0]["code"], "spc0004");
    assert_eq!(json_val["diagnostics"][1]["code"], "spc0005");
}

#[test]
fn test_spc0006_and_spc0034_diagnostic_instantiation() {
    // Spc0006: Naming category
    let diag_0006 = DiagnosticCode::Spc0006
        .to_diagnostic("Ambiguous object reference 'Product'; qualified name required");
    assert_eq!(diag_0006.code, "spc0006");
    assert_eq!(diag_0006.severity, DiagnosticSeverity::Error);
    assert_eq!(diag_0006.category, DiagnosticCategory::Naming);
    assert_eq!(
        diag_0006.help_text.as_deref(),
        Some("Ambiguous object reference; qualified name required")
    );

    // Spc0034: Database category
    let diag_0034 =
        DiagnosticCode::Spc0034.to_diagnostic("Primary key cannot be updated on table Customer");
    assert_eq!(diag_0034.code, "spc0034");
    assert_eq!(diag_0034.severity, DiagnosticSeverity::Error);
    assert_eq!(diag_0034.category, DiagnosticCategory::Database);
    assert_eq!(
        diag_0034.help_text.as_deref(),
        Some("Primary key cannot be updated")
    );

    let mut bag = DiagnosticBag::new();
    bag.add(diag_0006);
    bag.add_warning(
        DiagnosticCode::Spc0034,
        "Primary key update attempted; skipping update",
        Some(SourceLocation::new("CustomerOps.prc", 8, 3, 12)),
    );

    assert_eq!(bag.error_count(), 1);
    assert_eq!(bag.warning_count(), 1);

    let naming_diags = bag.filter_by_category(DiagnosticCategory::Naming);
    assert_eq!(naming_diags.len(), 1);
    assert_eq!(naming_diags[0].code, "spc0006");

    let db_diags = bag.filter_by_category(DiagnosticCategory::Database);
    assert_eq!(db_diags.len(), 1);
    assert_eq!(db_diags[0].code, "spc0034");

    let rendered = bag.render_text(Some("1\n2\n3\n4\n5\n6\n7\n  UpdatePrimaryKey();\n"));
    assert!(rendered.contains("error: [spc0006]"));
    assert!(rendered.contains("CustomerOps.prc:8:3: warning: [spc0034]"));
    assert!(rendered.contains("  UpdatePrimaryKey();"));
    assert!(rendered.contains("  ^^^^^^^^^^^^"));
}
