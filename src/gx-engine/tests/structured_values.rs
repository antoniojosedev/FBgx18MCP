use gx_engine::{date, datetime, structured, types};

use std::str::FromStr;
use structured::{
    Collection, FieldSchema, MAX_FIELDS_PER_RECORD, MAX_STRUCTURED_DEPTH, Record, RecordSchema,
    StructuredType, StructuredValue,
};

fn schema(name: &str, fields: Vec<FieldSchema>) -> RecordSchema {
    RecordSchema::new(name, fields).expect("valid test schema")
}

fn field(name: &str, field_type: StructuredType) -> FieldSchema {
    FieldSchema::new(name, field_type).expect("valid test field")
}

fn numeric(precision: u8, scale: u8, value: &str) -> StructuredValue {
    StructuredValue::numeric(precision, scale, types::Decimal::from_str(value).unwrap()).unwrap()
}

fn varchar(length: u32, value: &str) -> StructuredValue {
    StructuredValue::varchar(length, value).unwrap()
}

#[test]
fn record_defaults_are_recursive_and_match_declared_schema() {
    let address = schema(
        "Address",
        vec![field("city", StructuredType::Varchar { length: 40 })],
    );
    let root_schema = schema(
        "Customer",
        vec![
            field("active", StructuredType::Boolean {}),
            field(
                "amount",
                StructuredType::Numeric {
                    precision: 4,
                    scale: 0,
                },
            ),
            field("code", StructuredType::Character { length: 12 }),
            field("note", StructuredType::Varchar { length: 80 }),
            field("since", StructuredType::Date {}),
            field("updated", StructuredType::DateTime {}),
            field(
                "tags",
                StructuredType::collection(StructuredType::Varchar { length: 24 }),
            ),
            field("address", StructuredType::record(address)),
        ],
    );
    let record = Record::new(root_schema).unwrap();

    assert_eq!(record.schema().name(), "CUSTOMER");
    assert_eq!(record.values().len(), 8);
    assert!(matches!(
        record.get("ACTIVE"),
        Ok(StructuredValue::Boolean(false))
    ));
    assert!(
        matches!(record.get("amount"), Ok(StructuredValue::Numeric { value, .. }) if value.to_string() == "0")
    );
    assert!(
        matches!(record.get("code"), Ok(StructuredValue::Character { value, .. }) if value.to_string().is_empty())
    );
    assert!(
        matches!(record.get("note"), Ok(StructuredValue::Varchar { value, .. }) if value.to_string().is_empty())
    );
    assert!(matches!(record.get("since"), Ok(StructuredValue::Date(value)) if value.is_empty()));
    assert!(
        matches!(record.get("updated"), Ok(StructuredValue::DateTime(value)) if value.is_empty())
    );
    assert!(
        matches!(record.get("tags"), Ok(StructuredValue::Collection(value)) if value.is_empty())
    );
    assert!(
        matches!(record.get("address"), Ok(StructuredValue::Record(value)) if matches!(value.get("city"), Ok(StructuredValue::Varchar { value, .. }) if value.to_string().is_empty()))
    );
    assert!(record.validate().is_ok());
}

#[test]
fn schema_and_member_validation_are_case_insensitive_and_fail_closed() {
    let duplicate = RecordSchema::new(
        "bad",
        vec![
            field("amount", StructuredType::Boolean {}),
            field("AMOUNT", StructuredType::Boolean {}),
        ],
    );
    assert!(duplicate.unwrap_err().contains("duplicate field"));
    assert!(FieldSchema::new("bad-name", StructuredType::Boolean {}).is_err());
    assert!(StructuredValue::numeric(0, 0, types::Decimal::from_str("1").unwrap()).is_err());
    assert!(matches!(
        numeric(4, 0, "12.8"),
        StructuredValue::Numeric { value, .. } if value.to_string() == "12"
    ));
    assert!(StructuredValue::numeric(4, 0, types::Decimal::from_str("32768").unwrap()).is_err());

    let mut record = Record::new(schema(
        "Order",
        vec![
            field(
                "amount",
                StructuredType::Numeric {
                    precision: 5,
                    scale: 2,
                },
            ),
            field("active", StructuredType::Boolean {}),
        ],
    ))
    .unwrap();
    assert!(record.get("unknown").unwrap_err().contains("no member"));
    assert!(
        record
            .set("unknown", StructuredValue::boolean(true))
            .is_err()
    );
    assert!(record.set("amount", numeric(4, 0, "12")).is_err());
    assert!(record.set("active", numeric(5, 2, "1")).is_err());
    assert!(
        matches!(record.get("AMOUNT"), Ok(StructuredValue::Numeric { value, .. }) if value.to_string() == "0")
    );
    assert!(matches!(
        record.get("active"),
        Ok(StructuredValue::Boolean(false))
    ));
    assert!(record.set("Amount", numeric(5, 2, "12.25")).is_ok());
}

#[test]
fn numeric_construction_normalizes_and_direct_variants_fail_storage_validation() {
    let normalized =
        StructuredValue::numeric(4, 0, types::Decimal::from_str("1.5").unwrap()).unwrap();
    assert!(matches!(
        normalized,
        StructuredValue::Numeric { value, .. } if value.to_string() == "1"
    ));
    assert!(StructuredValue::numeric(1, 0, types::Decimal::from_str("32768").unwrap()).is_err());

    let mut record = Record::new(schema(
        "IntegerAmount",
        vec![field(
            "amount",
            StructuredType::Numeric {
                precision: 4,
                scale: 0,
            },
        )],
    ))
    .unwrap();
    let fractional = StructuredValue::Numeric {
        precision: 4,
        scale: 0,
        value: types::Decimal::from_str("1.5").unwrap(),
    };
    assert!(record.set("amount", fractional.clone()).is_err());
    assert!(fractional.to_json().is_err());
    assert!(
        record
            .set(
                "amount",
                StructuredValue::Numeric {
                    precision: 4,
                    scale: 0,
                    value: types::Decimal::from_str("32768").unwrap(),
                }
            )
            .is_err()
    );
    assert!(matches!(
        record.get("amount"),
        Ok(StructuredValue::Numeric { value, .. }) if value.to_string() == "0"
    ));
}

#[test]
fn collections_enforce_exact_element_schema_and_rollback_invalid_updates() {
    let element_type = StructuredType::Numeric {
        precision: 5,
        scale: 2,
    };
    let mut values = Collection::new(element_type.clone()).unwrap();
    assert_eq!(values.element_type(), &element_type);
    assert!(values.items().is_empty());
    values.push(numeric(5, 2, "1.25")).unwrap();
    assert!(values.push(numeric(5, 0, "2")).is_err());
    assert_eq!(values.len(), 1);
    assert_eq!(values.items().len(), 1);
    assert!(values.set(8, numeric(5, 2, "3")).is_err());
    assert!(
        values
            .update(0, |value| {
                *value = StructuredValue::boolean(true);
                Ok(())
            })
            .is_err()
    );
    assert!(
        matches!(values.get(0), Ok(StructuredValue::Numeric { value, .. }) if value.to_string() == "1.25")
    );
    assert!(
        values
            .set(0, StructuredValue::varchar(8, "wrong type").unwrap())
            .is_err()
    );
    assert_eq!(values.len(), 1);
    assert!(values.validate().is_ok());
}

#[test]
fn nested_record_and_collection_updates_are_checked_transactionally() {
    let line_schema = schema(
        "Line",
        vec![
            field("sku", StructuredType::Varchar { length: 20 }),
            field(
                "quantity",
                StructuredType::Numeric {
                    precision: 4,
                    scale: 0,
                },
            ),
        ],
    );
    let mut lines = Collection::new(StructuredType::record(line_schema.clone())).unwrap();
    let line = Record::new(line_schema).unwrap();
    lines.push(StructuredValue::record(line)).unwrap();
    lines
        .update(0, |value| {
            let StructuredValue::Record(line) = value else {
                return Err("expected line record".into());
            };
            line.set("sku", varchar(20, "A-1"))?;
            line.set("quantity", numeric(4, 0, "3"))
        })
        .unwrap();
    assert!(
        matches!(lines.get(0), Ok(StructuredValue::Record(line)) if matches!(line.get("sku"), Ok(StructuredValue::Varchar { value, .. }) if value.to_string() == "A-1"))
    );

    let mut root = Record::new(schema(
        "Order",
        vec![field(
            "line",
            StructuredType::record(lines_schema_for_test()),
        )],
    ))
    .unwrap();
    assert!(
        root.update("line", |value| {
            *value = StructuredValue::boolean(true);
            Ok(())
        })
        .is_err()
    );
    assert!(matches!(root.get("line"), Ok(StructuredValue::Record(_))));
}

fn lines_schema_for_test() -> RecordSchema {
    schema(
        "Line",
        vec![field("sku", StructuredType::Varchar { length: 20 })],
    )
}

#[test]
fn clone_and_named_deep_clone_make_independent_owned_values() {
    let mut original = Record::new(schema(
        "Item",
        vec![field("name", StructuredType::Varchar { length: 16 })],
    ))
    .unwrap();
    original.set("name", varchar(16, "before")).unwrap();
    let mut via_clone = original.clone();
    let mut via_deep_clone = original.deep_clone();
    original.set("name", varchar(16, "original")).unwrap();
    via_clone.set("name", varchar(16, "clone")).unwrap();
    via_deep_clone
        .set("name", varchar(16, "deep clone"))
        .unwrap();

    assert!(
        matches!(original.get("name"), Ok(StructuredValue::Varchar { value, .. }) if value.to_string() == "original")
    );
    assert!(
        matches!(via_clone.get("name"), Ok(StructuredValue::Varchar { value, .. }) if value.to_string() == "clone")
    );
    assert!(
        matches!(via_deep_clone.get("name"), Ok(StructuredValue::Varchar { value, .. }) if value.to_string() == "deep clone")
    );

    let mut source = Collection::new(StructuredType::Boolean {}).unwrap();
    source.push(StructuredValue::boolean(true)).unwrap();
    let mut copied = source.deep_clone();
    copied.set(0, StructuredValue::boolean(false)).unwrap();
    assert!(matches!(source.get(0), Ok(StructuredValue::Boolean(true))));
}

#[test]
fn schema_and_value_json_are_deterministic_and_schema_round_trips() {
    let schema = schema(
        "profile",
        vec![
            field("name", StructuredType::Varchar { length: 12 }),
            field(
                "score",
                StructuredType::Numeric {
                    precision: 6,
                    scale: 2,
                },
            ),
        ],
    );
    assert_eq!(schema.name(), "PROFILE");
    assert_eq!(schema.fields()[0].name(), "NAME");
    assert_eq!(
        schema.field("score").unwrap().field_type(),
        &StructuredType::Numeric {
            precision: 6,
            scale: 2
        }
    );
    assert!(schema.field("missing").is_none());
    assert!(
        StructuredType::Numeric {
            precision: 6,
            scale: 2
        }
        .validate()
        .is_ok()
    );
    let schema_json = schema.to_json().unwrap();
    assert_eq!(schema_json, schema.to_json().unwrap());
    let parsed = RecordSchema::from_json(&schema_json).unwrap();
    assert_eq!(parsed, schema);

    let mut record = Record::new(schema).unwrap();
    record.set("name", varchar(12, "Ada")).unwrap();
    record.set("score", numeric(6, 2, "9.50")).unwrap();
    let first = record.to_json().unwrap();
    assert_eq!(first, record.to_json().unwrap());
    let document: serde_json::Value = serde_json::from_str(&first).unwrap();
    let names: Vec<_> = document["fields"]
        .as_array()
        .unwrap()
        .iter()
        .map(|field| field["name"].as_str().unwrap())
        .collect();
    assert_eq!(names, ["NAME", "SCORE"]);
    assert_eq!(document["fields"][1]["value"]["value"], "9.5");

    let mut collection = Collection::new(StructuredType::DateTime {}).unwrap();
    collection
        .push(StructuredValue::date_time(
            datetime::DateTime::from_parts([2024, 2, 29, 23, 59, 59], None).unwrap(),
        ))
        .unwrap();
    assert_eq!(collection.to_json().unwrap(), collection.to_json().unwrap());
    let date_value = StructuredValue::date(date::Date::ymd(2024, 2, 29).unwrap());
    assert!(date_value.to_json().unwrap().contains("2024"));
    assert!(StructuredValue::character(12, "hello").is_ok());
    let wrapped = StructuredValue::collection(Collection::new(StructuredType::Boolean {}).unwrap());
    assert!(wrapped.to_json().unwrap().contains("collection"));
    assert!(matches!(
        date_value.deep_clone(),
        StructuredValue::Date(value) if value == date::Date::ymd(2024, 2, 29).unwrap()
    ));
}

#[test]
fn malformed_or_excessive_schemas_are_rejected() {
    let fields = (0..=MAX_FIELDS_PER_RECORD)
        .map(|index| field(&format!("F{index}"), StructuredType::Boolean {}))
        .collect();
    assert!(
        RecordSchema::new("TooMany", fields)
            .unwrap_err()
            .contains("exceeds")
    );
    assert!(
        RecordSchema::from_json(r#"{"name":"x","fields":[],"extra":true}"#)
            .unwrap_err()
            .contains("unknown field")
    );
    assert!(
        RecordSchema::from_json("x".repeat(1024 * 1024 + 1).as_str())
            .unwrap_err()
            .contains("byte limit")
    );

    let mut nested = schema("Leaf", vec![]);
    let mut rejected_deep_schema = false;
    for level in 0..=MAX_STRUCTURED_DEPTH {
        let nested_field = FieldSchema::new("inner", StructuredType::record(nested));
        match nested_field {
            Err(error) => {
                assert!(error.contains("depth"));
                rejected_deep_schema = true;
                break;
            }
            Ok(field) => {
                nested = RecordSchema::new(format!("Layer{level}"), vec![field]).unwrap();
            }
        }
    }
    assert!(rejected_deep_schema);
}

#[test]
fn aggregate_text_limit_rolls_back_failed_member_write() {
    let fields = (0..17)
        .map(|index| {
            field(
                &format!("Text{index}"),
                StructuredType::Varchar { length: 65_535 },
            )
        })
        .collect();
    let schema = schema("TextBatch", fields);
    let large = "x".repeat(65_535);
    let mut record = Record::new(schema).unwrap();
    for index in 0..16 {
        record
            .set(&format!("Text{index}"), varchar(65_535, &large))
            .unwrap();
    }
    assert!(record.set("Text16", varchar(65_535, &large)).is_err());
    assert!(
        matches!(record.get("Text16"), Ok(StructuredValue::Varchar { value, .. }) if value.to_string().is_empty())
    );
    assert!(record.validate().is_ok());
}
