//! DataStores and DBMS Provider Profiles Engine (Row H3).
//!
//! Models GeneXus DataStores (Default, GAM, secondary), database provider
//! profiles (SQL Server, PostgreSQL, MySQL, Oracle, DB2, Informix), connection
//! parameters, dialect properties, and ADO.NET connection string generation.

use serde::{Deserialize, Serialize};

use crate::dataview::strip_comments;

/// Supported DBMS providers in GeneXus Knowledge Bases.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DbmsProvider {
    SqlServer,
    PostgreSql,
    MySql,
    Oracle,
    Db2,
    Informix,
}

impl DbmsProvider {
    pub fn parse(s: &str) -> Result<Self, String> {
        match s.to_ascii_lowercase().as_str() {
            "sqlserver" | "sql server" | "mssql" => Ok(Self::SqlServer),
            "postgresql" | "postgres" | "pgsql" => Ok(Self::PostgreSql),
            "mysql" | "mariadb" => Ok(Self::MySql),
            "oracle" => Ok(Self::Oracle),
            "db2" | "ibm db2" => Ok(Self::Db2),
            "informix" => Ok(Self::Informix),
            _ => Err(format!("Unsupported DBMS provider '{s}'")),
        }
    }

    pub fn default_port(&self) -> u16 {
        match self {
            Self::SqlServer => 1433,
            Self::PostgreSql => 5432,
            Self::MySql => 3306,
            Self::Oracle => 1521,
            Self::Db2 => 50000,
            Self::Informix => 9088,
        }
    }

    pub fn default_schema(&self) -> &'static str {
        match self {
            Self::SqlServer => "dbo",
            Self::PostgreSql => "public",
            Self::MySql => "",
            Self::Oracle => "",
            Self::Db2 => "",
            Self::Informix => "",
        }
    }
}

/// Dialect and feature settings for a specific DBMS provider profile.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DialectSettings {
    pub quote_identifiers: bool,
    pub date_format: String,
    pub identity_clause: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_prefix: Option<String>,
}

impl Default for DialectSettings {
    fn default() -> Self {
        Self {
            quote_identifiers: true,
            date_format: "YMD".to_string(),
            identity_clause: "IDENTITY".to_string(),
            schema_prefix: None,
        }
    }
}

/// A DataStore definition representing database connection settings and options.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DataStore {
    pub name: String,
    pub provider: DbmsProvider,
    pub server: String,
    pub database: String,
    pub port: u16,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema: Option<String>,
    pub user: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub password: Option<String>,
    #[serde(default)]
    pub integrated_security: bool,
    #[serde(default)]
    pub pooling: bool,
    #[serde(default = "default_timeout")]
    pub connection_timeout: u32,
    #[serde(default)]
    pub dialect: DialectSettings,
}

fn default_timeout() -> u32 {
    30
}

impl DataStore {
    /// Parses a DataStore from DSL or JSON text.
    pub fn parse(name: &str, source: &str) -> Result<Self, String> {
        let clean = strip_comments(source);
        let trimmed = clean.trim();

        if trimmed.is_empty() {
            return Err("DataStore source cannot be empty".to_string());
        }

        if trimmed.starts_with('{') {
            let mut ds: DataStore = serde_json::from_str(trimmed)
                .map_err(|e| format!("Failed to parse DataStore JSON: {e}"))?;
            if ds.name.is_empty() && !name.is_empty() {
                ds.name = name.to_string();
            }
            ds.validate()?;
            return Ok(ds);
        }

        let mut ds = DataStore {
            name: name.to_string(),
            provider: DbmsProvider::SqlServer,
            server: String::new(),
            database: String::new(),
            port: 0,
            schema: None,
            user: String::new(),
            password: None,
            integrated_security: false,
            pooling: true,
            connection_timeout: 30,
            dialect: DialectSettings::default(),
        };

        for line in clean.lines() {
            let l = line.trim();
            if l.is_empty() || l.starts_with("datastore ") || l == "{" || l == "}" {
                continue;
            }

            if let Some((k, v)) = l.split_once('=') {
                let k = k.trim().to_ascii_lowercase();
                let v = v.trim().trim_matches(';').trim_matches('"');
                match k.as_str() {
                    "provider" => ds.provider = DbmsProvider::parse(v)?,
                    "server" | "host" => ds.server = v.to_string(),
                    "database" | "db" => ds.database = v.to_string(),
                    "port" => ds.port = v.parse().map_err(|_| format!("Invalid port '{v}'"))?,
                    "schema" => ds.schema = Some(v.to_string()),
                    "user" | "username" => ds.user = v.to_string(),
                    "password" => ds.password = Some(v.to_string()),
                    "integrated_security" => {
                        ds.integrated_security = v.eq_ignore_ascii_case("true")
                    }
                    "pooling" => ds.pooling = v.eq_ignore_ascii_case("true"),
                    "timeout" | "connection_timeout" => {
                        ds.connection_timeout =
                            v.parse().map_err(|_| format!("Invalid timeout '{v}'"))?
                    }
                    _ => {}
                }
            }
        }

        if ds.port == 0 {
            ds.port = ds.provider.default_port();
        }
        if ds.schema.is_none() {
            let def_schema = ds.provider.default_schema();
            if !def_schema.is_empty() {
                ds.schema = Some(def_schema.to_string());
            }
        }

        ds.validate()?;
        Ok(ds)
    }

    /// Validates DataStore connection settings fail-closed.
    pub fn validate(&self) -> Result<(), String> {
        if self.name.is_empty() {
            return Err("DataStore name cannot be empty".to_string());
        }
        if self.server.is_empty() {
            return Err("DataStore server host cannot be empty".to_string());
        }
        if self.database.is_empty() {
            return Err("DataStore database name cannot be empty".to_string());
        }
        if self.port == 0 {
            return Err("DataStore port must be greater than 0".to_string());
        }
        if !self.integrated_security && self.user.is_empty() {
            return Err(
                "DataStore username required when integrated_security is false".to_string(),
            );
        }
        Ok(())
    }

    /// Renders an ADO.NET connection string appropriate for the target provider.
    pub fn to_connection_string(&self) -> String {
        match self.provider {
            DbmsProvider::SqlServer => {
                let mut conn = format!(
                    "Server={},{};Database={};",
                    self.server, self.port, self.database
                );
                if self.integrated_security {
                    conn.push_str("Integrated Security=True;TrustServerCertificate=True;");
                } else {
                    conn.push_str(&format!(
                        "User Id={};Password={};",
                        self.user,
                        self.password.as_deref().unwrap_or("")
                    ));
                }
                conn.push_str(&format!(
                    "Connection Timeout={};Pooling={};",
                    self.connection_timeout, self.pooling
                ));
                conn
            }
            DbmsProvider::PostgreSql => {
                let mut conn = format!(
                    "Host={};Port={};Database={};",
                    self.server, self.port, self.database
                );
                conn.push_str(&format!(
                    "Username={};Password={};",
                    self.user,
                    self.password.as_deref().unwrap_or("")
                ));
                if let Some(schema) = &self.schema {
                    conn.push_str(&format!("Search Path={};", schema));
                }
                conn.push_str(&format!(
                    "Timeout={};Pooling={};",
                    self.connection_timeout, self.pooling
                ));
                conn
            }
            DbmsProvider::MySql => {
                let mut conn = format!(
                    "Server={};Port={};Database={};",
                    self.server, self.port, self.database
                );
                conn.push_str(&format!(
                    "Uid={};Pwd={};",
                    self.user,
                    self.password.as_deref().unwrap_or("")
                ));
                conn.push_str(&format!(
                    "Connection Timeout={};Pooling={};",
                    self.connection_timeout, self.pooling
                ));
                conn
            }
            DbmsProvider::Oracle => {
                let mut conn = format!(
                    "Data Source={}:{}/{};",
                    self.server, self.port, self.database
                );
                conn.push_str(&format!(
                    "User Id={};Password={};",
                    self.user,
                    self.password.as_deref().unwrap_or("")
                ));
                conn.push_str(&format!(
                    "Connection Timeout={};Pooling={};",
                    self.connection_timeout, self.pooling
                ));
                conn
            }
            DbmsProvider::Db2 => {
                let mut conn = format!(
                    "Server={}:{};Database={};",
                    self.server, self.port, self.database
                );
                conn.push_str(&format!(
                    "UID={};PWD={};",
                    self.user,
                    self.password.as_deref().unwrap_or("")
                ));
                conn.push_str(&format!(
                    "ConnectionTimeout={};Pooling={};",
                    self.connection_timeout, self.pooling
                ));
                conn
            }
            DbmsProvider::Informix => {
                let mut conn = format!(
                    "Host={};Service={};Database={};",
                    self.server, self.port, self.database
                );
                conn.push_str(&format!(
                    "User ID={};Password={};",
                    self.user,
                    self.password.as_deref().unwrap_or("")
                ));
                conn
            }
        }
    }
}
