use gx_engine::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    dataprovider::DataProvider,
};
use std::collections::HashMap;

#[test]
fn test_basic_scalar_data_provider_to_json() {
    let source = r#"
Customer
{
    CustomerId = 101
    CustomerName = 'Acme Corporation'
    CustomerActive = true
    DiscountRate = 12.5
}
"#;
    let dp = DataProvider::parse("CustomerDP", source).expect("parse should succeed");
    assert_eq!(dp.name, "CustomerDP");
    assert_eq!(dp.root_group.name, "Customer");
    assert_eq!(dp.root_group.items.len(), 4);

    let params = HashMap::new();
    let json = dp
        .evaluate_to_json(&params)
        .expect("evaluation should succeed");

    assert_eq!(json["CustomerId"], 101);
    assert_eq!(json["CustomerName"], "Acme Corporation");
    assert_eq!(json["CustomerActive"], true);
    assert_eq!(json["DiscountRate"], 12.5);
}

#[test]
fn test_nested_group_hierarchy() {
    let source = r#"
Customer
{
    CustomerId = 101
    CustomerName = 'Acme Corp'
    Address
    {
        Street = '123 Main St'
        City = 'Springfield'
        PostalCode = 12345
    }
    Contact
    {
        Email = 'info@acme.com'
        Phone = '555-0199'
    }
}
"#;
    let dp = DataProvider::parse("CustomerHierarchyDP", source).expect("parse should succeed");
    let params = HashMap::new();
    let json = dp
        .evaluate_to_json(&params)
        .expect("evaluation should succeed");

    assert_eq!(json["CustomerId"], 101);
    assert_eq!(json["CustomerName"], "Acme Corp");
    assert_eq!(json["Address"]["Street"], "123 Main St");
    assert_eq!(json["Address"]["City"], "Springfield");
    assert_eq!(json["Address"]["PostalCode"], 12345);
    assert_eq!(json["Contact"]["Email"], "info@acme.com");
    assert_eq!(json["Contact"]["Phone"], "555-0199");
}

#[test]
fn test_collection_group_generation() {
    let source = r#"
Customers [Collection]
{
    Customer
    {
        Id = 1
        Name = 'Alice'
    }
    Customer
    {
        Id = 2
        Name = 'Bob'
    }
    Customer
    {
        Id = 3
        Name = 'Charlie'
    }
}
"#;
    let dp = DataProvider::parse("CustomersCollectionDP", source).expect("parse should succeed");
    assert!(dp.root_group.is_collection);
    let params = HashMap::new();
    let json = dp
        .evaluate_to_json(&params)
        .expect("evaluation should succeed");

    assert!(json.is_array());
    let arr = json.as_array().expect("must be array");
    assert_eq!(arr.len(), 3);
    assert_eq!(arr[0]["Id"], 1);
    assert_eq!(arr[0]["Name"], "Alice");
    assert_eq!(arr[1]["Id"], 2);
    assert_eq!(arr[1]["Name"], "Bob");
    assert_eq!(arr[2]["Id"], 3);
    assert_eq!(arr[2]["Name"], "Charlie");
}

#[test]
fn test_parameter_substitution_in_assignments() {
    let source = r#"
parm(in:&ClientId, in:&Status, in:&IsSpecial);
Customer
{
    CustomerId = &ClientId
    CustomerStatus = &Status
    SpecialFlag = &IsSpecial
}
"#;
    let dp = DataProvider::parse("CustomerParamDP", source).expect("parse should succeed");
    let mut params = HashMap::new();
    params.insert("&ClientId".to_string(), serde_json::json!(42));
    params.insert("Status".to_string(), serde_json::json!("Active"));
    params.insert("&IsSpecial".to_string(), serde_json::json!(true));

    let json = dp
        .evaluate_to_json(&params)
        .expect("evaluation should succeed");
    assert_eq!(json["CustomerId"], 42);
    assert_eq!(json["CustomerStatus"], "Active");
    assert_eq!(json["SpecialFlag"], true);
}

#[test]
fn test_parm_rule_parsing() {
    let source = r#"
parm(in:&Id, in:&Type);
Item
{
    ItemId = &Id
    ItemType = &Type
}
"#;
    let dp = DataProvider::parse("ItemDP", source).expect("parse should succeed");
    assert_eq!(dp.parameters, vec!["&Id", "&Type"]);
    assert_eq!(dp.output_sdt, None);

    // Also test with output SDT specification
    let source_with_out = r#"
parm(in:&ClientId, out:&CustomerData);
Customer
{
    Id = &ClientId
}
"#;
    let dp_out =
        DataProvider::parse("CustomerOutDP", source_with_out).expect("parse should succeed");
    assert_eq!(dp_out.parameters, vec!["&ClientId"]);
    assert_eq!(dp_out.output_sdt, Some("CustomerData".to_string()));
}

#[test]
fn test_comments_stripping_and_string_literals() {
    let source = r#"
// Top-level line comment
/* Multi-line
   block comment
   with semicolons; and brackets [] {} */
Customer [Count = 5] // inline comment
{
    // Property assignment
    Url = 'https://example.com/api?id=123;filter=all//not-a-comment/*not-block*/'
    Note = "Double quote with ; and // inside"
    /* Block comment before assignment */
    Escaped = 'It''s a GeneXus string; with semicolon'
}
"#;
    let dp = DataProvider::parse("CommentsDP", source).expect("parse should succeed");
    assert_eq!(dp.root_group.properties.len(), 1);
    assert_eq!(dp.root_group.properties[0].name, "Count");
    assert_eq!(dp.root_group.properties[0].value, "5");

    let params = HashMap::new();
    let json = dp
        .evaluate_to_json(&params)
        .expect("evaluation should succeed");

    assert_eq!(
        json["Url"],
        "https://example.com/api?id=123;filter=all//not-a-comment/*not-block*/"
    );
    assert_eq!(json["Note"], "Double quote with ; and // inside");
    assert_eq!(json["Escaped"], "It's a GeneXus string; with semicolon");
}

#[test]
fn test_sdt_schema_validation_success() {
    let mut catalog = Catalog::empty();
    let mut sdt_obj = KbObject::new(
        ObjectId::new("customersdt").unwrap(),
        "CustomerSDT",
        ObjectKind::StructuredDataType,
    );
    sdt_obj.source_parts.push(SourcePart {
        name: "FIELDS".into(),
        source: r#"{"CustomerId":{"kind":"numeric","precision":9,"scale":0},"CustomerName":{"kind":"character","length":50}}"#.into(),
    });
    catalog.apply(0, &[Edit::Upsert(sdt_obj)]).unwrap();

    let source = r#"
Customer
{
    CustomerId = 1001
    CustomerName = 'Valid Customer'
}
"#;
    let dp = DataProvider::parse("ValidateSuccessDP", source).expect("parse");
    let result = dp.validate_against_sdt(&catalog, "CustomerSDT");
    assert!(result.is_ok());
}

#[test]
fn test_sdt_schema_validation_unknown_field_fail_closed() {
    let mut catalog = Catalog::empty();
    let mut sdt_obj = KbObject::new(
        ObjectId::new("customersdt").unwrap(),
        "CustomerSDT",
        ObjectKind::StructuredDataType,
    );
    sdt_obj.source_parts.push(SourcePart {
        name: "FIELDS".into(),
        source: r#"{"CustomerId":{"kind":"numeric","precision":9,"scale":0},"CustomerName":{"kind":"character","length":50}}"#.into(),
    });
    catalog.apply(0, &[Edit::Upsert(sdt_obj)]).unwrap();

    let source = r#"
Customer
{
    CustomerId = 1001
    CustomerName = 'Valid Customer'
    UnknownField = 'This should fail'
}
"#;
    let dp = DataProvider::parse("ValidateFailDP", source).expect("parse");
    let result = dp.validate_against_sdt(&catalog, "CustomerSDT");
    assert!(result.is_err());
    let err = result.unwrap_err();
    assert!(
        err.contains("UnknownField"),
        "Error message should mention unknown field: {err}"
    );
}

#[test]
fn test_data_provider_json_serialization() {
    let source = r#"
parm(in:&Id, out:&CustomerSDT);
Customer [Count = 10, Paging = 20]
{
    CustomerId = &Id
    CustomerName = 'Acme'
    Address
    {
        City = 'Boston'
    }
}
"#;
    let dp = DataProvider::parse("SerializationDP", source).expect("parse should succeed");
    let serialized = serde_json::to_string(&dp).expect("serialization should succeed");
    let deserialized: DataProvider =
        serde_json::from_str(&serialized).expect("deserialization should succeed");

    assert_eq!(dp, deserialized);
    assert_eq!(deserialized.name, "SerializationDP");
    assert_eq!(deserialized.parameters, vec!["&Id"]);
    assert_eq!(deserialized.output_sdt, Some("CustomerSDT".to_string()));
    assert_eq!(deserialized.root_group.properties.len(), 2);
}

#[test]
fn test_catalog_integration_with_data_provider_object() {
    let mut catalog = Catalog::empty();
    let sdt_id = ObjectId::new("productsdt").unwrap();
    let dp_id = ObjectId::new("product_dp").unwrap();

    let mut sdt_obj = KbObject::new(sdt_id.clone(), "ProductSDT", ObjectKind::StructuredDataType);
    sdt_obj.source_parts.push(SourcePart {
        name: "FIELDS".into(),
        source: r#"{"ProductId":{"kind":"numeric","precision":6,"scale":0},"Price":{"kind":"numeric","precision":10,"scale":2}}"#.into(),
    });

    let dp_source = r#"
parm(in:&BasePrice, out:&ProductSDT);
Product
{
    ProductId = 501
    Price = &BasePrice
}
"#;
    let mut dp_obj = KbObject::new(dp_id.clone(), "ProductDP", ObjectKind::DataProvider);
    dp_obj.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: dp_source.into(),
    });
    dp_obj.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: sdt_id.clone(),
    });

    catalog
        .apply(0, &[Edit::Upsert(sdt_obj), Edit::Upsert(dp_obj)])
        .expect("catalog apply should succeed");

    // Retrieve from catalog
    let retrieved = catalog
        .get(&dp_id)
        .expect("DataProvider object exists in catalog");
    assert_eq!(retrieved.kind, ObjectKind::DataProvider);
    assert_eq!(retrieved.name, "PRODUCTDP");

    let source_part = retrieved
        .source_parts
        .iter()
        .find(|p| p.name == "SOURCE")
        .expect("SOURCE part exists");

    let dp = DataProvider::parse(&retrieved.name, &source_part.source).expect("parse DP");
    assert_eq!(dp.output_sdt, Some("ProductSDT".to_string()));

    // Validate against SDT in catalog
    dp.validate_against_sdt(&catalog, "ProductSDT")
        .expect("validates against catalog SDT");

    // Evaluate
    let mut params = HashMap::new();
    params.insert("&BasePrice".to_string(), serde_json::json!(29.99));
    let json = dp.evaluate_to_json(&params).expect("evaluate");
    assert_eq!(json["ProductId"], 501);
    assert_eq!(json["Price"], 29.99);

    // Verify catalog serialization round-trip
    let cat_json = catalog.to_json().expect("catalog to_json");
    let loaded = Catalog::from_json(&cat_json).expect("catalog from_json");
    assert_eq!(catalog.revision(), loaded.revision());
}
