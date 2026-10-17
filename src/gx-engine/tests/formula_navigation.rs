use gx_engine::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::frontend::parse_expression_with_attributes;
use gx_engine::model::DataType;
use gx_engine::navigation::{
    specify_aggregate_formula, specify_horizontal_formula, specify_procedure,
    specify_transaction_attribute_formula,
};
use gx_engine::table::{SqlDialect, Table, TableColumn, TableForeignKey, TableIndex};

fn id(s: &str) -> ObjectId {
    ObjectId::new(s).unwrap()
}

fn fixture_tables() -> Vec<Table> {
    vec![
        Table {
            name: "Customer".into(),
            description: Some("Customer master".into()),
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
            indexes: vec![TableIndex {
                name: "PK_Customer".into(),
                columns: vec!["CustomerId".into()],
                is_primary: true,
                is_unique: true,
            }],
        },
        Table {
            name: "Invoice".into(),
            description: Some("Invoice header".into()),
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
                TableColumn::new("InvoiceStatus", DataType::Character { length: 10 }),
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
            name: "InvoiceDetail".into(),
            description: Some("Invoice line item".into()),
            primary_key: vec!["InvoiceId".into(), "InvoiceDetailId".into()],
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
                    "InvoiceDetailId",
                    DataType::Numeric {
                        precision: 4,
                        scale: 0,
                    },
                )
                .key(true),
                TableColumn::new(
                    "ProductId",
                    DataType::Numeric {
                        precision: 9,
                        scale: 0,
                    },
                ),
                TableColumn::new(
                    "DetailQuantity",
                    DataType::Numeric {
                        precision: 6,
                        scale: 0,
                    },
                ),
                TableColumn::new(
                    "DetailPrice",
                    DataType::Numeric {
                        precision: 10,
                        scale: 2,
                    },
                ),
            ],
            foreign_keys: vec![
                TableForeignKey {
                    name: "FK_Detail_Invoice".into(),
                    columns: vec!["InvoiceId".into()],
                    referenced_table: "Invoice".into(),
                    referenced_columns: vec!["InvoiceId".into()],
                },
                TableForeignKey {
                    name: "FK_Detail_Product".into(),
                    columns: vec!["ProductId".into()],
                    referenced_table: "Product".into(),
                    referenced_columns: vec!["ProductId".into()],
                },
            ],
            indexes: vec![TableIndex {
                name: "PK_InvoiceDetail".into(),
                columns: vec!["InvoiceId".into(), "InvoiceDetailId".into()],
                is_primary: true,
                is_unique: true,
            }],
        },
        Table {
            name: "Product".into(),
            description: Some("Product master".into()),
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
        Table {
            name: "Warehouse".into(),
            description: Some("Unrelated warehouse table".into()),
            primary_key: vec!["WarehouseId".into()],
            columns: vec![
                TableColumn::new(
                    "WarehouseId",
                    DataType::Numeric {
                        precision: 4,
                        scale: 0,
                    },
                )
                .key(true),
                TableColumn::new("WarehouseLocation", DataType::Character { length: 50 }),
            ],
            foreign_keys: vec![],
            indexes: vec![TableIndex {
                name: "PK_Warehouse".into(),
                columns: vec!["WarehouseId".into()],
                is_primary: true,
                is_unique: true,
            }],
        },
    ]
}

#[test]
fn test_correlated_aggregate_sum_formula_in_for_each() {
    let tables = fixture_tables();
    let expr = parse_expression_with_attributes("Sum(InvoiceTotal)").expect("parse sum");
    let (name, args) = match &expr {
        gx_engine::frontend::Expr::Call(n, a) => (n.as_str(), a.as_slice()),
        _ => panic!("expected call"),
    };

    let formula = specify_aggregate_formula(
        name,
        args,
        Some("Customer"),
        &tables,
        SqlDialect::PostgreSql,
    )
    .expect("specify aggregate");

    assert_eq!(formula.formula_type, "Sum");
    assert_eq!(formula.base_table, "Invoice");
    assert!(
        formula
            .target_attribute
            .as_deref()
            .unwrap()
            .eq_ignore_ascii_case("InvoiceTotal")
    );
    assert_eq!(formula.index.as_deref(), Some("IX_Invoice_Customer"));
    assert_eq!(formula.correlations.len(), 1);
    assert_eq!(
        formula.correlations[0],
        "\"Invoice\".\"CustomerId\" = \"Customer\".\"CustomerId\""
    );
    assert!(
        formula
            .sql
            .contains("SELECT SUM(\"Invoice\".\"InvoiceTotal\") FROM \"Invoice\""),
        "sql: {}",
        formula.sql
    );
    assert!(
        formula
            .sql
            .contains("WHERE \"Invoice\".\"CustomerId\" = \"Customer\".\"CustomerId\""),
        "sql: {}",
        formula.sql
    );
}

#[test]
fn test_multi_aggregate_formulas_in_for_each() {
    let tables = fixture_tables();

    // Count(InvoiceId)
    let expr_count = parse_expression_with_attributes("Count(InvoiceId)").expect("parse count");
    let (n1, a1) = match &expr_count {
        gx_engine::frontend::Expr::Call(n, a) => (n.as_str(), a.as_slice()),
        _ => panic!("expected call"),
    };
    let f_count =
        specify_aggregate_formula(n1, a1, Some("Customer"), &tables, SqlDialect::SqlServer)
            .expect("specify count");
    assert_eq!(f_count.formula_type, "Count");
    assert_eq!(f_count.base_table, "Invoice");
    assert!(
        f_count
            .target_attribute
            .as_deref()
            .unwrap()
            .eq_ignore_ascii_case("InvoiceId")
    );
    assert!(
        f_count
            .sql
            .contains("SELECT COUNT([Invoice].[InvoiceId]) FROM [Invoice]"),
        "sql: {}",
        f_count.sql
    );
    assert!(
        f_count
            .sql
            .contains("WHERE [Invoice].[CustomerId] = [Customer].[CustomerId]"),
        "sql: {}",
        f_count.sql
    );

    // Max(InvoiceTotal)
    let expr_max = parse_expression_with_attributes("Max(InvoiceTotal)").expect("parse max");
    let (n2, a2) = match &expr_max {
        gx_engine::frontend::Expr::Call(n, a) => (n.as_str(), a.as_slice()),
        _ => panic!("expected call"),
    };
    let f_max = specify_aggregate_formula(n2, a2, Some("Customer"), &tables, SqlDialect::SqlServer)
        .expect("specify max");
    assert_eq!(f_max.formula_type, "Max");
    assert!(
        f_max
            .sql
            .contains("SELECT MAX([Invoice].[InvoiceTotal]) FROM [Invoice]"),
        "sql: {}",
        f_max.sql
    );

    // Min(InvoiceTotal)
    let expr_min = parse_expression_with_attributes("Min(InvoiceTotal)").expect("parse min");
    let (n3, a3) = match &expr_min {
        gx_engine::frontend::Expr::Call(n, a) => (n.as_str(), a.as_slice()),
        _ => panic!("expected call"),
    };
    let f_min = specify_aggregate_formula(n3, a3, Some("Customer"), &tables, SqlDialect::SqlServer)
        .expect("specify min");
    assert_eq!(f_min.formula_type, "Min");
    assert!(
        f_min
            .sql
            .contains("SELECT MIN([Invoice].[InvoiceTotal]) FROM [Invoice]"),
        "sql: {}",
        f_min.sql
    );
}

#[test]
fn test_standalone_aggregate_formula_outside_for_each() {
    let tables = fixture_tables();
    let expr = parse_expression_with_attributes("Sum(InvoiceTotal)").expect("parse sum");
    let (name, args) = match &expr {
        gx_engine::frontend::Expr::Call(n, a) => (n.as_str(), a.as_slice()),
        _ => panic!("expected call"),
    };

    let formula = specify_aggregate_formula(
        name,
        args,
        None, // No enclosing For Each context table
        &tables,
        SqlDialect::Ansi,
    )
    .expect("specify standalone aggregate");

    assert_eq!(formula.formula_type, "Sum");
    assert_eq!(formula.base_table, "Invoice");
    assert!(formula.correlations.is_empty());
    assert_eq!(
        formula.sql,
        "SELECT SUM(\"Invoice\".\"InvoiceTotal\") FROM \"Invoice\""
    );
}

#[test]
fn test_find_formula_multi_dialect_sql() {
    let tables = fixture_tables();
    let expr = parse_expression_with_attributes("Find(InvoiceTotal, InvoiceDate = &TargetDate, 0)")
        .expect("parse find");
    let (name, args) = match &expr {
        gx_engine::frontend::Expr::Call(n, a) => (n.as_str(), a.as_slice()),
        _ => panic!("expected call"),
    };

    // SQL Server: SELECT TOP 1 [InvoiceTotal] FROM [Invoice] WHERE ...
    let f_sqlserver = specify_aggregate_formula(name, args, None, &tables, SqlDialect::SqlServer)
        .expect("find sqlserver");
    assert_eq!(f_sqlserver.formula_type, "Find");
    assert_eq!(f_sqlserver.default_value.as_deref(), Some("0"));
    assert!(
        f_sqlserver
            .sql
            .starts_with("SELECT TOP 1 [Invoice].[InvoiceTotal] FROM [Invoice] WHERE"),
        "sql: {}",
        f_sqlserver.sql
    );
    assert!(
        f_sqlserver
            .sql
            .contains("[Invoice].[InvoiceDate] = @TARGETDATE")
            || f_sqlserver
                .sql
                .contains("[Invoice].[InvoiceDate] = @TargetDate")
    );

    // PostgreSQL: SELECT "InvoiceTotal" FROM ... LIMIT 1
    let f_pg = specify_aggregate_formula(name, args, None, &tables, SqlDialect::PostgreSql)
        .expect("find pg");
    assert!(
        f_pg.sql
            .starts_with("SELECT \"Invoice\".\"InvoiceTotal\" FROM \"Invoice\" WHERE")
    );
    assert!(f_pg.sql.ends_with("LIMIT 1"), "sql: {}", f_pg.sql);

    // Oracle: SELECT InvoiceTotal FROM Invoice WHERE ... AND ROWNUM = 1
    let f_oracle = specify_aggregate_formula(name, args, None, &tables, SqlDialect::Oracle)
        .expect("find oracle");
    assert!(
        f_oracle
            .sql
            .starts_with("SELECT Invoice.InvoiceTotal FROM Invoice WHERE")
    );
    assert!(f_oracle.sql.contains("ROWNUM = 1"), "sql: {}", f_oracle.sql);
}

#[test]
fn test_conditional_count_formula() {
    let tables = fixture_tables();
    let expr = parse_expression_with_attributes("Count(InvoiceId, InvoiceStatus = 'PAID')")
        .expect("parse count with condition");
    let (name, args) = match &expr {
        gx_engine::frontend::Expr::Call(n, a) => (n.as_str(), a.as_slice()),
        _ => panic!("expected call"),
    };

    let formula = specify_aggregate_formula(
        name,
        args,
        Some("Customer"),
        &tables,
        SqlDialect::PostgreSql,
    )
    .expect("specify count condition");

    assert_eq!(formula.formula_type, "Count");
    assert_eq!(formula.conditions.len(), 1);
    assert!(
        formula.conditions[0]
            .attribute
            .eq_ignore_ascii_case("InvoiceStatus")
    );
    assert_eq!(formula.conditions[0].expression, "'PAID'");
    assert!(
        formula
            .sql
            .contains("\"Invoice\".\"InvoiceStatus\" = 'PAID'"),
        "sql: {}",
        formula.sql
    );
    assert!(
        formula
            .sql
            .contains("\"Invoice\".\"CustomerId\" = \"Customer\".\"CustomerId\""),
        "sql: {}",
        formula.sql
    );
}

#[test]
fn test_average_formula_with_default() {
    let tables = fixture_tables();
    let expr = parse_expression_with_attributes("Average(InvoiceTotal, InvoiceTotal > 0, 0)")
        .expect("parse avg");
    let (name, args) = match &expr {
        gx_engine::frontend::Expr::Call(n, a) => (n.as_str(), a.as_slice()),
        _ => panic!("expected call"),
    };

    let formula = specify_aggregate_formula(name, args, None, &tables, SqlDialect::PostgreSql)
        .expect("specify avg");

    assert_eq!(formula.formula_type, "Average");
    assert_eq!(formula.default_value.as_deref(), Some("0"));
    assert!(
        formula.sql.contains("SELECT AVG(\"Invoice\".\"InvoiceTotal\") FROM \"Invoice\" WHERE \"Invoice\".\"InvoiceTotal\" > 0"),
        "sql: {}",
        formula.sql
    );
}

#[test]
fn test_formula_fails_closed_spc0004_unrelated_attributes() {
    let tables = fixture_tables();
    // Sum(ProductPrice, WarehouseLocation = 'Main') -> Product and Warehouse have no FK or relation!
    let expr = parse_expression_with_attributes("Sum(ProductPrice, WarehouseLocation = 'Main')")
        .expect("parse sum unrelated");
    let (name, args) = match &expr {
        gx_engine::frontend::Expr::Call(n, a) => (n.as_str(), a.as_slice()),
        _ => panic!("expected call"),
    };

    let result = specify_aggregate_formula(name, args, None, &tables, SqlDialect::PostgreSql);

    assert!(result.is_err());
    let err = result.unwrap_err();
    assert!(
        err.starts_with("spc0004:"),
        "expected spc0004 diagnostic, got: {err}"
    );
}

#[test]
fn test_formula_fails_closed_spc0005_ambiguous_base_table() {
    let tables = fixture_tables();
    // Count() without arguments and without context table
    let expr = parse_expression_with_attributes("Count()").expect("parse bare count");
    let (name, args) = match &expr {
        gx_engine::frontend::Expr::Call(n, a) => (n.as_str(), a.as_slice()),
        _ => panic!("expected call"),
    };

    let result = specify_aggregate_formula(
        name,
        args,
        None, // no context table
        &tables,
        SqlDialect::PostgreSql,
    );

    assert!(result.is_err());
    let err = result.unwrap_err();
    assert!(
        err.starts_with("spc0005:"),
        "expected spc0005 diagnostic, got: {err}"
    );
}

#[test]
fn test_horizontal_formula_evaluation_and_joins() {
    let tables = fixture_tables();
    // Horizontal formula: DetailQuantity * DetailPrice
    let expr =
        parse_expression_with_attributes("DetailQuantity * DetailPrice").expect("parse horizontal");

    let formula =
        specify_horizontal_formula(&expr, "InvoiceDetail", &tables, SqlDialect::PostgreSql)
            .expect("specify horizontal");

    assert_eq!(formula.formula_type, "Horizontal");
    assert_eq!(formula.base_table, "InvoiceDetail");
    assert_eq!(
        formula.sql,
        "(\"InvoiceDetail\".\"DetailQuantity\" * \"InvoiceDetail\".\"DetailPrice\")"
    );

    // Horizontal formula reaching extended table attribute: DetailQuantity * ProductPrice
    // InvoiceDetail -> Product via FK
    let expr_ext = parse_expression_with_attributes("DetailQuantity * ProductPrice")
        .expect("parse horizontal with join");

    let formula_ext =
        specify_horizontal_formula(&expr_ext, "InvoiceDetail", &tables, SqlDialect::PostgreSql)
            .expect("specify horizontal with join");

    assert_eq!(formula_ext.formula_type, "Horizontal");
    assert_eq!(formula_ext.base_table, "InvoiceDetail");
    assert_eq!(
        formula_ext.sql,
        "(\"InvoiceDetail\".\"DetailQuantity\" * \"Product\".\"ProductPrice\")"
    );

    // Fails closed spc0004 if attribute cannot be reached from base table
    let expr_unrelated = parse_expression_with_attributes("DetailQuantity * WarehouseLocation")
        .expect("parse horizontal unrelated");
    let err = specify_horizontal_formula(
        &expr_unrelated,
        "InvoiceDetail",
        &tables,
        SqlDialect::PostgreSql,
    )
    .unwrap_err();
    assert!(err.starts_with("spc0004:"), "expected spc0004, got: {err}");
}

#[test]
fn test_transaction_attribute_formula_specification() {
    let tables = fixture_tables();
    let tx_tables = vec![tables[1].clone()]; // Invoice table

    let formula = specify_transaction_attribute_formula(
        "InvoiceTotal",
        "Sum(DetailPrice * DetailQuantity)",
        &tx_tables,
        &tables,
        SqlDialect::PostgreSql,
    )
    .expect("specify tx formula");

    assert_eq!(formula.target_attribute.as_deref(), Some("InvoiceTotal"));
    assert_eq!(formula.base_table, "InvoiceDetail");
    assert!(
        formula.sql.contains("FROM \"InvoiceDetail\""),
        "sql: {}",
        formula.sql
    );
}

#[test]
fn test_full_procedure_navigation_report_with_formulas() {
    let mut catalog = Catalog::empty();

    let mut cust_tx = KbObject::new(id("CustomerTx"), "Customer", ObjectKind::Transaction);
    cust_tx.source_parts.push(SourcePart {
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

    let mut inv_tx = KbObject::new(id("InvoiceTx"), "Invoice", ObjectKind::Transaction);
    inv_tx.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Invoice",
                "attributes": [
                    {"name": "InvoiceId", "is_key": true, "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
                    {"name": "CustomerId", "data_type": {"kind": "numeric", "precision": 9, "scale": 0}, "is_foreign_key": true},
                    {"name": "InvoiceDate", "data_type": {"kind": "date"}},
                    {"name": "InvoiceTotal", "data_type": {"kind": "numeric", "precision": 12, "scale": 2}},
                    {"name": "InvoiceStatus", "data_type": {"kind": "varchar", "length": 10}}
                ]
            }
        }"#
        .into(),
    });
    inv_tx.references.push(Reference {
        target: id("CustomerTx"),
        kind: ReferenceKind::Uses,
    });

    let mut proc_obj = KbObject::new(
        id("CustomerInvoiceSummary"),
        "CustomerInvoiceSummary",
        ObjectKind::Procedure,
    );
    proc_obj.source_parts.push(SourcePart {
        name: "Source".into(),
        source: r#"
&GrandTotal = Sum(InvoiceTotal)
print(&GrandTotal)

For Each
    Where CustomerActive = true
    &CustTotal = Sum(InvoiceTotal)
    &CustInvoices = Count(InvoiceId)
    print(CustomerName)
    print(&CustTotal)
EndFor
"#
        .into(),
    });

    catalog
        .apply(
            0,
            &[
                Edit::Upsert(cust_tx),
                Edit::Upsert(inv_tx),
                Edit::Upsert(proc_obj),
            ],
        )
        .unwrap();

    let report = specify_procedure(&catalog, "CustomerInvoiceSummary", SqlDialect::PostgreSql)
        .expect("specify procedure");

    assert!(
        report
            .target_name
            .eq_ignore_ascii_case("CustomerInvoiceSummary")
    );
    assert_eq!(report.levels.len(), 1);
    let level = &report.levels[0];
    assert_eq!(level.base_table, "Customer");

    // The For Each level should have extracted both aggregate formulas
    assert_eq!(level.formulas.len(), 2);
    assert_eq!(level.formulas[0].formula_type, "Sum");
    assert_eq!(level.formulas[0].base_table, "Invoice");
    assert_eq!(level.formulas[1].formula_type, "Count");
    assert_eq!(level.formulas[1].base_table, "Invoice");

    // Outside the For Each, &GrandTotal = Sum(InvoiceTotal) is a standalone formula
    assert_eq!(report.standalone_formulas.len(), 1);
    assert_eq!(report.standalone_formulas[0].formula_type, "Sum");
    assert_eq!(report.standalone_formulas[0].base_table, "Invoice");
    assert!(report.standalone_formulas[0].correlations.is_empty());
}
