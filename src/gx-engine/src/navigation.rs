//! GeneXus Specifier & SQL Navigation Engine.
//!
//! Provides read-only query planning, relational base table inference, extended table
//! join path resolution, order and index selection, and multi-dialect SQL query generation
//! with ZERO external dependencies and ZERO database writes.
//!
//! Adheres strictly to official GeneXus specifier semantics and error diagnostics:
//! - `spc0004`: No relationship between attributes.
//! - `spc0005`: Ambiguous base table for attributes.

use crate::catalog::{Catalog, ObjectKind};
use crate::frontend::{BinOp, Expr, ForEachOrder, ForEachStmt, Stmt};
use crate::table::{SqlDialect, Table, TableForeignKey, derive_tables_from_catalog};
use std::collections::{BTreeMap, BTreeSet};

/// Represents an individual filter condition extracted from a navigation block.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct NavigationFilter {
    pub attribute: String,
    pub op: String,
    pub expression: String,
}

/// Represents a join relationship from the base table to an extended table.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct NavigationJoin {
    pub table: String,
    pub on_condition: String,
}

/// Represents a single data access navigation level (e.g. `For Each`).
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct NavigationLevel {
    pub number: usize,
    pub level_type: String,
    pub base_table: String,
    pub index: String,
    pub order: Vec<String>,
    pub filters: Vec<NavigationFilter>,
    pub joins: Vec<NavigationJoin>,
    pub sql: String,
    pub has_when_none: bool,
}

/// Complete navigation report for a GeneXus object (Procedure, etc.).
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct NavigationReport {
    pub target_name: String,
    pub levels: Vec<NavigationLevel>,
    pub warnings: Vec<String>,
}

/// Extracts all referenced attribute names (in ASCII uppercase) from a `ForEachStmt`.
pub fn extract_for_each_attributes(for_each: &ForEachStmt) -> BTreeSet<String> {
    let mut attrs = BTreeSet::new();
    for explicit in &for_each.explicit_attributes {
        attrs.insert(explicit.to_ascii_uppercase());
    }
    for expr in &for_each.wheres {
        collect_expr_attributes(expr, &mut attrs);
    }
    for order in &for_each.orders {
        attrs.insert(order.attribute.to_ascii_uppercase());
    }
    if let Some(ref defined) = for_each.defined_by {
        attrs.insert(defined.to_ascii_uppercase());
    }
    collect_stmt_attributes(&for_each.body, &mut attrs);
    attrs
}

fn collect_stmt_attributes(stmts: &[Stmt], attrs: &mut BTreeSet<String>) {
    for stmt in stmts {
        match stmt {
            Stmt::AssignAttribute(name, expr) => {
                attrs.insert(name.to_ascii_uppercase());
                collect_expr_attributes(expr, attrs);
            }
            Stmt::Assign(_, expr)
            | Stmt::Print(expr)
            | Stmt::MsgStatus(expr)
            | Stmt::Return(expr)
            | Stmt::Call(expr) => {
                collect_expr_attributes(expr, attrs);
            }
            Stmt::If(cond, yes, no) => {
                collect_expr_attributes(cond, attrs);
                collect_stmt_attributes(yes, attrs);
                collect_stmt_attributes(no, attrs);
            }
            Stmt::While(cond, body) => {
                collect_expr_attributes(cond, attrs);
                collect_stmt_attributes(body, attrs);
            }
            Stmt::For(_, init, limit, _, body) => {
                collect_expr_attributes(init, attrs);
                collect_expr_attributes(limit, attrs);
                collect_stmt_attributes(body, attrs);
            }
            Stmt::ForIn(_, col, body) => {
                collect_expr_attributes(col, attrs);
                collect_stmt_attributes(body, attrs);
            }
            Stmt::Sub(_, body) => {
                collect_stmt_attributes(body, attrs);
            }
            Stmt::ForEach(nested) => {
                // Collect explicit attributes from nested loops
                for explicit in &nested.explicit_attributes {
                    attrs.insert(explicit.to_ascii_uppercase());
                }
            }
            _ => {}
        }
    }
}

fn collect_expr_attributes(expr: &Expr, attrs: &mut BTreeSet<String>) {
    match expr {
        Expr::Attribute(name) => {
            attrs.insert(name.to_ascii_uppercase());
        }
        Expr::Binary(left, _, right) => {
            collect_expr_attributes(left, attrs);
            collect_expr_attributes(right, attrs);
        }
        Expr::Not(inner) | Expr::Negate(inner) | Expr::TextLength(inner) => {
            collect_expr_attributes(inner, attrs);
        }
        Expr::Member(inner, _) | Expr::CloneRecord(inner) => {
            collect_expr_attributes(inner, attrs);
        }
        Expr::CollectionItem(receiver, idx) => {
            collect_expr_attributes(receiver, attrs);
            collect_expr_attributes(idx, attrs);
        }
        Expr::DateMethod(_, inner) => {
            collect_expr_attributes(inner, attrs);
        }
        Expr::DateShift(_, a, b) => {
            collect_expr_attributes(a, attrs);
            collect_expr_attributes(b, attrs);
        }
        Expr::Call(_, args) => {
            for arg in args {
                collect_expr_attributes(arg, attrs);
            }
        }
        _ => {}
    }
}

/// Represents a table reachable in the extended table (Tabela Estendida).
#[derive(Debug, Clone)]
pub struct ExtendedTableNode {
    pub table_name: String,
    pub parent_table: String,
    pub foreign_key: TableForeignKey,
    pub attributes: BTreeSet<String>,
}

/// Represents the full extended table view of a given physical table.
#[derive(Debug, Clone)]
pub struct ExtendedTable {
    pub base_table: String,
    pub direct_attributes: BTreeSet<String>,
    pub reachable_tables: BTreeMap<String, ExtendedTableNode>,
}

impl ExtendedTable {
    /// Returns true if this table or any table in its extended table contains the attribute.
    pub fn contains_attribute(&self, attribute: &str) -> bool {
        let upper = attribute.to_ascii_uppercase();
        if self.direct_attributes.contains(&upper) {
            return true;
        }
        self.reachable_tables
            .values()
            .any(|node| node.attributes.contains(&upper))
    }

    /// Returns all attributes available through this extended table.
    pub fn all_attributes(&self) -> BTreeSet<String> {
        let mut set = self.direct_attributes.clone();
        for node in self.reachable_tables.values() {
            set.extend(node.attributes.clone());
        }
        set
    }
}

/// Computes the extended table for each table in the physical schema.
pub fn compute_extended_tables(tables: &[Table]) -> BTreeMap<String, ExtendedTable> {
    let table_map: BTreeMap<String, &Table> = tables
        .iter()
        .map(|t| (t.name.to_ascii_uppercase(), t))
        .collect();

    let mut result = BTreeMap::new();

    for table in tables {
        let upper_name = table.name.to_ascii_uppercase();
        let direct_attributes: BTreeSet<String> = table
            .columns
            .iter()
            .map(|c| c.name.to_ascii_uppercase())
            .collect();

        let mut reachable = BTreeMap::new();
        let mut queue = vec![(table.name.clone(), table)];
        let mut visited = BTreeSet::new();
        visited.insert(upper_name.clone());

        let mut depth = 0;
        while !queue.is_empty() && depth < 16 {
            depth += 1;
            let current_level = queue;
            queue = Vec::new();

            for (from_table_name, current_t) in current_level {
                for fk in &current_t.foreign_keys {
                    let ref_upper = fk.referenced_table.to_ascii_uppercase();
                    if visited.contains(&ref_upper) {
                        continue;
                    }
                    if let Some(&ref_table) = table_map.get(&ref_upper) {
                        visited.insert(ref_upper.clone());
                        let ref_attrs: BTreeSet<String> = ref_table
                            .columns
                            .iter()
                            .map(|c| c.name.to_ascii_uppercase())
                            .collect();

                        reachable.insert(
                            ref_upper.clone(),
                            ExtendedTableNode {
                                table_name: ref_table.name.clone(),
                                parent_table: from_table_name.clone(),
                                foreign_key: fk.clone(),
                                attributes: ref_attrs,
                            },
                        );

                        queue.push((ref_table.name.clone(), ref_table));
                    }
                }
            }
        }

        result.insert(
            upper_name,
            ExtendedTable {
                base_table: table.name.clone(),
                direct_attributes,
                reachable_tables: reachable,
            },
        );
    }

    result
}

/// Infers the unique Base Table for a `ForEachStmt` according to GeneXus specifier rules (Matrix D1).
pub fn infer_base_table<'a>(
    for_each: &ForEachStmt,
    tables: &'a [Table],
    extended_tables: &BTreeMap<String, ExtendedTable>,
) -> Result<&'a Table, String> {
    let referenced_attrs = extract_for_each_attributes(for_each);
    if referenced_attrs.is_empty() && tables.len() == 1 {
        return Ok(&tables[0]);
    }

    // Filter candidate tables whose extended table contains all referenced attributes
    let mut candidates: Vec<&'a Table> = Vec::new();

    for table in tables {
        let upper_name = table.name.to_ascii_uppercase();
        if let Some(ext) = extended_tables.get(&upper_name) {
            // Check defined_by constraint
            if let Some(ref def_by) = for_each.defined_by
                && !ext.direct_attributes.contains(&def_by.to_ascii_uppercase())
            {
                continue;
            }

            // Check if all referenced attributes are reachable
            let all_reachable = referenced_attrs
                .iter()
                .all(|attr| ext.contains_attribute(attr));
            if all_reachable {
                candidates.push(table);
            }
        }
    }

    if candidates.is_empty() {
        let attr_list = referenced_attrs.into_iter().collect::<Vec<_>>().join(", ");
        return Err(format!(
            "spc0004: No relationship between attributes: [{attr_list}]"
        ));
    }

    // If any candidate contains ALL referenced attributes directly in direct_attributes:
    let all_direct_matching: Vec<&'a Table> = candidates
        .iter()
        .copied()
        .filter(|t| {
            let upper = t.name.to_ascii_uppercase();
            if let Some(ext) = extended_tables.get(&upper) {
                referenced_attrs
                    .iter()
                    .all(|attr| ext.direct_attributes.contains(attr))
            } else {
                false
            }
        })
        .collect();

    let candidates = if !all_direct_matching.is_empty() {
        all_direct_matching
    } else {
        // Otherwise, filter to candidates that have at least some direct matches
        let direct_matching_candidates: Vec<&'a Table> = candidates
            .iter()
            .copied()
            .filter(|t| {
                let upper = t.name.to_ascii_uppercase();
                if let Some(ext) = extended_tables.get(&upper) {
                    if let Some(ref def_by) = for_each.defined_by
                        && ext.direct_attributes.contains(&def_by.to_ascii_uppercase())
                    {
                        return true;
                    }
                    ext.direct_attributes
                        .iter()
                        .any(|attr| referenced_attrs.contains(attr))
                } else {
                    false
                }
            })
            .collect();

        if !direct_matching_candidates.is_empty() {
            direct_matching_candidates
        } else {
            candidates
        }
    };

    if candidates.len() == 1 {
        return Ok(candidates[0]);
    }

    // Disambiguate when multiple candidates exist:
    // In GeneXus, if table A has table B in its extended table (A -> B),
    // then A is the more detailed/subordinate table (child) and is chosen as the base table.
    let mut non_subordinate_candidates = Vec::new();

    for candidate in &candidates {
        let cand_upper = candidate.name.to_ascii_uppercase();
        let mut is_ancestor_of_another = false;

        for other in &candidates {
            if candidate.name == other.name {
                continue;
            }
            let other_upper = other.name.to_ascii_uppercase();
            if let Some(other_ext) = extended_tables.get(&other_upper)
                && other_ext.reachable_tables.contains_key(&cand_upper)
            {
                is_ancestor_of_another = true;
                break;
            }
        }

        if !is_ancestor_of_another {
            non_subordinate_candidates.push(*candidate);
        }
    }

    if non_subordinate_candidates.len() == 1 {
        return Ok(non_subordinate_candidates[0]);
    }

    // If still ambiguous without subordination relationship:
    let candidate_names = non_subordinate_candidates
        .iter()
        .map(|t| t.name.as_str())
        .collect::<Vec<_>>()
        .join(", ");
    let attr_list = referenced_attrs.into_iter().collect::<Vec<_>>().join(", ");
    Err(format!(
        "spc0005: Ambiguous base table for attributes [{attr_list}]; candidate tables: [{candidate_names}]. Disambiguate with 'Defined By' or explicit For Each attribute."
    ))
}

/// Selects the best index for the base table matching the `Order` clause (Matrix D3).
pub fn select_index(base_table: &Table, orders: &[ForEachOrder]) -> (String, Vec<String>) {
    if orders.is_empty() {
        // Default order: Primary Key
        let order_cols = base_table.primary_key.clone();
        let index_name = format!("PK_{}", base_table.name);
        return (index_name, order_cols);
    }

    let order_cols: Vec<String> = orders.iter().map(|o| o.attribute.clone()).collect();
    let order_upper: Vec<String> = order_cols.iter().map(|c| c.to_ascii_uppercase()).collect();

    // Check primary key match
    let pk_upper: Vec<String> = base_table
        .primary_key
        .iter()
        .map(|c| c.to_ascii_uppercase())
        .collect();
    if pk_upper == order_upper {
        return (format!("PK_{}", base_table.name), order_cols);
    }

    // Check secondary indexes
    for idx in &base_table.indexes {
        let idx_upper: Vec<String> = idx.columns.iter().map(|c| c.to_ascii_uppercase()).collect();
        if idx_upper == order_upper {
            return (idx.name.clone(), order_cols);
        }
    }

    ("NONE".to_string(), order_cols)
}

/// Resolves the minimum set of joins needed to satisfy attributes outside the base table (Matrix D5).
pub fn resolve_joins(
    _base_table: &Table,
    referenced_attrs: &BTreeSet<String>,
    ext_table: &ExtendedTable,
    dialect: SqlDialect,
) -> Vec<NavigationJoin> {
    let direct_attrs = &ext_table.direct_attributes;
    let mut needed_tables = BTreeSet::new();

    for attr in referenced_attrs {
        if !direct_attrs.contains(attr) {
            // Find which reachable table provides this attribute
            for (table_name, node) in &ext_table.reachable_tables {
                if node.attributes.contains(attr) {
                    needed_tables.insert(table_name.clone());
                    break;
                }
            }
        }
    }

    let mut joins = Vec::new();
    for table_name in needed_tables {
        if let Some(node) = ext_table.reachable_tables.get(&table_name) {
            let left_table = dialect.quote_identifier(&node.parent_table);
            let right_table = dialect.quote_identifier(&node.table_name);

            let mut conditions = Vec::new();
            for (fk_col, pk_col) in node
                .foreign_key
                .columns
                .iter()
                .zip(&node.foreign_key.referenced_columns)
            {
                let left_col = dialect.quote_identifier(fk_col);
                let right_col = dialect.quote_identifier(pk_col);
                conditions.push(format!(
                    "{left_table}.{left_col} = {right_table}.{right_col}"
                ));
            }

            joins.push(NavigationJoin {
                table: node.table_name.clone(),
                on_condition: conditions.join(" AND "),
            });
        }
    }

    joins
}

/// Formats a filter expression from an AST `Expr`.
pub fn format_filter_expression(expr: &Expr, dialect: SqlDialect) -> Option<NavigationFilter> {
    match expr {
        Expr::Binary(left, op, right) => {
            let op_str = match op {
                BinOp::Equal => "=",
                BinOp::NotEqual => "<>",
                BinOp::Less => "<",
                BinOp::LessEqual => "<=",
                BinOp::Greater => ">",
                BinOp::GreaterEqual => ">=",
                _ => return None,
            };

            let (att_name, val_expr) = match (left.as_ref(), right.as_ref()) {
                (Expr::Attribute(att), other) => (att.clone(), format_expr_value(other, dialect)),
                (other, Expr::Attribute(att)) => {
                    let inverted_op = match op {
                        BinOp::Equal => "=",
                        BinOp::NotEqual => "<>",
                        BinOp::Less => ">",
                        BinOp::LessEqual => ">=",
                        BinOp::Greater => "<",
                        BinOp::GreaterEqual => "<=",
                        _ => return None,
                    };
                    return Some(NavigationFilter {
                        attribute: att.clone(),
                        op: inverted_op.to_string(),
                        expression: format_expr_value(other, dialect),
                    });
                }
                _ => return None,
            };

            Some(NavigationFilter {
                attribute: att_name,
                op: op_str.to_string(),
                expression: val_expr,
            })
        }
        _ => None,
    }
}

fn format_expr_value(expr: &Expr, dialect: SqlDialect) -> String {
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
        Expr::Variable(var) => format!("@{var}"),
        Expr::Attribute(att) => dialect.quote_identifier(att),
        _ => "/* expr */".to_string(),
    }
}

/// Generates clean, read-only SQL for the navigation level (Matrix D2 & H1).
pub fn generate_navigation_sql(
    base_table: &Table,
    joins: &[NavigationJoin],
    filters: &[NavigationFilter],
    orders: &[ForEachOrder],
    order_cols: &[String],
    tables: &[Table],
    dialect: SqlDialect,
) -> String {
    let mut sql = String::new();
    sql.push_str("SELECT ");

    let resolve_column = |attr: &str| -> (String, String) {
        if let Some(c) = base_table
            .columns
            .iter()
            .find(|c| c.name.eq_ignore_ascii_case(attr))
        {
            return (
                dialect.quote_identifier(&base_table.name),
                dialect.quote_identifier(&c.name),
            );
        }
        for join in joins {
            if let Some(t) = tables
                .iter()
                .find(|t| t.name.eq_ignore_ascii_case(&join.table))
                && let Some(c) = t.columns.iter().find(|c| c.name.eq_ignore_ascii_case(attr))
            {
                return (
                    dialect.quote_identifier(&t.name),
                    dialect.quote_identifier(&c.name),
                );
            }
        }
        (
            dialect.quote_identifier(&base_table.name),
            dialect.quote_identifier(attr),
        )
    };

    let quoted_base = dialect.quote_identifier(&base_table.name);
    let cols: Vec<String> = base_table
        .columns
        .iter()
        .map(|c| format!("{quoted_base}.{}", dialect.quote_identifier(&c.name)))
        .collect();
    sql.push_str(&cols.join(", "));

    sql.push_str(" FROM ");
    sql.push_str(&quoted_base);

    for join in joins {
        let quoted_join = dialect.quote_identifier(&join.table);
        sql.push_str(&format!(
            " INNER JOIN {quoted_join} ON {}",
            join.on_condition
        ));
    }

    if !filters.is_empty() {
        sql.push_str(" WHERE ");
        let where_clauses: Vec<String> = filters
            .iter()
            .map(|f| {
                let (table_prefix, col_name) = resolve_column(&f.attribute);
                format!("{table_prefix}.{col_name} {} {}", f.op, f.expression)
            })
            .collect();
        sql.push_str(&where_clauses.join(" AND "));
    }

    if !orders.is_empty() {
        sql.push_str(" ORDER BY ");
        let order_clauses: Vec<String> = orders
            .iter()
            .map(|o| {
                let (table_prefix, col_name) = resolve_column(&o.attribute);
                let dir = if o.descending { " DESC" } else { " ASC" };
                format!("{table_prefix}.{col_name}{dir}")
            })
            .collect();
        sql.push_str(&order_clauses.join(", "));
    } else if !order_cols.is_empty() {
        sql.push_str(" ORDER BY ");
        let order_clauses: Vec<String> = order_cols
            .iter()
            .map(|col| {
                let (table_prefix, col_name) = resolve_column(col);
                format!("{table_prefix}.{col_name} ASC")
            })
            .collect();
        sql.push_str(&order_clauses.join(", "));
    }

    sql
}

/// Specifies one `ForEachStmt` within physical table schema and correlation context.
pub fn specify_for_each(
    for_each: &ForEachStmt,
    level_num: usize,
    tables: &[Table],
    extended_tables: &BTreeMap<String, ExtendedTable>,
    context_tables: &[&Table],
    dialect: SqlDialect,
) -> Result<NavigationLevel, String> {
    let base_table = infer_base_table(for_each, tables, extended_tables)?;
    let base_upper = base_table.name.to_ascii_uppercase();
    let ext_table = extended_tables
        .get(&base_upper)
        .ok_or("missing extended table")?;

    let referenced_attrs = extract_for_each_attributes(for_each);
    let joins = resolve_joins(base_table, &referenced_attrs, ext_table, dialect);
    let (index_name, default_order_cols) = select_index(base_table, &for_each.orders);

    let mut filters = Vec::new();

    // Context / correlation filters from outer nested For Each loops
    for parent in context_tables {
        for fk in &base_table.foreign_keys {
            if fk.referenced_table.eq_ignore_ascii_case(&parent.name) {
                for (fk_col, pk_col) in fk.columns.iter().zip(&fk.referenced_columns) {
                    filters.push(NavigationFilter {
                        attribute: fk_col.clone(),
                        op: "=".to_string(),
                        expression: format!(
                            "{}.{}",
                            dialect.quote_identifier(&parent.name),
                            dialect.quote_identifier(pk_col)
                        ),
                    });
                }
            }
        }
    }

    // Where filters
    for where_expr in &for_each.wheres {
        if let Some(f) = format_filter_expression(where_expr, dialect) {
            filters.push(f);
        }
    }

    let order_names: Vec<String> = if !for_each.orders.is_empty() {
        for_each
            .orders
            .iter()
            .map(|o| o.attribute.clone())
            .collect()
    } else {
        default_order_cols.clone()
    };

    let sql = generate_navigation_sql(
        base_table,
        &joins,
        &filters,
        &for_each.orders,
        &default_order_cols,
        tables,
        dialect,
    );

    Ok(NavigationLevel {
        number: level_num,
        level_type: "For Each".to_string(),
        base_table: base_table.name.clone(),
        index: index_name,
        order: order_names,
        filters,
        joins,
        sql,
        has_when_none: !for_each.when_none.is_empty(),
    })
}

/// Recursively traverses a Procedure's statements to extract and specify all navigation levels.
pub fn specify_statements<'a>(
    stmts: &[Stmt],
    tables: &'a [Table],
    extended_tables: &BTreeMap<String, ExtendedTable>,
    context_tables: &mut Vec<&'a Table>,
    levels: &mut Vec<NavigationLevel>,
    dialect: SqlDialect,
) -> Result<(), String> {
    for stmt in stmts {
        match stmt {
            Stmt::ForEach(for_each) => {
                let level_num = levels.len() + 1;
                let level = specify_for_each(
                    for_each,
                    level_num,
                    tables,
                    extended_tables,
                    context_tables,
                    dialect,
                )?;
                let base_table = tables
                    .iter()
                    .find(|t| t.name.eq_ignore_ascii_case(&level.base_table))
                    .ok_or("base table lookup failed")?;

                levels.push(level);

                context_tables.push(base_table);
                specify_statements(
                    &for_each.body,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
                context_tables.pop();

                if !for_each.when_none.is_empty() {
                    specify_statements(
                        &for_each.when_none,
                        tables,
                        extended_tables,
                        context_tables,
                        levels,
                        dialect,
                    )?;
                }
            }
            Stmt::If(_, yes, no) => {
                specify_statements(
                    yes,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
                specify_statements(no, tables, extended_tables, context_tables, levels, dialect)?;
            }
            Stmt::While(_, body) => {
                specify_statements(
                    body,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
            }
            Stmt::For(_, _, _, _, body) => {
                specify_statements(
                    body,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
            }
            Stmt::ForIn(_, _, body) => {
                specify_statements(
                    body,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
            }
            Stmt::Sub(_, body) => {
                specify_statements(
                    body,
                    tables,
                    extended_tables,
                    context_tables,
                    levels,
                    dialect,
                )?;
            }
            _ => {}
        }
    }
    Ok(())
}

/// Generates a comprehensive navigation report for an authored procedure in a catalog (Matrix D2).
pub fn specify_procedure(
    catalog: &Catalog,
    procedure_name: &str,
    dialect: SqlDialect,
) -> Result<NavigationReport, String> {
    let proc_obj = catalog
        .objects()
        .find(|obj| {
            obj.name.eq_ignore_ascii_case(procedure_name) && obj.kind == ObjectKind::Procedure
        })
        .ok_or_else(|| format!("procedure '{procedure_name}' not found in catalog"))?;

    let source = proc_obj
        .source_parts
        .iter()
        .find(|p| p.name.eq_ignore_ascii_case("Source"))
        .map(|p| p.source.as_str())
        .unwrap_or_default();

    let stmts = crate::frontend::parse_body(source)?;
    let tables = derive_tables_from_catalog(catalog)?;
    let extended_tables = compute_extended_tables(&tables);

    let mut levels = Vec::new();
    let mut context_tables = Vec::new();
    specify_statements(
        &stmts,
        &tables,
        &extended_tables,
        &mut context_tables,
        &mut levels,
        dialect,
    )?;

    Ok(NavigationReport {
        target_name: proc_obj.name.clone(),
        levels,
        warnings: Vec::new(),
    })
}

/// Generates navigation reports for all procedures in a catalog.
pub fn specify_catalog(
    catalog: &Catalog,
    dialect: SqlDialect,
) -> Result<Vec<NavigationReport>, String> {
    let mut reports = Vec::new();
    for obj in catalog.objects() {
        if obj.kind == ObjectKind::Procedure {
            let report = specify_procedure(catalog, &obj.name, dialect)?;
            reports.push(report);
        }
    }
    Ok(reports)
}
