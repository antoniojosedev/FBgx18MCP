//! Integration test suite for Relational DML Query Planning Engine in `gx-engine`.
//! Validates Plan 129 Matrix Rows for database mutations:
//! - Attribute assignment inside For Each (UPDATE with spc0034 PK protection)
//! - Delete inside For Each (DELETE with WHERE PK)
//! - New ... EndNew (INSERT with spc0023 PK completeness & WHEN DUPLICATE multi-dialect conflict handling)

use gx_engine::dml::{
    NavigationDmlOp, NavigationDmlPlan, plan_delete_for_for_each, plan_insert_for_new,
    plan_updates_for_for_each,
};
use gx_engine::frontend::{BinOp, Expr, ForEachStmt, NewStmt, Stmt};
use gx_engine::model::DataType;
use gx_engine::table::{SqlDialect, Table, TableColumn, TableIndex};

/// Creates standard relational table fixtures for testing DML planning.
fn create_test_tables() -> Vec<Table> {
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
                TableColumn::new("CustomerStatus", DataType::Character { length: 20 }),
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
                TableColumn::new("InvoiceStatus", DataType::Character { length: 20 }),
            ],
            foreign_keys: vec![],
            indexes: vec![TableIndex {
                name: "PK_Invoice".into(),
                columns: vec!["InvoiceId".into()],
                is_primary: true,
                is_unique: true,
            }],
        },
        Table {
            name: "InvoiceLine".into(),
            description: Some("Invoice lines table with composite primary key".into()),
            primary_key: vec!["InvoiceId".into(), "LineId".into()],
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
                    "LineId",
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
                    "Quantity",
                    DataType::Numeric {
                        precision: 6,
                        scale: 0,
                    },
                ),
                TableColumn::new(
                    "Price",
                    DataType::Numeric {
                        precision: 10,
                        scale: 2,
                    },
                ),
                TableColumn::new(
                    "LineTotal",
                    DataType::Numeric {
                        precision: 12,
                        scale: 2,
                    },
                ),
            ],
            foreign_keys: vec![],
            indexes: vec![TableIndex {
                name: "PK_InvoiceLine".into(),
                columns: vec!["InvoiceId".into(), "LineId".into()],
                is_primary: true,
                is_unique: true,
            }],
        },
    ]
}

#[test]
fn test_plan_update_inside_for_each_multi_dialect_sql() {
    let tables = create_test_tables();
    let customer = &tables[0];

    let body = vec![
        Stmt::AssignAttribute("CustomerStatus".into(), Expr::Text("Active".into())),
        Stmt::AssignAttribute("CustomerActive".into(), Expr::Boolean(true)),
    ];

    let plan_opt = plan_updates_for_for_each(customer, &body, SqlDialect::Ansi)
        .expect("planning should succeed");
    assert!(plan_opt.is_some());
    let plan = plan_opt.unwrap();

    assert_eq!(plan.operation, NavigationDmlOp::Update);
    assert_eq!(plan.target_table, "Customer");
    assert_eq!(plan.primary_key, vec!["CustomerId"]);
    assert_eq!(plan.where_predicates, vec!["CustomerId = :CustomerId"]);
    assert!(!plan.has_when_duplicate);
    assert_eq!(plan.assignments.len(), 2);

    assert_eq!(plan.assignments[0].attribute, "CustomerStatus");
    assert_eq!(plan.assignments[0].column, "CustomerStatus");
    assert_eq!(plan.assignments[0].expr_sql, "'Active'");

    assert_eq!(plan.assignments[1].attribute, "CustomerActive");
    assert_eq!(plan.assignments[1].column, "CustomerActive");
    assert_eq!(plan.assignments[1].expr_sql, "TRUE");

    // Verify all 4 SQL dialects
    assert_eq!(
        plan.sql["ansi"],
        "UPDATE Customer SET CustomerStatus = 'Active', CustomerActive = TRUE WHERE CustomerId = :CustomerId"
    );
    assert_eq!(
        plan.sql["postgresql"],
        "UPDATE Customer SET CustomerStatus = 'Active', CustomerActive = TRUE WHERE CustomerId = :CustomerId"
    );
    assert_eq!(
        plan.sql["sqlserver"],
        "UPDATE Customer SET CustomerStatus = 'Active', CustomerActive = 1 WHERE CustomerId = :CustomerId"
    );
    assert_eq!(
        plan.sql["oracle"],
        "UPDATE Customer SET CustomerStatus = 'Active', CustomerActive = 1 WHERE CustomerId = :CustomerId"
    );
}

#[test]
fn test_fail_closed_spc0034_cannot_update_primary_key_in_for_each() {
    let tables = create_test_tables();
    let customer = &tables[0];

    let body = vec![
        Stmt::AssignAttribute("CustomerStatus".into(), Expr::Text("Active".into())),
        Stmt::AssignAttribute("CustomerId".into(), Expr::Integer(999)),
    ];

    let err = plan_updates_for_for_each(customer, &body, SqlDialect::Ansi).unwrap_err();
    assert!(err.contains("spc0034"));
    assert_eq!(
        err,
        "spc0034: Cannot update primary key attribute 'CustomerId' in table 'Customer'"
    );

    // Case-insensitive primary key check
    let body_case = vec![Stmt::AssignAttribute(
        "customerid".into(),
        Expr::Integer(123),
    )];
    let err_case = plan_updates_for_for_each(customer, &body_case, SqlDialect::Ansi).unwrap_err();
    assert!(err_case.contains("spc0034"));
    assert_eq!(
        err_case,
        "spc0034: Cannot update primary key attribute 'customerid' in table 'Customer'"
    );
}

#[test]
fn test_plan_delete_inside_for_each_multi_dialect_sql() {
    let tables = create_test_tables();
    let customer = &tables[0];
    let invoice_line = &tables[2];

    // Single primary key table
    let plan = plan_delete_for_for_each(customer, SqlDialect::Ansi);
    assert_eq!(plan.operation, NavigationDmlOp::Delete);
    assert_eq!(plan.target_table, "Customer");
    assert_eq!(plan.primary_key, vec!["CustomerId"]);
    assert_eq!(plan.where_predicates, vec!["CustomerId = :CustomerId"]);
    assert!(plan.assignments.is_empty());
    assert!(!plan.has_when_duplicate);

    for dialect in ["ansi", "sqlserver", "postgresql", "oracle"] {
        assert_eq!(
            plan.sql[dialect],
            "DELETE FROM Customer WHERE CustomerId = :CustomerId"
        );
    }

    // Composite primary key table
    let plan_composite = plan_delete_for_for_each(invoice_line, SqlDialect::Ansi);
    assert_eq!(plan_composite.operation, NavigationDmlOp::Delete);
    assert_eq!(plan_composite.target_table, "InvoiceLine");
    assert_eq!(plan_composite.primary_key, vec!["InvoiceId", "LineId"]);
    assert_eq!(
        plan_composite.where_predicates,
        vec!["InvoiceId = :InvoiceId", "LineId = :LineId"]
    );

    for dialect in ["ansi", "sqlserver", "postgresql", "oracle"] {
        assert_eq!(
            plan_composite.sql[dialect],
            "DELETE FROM InvoiceLine WHERE InvoiceId = :InvoiceId AND LineId = :LineId"
        );
    }
}

#[test]
fn test_plan_insert_basic_new_multi_dialect_sql() {
    let tables = create_test_tables();

    let new_stmt = NewStmt {
        body: vec![
            Stmt::AssignAttribute("CustomerId".into(), Expr::Integer(1)),
            Stmt::AssignAttribute("CustomerName".into(), Expr::Text("Acme Corp".into())),
            Stmt::AssignAttribute("CustomerActive".into(), Expr::Boolean(true)),
        ],
        when_duplicate: vec![],
    };

    let plan = plan_insert_for_new(&new_stmt, &tables, SqlDialect::Ansi)
        .expect("insert planning should succeed");
    assert_eq!(plan.operation, NavigationDmlOp::Insert);
    assert_eq!(plan.target_table, "Customer");
    assert_eq!(plan.primary_key, vec!["CustomerId"]);
    assert!(!plan.has_when_duplicate);
    assert_eq!(plan.assignments.len(), 3);

    assert_eq!(
        plan.sql["ansi"],
        "INSERT INTO Customer (CustomerId, CustomerName, CustomerActive) VALUES (1, 'Acme Corp', TRUE)"
    );
    assert_eq!(
        plan.sql["postgresql"],
        "INSERT INTO Customer (CustomerId, CustomerName, CustomerActive) VALUES (1, 'Acme Corp', TRUE)"
    );
    assert_eq!(
        plan.sql["sqlserver"],
        "INSERT INTO Customer (CustomerId, CustomerName, CustomerActive) VALUES (1, 'Acme Corp', 1)"
    );
    assert_eq!(
        plan.sql["oracle"],
        "INSERT INTO Customer (CustomerId, CustomerName, CustomerActive) VALUES (1, 'Acme Corp', 1)"
    );
}

#[test]
fn test_plan_insert_when_duplicate_multi_dialect_sql_conflict_handling() {
    let tables = create_test_tables();

    // Single PK table with WHEN DUPLICATE
    let new_customer = NewStmt {
        body: vec![
            Stmt::AssignAttribute("CustomerId".into(), Expr::Integer(42)),
            Stmt::AssignAttribute("CustomerName".into(), Expr::Text("Duplicate Inc".into())),
        ],
        when_duplicate: vec![Stmt::AssignAttribute(
            "CustomerName".into(),
            Expr::Text("Existing".into()),
        )],
    };

    let plan_cust =
        plan_insert_for_new(&new_customer, &tables, SqlDialect::Ansi).expect("should succeed");
    assert!(plan_cust.has_when_duplicate);

    assert_eq!(
        plan_cust.sql["ansi"],
        "INSERT INTO Customer (CustomerId, CustomerName) VALUES (42, 'Duplicate Inc')"
    );
    assert_eq!(
        plan_cust.sql["sqlserver"],
        "IF NOT EXISTS (SELECT 1 FROM Customer WHERE CustomerId = 42) INSERT INTO Customer (CustomerId, CustomerName) VALUES (42, 'Duplicate Inc')"
    );
    assert_eq!(
        plan_cust.sql["postgresql"],
        "INSERT INTO Customer (CustomerId, CustomerName) VALUES (42, 'Duplicate Inc') ON CONFLICT (CustomerId) DO NOTHING"
    );
    assert_eq!(
        plan_cust.sql["oracle"],
        "MERGE INTO Customer t USING (SELECT 42 AS CustomerId FROM dual) s ON (t.CustomerId = s.CustomerId) WHEN NOT MATCHED THEN INSERT (CustomerId, CustomerName) VALUES (42, 'Duplicate Inc')"
    );

    // Composite PK table with WHEN DUPLICATE
    let new_line = NewStmt {
        body: vec![
            Stmt::AssignAttribute("InvoiceId".into(), Expr::Integer(100)),
            Stmt::AssignAttribute("LineId".into(), Expr::Integer(1)),
            Stmt::AssignAttribute("Quantity".into(), Expr::Integer(5)),
        ],
        when_duplicate: vec![Stmt::AssignAttribute("Quantity".into(), Expr::Integer(10))],
    };

    let plan_line =
        plan_insert_for_new(&new_line, &tables, SqlDialect::Ansi).expect("should succeed");
    assert!(plan_line.has_when_duplicate);

    assert_eq!(
        plan_line.sql["sqlserver"],
        "IF NOT EXISTS (SELECT 1 FROM InvoiceLine WHERE InvoiceId = 100 AND LineId = 1) INSERT INTO InvoiceLine (InvoiceId, LineId, Quantity) VALUES (100, 1, 5)"
    );
    assert_eq!(
        plan_line.sql["postgresql"],
        "INSERT INTO InvoiceLine (InvoiceId, LineId, Quantity) VALUES (100, 1, 5) ON CONFLICT (InvoiceId, LineId) DO NOTHING"
    );
    assert_eq!(
        plan_line.sql["oracle"],
        "MERGE INTO InvoiceLine t USING (SELECT 100 AS InvoiceId, 1 AS LineId FROM dual) s ON (t.InvoiceId = s.InvoiceId AND t.LineId = s.LineId) WHEN NOT MATCHED THEN INSERT (InvoiceId, LineId, Quantity) VALUES (100, 1, 5)"
    );
}

#[test]
fn test_fail_closed_spc0023_incomplete_primary_key_for_insert() {
    let tables = create_test_tables();

    // Single PK missing: only CustomerName is assigned, CustomerId is missing
    let orphan_customer = NewStmt {
        body: vec![Stmt::AssignAttribute(
            "CustomerName".into(),
            Expr::Text("Orphan".into()),
        )],
        when_duplicate: vec![],
    };

    let err = plan_insert_for_new(&orphan_customer, &tables, SqlDialect::Ansi).unwrap_err();
    assert!(err.contains("spc0023"));
    assert_eq!(
        err,
        "spc0023: Primary key is not completely specified for insertion in table 'Customer' (missing: [\"CustomerId\"])"
    );

    // Composite PK missing: InvoiceId assigned, but LineId missing
    let incomplete_line = NewStmt {
        body: vec![
            Stmt::AssignAttribute("InvoiceId".into(), Expr::Integer(100)),
            Stmt::AssignAttribute("Quantity".into(), Expr::Integer(5)),
        ],
        when_duplicate: vec![],
    };

    let err_composite =
        plan_insert_for_new(&incomplete_line, &tables, SqlDialect::Ansi).unwrap_err();
    assert!(err_composite.contains("spc0023"));
    assert_eq!(
        err_composite,
        "spc0023: Primary key is not completely specified for insertion in table 'InvoiceLine' (missing: [\"LineId\"])"
    );
}

#[test]
fn test_multi_table_updates_and_deletes_in_nested_loops() {
    let tables = create_test_tables();
    let customer = &tables[0];
    let invoice = &tables[1];

    let inner_for_each = ForEachStmt {
        explicit_attributes: vec![],
        using: None,
        wheres: vec![],
        orders: vec![],
        defined_by: None,
        body: vec![
            Stmt::AssignAttribute("InvoiceStatus".into(), Expr::Text("Archived".into())),
            Stmt::Delete,
        ],
        when_none: vec![],
    };

    let outer_for_each = ForEachStmt {
        explicit_attributes: vec![],
        using: None,
        wheres: vec![],
        orders: vec![],
        defined_by: None,
        body: vec![
            Stmt::AssignAttribute("CustomerStatus".into(), Expr::Text("Preferred".into())),
            Stmt::ForEach(Box::new(inner_for_each)),
        ],
        when_none: vec![],
    };

    // Plan outer update: should target Customer and NOT pick up inner loop assignments
    let outer_update_opt =
        plan_updates_for_for_each(customer, &outer_for_each.body, SqlDialect::Ansi)
            .expect("should plan outer update");
    assert!(outer_update_opt.is_some());
    let outer_plan = outer_update_opt.unwrap();
    assert_eq!(outer_plan.target_table, "Customer");
    assert_eq!(outer_plan.assignments.len(), 1);
    assert_eq!(outer_plan.assignments[0].column, "CustomerStatus");
    assert_eq!(
        outer_plan.sql["ansi"],
        "UPDATE Customer SET CustomerStatus = 'Preferred' WHERE CustomerId = :CustomerId"
    );

    // Plan inner delete: targets Invoice
    let inner_delete = plan_delete_for_for_each(invoice, SqlDialect::Ansi);
    assert_eq!(inner_delete.target_table, "Invoice");
    assert_eq!(
        inner_delete.sql["ansi"],
        "DELETE FROM Invoice WHERE InvoiceId = :InvoiceId"
    );
}

#[test]
fn test_new_statement_with_expression_assignments() {
    let tables = create_test_tables();

    // Expression: LineTotal = Price * Quantity
    let new_stmt = NewStmt {
        body: vec![
            Stmt::AssignAttribute("InvoiceId".into(), Expr::Integer(200)),
            Stmt::AssignAttribute("LineId".into(), Expr::Integer(2)),
            Stmt::AssignAttribute("Price".into(), Expr::Decimal("19.99".into())),
            Stmt::AssignAttribute(
                "Quantity".into(),
                Expr::Binary(
                    Box::new(Expr::Variable("BaseQty".into())),
                    BinOp::Add,
                    Box::new(Expr::Integer(1)),
                ),
            ),
            Stmt::AssignAttribute(
                "LineTotal".into(),
                Expr::Binary(
                    Box::new(Expr::Attribute("Price".into())),
                    BinOp::Multiply,
                    Box::new(Expr::Attribute("Quantity".into())),
                ),
            ),
        ],
        when_duplicate: vec![],
    };

    let plan = plan_insert_for_new(&new_stmt, &tables, SqlDialect::Ansi).expect("should succeed");
    assert_eq!(plan.target_table, "InvoiceLine");
    assert_eq!(plan.assignments.len(), 5);

    assert_eq!(plan.assignments[3].column, "Quantity");
    assert_eq!(plan.assignments[3].expr_sql, ":BaseQty + 1");

    assert_eq!(plan.assignments[4].column, "LineTotal");
    assert_eq!(plan.assignments[4].expr_sql, "Price * Quantity");

    assert_eq!(
        plan.sql["ansi"],
        "INSERT INTO InvoiceLine (InvoiceId, LineId, Price, Quantity, LineTotal) VALUES (200, 2, 19.99, :BaseQty + 1, Price * Quantity)"
    );
}

#[test]
fn test_dml_plans_serialization_and_json_structure() {
    let tables = create_test_tables();
    let customer = &tables[0];

    let body = vec![Stmt::AssignAttribute(
        "CustomerStatus".into(),
        Expr::Text("VIP".into()),
    )];
    let plan = plan_updates_for_for_each(customer, &body, SqlDialect::Ansi)
        .unwrap()
        .unwrap();

    let json_str = serde_json::to_string(&plan).expect("serialization should work");
    let deserialized: NavigationDmlPlan =
        serde_json::from_str(&json_str).expect("deserialization should work");

    assert_eq!(plan, deserialized);

    // Verify key fields in JSON structure
    let json_val: serde_json::Value = serde_json::from_str(&json_str).unwrap();
    assert_eq!(json_val["operation"], "Update");
    assert_eq!(json_val["target_table"], "Customer");
    assert_eq!(json_val["primary_key"][0], "CustomerId");
    assert_eq!(json_val["assignments"][0]["column"], "CustomerStatus");
    assert_eq!(json_val["where_predicates"][0], "CustomerId = :CustomerId");
    assert_eq!(json_val["has_when_duplicate"], false);
    assert!(json_val["sql"]["ansi"].is_string());
    assert!(json_val["sql"]["sqlserver"].is_string());
    assert!(json_val["sql"]["postgresql"].is_string());
    assert!(json_val["sql"]["oracle"].is_string());
}

#[test]
fn test_end_to_end_procedure_dml_planning_and_indexes() {
    let tables = create_test_tables();
    let customer = &tables[0];
    let invoice = &tables[1];
    let invoice_line = &tables[2];

    // 1. Verify indexes on tables are respected
    assert_eq!(customer.indexes.len(), 2);
    assert!(customer.indexes[0].is_primary);
    assert_eq!(customer.indexes[0].columns, customer.primary_key);

    assert_eq!(invoice_line.indexes.len(), 1);
    assert!(invoice_line.indexes[0].is_primary);
    assert_eq!(invoice_line.indexes[0].columns, invoice_line.primary_key);

    // 2. Perform UPDATE planning
    let update_body = vec![
        Stmt::AssignAttribute("CustomerStatus".into(), Expr::Text("Verified".into())),
        Stmt::AssignAttribute("CustomerActive".into(), Expr::Boolean(false)),
    ];
    let update_plan = plan_updates_for_for_each(customer, &update_body, SqlDialect::PostgreSql)
        .unwrap()
        .unwrap();
    assert_eq!(update_plan.operation, NavigationDmlOp::Update);
    assert_eq!(update_plan.primary_key, customer.primary_key);
    assert_eq!(
        update_plan.sql["postgresql"],
        "UPDATE Customer SET CustomerStatus = 'Verified', CustomerActive = FALSE WHERE CustomerId = :CustomerId"
    );

    // 3. Perform DELETE planning
    let delete_plan = plan_delete_for_for_each(invoice, SqlDialect::SqlServer);
    assert_eq!(delete_plan.operation, NavigationDmlOp::Delete);
    assert_eq!(delete_plan.primary_key, invoice.primary_key);
    assert_eq!(
        delete_plan.sql["sqlserver"],
        "DELETE FROM Invoice WHERE InvoiceId = :InvoiceId"
    );

    // 4. Perform INSERT planning with composite key and WHEN DUPLICATE
    let insert_stmt = NewStmt {
        body: vec![
            Stmt::AssignAttribute("InvoiceId".into(), Expr::Integer(500)),
            Stmt::AssignAttribute("LineId".into(), Expr::Integer(1)),
            Stmt::AssignAttribute("ProductId".into(), Expr::Integer(99)),
            Stmt::AssignAttribute("Quantity".into(), Expr::Integer(10)),
            Stmt::AssignAttribute("Price".into(), Expr::Decimal("25.00".into())),
            Stmt::AssignAttribute(
                "LineTotal".into(),
                Expr::Binary(
                    Box::new(Expr::Attribute("Price".into())),
                    BinOp::Multiply,
                    Box::new(Expr::Attribute("Quantity".into())),
                ),
            ),
        ],
        when_duplicate: vec![Stmt::AssignAttribute("Quantity".into(), Expr::Integer(20))],
    };

    let insert_plan = plan_insert_for_new(&insert_stmt, &tables, SqlDialect::Oracle).unwrap();
    assert_eq!(insert_plan.operation, NavigationDmlOp::Insert);
    assert_eq!(insert_plan.target_table, "InvoiceLine");
    assert_eq!(insert_plan.primary_key, invoice_line.primary_key);
    assert!(insert_plan.has_when_duplicate);

    // Validate Oracle MERGE
    assert_eq!(
        insert_plan.sql["oracle"],
        "MERGE INTO InvoiceLine t USING (SELECT 500 AS InvoiceId, 1 AS LineId FROM dual) s ON (t.InvoiceId = s.InvoiceId AND t.LineId = s.LineId) WHEN NOT MATCHED THEN INSERT (InvoiceId, LineId, ProductId, Quantity, Price, LineTotal) VALUES (500, 1, 99, 10, 25.00, Price * Quantity)"
    );

    // Validate SQL Server IF NOT EXISTS
    assert_eq!(
        insert_plan.sql["sqlserver"],
        "IF NOT EXISTS (SELECT 1 FROM InvoiceLine WHERE InvoiceId = 500 AND LineId = 1) INSERT INTO InvoiceLine (InvoiceId, LineId, ProductId, Quantity, Price, LineTotal) VALUES (500, 1, 99, 10, 25.00, Price * Quantity)"
    );

    // Validate PostgreSQL ON CONFLICT
    assert_eq!(
        insert_plan.sql["postgresql"],
        "INSERT INTO InvoiceLine (InvoiceId, LineId, ProductId, Quantity, Price, LineTotal) VALUES (500, 1, 99, 10, 25.00, Price * Quantity) ON CONFLICT (InvoiceId, LineId) DO NOTHING"
    );

    // Validate ANSI standard
    assert_eq!(
        insert_plan.sql["ansi"],
        "INSERT INTO InvoiceLine (InvoiceId, LineId, ProductId, Quantity, Price, LineTotal) VALUES (500, 1, 99, 10, 25.00, Price * Quantity)"
    );
}
