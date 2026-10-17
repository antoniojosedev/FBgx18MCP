use gx_engine::dataselector::{
    ComposedForEach, DataSelector, Parameter, compose_data_selector, parse_data_selector,
    substitute_parameters,
};
use gx_engine::frontend::{
    BinOp, DataSelectorRef, Expr, ForEachOrder, ForEachStmt, Stmt, parse_body,
    parse_expression_with_attributes,
};
use gx_engine::model::{DataType, ParamMode};
use gx_engine::navigation::{compute_extended_tables, infer_base_table, specify_for_each};
use gx_engine::table::{SqlDialect, Table, TableColumn, TableForeignKey, TableIndex};
use std::collections::BTreeMap;

fn sample_customer_tables() -> Vec<Table> {
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
                TableColumn::new("CustomerStatus", DataType::Character { length: 1 }),
                TableColumn::new("CustomerActive", DataType::Boolean {}),
                TableColumn::new(
                    "CustomerBalance",
                    DataType::Numeric {
                        precision: 12,
                        scale: 2,
                    },
                ),
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
            indexes: vec![TableIndex {
                name: "PK_Invoice".into(),
                columns: vec!["InvoiceId".into()],
                is_primary: true,
                is_unique: true,
            }],
        },
    ]
}

#[test]
fn test_parse_data_selector_rules_and_conditions() {
    let rules = "parm(in:&MinBalance, out:&Result, inout:&Status);\r\n";
    let conditions = r#"
        // Order by city ascending and name descending
        Order CustomerCity, CustomerName DESC;
        /* Active customer filter */
        Where CustomerStatus = "A";
        Where CustomerBalance >= &MinBalance;
    "#;

    let selector = parse_data_selector("ActiveCustomers", rules, conditions).unwrap();

    assert_eq!(selector.name, "ActiveCustomers");

    // Parameters
    assert_eq!(selector.parameters.len(), 3);
    assert_eq!(
        selector.parameters[0],
        Parameter {
            name: "MINBALANCE".into(),
            mode: ParamMode::In,
        }
    );
    assert_eq!(
        selector.parameters[1],
        Parameter {
            name: "RESULT".into(),
            mode: ParamMode::Out,
        }
    );
    assert_eq!(
        selector.parameters[2],
        Parameter {
            name: "STATUS".into(),
            mode: ParamMode::InOut,
        }
    );

    // Orders
    assert_eq!(selector.orders.len(), 2);
    assert_eq!(
        selector.orders[0],
        ForEachOrder {
            attribute: "CustomerCity".into(),
            descending: false,
        }
    );
    assert_eq!(
        selector.orders[1],
        ForEachOrder {
            attribute: "CustomerName".into(),
            descending: true,
        }
    );

    // Wheres
    assert_eq!(selector.wheres.len(), 2);
    assert_eq!(
        selector.wheres[0],
        parse_expression_with_attributes("CustomerStatus = \"A\"").unwrap()
    );
    assert_eq!(
        selector.wheres[1],
        parse_expression_with_attributes("CustomerBalance >= &MinBalance").unwrap()
    );
}

#[test]
fn test_substitute_parameters_in_expressions() {
    let mut param_map = BTreeMap::new();
    param_map.insert("MINBALANCE".into(), Expr::Integer(1000));
    param_map.insert("STATUS".into(), Expr::Text("ACTIVE".into()));
    param_map.insert("FLAG".into(), Expr::Boolean(true));
    param_map.insert("FACTOR".into(), Expr::Decimal("1.5".into()));

    // Binary expression substitution
    let binary_expr = Expr::Binary(
        Box::new(Expr::Attribute("CustomerBalance".into())),
        BinOp::GreaterEqual,
        Box::new(Expr::Variable("&MinBalance".into())),
    );
    let substituted_binary = substitute_parameters(&binary_expr, &param_map);
    assert_eq!(
        substituted_binary,
        Expr::Binary(
            Box::new(Expr::Attribute("CustomerBalance".into())),
            BinOp::GreaterEqual,
            Box::new(Expr::Integer(1000)),
        )
    );

    // Call expression substitution
    let call_expr = Expr::Call(
        "Calculate".into(),
        vec![
            Expr::Variable("MinBalance".into()),
            Expr::Variable("&Factor".into()),
            Expr::Variable("&Status".into()),
        ],
    );
    let substituted_call = substitute_parameters(&call_expr, &param_map);
    assert_eq!(
        substituted_call,
        Expr::Call(
            "Calculate".into(),
            vec![
                Expr::Integer(1000),
                Expr::Decimal("1.5".into()),
                Expr::Text("ACTIVE".into()),
            ]
        )
    );

    // Unary Not substitution
    let not_expr = Expr::Not(Box::new(Expr::Variable("&Flag".into())));
    let substituted_not = substitute_parameters(&not_expr, &param_map);
    assert_eq!(substituted_not, Expr::Not(Box::new(Expr::Boolean(true))));

    // Unary Negate substitution
    let neg_expr = Expr::Negate(Box::new(Expr::Variable("MinBalance".into())));
    let substituted_neg = substitute_parameters(&neg_expr, &param_map);
    assert_eq!(substituted_neg, Expr::Negate(Box::new(Expr::Integer(1000))));
}

#[test]
fn test_compose_data_selector_with_for_each() {
    let for_each = ForEachStmt {
        explicit_attributes: vec!["Customer".into()],
        using: None,
        wheres: vec![Expr::Binary(
            Box::new(Expr::Attribute("CustomerStatus".into())),
            BinOp::Equal,
            Box::new(Expr::Text("A".into())),
        )],
        orders: vec![ForEachOrder {
            attribute: "CustomerId".into(),
            descending: false,
        }],
        defined_by: None,
        body: vec![],
        when_none: vec![],
    };

    let selector = DataSelector {
        name: "ActiveByBalance".into(),
        parameters: vec![Parameter {
            name: "MINBAL".into(),
            mode: ParamMode::In,
        }],
        orders: vec![ForEachOrder {
            attribute: "CustomerName".into(),
            descending: true,
        }],
        wheres: vec![Expr::Binary(
            Box::new(Expr::Attribute("CustomerBalance".into())),
            BinOp::GreaterEqual,
            Box::new(Expr::Variable("MINBAL".into())),
        )],
    };

    let args = vec![Expr::Integer(500)];
    let composed = compose_data_selector(&for_each, &selector, &args).unwrap();

    assert_eq!(composed.wheres.len(), 2);
    assert_eq!(
        composed.wheres[0],
        Expr::Binary(
            Box::new(Expr::Attribute("CustomerStatus".into())),
            BinOp::Equal,
            Box::new(Expr::Text("A".into())),
        )
    );
    assert_eq!(
        composed.wheres[1],
        Expr::Binary(
            Box::new(Expr::Attribute("CustomerBalance".into())),
            BinOp::GreaterEqual,
            Box::new(Expr::Integer(500)),
        )
    );

    assert_eq!(composed.orders.len(), 2);
    assert_eq!(
        composed.orders[0],
        ForEachOrder {
            attribute: "CustomerId".into(),
            descending: false,
        }
    );
    assert_eq!(
        composed.orders[1],
        ForEachOrder {
            attribute: "CustomerName".into(),
            descending: true,
        }
    );
}

#[test]
fn test_compose_data_selector_arity_mismatch_fails_closed() {
    let for_each = ForEachStmt {
        explicit_attributes: vec![],
        using: None,
        wheres: vec![],
        orders: vec![],
        defined_by: None,
        body: vec![],
        when_none: vec![],
    };

    let selector = DataSelector {
        name: "TwoParamSelector".into(),
        parameters: vec![
            Parameter {
                name: "A".into(),
                mode: ParamMode::In,
            },
            Parameter {
                name: "B".into(),
                mode: ParamMode::In,
            },
        ],
        orders: vec![],
        wheres: vec![],
    };

    // Under-arity: 1 argument provided, 2 expected
    let err_under = compose_data_selector(&for_each, &selector, &[Expr::Integer(1)]).unwrap_err();
    assert!(
        err_under.contains("expects 2 arguments, but got 1"),
        "expected under-arity error, got: {err_under}"
    );

    // Over-arity: 3 arguments provided, 2 expected
    let err_over = compose_data_selector(
        &for_each,
        &selector,
        &[Expr::Integer(1), Expr::Integer(2), Expr::Integer(3)],
    )
    .unwrap_err();
    assert!(
        err_over.contains("expects 2 arguments, but got 3"),
        "expected over-arity error, got: {err_over}"
    );
}

#[test]
fn test_for_each_using_syntax_parsing() {
    let source = r#"
        For each using ActiveCustomers(&MinBalance)
            &Total = &Total + 1
        EndFor
    "#;

    let stmts = parse_body(source).unwrap();
    assert_eq!(stmts.len(), 1);

    match &stmts[0] {
        Stmt::ForEach(fe) => {
            assert_eq!(
                fe.using,
                Some(DataSelectorRef {
                    name: "ACTIVECUSTOMERS".into(),
                    arguments: vec![Expr::Variable("MINBALANCE".into())],
                })
            );
        }
        other => panic!("expected ForEach statement, got: {other:?}"),
    }
}

#[test]
fn test_for_each_with_attributes_and_using_clause() {
    let source = r#"
        For each Customer using ActiveCustomers(&MinBalance) where CustomerStatus = "A" order CustomerName
            &Total = &Total + 1
        EndFor
    "#;

    let stmts = parse_body(source).unwrap();
    assert_eq!(stmts.len(), 1);

    match &stmts[0] {
        Stmt::ForEach(fe) => {
            assert_eq!(fe.explicit_attributes, vec!["CUSTOMER"]);
            assert_eq!(
                fe.using,
                Some(DataSelectorRef {
                    name: "ACTIVECUSTOMERS".into(),
                    arguments: vec![Expr::Variable("MINBALANCE".into())],
                })
            );

            assert_eq!(fe.wheres.len(), 1);
            assert_eq!(
                fe.wheres[0],
                Expr::Binary(
                    Box::new(Expr::Attribute("CUSTOMERSTATUS".into())),
                    BinOp::Equal,
                    Box::new(Expr::Text("A".into())),
                )
            );

            assert_eq!(fe.orders.len(), 1);
            assert_eq!(
                fe.orders[0],
                ForEachOrder {
                    attribute: "CUSTOMERNAME".into(),
                    descending: false,
                }
            );
        }
        other => panic!("expected ForEach statement, got: {other:?}"),
    }
}

#[test]
fn test_composed_for_each_base_table_inference() {
    let tables = sample_customer_tables();
    let extended_tables = compute_extended_tables(&tables);

    // Procedure source with For Each using ActiveCustomers
    let source = r#"
        For each CustomerId where CustomerStatus = "A"
            &Total = &Total + 1
        EndFor
    "#;
    let stmts = parse_body(source).unwrap();
    let fe = match &stmts[0] {
        Stmt::ForEach(fe) => fe.as_ref().clone(),
        _ => unreachable!(),
    };

    // DataSelector with filter on CustomerBalance and order on CustomerName
    let selector = parse_data_selector(
        "ActiveCustomers",
        "parm(in:&MinBalance);",
        "Order CustomerName;\nWhere CustomerBalance >= &MinBalance;",
    )
    .unwrap();

    let composed = compose_data_selector(&fe, &selector, &[Expr::Integer(1000)]).unwrap();

    // Base table inference must select the Customer table
    let base_table = infer_base_table(&composed, &tables, &extended_tables).unwrap();
    assert_eq!(base_table.name, "Customer");
}

#[test]
fn test_full_composed_navigation_sql_query_plan() {
    let tables = sample_customer_tables();
    let extended_tables = compute_extended_tables(&tables);

    let for_each = ForEachStmt {
        explicit_attributes: vec!["CustomerId".into()],
        using: None,
        wheres: vec![Expr::Binary(
            Box::new(Expr::Attribute("CustomerActive".into())),
            BinOp::Equal,
            Box::new(Expr::Boolean(true)),
        )],
        orders: vec![ForEachOrder {
            attribute: "CustomerId".into(),
            descending: false,
        }],
        defined_by: None,
        body: vec![],
        when_none: vec![],
    };

    let selector = parse_data_selector(
        "MinIdSelector",
        "parm(in:&MinId);",
        "Where CustomerId >= &MinId;",
    )
    .unwrap();

    // Compose with substituted parameter 1000
    let composed = compose_data_selector(&for_each, &selector, &[Expr::Integer(1000)]).unwrap();

    // 1. ANSI Dialect
    let nav_ansi = specify_for_each(
        &composed,
        1,
        &tables,
        &extended_tables,
        &[],
        SqlDialect::Ansi,
    )
    .unwrap();
    assert_eq!(nav_ansi.base_table, "Customer");
    assert_eq!(nav_ansi.filters.len(), 2);
    assert!(
        nav_ansi
            .sql
            .contains("\"Customer\".\"CustomerActive\" = TRUE"),
        "ANSI SQL must include CustomerActive filter: {}",
        nav_ansi.sql
    );
    assert!(
        nav_ansi.sql.contains("\"Customer\".\"CustomerId\" >= 1000"),
        "ANSI SQL must include substituted CustomerId filter: {}",
        nav_ansi.sql
    );
    assert!(
        nav_ansi.sql.contains(
            "WHERE \"Customer\".\"CustomerActive\" = TRUE AND \"Customer\".\"CustomerId\" >= 1000"
        ),
        "ANSI WHERE clause check: {}",
        nav_ansi.sql
    );

    // 2. SQL Server Dialect
    let nav_sqlserver = specify_for_each(
        &composed,
        1,
        &tables,
        &extended_tables,
        &[],
        SqlDialect::SqlServer,
    )
    .unwrap();
    assert_eq!(nav_sqlserver.base_table, "Customer");
    assert_eq!(nav_sqlserver.filters.len(), 2);
    assert!(
        nav_sqlserver
            .sql
            .contains("[Customer].[CustomerActive] = 1"),
        "SQL Server must format boolean as 1: {}",
        nav_sqlserver.sql
    );
    assert!(
        nav_sqlserver
            .sql
            .contains("[Customer].[CustomerId] >= 1000"),
        "SQL Server must include substituted CustomerId filter: {}",
        nav_sqlserver.sql
    );
    assert!(
        nav_sqlserver.sql.contains("FROM [Customer]"),
        "SQL Server must quote table name: {}",
        nav_sqlserver.sql
    );

    // 3. PostgreSQL Dialect
    let nav_postgres = specify_for_each(
        &composed,
        1,
        &tables,
        &extended_tables,
        &[],
        SqlDialect::PostgreSql,
    )
    .unwrap();
    assert_eq!(nav_postgres.base_table, "Customer");
    assert_eq!(nav_postgres.filters.len(), 2);
    assert!(
        nav_postgres
            .sql
            .contains("\"Customer\".\"CustomerActive\" = TRUE"),
        "PostgreSQL must quote identifiers and format boolean as TRUE: {}",
        nav_postgres.sql
    );
    assert!(
        nav_postgres
            .sql
            .contains("\"Customer\".\"CustomerId\" >= 1000"),
        "PostgreSQL must include substituted CustomerId filter: {}",
        nav_postgres.sql
    );
    assert!(
        nav_postgres.sql.contains("FROM \"Customer\""),
        "PostgreSQL must quote table name: {}",
        nav_postgres.sql
    );

    // 4. Oracle Dialect
    let nav_oracle = specify_for_each(
        &composed,
        1,
        &tables,
        &extended_tables,
        &[],
        SqlDialect::Oracle,
    )
    .unwrap();
    assert_eq!(nav_oracle.base_table, "Customer");
    assert_eq!(nav_oracle.filters.len(), 2);
    assert!(
        nav_oracle.sql.contains("Customer.CustomerActive = 1"),
        "Oracle must format boolean as 1: {}",
        nav_oracle.sql
    );
    assert!(
        nav_oracle.sql.contains("Customer.CustomerId >= 1000"),
        "Oracle must include substituted CustomerId filter: {}",
        nav_oracle.sql
    );
    assert!(
        nav_oracle.sql.contains("FROM Customer"),
        "Oracle unquoted table name: {}",
        nav_oracle.sql
    );
}

#[test]
fn test_composed_for_each_wrapper_helpers() {
    let for_each = ForEachStmt {
        explicit_attributes: vec!["Customer".into()],
        using: None,
        wheres: vec![],
        orders: vec![],
        defined_by: None,
        body: vec![],
        when_none: vec![],
    };

    let selector = DataSelector {
        name: "NoParamSelector".into(),
        parameters: vec![],
        orders: vec![ForEachOrder {
            attribute: "CustomerId".into(),
            descending: false,
        }],
        wheres: vec![Expr::Binary(
            Box::new(Expr::Attribute("CustomerId".into())),
            BinOp::Greater,
            Box::new(Expr::Integer(0)),
        )],
    };

    let composed_wrapper = ComposedForEach::compose(&for_each, &selector, &[]).unwrap();
    assert_eq!(composed_wrapper.orders.len(), 1);
    assert_eq!(composed_wrapper.wheres.len(), 1);
    assert_eq!(composed_wrapper.stmt().orders.len(), 1);
    assert_eq!(composed_wrapper.for_each.orders.len(), 1);

    let inner_stmt = composed_wrapper.into_inner();
    assert_eq!(inner_stmt.orders.len(), 1);
}
