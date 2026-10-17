//! Object Cross-Reference & Impact Analysis Engine for GeneXus Knowledge Bases.
//!
//! Provides dependency graph construction, cycle-safe transitive traversal,
//! attribute and table impact querying, blast radius calculation, and
//! automated catalog indexing. Conforms to Plan 129 Matrix Row D4.

use crate::catalog::{Catalog, ObjectKind};
use crate::frontend::{Expr, Stmt};
use crate::navigation::{ExtendedTable, infer_base_table};
use crate::table::{SqlDialect, Table};
use crate::transaction::TransactionStructure;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet, HashSet, VecDeque};

/// Maximum allowed depth for transitive traversal (cycle prevention & recursion guard).
pub const MAX_TRANSITIVE_DEPTH: usize = 16;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ObjectRef {
    pub kind: ObjectKind,
    pub name: String,
}

impl PartialEq for ObjectRef {
    fn eq(&self, other: &Self) -> bool {
        self.kind == other.kind && self.name.eq_ignore_ascii_case(&other.name)
    }
}

impl Eq for ObjectRef {}

impl std::hash::Hash for ObjectRef {
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        self.kind.hash(state);
        self.name.to_ascii_uppercase().hash(state);
    }
}

impl PartialOrd for ObjectRef {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for ObjectRef {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        self.kind.cmp(&other.kind).then_with(|| {
            self.name
                .to_ascii_uppercase()
                .cmp(&other.name.to_ascii_uppercase())
        })
    }
}

impl ObjectRef {
    pub fn new(kind: ObjectKind, name: impl Into<String>) -> Self {
        Self {
            kind,
            name: name.into(),
        }
    }

    pub fn procedure(name: impl Into<String>) -> Self {
        Self::new(ObjectKind::Procedure, name)
    }

    pub fn transaction(name: impl Into<String>) -> Self {
        Self::new(ObjectKind::Transaction, name)
    }

    pub fn attribute(name: impl Into<String>) -> Self {
        Self::new(ObjectKind::Attribute, name)
    }

    pub fn table(name: impl Into<String>) -> Self {
        Self::new(ObjectKind::Table, name)
    }

    pub fn sdt(name: impl Into<String>) -> Self {
        Self::new(ObjectKind::StructuredDataType, name)
    }

    pub fn web_panel(name: impl Into<String>) -> Self {
        Self::new(ObjectKind::WebPanel, name)
    }
}

/// The nature of a dependency between two objects.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
pub enum DependencyKind {
    CallsProcedure,
    UsesSdt,
    ReferencesAttribute,
    ReadsTable,
    UpdatesTable,
    InsertsTable,
    DeletesTable,
}

/// A directed dependency edge in the impact graph: `from` depends on `to`.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
pub struct DependencyEdge {
    pub from: ObjectRef,
    pub to: ObjectRef,
    pub kind: DependencyKind,
}

impl DependencyEdge {
    pub fn new(from: ObjectRef, to: ObjectRef, kind: DependencyKind) -> Self {
        Self { from, to, kind }
    }
}

/// Risk level classification for the blast radius of a change.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
pub enum RiskLevel {
    Low,
    Medium,
    High,
    Critical,
}

/// Comprehensive report on the blast radius and impact of modifying a target object.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BlastRadiusReport {
    pub target: ObjectRef,
    pub direct_callers: usize,
    pub transitive_callers: usize,
    pub affected_attributes: Vec<String>,
    pub affected_tables: Vec<String>,
    pub risk_level: RiskLevel,
}

/// Cross-reference and impact analysis graph for Knowledge Base objects.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct ImpactGraph {
    pub edges: Vec<DependencyEdge>,
}

impl ImpactGraph {
    pub fn new() -> Self {
        Self { edges: Vec::new() }
    }

    /// Adds a dependency edge if it does not already exist.
    pub fn add_dependency(&mut self, from: ObjectRef, to: ObjectRef, kind: DependencyKind) {
        let edge = DependencyEdge { from, to, kind };
        if !self.edges.contains(&edge) {
            self.edges.push(edge);
        }
    }

    /// Returns all direct dependencies of the given object (`from == object`).
    pub fn direct_dependencies(&self, object: &ObjectRef) -> Vec<DependencyEdge> {
        self.edges
            .iter()
            .filter(|e| {
                e.from.kind == object.kind && e.from.name.eq_ignore_ascii_case(&object.name)
            })
            .cloned()
            .collect()
    }

    /// Returns all direct dependents that depend on the given object (`to == object`).
    pub fn direct_dependents(&self, object: &ObjectRef) -> Vec<DependencyEdge> {
        self.edges
            .iter()
            .filter(|e| e.to.kind == object.kind && e.to.name.eq_ignore_ascii_case(&object.name))
            .cloned()
            .collect()
    }

    /// Transitive dependents (callers/users) of the given object, cycle-safe, depth limited to 16.
    pub fn transitive_dependents(&self, object: &ObjectRef, max_depth: usize) -> Vec<ObjectRef> {
        let effective_depth = max_depth.min(MAX_TRANSITIVE_DEPTH);
        if effective_depth == 0 {
            return Vec::new();
        }

        let mut visited: HashSet<(ObjectKind, String)> = HashSet::new();
        visited.insert((object.kind, object.name.to_ascii_uppercase()));

        let mut result = Vec::new();
        let mut queue = VecDeque::new();
        queue.push_back((object.clone(), 0));

        while let Some((curr, depth)) = queue.pop_front() {
            if depth >= effective_depth {
                continue;
            }
            for edge in self.direct_dependents(&curr) {
                let key = (edge.from.kind, edge.from.name.to_ascii_uppercase());
                if visited.insert(key) {
                    result.push(edge.from.clone());
                    queue.push_back((edge.from, depth + 1));
                }
            }
        }

        result
    }

    /// Transitive dependencies of the given object, cycle-safe, depth limited to 16.
    pub fn transitive_dependencies(&self, object: &ObjectRef, max_depth: usize) -> Vec<ObjectRef> {
        let effective_depth = max_depth.min(MAX_TRANSITIVE_DEPTH);
        if effective_depth == 0 {
            return Vec::new();
        }

        let mut visited: HashSet<(ObjectKind, String)> = HashSet::new();
        visited.insert((object.kind, object.name.to_ascii_uppercase()));

        let mut result = Vec::new();
        let mut queue = VecDeque::new();
        queue.push_back((object.clone(), 0));

        while let Some((curr, depth)) = queue.pop_front() {
            if depth >= effective_depth {
                continue;
            }
            for edge in self.direct_dependencies(&curr) {
                let key = (edge.to.kind, edge.to.name.to_ascii_uppercase());
                if visited.insert(key) {
                    result.push(edge.to.clone());
                    queue.push_back((edge.to, depth + 1));
                }
            }
        }

        result
    }

    /// Finds all objects with `ReferencesAttribute` to this attribute.
    pub fn impact_of_attribute(&self, attribute_name: &str) -> Vec<ObjectRef> {
        let mut seen = HashSet::new();
        let mut result = Vec::new();
        for edge in &self.edges {
            if edge.kind == DependencyKind::ReferencesAttribute
                && edge.to.name.eq_ignore_ascii_case(attribute_name)
            {
                let key = (edge.from.kind, edge.from.name.to_ascii_uppercase());
                if seen.insert(key) {
                    result.push(edge.from.clone());
                }
            }
        }
        result
    }

    /// Finds all objects with `ReadsTable`, `UpdatesTable`, `InsertsTable`, or `DeletesTable` to this table.
    pub fn impact_of_table(&self, table_name: &str) -> Vec<ObjectRef> {
        let mut seen = HashSet::new();
        let mut result = Vec::new();
        for edge in &self.edges {
            if matches!(
                edge.kind,
                DependencyKind::ReadsTable
                    | DependencyKind::UpdatesTable
                    | DependencyKind::InsertsTable
                    | DependencyKind::DeletesTable
            ) && edge.to.name.eq_ignore_ascii_case(table_name)
            {
                let key = (edge.from.kind, edge.from.name.to_ascii_uppercase());
                if seen.insert(key) {
                    result.push(edge.from.clone());
                }
            }
        }
        result
    }

    /// Calculates the blast radius report for a target object, including direct/transitive callers,
    /// affected attributes, affected tables, and risk level.
    pub fn blast_radius(&self, target: &ObjectRef) -> BlastRadiusReport {
        let direct = self.direct_dependents(target);
        let mut direct_callers_set = HashSet::new();
        for edge in &direct {
            direct_callers_set.insert((edge.from.kind, edge.from.name.to_ascii_uppercase()));
        }
        let direct_callers = direct_callers_set.len();

        let transitive = self.transitive_dependents(target, MAX_TRANSITIVE_DEPTH);
        let transitive_callers = transitive.len();

        let mut affected_attrs_set = BTreeSet::new();
        let mut affected_tables_set = BTreeSet::new();

        if target.kind == ObjectKind::Attribute {
            affected_attrs_set.insert(target.name.clone());
        } else if target.kind == ObjectKind::Table {
            affected_tables_set.insert(target.name.clone());
        }

        // Collect attributes and tables from direct dependencies
        for edge in self.direct_dependencies(target) {
            match edge.kind {
                DependencyKind::ReferencesAttribute => {
                    affected_attrs_set.insert(edge.to.name);
                }
                DependencyKind::ReadsTable
                | DependencyKind::UpdatesTable
                | DependencyKind::InsertsTable
                | DependencyKind::DeletesTable => {
                    affected_tables_set.insert(edge.to.name);
                }
                _ => {}
            }
        }

        // Collect attributes and tables from transitive dependencies
        for dep in self.transitive_dependencies(target, MAX_TRANSITIVE_DEPTH) {
            if dep.kind == ObjectKind::Attribute {
                affected_attrs_set.insert(dep.name.clone());
            } else if dep.kind == ObjectKind::Table {
                affected_tables_set.insert(dep.name.clone());
            }
            for edge in self.direct_dependencies(&dep) {
                match edge.kind {
                    DependencyKind::ReferencesAttribute => {
                        affected_attrs_set.insert(edge.to.name);
                    }
                    DependencyKind::ReadsTable
                    | DependencyKind::UpdatesTable
                    | DependencyKind::InsertsTable
                    | DependencyKind::DeletesTable => {
                        affected_tables_set.insert(edge.to.name);
                    }
                    _ => {}
                }
            }
        }

        let affected_attributes: Vec<String> = affected_attrs_set.into_iter().collect();
        let affected_tables: Vec<String> = affected_tables_set.into_iter().collect();

        let is_core_affected = affected_tables.iter().any(|t| {
            let u = t.to_ascii_uppercase();
            u == "CORE" || u.starts_with("CORE_") || u == "SECURITY" || u.starts_with("SEC_")
        });

        let callers_count = transitive_callers.max(direct_callers);
        let risk_level = if callers_count > 10 || is_core_affected {
            RiskLevel::Critical
        } else if callers_count > 5 {
            RiskLevel::High
        } else if callers_count > 2 {
            RiskLevel::Medium
        } else {
            RiskLevel::Low
        };

        BlastRadiusReport {
            target: target.clone(),
            direct_callers,
            transitive_callers,
            affected_attributes,
            affected_tables,
            risk_level,
        }
    }

    /// Indexes objects from a `Catalog`:
    /// - For Procedures: parses body to extract calls, attributes, and table reads/writes/inserts/deletes.
    /// - For Transactions: extracts level attributes, base tables, and child level tables.
    pub fn index_catalog(&mut self, catalog: &Catalog) {
        let derived_tables = crate::table::derive_tables_from_catalog(catalog).unwrap_or_default();
        let extended_tables = crate::navigation::compute_extended_tables(&derived_tables);

        // 1. Index Transactions
        for obj in catalog.objects() {
            if obj.kind == ObjectKind::Transaction {
                self.index_transaction(obj, &derived_tables, catalog);
            }
        }

        // 2. Index Procedures
        for obj in catalog.objects() {
            if obj.kind == ObjectKind::Procedure {
                self.index_procedure(obj, &derived_tables, &extended_tables, catalog);
            }
        }
    }

    fn index_transaction(
        &mut self,
        obj: &crate::catalog::KbObject,
        tables: &[Table],
        catalog: &Catalog,
    ) {
        let trn_ref = ObjectRef::new(ObjectKind::Transaction, &obj.name);

        if let Some(structure_part) = obj.source_parts.iter().find(|p| p.name == "STRUCTURE")
            && let Ok(structure) =
                serde_json::from_str::<TransactionStructure>(&structure_part.source)
        {
            let root_table = structure.root.name.clone();
            self.add_dependency(
                trn_ref.clone(),
                ObjectRef::new(ObjectKind::Table, &root_table),
                DependencyKind::ReadsTable,
            );
            self.add_dependency(
                trn_ref.clone(),
                ObjectRef::new(ObjectKind::Table, &root_table),
                DependencyKind::UpdatesTable,
            );
            self.add_dependency(
                trn_ref.clone(),
                ObjectRef::new(ObjectKind::Table, &root_table),
                DependencyKind::InsertsTable,
            );
            self.add_dependency(
                trn_ref.clone(),
                ObjectRef::new(ObjectKind::Table, &root_table),
                DependencyKind::DeletesTable,
            );

            for child in &structure.root.levels {
                self.add_dependency(
                    trn_ref.clone(),
                    ObjectRef::new(ObjectKind::Table, &child.name),
                    DependencyKind::ReadsTable,
                );
                self.add_dependency(
                    trn_ref.clone(),
                    ObjectRef::new(ObjectKind::Table, &child.name),
                    DependencyKind::UpdatesTable,
                );
                self.add_dependency(
                    trn_ref.clone(),
                    ObjectRef::new(ObjectKind::Table, &child.name),
                    DependencyKind::InsertsTable,
                );
                self.add_dependency(
                    trn_ref.clone(),
                    ObjectRef::new(ObjectKind::Table, &child.name),
                    DependencyKind::DeletesTable,
                );
            }

            for attr in structure.all_attributes() {
                self.add_dependency(
                    trn_ref.clone(),
                    ObjectRef::new(ObjectKind::Attribute, attr),
                    DependencyKind::ReferencesAttribute,
                );
            }
        }

        // Check if any derived table name matches this transaction
        for tbl in tables {
            if tbl.name.eq_ignore_ascii_case(&obj.name) {
                self.add_dependency(
                    trn_ref.clone(),
                    ObjectRef::new(ObjectKind::Table, &tbl.name),
                    DependencyKind::ReadsTable,
                );
                self.add_dependency(
                    trn_ref.clone(),
                    ObjectRef::new(ObjectKind::Table, &tbl.name),
                    DependencyKind::UpdatesTable,
                );
            }
        }

        // References in obj.references
        for r in &obj.references {
            if let Some(target) = catalog.get(&r.target) {
                match target.kind {
                    ObjectKind::Attribute => {
                        self.add_dependency(
                            trn_ref.clone(),
                            ObjectRef::new(ObjectKind::Attribute, &target.name),
                            DependencyKind::ReferencesAttribute,
                        );
                    }
                    ObjectKind::StructuredDataType => {
                        self.add_dependency(
                            trn_ref.clone(),
                            ObjectRef::new(ObjectKind::StructuredDataType, &target.name),
                            DependencyKind::UsesSdt,
                        );
                    }
                    _ => {}
                }
            }
        }
    }

    fn index_procedure(
        &mut self,
        obj: &crate::catalog::KbObject,
        tables: &[Table],
        extended_tables: &BTreeMap<String, ExtendedTable>,
        catalog: &Catalog,
    ) {
        let proc_ref = ObjectRef::new(ObjectKind::Procedure, &obj.name);

        // Explicit references
        for r in &obj.references {
            if let Some(target) = catalog.get(&r.target) {
                match target.kind {
                    ObjectKind::Procedure => {
                        self.add_dependency(
                            proc_ref.clone(),
                            ObjectRef::new(ObjectKind::Procedure, &target.name),
                            DependencyKind::CallsProcedure,
                        );
                    }
                    ObjectKind::StructuredDataType => {
                        self.add_dependency(
                            proc_ref.clone(),
                            ObjectRef::new(ObjectKind::StructuredDataType, &target.name),
                            DependencyKind::UsesSdt,
                        );
                    }
                    ObjectKind::Attribute => {
                        self.add_dependency(
                            proc_ref.clone(),
                            ObjectRef::new(ObjectKind::Attribute, &target.name),
                            DependencyKind::ReferencesAttribute,
                        );
                    }
                    ObjectKind::Table => {
                        self.add_dependency(
                            proc_ref.clone(),
                            ObjectRef::new(ObjectKind::Table, &target.name),
                            DependencyKind::ReadsTable,
                        );
                    }
                    _ => {}
                }
            }
        }

        // Source part
        if let Some(part) = obj
            .source_parts
            .iter()
            .find(|p| p.name.eq_ignore_ascii_case("Source"))
        {
            // Check for SDT mentions in source
            for catalog_obj in catalog.objects() {
                if catalog_obj.kind == ObjectKind::StructuredDataType
                    && part.source.contains(&catalog_obj.name)
                {
                    self.add_dependency(
                        proc_ref.clone(),
                        ObjectRef::new(ObjectKind::StructuredDataType, &catalog_obj.name),
                        DependencyKind::UsesSdt,
                    );
                }
            }

            if let Ok(stmts) = crate::frontend::parse_body(&part.source) {
                // Calls
                let callees = crate::dependencies::calls(&stmts);
                for callee in callees {
                    self.add_dependency(
                        proc_ref.clone(),
                        ObjectRef::new(ObjectKind::Procedure, callee),
                        DependencyKind::CallsProcedure,
                    );
                }

                // Statements
                self.index_stmts_recursive(&proc_ref, &stmts, tables, extended_tables, catalog);
            }
        }
    }

    fn index_stmts_recursive(
        &mut self,
        proc_ref: &ObjectRef,
        stmts: &[Stmt],
        tables: &[Table],
        extended_tables: &BTreeMap<String, ExtendedTable>,
        catalog: &Catalog,
    ) {
        for stmt in stmts {
            match stmt {
                Stmt::AssignAttribute(attr, expr) => {
                    self.add_dependency(
                        proc_ref.clone(),
                        ObjectRef::new(ObjectKind::Attribute, attr),
                        DependencyKind::ReferencesAttribute,
                    );
                    self.index_expr(proc_ref, expr);
                }
                Stmt::Assign(_, expr)
                | Stmt::Print(expr)
                | Stmt::MsgStatus(expr)
                | Stmt::Return(expr)
                | Stmt::Call(expr) => {
                    self.index_expr(proc_ref, expr);
                }
                Stmt::If(cond, then_b, else_b) => {
                    self.index_expr(proc_ref, cond);
                    self.index_stmts_recursive(proc_ref, then_b, tables, extended_tables, catalog);
                    self.index_stmts_recursive(proc_ref, else_b, tables, extended_tables, catalog);
                }
                Stmt::While(cond, body) => {
                    self.index_expr(proc_ref, cond);
                    self.index_stmts_recursive(proc_ref, body, tables, extended_tables, catalog);
                }
                Stmt::For(_, init, limit, _, body) => {
                    self.index_expr(proc_ref, init);
                    self.index_expr(proc_ref, limit);
                    self.index_stmts_recursive(proc_ref, body, tables, extended_tables, catalog);
                }
                Stmt::Sub(_, body) => {
                    self.index_stmts_recursive(proc_ref, body, tables, extended_tables, catalog);
                }
                Stmt::ForIn(_, coll, body) => {
                    self.index_expr(proc_ref, coll);
                    self.index_stmts_recursive(proc_ref, body, tables, extended_tables, catalog);
                }
                Stmt::CollectionAdd(r, v, opt_idx) => {
                    self.index_expr(proc_ref, r);
                    self.index_expr(proc_ref, v);
                    if let Some(idx) = opt_idx {
                        self.index_expr(proc_ref, idx);
                    }
                }
                Stmt::CollectionRemove(r, idx) => {
                    self.index_expr(proc_ref, r);
                    self.index_expr(proc_ref, idx);
                }
                Stmt::CollectionClear(r) => {
                    self.index_expr(proc_ref, r);
                }
                Stmt::FromJson(r, j) => {
                    self.index_expr(proc_ref, r);
                    self.index_expr(proc_ref, j);
                }
                Stmt::FromXml(fx) => {
                    self.index_expr(proc_ref, &fx.target);
                    self.index_expr(proc_ref, &fx.xml);
                    if let Some(ref root) = fx.root {
                        self.index_expr(proc_ref, root);
                    }
                }
                Stmt::AssignField(r, _, v) => {
                    self.index_expr(proc_ref, r);
                    self.index_expr(proc_ref, v);
                }
                Stmt::ForEach(for_each) => {
                    for attr in &for_each.explicit_attributes {
                        self.add_dependency(
                            proc_ref.clone(),
                            ObjectRef::new(ObjectKind::Attribute, attr),
                            DependencyKind::ReferencesAttribute,
                        );
                    }
                    for w in &for_each.wheres {
                        self.index_expr(proc_ref, w);
                    }
                    for o in &for_each.orders {
                        self.add_dependency(
                            proc_ref.clone(),
                            ObjectRef::new(ObjectKind::Attribute, &o.attribute),
                            DependencyKind::ReferencesAttribute,
                        );
                    }
                    if let Some(ref d) = for_each.defined_by {
                        self.add_dependency(
                            proc_ref.clone(),
                            ObjectRef::new(ObjectKind::Attribute, d),
                            DependencyKind::ReferencesAttribute,
                        );
                    }

                    // Inferred base table
                    let mut matched_tables = Vec::new();
                    if !tables.is_empty()
                        && let Ok(base_tbl) = infer_base_table(for_each, tables, extended_tables)
                    {
                        matched_tables.push(base_tbl.name.clone());
                    }

                    // Fallback matching if infer_base_table did not resolve
                    if matched_tables.is_empty() {
                        for attr in &for_each.explicit_attributes {
                            for tbl in tables {
                                if (tbl.name.eq_ignore_ascii_case(attr)
                                    || tbl
                                        .columns
                                        .iter()
                                        .any(|c| c.name.eq_ignore_ascii_case(attr)))
                                    && !matched_tables.contains(&tbl.name)
                                {
                                    matched_tables.push(tbl.name.clone());
                                }
                            }
                            for obj in catalog.objects() {
                                if obj.kind == ObjectKind::Transaction
                                    && obj.name.eq_ignore_ascii_case(attr)
                                    && !matched_tables.contains(&obj.name)
                                {
                                    matched_tables.push(obj.name.clone());
                                }
                            }
                        }
                    }

                    for tbl_name in &matched_tables {
                        self.add_dependency(
                            proc_ref.clone(),
                            ObjectRef::new(ObjectKind::Table, tbl_name),
                            DependencyKind::ReadsTable,
                        );
                    }

                    let has_delete = contains_delete(&for_each.body);
                    let has_updates = contains_assign_attribute(&for_each.body);

                    for tbl_name in &matched_tables {
                        if has_delete {
                            self.add_dependency(
                                proc_ref.clone(),
                                ObjectRef::new(ObjectKind::Table, tbl_name),
                                DependencyKind::DeletesTable,
                            );
                        }
                        if has_updates {
                            self.add_dependency(
                                proc_ref.clone(),
                                ObjectRef::new(ObjectKind::Table, tbl_name),
                                DependencyKind::UpdatesTable,
                            );
                        }
                    }

                    self.index_stmts_recursive(
                        proc_ref,
                        &for_each.body,
                        tables,
                        extended_tables,
                        catalog,
                    );
                    self.index_stmts_recursive(
                        proc_ref,
                        &for_each.when_none,
                        tables,
                        extended_tables,
                        catalog,
                    );
                }
                Stmt::New(new_stmt) => {
                    let mut target_tables = Vec::new();
                    if !tables.is_empty()
                        && let Ok(plan) =
                            crate::dml::plan_insert_for_new(new_stmt, tables, SqlDialect::Ansi)
                    {
                        target_tables.push(plan.target_table);
                    }

                    if target_tables.is_empty() {
                        let mut assigned = Vec::new();
                        collect_assigned_attributes(&new_stmt.body, &mut assigned);
                        for tbl in tables {
                            if !assigned.is_empty()
                                && assigned.iter().all(|a| {
                                    tbl.columns.iter().any(|c| c.name.eq_ignore_ascii_case(a))
                                })
                            {
                                target_tables.push(tbl.name.clone());
                                break;
                            }
                        }
                        if target_tables.is_empty() {
                            for obj in catalog.objects() {
                                if obj.kind == ObjectKind::Transaction
                                    && let Some(sp) =
                                        obj.source_parts.iter().find(|p| p.name == "STRUCTURE")
                                    && let Ok(st) =
                                        serde_json::from_str::<TransactionStructure>(&sp.source)
                                    && !assigned.is_empty()
                                    && assigned.iter().any(|a| {
                                        st.all_attributes()
                                            .iter()
                                            .any(|sa| sa.eq_ignore_ascii_case(a))
                                    })
                                {
                                    target_tables.push(st.root.name);
                                    break;
                                }
                            }
                        }
                    }

                    for tbl_name in &target_tables {
                        self.add_dependency(
                            proc_ref.clone(),
                            ObjectRef::new(ObjectKind::Table, tbl_name),
                            DependencyKind::InsertsTable,
                        );
                    }

                    self.index_stmts_recursive(
                        proc_ref,
                        &new_stmt.body,
                        tables,
                        extended_tables,
                        catalog,
                    );
                    self.index_stmts_recursive(
                        proc_ref,
                        &new_stmt.when_duplicate,
                        tables,
                        extended_tables,
                        catalog,
                    );
                }
                Stmt::ReturnVoid
                | Stmt::Exit
                | Stmt::DoSub(_)
                | Stmt::Commit
                | Stmt::Rollback
                | Stmt::Delete => {}
            }
        }
    }

    fn index_expr(&mut self, proc_ref: &ObjectRef, expr: &Expr) {
        match expr {
            Expr::Attribute(name) => {
                self.add_dependency(
                    proc_ref.clone(),
                    ObjectRef::new(ObjectKind::Attribute, name),
                    DependencyKind::ReferencesAttribute,
                );
            }
            Expr::Call(name, args) => {
                if !matches!(
                    name.to_ascii_uppercase().as_str(),
                    "SUM"
                        | "COUNT"
                        | "MAX"
                        | "MIN"
                        | "FIND"
                        | "AVERAGE"
                        | "AVG"
                        | "ROUND"
                        | "TRUNC"
                        | "INT"
                        | "VAL"
                        | "STR"
                ) {
                    self.add_dependency(
                        proc_ref.clone(),
                        ObjectRef::new(ObjectKind::Procedure, name),
                        DependencyKind::CallsProcedure,
                    );
                }
                for arg in args {
                    self.index_expr(proc_ref, arg);
                }
            }
            Expr::Binary(l, _, r) | Expr::DateShift(_, l, r) | Expr::CollectionItem(l, r) => {
                self.index_expr(proc_ref, l);
                self.index_expr(proc_ref, r);
            }
            Expr::Not(e)
            | Expr::Negate(e)
            | Expr::TextLength(e)
            | Expr::DateMethod(_, e)
            | Expr::Member(e, _)
            | Expr::CloneRecord(e) => {
                self.index_expr(proc_ref, e);
            }
            _ => {}
        }
    }
}

fn contains_delete(stmts: &[Stmt]) -> bool {
    stmts.iter().any(|s| match s {
        Stmt::Delete => true,
        Stmt::If(_, t, e) => contains_delete(t) || contains_delete(e),
        Stmt::While(_, b) => contains_delete(b),
        Stmt::For(_, _, _, _, b) => contains_delete(b),
        Stmt::Sub(_, b) => contains_delete(b),
        Stmt::ForEach(fe) => contains_delete(&fe.body),
        _ => false,
    })
}

fn contains_assign_attribute(stmts: &[Stmt]) -> bool {
    stmts.iter().any(|s| match s {
        Stmt::AssignAttribute(_, _) => true,
        Stmt::If(_, t, e) => contains_assign_attribute(t) || contains_assign_attribute(e),
        Stmt::While(_, b) => contains_assign_attribute(b),
        Stmt::For(_, _, _, _, b) => contains_assign_attribute(b),
        Stmt::Sub(_, b) => contains_assign_attribute(b),
        _ => false,
    })
}

fn collect_assigned_attributes(stmts: &[Stmt], out: &mut Vec<String>) {
    for s in stmts {
        match s {
            Stmt::AssignAttribute(name, _) => out.push(name.clone()),
            Stmt::If(_, t, e) => {
                collect_assigned_attributes(t, out);
                collect_assigned_attributes(e, out);
            }
            Stmt::While(_, b) => collect_assigned_attributes(b, out),
            Stmt::For(_, _, _, _, b) => collect_assigned_attributes(b, out),
            _ => {}
        }
    }
}
