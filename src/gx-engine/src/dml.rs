//! Relational DML query planning and multi-dialect SQL generation.
//! Produces read-only INSERT, UPDATE, and DELETE query plans with PK verification and WHEN DUPLICATE handling.

use crate::frontend::{BinOp, Expr, NewStmt, Stmt};
use crate::table::{SqlDialect, Table};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

/// Relational DML operation kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum NavigationDmlOp {
    Insert,
    Update,
    Delete,
}

/// A single column assignment in an INSERT or UPDATE statement.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ColumnAssignment {
    pub attribute: String,
    pub column: String,
    pub expr_sql: String,
}

/// A planned DML mutation query with multi-dialect SQL statements.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NavigationDmlPlan {
    pub operation: NavigationDmlOp,
    pub target_table: String,
    pub primary_key: Vec<String>,
    pub assignments: Vec<ColumnAssignment>,
    pub where_predicates: Vec<String>,
    pub has_when_duplicate: bool,
    pub sql: BTreeMap<String, String>,
}

/// Formats an expression into an SQL string for a given dialect.
pub fn expr_to_sql(expr: &Expr, dialect: SqlDialect) -> String {
    match expr {
        Expr::Integer(val) => val.to_string(),
        Expr::Decimal(val) => val.clone(),
        Expr::Text(val) => format!("'{val}'"),
        Expr::Boolean(val) => match dialect {
            SqlDialect::SqlServer | SqlDialect::Oracle => {
                if *val {
                    "1".into()
                } else {
                    "0".into()
                }
            }
            SqlDialect::PostgreSql | SqlDialect::Ansi => {
                if *val {
                    "TRUE".into()
                } else {
                    "FALSE".into()
                }
            }
        },
        Expr::Variable(var) => format!(":{var}"),
        Expr::Attribute(att) => att.clone(),
        Expr::Not(inner) => format!("NOT ({})", expr_to_sql(inner, dialect)),
        Expr::Negate(inner) => format!("-{}", expr_to_sql(inner, dialect)),
        Expr::Binary(left, op, right) => {
            let l = expr_to_sql(left, dialect);
            let r = expr_to_sql(right, dialect);
            let op_str = match op {
                BinOp::Add => "+",
                BinOp::Subtract => "-",
                BinOp::Multiply => "*",
                BinOp::Divide => "/",
                BinOp::Equal => "=",
                BinOp::NotEqual => "<>",
                BinOp::Less => "<",
                BinOp::LessEqual => "<=",
                BinOp::Greater => ">",
                BinOp::GreaterEqual => ">=",
                BinOp::And => "AND",
                BinOp::Or => "OR",
            };
            format!("{l} {op_str} {r}")
        }
        Expr::Call(name, args) => {
            let args_sql = args
                .iter()
                .map(|a| expr_to_sql(a, dialect))
                .collect::<Vec<_>>()
                .join(", ");
            format!("{name}({args_sql})")
        }
        Expr::EmptyDate => match dialect {
            SqlDialect::SqlServer | SqlDialect::Oracle => "'1753-01-01'".into(),
            SqlDialect::PostgreSql | SqlDialect::Ansi => "'0001-01-01'".into(),
        },
        _ => format!("{expr:?}"),
    }
}

/// Recursively collects attribute assignments from a statement block,
/// avoiding descending into nested ForEach or New scopes.
fn collect_assignments(stmts: &[Stmt], out: &mut Vec<(String, Expr)>) {
    for stmt in stmts {
        match stmt {
            Stmt::AssignAttribute(att, expr) => {
                out.push((att.clone(), expr.clone()));
            }
            Stmt::If(_, then_branch, else_branch) => {
                collect_assignments(then_branch, out);
                collect_assignments(else_branch, out);
            }
            Stmt::While(_, body) => {
                collect_assignments(body, out);
            }
            Stmt::For(_, _, _, _, body) => {
                collect_assignments(body, out);
            }
            Stmt::ForIn(_, _, body) => {
                collect_assignments(body, out);
            }
            Stmt::ForEach(_) | Stmt::New(_) => {}
            _ => {}
        }
    }
}

/// Plans database row updates for assignments occurring inside a `For Each` loop.
///
/// Fails closed with `spc0034` if an attempt is made to update a primary key attribute.
/// Returns `Ok(None)` if no assignments targeting `base_table` are found.
pub fn plan_updates_for_for_each(
    base_table: &Table,
    body: &[Stmt],
    dialect: SqlDialect,
) -> Result<Option<NavigationDmlPlan>, String> {
    let mut raw_assignments = Vec::new();
    collect_assignments(body, &mut raw_assignments);

    // Validate that none of the assigned attributes belong to the base table's primary key.
    for (att, _) in &raw_assignments {
        if base_table
            .primary_key
            .iter()
            .any(|pk| pk.eq_ignore_ascii_case(att))
        {
            return Err(format!(
                "spc0034: Cannot update primary key attribute '{att}' in table '{}'",
                base_table.name
            ));
        }
    }

    // Match assignments to columns in base_table.
    let mut matched_assignments = Vec::new();
    for (att, expr) in &raw_assignments {
        if let Some(col) = base_table
            .columns
            .iter()
            .find(|c| c.name.eq_ignore_ascii_case(att))
        {
            matched_assignments.push((att.clone(), col.name.clone(), expr.clone()));
        }
    }

    if matched_assignments.is_empty() {
        return Ok(None);
    }

    let assignments: Vec<ColumnAssignment> = matched_assignments
        .iter()
        .map(|(att, col, expr)| ColumnAssignment {
            attribute: att.clone(),
            column: col.clone(),
            expr_sql: expr_to_sql(expr, dialect),
        })
        .collect();

    let where_predicates: Vec<String> = base_table
        .primary_key
        .iter()
        .map(|pk| format!("{pk} = :{pk}"))
        .collect();

    let dialects = [
        ("ansi", SqlDialect::Ansi),
        ("sqlserver", SqlDialect::SqlServer),
        ("postgresql", SqlDialect::PostgreSql),
        ("oracle", SqlDialect::Oracle),
    ];

    let mut sql = BTreeMap::new();
    for (key, d) in dialects {
        let set_clauses: Vec<String> = matched_assignments
            .iter()
            .map(|(_, col, expr)| {
                let val_sql = expr_to_sql(expr, d);
                format!("{col} = {val_sql}")
            })
            .collect();
        let set_str = set_clauses.join(", ");
        let stmt_sql = if where_predicates.is_empty() {
            format!("UPDATE {} SET {}", base_table.name, set_str)
        } else {
            format!(
                "UPDATE {} SET {} WHERE {}",
                base_table.name,
                set_str,
                where_predicates.join(" AND ")
            )
        };
        sql.insert(key.to_string(), stmt_sql);
    }

    Ok(Some(NavigationDmlPlan {
        operation: NavigationDmlOp::Update,
        target_table: base_table.name.clone(),
        primary_key: base_table.primary_key.clone(),
        assignments,
        where_predicates,
        has_when_duplicate: false,
        sql,
    }))
}

/// Plans database row deletion for `delete` statement occurring inside a `For Each` loop.
pub fn plan_delete_for_for_each(base_table: &Table, _dialect: SqlDialect) -> NavigationDmlPlan {
    let where_predicates: Vec<String> = base_table
        .primary_key
        .iter()
        .map(|pk| format!("{pk} = :{pk}"))
        .collect();

    let dialects = ["ansi", "sqlserver", "postgresql", "oracle"];
    let mut sql = BTreeMap::new();
    for key in dialects {
        let stmt_sql = if where_predicates.is_empty() {
            format!("DELETE FROM {}", base_table.name)
        } else {
            format!(
                "DELETE FROM {} WHERE {}",
                base_table.name,
                where_predicates.join(" AND ")
            )
        };
        sql.insert(key.to_string(), stmt_sql);
    }

    NavigationDmlPlan {
        operation: NavigationDmlOp::Delete,
        target_table: base_table.name.clone(),
        primary_key: base_table.primary_key.clone(),
        assignments: Vec::new(),
        where_predicates,
        has_when_duplicate: false,
        sql,
    }
}

/// Plans database row insertion for `new ... endnew` statements.
///
/// Infers the target table from assigned attributes, validates primary key completeness (`spc0023`),
/// and emits multi-dialect SQL handling `WHEN DUPLICATE` conflict scenarios.
pub fn plan_insert_for_new(
    new_stmt: &NewStmt,
    tables: &[Table],
    dialect: SqlDialect,
) -> Result<NavigationDmlPlan, String> {
    let mut raw_assignments = Vec::new();
    collect_assignments(&new_stmt.body, &mut raw_assignments);

    if raw_assignments.is_empty() {
        return Err(
            "cannot infer target table for new statement with no attribute assignments".into(),
        );
    }

    let assigned_attrs: Vec<&str> = raw_assignments
        .iter()
        .map(|(att, _)| att.as_str())
        .collect();

    // Find candidate tables in `tables` where all assigned attributes exist as columns.
    let mut candidates: Vec<&Table> = tables
        .iter()
        .filter(|t| {
            assigned_attrs
                .iter()
                .all(|att| t.columns.iter().any(|c| c.name.eq_ignore_ascii_case(att)))
        })
        .collect();

    if candidates.is_empty() {
        return Err(format!(
            "spc0011: No table found containing all assigned attributes: {:?}",
            assigned_attrs
        ));
    }

    // Disambiguate using minimal base table / primary key match.
    candidates.sort_by_key(|t| {
        let all_pk_assigned = !t.primary_key.is_empty()
            && t.primary_key.iter().all(|pk| {
                assigned_attrs
                    .iter()
                    .any(|att| pk.eq_ignore_ascii_case(att))
            });
        (!all_pk_assigned, t.columns.len(), &t.name)
    });
    let target_table = candidates[0];

    // Validate primary key completeness: check that EVERY column in `target_table.primary_key` is assigned.
    let mut missing = Vec::new();
    for pk in &target_table.primary_key {
        if !assigned_attrs
            .iter()
            .any(|att| pk.eq_ignore_ascii_case(att))
        {
            missing.push(pk.clone());
        }
    }
    if !missing.is_empty() {
        return Err(format!(
            "spc0023: Primary key is not completely specified for insertion in table '{}' (missing: {:?})",
            target_table.name, missing
        ));
    }

    let has_when_duplicate = !new_stmt.when_duplicate.is_empty();

    let mut assignments = Vec::new();
    for (att, expr) in &raw_assignments {
        if let Some(col) = target_table
            .columns
            .iter()
            .find(|c| c.name.eq_ignore_ascii_case(att))
        {
            assignments.push(ColumnAssignment {
                attribute: att.clone(),
                column: col.name.clone(),
                expr_sql: expr_to_sql(expr, dialect),
            });
        }
    }

    let cols: Vec<String> = assignments.iter().map(|a| a.column.clone()).collect();
    let cols_str = cols.join(", ");

    let mut sql = BTreeMap::new();

    // ANSI
    let ansi_vals: Vec<String> = raw_assignments
        .iter()
        .map(|(_, expr)| expr_to_sql(expr, SqlDialect::Ansi))
        .collect();
    let ansi_vals_str = ansi_vals.join(", ");
    let ansi_insert = format!(
        "INSERT INTO {} ({}) VALUES ({})",
        target_table.name, cols_str, ansi_vals_str
    );
    sql.insert("ansi".to_string(), ansi_insert);

    // SQL Server
    let sqlserver_vals: Vec<String> = raw_assignments
        .iter()
        .map(|(_, expr)| expr_to_sql(expr, SqlDialect::SqlServer))
        .collect();
    let sqlserver_vals_str = sqlserver_vals.join(", ");
    let sqlserver_sql = if has_when_duplicate {
        let pk_preds: Vec<String> = target_table
            .primary_key
            .iter()
            .map(|pk| {
                let (_, expr) = raw_assignments
                    .iter()
                    .find(|(att, _)| pk.eq_ignore_ascii_case(att))
                    .expect("PK attribute validated as present");
                let val = expr_to_sql(expr, SqlDialect::SqlServer);
                format!("{pk} = {val}")
            })
            .collect();
        let pk_where = pk_preds.join(" AND ");
        format!(
            "IF NOT EXISTS (SELECT 1 FROM {} WHERE {}) INSERT INTO {} ({}) VALUES ({})",
            target_table.name, pk_where, target_table.name, cols_str, sqlserver_vals_str
        )
    } else {
        format!(
            "INSERT INTO {} ({}) VALUES ({})",
            target_table.name, cols_str, sqlserver_vals_str
        )
    };
    sql.insert("sqlserver".to_string(), sqlserver_sql);

    // PostgreSQL
    let postgres_vals: Vec<String> = raw_assignments
        .iter()
        .map(|(_, expr)| expr_to_sql(expr, SqlDialect::PostgreSql))
        .collect();
    let postgres_vals_str = postgres_vals.join(", ");
    let postgres_sql = if has_when_duplicate {
        let pk_list = target_table.primary_key.join(", ");
        format!(
            "INSERT INTO {} ({}) VALUES ({}) ON CONFLICT ({}) DO NOTHING",
            target_table.name, cols_str, postgres_vals_str, pk_list
        )
    } else {
        format!(
            "INSERT INTO {} ({}) VALUES ({})",
            target_table.name, cols_str, postgres_vals_str
        )
    };
    sql.insert("postgresql".to_string(), postgres_sql);

    // Oracle
    let oracle_vals: Vec<String> = raw_assignments
        .iter()
        .map(|(_, expr)| expr_to_sql(expr, SqlDialect::Oracle))
        .collect();
    let oracle_vals_str = oracle_vals.join(", ");
    let oracle_sql = if has_when_duplicate {
        let select_items: Vec<String> = target_table
            .primary_key
            .iter()
            .map(|pk| {
                let (_, expr) = raw_assignments
                    .iter()
                    .find(|(att, _)| pk.eq_ignore_ascii_case(att))
                    .expect("PK attribute validated as present");
                let val = expr_to_sql(expr, SqlDialect::Oracle);
                format!("{val} AS {pk}")
            })
            .collect();
        let on_conditions: Vec<String> = target_table
            .primary_key
            .iter()
            .map(|pk| format!("t.{pk} = s.{pk}"))
            .collect();
        format!(
            "MERGE INTO {} t USING (SELECT {} FROM dual) s ON ({}) WHEN NOT MATCHED THEN INSERT ({}) VALUES ({})",
            target_table.name,
            select_items.join(", "),
            on_conditions.join(" AND "),
            cols_str,
            oracle_vals_str
        )
    } else {
        format!(
            "INSERT INTO {} ({}) VALUES ({})",
            target_table.name, cols_str, oracle_vals_str
        )
    };
    sql.insert("oracle".to_string(), oracle_sql);

    Ok(NavigationDmlPlan {
        operation: NavigationDmlOp::Insert,
        target_table: target_table.name.clone(),
        primary_key: target_table.primary_key.clone(),
        assignments,
        where_predicates: Vec::new(),
        has_when_duplicate,
        sql,
    })
}
