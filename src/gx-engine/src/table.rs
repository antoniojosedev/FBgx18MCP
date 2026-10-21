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

mod derive;

pub use derive::{
    derive_tables_from_catalog, derive_tables_from_transaction, derive_tables_partial,
};

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
