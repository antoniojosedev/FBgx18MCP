//! Physical Table schema, relational normalization, and SQL DDL generation.
//!
//! Conforms to Plan 129 Matrix Rows B8 (Table) and H1 (Physical schema and DDL).
//! Derives relational physical tables and foreign keys from GeneXus Transactions,
//! maps GeneXus data types to standard SQL dialects, and emits clean, inspectable
//! DDL without executing any database writes.

use crate::catalog::{Catalog, KbObject, ObjectKind};
use crate::model::DataType;
use crate::transaction::{KeyReference, TransactionLevel, TransactionStructure};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};

/// Supported SQL dialects for DDL generation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SqlDialect {
    /// ANSI/ISO standard SQL.
    Ansi,
    /// Microsoft SQL Server (T-SQL).
    SqlServer,
    /// PostgreSQL.
    PostgreSql,
    /// Oracle Database SQL.
    Oracle,
}

impl SqlDialect {
    /// Quotes an SQL identifier according to the dialect's conventions.
    pub fn quote_identifier(&self, ident: &str) -> String {
        match self {
            SqlDialect::SqlServer => format!("[{ident}]"),
            SqlDialect::PostgreSql | SqlDialect::Ansi => format!("\"{ident}\""),
            SqlDialect::Oracle => ident.to_string(),
        }
    }

    /// Maps a GeneXus `DataType` to the dialect's corresponding SQL type keyword.
    pub fn map_data_type(&self, data_type: &DataType) -> Result<String, String> {
        match data_type {
            DataType::Numeric { precision, scale } => {
                if *scale == 0 {
                    match self {
                        SqlDialect::Oracle => Ok(format!("NUMBER({precision})")),
                        SqlDialect::SqlServer => {
                            if *precision <= 4 {
                                Ok("SMALLINT".into())
                            } else if *precision <= 9 {
                                Ok("INT".into())
                            } else {
                                Ok("BIGINT".into())
                            }
                        }
                        SqlDialect::PostgreSql | SqlDialect::Ansi => {
                            if *precision <= 4 {
                                Ok("SMALLINT".into())
                            } else if *precision <= 9 {
                                Ok("INTEGER".into())
                            } else {
                                Ok("BIGINT".into())
                            }
                        }
                    }
                } else {
                    match self {
                        SqlDialect::Oracle => Ok(format!("NUMBER({precision}, {scale})")),
                        SqlDialect::PostgreSql => Ok(format!("NUMERIC({precision}, {scale})")),
                        SqlDialect::SqlServer | SqlDialect::Ansi => {
                            Ok(format!("DECIMAL({precision}, {scale})"))
                        }
                    }
                }
            }
            DataType::Character { length } => Ok(format!("CHAR({length})")),
            // No DDL type for these is evidenced for any dialect; a column of one is refused.
            DataType::LongVarchar { .. }
            | DataType::Guid {}
            | DataType::Image {}
            | DataType::Geospatial {} => Err(format!(
                "no evidenced SQL column type for {data_type:?} in this dialect"
            )),
            DataType::Varchar { length } => match self {
                SqlDialect::Oracle => Ok(format!("VARCHAR2({length})")),
                SqlDialect::SqlServer | SqlDialect::PostgreSql | SqlDialect::Ansi => {
                    Ok(format!("VARCHAR({length})"))
                }
            },
            DataType::Boolean {} => match self {
                SqlDialect::Oracle => Ok("NUMBER(1)".into()),
                SqlDialect::SqlServer => Ok("BIT".into()),
                SqlDialect::PostgreSql | SqlDialect::Ansi => Ok("BOOLEAN".into()),
            },
            DataType::Date {} => Ok("DATE".into()),
            DataType::DateTime {} => match self {
                SqlDialect::SqlServer => Ok("DATETIME2".into()),
                SqlDialect::Oracle | SqlDialect::PostgreSql | SqlDialect::Ansi => {
                    Ok("TIMESTAMP".into())
                }
            },
            DataType::Record { schema }
            | DataType::Collection { schema }
            | DataType::BaseCollection { schema, .. } => Err(format!(
                "cannot map complex type '{schema}' directly to relational table column"
            )),
        }
    }
}

/// A physical column in a database table.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TableColumn {
    pub name: String,
    pub data_type: DataType,
    #[serde(default)]
    pub is_key: bool,
    #[serde(default)]
    pub is_nullable: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
}

impl TableColumn {
    pub fn new(name: impl Into<String>, data_type: DataType) -> Self {
        Self {
            name: name.into(),
            data_type,
            is_key: false,
            is_nullable: false,
            description: None,
        }
    }

    pub fn key(mut self, is_key: bool) -> Self {
        self.is_key = is_key;
        if is_key {
            self.is_nullable = false;
        }
        self
    }

    pub fn nullable(mut self, is_nullable: bool) -> Self {
        if !self.is_key {
            self.is_nullable = is_nullable;
        }
        self
    }

    pub fn with_description(mut self, desc: impl Into<String>) -> Self {
        self.description = Some(desc.into());
        self
    }

    /// Renders the SQL definition for this column, e.g. `[CustomerId] INT NOT NULL`.
    pub fn to_column_sql(&self, dialect: SqlDialect) -> Result<String, String> {
        let col_name = dialect.quote_identifier(&self.name);
        let type_sql = dialect.map_data_type(&self.data_type)?;
        let null_sql = if self.is_nullable && !self.is_key {
            "NULL"
        } else {
            "NOT NULL"
        };
        Ok(format!("{col_name} {type_sql} {null_sql}"))
    }
}

/// A foreign key constraint linking columns to a parent table.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TableForeignKey {
    pub name: String,
    pub columns: Vec<String>,
    pub referenced_table: String,
    pub referenced_columns: Vec<String>,
}

impl TableForeignKey {
    pub fn new(
        name: impl Into<String>,
        columns: Vec<String>,
        referenced_table: impl Into<String>,
        referenced_columns: Vec<String>,
    ) -> Self {
        Self {
            name: name.into(),
            columns,
            referenced_table: referenced_table.into(),
            referenced_columns,
        }
    }

    /// Renders the SQL `CONSTRAINT ... FOREIGN KEY ... REFERENCES ...` clause.
    pub fn to_constraint_sql(&self, dialect: SqlDialect) -> String {
        let fk_name = dialect.quote_identifier(&self.name);
        let ref_tbl = dialect.quote_identifier(&self.referenced_table);
        let cols = self
            .columns
            .iter()
            .map(|c| dialect.quote_identifier(c))
            .collect::<Vec<_>>()
            .join(", ");
        let ref_cols = self
            .referenced_columns
            .iter()
            .map(|c| dialect.quote_identifier(c))
            .collect::<Vec<_>>()
            .join(", ");
        format!("CONSTRAINT {fk_name} FOREIGN KEY ({cols}) REFERENCES {ref_tbl} ({ref_cols})")
    }
}

/// A physical index on a database table.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TableIndex {
    pub name: String,
    #[serde(default)]
    pub is_unique: bool,
    #[serde(default)]
    pub is_primary: bool,
    pub columns: Vec<String>,
}

impl TableIndex {
    pub fn new(name: impl Into<String>, columns: Vec<String>, is_unique: bool) -> Self {
        Self {
            name: name.into(),
            is_unique,
            is_primary: false,
            columns,
        }
    }

    /// Renders a standalone `CREATE [UNIQUE] INDEX ... ON ... (...)` statement.
    pub fn to_create_sql(&self, table_name: &str, dialect: SqlDialect) -> String {
        let idx_name = dialect.quote_identifier(&self.name);
        let tbl_name = dialect.quote_identifier(table_name);
        let unique = if self.is_unique { "UNIQUE " } else { "" };
        let cols = self
            .columns
            .iter()
            .map(|c| dialect.quote_identifier(c))
            .collect::<Vec<_>>()
            .join(", ");
        format!("CREATE {unique}INDEX {idx_name} ON {tbl_name} ({cols});")
    }
}

/// A physical relational table derived from a GeneXus Transaction level.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Table {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    pub columns: Vec<TableColumn>,
    pub primary_key: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub foreign_keys: Vec<TableForeignKey>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub indexes: Vec<TableIndex>,
}

impl Table {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            description: None,
            columns: Vec::new(),
            primary_key: Vec::new(),
            foreign_keys: Vec::new(),
            indexes: Vec::new(),
        }
    }

    pub fn with_description(mut self, desc: impl Into<String>) -> Self {
        self.description = Some(desc.into());
        self
    }

    pub fn add_column(&mut self, col: TableColumn) {
        if col.is_key
            && !self
                .primary_key
                .iter()
                .any(|k| k.eq_ignore_ascii_case(&col.name))
        {
            self.primary_key.push(col.name.clone());
        }
        self.columns.push(col);
    }

    pub fn add_foreign_key(&mut self, fk: TableForeignKey) {
        self.foreign_keys.push(fk);
    }

    pub fn add_index(&mut self, idx: TableIndex) {
        self.indexes.push(idx);
    }

    /// Validates table hygiene: identifier length, character restrictions,
    /// unique columns, required primary key, and reference consistency.
    pub fn validate(&self) -> Result<(), String> {
        let normalized = self.name.trim();
        if normalized.is_empty() {
            return Err("Table name cannot be empty".into());
        }
        if normalized.len() > 128 {
            return Err(format!("Table name '{normalized}' exceeds 128 bytes"));
        }
        if !normalized
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '_')
        {
            return Err(format!(
                "Table name '{normalized}' contains invalid characters (must be ASCII alphanumeric or '_')"
            ));
        }

        if self.columns.is_empty() {
            return Err(format!(
                "Table '{normalized}' must have at least one column"
            ));
        }
        if self.columns.len() > 1024 {
            return Err(format!(
                "Table '{normalized}' exceeds limit of 1024 columns"
            ));
        }

        let mut seen_cols = BTreeSet::new();
        for col in &self.columns {
            let col_name = col.name.trim();
            if col_name.is_empty() {
                return Err(format!(
                    "Table '{normalized}' contains a column with an empty name"
                ));
            }
            if col_name.len() > 128 {
                return Err(format!(
                    "Column name '{col_name}' in Table '{normalized}' exceeds 128 bytes"
                ));
            }
            if !col_name
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || c == '_')
            {
                return Err(format!(
                    "Column name '{col_name}' in Table '{normalized}' contains invalid characters"
                ));
            }
            let key = col_name.to_ascii_uppercase();
            if !seen_cols.insert(key) {
                return Err(format!(
                    "duplicate column '{col_name}' in Table '{normalized}'"
                ));
            }
            crate::model::validate_data_type(&col.data_type)?;
        }

        if self.primary_key.is_empty() {
            return Err(format!(
                "Table '{normalized}' must have at least one primary key column"
            ));
        }

        for pk in &self.primary_key {
            let pk_upper = pk.to_ascii_uppercase();
            let col = self
                .columns
                .iter()
                .find(|c| c.name.to_ascii_uppercase() == pk_upper)
                .ok_or_else(|| {
                    format!("primary key column '{pk}' not found in Table '{normalized}'")
                })?;
            if !col.is_key {
                return Err(format!(
                    "column '{pk}' is declared in primary_key but has is_key = false in Table '{normalized}'"
                ));
            }
            if col.is_nullable {
                return Err(format!(
                    "primary key column '{pk}' in Table '{normalized}' cannot be nullable"
                ));
            }
        }

        for fk in &self.foreign_keys {
            if fk.columns.is_empty() {
                return Err(format!(
                    "Foreign key '{}' in Table '{normalized}' has no columns",
                    fk.name
                ));
            }
            if fk.columns.len() != fk.referenced_columns.len() {
                return Err(format!(
                    "Foreign key '{}' column count ({}) does not match referenced column count ({})",
                    fk.name,
                    fk.columns.len(),
                    fk.referenced_columns.len()
                ));
            }
            for col_name in &fk.columns {
                if !seen_cols.contains(&col_name.to_ascii_uppercase()) {
                    return Err(format!(
                        "Foreign key '{}' references unknown column '{col_name}' in Table '{normalized}'",
                        fk.name
                    ));
                }
            }
        }

        for idx in &self.indexes {
            if idx.columns.is_empty() {
                return Err(format!(
                    "Index '{}' in Table '{normalized}' has no columns",
                    idx.name
                ));
            }
            for col_name in &idx.columns {
                if !seen_cols.contains(&col_name.to_ascii_uppercase()) {
                    return Err(format!(
                        "Index '{}' references unknown column '{col_name}' in Table '{normalized}'",
                        idx.name
                    ));
                }
            }
        }

        Ok(())
    }

    /// Renders the `CREATE TABLE ...` DDL statement with primary key and foreign keys.
    pub fn to_create_table_ddl(&self, dialect: SqlDialect) -> Result<String, String> {
        let tbl_name = dialect.quote_identifier(&self.name);
        let mut lines = Vec::new();

        for col in &self.columns {
            lines.push(format!("  {}", col.to_column_sql(dialect)?));
        }

        if !self.primary_key.is_empty() {
            let pk_cols = self
                .primary_key
                .iter()
                .map(|c| dialect.quote_identifier(c))
                .collect::<Vec<_>>()
                .join(", ");
            lines.push(format!("  PRIMARY KEY ({pk_cols})"));
        }

        for fk in &self.foreign_keys {
            lines.push(format!("  {}", fk.to_constraint_sql(dialect)));
        }

        let body = lines.join(",\n");
        Ok(format!("CREATE TABLE {tbl_name} (\n{body}\n);"))
    }

    /// Renders standalone SQL statements for non-primary indexes.
    pub fn to_indexes_ddl(&self, dialect: SqlDialect) -> String {
        let mut stmts = Vec::new();
        for idx in &self.indexes {
            if !idx.is_primary {
                stmts.push(idx.to_create_sql(&self.name, dialect));
            }
        }
        stmts.join("\n")
    }

    /// Renders complete DDL for this table, including `CREATE TABLE` and any secondary indexes.
    pub fn to_full_ddl(&self, dialect: SqlDialect) -> Result<String, String> {
        let create_sql = self.to_create_table_ddl(dialect)?;
        let idx_sql = self.to_indexes_ddl(dialect);
        if idx_sql.is_empty() {
            Ok(create_sql)
        } else {
            Ok(format!("{create_sql}\n\n{idx_sql}"))
        }
    }
}

/// Derives relational physical `Table` schemas from a `TransactionStructure`.
///
/// Follows GeneXus relational table derivation:
/// - Root level maps to the root Table with its primary key.
/// - Child levels inherit parent primary keys as both primary key parts and Foreign Keys.
/// - Formula attributes are omitted from physical storage.
/// - Attribute data types are resolved using the provided `attribute_resolver` when not inline.
pub fn derive_tables_from_transaction(
    structure: &TransactionStructure,
    attribute_resolver: impl Fn(&str) -> Option<DataType>,
) -> Result<Vec<Table>, String> {
    structure.validate()?;
    let mut tables = Vec::new();
    let inherited_keys = Vec::new();
    derive_level_table(
        &structure.root,
        None,
        &inherited_keys,
        &attribute_resolver,
        &mut tables,
        0,
    )?;
    Ok(tables)
}

fn derive_level_table(
    level: &TransactionLevel,
    parent_table_name: Option<&str>,
    inherited_keys: &[TableColumn],
    resolver: &impl Fn(&str) -> Option<DataType>,
    tables: &mut Vec<Table>,
    depth: usize,
) -> Result<(), String> {
    if depth > 16 {
        return Err("Transaction level nesting depth exceeds limit of 16".into());
    }

    let table_name = match parent_table_name {
        None => level.name.clone(),
        Some(parent) => {
            if level
                .name
                .to_ascii_uppercase()
                .starts_with(&parent.to_ascii_uppercase())
            {
                level.name.clone()
            } else {
                format!("{parent}{}", level.name)
            }
        }
    };

    let mut columns: Vec<TableColumn> = Vec::new();
    let mut primary_key: Vec<String> = Vec::new();
    let mut seen_col_names = BTreeSet::new();

    // 1. Add inherited parent primary keys (for child tables)
    for parent_key in inherited_keys.iter() {
        let mut key_col = parent_key.clone();
        key_col.is_key = true;
        key_col.is_nullable = false;
        primary_key.push(key_col.name.clone());
        seen_col_names.insert(key_col.name.to_ascii_uppercase());
        columns.push(key_col);
    }

    // 2. Add attributes from this level
    for attr in &level.attributes {
        // Skip formula attributes (in GeneXus formulas are virtual/non-stored)
        if attr.formula.is_some() {
            continue;
        }

        let upper_name = attr.name.to_ascii_uppercase();
        if seen_col_names.contains(&upper_name) {
            // Already present from inherited keys
            continue;
        }

        let data_type = if let Some(ref dt) = attr.data_type {
            dt.clone()
        } else if let Some(dt) = resolver(&attr.name) {
            dt
        } else {
            return Err(format!(
                "cannot resolve data type for attribute '{}' in level '{}'",
                attr.name, level.name
            ));
        };

        let col = TableColumn {
            name: attr.name.clone(),
            data_type,
            is_key: attr.is_key,
            is_nullable: if attr.is_key { false } else { attr.is_nullable },
            description: None,
        };

        if attr.is_key {
            primary_key.push(col.name.clone());
        }

        seen_col_names.insert(upper_name);
        columns.push(col);
    }

    // 3. Foreign key to parent table (for child tables)
    let mut foreign_keys = Vec::new();
    if let Some(parent) = parent_table_name.filter(|_| !inherited_keys.is_empty()) {
        let parent_pk_names: Vec<String> = inherited_keys.iter().map(|k| k.name.clone()).collect();
        let fk_name = format!("FK_{table_name}_{parent}");
        foreign_keys.push(TableForeignKey::new(
            fk_name,
            parent_pk_names.clone(),
            parent,
            parent_pk_names,
        ));
    }

    // 4. Primary index
    let mut indexes = Vec::new();
    let pk_index_name = format!("U{table_name}");
    let mut pk_index = TableIndex::new(pk_index_name, primary_key.clone(), true);
    pk_index.is_primary = true;
    indexes.push(pk_index);

    let table = Table {
        name: table_name.clone(),
        description: level.description.clone(),
        columns,
        primary_key,
        foreign_keys,
        indexes,
    };
    table.validate()?;
    tables.push(table);

    // 5. Descend to child levels with accumulated primary keys
    let mut current_level_keys = Vec::new();
    for col in &tables.last().unwrap().columns {
        if col.is_key {
            current_level_keys.push(col.clone());
        }
    }

    for child in &level.levels {
        derive_level_table(
            child,
            Some(&table_name),
            &current_level_keys,
            resolver,
            tables,
            depth + 1,
        )?;
    }

    Ok(())
}

/// Derives all physical tables across all Transactions in a `Catalog`.
///
/// Automatically resolves attribute types through Catalog Attributes and Domains,
/// infers cross-transaction foreign keys where non-key attributes match another table's primary key,
/// and returns tables in dependency order.
pub fn derive_tables_from_catalog(catalog: &Catalog) -> Result<Vec<Table>, String> {
    let (tables, failures) = derive_tables_partial(catalog);
    match failures.first() {
        None => Ok(tables),
        Some(_) => Err(failures.join("; ")),
    }
}

/// Derives the physical tables of every Transaction that can be derived, and names each one that cannot.
///
/// A Transaction is derived or refused on its own: one refused Transaction does not stop the others. Each failure
/// is one message naming its Transaction, in catalog order. A Transaction whose key references cannot be resolved
/// is refused the same way (see `resolve_key_references`). The tables returned are those of the Transactions that
/// derived, with their key references and the cross-Transaction foreign keys inferred among them.
pub fn derive_tables_partial(catalog: &Catalog) -> (Vec<Table>, Vec<String>) {
    // Map attribute name -> DataType using catalog Attributes and Domains
    let resolve_attr = |name: &str| -> Option<DataType> {
        let mut visited = BTreeSet::new();
        crate::kb::resolve_domain_or_attribute_data_type(catalog, name, &mut visited, 0).ok()
    };

    // One outcome per Transaction, in catalog order: its tables, or the reason they cannot be derived.
    let mut outcomes: Vec<Result<DerivedTransaction, String>> = Vec::new();
    for obj in catalog.objects() {
        if obj.kind != ObjectKind::Transaction {
            continue;
        }
        outcomes.push(derive_transaction_tables(obj, &resolve_attr));
    }
    resolve_key_references(&mut outcomes);

    let mut all_tables = Vec::new();
    let mut table_names = BTreeSet::new();
    let mut failures = Vec::new();
    for outcome in outcomes {
        match outcome {
            Ok(derived) => {
                for tbl in derived.tables {
                    let key = tbl.name.to_ascii_uppercase();
                    if table_names.insert(key) {
                        all_tables.push(tbl);
                    }
                }
            }
            Err(reason) => failures.push(reason),
        }
    }

    // Infer cross-transaction foreign keys:
    // If table A has a non-key column whose name matches the single primary key of table B,
    // and no foreign key exists yet, add a foreign key and index!
    let mut primary_keys: BTreeMap<String, (String, String)> = BTreeMap::new();
    for tbl in &all_tables {
        if tbl.primary_key.len() == 1 {
            let pk_name = tbl.primary_key[0].clone();
            primary_keys.insert(pk_name.to_ascii_uppercase(), (tbl.name.clone(), pk_name));
        }
    }

    for tbl in &mut all_tables {
        let current_table_name = tbl.name.clone();
        for col in &tbl.columns {
            if col.is_key {
                continue;
            }
            let col_upper = col.name.to_ascii_uppercase();
            if let Some((target_tbl, target_pk)) = primary_keys.get(&col_upper) {
                if target_tbl.eq_ignore_ascii_case(&current_table_name) {
                    continue;
                }
                // Check if FK already exists
                let already_exists = tbl
                    .foreign_keys
                    .iter()
                    .any(|fk| fk.referenced_table.eq_ignore_ascii_case(target_tbl));
                if !already_exists {
                    let fk_name = format!("FK_{current_table_name}_{target_tbl}");
                    tbl.foreign_keys.push(TableForeignKey::new(
                        fk_name,
                        vec![col.name.clone()],
                        target_tbl.clone(),
                        vec![target_pk.clone()],
                    ));
                    let idx_name = format!("I{current_table_name}_{target_tbl}");
                    tbl.indexes
                        .push(TableIndex::new(idx_name, vec![col.name.clone()], false));
                }
            }
        }
    }

    (all_tables, failures)
}

/// A Transaction's resolved key references: the index of its root table, and each foreign key with the name of
/// the Transaction it targets.
type ResolvedKeys = (usize, Vec<(TableForeignKey, String)>);

/// One Transaction's derived tables, with the key references its structure declares.
struct DerivedTransaction {
    name: String,
    tables: Vec<Table>,
    key_references: Vec<KeyReference>,
}

/// Resolves the key references of the Transactions that derived, and attaches each resolved one to the root table
/// of its Transaction as a foreign key.
///
/// A reference is refused when it names no derived table's whole primary key, names more than one (ambiguous),
/// names a key of another type, or names the same table twice. A Transaction whose references lead back to itself
/// through the others is refused as a cycle. A refused Transaction becomes a failure in place, and the others are
/// resolved again, because a refused target is no longer derived. This repeats until no Transaction is refused.
/// Nothing is defaulted, and no reference points at a table that is not derived.
fn resolve_key_references(outcomes: &mut [Result<DerivedTransaction, String>]) {
    loop {
        // Each Transaction that declares references: its index, its name, and its root table and links, or why not.
        let mut planned: Vec<(usize, String, Result<ResolvedKeys, String>)> = Vec::new();
        {
            let candidates: Vec<(&str, &Table)> = outcomes
                .iter()
                .filter_map(|outcome| outcome.as_ref().ok())
                .flat_map(|derived| {
                    derived
                        .tables
                        .iter()
                        .map(move |table| (derived.name.as_str(), table))
                })
                .collect();
            for (index, outcome) in outcomes.iter().enumerate() {
                let Ok(owner) = outcome else { continue };
                if owner.key_references.is_empty() {
                    continue;
                }
                planned.push((
                    index,
                    owner.name.clone(),
                    foreign_keys_of(owner, &candidates),
                ));
            }
        }

        // The Transaction-level graph of the references that resolved: owner -> the Transaction each one names.
        let mut edges: BTreeMap<String, Vec<String>> = BTreeMap::new();
        for (_, name, links) in &planned {
            if let Ok((_, links)) = links {
                edges.insert(
                    name.to_ascii_uppercase(),
                    links
                        .iter()
                        .map(|(_, target)| target.to_ascii_uppercase())
                        .collect(),
                );
            }
        }

        let mut refused: Vec<(usize, String)> = Vec::new();
        for (index, name, links) in &planned {
            match links {
                Err(reason) => refused.push((*index, format!("Transaction '{name}': {reason}"))),
                Ok(_) if reaches_itself(&name.to_ascii_uppercase(), &edges) => refused.push((
                    *index,
                    format!(
                        "Transaction '{name}': its key references form a cycle through '{name}'"
                    ),
                )),
                Ok(_) => {}
            }
        }

        if refused.is_empty() {
            // Every planned Transaction resolved, so every plan is `Ok`: the foreign keys go on the root table
            // whose index was found while planning, and nothing is attached to any other table.
            for (index, _, links) in planned {
                let Ok((root_index, links)) = links else {
                    continue;
                };
                if let Ok(owner) = &mut outcomes[index] {
                    owner.tables[root_index]
                        .foreign_keys
                        .extend(links.into_iter().map(|(fk, _)| fk));
                }
            }
            return;
        }
        for (index, reason) in refused {
            outcomes[index] = Err(reason);
        }
    }
}

/// The foreign keys that one Transaction's key references make, each with the name of the Transaction it targets.
/// The result also gives the index of the Transaction's root table, where the keys go.
///
/// A reference targets the one derived table whose whole primary key is the referenced attribute. Zero matches, more
/// than one match, a key of another type, or a second reference to the same table each refuse the Transaction.
fn foreign_keys_of(
    owner: &DerivedTransaction,
    candidates: &[(&str, &Table)],
) -> Result<ResolvedKeys, String> {
    let root_index = owner
        .tables
        .iter()
        .position(|table| table.name.eq_ignore_ascii_case(&owner.name))
        .ok_or_else(|| "its root table was not derived".to_string())?;
    let root = &owner.tables[root_index];
    let mut links: Vec<(TableForeignKey, String)> = Vec::new();
    for reference in &owner.key_references {
        let column = root
            .columns
            .iter()
            .find(|c| c.name.eq_ignore_ascii_case(&reference.attribute))
            .ok_or_else(|| {
                format!(
                    "key '{}' is not a column of its root table",
                    reference.attribute
                )
            })?;
        let matching: Vec<&(&str, &Table)> = candidates
            .iter()
            .filter(|(_, table)| {
                matches!(
                    table.primary_key.as_slice(),
                    [only] if only.eq_ignore_ascii_case(&reference.references)
                )
            })
            .collect();
        let (target_txn, target) = match matching.as_slice() {
            [] => {
                return Err(format!(
                    "its primary key references {}, which is not the whole primary key of any derived table",
                    reference.references
                ));
            }
            [single] => (single.0, single.1),
            many => {
                let names: Vec<&str> = many.iter().map(|(_, table)| table.name.as_str()).collect();
                return Err(format!(
                    "its primary key references {}, which is the whole primary key of {} tables ({}), so the target is ambiguous",
                    reference.references,
                    many.len(),
                    names.join(", ")
                ));
            }
        };
        let target_column = target
            .columns
            .iter()
            .find(|c| c.name.eq_ignore_ascii_case(&reference.references))
            .ok_or_else(|| {
                format!(
                    "the referenced key {} is not a column of '{}'",
                    reference.references, target.name
                )
            })?;
        if target_column.data_type != column.data_type {
            return Err(format!(
                "its key '{}' has type {:?}, but the key it references, {} of '{}', has type {:?}",
                reference.attribute,
                column.data_type,
                reference.references,
                target.name,
                target_column.data_type
            ));
        }
        if links
            .iter()
            .any(|(fk, _)| fk.referenced_table.eq_ignore_ascii_case(&target.name))
        {
            return Err(format!(
                "two of its key references name table '{}'",
                target.name
            ));
        }
        links.push((
            TableForeignKey::new(
                format!("FK_{}_{}", root.name, target.name),
                vec![column.name.clone()],
                target.name.clone(),
                vec![target_column.name.clone()],
            ),
            target_txn.to_string(),
        ));
    }
    Ok((root_index, links))
}

/// Whether `start` reaches itself through the references: a cycle. `edges` holds upper-case Transaction names.
fn reaches_itself(start: &str, edges: &BTreeMap<String, Vec<String>>) -> bool {
    let mut stack: Vec<&str> = edges
        .get(start)
        .map(|next| next.iter().map(String::as_str).collect())
        .unwrap_or_default();
    let mut seen: BTreeSet<&str> = BTreeSet::new();
    while let Some(node) = stack.pop() {
        if node == start {
            return true;
        }
        if !seen.insert(node) {
            continue;
        }
        if let Some(next) = edges.get(node) {
            stack.extend(next.iter().map(String::as_str));
        }
    }
    false
}

/// Derives one Transaction's tables. A refused Transaction, or one with no STRUCTURE part, is an error that
/// names it; the refusal says why its tables cannot be derived.
fn derive_transaction_tables(
    obj: &KbObject,
    resolve_attr: &dyn Fn(&str) -> Option<DataType>,
) -> Result<DerivedTransaction, String> {
    let Some(structure_part) = obj.source_parts.iter().find(|p| p.name == "STRUCTURE") else {
        // A Transaction the reader refused holds no STRUCTURE part; its refusal names the element
        // that cannot be decoded, and is the reason its tables cannot be derived.
        return Err(match &obj.refusal {
            Some(reason) => format!(
                "Transaction '{}' is refused, so its tables cannot be derived: {reason}",
                obj.name
            ),
            None => format!(
                "Transaction '{}' requires a STRUCTURE part to derive tables",
                obj.name
            ),
        });
    };

    let structure: TransactionStructure = serde_json::from_str(&structure_part.source)
        .map_err(|e| format!("invalid Transaction STRUCTURE on '{}': {e}", obj.name))?;

    let tables = derive_tables_from_transaction(&structure, resolve_attr)
        .map_err(|e| format!("Transaction '{}': {e}", obj.name))?;
    Ok(DerivedTransaction {
        name: obj.name.clone(),
        tables,
        key_references: structure.key_references,
    })
}

/// Generates complete SQL DDL for a set of tables in dependency order.
pub fn generate_database_ddl(tables: &[Table], dialect: SqlDialect) -> Result<String, String> {
    let mut sections = Vec::new();
    sections.push(format!(
        "-- Physical DDL generated by gx-engine ({:?} dialect)\n-- Total tables: {}",
        dialect,
        tables.len()
    ));

    let mut pending: Vec<&Table> = tables.iter().collect();
    let mut created = BTreeSet::new();
    let mut names = BTreeSet::new();
    for table in tables {
        table.validate()?;
        if !names.insert(table.name.to_ascii_uppercase()) {
            return Err(format!("duplicate table '{}' in database DDL", table.name));
        }
    }
    for table in tables {
        for fk in &table.foreign_keys {
            if !names.contains(&fk.referenced_table.to_ascii_uppercase()) {
                return Err(format!(
                    "Table '{}' references missing table '{}'",
                    table.name, fk.referenced_table
                ));
            }
        }
    }
    while !pending.is_empty() {
        let next = pending
            .iter()
            .position(|table| {
                table.foreign_keys.iter().all(|fk| {
                    fk.referenced_table.eq_ignore_ascii_case(&table.name)
                        || created.contains(&fk.referenced_table.to_ascii_uppercase())
                })
            })
            .ok_or_else(|| {
                format!(
                    "cyclic foreign-key dependencies among tables: {}",
                    pending
                        .iter()
                        .map(|table| table.name.as_str())
                        .collect::<Vec<_>>()
                        .join(", ")
                )
            })?;
        let table = pending.remove(next);
        sections.push(table.to_full_ddl(dialect)?);
        created.insert(table.name.to_ascii_uppercase());
    }

    Ok(sections.join("\n\n"))
}
