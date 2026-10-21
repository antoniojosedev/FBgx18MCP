//! Phase 1: specifies every Procedure's navigation and verifies every Transaction's formulas.

use super::{
    RebuildOptions,
    classify::ObjectManifest,
    parallel::{Findings, map_chunks},
};
use crate::{
    catalog::{Catalog, KbObject, ObjectKind},
    csharp::webform,
    navigation::{
        ExtendedTable, NavigationReport, compute_extended_tables, specify_procedure_with_tables,
        specify_transaction_attribute_formula,
    },
    table::{SqlDialect, Table, derive_tables_from_transaction, derive_tables_partial},
    transaction::{TransactionLevel, TransactionStructure},
};
use std::{
    collections::{BTreeMap, BTreeSet},
    time::Instant,
};

pub(super) fn specify_phase(
    manifest: &ObjectManifest<'_>,
    catalog: &Catalog,
    concurrency: usize,
    options: &RebuildOptions,
    findings: &mut Findings,
) -> Result<(Vec<NavigationReport>, u64), String> {
    let spec_start = Instant::now();

    // Each Transaction derives or is refused on its own. A refused one is an error naming it, and the tables
    // of the others are still derived, so the Procedures that do not use it are specified.
    let (tables, physical_failures) = derive_tables_partial(catalog);
    if options.fail_fast {
        if let Some(err) = physical_failures.first() {
            return Err(format!("failed to derive physical tables: {err}"));
        }
    }
    findings.errors.extend(
        physical_failures
            .iter()
            .map(|err| format!("physical schema error: {err}")),
    );
    let extended_tables = compute_extended_tables(&tables);
    let dialect = options.sql_dialect;

    let mut reports = Vec::new();
    for (chunk_reports, chunk_findings) in map_chunks(&manifest.procedures, concurrency, |chunk| {
        specify_procedures(chunk, &tables, &extended_tables, dialect)
    }) {
        reports.extend(chunk_reports);
        findings.merge(chunk_findings);
    }

    // A Transaction whose web class is emitted checks its referenced keys there.
    let web_checked: BTreeSet<String> = manifest
        .webpanels
        .iter()
        .filter_map(|class| match class {
            webform::WebClass::Transaction(form) => Some(form.name.clone()),
            webform::WebClass::WorkWith(class) => class.transaction_name().map(str::to_string),
            _ => None,
        })
        .collect();
    for chunk_findings in map_chunks(&manifest.transactions, concurrency, |chunk| {
        verify_transactions(chunk, catalog, &tables, &web_checked, dialect)
    }) {
        findings.merge(chunk_findings);
    }

    Ok((reports, spec_start.elapsed().as_millis() as u64))
}

fn specify_procedures(
    procedures: &[&KbObject],
    tables: &[Table],
    extended_tables: &BTreeMap<String, ExtendedTable>,
    dialect: SqlDialect,
) -> (Vec<NavigationReport>, Findings) {
    let mut reports = Vec::new();
    let mut findings = Findings::default();
    for proc in procedures {
        // Admitted DataProviders have no database navigation: their typed AST was
        // validated during classification and uses the checked Procedure emitter.
        if proc.kind == ObjectKind::DataProvider {
            continue;
        }
        match specify_procedure_with_tables(proc, tables, extended_tables, dialect) {
            Ok(report) => {
                findings.warnings.extend(report.warnings.iter().cloned());
                reports.push(report);
            }
            Err(err) => findings.errors.push(format!(
                "Procedure '{}' specification error: {err}",
                proc.name
            )),
        }
    }
    (reports, findings)
}

/// Verifies the formulas of each Transaction and warns about what its STRUCTURE part leaves unchecked.
fn verify_transactions(
    transactions: &[&KbObject],
    catalog: &Catalog,
    all_tables: &[Table],
    web_checked: &BTreeSet<String>,
    dialect: SqlDialect,
) -> Findings {
    let mut findings = Findings::default();
    for txn in transactions {
        let Some(part) = txn.source_parts.iter().find(|p| p.name == "STRUCTURE") else {
            continue;
        };
        let Ok(structure) = serde_json::from_str::<TransactionStructure>(&part.source) else {
            // Not a silent skip: the STRUCTURE part is not the JSON form
            // this reader parses, so formula verification did not run.
            findings.warnings.push(format!(
                "Transaction '{}': STRUCTURE part is not the JSON form this build reads; its formula verification was skipped",
                txn.name
            ));
            continue;
        };
        if !structure.key_references.is_empty() {
            findings
                .warnings
                .push(key_references_warning(&txn.name, web_checked));
        }
        let resolve_attr = |name: &str| -> Option<crate::model::DataType> {
            let mut visited = BTreeSet::new();
            crate::kb::resolve_domain_or_attribute_data_type(catalog, name, &mut visited, 0).ok()
        };
        let txn_tables =
            derive_tables_from_transaction(&structure, resolve_attr).unwrap_or_default();
        verify_level_formulas(
            &structure.root,
            &txn_tables,
            all_tables,
            dialect,
            &mut findings,
        );
    }
    findings
}

fn key_references_warning(transaction: &str, web_checked: &BTreeSet<String>) -> String {
    if web_checked.contains(transaction) {
        format!(
            "Transaction '{transaction}': its web class checks its referenced keys, but foreign keys are metadata only, and no DDL is emitted or applied by rebuild-all"
        )
    } else {
        format!(
            "Transaction '{transaction}': key-existence checks are not generated; foreign keys are metadata only, and no DDL is emitted or applied by rebuild-all"
        )
    }
}

fn verify_level_formulas(
    level: &TransactionLevel,
    txn_tables: &[Table],
    all_tables: &[Table],
    dialect: SqlDialect,
    findings: &mut Findings,
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
                findings
                    .errors
                    .push(format!("Transaction formula error on '{}': {e}", attr.name));
            }
        }
    }
    for sub in &level.levels {
        verify_level_formulas(sub, txn_tables, all_tables, dialect, findings);
    }
}
