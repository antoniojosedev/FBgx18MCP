use gx_engine::datastore::{DataStore, DbmsProvider};

#[test]
fn test_datastore_sqlserver_dsl_and_connection_string() {
    let dsl = r#"
        datastore Default {
            provider = "SqlServer";
            server = "sql.internal.net";
            database = "ProductionKB";
            port = 1433;
            user = "app_user";
            password = "sqlpw";
            integrated_security = false;
            pooling = true;
            timeout = 15;
        }
    "#;

    let ds = DataStore::parse("Default", dsl).expect("DataStore DSL should parse");
    assert_eq!(ds.name, "Default");
    assert_eq!(ds.provider, DbmsProvider::SqlServer);
    assert_eq!(ds.server, "sql.internal.net");
    assert_eq!(ds.database, "ProductionKB");
    assert_eq!(ds.port, 1433);
    assert_eq!(ds.user, "app_user");
    assert_eq!(ds.connection_timeout, 15);

    let conn_str = ds.to_connection_string();
    assert!(conn_str.contains("Server=sql.internal.net,1433;"));
    assert!(conn_str.contains("Database=ProductionKB;"));
    assert!(conn_str.contains("User Id=app_user;"));
    assert!(conn_str.contains("Password=sqlpw;"));
    assert!(conn_str.contains("Pooling=true;"));
}

#[test]
fn test_datastore_postgresql_with_search_path() {
    let dsl = r#"
        datastore GAM {
            provider = "PostgreSql";
            server = "pg.internal.net";
            database = "gam_db";
            user = "gam_user";
            password = "gampw";
            schema = "custom_schema";
        }
    "#;

    let ds = DataStore::parse("GAM", dsl).expect("PostgreSQL DataStore should parse");
    assert_eq!(ds.provider, DbmsProvider::PostgreSql);
    assert_eq!(ds.port, 5432); // Default port
    assert_eq!(ds.schema.as_deref(), Some("custom_schema"));

    let conn_str = ds.to_connection_string();
    assert!(conn_str.contains("Host=pg.internal.net;Port=5432;"));
    assert!(conn_str.contains("Search Path=custom_schema;"));
}

#[test]
fn test_datastore_mysql_connection_string() {
    let dsl = r#"
        datastore Secondary {
            provider = "MySql";
            server = "mysql.internal.net";
            database = "sales";
            user = "root";
            password = "pwd";
        }
    "#;

    let ds = DataStore::parse("Secondary", dsl).expect("MySQL DataStore should parse");
    assert_eq!(ds.provider, DbmsProvider::MySql);
    assert_eq!(ds.port, 3306);
    let conn_str = ds.to_connection_string();
    assert!(conn_str.contains("Server=mysql.internal.net;Port=3306;Database=sales;"));
    assert!(conn_str.contains("Uid=root;Pwd=pwd;"));
}

#[test]
fn test_datastore_oracle_connection_string() {
    let dsl = r#"
        datastore OracleStore {
            provider = "Oracle";
            server = "oracle.internal.net";
            database = "ORCL";
            user = "scott";
            password = "tiger";
        }
    "#;

    let ds = DataStore::parse("OracleStore", dsl).expect("Oracle DataStore should parse");
    assert_eq!(ds.provider, DbmsProvider::Oracle);
    assert_eq!(ds.port, 1521);
    let conn_str = ds.to_connection_string();
    assert!(conn_str.contains("Data Source=oracle.internal.net:1521/ORCL;"));
}

#[test]
fn test_datastore_json_roundtrip() {
    let json = r#"{
        "name": "CloudStore",
        "provider": "sql_server",
        "server": "cloud.database.windows.net",
        "database": "CloudDb",
        "port": 1433,
        "user": "cloudadmin",
        "integrated_security": false,
        "pooling": true,
        "connection_timeout": 60,
        "dialect": {
            "quote_identifiers": true,
            "date_format": "YMD",
            "identity_clause": "IDENTITY"
        }
    }"#;

    let ds = DataStore::parse("CloudStore", json).expect("JSON should parse");
    assert_eq!(ds.name, "CloudStore");
    assert_eq!(ds.connection_timeout, 60);

    let serialized = serde_json::to_string(&ds).expect("Serialization failed");
    let roundtrip: DataStore = serde_json::from_str(&serialized).expect("Deserialization failed");
    assert_eq!(ds, roundtrip);
}

#[test]
fn test_datastore_fail_closed_on_empty_server() {
    let dsl = r#"
        datastore Bad {
            provider = "SqlServer";
            server = "";
            database = "TestDB";
            user = "u";
        }
    "#;
    let err = DataStore::parse("Bad", dsl).unwrap_err();
    assert!(err.contains("server host cannot be empty"), "Actual: {err}");
}

#[test]
fn test_datastore_fail_closed_on_empty_database() {
    let dsl = r#"
        datastore Bad {
            provider = "SqlServer";
            server = "localhost";
            database = "";
            user = "u";
        }
    "#;
    let err = DataStore::parse("Bad", dsl).unwrap_err();
    assert!(
        err.contains("database name cannot be empty"),
        "Actual: {err}"
    );
}

#[test]
fn test_datastore_fail_closed_on_missing_user_without_integrated_security() {
    let dsl = r#"
        datastore Bad {
            provider = "SqlServer";
            server = "localhost";
            database = "TestDB";
            integrated_security = false;
        }
    "#;
    let err = DataStore::parse("Bad", dsl).unwrap_err();
    assert!(err.contains("username required"), "Actual: {err}");
}

#[test]
fn test_datastore_integrated_security_allows_empty_user() {
    let dsl = r#"
        datastore Good {
            provider = "SqlServer";
            server = "localhost";
            database = "TestDB";
            integrated_security = true;
        }
    "#;
    let ds = DataStore::parse("Good", dsl).expect("Should succeed with integrated security");
    assert!(ds.integrated_security);
    assert!(
        ds.to_connection_string()
            .contains("Integrated Security=True;")
    );
}

#[test]
fn test_datastore_fail_closed_on_unsupported_provider() {
    let dsl = r#"
        datastore Bad {
            provider = "Cassandra";
            server = "localhost";
            database = "TestDB";
        }
    "#;
    let err = DataStore::parse("Bad", dsl).unwrap_err();
    assert!(err.contains("Unsupported DBMS provider"), "Actual: {err}");
}
