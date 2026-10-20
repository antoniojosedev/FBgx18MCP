//! Integration tests and benchmarks for Parallel Rebuild All Engine and Specifier.
//!
//! Track C & E of Plan 130.

use gx_engine::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    rebuild::{RebuildOptions, RebuildStatus, rebuild_all},
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
    // The portable structure the reader holds for an SDT (see `stored_sdt::PortableSdt`).
    customer_sdt.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{"fields": [
            {"node": "field", "name": "CustomerId", "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
            {"node": "field", "name": "CustomerName", "data_type": {"kind": "varchar", "length": 50}},
            {"node": "field", "name": "Balance", "data_type": {"kind": "numeric", "precision": 12, "scale": 2}}
        ]}"#
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
    // The KB's cross-reference rows record that ListInvoices reads Invoice's attributes.
    // The SDT is built only because a generated object reaches it (see `gx_engine::reachability`).
    list_invoices.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("customer_sdt"),
    });
    list_invoices.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("invoice_tx"),
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
    calc_balance.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: id("invoice_tx"),
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

/// One Procedure with no web UI dependency.
fn hello_procedure() -> KbObject {
    let mut hello = KbObject::new(id("hello"), "Hello", ObjectKind::Procedure);
    hello.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "msg('hi', status)".into(),
    });
    hello
}

/// Hello plus the Customer Transaction. The Transaction is web UI: skipped, so the run is partial.
fn hello_and_customer_catalog() -> Catalog {
    let mut customer = KbObject::new(id("customer_tx"), "Customer", ObjectKind::Transaction);
    customer.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{"root":{"name":"Customer","attributes":[{"name":"CustomerId","is_key":true,"data_type":{"kind":"numeric","precision":9,"scale":0}}]}}"#.into(),
    });
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[Edit::Upsert(hello_procedure()), Edit::Upsert(customer)],
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

    // Transactions outside a matched form are web UI: skipped by name, with the reason. The two Procedures need the Invoice Transaction,
    // so they are refused: each is an error, and the run is failed, never success.
    assert_eq!(report.status, RebuildStatus::Failed);
    assert!(
        !report.is_success(),
        "a refused rebuild must not report success"
    );
    assert_eq!(report.procedures_count, 2);
    assert_eq!(report.transactions_count, 2);
    assert_eq!(report.sdts_count, 1);
    assert_eq!(report.total_objects, 5);

    assert_eq!(report.skipped_count, 2);
    let mut skipped_names: Vec<String> = report
        .skipped_objects
        .iter()
        .map(|skipped| skipped.name.to_ascii_uppercase())
        .collect();
    skipped_names.sort();
    assert_eq!(skipped_names, vec!["CUSTOMER", "INVOICE"]);
    assert!(
        report
            .skipped_objects
            .iter()
            .all(|skipped| skipped.kind == "Transaction"
                && skipped.reason.starts_with("web UI, not generated: ")),
        "every skipped Transaction carries its kind and reason: {:?}",
        report.skipped_objects
    );

    assert_eq!(report.refused_count, 2);
    let mut refused_names: Vec<String> = report
        .refused_objects
        .iter()
        .map(|refused| refused.name.to_ascii_uppercase())
        .collect();
    refused_names.sort();
    assert_eq!(
        refused_names,
        vec!["CALCULATECUSTOMERBALANCE", "LISTINVOICES"]
    );
    assert_eq!(
        report.errors.len(),
        2,
        "one error per refused Procedure: {:?}",
        report.errors
    );
    assert!(
        report
            .errors
            .iter()
            .all(|error| error.contains("refused to build Procedure")
                && error.contains("web UI, not generated")),
        "each refusal names its reason: {:?}",
        report.errors
    );

    // Verify timing metrics
    assert!(
        report.total_ms < 20_000,
        "total time should be well under 20s"
    );

    // Refused Procedures are not specified and not emitted; skipped Transactions are not compile
    // units. The SDT is the one object that is emitted.
    assert!(
        report.navigation_reports.is_empty(),
        "refused Procedures are not specified"
    );
    for absent in [
        "LISTINVOICES.cs",
        "CALCULATECUSTOMERBALANCE.cs",
        "CUSTOMER.cs",
        "INVOICE.cs",
    ] {
        assert!(
            !temp_dir.join(absent).exists(),
            "{absent} must not be emitted"
        );
    }
    let sdt_cs = temp_dir.join("SdtCustomerSDT.cs");
    let main_proj = temp_dir.join("TestRebuildApp.csproj");
    assert!(sdt_cs.exists(), "SdtCustomerSDT.cs must exist");
    assert!(main_proj.exists(), "TestRebuildApp.csproj must exist");

    let sdt_code = fs::read_to_string(&sdt_cs).expect("read SdtCustomerSDT.cs");
    assert!(sdt_code.contains("public class SdtCustomerSDT : GxUserType"));
    // The generator keeps the first letter and lower-cases the rest in a member name (every
    // `gxTpr_` member of the KBTeste SDK output follows this rule; none has an inner capital).
    // The SDK shape: a backing field that holds the default, and a getter and a setter over it
    // (type_SdtRSN03944700.cs).
    assert!(sdt_code.contains("public int gxTpr_Customerid"));
    assert!(sdt_code.contains("protected int gxTv_SdtCustomerSDT_Customerid = 0;"));

    let proj_code = fs::read_to_string(&main_proj).expect("read TestRebuildApp.csproj");
    assert!(proj_code.contains("<TargetFramework>net8.0</TargetFramework>"));
    assert!(proj_code.contains("GeneXus.Classes.Web.Core"));
    assert!(
        !proj_code.contains("LISTINVOICES.cs"),
        "a refused Procedure is not a Compile item"
    );
    assert!(
        !proj_code.contains("CUSTOMER.cs"),
        "a skipped Transaction is not a Compile item"
    );

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

    // The benchmark catalog has a Transaction (web UI, skipped), so each run is partial. The
    // Procedures must build with no refusal and no error, sequentially and in parallel.
    assert!(
        report_seq.errors.is_empty(),
        "seq errors: {:?}",
        report_seq.errors
    );
    assert!(
        report_par.errors.is_empty(),
        "par errors: {:?}",
        report_par.errors
    );
    assert_eq!(report_seq.status, RebuildStatus::Partial);
    assert_eq!(report_par.status, RebuildStatus::Partial);
    assert_eq!(report_seq.refused_count, 0);
    assert_eq!(report_par.refused_count, 0);
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

    // Hello builds; the Customer Transaction is web UI and is skipped, so the run is partial.
    let catalog = hello_and_customer_catalog();
    let temp_dir = std::env::temp_dir().join(format!("gx_rebuild_dotnet_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);

    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_concurrency(4)
        .with_skip_compile(false)
        .with_assembly_name("DotnetBuildTestApp");

    let report = rebuild_all(&catalog, &temp_dir, &options).expect("rebuild_all with dotnet");

    // The generated project must build: no Roslyn error and no MSBuild error (a project
    // that fails to load, or a restore that fails, is a build error too).
    assert!(
        report.errors.is_empty(),
        "generated project does not build: {:?}",
        report.errors
    );
    assert_eq!(report.status, RebuildStatus::Partial);
    assert_eq!(report.skipped_count, 1);
    assert!(report.generated_count >= 1, "Hello is generated");
    assert_eq!(
        report.compiled_count, report.generated_count,
        "a project that built compiled every unit it generated"
    );

    let _ = fs::remove_dir_all(&temp_dir);
}

#[test]
fn a_procedure_catalog_without_web_ui_builds_complete() {
    if Command::new("dotnet").arg("--version").output().is_err() {
        return; // Skip if dotnet CLI is not installed
    }

    let mut catalog = Catalog::empty();
    catalog
        .apply(0, &[Edit::Upsert(hello_procedure())])
        .expect("catalog setup");
    let temp_dir = std::env::temp_dir().join(format!("gx_rebuild_complete_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);

    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_concurrency(2)
        .with_skip_compile(false)
        .with_assembly_name("CompleteBuildApp");
    let report = rebuild_all(&catalog, &temp_dir, &options).expect("rebuild_all with dotnet");

    assert!(
        report.errors.is_empty(),
        "does not build: {:?}",
        report.errors
    );
    assert_eq!(report.status, RebuildStatus::Complete);
    assert!(report.is_success());
    assert_eq!(report.skipped_count, 0);
    assert_eq!(report.not_generated_count, 0);
    assert_eq!(report.refused_count, 0);
    // A procedure without parameters is two compile units, as the SDK writes it: its class `ahello`, and
    // the lower-case wrapper `hello` that starts it through ClassLoader.
    assert_eq!(report.generated_count, 2);
    assert_eq!(report.compiled_count, 2);
    let code = fs::read_to_string(temp_dir.join("ahello.cs")).expect("read ahello.cs");
    assert!(code.contains("public class ahello : GXProcedure"));
    let wrapper = fs::read_to_string(temp_dir.join("hello.cs")).expect("read hello.cs");
    assert!(wrapper.contains("ClassLoader.Execute(\"ahello\""));

    let _ = fs::remove_dir_all(&temp_dir);
}

#[test]
fn test_cli_rebuild_all_command() {
    let catalog = hello_and_customer_catalog();
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

    // A skipped Transaction makes the run partial: exit 3, never 0.
    assert_eq!(
        output.status.code(),
        Some(3),
        "a partial rebuild exits 3:\nstdout:\n{stdout}\nstderr:\n{stderr}"
    );
    assert!(
        stdout.contains("Rebuild All completed"),
        "stdout should report completion: {stdout}"
    );
    assert!(
        stdout.contains("Objects: 2"),
        "stdout should report 2 objects: {stdout}"
    );
    assert!(
        stdout.contains("Status: partial"),
        "stdout should report partial: {stdout}"
    );
    assert!(
        stdout
            .to_ascii_uppercase()
            .contains("[SKIPPED] TRANSACTION 'CUSTOMER': WEB UI, NOT GENERATED"),
        "the skipped Transaction is listed by name and reason: {stdout}"
    );

    assert!(out_dir.join("HELLO.cs").exists());
    assert!(!out_dir.join("CUSTOMER.cs").exists());
    assert!(out_dir.join("RebuildApp.csproj").exists());

    let _ = fs::remove_dir_all(&temp_dir);
}

#[test]
fn test_cli_rebuild_all_refusal_exits_1() {
    // A Procedure that needs a Transaction is refused: the run fails with exit 1.
    let catalog = create_test_catalog();
    let temp_dir = std::env::temp_dir().join(format!("gx_cli_refused_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);
    fs::create_dir_all(&temp_dir).unwrap();

    let catalog_path = temp_dir.join("catalog.json");
    let out_dir = temp_dir.join("out");
    fs::write(&catalog_path, catalog.to_json().unwrap()).unwrap();

    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
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
    assert_eq!(
        output.status.code(),
        Some(1),
        "a refused Procedure fails the run with exit 1:\nstdout:\n{stdout}\nstderr:\n{stderr}"
    );
    assert!(
        stdout.contains("refused to build Procedure"),
        "the refusal is reported: {stdout}"
    );
    assert!(!out_dir.join("LISTINVOICES.cs").exists());

    let _ = fs::remove_dir_all(&temp_dir);
}
