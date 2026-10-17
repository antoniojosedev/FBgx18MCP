use gx_engine::model::DataType;
use gx_engine::reorg::{
    Column, ColumnDiff, ReorgImpactReport, TableAction, TableDiff, diff_schemas,
};
use gx_engine::table::{SqlDialect, Table, TableColumn, TableForeignKey, TableIndex};

fn make_col(name: &str, data_type: DataType, is_key: bool, is_nullable: bool) -> TableColumn {
    TableColumn::new(name, data_type)
        .key(is_key)
        .nullable(is_nullable)
}

#[test]
fn test_reorg_create_new_table_multi_dialect() {
    let mut customer = Table::new("Customer");
    let key_col: Column = make_col(
        "CustomerId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    );
    customer.add_column(key_col);
    customer.add_column(make_col(
        "CustomerName",
        DataType::Varchar { length: 100 },
        false,
        false,
    ));
    customer.add_column(make_col(
        "CustomerStatus",
        DataType::Character { length: 1 },
        false,
        false,
    ));

    let report = diff_schemas(&[], &[customer], SqlDialect::Ansi);

    assert_eq!(report.tables.len(), 1);
    let diff: &TableDiff = &report.tables[0];
    assert_eq!(diff.table_name, "Customer");
    assert_eq!(diff.action, TableAction::Create);
    assert_eq!(diff.column_diffs.len(), 3);
    assert!(report.risk_warnings.is_empty());
    assert!(!report.has_risks());

    // Verify ANSI
    let ansi = report.script_for_dialect(SqlDialect::Ansi);
    assert_eq!(ansi.len(), 1);
    assert!(ansi[0].contains("CREATE TABLE \"Customer\""));
    assert!(ansi[0].contains("\"CustomerId\" INTEGER NOT NULL"));
    assert!(ansi[0].contains("\"CustomerName\" VARCHAR(100) NOT NULL"));
    assert!(ansi[0].contains("\"CustomerStatus\" CHAR(1) NOT NULL"));
    assert!(ansi[0].contains("PRIMARY KEY (\"CustomerId\")"));

    // Verify SQL Server
    let sqlserver = report.script_for_dialect(SqlDialect::SqlServer);
    assert_eq!(sqlserver.len(), 1);
    assert!(sqlserver[0].contains("CREATE TABLE [Customer]"));
    assert!(sqlserver[0].contains("[CustomerId] INT NOT NULL"));
    assert!(sqlserver[0].contains("[CustomerName] VARCHAR(100) NOT NULL"));
    assert!(sqlserver[0].contains("[CustomerStatus] CHAR(1) NOT NULL"));
    assert!(sqlserver[0].contains("PRIMARY KEY ([CustomerId])"));

    // Verify PostgreSQL
    let postgres = report.script_for_dialect(SqlDialect::PostgreSql);
    assert_eq!(postgres.len(), 1);
    assert!(postgres[0].contains("CREATE TABLE \"Customer\""));
    assert!(postgres[0].contains("\"CustomerId\" INTEGER NOT NULL"));
    assert!(postgres[0].contains("\"CustomerName\" VARCHAR(100) NOT NULL"));
    assert!(postgres[0].contains("\"CustomerStatus\" CHAR(1) NOT NULL"));
    assert!(postgres[0].contains("PRIMARY KEY (\"CustomerId\")"));

    // Verify Oracle
    let oracle = report.script_for_dialect(SqlDialect::Oracle);
    assert_eq!(oracle.len(), 1);
    assert!(oracle[0].contains("CREATE TABLE Customer"));
    assert!(oracle[0].contains("CustomerId NUMBER(9) NOT NULL"));
    assert!(oracle[0].contains("CustomerName VARCHAR2(100) NOT NULL"));
    assert!(oracle[0].contains("CustomerStatus CHAR(1) NOT NULL"));
    assert!(oracle[0].contains("PRIMARY KEY (CustomerId)"));
}

#[test]
fn test_reorg_drop_table_with_data_loss_warning() {
    let mut legacy = Table::new("LegacyLogs");
    legacy.add_column(make_col(
        "LogId",
        DataType::Numeric {
            precision: 18,
            scale: 0,
        },
        true,
        false,
    ));
    legacy.add_column(make_col(
        "LogMessage",
        DataType::Varchar { length: 500 },
        false,
        true,
    ));

    let report = diff_schemas(&[legacy], &[], SqlDialect::Ansi);

    assert_eq!(report.tables.len(), 1);
    let diff = &report.tables[0];
    assert_eq!(diff.table_name, "LegacyLogs");
    assert_eq!(diff.action, TableAction::Drop);
    assert!(diff.column_diffs.is_empty());

    assert_eq!(report.risk_warnings.len(), 1);
    assert_eq!(
        report.risk_warnings[0],
        "Table 'LegacyLogs' will be dropped; potential data loss"
    );
    assert!(report.has_risks());

    assert_eq!(
        report.script_for_dialect(SqlDialect::Ansi),
        &["DROP TABLE \"LegacyLogs\";"]
    );
    assert_eq!(
        report.script_for_dialect(SqlDialect::SqlServer),
        &["DROP TABLE [LegacyLogs];"]
    );
    assert_eq!(
        report.script_for_dialect(SqlDialect::PostgreSql),
        &["DROP TABLE \"LegacyLogs\";"]
    );
    assert_eq!(
        report.script_for_dialect(SqlDialect::Oracle),
        &["DROP TABLE LegacyLogs;"]
    );
}

#[test]
fn test_reorg_add_column_multi_dialect() {
    let mut prev = Table::new("Customer");
    prev.add_column(make_col(
        "CustomerId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    prev.add_column(make_col(
        "CustomerName",
        DataType::Varchar { length: 100 },
        false,
        false,
    ));

    let mut curr = prev.clone();
    curr.add_column(make_col(
        "CustomerEmail",
        DataType::Varchar { length: 150 },
        false,
        true,
    ));

    let report = diff_schemas(&[prev], &[curr], SqlDialect::Ansi);

    assert_eq!(report.tables.len(), 1);
    let diff = &report.tables[0];
    assert_eq!(diff.action, TableAction::Alter);
    assert_eq!(diff.column_diffs.len(), 1);

    match &diff.column_diffs[0] {
        ColumnDiff::Add(col) => {
            assert_eq!(col.name, "CustomerEmail");
            assert!(col.is_nullable);
        }
        other => panic!("expected ColumnDiff::Add, got {:?}", other),
    }

    assert!(report.risk_warnings.is_empty());

    assert_eq!(
        report.script_for_dialect(SqlDialect::Ansi),
        &["ALTER TABLE \"Customer\" ADD \"CustomerEmail\" VARCHAR(150) NULL;"]
    );
    assert_eq!(
        report.script_for_dialect(SqlDialect::SqlServer),
        &["ALTER TABLE [Customer] ADD [CustomerEmail] VARCHAR(150) NULL;"]
    );
    assert_eq!(
        report.script_for_dialect(SqlDialect::PostgreSql),
        &["ALTER TABLE \"Customer\" ADD \"CustomerEmail\" VARCHAR(150) NULL;"]
    );
    assert_eq!(
        report.script_for_dialect(SqlDialect::Oracle),
        &["ALTER TABLE Customer ADD CustomerEmail VARCHAR2(150) NULL;"]
    );
}

#[test]
fn test_reorg_drop_column_with_warning() {
    let mut prev = Table::new("Customer");
    prev.add_column(make_col(
        "CustomerId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    prev.add_column(make_col(
        "CustomerName",
        DataType::Varchar { length: 100 },
        false,
        false,
    ));
    prev.add_column(make_col(
        "DeprecatedNotes",
        DataType::Varchar { length: 255 },
        false,
        true,
    ));

    let mut curr = Table::new("Customer");
    curr.add_column(make_col(
        "CustomerId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    curr.add_column(make_col(
        "CustomerName",
        DataType::Varchar { length: 100 },
        false,
        false,
    ));

    let report = diff_schemas(&[prev], &[curr], SqlDialect::Ansi);

    assert_eq!(report.tables.len(), 1);
    let diff = &report.tables[0];
    assert_eq!(diff.action, TableAction::Alter);
    assert_eq!(diff.column_diffs.len(), 1);

    match &diff.column_diffs[0] {
        ColumnDiff::Drop(name) => assert_eq!(name, "DeprecatedNotes"),
        other => panic!("expected ColumnDiff::Drop, got {:?}", other),
    }

    assert_eq!(report.risk_warnings.len(), 1);
    assert_eq!(
        report.risk_warnings[0],
        "Column 'DeprecatedNotes' in table 'Customer' will be dropped; potential data loss"
    );
    assert!(report.has_risks());

    assert_eq!(
        report.script_for_dialect(SqlDialect::Ansi),
        &["ALTER TABLE \"Customer\" DROP COLUMN \"DeprecatedNotes\";"]
    );
    assert_eq!(
        report.script_for_dialect(SqlDialect::SqlServer),
        &["ALTER TABLE [Customer] DROP COLUMN [DeprecatedNotes];"]
    );
    assert_eq!(
        report.script_for_dialect(SqlDialect::PostgreSql),
        &["ALTER TABLE \"Customer\" DROP COLUMN \"DeprecatedNotes\";"]
    );
    assert_eq!(
        report.script_for_dialect(SqlDialect::Oracle),
        &["ALTER TABLE Customer DROP COLUMN DeprecatedNotes;"]
    );
}

#[test]
fn test_reorg_alter_column_type_and_nullability() {
    let mut prev = Table::new("Product");
    prev.add_column(make_col(
        "ProductId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    prev.add_column(make_col(
        "ProductPrice",
        DataType::Numeric {
            precision: 8,
            scale: 2,
        },
        false,
        true,
    ));

    let mut curr = Table::new("Product");
    curr.add_column(make_col(
        "ProductId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    curr.add_column(make_col(
        "ProductPrice",
        DataType::Numeric {
            precision: 12,
            scale: 2,
        },
        false,
        false,
    ));

    let report = diff_schemas(&[prev], &[curr], SqlDialect::Ansi);

    assert_eq!(report.tables.len(), 1);
    let diff = &report.tables[0];
    assert_eq!(diff.action, TableAction::Alter);
    assert_eq!(diff.column_diffs.len(), 1);

    match &diff.column_diffs[0] {
        ColumnDiff::Alter { old, new } => {
            assert_eq!(old.name, "ProductPrice");
            assert_eq!(new.name, "ProductPrice");
            assert!(old.is_nullable);
            assert!(!new.is_nullable);
        }
        other => panic!("expected ColumnDiff::Alter, got {:?}", other),
    }

    assert!(report.risk_warnings.is_empty());

    // ANSI: ALTER COLUMN TYPE and SET NOT NULL
    let ansi = report.script_for_dialect(SqlDialect::Ansi);
    assert!(ansi.contains(
        &"ALTER TABLE \"Product\" ALTER COLUMN \"ProductPrice\" TYPE DECIMAL(12, 2);".to_string()
    ));
    assert!(ansi.contains(
        &"ALTER TABLE \"Product\" ALTER COLUMN \"ProductPrice\" SET NOT NULL;".to_string()
    ));

    // SQL Server: ALTER COLUMN <type> NOT NULL
    assert_eq!(
        report.script_for_dialect(SqlDialect::SqlServer),
        &["ALTER TABLE [Product] ALTER COLUMN [ProductPrice] DECIMAL(12, 2) NOT NULL;"]
    );

    // PostgreSQL: combined ALTER COLUMN TYPE ..., ALTER COLUMN ... SET NOT NULL
    assert_eq!(
        report.script_for_dialect(SqlDialect::PostgreSql),
        &[
            "ALTER TABLE \"Product\" ALTER COLUMN \"ProductPrice\" TYPE NUMERIC(12, 2), ALTER COLUMN \"ProductPrice\" SET NOT NULL;"
        ]
    );

    // Oracle: MODIFY (ProductPrice NUMBER(12, 2) NOT NULL)
    assert_eq!(
        report.script_for_dialect(SqlDialect::Oracle),
        &["ALTER TABLE Product MODIFY (ProductPrice NUMBER(12, 2) NOT NULL);"]
    );
}

#[test]
fn test_reorg_foreign_key_addition_and_removal() {
    let mut order_prev = Table::new("Order");
    order_prev.add_column(make_col(
        "OrderId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    order_prev.add_column(make_col(
        "CustomerId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        false,
        false,
    ));

    let mut order_curr = order_prev.clone();
    order_curr.add_foreign_key(TableForeignKey::new(
        "FK_Order_Customer",
        vec!["CustomerId".into()],
        "Customer",
        vec!["CustomerId".into()],
    ));

    // 1. Addition of Foreign Key
    let report_add = diff_schemas(
        &[order_prev.clone()],
        &[order_curr.clone()],
        SqlDialect::Ansi,
    );
    assert_eq!(report_add.tables.len(), 1);
    assert_eq!(report_add.tables[0].action, TableAction::Alter);
    assert_eq!(report_add.tables[0].added_foreign_keys.len(), 1);
    assert!(report_add.tables[0].dropped_foreign_keys.is_empty());

    assert_eq!(
        report_add.script_for_dialect(SqlDialect::Ansi),
        &[
            "ALTER TABLE \"Order\" ADD CONSTRAINT \"FK_Order_Customer\" FOREIGN KEY (\"CustomerId\") REFERENCES \"Customer\" (\"CustomerId\");"
        ]
    );
    assert_eq!(
        report_add.script_for_dialect(SqlDialect::SqlServer),
        &[
            "ALTER TABLE [Order] ADD CONSTRAINT [FK_Order_Customer] FOREIGN KEY ([CustomerId]) REFERENCES [Customer] ([CustomerId]);"
        ]
    );
    assert_eq!(
        report_add.script_for_dialect(SqlDialect::PostgreSql),
        &[
            "ALTER TABLE \"Order\" ADD CONSTRAINT \"FK_Order_Customer\" FOREIGN KEY (\"CustomerId\") REFERENCES \"Customer\" (\"CustomerId\");"
        ]
    );
    assert_eq!(
        report_add.script_for_dialect(SqlDialect::Oracle),
        &[
            "ALTER TABLE Order ADD CONSTRAINT FK_Order_Customer FOREIGN KEY (CustomerId) REFERENCES Customer (CustomerId);"
        ]
    );

    // 2. Removal of Foreign Key
    let report_drop = diff_schemas(&[order_curr], &[order_prev], SqlDialect::Ansi);
    assert_eq!(report_drop.tables.len(), 1);
    assert_eq!(report_drop.tables[0].action, TableAction::Alter);
    assert_eq!(report_drop.tables[0].dropped_foreign_keys.len(), 1);
    assert_eq!(
        report_drop.tables[0].dropped_foreign_keys[0],
        "FK_Order_Customer"
    );
    assert!(report_drop.tables[0].added_foreign_keys.is_empty());

    assert_eq!(
        report_drop.script_for_dialect(SqlDialect::Ansi),
        &["ALTER TABLE \"Order\" DROP CONSTRAINT \"FK_Order_Customer\";"]
    );
    assert_eq!(
        report_drop.script_for_dialect(SqlDialect::SqlServer),
        &["ALTER TABLE [Order] DROP CONSTRAINT [FK_Order_Customer];"]
    );
    assert_eq!(
        report_drop.script_for_dialect(SqlDialect::PostgreSql),
        &["ALTER TABLE \"Order\" DROP CONSTRAINT \"FK_Order_Customer\";"]
    );
    assert_eq!(
        report_drop.script_for_dialect(SqlDialect::Oracle),
        &["ALTER TABLE Order DROP CONSTRAINT FK_Order_Customer;"]
    );
}

#[test]
fn test_reorg_secondary_index_addition_and_removal() {
    let mut prev = Table::new("Customer");
    prev.add_column(make_col(
        "CustomerId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    prev.add_column(make_col(
        "CustomerName",
        DataType::Varchar { length: 100 },
        false,
        false,
    ));

    let mut curr = prev.clone();
    curr.add_index(TableIndex::new(
        "Idx_Customer_Name",
        vec!["CustomerName".into()],
        false,
    ));

    // 1. Addition of Secondary Index
    let report_add = diff_schemas(&[prev.clone()], &[curr.clone()], SqlDialect::Ansi);
    assert_eq!(report_add.tables.len(), 1);
    assert_eq!(report_add.tables[0].action, TableAction::Alter);
    assert_eq!(report_add.tables[0].added_indexes.len(), 1);
    assert!(report_add.tables[0].dropped_indexes.is_empty());

    assert_eq!(
        report_add.script_for_dialect(SqlDialect::Ansi),
        &["CREATE INDEX \"Idx_Customer_Name\" ON \"Customer\" (\"CustomerName\");"]
    );
    assert_eq!(
        report_add.script_for_dialect(SqlDialect::SqlServer),
        &["CREATE INDEX [Idx_Customer_Name] ON [Customer] ([CustomerName]);"]
    );
    assert_eq!(
        report_add.script_for_dialect(SqlDialect::PostgreSql),
        &["CREATE INDEX \"Idx_Customer_Name\" ON \"Customer\" (\"CustomerName\");"]
    );
    assert_eq!(
        report_add.script_for_dialect(SqlDialect::Oracle),
        &["CREATE INDEX Idx_Customer_Name ON Customer (CustomerName);"]
    );

    // 2. Removal of Secondary Index
    let report_drop = diff_schemas(&[curr], &[prev], SqlDialect::Ansi);
    assert_eq!(report_drop.tables.len(), 1);
    assert_eq!(report_drop.tables[0].action, TableAction::Alter);
    assert_eq!(report_drop.tables[0].dropped_indexes.len(), 1);
    assert_eq!(
        report_drop.tables[0].dropped_indexes[0],
        "Idx_Customer_Name"
    );
    assert!(report_drop.tables[0].added_indexes.is_empty());

    assert_eq!(
        report_drop.script_for_dialect(SqlDialect::Ansi),
        &["DROP INDEX \"Idx_Customer_Name\";"]
    );
    assert_eq!(
        report_drop.script_for_dialect(SqlDialect::SqlServer),
        &["DROP INDEX [Idx_Customer_Name] ON [Customer];"]
    );
    assert_eq!(
        report_drop.script_for_dialect(SqlDialect::PostgreSql),
        &["DROP INDEX \"Idx_Customer_Name\";"]
    );
    assert_eq!(
        report_drop.script_for_dialect(SqlDialect::Oracle),
        &["DROP INDEX Idx_Customer_Name;"]
    );
}

#[test]
fn test_reorg_multi_table_complex_drift() {
    // Previous schema: Customer (with TempField), Product, OldAudit
    let mut cust_prev = Table::new("Customer");
    cust_prev.add_column(make_col(
        "CustomerId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    cust_prev.add_column(make_col(
        "CustomerName",
        DataType::Varchar { length: 100 },
        false,
        false,
    ));
    cust_prev.add_column(make_col(
        "TempField",
        DataType::Varchar { length: 50 },
        false,
        true,
    ));

    let mut prod_prev = Table::new("Product");
    prod_prev.add_column(make_col(
        "ProductId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    prod_prev.add_column(make_col(
        "ProductName",
        DataType::Varchar { length: 80 },
        false,
        false,
    ));

    let mut audit_prev = Table::new("OldAudit");
    audit_prev.add_column(make_col(
        "AuditId",
        DataType::Numeric {
            precision: 18,
            scale: 0,
        },
        true,
        false,
    ));

    let previous_tables = vec![cust_prev, prod_prev.clone(), audit_prev];

    // Current schema: Customer (added CustomerEmail, dropped TempField), Product (unchanged), Invoice (new)
    let mut cust_curr = Table::new("Customer");
    cust_curr.add_column(make_col(
        "CustomerId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    cust_curr.add_column(make_col(
        "CustomerName",
        DataType::Varchar { length: 100 },
        false,
        false,
    ));
    cust_curr.add_column(make_col(
        "CustomerEmail",
        DataType::Varchar { length: 120 },
        false,
        true,
    ));

    let mut invoice_curr = Table::new("Invoice");
    invoice_curr.add_column(make_col(
        "InvoiceId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    invoice_curr.add_column(make_col(
        "InvoiceTotal",
        DataType::Numeric {
            precision: 12,
            scale: 2,
        },
        false,
        false,
    ));

    let current_tables = vec![cust_curr, prod_prev, invoice_curr];

    let report = diff_schemas(&previous_tables, &current_tables, SqlDialect::Ansi);

    assert_eq!(report.tables.len(), 4);

    let cust_diff = report
        .tables
        .iter()
        .find(|t| t.table_name == "Customer")
        .unwrap();
    assert_eq!(cust_diff.action, TableAction::Alter);
    assert_eq!(cust_diff.column_diffs.len(), 2);

    let prod_diff = report
        .tables
        .iter()
        .find(|t| t.table_name == "Product")
        .unwrap();
    assert_eq!(prod_diff.action, TableAction::Unchanged);
    assert!(prod_diff.column_diffs.is_empty());

    let inv_diff = report
        .tables
        .iter()
        .find(|t| t.table_name == "Invoice")
        .unwrap();
    assert_eq!(inv_diff.action, TableAction::Create);

    let audit_diff = report
        .tables
        .iter()
        .find(|t| t.table_name == "OldAudit")
        .unwrap();
    assert_eq!(audit_diff.action, TableAction::Drop);

    // Verify warnings
    assert_eq!(report.risk_warnings.len(), 2);
    assert!(
        report
            .risk_warnings
            .contains(&"Table 'OldAudit' will be dropped; potential data loss".to_string())
    );
    assert!(report.risk_warnings.contains(
        &"Column 'TempField' in table 'Customer' will be dropped; potential data loss".to_string()
    ));

    // Verify SQL generated
    let ansi = report.script_for_dialect(SqlDialect::Ansi);
    assert!(ansi.iter().any(|s| s.contains("DROP COLUMN \"TempField\"")));
    assert!(
        ansi.iter()
            .any(|s| s.contains("ADD \"CustomerEmail\" VARCHAR(120) NULL;"))
    );
    assert!(ansi.iter().any(|s| s.contains("CREATE TABLE \"Invoice\"")));
    assert!(ansi.iter().any(|s| s.contains("DROP TABLE \"OldAudit\";")));
    assert!(!ansi.iter().any(|s| s.contains("\"Product\"")));
}

#[test]
fn test_reorg_report_json_serialization() {
    let mut prev_table = Table::new("Customer");
    prev_table.add_column(make_col(
        "CustomerId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    prev_table.add_column(make_col(
        "OldField",
        DataType::Character { length: 10 },
        false,
        true,
    ));

    let mut curr_table = Table::new("Customer");
    curr_table.add_column(make_col(
        "CustomerId",
        DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        true,
        false,
    ));
    curr_table.add_column(make_col(
        "NewField",
        DataType::Varchar { length: 50 },
        false,
        true,
    ));

    let report = diff_schemas(&[prev_table], &[curr_table], SqlDialect::Ansi);

    let json = serde_json::to_string_pretty(&report).expect("serialization to JSON failed");
    assert!(json.contains("\"tables\""));
    assert!(json.contains("\"risk_warnings\""));
    assert!(json.contains("\"sql_scripts\""));
    assert!(json.contains("\"ansi\""));
    assert!(json.contains("\"sqlserver\""));
    assert!(json.contains("\"postgresql\""));
    assert!(json.contains("\"oracle\""));

    let deserialized: ReorgImpactReport =
        serde_json::from_str(&json).expect("deserialization from JSON failed");
    assert_eq!(report, deserialized);
}
