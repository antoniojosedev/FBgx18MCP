//! Tests for DataView engine and external table mapping (Row H4 of Plan 129).

use gx_engine::catalog::{Catalog, Edit, KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::dataview::DataView;
use gx_engine::model::DataType;
use gx_engine::table::SqlDialect;

#[test]
fn test_dataview_basic_dsl_parsing() {
    let source = r#"
    // Header comment
    dataview CustomerView {
        table = Customer;
        external_name = tbl_customers_ext;
        platform = SqlServer;
        datastore = ExternalDb;
        /* multi-line comment */
        fields {
            CustomerId => cust_id : Numeric(10,0);
            CustomerName => cust_full_name : VarChar(100);
            CustomerStatus => cust_status_code : Character(1); // inline comment
        }
        index PkCustomer {
            external = pk_tbl_customers;
            primary = true;
            CustomerId;
        }
        index UxCustomerName {
            external = uq_tbl_customers_name;
            unique = true;
            CustomerName;
        }
    }
    "#;
    let dv = DataView::parse("CustomerView", source).expect("parsing should succeed");
    assert_eq!(dv.name, "CustomerView");
    assert_eq!(dv.associated_table, "Customer");
    assert_eq!(dv.external_name, "tbl_customers_ext");
    assert_eq!(dv.platform.as_deref(), Some("SqlServer"));
    assert_eq!(dv.datastore.as_deref(), Some("ExternalDb"));

    assert_eq!(dv.fields.len(), 3);
    assert_eq!(dv.fields[0].attribute_name, "CustomerId");
    assert_eq!(dv.fields[0].external_name, "cust_id");
    assert_eq!(
        dv.fields[0].data_type,
        DataType::Numeric {
            precision: 10,
            scale: 0,
        }
    );

    assert_eq!(dv.fields[1].attribute_name, "CustomerName");
    assert_eq!(dv.fields[1].external_name, "cust_full_name");
    assert_eq!(dv.fields[1].data_type, DataType::Varchar { length: 100 });

    assert_eq!(dv.fields[2].attribute_name, "CustomerStatus");
    assert_eq!(dv.fields[2].external_name, "cust_status_code");
    assert_eq!(dv.fields[2].data_type, DataType::Character { length: 1 });

    assert_eq!(dv.indexes.len(), 2);
    assert_eq!(dv.indexes[0].name, "PkCustomer");
    assert_eq!(dv.indexes[0].external_name, "pk_tbl_customers");
    assert!(dv.indexes[0].is_primary);
    assert_eq!(dv.indexes[0].keys, vec!["CustomerId"]);

    assert_eq!(dv.indexes[1].name, "UxCustomerName");
    assert_eq!(dv.indexes[1].external_name, "uq_tbl_customers_name");
    assert!(dv.indexes[1].is_unique);
    assert!(!dv.indexes[1].is_primary);
    assert_eq!(dv.indexes[1].keys, vec!["CustomerName"]);
}

#[test]
fn test_attribute_to_column_bidirectional_mapping() {
    let source = r#"
    dataview CustomerView {
        table = Customer;
        external_name = tbl_customers_ext;
        fields {
            CustomerId => cust_id : Numeric(10,0);
            CustomerName => cust_full_name : VarChar(100);
            CustomerStatus => cust_status_code : Character(1);
        }
    }
    "#;
    let dv = DataView::parse("CustomerView", source).unwrap();

    // Attribute to Column
    assert_eq!(dv.map_attribute_to_column("CustomerId"), Some("cust_id"));
    assert_eq!(
        dv.map_attribute_to_column("CustomerName"),
        Some("cust_full_name")
    );
    assert_eq!(
        dv.map_attribute_to_column("CustomerStatus"),
        Some("cust_status_code")
    );
    // Case-insensitive attribute lookup
    assert_eq!(dv.map_attribute_to_column("customerid"), Some("cust_id"));
    assert_eq!(
        dv.map_attribute_to_column("CUSTOMERNAME"),
        Some("cust_full_name")
    );
    assert_eq!(dv.map_attribute_to_column("NonExistentAttr"), None);

    // Column to Attribute
    assert_eq!(dv.map_column_to_attribute("cust_id"), Some("CustomerId"));
    assert_eq!(
        dv.map_column_to_attribute("cust_full_name"),
        Some("CustomerName")
    );
    assert_eq!(
        dv.map_column_to_attribute("cust_status_code"),
        Some("CustomerStatus")
    );
    // Case-insensitive column lookup
    assert_eq!(dv.map_column_to_attribute("CUST_ID"), Some("CustomerId"));
    assert_eq!(dv.map_column_to_attribute("unknown_col"), None);
}

#[test]
fn test_primary_and_unique_indexes_inspection() {
    let source = r#"
    dataview CustomerView {
        table = Customer;
        external_name = tbl_customers_ext;
        fields {
            CustomerId => cust_id : Numeric(10,0);
            CustomerName => cust_full_name : VarChar(100);
            CustomerStatus => cust_status_code : Character(1);
        }
        index PkCustomer {
            external = pk_tbl_customers;
            primary = true;
            CustomerId;
        }
        index UxCustomerName {
            external = uq_tbl_customers_name;
            unique = true;
            CustomerName;
        }
    }
    "#;
    let dv = DataView::parse("CustomerView", source).unwrap();

    let pk = dv.primary_index().expect("primary index should be present");
    assert_eq!(pk.name, "PkCustomer");
    assert_eq!(pk.external_name, "pk_tbl_customers");
    assert!(pk.is_primary);
    assert_eq!(pk.keys, vec!["CustomerId"]);

    let ux = dv
        .indexes
        .iter()
        .find(|idx| idx.name == "UxCustomerName")
        .expect("unique index should be present");
    assert_eq!(ux.external_name, "uq_tbl_customers_name");
    assert!(ux.is_unique);
    assert!(!ux.is_primary);
    assert_eq!(ux.keys, vec!["CustomerName"]);
}

#[test]
fn test_render_external_select_sql_server() {
    let source = r#"
    dataview CustomerView {
        table = Customer;
        external_name = tbl_customers_ext;
        fields {
            CustomerId => cust_id : Numeric(10,0);
            CustomerName => cust_full_name : VarChar(100);
            CustomerStatus => cust_status_code : Character(1);
        }
    }
    "#;
    let dv = DataView::parse("CustomerView", source).unwrap();
    let query = dv
        .render_external_select_query(
            SqlDialect::SqlServer,
            &["CustomerId".to_string(), "CustomerName".to_string()],
            None,
        )
        .expect("query rendering should succeed");
    assert_eq!(
        query,
        "SELECT [cust_id], [cust_full_name] FROM [tbl_customers_ext]"
    );
}

#[test]
fn test_render_external_select_postgresql() {
    let source = r#"
    dataview CustomerView {
        table = Customer;
        external_name = tbl_customers_ext;
        fields {
            CustomerId => cust_id : Numeric(10,0);
            CustomerName => cust_full_name : VarChar(100);
            CustomerStatus => cust_status_code : Character(1);
        }
    }
    "#;
    let dv = DataView::parse("CustomerView", source).unwrap();
    let query = dv
        .render_external_select_query(
            SqlDialect::PostgreSql,
            &["CustomerId".to_string(), "CustomerName".to_string()],
            None,
        )
        .expect("query rendering should succeed");
    assert_eq!(
        query,
        "SELECT \"cust_id\", \"cust_full_name\" FROM \"tbl_customers_ext\""
    );
}

#[test]
fn test_render_external_select_oracle() {
    let source = r#"
    dataview CustomerView {
        table = Customer;
        external_name = tbl_customers_ext;
        fields {
            CustomerId => cust_id : Numeric(10,0);
            CustomerName => cust_full_name : VarChar(100);
            CustomerStatus => cust_status_code : Character(1);
        }
    }
    "#;
    let dv = DataView::parse("CustomerView", source).unwrap();
    let query = dv
        .render_external_select_query(
            SqlDialect::Oracle,
            &["CustomerId".to_string(), "CustomerName".to_string()],
            None,
        )
        .expect("query rendering should succeed");
    assert_eq!(
        query,
        "SELECT \"cust_id\", \"cust_full_name\" FROM \"tbl_customers_ext\""
    );
}

#[test]
fn test_render_external_select_with_where_clause() {
    let source = r#"
    dataview CustomerView {
        table = Customer;
        external_name = tbl_customers_ext;
        fields {
            CustomerId => cust_id : Numeric(10,0);
            CustomerName => cust_full_name : VarChar(100);
            CustomerStatus => cust_status_code : Character(1);
        }
    }
    "#;
    let dv = DataView::parse("CustomerView", source).unwrap();
    let query = dv
        .render_external_select_query(
            SqlDialect::SqlServer,
            &["CustomerId".to_string(), "CustomerName".to_string()],
            Some("cust_status_code = 'A'"),
        )
        .expect("query rendering should succeed");
    assert_eq!(
        query,
        "SELECT [cust_id], [cust_full_name] FROM [tbl_customers_ext] WHERE cust_status_code = 'A'"
    );

    // Fail closed on unmapped attribute
    let err = dv
        .render_external_select_query(
            SqlDialect::SqlServer,
            &["UnknownAttribute".to_string()],
            None,
        )
        .unwrap_err();
    assert!(err.contains("UnknownAttribute"));
}

#[test]
fn test_dataview_validation_fail_closed_duplicate_fields() {
    // Duplicate attribute name
    let dup_attr = r#"
    dataview DupAttrView {
        table = Customer;
        external_name = tbl_cust;
        fields {
            CustomerId => col1 : Numeric(10,0);
            CustomerId => col2 : Numeric(10,0);
        }
    }
    "#;
    let res = DataView::parse("DupAttrView", dup_attr);
    assert!(res.is_err());
    assert!(res.unwrap_err().contains("duplicate attribute mapping"));

    // Duplicate external column name
    let dup_col = r#"
    dataview DupColView {
        table = Customer;
        external_name = tbl_cust;
        fields {
            CustomerId => col_same : Numeric(10,0);
            OtherId => col_same : Numeric(10,0);
        }
    }
    "#;
    let res = DataView::parse("DupColView", dup_col);
    assert!(res.is_err());
    assert!(
        res.unwrap_err()
            .contains("duplicate external column mapping")
    );

    // Empty external table name
    let empty_ext = r#"
    dataview EmptyExtView {
        table = Customer;
        external_name = "";
        fields {
            CustomerId => col1 : Numeric(10,0);
        }
    }
    "#;
    let res = DataView::parse("EmptyExtView", empty_ext);
    assert!(res.is_err());
    assert!(
        res.unwrap_err()
            .contains("external table name cannot be empty")
    );

    // Multiple primary indexes
    let multi_pk = r#"
    dataview MultiPkView {
        table = Customer;
        external_name = tbl_cust;
        fields {
            CustomerId => col1 : Numeric(10,0);
            CustomerCode => col2 : VarChar(20);
        }
        index Pk1 {
            external = pk1;
            primary = true;
            CustomerId;
        }
        index Pk2 {
            external = pk2;
            primary = true;
            CustomerCode;
        }
    }
    "#;
    let res = DataView::parse("MultiPkView", multi_pk);
    assert!(res.is_err());
    assert!(res.unwrap_err().contains("multiple primary indexes"));
}

#[test]
fn test_dataview_validation_fail_closed_index_unknown_attribute() {
    let dsl = r#"
    dataview InvalidIndexView {
        table = Customer;
        external_name = tbl_cust;
        fields {
            CustomerId => cust_id : Numeric(10,0);
        }
        index PkCustomer {
            external = pk_cust;
            primary = true;
            NonExistentAttr;
        }
    }
    "#;
    let res = DataView::parse("InvalidIndexView", dsl);
    assert!(res.is_err());
    let err = res.unwrap_err();
    assert!(err.contains("NonExistentAttr"));
}

#[test]
fn test_dataview_json_roundtrip_and_catalog_admission() {
    let source = r#"
    dataview CustomerView {
        table = Customer;
        external_name = tbl_customers_ext;
        platform = SqlServer;
        datastore = ExternalDb;
        fields {
            CustomerId => cust_id : Numeric(10,0);
            CustomerName => cust_full_name : VarChar(100);
            CustomerStatus => cust_status_code : Character(1);
        }
        index PkCustomer {
            external = pk_tbl_customers;
            primary = true;
            CustomerId;
        }
        index UxCustomerName {
            external = uq_tbl_customers_name;
            unique = true;
            CustomerName;
        }
    }
    "#;
    let dv = DataView::parse("CustomerView", source).expect("DSL parse should succeed");

    // 1. JSON roundtrip
    let json = serde_json::to_string(&dv).expect("serialization should succeed");
    let dv_from_json = DataView::parse("CustomerView", &json).expect("JSON parse should succeed");
    assert_eq!(dv, dv_from_json);

    // 2. Admission of DataView in Catalog with ObjectKind::DataView
    let mut catalog = Catalog::empty();
    let dv_id = ObjectId::new("customerview").unwrap();
    let mut dv_obj = KbObject::new(dv_id.clone(), "CustomerView", ObjectKind::DataView);
    dv_obj.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: json,
    });

    catalog
        .apply(0, &[Edit::Upsert(dv_obj)])
        .expect("catalog admission should succeed");

    let retrieved = catalog
        .get(&dv_id)
        .expect("DataView object must exist in catalog");
    assert_eq!(retrieved.kind, ObjectKind::DataView);
    assert_eq!(retrieved.name, "CUSTOMERVIEW");
    assert_eq!(retrieved.source_parts.len(), 1);
    assert_eq!(retrieved.source_parts[0].name, "STRUCTURE");
}
