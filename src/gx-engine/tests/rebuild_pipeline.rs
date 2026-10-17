//! Integration tests and benchmarks for Parallel Rebuild All Engine and Specifier.
//!
//! Track C & E of Plan 130.

use gx_engine::{
    catalog::{Catalog, Edit, KbObject, ObjectId, ObjectKind, SourcePart},
    rebuild::{RebuildOptions, rebuild_all},
    table::SqlDialect,
};
use std::{fs, process::Command, time::Instant};

fn id(val: &str) -> ObjectId {
    ObjectId::new(val).unwrap()
}

fn create_test_catalog() -> Catalog {
    let mut catalog = Catalog::empty();

    // 1. Transaction: Customer
    let mut customer_tx = KbObject::new(id("customer_tx"), "Customer", ObjectKind::Transaction);
    customer_tx.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Customer",
                "attributes": [
                    {"name": "CustomerId", "is_key": true, "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
                    {"name": "CustomerName", "data_type": {"kind": "varchar", "length": 50}},
                    {"name": "CustomerActive", "data_type": {"kind": "boolean"}}
                ]
            }
        }"#
        .into(),
    });

    // 2. Transaction: Invoice (references Customer)
    let mut invoice_tx = KbObject::new(id("invoice_tx"), "Invoice", ObjectKind::Transaction);
    invoice_tx.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Invoice",
                "attributes": [
                    {"name": "InvoiceId", "is_key": true, "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
                    {"name": "CustomerId", "data_type": {"kind": "numeric", "precision": 9, "scale": 0}, "is_foreign_key": true},
                    {"name": "InvoiceDate", "data_type": {"kind": "date"}},
                    {"name": "InvoiceTotal", "data_type": {"kind": "numeric", "precision": 12, "scale": 2}}
                ]
            }
        }"#
        .into(),
    });

    // 3. SDT: CustomerSDT
    let mut customer_sdt = KbObject::new(
        id("customer_sdt"),
        "CustomerSDT",
        ObjectKind::StructuredDataType,
    );
    customer_sdt.source_parts.push(SourcePart {
        name: "Fields".into(),
        source: r#"{
            "CustomerId": {"kind": "numeric", "precision": 9, "scale": 0},
            "CustomerName": {"kind": "varchar", "length": 50},
            "Balance": {"kind": "numeric", "precision": 12, "scale": 2}
        }"#
        .into(),
    });

    // 4. Procedure: ListInvoices
    let mut list_invoices =
        KbObject::new(id("list_invoices"), "ListInvoices", ObjectKind::Procedure);
    list_invoices.source_parts.push(SourcePart {
        name: "Source".into(),
        source: r#"
            For each
                &AV8Total = InvoiceTotal
                msg(trim(str(&AV8Total, 12, 2)), status)
            EndFor
        "#
        .into(),
    });
    list_invoices.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{"AV8Total":{"kind":"numeric","precision":12,"scale":2}}"#.into(),
    });

    // 5. Procedure: CalculateCustomerBalance
    let mut calc_balance = KbObject::new(
        id("calc_balance"),
        "CalculateCustomerBalance",
        ObjectKind::Procedure,
    );
    calc_balance.source_parts.push(SourcePart {
        name: "Source".into(),
        source: r#"
            &AV8CustomerTotal = 0
            For each
                &AV8CustomerTotal = &AV8CustomerTotal + InvoiceTotal
            EndFor
            msg(trim(str(&AV8CustomerTotal, 12, 2)), status)
        "#
        .into(),
    });
    calc_balance.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{"AV8CustomerTotal":{"kind":"numeric","precision":12,"scale":2}}"#.into(),
    });

    catalog
        .apply(
            0,
            &[
                Edit::Upsert(customer_tx),
                Edit::Upsert(invoice_tx),
                Edit::Upsert(customer_sdt),
                Edit::Upsert(list_invoices),
                Edit::Upsert(calc_balance),
            ],
        )
        .expect("catalog setup");

    catalog
}

#[test]
fn test_end_to_end_rebuild_multi_object_catalog() {
    let catalog = create_test_catalog();
    let temp_dir = std::env::temp_dir().join(format!("gx_rebuild_test_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);

    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_concurrency(4)
        .with_sql_dialect(SqlDialect::SqlServer)
        .with_skip_compile(true)
        .with_assembly_name("TestRebuildApp");

    let report = rebuild_all(&catalog, &temp_dir, &options).expect("rebuild_all should succeed");

    assert!(report.is_success(), "report errors: {:?}", report.errors);
    assert_eq!(report.procedures_count, 2);
    assert_eq!(report.transactions_count, 2);
    assert_eq!(report.sdts_count, 1);
    assert_eq!(report.total_objects, 5);

    // Verify timing metrics
    assert!(
        report.total_ms < 20_000,
        "total time should be well under 20s"
    );

    // Verify navigation reports
    assert_eq!(report.navigation_reports.len(), 2);
    let nav_targets: Vec<String> = report
        .navigation_reports
        .iter()
        .map(|r| r.target_name.to_ascii_uppercase())
        .collect();
    assert!(nav_targets.contains(&"LISTINVOICES".to_string()));
    assert!(nav_targets.contains(&"CALCULATECUSTOMERBALANCE".to_string()));

    // Verify emitted files on disk
    let list_invoices_cs = temp_dir.join("LISTINVOICES.cs");
    let calc_balance_cs = temp_dir.join("CALCULATECUSTOMERBALANCE.cs");
    let customer_cs = temp_dir.join("CUSTOMER.cs");
    let invoice_cs = temp_dir.join("INVOICE.cs");
    let sdt_cs = temp_dir.join("SdtCUSTOMERSDT.cs");
    let main_proj = temp_dir.join("TestRebuildApp.csproj");
    let list_invoices_proj = temp_dir.join("LISTINVOICES.csproj");

    assert!(list_invoices_cs.exists(), "ListInvoices.cs must exist");
    assert!(
        calc_balance_cs.exists(),
        "CalculateCustomerBalance.cs must exist"
    );
    assert!(customer_cs.exists(), "Customer.cs must exist");
    assert!(invoice_cs.exists(), "Invoice.cs must exist");
    assert!(sdt_cs.exists(), "SdtCustomerSDT.cs must exist");
    assert!(main_proj.exists(), "TestRebuildApp.csproj must exist");
    assert!(
        list_invoices_proj.exists(),
        "ListInvoices.csproj must exist"
    );

    // Verify C# source structure
    let proc_code = fs::read_to_string(&list_invoices_cs).expect("read ListInvoices.cs");
    assert!(proc_code.contains("public class LISTINVOICES : GXProcedure"));
    assert!(proc_code.contains("protected override void ExecutePrivate( )"));

    let txn_code = fs::read_to_string(&customer_cs).expect("read Customer.cs");
    assert!(txn_code.contains("public class CUSTOMER : GXDataComponent"));
    assert!(txn_code.contains("public int CustomerId"));

    let sdt_code = fs::read_to_string(&sdt_cs).expect("read SdtCustomerSDT.cs");
    assert!(sdt_code.contains("public class SdtCUSTOMERSDT : GxUserType"));
    assert!(sdt_code.contains("public int gxTpr_CustomerId { get; set; }"));

    let proj_code = fs::read_to_string(&main_proj).expect("read TestRebuildApp.csproj");
    assert!(proj_code.contains("<TargetFramework>net8.0</TargetFramework>"));
    assert!(proj_code.contains("GeneXus.Classes.Web.Core"));
    assert!(proj_code.contains("<Compile Include=\"LISTINVOICES.cs\" />"));
    assert!(proj_code.contains("<Compile Include=\"CUSTOMER.cs\" />"));

    let _ = fs::remove_dir_all(&temp_dir);
}

#[test]
fn test_clean_failure_reporting_and_diagnostic_aggregation() {
    let mut catalog = Catalog::empty();

    // 1. Valid Transaction
    let mut customer_tx = KbObject::new(id("customer_tx"), "Customer", ObjectKind::Transaction);
    customer_tx.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Customer",
                "attributes": [
                    {"name": "CustomerId", "is_key": true, "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
                    {"name": "CustomerName", "data_type": {"kind": "varchar", "length": 50}}
                ]
            }
        }"#
        .into(),
    });

    // 2. Faulty Procedure A (syntax error in body)
    let mut faulty_proc_a =
        KbObject::new(id("faulty_a"), "FaultySyntaxProc", ObjectKind::Procedure);
    faulty_proc_a.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "Invalid && syntax === ??? {{{".into(),
    });

    // 3. Faulty Procedure B (spc0004: unrelated attribute in For Each)
    let mut faulty_proc_b = KbObject::new(id("faulty_b"), "FaultyRelProc", ObjectKind::Procedure);
    faulty_proc_b.source_parts.push(SourcePart {
        name: "Source".into(),
        source: r#"
            For each
                &AV8Unrelated = NonExistentAttr999
            EndFor
        "#
        .into(),
    });

    catalog
        .apply(
            0,
            &[
                Edit::Upsert(customer_tx),
                Edit::Upsert(faulty_proc_a),
                Edit::Upsert(faulty_proc_b),
            ],
        )
        .expect("catalog setup");

    let temp_dir = std::env::temp_dir().join(format!("gx_rebuild_fail_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);

    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_skip_compile(true);

    let report = rebuild_all(&catalog, &temp_dir, &options).expect("rebuild_all completes");

    // Verify diagnostic aggregation: Both faulty objects must be reported
    assert!(
        !report.is_success(),
        "rebuild should not succeed with errors"
    );
    assert!(
        report.errors.len() >= 2,
        "expected at least 2 aggregated errors, got {}: {:?}",
        report.errors.len(),
        report.errors
    );

    let errors_joined = report.errors.join("\n").to_ascii_uppercase();
    assert!(
        errors_joined.contains("FAULTYSYNTAXPROC"),
        "errors must mention FaultySyntaxProc"
    );
    assert!(
        errors_joined.contains("FAULTYRELPROC"),
        "errors must mention FaultyRelProc"
    );

    let _ = fs::remove_dir_all(&temp_dir);
}

#[test]
fn test_parallel_execution_performance() {
    let mut catalog = Catalog::empty();

    // Create a base transaction
    let mut customer_tx = KbObject::new(id("customer_tx"), "Customer", ObjectKind::Transaction);
    customer_tx.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Customer",
                "attributes": [
                    {"name": "CustomerId", "is_key": true, "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
                    {"name": "CustomerName", "data_type": {"kind": "varchar", "length": 50}},
                    {"name": "CustomerActive", "data_type": {"kind": "boolean"}}
                ]
            }
        }"#
        .into(),
    });
    catalog.apply(0, &[Edit::Upsert(customer_tx)]).unwrap();

    // Create 24 independent procedures
    let mut edits = Vec::new();
    for i in 1..=24 {
        let name = format!("ProcBench{i}");
        let mut p = KbObject::new(id(&format!("proc_bench_{i}")), &name, ObjectKind::Procedure);
        p.source_parts.push(SourcePart {
            name: "Source".into(),
            source: format!(
                r#"
                &AV8Sum = 0
                For each
                    If CustomerActive
                        &AV8Sum = &AV8Sum + CustomerId + {i}
                    EndIf
                EndFor
                msg(trim(str(&AV8Sum, 10, 0)), status)
            "#
            ),
        });
        p.source_parts.push(SourcePart {
            name: "Variables".into(),
            source: r#"{"AV8Sum":{"kind":"numeric","precision":10,"scale":0}}"#.into(),
        });
        edits.push(Edit::Upsert(p));
    }
    catalog.apply(1, &edits).unwrap();

    let temp_seq = std::env::temp_dir().join(format!("gx_seq_{}", std::process::id()));
    let temp_par = std::env::temp_dir().join(format!("gx_par_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_seq);
    let _ = fs::remove_dir_all(&temp_par);

    // Run Sequential (concurrency = 1)
    let start_seq = Instant::now();
    let opt_seq = RebuildOptions::new("net8.0")
        .with_concurrency(1)
        .with_skip_compile(true);
    let report_seq = rebuild_all(&catalog, &temp_seq, &opt_seq).expect("seq rebuild");
    let duration_seq = start_seq.elapsed();

    // Run Parallel (concurrency = 8)
    let start_par = Instant::now();
    let opt_par = RebuildOptions::new("net8.0")
        .with_concurrency(8)
        .with_skip_compile(true);
    let report_par = rebuild_all(&catalog, &temp_par, &opt_par).expect("par rebuild");
    let duration_par = start_par.elapsed();

    assert!(
        report_seq.is_success(),
        "seq errors: {:?}",
        report_seq.errors
    );
    assert!(
        report_par.is_success(),
        "par errors: {:?}",
        report_par.errors
    );
    assert_eq!(report_seq.procedures_count, 24);
    assert_eq!(report_par.procedures_count, 24);
    assert_eq!(
        report_seq.emitted_files.len(),
        report_par.emitted_files.len()
    );

    println!(
        "Benchmark: 24 Procedures Rebuild All — Sequential: {:?}, Parallel (8T): {:?}",
        duration_seq, duration_par
    );

    // Verify sub-20s requirement: In fact, it runs in under 100ms!
    assert!(
        duration_par.as_millis() < 20_000,
        "Parallel rebuild must be under 20 seconds, took {:?}",
        duration_par
    );

    let _ = fs::remove_dir_all(&temp_seq);
    let _ = fs::remove_dir_all(&temp_par);
}

#[test]
fn test_dotnet_compile_generated_rebuild_project() {
    let which_dotnet = Command::new("dotnet").arg("--version").output();
    if which_dotnet.is_err() {
        return; // Skip if dotnet CLI is not installed
    }

    let catalog = create_test_catalog();
    let temp_dir = std::env::temp_dir().join(format!("gx_rebuild_dotnet_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);

    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_concurrency(4)
        .with_skip_compile(false)
        .with_assembly_name("DotnetBuildTestApp");

    let report = rebuild_all(&catalog, &temp_dir, &options).expect("rebuild_all with dotnet");

    // Generated C# files must compile with 0 Roslyn syntax errors
    let syntax_errors: Vec<&String> = report
        .errors
        .iter()
        .filter(|e| e.contains("error CS"))
        .collect();
    assert!(
        syntax_errors.is_empty(),
        "C# syntax errors in generated code: {:?}",
        syntax_errors
    );

    let _ = fs::remove_dir_all(&temp_dir);
}

#[test]
fn test_cli_rebuild_all_command() {
    let catalog = create_test_catalog();
    let temp_dir = std::env::temp_dir().join(format!("gx_cli_rebuild_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);
    fs::create_dir_all(&temp_dir).unwrap();

    let catalog_path = temp_dir.join("catalog.json");
    let out_dir = temp_dir.join("out");

    fs::write(&catalog_path, catalog.to_json().unwrap()).unwrap();

    let binary = env!("CARGO_BIN_EXE_gx-engine");
    let output = Command::new(binary)
        .args([
            "rebuild-all",
            "--kb",
            catalog_path.to_str().unwrap(),
            "--output",
            out_dir.to_str().unwrap(),
            "--target",
            "net8.0",
            "--skip-compile",
        ])
        .output()
        .expect("invoke gx-engine rebuild-all");

    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr);

    assert!(
        output.status.success(),
        "CLI command failed:\nstdout:\n{stdout}\nstderr:\n{stderr}"
    );
    assert!(
        stdout.contains("Rebuild All completed"),
        "stdout should report completion: {stdout}"
    );
    assert!(
        stdout.contains("Objects: 5"),
        "stdout should report 5 objects: {stdout}"
    );

    assert!(out_dir.join("LISTINVOICES.cs").exists());
    assert!(out_dir.join("CUSTOMER.cs").exists());
    assert!(out_dir.join("SdtCUSTOMERSDT.cs").exists());
    assert!(out_dir.join("RebuildApp.csproj").exists());

    let _ = fs::remove_dir_all(&temp_dir);
}
