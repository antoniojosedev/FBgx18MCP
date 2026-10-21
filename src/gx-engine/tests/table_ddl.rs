use gx_engine::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::model::DataType;
use gx_engine::table::{
    SqlDialect, Table, TableColumn, TableForeignKey, TableIndex, derive_tables_from_catalog,
    derive_tables_from_transaction, generate_database_ddl,
};
use gx_engine::transaction::{TransactionAttributeRef, TransactionLevel, TransactionStructure};

fn id(s: &str) -> ObjectId {
    ObjectId::new(s).unwrap()
}

#[test]
fn test_sql_dialect_data_type_mappings() {
    let small_int = DataType::Numeric {
        precision: 4,
        scale: 0,
    };
    let regular_int = DataType::Numeric {
        precision: 9,
        scale: 0,
    };
    let big_int = DataType::Numeric {
        precision: 18,
        scale: 0,
    };
    let decimal_val = DataType::Numeric {
        precision: 12,
        scale: 2,
    };
    let char_val = DataType::Character { length: 10 };
    let varchar_val = DataType::Varchar { length: 100 };
    let bool_val = DataType::Boolean {};
    let date_val = DataType::Date {};
    let datetime_val = DataType::DateTime {};
    let complex_val = DataType::Record {
        schema: "MySchema".into(),
    };

    // SQL Server
    assert_eq!(
        SqlDialect::SqlServer.map_data_type(&small_int).unwrap(),
        "SMALLINT"
    );
    assert_eq!(
        SqlDialect::SqlServer.map_data_type(&regular_int).unwrap(),
        "INT"
    );
    assert_eq!(
        SqlDialect::SqlServer.map_data_type(&big_int).unwrap(),
        "BIGINT"
    );
    assert_eq!(
        SqlDialect::SqlServer.map_data_type(&decimal_val).unwrap(),
        "DECIMAL(12, 2)"
    );
    assert_eq!(
        SqlDialect::SqlServer.map_data_type(&char_val).unwrap(),
        "CHAR(10)"
    );
    assert_eq!(
        SqlDialect::SqlServer.map_data_type(&varchar_val).unwrap(),
        "VARCHAR(100)"
    );
    assert_eq!(
        SqlDialect::SqlServer.map_data_type(&bool_val).unwrap(),
        "BIT"
    );
    assert_eq!(
        SqlDialect::SqlServer.map_data_type(&date_val).unwrap(),
        "DATE"
    );
    assert_eq!(
        SqlDialect::SqlServer.map_data_type(&datetime_val).unwrap(),
        "DATETIME2"
    );
    assert!(SqlDialect::SqlServer.map_data_type(&complex_val).is_err());

    // PostgreSQL
    assert_eq!(
        SqlDialect::PostgreSql.map_data_type(&small_int).unwrap(),
        "SMALLINT"
    );
    assert_eq!(
        SqlDialect::PostgreSql.map_data_type(&regular_int).unwrap(),
        "INTEGER"
    );
    assert_eq!(
        SqlDialect::PostgreSql.map_data_type(&big_int).unwrap(),
        "BIGINT"
    );
    assert_eq!(
        SqlDialect::PostgreSql.map_data_type(&decimal_val).unwrap(),
        "NUMERIC(12, 2)"
    );
    assert_eq!(
        SqlDialect::PostgreSql.map_data_type(&char_val).unwrap(),
        "CHAR(10)"
    );
    assert_eq!(
        SqlDialect::PostgreSql.map_data_type(&varchar_val).unwrap(),
        "VARCHAR(100)"
    );
    assert_eq!(
        SqlDialect::PostgreSql.map_data_type(&bool_val).unwrap(),
        "BOOLEAN"
    );
    assert_eq!(
        SqlDialect::PostgreSql.map_data_type(&date_val).unwrap(),
        "DATE"
    );
    assert_eq!(
        SqlDialect::PostgreSql.map_data_type(&datetime_val).unwrap(),
        "TIMESTAMP"
    );

    // Oracle
    assert_eq!(
        SqlDialect::Oracle.map_data_type(&small_int).unwrap(),
        "NUMBER(4)"
    );
    assert_eq!(
        SqlDialect::Oracle.map_data_type(&regular_int).unwrap(),
        "NUMBER(9)"
    );
    assert_eq!(
        SqlDialect::Oracle.map_data_type(&big_int).unwrap(),
        "NUMBER(18)"
    );
    assert_eq!(
        SqlDialect::Oracle.map_data_type(&decimal_val).unwrap(),
        "NUMBER(12, 2)"
    );
    assert_eq!(
        SqlDialect::Oracle.map_data_type(&char_val).unwrap(),
        "CHAR(10)"
    );
    assert_eq!(
        SqlDialect::Oracle.map_data_type(&varchar_val).unwrap(),
        "VARCHAR2(100)"
    );
    assert_eq!(
        SqlDialect::Oracle.map_data_type(&bool_val).unwrap(),
        "NUMBER(1)"
    );
    assert_eq!(SqlDialect::Oracle.map_data_type(&date_val).unwrap(), "DATE");
    assert_eq!(
        SqlDialect::Oracle.map_data_type(&datetime_val).unwrap(),
        "TIMESTAMP"
    );

    // ANSI
    assert_eq!(
        SqlDialect::Ansi.map_data_type(&small_int).unwrap(),
        "SMALLINT"
    );
    assert_eq!(
        SqlDialect::Ansi.map_data_type(&regular_int).unwrap(),
        "INTEGER"
    );
    assert_eq!(SqlDialect::Ansi.map_data_type(&big_int).unwrap(), "BIGINT");
    assert_eq!(
        SqlDialect::Ansi.map_data_type(&decimal_val).unwrap(),
        "DECIMAL(12, 2)"
    );
    assert_eq!(
        SqlDialect::Ansi.map_data_type(&char_val).unwrap(),
        "CHAR(10)"
    );
    assert_eq!(
        SqlDialect::Ansi.map_data_type(&varchar_val).unwrap(),
        "VARCHAR(100)"
    );
    assert_eq!(
        SqlDialect::Ansi.map_data_type(&bool_val).unwrap(),
        "BOOLEAN"
    );
    assert_eq!(SqlDialect::Ansi.map_data_type(&date_val).unwrap(), "DATE");
    assert_eq!(
        SqlDialect::Ansi.map_data_type(&datetime_val).unwrap(),
        "TIMESTAMP"
    );
}

#[test]
fn test_table_validation_rules() {
    // Valid table
    let mut table = Table::new("Customer");
    table.add_column(
        TableColumn::new(
            "CustomerId",
            DataType::Numeric {
                precision: 9,
                scale: 0,
            },
        )
        .key(true),
    );
    table.add_column(
        TableColumn::new("CustomerName", DataType::Varchar { length: 50 }).nullable(true),
    );
    assert!(table.validate().is_ok());

    // Empty name
    let mut bad_name = table.clone();
    bad_name.name = "".into();
    assert!(bad_name.validate().is_err());

    // Invalid chars in name
    let mut bad_chars = table.clone();
    bad_chars.name = "Customer;Drop Table".into();
    assert!(bad_chars.validate().is_err());

    // No columns
    let empty_cols = Table::new("EmptyTable");
    assert!(empty_cols.validate().is_err());

    // Duplicate column names (case-insensitive)
    let mut dup_cols = table.clone();
    dup_cols.add_column(TableColumn::new(
        "customername",
        DataType::Varchar { length: 50 },
    ));
    assert!(dup_cols.validate().is_err());

    // Primary key column not in columns
    let mut missing_pk = table.clone();
    missing_pk.primary_key = vec!["NonExistentId".into()];
    assert!(missing_pk.validate().is_err());

    // Nullable primary key column
    let mut nullable_pk = Table::new("NullablePkTable");
    nullable_pk.add_column(TableColumn {
        name: "Id".into(),
        data_type: DataType::Numeric {
            precision: 9,
            scale: 0,
        },
        is_key: true,
        is_nullable: true,
        description: None,
    });
    nullable_pk.primary_key = vec!["Id".into()];
    assert!(nullable_pk.validate().is_err());

    // Foreign key with invalid referenced column count
    let mut bad_fk = table.clone();
    bad_fk.add_foreign_key(TableForeignKey::new(
        "FK_Cust_Other",
        vec!["CustomerId".into()],
        "OtherTable",
        vec!["OtherId".into(), "ExtraId".into()],
    ));
    assert!(bad_fk.validate().is_err());

    // Foreign key with unknown column
    let mut bad_fk_col = table.clone();
    bad_fk_col.add_foreign_key(TableForeignKey::new(
        "FK_Cust_Other",
        vec!["UnknownCol".into()],
        "OtherTable",
        vec!["OtherId".into()],
    ));
    assert!(bad_fk_col.validate().is_err());

    // Index with unknown column
    let mut bad_idx = table.clone();
    bad_idx.add_index(TableIndex::new(
        "Idx_Cust",
        vec!["UnknownCol".into()],
        false,
    ));
    assert!(bad_idx.validate().is_err());
}

#[test]
fn test_single_level_transaction_to_table_lowering() {
    let mut root_level = TransactionLevel::new("Customer");
    root_level.attributes = vec![
        TransactionAttributeRef {
            name: "CustomerId".into(),
            is_key: true,
            data_type: Some(DataType::Numeric {
                precision: 9,
                scale: 0,
            }),
            formula: None,
            is_nullable: false,
            is_foreign_key: false,
        },
        TransactionAttributeRef {
            name: "CustomerName".into(),
            is_key: false,
            data_type: Some(DataType::Varchar { length: 80 }),
            formula: None,
            is_nullable: false,
            is_foreign_key: false,
        },
        TransactionAttributeRef {
            name: "CustomerActive".into(),
            is_key: false,
            data_type: Some(DataType::Boolean {}),
            formula: None,
            is_nullable: false,
            is_foreign_key: false,
        },
        TransactionAttributeRef {
            name: "CustomerBalance".into(),
            is_key: false,
            data_type: Some(DataType::Numeric {
                precision: 12,
                scale: 2,
            }),
            formula: None,
            is_nullable: true,
            is_foreign_key: false,
        },
        // Virtual formula attribute - must NOT be included in physical table
        TransactionAttributeRef {
            name: "CustomerTotalPurchases".into(),
            is_key: false,
            data_type: Some(DataType::Numeric {
                precision: 12,
                scale: 2,
            }),
            formula: Some("Sum(InvoiceTotal)".into()),
            is_nullable: true,
            is_foreign_key: false,
        },
    ];

    let structure = TransactionStructure::new(root_level);
    let tables = derive_tables_from_transaction(&structure, |_| None).unwrap();

    assert_eq!(tables.len(), 1);
    let customer_table = &tables[0];
    assert_eq!(customer_table.name, "Customer");
    assert_eq!(customer_table.primary_key, vec!["CustomerId"]);
    // Formula attribute must be excluded: 5 attributes - 1 formula = 4 columns
    assert_eq!(customer_table.columns.len(), 4);
    assert_eq!(customer_table.columns[0].name, "CustomerId");
    assert!(customer_table.columns[0].is_key);
    assert!(!customer_table.columns[0].is_nullable);
    assert_eq!(customer_table.columns[1].name, "CustomerName");
    assert_eq!(customer_table.columns[2].name, "CustomerActive");
    assert_eq!(customer_table.columns[3].name, "CustomerBalance");
    assert!(customer_table.columns[3].is_nullable);

    // Verify SQL Server DDL
    let sqlserver_ddl = customer_table.to_full_ddl(SqlDialect::SqlServer).unwrap();
    assert!(sqlserver_ddl.contains("CREATE TABLE [Customer] ("));
    assert!(sqlserver_ddl.contains("  [CustomerId] INT NOT NULL,"));
    assert!(sqlserver_ddl.contains("  [CustomerName] VARCHAR(80) NOT NULL,"));
    assert!(sqlserver_ddl.contains("  [CustomerActive] BIT NOT NULL,"));
    assert!(sqlserver_ddl.contains("  [CustomerBalance] DECIMAL(12, 2) NULL,"));
    assert!(sqlserver_ddl.contains("  PRIMARY KEY ([CustomerId])"));

    // Verify PostgreSQL DDL
    let pg_ddl = customer_table.to_full_ddl(SqlDialect::PostgreSql).unwrap();
    assert!(pg_ddl.contains("CREATE TABLE \"Customer\" ("));
    assert!(pg_ddl.contains("  \"CustomerId\" INTEGER NOT NULL,"));
    assert!(pg_ddl.contains("  \"CustomerActive\" BOOLEAN NOT NULL,"));
    assert!(pg_ddl.contains("  \"CustomerBalance\" NUMERIC(12, 2) NULL,"));
    assert!(pg_ddl.contains("  PRIMARY KEY (\"CustomerId\")"));

    // Verify Oracle DDL
    let oracle_ddl = customer_table.to_full_ddl(SqlDialect::Oracle).unwrap();
    assert!(oracle_ddl.contains("CREATE TABLE Customer ("));
    assert!(oracle_ddl.contains("  CustomerId NUMBER(9) NOT NULL,"));
    assert!(oracle_ddl.contains("  CustomerName VARCHAR2(80) NOT NULL,"));
    assert!(oracle_ddl.contains("  CustomerActive NUMBER(1) NOT NULL,"));
    assert!(oracle_ddl.contains("  CustomerBalance NUMBER(12, 2) NULL,"));
    assert!(oracle_ddl.contains("  PRIMARY KEY (CustomerId)"));
}

#[test]
fn test_multi_level_transaction_to_tables_lowering() {
    let mut root_level = TransactionLevel::new("Invoice");
    root_level.attributes = vec![
        TransactionAttributeRef {
            name: "InvoiceId".into(),
            is_key: true,
            data_type: Some(DataType::Numeric {
                precision: 9,
                scale: 0,
            }),
            formula: None,
            is_nullable: false,
            is_foreign_key: false,
        },
        TransactionAttributeRef {
            name: "InvoiceDate".into(),
            is_key: false,
            data_type: Some(DataType::Date {}),
            formula: None,
            is_nullable: false,
            is_foreign_key: false,
        },
        TransactionAttributeRef {
            name: "CustomerId".into(),
            is_key: false,
            data_type: Some(DataType::Numeric {
                precision: 9,
                scale: 0,
            }),
            formula: None,
            is_nullable: false,
            is_foreign_key: true,
        },
    ];

    let mut detail_level = TransactionLevel::new("Detail");
    detail_level.attributes = vec![
        TransactionAttributeRef {
            name: "InvoiceDetailLineId".into(),
            is_key: true,
            data_type: Some(DataType::Numeric {
                precision: 4,
                scale: 0,
            }),
            formula: None,
            is_nullable: false,
            is_foreign_key: false,
        },
        TransactionAttributeRef {
            name: "ProductId".into(),
            is_key: false,
            data_type: Some(DataType::Numeric {
                precision: 9,
                scale: 0,
            }),
            formula: None,
            is_nullable: false,
            is_foreign_key: true,
        },
        TransactionAttributeRef {
            name: "InvoiceDetailQuantity".into(),
            is_key: false,
            data_type: Some(DataType::Numeric {
                precision: 8,
                scale: 2,
            }),
            formula: None,
            is_nullable: false,
            is_foreign_key: false,
        },
        TransactionAttributeRef {
            name: "InvoiceDetailPrice".into(),
            is_key: false,
            data_type: Some(DataType::Numeric {
                precision: 12,
                scale: 2,
            }),
            formula: None,
            is_nullable: false,
            is_foreign_key: false,
        },
    ];

    root_level.levels = vec![detail_level];
    let structure = TransactionStructure::new(root_level);
    let tables = derive_tables_from_transaction(&structure, |_| None).unwrap();

    assert_eq!(tables.len(), 2);

    // Root table: Invoice
    let invoice_table = &tables[0];
    assert_eq!(invoice_table.name, "Invoice");
    assert_eq!(invoice_table.primary_key, vec!["InvoiceId"]);
    assert_eq!(invoice_table.columns.len(), 3);
    assert_eq!(invoice_table.foreign_keys.len(), 0);

    // Child table: InvoiceDetail
    let detail_table = &tables[1];
    assert_eq!(detail_table.name, "InvoiceDetail");
    // Inherited InvoiceId + LineId
    assert_eq!(
        detail_table.primary_key,
        vec!["InvoiceId", "InvoiceDetailLineId"]
    );
    // Inherited InvoiceId + 4 detail attributes = 5 columns
    assert_eq!(detail_table.columns.len(), 5);
    assert_eq!(detail_table.columns[0].name, "InvoiceId");
    assert!(detail_table.columns[0].is_key);
    assert_eq!(detail_table.columns[1].name, "InvoiceDetailLineId");
    assert!(detail_table.columns[1].is_key);

    // Foreign key to parent table
    assert_eq!(detail_table.foreign_keys.len(), 1);
    let fk = &detail_table.foreign_keys[0];
    assert_eq!(fk.name, "FK_InvoiceDetail_Invoice");
    assert_eq!(fk.columns, vec!["InvoiceId"]);
    assert_eq!(fk.referenced_table, "Invoice");
    assert_eq!(fk.referenced_columns, vec!["InvoiceId"]);

    // DDL verification
    let ddl = detail_table.to_full_ddl(SqlDialect::SqlServer).unwrap();
    assert!(ddl.contains("CREATE TABLE [InvoiceDetail] ("));
    assert!(ddl.contains("  [InvoiceId] INT NOT NULL,"));
    assert!(ddl.contains("  [InvoiceDetailLineId] SMALLINT NOT NULL,"));
    assert!(ddl.contains("  [ProductId] INT NOT NULL,"));
    assert!(ddl.contains("  [InvoiceDetailQuantity] DECIMAL(8, 2) NOT NULL,"));
    assert!(ddl.contains("  [InvoiceDetailPrice] DECIMAL(12, 2) NOT NULL,"));
    assert!(ddl.contains("  PRIMARY KEY ([InvoiceId], [InvoiceDetailLineId]),"));
    assert!(ddl.contains(
        "  CONSTRAINT [FK_InvoiceDetail_Invoice] FOREIGN KEY ([InvoiceId]) REFERENCES [Invoice] ([InvoiceId])"
    ));
}

#[test]
fn test_three_level_deep_transaction_hierarchy() {
    let mut root = TransactionLevel::new("PurchaseOrder");
    root.attributes = vec![TransactionAttributeRef {
        name: "PurchaseOrderId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 9,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    }];

    let mut line = TransactionLevel::new("Line");
    line.attributes = vec![TransactionAttributeRef {
        name: "PurchaseOrderLineId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    }];

    let mut schedule = TransactionLevel::new("Schedule");
    schedule.attributes = vec![TransactionAttributeRef {
        name: "PurchaseOrderScheduleId".into(),
        is_key: true,
        data_type: Some(DataType::Numeric {
            precision: 4,
            scale: 0,
        }),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    }];

    line.levels = vec![schedule];
    root.levels = vec![line];

    let structure = TransactionStructure::new(root);
    let tables = derive_tables_from_transaction(&structure, |_| None).unwrap();

    assert_eq!(tables.len(), 3);
    assert_eq!(tables[0].name, "PurchaseOrder");
    assert_eq!(tables[0].primary_key, vec!["PurchaseOrderId"]);

    assert_eq!(tables[1].name, "PurchaseOrderLine");
    assert_eq!(
        tables[1].primary_key,
        vec!["PurchaseOrderId", "PurchaseOrderLineId"]
    );
    assert_eq!(tables[1].foreign_keys[0].referenced_table, "PurchaseOrder");

    assert_eq!(tables[2].name, "PurchaseOrderLineSchedule");
    assert_eq!(
        tables[2].primary_key,
        vec![
            "PurchaseOrderId",
            "PurchaseOrderLineId",
            "PurchaseOrderScheduleId"
        ]
    );
    assert_eq!(
        tables[2].foreign_keys[0].referenced_table,
        "PurchaseOrderLine"
    );
    assert_eq!(
        tables[2].foreign_keys[0].columns,
        vec!["PurchaseOrderId", "PurchaseOrderLineId"]
    );
}

#[test]
fn test_catalog_wide_table_derivation_and_cross_transaction_foreign_keys() {
    let mut catalog = Catalog::empty();

    // 1. Add Customer Transaction
    let mut customer_obj = KbObject::new(id("customer-id"), "Customer", ObjectKind::Transaction);
    customer_obj.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Customer",
                "attributes": [
                    {"name": "CustomerId", "is_key": true, "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
                    {"name": "CustomerName", "data_type": {"kind": "varchar", "length": 100}}
                ]
            }
        }"#.into(),
    });

    // 2. Add Invoice Transaction referencing Customer
    let mut invoice_obj = KbObject::new(id("invoice-id"), "Invoice", ObjectKind::Transaction);
    invoice_obj.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Invoice",
                "attributes": [
                    {"name": "InvoiceId", "is_key": true, "data_type": {"kind": "numeric", "precision": 9, "scale": 0}},
                    {"name": "InvoiceDate", "data_type": {"kind": "date"}},
                    {"name": "CustomerId", "data_type": {"kind": "numeric", "precision": 9, "scale": 0}, "is_foreign_key": true}
                ],
                "levels": [
                    {
                        "name": "Detail",
                        "attributes": [
                            {"name": "InvoiceDetailId", "is_key": true, "data_type": {"kind": "numeric", "precision": 4, "scale": 0}},
                            {"name": "InvoiceDetailAmount", "data_type": {"kind": "numeric", "precision": 10, "scale": 2}}
                        ]
                    }
                ]
            }
        }"#.into(),
    });
    invoice_obj.references.push(Reference {
        target: id("customer-id"),
        kind: ReferenceKind::Uses,
    });

    catalog
        .apply(0, &[Edit::Upsert(customer_obj), Edit::Upsert(invoice_obj)])
        .unwrap();

    let tables = derive_tables_from_catalog(&catalog).unwrap();
    assert_eq!(tables.len(), 3); // Customer, Invoice, InvoiceDetail

    let invoice_tbl = tables.iter().find(|t| t.name == "Invoice").unwrap();
    // Inferred cross-transaction foreign key from Invoice to Customer
    let cross_fk = invoice_tbl
        .foreign_keys
        .iter()
        .find(|fk| fk.referenced_table == "Customer")
        .expect("Invoice should have foreign key to Customer");
    assert_eq!(cross_fk.columns, vec!["CustomerId"]);
    assert_eq!(cross_fk.referenced_columns, vec!["CustomerId"]);

    // Inferred foreign key index on Invoice.CustomerId
    let fk_idx = invoice_tbl
        .indexes
        .iter()
        .find(|idx| idx.columns == vec!["CustomerId"])
        .expect("Invoice should have secondary index on CustomerId");
    assert!(!fk_idx.is_unique);
}

#[test]
fn test_database_ddl_generation_complete() {
    let mut table1 = Table::new("Department");
    table1.add_column(
        TableColumn::new(
            "DepartmentId",
            DataType::Numeric {
                precision: 4,
                scale: 0,
            },
        )
        .key(true),
    );
    table1.add_column(TableColumn::new(
        "DepartmentName",
        DataType::Varchar { length: 50 },
    ));

    let mut table2 = Table::new("Employee");
    table2.add_column(
        TableColumn::new(
            "EmployeeId",
            DataType::Numeric {
                precision: 9,
                scale: 0,
            },
        )
        .key(true),
    );
    table2.add_column(TableColumn::new(
        "EmployeeName",
        DataType::Varchar { length: 60 },
    ));
    table2.add_column(TableColumn::new(
        "DepartmentId",
        DataType::Numeric {
            precision: 4,
            scale: 0,
        },
    ));
    table2.add_foreign_key(TableForeignKey::new(
        "FK_Employee_Department",
        vec!["DepartmentId".into()],
        "Department",
        vec!["DepartmentId".into()],
    ));
    table2.add_index(TableIndex::new(
        "IEmployee_Department",
        vec!["DepartmentId".into()],
        false,
    ));

    let ddl = generate_database_ddl(&[table1, table2], SqlDialect::SqlServer).unwrap();
    assert!(ddl.contains("-- Physical DDL generated by gx-engine (SqlServer dialect)"));
    assert!(ddl.contains("CREATE TABLE [Department] ("));
    assert!(ddl.contains("  [DepartmentId] SMALLINT NOT NULL,"));
    assert!(ddl.contains("  PRIMARY KEY ([DepartmentId])"));
    assert!(ddl.contains("CREATE TABLE [Employee] ("));
    assert!(ddl.contains("  [EmployeeId] INT NOT NULL,"));
    assert!(ddl.contains("  [DepartmentId] SMALLINT NOT NULL,"));
    assert!(ddl.contains(
        "  CONSTRAINT [FK_Employee_Department] FOREIGN KEY ([DepartmentId]) REFERENCES [Department] ([DepartmentId])"
    ));
    assert!(ddl.contains("CREATE INDEX [IEmployee_Department] ON [Employee] ([DepartmentId]);"));
}

#[test]
fn test_attribute_type_resolution_from_catalog_dictionary() {
    let mut catalog = Catalog::empty();

    // Add Domain
    let mut domain_obj = KbObject::new(id("id-domain"), "IdDomain", ObjectKind::Domain);
    domain_obj.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"kind": "numeric", "precision": 9, "scale": 0}"#.into(),
    });

    // Add Attribute based on Domain
    let mut attr_obj = KbObject::new(id("user-id-att"), "UserId", ObjectKind::Attribute);
    attr_obj.source_parts.push(SourcePart {
        name: "TYPE".into(),
        source: r#"{"kind": "numeric", "precision": 9, "scale": 0}"#.into(),
    });
    attr_obj.references.push(Reference {
        target: id("id-domain"),
        kind: ReferenceKind::BasedOn,
    });

    // Add Transaction without inline data types on attributes
    let mut trn_obj = KbObject::new(id("app-user-trn"), "AppUser", ObjectKind::Transaction);
    trn_obj.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "AppUser",
                "attributes": [
                    {"name": "UserId", "is_key": true}
                ]
            }
        }"#
        .into(),
    });
    trn_obj.references.push(Reference {
        target: id("user-id-att"),
        kind: ReferenceKind::Uses,
    });

    catalog
        .apply(
            0,
            &[
                Edit::Upsert(domain_obj),
                Edit::Upsert(attr_obj),
                Edit::Upsert(trn_obj),
            ],
        )
        .unwrap();

    let tables = derive_tables_from_catalog(&catalog).unwrap();
    assert_eq!(tables.len(), 1);
    assert_eq!(tables[0].name, "AppUser");
    assert_eq!(tables[0].columns.len(), 1);
    assert_eq!(tables[0].columns[0].name, "UserId");
    assert_eq!(
        tables[0].columns[0].data_type,
        DataType::Numeric {
            precision: 9,
            scale: 0
        }
    );
}

#[test]
fn test_exportfile_xml_to_physical_table_ddl() {
    let xml = r#"<ExportFile><Objects>
<Object guid="dom-code" name="CustomerCode" type="00972a17-9975-449e-aab1-d26165d51393">
  <Properties>
    <Property><Name>Name</Name><Value>CustomerCode</Value></Property>
    <Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property>
    <Property><Name>Length</Name><Value>6</Value></Property>
    <Property><Name>Decimals</Name><Value>0</Value></Property>
  </Properties>
</Object>
<Object guid="att-cid" name="CustomerId" type="adbb33c9-0906-4971-833c-998de27e0676">
  <Properties>
    <Property><Name>Name</Name><Value>CustomerId</Value></Property>
    <Property><Name>DomainBasedOn</Name><Value>CustomerCode</Value></Property>
  </Properties>
</Object>
<Object guid="att-cname" name="CustomerName" type="adbb33c9-0906-4971-833c-998de27e0676">
  <Properties>
    <Property><Name>Name</Name><Value>CustomerName</Value></Property>
    <Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Character</Value></Property>
    <Property><Name>Length</Name><Value>50</Value></Property>
  </Properties>
</Object>
<Object guid="trn-cust" name="Customer" type="1db606f2-af09-4cf9-a3b5-b481519d28f6">
  <Part type="1608677c-a7a2-4a00-8809-6d2466085a5a">
    <Level Name="Customer">
      <Attribute Name="CustomerId">
        <Properties>
          <Property><Name>IsKey</Name><Value>True</Value></Property>
        </Properties>
      </Attribute>
      <Attribute Name="CustomerName">
        <Properties>
          <Property><Name>IsKey</Name><Value>False</Value></Property>
        </Properties>
      </Attribute>
    </Level>
  </Part>
  <Part type="9b0a32a3-de6d-4be1-a4dd-1b85d3741534">
    <Source><![CDATA[Error('Invalid') if CustomerId = 0;]]></Source>
  </Part>
  <Properties>
    <Property><Name>Name</Name><Value>Customer</Value></Property>
  </Properties>
</Object>
</Objects></ExportFile>"#;

    let catalog = gx_engine::exchange::catalog_from_xml(xml).unwrap();
    let tables = derive_tables_from_catalog(&catalog).unwrap();
    assert_eq!(tables.len(), 1);
    let table = &tables[0];
    assert_eq!(table.name.to_ascii_uppercase(), "CUSTOMER");
    assert_eq!(table.primary_key, vec!["CustomerId"]);
    assert_eq!(table.columns.len(), 2);
    assert_eq!(table.columns[0].name, "CustomerId");
    assert_eq!(
        table.columns[0].data_type,
        DataType::Numeric {
            precision: 6,
            scale: 0
        }
    );
    assert_eq!(table.columns[1].name, "CustomerName");
    assert_eq!(
        table.columns[1].data_type,
        DataType::Character { length: 50 }
    );

    let ddl = table.to_full_ddl(SqlDialect::SqlServer).unwrap();
    assert!(ddl.contains("CREATE TABLE [Customer] ("));
    assert!(ddl.contains("  [CustomerId] INT NOT NULL,"));
    assert!(ddl.contains("  [CustomerName] CHAR(50) NOT NULL,"));
    assert!(ddl.contains("  PRIMARY KEY ([CustomerId])"));
}

#[test]
fn database_ddl_creates_referenced_tables_before_their_dependents() {
    let mut parent = Table::new("Parent");
    parent.add_column(
        TableColumn::new(
            "Id",
            DataType::Numeric {
                precision: 4,
                scale: 0,
            },
        )
        .key(true),
    );
    let mut child = Table::new("Child");
    child.add_column(
        TableColumn::new(
            "Id",
            DataType::Numeric {
                precision: 4,
                scale: 0,
            },
        )
        .key(true),
    );
    child.add_foreign_key(TableForeignKey::new(
        "FK_Child_Parent",
        vec!["Id".into()],
        "Parent",
        vec!["Id".into()],
    ));
    for dialect in [
        SqlDialect::SqlServer,
        SqlDialect::PostgreSql,
        SqlDialect::Oracle,
        SqlDialect::Ansi,
    ] {
        let ddl = generate_database_ddl(&[child.clone(), parent.clone()], dialect).unwrap();
        let parent_create = format!("CREATE TABLE {}", dialect.quote_identifier("Parent"));
        let child_create = format!("CREATE TABLE {}", dialect.quote_identifier("Child"));
        assert!(
            ddl.find(&parent_create).unwrap() < ddl.find(&child_create).unwrap(),
            "{ddl}"
        );
    }
}

#[test]
fn database_ddl_refuses_missing_cyclic_and_duplicate_tables() {
    let make = |name: &str, target: &str| {
        let mut table = Table::new(name);
        table.add_column(
            TableColumn::new(
                "Id",
                DataType::Numeric {
                    precision: 4,
                    scale: 0,
                },
            )
            .key(true),
        );
        table.add_foreign_key(TableForeignKey::new(
            format!("FK_{name}_{target}"),
            vec!["Id".into()],
            target,
            vec!["Id".into()],
        ));
        table
    };
    let child = make("Child", "Parent");
    assert!(
        generate_database_ddl(std::slice::from_ref(&child), SqlDialect::SqlServer)
            .unwrap_err()
            .contains("missing table 'Parent'")
    );
    assert!(
        generate_database_ddl(
            &[child.clone(), make("Parent", "Child")],
            SqlDialect::SqlServer
        )
        .unwrap_err()
        .contains("cyclic foreign-key")
    );
    assert!(
        generate_database_ddl(&[child, make("child", "Parent")], SqlDialect::SqlServer)
            .unwrap_err()
            .contains("duplicate table")
    );
    assert!(generate_database_ddl(&[make("Self", "SELF")], SqlDialect::SqlServer).is_ok());
}
