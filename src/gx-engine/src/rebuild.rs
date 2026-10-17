//! High-performance parallel Rebuild All pipeline.
//!
//! Replaces the legacy 8-minute GeneXus IDE and MSBuild rebuild workflow by:
//! 1. Specifying the entire Knowledge Base concurrently with scoped threads.
//! 2. Emitting idiomatic C# (.NET) source and SDK-compatible project definitions.
//! 3. Driving multi-core Roslyn builds with structured diagnostic capture.

#![forbid(unsafe_code)]

use crate::{
    catalog::{Catalog, KbObject, ObjectKind},
    csharp::{
        CsprojConfig, CsprojEmitter, ProcedureClass, SdtClass, TransactionClass, WebPanelClass,
    },
    navigation::{
        NavigationReport, compute_extended_tables, specify_procedure_with_tables,
        specify_transaction_attribute_formula,
    },
    table::{SqlDialect, Table, derive_tables_from_catalog, derive_tables_from_transaction},
    transaction::TransactionStructure,
};
use serde::{Deserialize, Serialize};
use std::{
    path::{Path, PathBuf},
    process::Command,
    sync::Mutex,
    time::Instant,
};

/// Configuration options for the Rebuild All pipeline.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RebuildOptions {
    /// Target .NET runtime framework (e.g. "net8.0", "net9.0").
    pub target_framework: String,
    /// Whether to clean the output directory before generating files.
    pub clean: bool,
    /// Concurrency level for parallel phases (0 = automatic based on available cores).
    pub concurrency: usize,
    /// SQL dialect for SQL navigation and table schema derivation.
    pub sql_dialect: SqlDialect,
    /// Whether to abort on the first encountered error.
    pub fail_fast: bool,
    /// Whether to skip invoking `dotnet build` compiler after emission.
    pub skip_compile: bool,
    /// Optional application assembly name.
    pub assembly_name: Option<String>,
}

impl Default for RebuildOptions {
    fn default() -> Self {
        Self {
            target_framework: "net8.0".to_string(),
            clean: true,
            concurrency: 0,
            sql_dialect: SqlDialect::SqlServer,
            fail_fast: false,
            skip_compile: false,
            assembly_name: None,
        }
    }
}

impl RebuildOptions {
    pub fn new(target_framework: impl Into<String>) -> Self {
        Self {
            target_framework: target_framework.into(),
            ..Default::default()
        }
    }

    pub fn with_concurrency(mut self, concurrency: usize) -> Self {
        self.concurrency = concurrency;
        self
    }

    pub fn with_sql_dialect(mut self, dialect: SqlDialect) -> Self {
        self.sql_dialect = dialect;
        self
    }

    pub fn with_clean(mut self, clean: bool) -> Self {
        self.clean = clean;
        self
    }

    pub fn with_fail_fast(mut self, fail_fast: bool) -> Self {
        self.fail_fast = fail_fast;
        self
    }

    pub fn with_skip_compile(mut self, skip: bool) -> Self {
        self.skip_compile = skip;
        self
    }

    pub fn with_assembly_name(mut self, name: impl Into<String>) -> Self {
        self.assembly_name = Some(name.into());
        self
    }
}

/// Structured execution summary and timing report produced by `rebuild_all`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RebuildReport {
    pub read_ms: u64,
    pub spec_ms: u64,
    pub emit_ms: u64,
    pub compile_ms: u64,
    pub total_ms: u64,

    pub procedures_count: usize,
    pub transactions_count: usize,
    pub sdts_count: usize,
    pub webpanels_count: usize,
    pub total_objects: usize,

    pub errors: Vec<String>,
    pub warnings: Vec<String>,
    pub emitted_files: Vec<PathBuf>,
    pub navigation_reports: Vec<NavigationReport>,
}

impl RebuildReport {
    pub fn is_success(&self) -> bool {
        self.errors.is_empty()
    }

    pub fn success(&self) -> bool {
        self.errors.is_empty()
    }
}

/// Categorized catalog objects.
struct ObjectManifest<'a> {
    procedures: Vec<&'a KbObject>,
    transactions: Vec<&'a KbObject>,
    sdts: Vec<&'a KbObject>,
    webpanels: Vec<&'a KbObject>,
    total_objects: usize,
}

/// Executes the end-to-end parallel Rebuild All pipeline.
pub fn rebuild_all(
    catalog: &Catalog,
    output_dir: &Path,
    options: &RebuildOptions,
) -> Result<RebuildReport, String> {
    let total_start = Instant::now();

    // Phase 0: Object Classification
    let (manifest, read_ms) = classify_objects(catalog);

    let worker_concurrency = if options.concurrency == 0 {
        std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(4)
    } else {
        options.concurrency
    };

    let errors = Mutex::new(Vec::new());
    let warnings = Mutex::new(Vec::new());

    // Phase 1: Parallel Specification
    let (nav_reports, spec_ms) = specify_phase(
        &manifest,
        catalog,
        worker_concurrency,
        options,
        &errors,
        &warnings,
    )?;

    if options.fail_fast && !errors.lock().unwrap().is_empty() {
        return Err(errors.lock().unwrap().join("; "));
    }

    // Phase 2: Parallel C# & Project Code Emission
    let (emitted_files, main_csproj_path, emit_ms) =
        emission_phase(&manifest, output_dir, worker_concurrency, options, &errors)?;

    // Phase 3: Parallel Compilation Driver (`dotnet build`)
    let compile_ms = if !options.skip_compile {
        compile_phase(&main_csproj_path, worker_concurrency, &errors, &warnings)
    } else {
        0
    };

    let total_ms = total_start.elapsed().as_millis() as u64;
    let final_errors = errors.into_inner().unwrap();
    let final_warnings = warnings.into_inner().unwrap();

    Ok(RebuildReport {
        read_ms,
        spec_ms,
        emit_ms,
        compile_ms,
        total_ms,
        procedures_count: manifest.procedures.len(),
        transactions_count: manifest.transactions.len(),
        sdts_count: manifest.sdts.len(),
        webpanels_count: manifest.webpanels.len(),
        total_objects: manifest.total_objects,
        errors: final_errors,
        warnings: final_warnings,
        emitted_files,
        navigation_reports: nav_reports,
    })
}

// -----------------------------------------------------------------------------
// Phase Functions
// -----------------------------------------------------------------------------

fn classify_objects(catalog: &Catalog) -> (ObjectManifest<'_>, u64) {
    let read_start = Instant::now();
    let mut manifest = ObjectManifest {
        procedures: Vec::new(),
        transactions: Vec::new(),
        sdts: Vec::new(),
        webpanels: Vec::new(),
        total_objects: 0,
    };

    for obj in catalog.objects() {
        manifest.total_objects += 1;
        match obj.kind {
            ObjectKind::Procedure => manifest.procedures.push(obj),
            ObjectKind::Transaction => manifest.transactions.push(obj),
            ObjectKind::StructuredDataType => manifest.sdts.push(obj),
            ObjectKind::WebPanel => manifest.webpanels.push(obj),
            _ => {}
        }
    }

    let read_ms = read_start.elapsed().as_millis() as u64;
    (manifest, read_ms)
}

fn specify_phase(
    manifest: &ObjectManifest<'_>,
    catalog: &Catalog,
    concurrency: usize,
    options: &RebuildOptions,
    errors: &Mutex<Vec<String>>,
    warnings: &Mutex<Vec<String>>,
) -> Result<(Vec<NavigationReport>, u64), String> {
    let spec_start = Instant::now();

    let tables = match derive_tables_from_catalog(catalog) {
        Ok(t) => t,
        Err(err) => {
            if options.fail_fast {
                return Err(format!("failed to derive physical tables: {err}"));
            }
            errors
                .lock()
                .unwrap()
                .push(format!("physical schema error: {err}"));
            Vec::new()
        }
    };
    let extended_tables = compute_extended_tables(&tables);
    let nav_reports: Mutex<Vec<NavigationReport>> = Mutex::new(Vec::new());

    std::thread::scope(|s| {
        // 1. Procedures parallel specification
        if !manifest.procedures.is_empty() {
            let thread_count = concurrency.min(manifest.procedures.len()).max(1);
            let chunk_size = (manifest.procedures.len() + thread_count - 1) / thread_count;

            for chunk in manifest.procedures.chunks(chunk_size) {
                let tables_ref = &tables;
                let ext_ref = &extended_tables;
                let dialect = options.sql_dialect;
                let reports_ref = &nav_reports;
                let errors_ref = errors;
                let warnings_ref = warnings;

                s.spawn(move || {
                    for proc in chunk {
                        match specify_procedure_with_tables(proc, tables_ref, ext_ref, dialect) {
                            Ok(report) => {
                                if !report.warnings.is_empty() {
                                    warnings_ref.lock().unwrap().extend(report.warnings.clone());
                                }
                                reports_ref.lock().unwrap().push(report);
                            }
                            Err(err) => {
                                errors_ref.lock().unwrap().push(format!(
                                    "Procedure '{}' specification error: {err}",
                                    proc.name
                                ));
                            }
                        }
                    }
                });
            }
        }

        // 2. Transactions formula specification in parallel
        if !manifest.transactions.is_empty() {
            let thread_count = concurrency.min(manifest.transactions.len()).max(1);
            let chunk_size = (manifest.transactions.len() + thread_count - 1) / thread_count;

            for chunk in manifest.transactions.chunks(chunk_size) {
                let all_tables_ref = &tables;
                let dialect = options.sql_dialect;
                let errors_ref = errors;

                s.spawn(move || {
                    for txn in chunk {
                        if let Some(part) = txn.source_parts.iter().find(|p| p.name == "STRUCTURE")
                        {
                            if let Ok(structure) =
                                serde_json::from_str::<TransactionStructure>(&part.source)
                            {
                                let resolve_attr = |name: &str| -> Option<crate::model::DataType> {
                                    let mut visited = std::collections::BTreeSet::new();
                                    crate::kb::resolve_domain_or_attribute_data_type(
                                        catalog,
                                        name,
                                        &mut visited,
                                        0,
                                    )
                                    .ok()
                                };
                                let txn_tables =
                                    derive_tables_from_transaction(&structure, resolve_attr)
                                        .unwrap_or_default();

                                verify_level_formulas(
                                    &structure.root,
                                    &txn_tables,
                                    all_tables_ref,
                                    dialect,
                                    errors_ref,
                                );
                            }
                        }
                    }
                });
            }
        }
    });

    let spec_ms = spec_start.elapsed().as_millis() as u64;
    let final_reports = nav_reports.into_inner().unwrap();
    Ok((final_reports, spec_ms))
}

fn verify_level_formulas(
    level: &crate::transaction::TransactionLevel,
    txn_tables: &[Table],
    all_tables: &[Table],
    dialect: SqlDialect,
    errors: &Mutex<Vec<String>>,
) {
    for attr in &level.attributes {
        if let Some(formula_src) = &attr.formula {
            if let Err(e) = specify_transaction_attribute_formula(
                &attr.name,
                formula_src,
                txn_tables,
                all_tables,
                dialect,
            ) {
                errors
                    .lock()
                    .unwrap()
                    .push(format!("Transaction formula error on '{}': {e}", attr.name));
            }
        }
    }
    for sub in &level.levels {
        verify_level_formulas(sub, txn_tables, all_tables, dialect, errors);
    }
}

fn emit_generated_file(
    out_dir: &Path,
    file_name: &str,
    content: &str,
    files_ref: &Mutex<Vec<PathBuf>>,
    errors_ref: &Mutex<Vec<String>>,
) {
    let file_path = out_dir.join(file_name);
    if let Err(e) = std::fs::write(&file_path, content) {
        errors_ref
            .lock()
            .unwrap()
            .push(format!("failed to write '{}': {e}", file_path.display()));
    } else {
        files_ref.lock().unwrap().push(file_path);
    }
}

fn spawn_parallel_chunks<'scope, 'env: 'scope, T: Sync>(
    items: &'env [T],
    concurrency: usize,
    scope: &'scope std::thread::Scope<'scope, 'env>,
    worker: impl Fn(&'env [T]) + Send + Sync + Copy + 'scope,
) {
    if items.is_empty() {
        return;
    }
    let thread_count = concurrency.min(items.len()).max(1);
    let chunk_size = (items.len() + thread_count - 1) / thread_count;
    for chunk in items.chunks(chunk_size) {
        scope.spawn(move || worker(chunk));
    }
}

fn emission_phase(
    manifest: &ObjectManifest<'_>,
    output_dir: &Path,
    concurrency: usize,
    options: &RebuildOptions,
    errors: &Mutex<Vec<String>>,
) -> Result<(Vec<PathBuf>, PathBuf, u64), String> {
    let emit_start = Instant::now();

    if options.clean && output_dir.exists() {
        let _ = std::fs::remove_dir_all(output_dir);
    }
    std::fs::create_dir_all(output_dir).map_err(|e| {
        format!(
            "cannot create output directory '{}': {e}",
            output_dir.display()
        )
    })?;

    let emitted_files: Mutex<Vec<PathBuf>> = Mutex::new(Vec::new());

    std::thread::scope(|s| {
        // 1. Emit Procedures
        spawn_parallel_chunks(&manifest.procedures, concurrency, s, |chunk| {
            for proc in chunk {
                match ProcedureClass::from_kb_object(proc) {
                    Ok(proc_class) => {
                        let code = proc_class.emit();
                        emit_generated_file(
                            output_dir,
                            &format!("{}.cs", proc.name),
                            &code,
                            &emitted_files,
                            errors,
                        );

                        let mut cfg = if options.target_framework.starts_with("net9") {
                            CsprojConfig::net9(&proc.name, false)
                        } else {
                            CsprojConfig::net8(&proc.name, false)
                        };
                        cfg.compile_includes = vec![format!("{}.cs", proc.name)];
                        let proj_xml = CsprojEmitter::emit(&cfg);
                        emit_generated_file(
                            output_dir,
                            &format!("{}.csproj", proc.name),
                            &proj_xml,
                            &emitted_files,
                            errors,
                        );
                    }
                    Err(e) => {
                        errors.lock().unwrap().push(format!(
                            "failed to generate C# for Procedure '{}': {e}",
                            proc.name
                        ));
                    }
                }
            }
        });

        // 2. Emit Transactions
        spawn_parallel_chunks(&manifest.transactions, concurrency, s, |chunk| {
            for txn in chunk {
                match TransactionClass::from_kb_object(txn) {
                    Ok(txn_class) => {
                        emit_generated_file(
                            output_dir,
                            &format!("{}.cs", txn.name),
                            &txn_class.emit(),
                            &emitted_files,
                            errors,
                        );
                    }
                    Err(e) => {
                        errors.lock().unwrap().push(format!(
                            "failed to generate C# for Transaction '{}': {e}",
                            txn.name
                        ));
                    }
                }
            }
        });

        // 3. Emit SDTs
        spawn_parallel_chunks(&manifest.sdts, concurrency, s, |chunk| {
            for sdt in chunk {
                match SdtClass::from_kb_object(sdt) {
                    Ok(sdt_class) => {
                        emit_generated_file(
                            output_dir,
                            &format!("Sdt{}.cs", sdt.name),
                            &sdt_class.emit(),
                            &emitted_files,
                            errors,
                        );
                    }
                    Err(e) => {
                        errors
                            .lock()
                            .unwrap()
                            .push(format!("failed to generate C# for SDT '{}': {e}", sdt.name));
                    }
                }
            }
        });

        // 4. Emit WebPanels
        spawn_parallel_chunks(&manifest.webpanels, concurrency, s, |chunk| {
            for wp in chunk {
                match WebPanelClass::from_kb_object(wp) {
                    Ok(wp_class) => {
                        emit_generated_file(
                            output_dir,
                            &format!("{}.cs", wp.name),
                            &wp_class.emit(),
                            &emitted_files,
                            errors,
                        );
                    }
                    Err(e) => {
                        errors.lock().unwrap().push(format!(
                            "failed to generate C# for WebPanel '{}': {e}",
                            wp.name
                        ));
                    }
                }
            }
        });
    });

    // 5. Emit Main Unified Project File (.csproj)
    let main_asm_name = options
        .assembly_name
        .clone()
        .unwrap_or_else(|| "RebuildApp".to_string());

    let mut main_cfg = if options.target_framework.starts_with("net9") {
        CsprojConfig::net9(&main_asm_name, false)
    } else {
        CsprojConfig::net8(&main_asm_name, false)
    };

    let mut cs_includes = Vec::new();
    for f in emitted_files.lock().unwrap().iter() {
        if f.extension().and_then(|ext| ext.to_str()) == Some("cs") {
            if let Some(file_name) = f.file_name().and_then(|n| n.to_str()) {
                cs_includes.push(file_name.to_string());
            }
        }
    }
    cs_includes.sort();
    main_cfg.compile_includes = cs_includes;

    let main_csproj_xml = CsprojEmitter::emit(&main_cfg);
    let main_csproj_path = output_dir.join(format!("{main_asm_name}.csproj"));
    std::fs::write(&main_csproj_path, main_csproj_xml).map_err(|e| {
        format!(
            "failed to write main project file '{}': {e}",
            main_csproj_path.display()
        )
    })?;
    emitted_files.lock().unwrap().push(main_csproj_path.clone());

    let emit_ms = emit_start.elapsed().as_millis() as u64;
    let mut final_emitted = emitted_files.into_inner().unwrap();
    final_emitted.sort();

    Ok((final_emitted, main_csproj_path, emit_ms))
}

fn compile_phase(
    main_csproj_path: &Path,
    concurrency: usize,
    errors: &Mutex<Vec<String>>,
    warnings: &Mutex<Vec<String>>,
) -> u64 {
    let compile_start = Instant::now();
    let dotnet_available = Command::new("dotnet").arg("--version").output().is_ok();

    if dotnet_available {
        let mut cmd = Command::new("dotnet");
        cmd.arg("build");
        cmd.arg(main_csproj_path);
        cmd.arg("-c");
        cmd.arg("Release");
        cmd.arg("--nologo");

        if concurrency > 0 {
            cmd.arg(format!("-maxcpucount:{concurrency}"));
        } else {
            cmd.arg("-m");
        }

        match cmd.output() {
            Ok(output) => {
                let stdout = String::from_utf8_lossy(&output.stdout);
                let stderr = String::from_utf8_lossy(&output.stderr);

                for line in stdout.lines().chain(stderr.lines()) {
                    let trimmed = line.trim();
                    if trimmed.contains("error CS") {
                        errors.lock().unwrap().push(trimmed.to_string());
                    } else if trimmed.contains("warning CS") {
                        warnings.lock().unwrap().push(trimmed.to_string());
                    }
                }

                if !output.status.success() && errors.lock().unwrap().is_empty() {
                    warnings.lock().unwrap().push(format!(
                        "dotnet build returned exit code {} (check package restore / runtime availability)",
                        output.status
                    ));
                }
            }
            Err(err) => {
                warnings
                    .lock()
                    .unwrap()
                    .push(format!("cannot invoke dotnet build: {err}"));
            }
        }
    } else {
        warnings
            .lock()
            .unwrap()
            .push("dotnet CLI is not installed; skipped compilation driver".to_string());
    }

    compile_start.elapsed().as_millis() as u64
}
