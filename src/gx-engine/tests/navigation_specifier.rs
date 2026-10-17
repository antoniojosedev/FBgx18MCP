use gx_engine::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::model::DataType;
use gx_engine::navigation::{
    compute_extended_tables, specify_catalog, specify_procedure, specify_statements,
};
use gx_engine::table::{SqlDialect, Table, TableColumn, TableForeignKey, TableIndex};

fn id(s: &str) -> ObjectId {
    ObjectId::new(s).unwrap()
}

fn sample_tables() -> Vec<Table> {
    vec![
        Table {
            name: "Customer".into(),
            description: Some("Customer master table".into()),
            primary_key: vec!["CustomerId".into()],
            columns: vec![
                TableColumn::new(
                    "CustomerId",
                    DataType::Numeric {
                        precision: 9,
                        scale: 0,
                    },
                )
                .key(true),
                TableColumn::new("CustomerName", DataType::Character { length: 50 }),
                TableColumn::new("CustomerActive", DataType::Boolean {}),
            ],
            foreign_keys: vec![],
            indexes: vec![
                TableIndex {
                    name: "PK_Customer".into(),
                    columns: vec!["CustomerId".into()],
                    is_primary: true,
                    is_unique: true,
                },
                TableIndex {
                    name: "IX_Customer_Name".into(),
                    columns: vec!["CustomerName".into()],
                    is_primary: false,
                    is_unique: false,
                },
            ],
        },
        Table {
            name: "Invoice".into(),
            description: Some("Invoice header table".into()),
            primary_key: vec!["InvoiceId".into()],
            columns: vec![
                TableColumn::new(
                    "InvoiceId",
                    DataType::Numeric {
                        precision: 9,
                        scale: 0,
                    },
                )
                .key(true),
                TableColumn::new(
                    "CustomerId",
                    DataType::Numeric {
                        precision: 9,
                        scale: 0,
                    },
                ),
                TableColumn::new("InvoiceDate", DataType::Date {}),
                TableColumn::new(
                    "InvoiceTotal",
                    DataType::Numeric {
                        precision: 12,
                        scale: 2,
                    },
                ),
            ],
            foreign_keys: vec![TableForeignKey {
                name: "FK_Invoice_Customer".into(),
                columns: vec!["CustomerId".into()],
                referenced_table: "Customer".into(),
                referenced_columns: vec!["CustomerId".into()],
            }],
            indexes: vec![
                TableIndex {
                    name: "PK_Invoice".into(),
                    columns: vec!["InvoiceId".into()],
                    is_primary: true,
                    is_unique: true,
                },
                TableIndex {
                    name: "IX_Invoice_Customer".into(),
                    columns: vec!["CustomerId".into()],
                    is_primary: false,
                    is_unique: false,
                },
            ],
        },
        Table {
            name: "Product".into(),
            description: Some("Product catalog table".into()),
            primary_key: vec!["ProductId".into()],
            columns: vec![
                TableColumn::new(
                    "ProductId",
                    DataType::Numeric {
                        precision: 9,
                        scale: 0,
                    },
                )
                .key(true),
                TableColumn::new("ProductName", DataType::Character { length: 50 }),
                TableColumn::new(
                    "ProductPrice",
                    DataType::Numeric {
                        precision: 10,
                        scale: 2,
                    },
                ),
            ],
            foreign_keys: vec![],
            indexes: vec![TableIndex {
                name: "PK_Product".into(),
                columns: vec!["ProductId".into()],
                is_primary: true,
                is_unique: true,
            }],
        },
    ]
}

#[test]
fn test_single_table_base_table_inference() {
    let tables = sample_tables();
    let extended = compute_extended_tables(&tables);

    let source = r#"
        For Each
            Where CustomerActive = true
            print(CustomerName)
        EndFor
    "#;

    let stmts = gx_engine::frontend::parse_body(source).expect("parse source");
    let mut levels = Vec::new();
    let mut context_tables = Vec::new();

    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut context_tables,
        &mut levels,
        SqlDialect::Ansi,
    )
    .expect("specify statement");

    assert_eq!(levels.len(), 1);
    let level = &levels[0];
    assert_eq!(level.base_table, "Customer");
    assert!(level.joins.is_empty());
    assert_eq!(level.filters.len(), 1);
    assert!(
        level.filters[0]
            .attribute
            .eq_ignore_ascii_case("CustomerActive")
    );
    assert_eq!(level.filters[0].op, "=");
    assert_eq!(level.filters[0].expression, "TRUE");
    assert_eq!(level.index, "PK_Customer");
    assert_eq!(level.order, vec!["CustomerId".to_string()]);

    // Verify ANSI SQL
    assert!(level
        .sql
        .starts_with("SELECT \"Customer\".\"CustomerId\", \"Customer\".\"CustomerName\", \"Customer\".\"CustomerActive\" FROM \"Customer\""));
    assert!(
        level
            .sql
            .contains("WHERE \"Customer\".\"CustomerActive\" = TRUE")
    );
    assert!(
        level
            .sql
            .contains("ORDER BY \"Customer\".\"CustomerId\" ASC")
    );

    // Verify SQL Server SQL
    let mut sqlserver_levels = Vec::new();
    let mut sqlserver_context = Vec::new();
    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut sqlserver_context,
        &mut sqlserver_levels,
        SqlDialect::SqlServer,
    )
    .expect("specify statement sqlserver");
    let sqlserver_sql = &sqlserver_levels[0].sql;
    assert!(sqlserver_sql.starts_with(
        "SELECT [Customer].[CustomerId], [Customer].[CustomerName], [Customer].[CustomerActive] FROM [Customer]"
    ));
    assert!(sqlserver_sql.contains("WHERE [Customer].[CustomerActive] = 1"));
}

#[test]
fn test_extended_table_join_resolution() {
    let tables = sample_tables();
    let extended = compute_extended_tables(&tables);

    let source = r#"
        For Each
            Where CustomerName = "ACME"
            print(InvoiceTotal)
        EndFor
    "#;

    let stmts = gx_engine::frontend::parse_body(source).expect("parse source");
    let mut levels = Vec::new();
    let mut context_tables = Vec::new();

    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut context_tables,
        &mut levels,
        SqlDialect::Ansi,
    )
    .expect("specify statement");

    assert_eq!(levels.len(), 1);
    let level = &levels[0];
    // Base table must be Invoice because Invoice can reach Customer via foreign key
    assert_eq!(level.base_table, "Invoice");
    assert_eq!(level.joins.len(), 1);
    let join = &level.joins[0];
    assert_eq!(join.table, "Customer");
    assert_eq!(
        join.on_condition,
        "\"Invoice\".\"CustomerId\" = \"Customer\".\"CustomerId\""
    );

    assert!(level
        .sql
        .contains("FROM \"Invoice\" INNER JOIN \"Customer\" ON \"Invoice\".\"CustomerId\" = \"Customer\".\"CustomerId\""));
    assert!(
        level
            .sql
            .contains("WHERE \"Customer\".\"CustomerName\" = 'ACME'")
    );
}

#[test]
fn test_defined_by_disambiguation() {
    let tables = sample_tables();
    let extended = compute_extended_tables(&tables);

    // Both Customer and Invoice contain CustomerId.
    // Specifying "Defined By CustomerName" forces the base table to Customer.
    let source = r#"
        For Each Defined By CustomerName
            Where CustomerId = 1
        EndFor
    "#;

    let stmts = gx_engine::frontend::parse_body(source).expect("parse source");
    let mut levels = Vec::new();
    let mut context_tables = Vec::new();

    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut context_tables,
        &mut levels,
        SqlDialect::Ansi,
    )
    .expect("specify statement");

    assert_eq!(levels.len(), 1);
    assert_eq!(levels[0].base_table, "Customer");
    assert!(levels[0].joins.is_empty());
}

#[test]
fn test_fail_closed_unrelated_attributes_spc0004() {
    let tables = sample_tables();
    let extended = compute_extended_tables(&tables);

    // Customer and Product have no relationship
    let source = r#"
        For Each
            Where CustomerName = "A"
            print(ProductPrice)
        EndFor
    "#;

    let stmts = gx_engine::frontend::parse_body(source).expect("parse source");
    let mut levels = Vec::new();
    let mut context_tables = Vec::new();

    let err = specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut context_tables,
        &mut levels,
        SqlDialect::Ansi,
    )
    .expect_err("must fail on unrelated attributes");

    assert!(err.contains("spc0004"), "error must contain spc0004: {err}");
}

#[test]
fn test_fail_closed_ambiguous_base_table_spc0005() {
    // Two isolated tables with the same attribute name but no relationship
    let tables = vec![
        Table {
            name: "TableA".into(),
            description: Some("Table A".into()),
            primary_key: vec!["SharedCode".into()],
            columns: vec![
                TableColumn::new(
                    "SharedCode",
                    DataType::Numeric {
                        precision: 5,
                        scale: 0,
                    },
                )
                .key(true),
            ],
            foreign_keys: vec![],
            indexes: vec![],
        },
        Table {
            name: "TableB".into(),
            description: Some("Table B".into()),
            primary_key: vec!["SharedCode".into()],
            columns: vec![
                TableColumn::new(
                    "SharedCode",
                    DataType::Numeric {
                        precision: 5,
                        scale: 0,
                    },
                )
                .key(true),
            ],
            foreign_keys: vec![],
            indexes: vec![],
        },
    ];
    let extended = compute_extended_tables(&tables);

    let source = r#"
        For Each
            Where SharedCode = 100
        EndFor
    "#;

    let stmts = gx_engine::frontend::parse_body(source).expect("parse source");
    let mut levels = Vec::new();
    let mut context_tables = Vec::new();

    let err = specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut context_tables,
        &mut levels,
        SqlDialect::Ansi,
    )
    .expect_err("must fail on ambiguous base table");

    assert!(err.contains("spc0005"), "error must contain spc0005: {err}");
}

#[test]
fn test_order_and_secondary_index_selection() {
    let tables = sample_tables();
    let extended = compute_extended_tables(&tables);

    let source = r#"
        For Each Order CustomerName DESC
            print(CustomerId)
        EndFor
    "#;

    let stmts = gx_engine::frontend::parse_body(source).expect("parse source");
    let mut levels = Vec::new();
    let mut context_tables = Vec::new();

    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut context_tables,
        &mut levels,
        SqlDialect::Ansi,
    )
    .expect("specify statement");

    assert_eq!(levels.len(), 1);
    let level = &levels[0];
    assert_eq!(level.index, "IX_Customer_Name");
    assert_eq!(level.order.len(), 1);
    assert!(level.order[0].eq_ignore_ascii_case("CustomerName"));

    assert!(
        level
            .sql
            .contains("ORDER BY \"Customer\".\"CustomerName\" DESC")
    );

    let mut sqlserver_levels = Vec::new();
    let mut sqlserver_context = Vec::new();
    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut sqlserver_context,
        &mut sqlserver_levels,
        SqlDialect::SqlServer,
    )
    .expect("specify statement sqlserver");
    assert!(
        sqlserver_levels[0]
            .sql
            .contains("ORDER BY [Customer].[CustomerName] DESC")
    );
}

#[test]
fn test_multi_dialect_sql_generation() {
    let tables = sample_tables();
    let extended = compute_extended_tables(&tables);

    let source = r#"
        For Each
            Where CustomerName = "TechCorp"
            print(InvoiceTotal)
        EndFor
    "#;

    let stmts = gx_engine::frontend::parse_body(source).expect("parse source");

    // Dialect: Ansi
    let mut ansi_levels = Vec::new();
    let mut ansi_ctx = Vec::new();
    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut ansi_ctx,
        &mut ansi_levels,
        SqlDialect::Ansi,
    )
    .unwrap();
    assert!(ansi_levels[0]
        .sql
        .contains("FROM \"Invoice\" INNER JOIN \"Customer\" ON \"Invoice\".\"CustomerId\" = \"Customer\".\"CustomerId\""));
    assert!(
        ansi_levels[0]
            .sql
            .contains("WHERE \"Customer\".\"CustomerName\" = 'TechCorp'")
    );

    // Dialect: SqlServer
    let mut sqlserver_levels = Vec::new();
    let mut sqlserver_ctx = Vec::new();
    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut sqlserver_ctx,
        &mut sqlserver_levels,
        SqlDialect::SqlServer,
    )
    .unwrap();
    assert!(sqlserver_levels[0].sql.contains(
        "FROM [Invoice] INNER JOIN [Customer] ON [Invoice].[CustomerId] = [Customer].[CustomerId]"
    ));
    assert!(
        sqlserver_levels[0]
            .sql
            .contains("WHERE [Customer].[CustomerName] = 'TechCorp'")
    );

    // Dialect: PostgreSql
    let mut postgres_levels = Vec::new();
    let mut postgres_ctx = Vec::new();
    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut postgres_ctx,
        &mut postgres_levels,
        SqlDialect::PostgreSql,
    )
    .unwrap();
    assert!(postgres_levels[0].sql.contains(
        "FROM \"Invoice\" INNER JOIN \"Customer\" ON \"Invoice\".\"CustomerId\" = \"Customer\".\"CustomerId\""
    ));
    assert!(
        postgres_levels[0]
            .sql
            .contains("WHERE \"Customer\".\"CustomerName\" = 'TechCorp'")
    );

    // Dialect: Oracle
    let mut oracle_levels = Vec::new();
    let mut oracle_ctx = Vec::new();
    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut oracle_ctx,
        &mut oracle_levels,
        SqlDialect::Oracle,
    )
    .unwrap();
    assert!(
        oracle_levels[0].sql.contains(
            "FROM Invoice INNER JOIN Customer ON Invoice.CustomerId = Customer.CustomerId"
        )
    );
    assert!(
        oracle_levels[0]
            .sql
            .contains("WHERE Customer.CustomerName = 'TechCorp'")
    );
}

#[test]
fn test_nested_for_each_automatic_correlation() {
    let tables = sample_tables();
    let extended = compute_extended_tables(&tables);

    let source = r#"
        For Each
            print(CustomerName)
            For Each
                print(InvoiceTotal)
            EndFor
        EndFor
    "#;

    let stmts = gx_engine::frontend::parse_body(source).expect("parse source");
    let mut levels = Vec::new();
    let mut context_tables = Vec::new();

    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut context_tables,
        &mut levels,
        SqlDialect::Ansi,
    )
    .expect("specify statement");

    assert_eq!(levels.len(), 2);

    let outer = &levels[0];
    assert_eq!(outer.base_table, "Customer");
    assert_eq!(outer.number, 1);
    assert!(outer.filters.is_empty());

    let inner = &levels[1];
    assert_eq!(inner.base_table, "Invoice");
    assert_eq!(inner.number, 2);
    // Automatic correlation filter on CustomerId from outer Customer table
    assert_eq!(inner.filters.len(), 1);
    assert_eq!(inner.filters[0].attribute, "CustomerId");
    assert_eq!(inner.filters[0].expression, "\"Customer\".\"CustomerId\"");

    assert!(
        inner
            .sql
            .contains("WHERE \"Invoice\".\"CustomerId\" = \"Customer\".\"CustomerId\"")
    );
}

#[test]
fn test_when_none_block_preservation() {
    let tables = sample_tables();
    let extended = compute_extended_tables(&tables);

    let source = r#"
        For Each
            Where CustomerId = 999
            print(CustomerName)
        When None
            print("Customer not found")
        EndFor
    "#;

    let stmts = gx_engine::frontend::parse_body(source).expect("parse source");
    let mut levels = Vec::new();
    let mut context_tables = Vec::new();

    specify_statements(
        &stmts,
        &tables,
        &extended,
        &mut context_tables,
        &mut levels,
        SqlDialect::Ansi,
    )
    .expect("specify statement");

    assert_eq!(levels.len(), 1);
    assert!(levels[0].has_when_none);
}

#[test]
fn test_end_to_end_specify_procedure_and_catalog() {
    let mut catalog = Catalog::empty();

    // Create Customer Transaction
    let mut customer_tx = KbObject::new(id("CustomerTx"), "Customer", ObjectKind::Transaction);
    customer_tx.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Customer",
                "attributes": [
                    {"name": "CustomerId", "is_key": true, "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
                    {"name": "CustomerName", "data_type": {"kind": "varchar", "length": 50}},
                    {"name": "CustomerActive", "data_type": {"kind": "boolean"}}
                ]
            }
        }"#
        .into(),
    });
    // Create Invoice Transaction
    let mut invoice_tx = KbObject::new(id("InvoiceTx"), "Invoice", ObjectKind::Transaction);
    invoice_tx.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Invoice",
                "attributes": [
                    {"name": "InvoiceId", "is_key": true, "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
                    {"name": "CustomerId", "data_type": {"kind": "numeric", "precision": 9, "scale": 0}, "is_foreign_key": true},
                    {"name": "InvoiceDate", "data_type": {"kind": "date"}},
                    {"name": "InvoiceTotal", "data_type": {"kind": "numeric", "precision": 12, "scale": 2}}
                ]
            }
        }"#
        .into(),
    });
    invoice_tx.references.push(Reference {
        target: id("CustomerTx"),
        kind: ReferenceKind::Uses,
    });

    // Create Procedure referencing Customer and Invoice
    let mut proc_obj = KbObject::new(
        id("ProcessInvoices"),
        "ProcessInvoices",
        ObjectKind::Procedure,
    );
    proc_obj.source_parts.push(SourcePart {
        name: "Source".into(),
        source: r#"
For Each
    Where CustomerName = "Important Corp"
    print(InvoiceTotal)
EndFor
"#
        .into(),
    });

    catalog
        .apply(
            0,
            &[
                Edit::Upsert(customer_tx),
                Edit::Upsert(invoice_tx),
                Edit::Upsert(proc_obj),
            ],
        )
        .unwrap();

    // Test specify_procedure
    let report = specify_procedure(&catalog, "ProcessInvoices", SqlDialect::Ansi)
        .expect("specify procedure");
    assert!(report.target_name.eq_ignore_ascii_case("ProcessInvoices"));
    assert!(report.warnings.is_empty());
    assert_eq!(report.levels.len(), 1);
    assert_eq!(report.levels[0].base_table, "Invoice");
    assert_eq!(report.levels[0].joins.len(), 1);
    assert_eq!(report.levels[0].joins[0].table, "Customer");

    // Test JSON serialization of NavigationReport
    let json = serde_json::to_string_pretty(&report).expect("serialize report to json");
    assert!(json.to_ascii_uppercase().contains("PROCESSINVOICES"));
    assert!(json.to_ascii_uppercase().contains("INVOICE"));
    assert!(json.to_ascii_uppercase().contains("CUSTOMER"));

    // Test specify_catalog
    let reports = specify_catalog(&catalog, SqlDialect::Ansi).expect("specify catalog");
    assert_eq!(reports.len(), 1);
    assert!(
        reports[0]
            .target_name
            .eq_ignore_ascii_case("ProcessInvoices")
    );
}
