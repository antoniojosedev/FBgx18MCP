//! Database schema reorganization, migration diffing, and drift impact analysis.
//! Computes structural diffs between physical schema revisions and generates multi-dialect migration scripts with zero SQL writes.

use crate::table::{SqlDialect, Table, TableColumn, TableForeignKey, TableIndex};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

/// Convenience type alias for `TableColumn` conforming to the reorg module contract.
pub type Column = TableColumn;

/// Action performed on a physical table during schema reorganization.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum TableAction {
    Create,
    Drop,
    Alter,
    Unchanged,
}

/// Structural difference detected on a table column.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum ColumnDiff {
    Add(TableColumn),
    Drop(String),
    Alter { old: TableColumn, new: TableColumn },
}

/// Detailed schema differences detected on a specific physical table.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TableDiff {
    pub table_name: String,
    pub action: TableAction,
    pub column_diffs: Vec<ColumnDiff>,
    pub added_foreign_keys: Vec<TableForeignKey>,
    pub dropped_foreign_keys: Vec<String>,
    pub added_indexes: Vec<TableIndex>,
    pub dropped_indexes: Vec<String>,
}

/// Complete report of database schema reorganization and multi-dialect migration scripts.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReorgImpactReport {
    pub tables: Vec<TableDiff>,
    pub risk_warnings: Vec<String>,
    pub sql_scripts: BTreeMap<String, Vec<String>>,
}

impl ReorgImpactReport {
    /// Returns the generated SQL script for the given dialect, or an empty slice if not present.
    pub fn script_for_dialect(&self, dialect: SqlDialect) -> &[String] {
        let key = dialect_to_key(dialect);
        self.sql_scripts
            .get(key)
            .map(|v| v.as_slice())
            .unwrap_or(&[])
    }

    /// Returns `true` if any risk warnings are present in the report.
    pub fn has_risks(&self) -> bool {
        !self.risk_warnings.is_empty()
    }
}

/// Maps a `SqlDialect` to its canonical key in `sql_scripts`.
pub fn dialect_to_key(dialect: SqlDialect) -> &'static str {
    match dialect {
        SqlDialect::Ansi => "ansi",
        SqlDialect::SqlServer => "sqlserver",
        SqlDialect::PostgreSql => "postgresql",
        SqlDialect::Oracle => "oracle",
    }
}

const ALL_DIALECTS: [(SqlDialect, &str); 4] = [
    (SqlDialect::Ansi, "ansi"),
    (SqlDialect::SqlServer, "sqlserver"),
    (SqlDialect::PostgreSql, "postgresql"),
    (SqlDialect::Oracle, "oracle"),
];

/// Compares `previous_tables` with `current_tables` and generates a structured
/// reorganization impact report along with migration scripts for all supported SQL dialects.
pub fn diff_schemas(
    previous_tables: &[Table],
    current_tables: &[Table],
    _dialect: SqlDialect,
) -> ReorgImpactReport {
    let mut tables = Vec::new();
    let mut risk_warnings = Vec::new();

    // 1. Process all tables in `current_tables`
    for curr in current_tables {
        if let Some(prev) = previous_tables
            .iter()
            .find(|t| t.name.eq_ignore_ascii_case(&curr.name))
        {
            // Table exists in both schemas: detect changes
            let mut column_diffs = Vec::new();

            // Check for added or altered columns
            for new_col in &curr.columns {
                if let Some(old_col) = prev
                    .columns
                    .iter()
                    .find(|c| c.name.eq_ignore_ascii_case(&new_col.name))
                {
                    if old_col.data_type != new_col.data_type
                        || old_col.is_nullable != new_col.is_nullable
                        || old_col.is_key != new_col.is_key
                    {
                        column_diffs.push(ColumnDiff::Alter {
                            old: old_col.clone(),
                            new: new_col.clone(),
                        });
                    }
                } else {
                    column_diffs.push(ColumnDiff::Add(new_col.clone()));
                }
            }

            // Check for dropped columns
            for old_col in &prev.columns {
                if !curr
                    .columns
                    .iter()
                    .any(|c| c.name.eq_ignore_ascii_case(&old_col.name))
                {
                    column_diffs.push(ColumnDiff::Drop(old_col.name.clone()));
                    risk_warnings.push(format!(
                        "Column '{}' in table '{}' will be dropped; potential data loss",
                        old_col.name, curr.name
                    ));
                }
            }

            // Check for foreign key changes
            let mut added_foreign_keys = Vec::new();
            let mut dropped_foreign_keys = Vec::new();

            for new_fk in &curr.foreign_keys {
                if let Some(old_fk) = prev
                    .foreign_keys
                    .iter()
                    .find(|f| f.name.eq_ignore_ascii_case(&new_fk.name))
                {
                    if old_fk != new_fk {
                        dropped_foreign_keys.push(old_fk.name.clone());
                        added_foreign_keys.push(new_fk.clone());
                    }
                } else {
                    added_foreign_keys.push(new_fk.clone());
                }
            }

            for old_fk in &prev.foreign_keys {
                if !curr
                    .foreign_keys
                    .iter()
                    .any(|f| f.name.eq_ignore_ascii_case(&old_fk.name))
                {
                    dropped_foreign_keys.push(old_fk.name.clone());
                }
            }

            // Check for secondary index changes
            let mut added_indexes = Vec::new();
            let mut dropped_indexes = Vec::new();

            for new_idx in &curr.indexes {
                if new_idx.is_primary {
                    continue;
                }
                if let Some(old_idx) = prev
                    .indexes
                    .iter()
                    .find(|i| i.name.eq_ignore_ascii_case(&new_idx.name))
                {
                    if old_idx != new_idx {
                        dropped_indexes.push(old_idx.name.clone());
                        added_indexes.push(new_idx.clone());
                    }
                } else {
                    added_indexes.push(new_idx.clone());
                }
            }

            for old_idx in &prev.indexes {
                if old_idx.is_primary {
                    continue;
                }
                if !curr
                    .indexes
                    .iter()
                    .any(|i| i.name.eq_ignore_ascii_case(&old_idx.name))
                {
                    dropped_indexes.push(old_idx.name.clone());
                }
            }

            let is_altered = !column_diffs.is_empty()
                || !added_foreign_keys.is_empty()
                || !dropped_foreign_keys.is_empty()
                || !added_indexes.is_empty()
                || !dropped_indexes.is_empty()
                || prev.primary_key != curr.primary_key;

            let action = if is_altered {
                TableAction::Alter
            } else {
                TableAction::Unchanged
            };

            tables.push(TableDiff {
                table_name: curr.name.clone(),
                action,
                column_diffs,
                added_foreign_keys,
                dropped_foreign_keys,
                added_indexes,
                dropped_indexes,
            });
        } else {
            // Table newly created
            let column_diffs = curr.columns.iter().cloned().map(ColumnDiff::Add).collect();
            let added_indexes = curr
                .indexes
                .iter()
                .filter(|i| !i.is_primary)
                .cloned()
                .collect();
            tables.push(TableDiff {
                table_name: curr.name.clone(),
                action: TableAction::Create,
                column_diffs,
                added_foreign_keys: curr.foreign_keys.clone(),
                dropped_foreign_keys: Vec::new(),
                added_indexes,
                dropped_indexes: Vec::new(),
            });
        }
    }

    // 2. Process dropped tables from `previous_tables`
    for prev in previous_tables {
        if !current_tables
            .iter()
            .any(|t| t.name.eq_ignore_ascii_case(&prev.name))
        {
            risk_warnings.push(format!(
                "Table '{}' will be dropped; potential data loss",
                prev.name
            ));
            tables.push(TableDiff {
                table_name: prev.name.clone(),
                action: TableAction::Drop,
                column_diffs: Vec::new(),
                added_foreign_keys: Vec::new(),
                dropped_foreign_keys: Vec::new(),
                added_indexes: Vec::new(),
                dropped_indexes: Vec::new(),
            });
        }
    }

    // 3. Generate multi-dialect migration scripts for all 4 dialects
    let mut sql_scripts = BTreeMap::new();

    for (d, key) in ALL_DIALECTS {
        let mut stmts = Vec::new();

        for diff in &tables {
            match diff.action {
                TableAction::Create => {
                    if let Some(curr) = current_tables
                        .iter()
                        .find(|t| t.name.eq_ignore_ascii_case(&diff.table_name))
                    {
                        if let Ok(ddl) = curr.to_create_table_ddl(d) {
                            stmts.push(ddl);
                        }
                        for idx in &curr.indexes {
                            if !idx.is_primary {
                                stmts.push(idx.to_create_sql(&curr.name, d));
                            }
                        }
                    }
                }
                TableAction::Drop => {
                    stmts.push(format!(
                        "DROP TABLE {};",
                        d.quote_identifier(&diff.table_name)
                    ));
                }
                TableAction::Alter => {
                    // Step 1: Drop foreign keys
                    for fk_name in &diff.dropped_foreign_keys {
                        stmts.push(format!(
                            "ALTER TABLE {} DROP CONSTRAINT {};",
                            d.quote_identifier(&diff.table_name),
                            d.quote_identifier(fk_name)
                        ));
                    }

                    // Step 2: Drop indexes
                    for idx_name in &diff.dropped_indexes {
                        let sql = match d {
                            SqlDialect::SqlServer => format!(
                                "DROP INDEX {} ON {};",
                                d.quote_identifier(idx_name),
                                d.quote_identifier(&diff.table_name)
                            ),
                            SqlDialect::Ansi | SqlDialect::PostgreSql | SqlDialect::Oracle => {
                                format!("DROP INDEX {};", d.quote_identifier(idx_name))
                            }
                        };
                        stmts.push(sql);
                    }

                    // Step 3: Drop columns
                    for col_diff in &diff.column_diffs {
                        if let ColumnDiff::Drop(col_name) = col_diff {
                            stmts.push(format!(
                                "ALTER TABLE {} DROP COLUMN {};",
                                d.quote_identifier(&diff.table_name),
                                d.quote_identifier(col_name)
                            ));
                        }
                    }

                    // Step 4: Alter modified columns
                    for col_diff in &diff.column_diffs {
                        if let ColumnDiff::Alter { old, new } = col_diff {
                            let tbl_sql = d.quote_identifier(&diff.table_name);
                            let col_sql = d.quote_identifier(&new.name);
                            let type_sql = d.map_data_type(&new.data_type).unwrap_or_default();
                            let null_kw = if new.is_nullable && !new.is_key {
                                "NULL"
                            } else {
                                "NOT NULL"
                            };

                            match d {
                                SqlDialect::SqlServer => {
                                    stmts.push(format!(
                                        "ALTER TABLE {tbl_sql} ALTER COLUMN {col_sql} {type_sql} {null_kw};"
                                    ));
                                }
                                SqlDialect::Oracle => {
                                    stmts.push(format!(
                                        "ALTER TABLE {tbl_sql} MODIFY ({col_sql} {type_sql} {null_kw});"
                                    ));
                                }
                                SqlDialect::PostgreSql => {
                                    let type_changed = old.data_type != new.data_type;
                                    let null_changed = old.is_nullable != new.is_nullable
                                        || old.is_key != new.is_key;
                                    let null_action = if new.is_nullable && !new.is_key {
                                        "DROP NOT NULL"
                                    } else {
                                        "SET NOT NULL"
                                    };

                                    if type_changed && null_changed {
                                        stmts.push(format!(
                                            "ALTER TABLE {tbl_sql} ALTER COLUMN {col_sql} TYPE {type_sql}, ALTER COLUMN {col_sql} {null_action};"
                                        ));
                                    } else if type_changed {
                                        stmts.push(format!(
                                            "ALTER TABLE {tbl_sql} ALTER COLUMN {col_sql} TYPE {type_sql};"
                                        ));
                                    } else if null_changed {
                                        stmts.push(format!(
                                            "ALTER TABLE {tbl_sql} ALTER COLUMN {col_sql} {null_action};"
                                        ));
                                    }
                                }
                                SqlDialect::Ansi => {
                                    let type_changed = old.data_type != new.data_type;
                                    let null_changed = old.is_nullable != new.is_nullable
                                        || old.is_key != new.is_key;
                                    let null_action = if new.is_nullable && !new.is_key {
                                        "DROP NOT NULL"
                                    } else {
                                        "SET NOT NULL"
                                    };

                                    if type_changed {
                                        stmts.push(format!(
                                            "ALTER TABLE {tbl_sql} ALTER COLUMN {col_sql} TYPE {type_sql};"
                                        ));
                                    }
                                    if null_changed {
                                        stmts.push(format!(
                                            "ALTER TABLE {tbl_sql} ALTER COLUMN {col_sql} {null_action};"
                                        ));
                                    }
                                }
                            }
                        }
                    }

                    // Step 5: Add new columns
                    for col_diff in &diff.column_diffs {
                        let ColumnDiff::Add(col) = col_diff else {
                            continue;
                        };
                        if let Ok(col_sql) = col.to_column_sql(d) {
                            stmts.push(format!(
                                "ALTER TABLE {} ADD {};",
                                d.quote_identifier(&diff.table_name),
                                col_sql
                            ));
                        }
                    }

                    // Step 6: Add secondary indexes
                    for idx in &diff.added_indexes {
                        stmts.push(idx.to_create_sql(&diff.table_name, d));
                    }

                    // Step 7: Add foreign keys
                    for fk in &diff.added_foreign_keys {
                        stmts.push(format!(
                            "ALTER TABLE {} ADD {};",
                            d.quote_identifier(&diff.table_name),
                            fk.to_constraint_sql(d)
                        ));
                    }
                }
                TableAction::Unchanged => {}
            }
        }

        sql_scripts.insert(key.to_string(), stmts);
    }

    ReorgImpactReport {
        tables,
        risk_warnings,
        sql_scripts,
    }
}
