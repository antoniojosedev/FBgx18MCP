use gx_engine::{
    date::Date,
    datetime::DateTime,
    record_runtime::{Collection, Field, Record},
    text::Text,
    types::Decimal,
};
use std::{collections::BTreeMap, str::FromStr};

fn numeric(value: &str) -> Field {
    Field::Numeric(Decimal::from_str(value).unwrap())
}

fn number(record: &Record, name: &str) -> i64 {
    match record.get(name).unwrap() {
        Field::Numeric(value) => value.to_i64().unwrap(),
        _ => panic!("expected Numeric field `{name}`"),
    }
}

fn child(schema: &'static str, value: &str) -> Record {
    Record::new(schema, BTreeMap::from([("Value", numeric(value))])).unwrap()
}

#[test]
fn record_assignment_aliases_and_clone_copies_only_the_outer_fields() {
    let first_child = child("Child", "0");
    let root = Record::new(
        "Root",
        BTreeMap::from([
            ("Child", Field::Record(first_child)),
            ("Value", numeric("0")),
        ]),
    )
    .unwrap();

    // Assignment aliases the record map, including direct field replacement.
    let alias = root.clone();
    alias.set("Value", numeric("9")).unwrap();
    assert_eq!(number(&root, "Value"), 9);
    assert_eq!(number(&alias, "Value"), 9);
    let Field::Record(alias_child) = alias.get("Child").unwrap() else {
        panic!("expected nested record");
    };
    alias_child.set("Value", numeric("9")).unwrap();
    assert_eq!(number(&root.get_record("Child"), "Value"), 9);
    assert_eq!(number(&alias.get_record("Child"), "Value"), 9);

    // Native Clone() copies the outer record but preserves nested aliases.
    let cloned = root.shallow_clone();
    cloned.set("Value", numeric("12")).unwrap();
    assert_eq!(number(&root, "Value"), 9);
    assert_eq!(number(&cloned, "Value"), 12);
    let Field::Record(cloned_child) = cloned.get("Child").unwrap() else {
        panic!("expected nested record");
    };
    cloned_child.set("Value", numeric("11")).unwrap();
    assert_eq!(number(&root.get_record("Child"), "Value"), 11);
    assert_eq!(number(&cloned.get_record("Child"), "Value"), 11);

    // The outer maps are distinct: replacing the cloned field leaves the source
    // record's field reference unchanged.
    cloned
        .set("Child", Field::Record(child("Child", "20")))
        .unwrap();
    assert_eq!(number(&root.get_record("Child"), "Value"), 11);
    assert_eq!(number(&cloned.get_record("Child"), "Value"), 20);
}

#[test]
fn collection_assignment_and_clone_match_native_shallow_aliasing() {
    let first_row = child("Row", "7");
    let rows = Collection::new("Row");
    assert_eq!(rows.len(), 0);
    rows.add(first_row).unwrap();
    assert_eq!(rows.len(), 1);
    assert_eq!(number(&rows.item(1).unwrap(), "Value"), 7);

    let first = Record::new("Root", BTreeMap::from([("Rows", Field::Collection(rows))])).unwrap();
    let second = first.shallow_clone();
    let first_rows = first.get_collection("Rows");
    let second_rows = second.get_collection("Rows");

    // A record Clone aliases its nested collection, including list edits.
    second_rows
        .item(1)
        .unwrap()
        .set("Value", numeric("9"))
        .unwrap();
    assert_eq!(number(&first_rows.item(1).unwrap(), "Value"), 9);
    second_rows.add(child("Row", "10")).unwrap();
    assert_eq!(first_rows.len(), 2);
    assert_eq!(second_rows.len(), 2);

    // Collection.Clone separates only the list; the contained record references
    // remain shared, while subsequent Add operations are list-local.
    let cloned_rows = first_rows.shallow_clone();
    second
        .set("Rows", Field::Collection(cloned_rows.clone()))
        .unwrap();
    cloned_rows
        .item(1)
        .unwrap()
        .set("Value", numeric("13"))
        .unwrap();
    cloned_rows.add(child("Row", "14")).unwrap();
    assert_eq!(number(&first_rows.item(1).unwrap(), "Value"), 13);
    assert_eq!(number(&cloned_rows.item(1).unwrap(), "Value"), 13);
    assert_eq!(first_rows.len(), 2);
    assert_eq!(cloned_rows.len(), 3);
    assert_eq!(first.get_collection("Rows").len(), 2);
    assert_eq!(second.get_collection("Rows").len(), 3);
}

#[test]
fn fields_validate_names_tags_and_nested_schemas() {
    let rows = Collection::new("Row");
    let mut fields = BTreeMap::new();
    fields.insert("Numeric", numeric("4"));
    fields.insert("Text", Field::Text(Text::new("text").unwrap()));
    fields.insert("Boolean", Field::Boolean(true));
    fields.insert("Date", Field::Date(Date::empty()));
    fields.insert("DateTime", Field::DateTime(DateTime::empty()));
    fields.insert("Child", Field::Record(child("Child", "5")));
    fields.insert("Rows", Field::Collection(rows.clone()));
    let record = Record::new("Root", fields).unwrap();

    assert!(record.get("Missing").is_err());
    assert!(record.set("Missing", Field::Boolean(false)).is_err());
    assert!(
        record
            .set("Numeric", Field::Text(Text::new("wrong").unwrap()))
            .is_err()
    );
    assert_eq!(number(&record, "Numeric"), 4);
    assert!(
        record
            .set("Child", Field::Record(child("OtherChild", "5")))
            .is_err()
    );
    assert!(
        record
            .set("Rows", Field::Collection(Collection::new("OtherRow")))
            .is_err()
    );
    assert_eq!(record.get("Child").unwrap().record_schema(), "Child");
    assert_eq!(record.get("Rows").unwrap().collection_item_schema(), "Row");

    // Same-schema values of each supported scalar kind can be replaced.
    record
        .set("Text", Field::Text(Text::new("next").unwrap()))
        .unwrap();
    record.set("Boolean", Field::Boolean(false)).unwrap();
    record.set("Date", Field::Date(Date::empty())).unwrap();
    record
        .set("DateTime", Field::DateTime(DateTime::empty()))
        .unwrap();
    record
        .set("Child", Field::Record(child("Child", "6")))
        .unwrap();
}

#[test]
fn record_field_count_and_collection_indices_are_bounded() {
    assert!(Record::new("Empty", BTreeMap::new()).is_err());

    let at_limit = (0..1024)
        .map(|index| {
            let name: &'static str = Box::leak(format!("Allowed{index}").into_boxed_str());
            (name, Field::Boolean(false))
        })
        .collect();
    assert!(Record::new("AtLimit", at_limit).is_ok());

    let too_many = (0..=1024)
        .map(|index| {
            let name: &'static str = Box::leak(format!("Field{index}").into_boxed_str());
            (name, Field::Boolean(false))
        })
        .collect();
    assert!(Record::new("TooMany", too_many).is_err());

    let empty = Collection::new("Row");
    assert!(empty.is_empty());
    assert!(empty.item(0).is_err());
    assert!(empty.item(-1).is_err());
    assert!(empty.item(1).is_err());
    empty.add(child("Row", "1")).unwrap();
    assert_eq!(number(&empty.item(1).unwrap(), "Value"), 1);
    assert!(empty.item(2).is_err());
}

#[test]
fn collection_rejects_wrong_schema_and_enforces_item_limit() {
    let rows = Collection::new("Row");
    assert!(rows.add(child("OtherRow", "1")).is_err());
    let row = child("Row", "1");
    for _ in 0..100_000 {
        rows.add(row.clone()).unwrap();
    }
    assert_eq!(rows.len(), 100_000);
    assert!(rows.add(row).is_err());
}

#[test]
fn returned_values_can_be_updated_without_holding_runtime_borrows() {
    let record = Record::new(
        "Root",
        BTreeMap::from([
            ("Child", Field::Record(child("Child", "1"))),
            ("Rows", Field::Collection(Collection::new("Row"))),
        ]),
    )
    .unwrap();

    let Field::Record(nested) = record.get("Child").unwrap() else {
        panic!("expected nested record");
    };
    nested.set("Value", numeric("2")).unwrap();
    record.set("Child", Field::Record(nested.clone())).unwrap();
    assert_eq!(number(&record.get_record("Child"), "Value"), 2);

    let Field::Collection(rows) = record.get("Rows").unwrap() else {
        panic!("expected collection");
    };
    rows.add(child("Row", "3")).unwrap();
    record.set("Rows", Field::Collection(rows.clone())).unwrap();
    assert_eq!(record.get_collection("Rows").len(), 1);
}

trait FieldAccess {
    fn get_record(&self, name: &str) -> Record;
    fn get_collection(&self, name: &str) -> Collection;
}

impl FieldAccess for Record {
    fn get_record(&self, name: &str) -> Record {
        match self.get(name).unwrap() {
            Field::Record(value) => value,
            _ => panic!("expected record field `{name}`"),
        }
    }

    fn get_collection(&self, name: &str) -> Collection {
        match self.get(name).unwrap() {
            Field::Collection(value) => value,
            _ => panic!("expected collection field `{name}`"),
        }
    }
}

trait FieldSchema {
    fn record_schema(&self) -> &'static str;
    fn collection_item_schema(&self) -> &'static str;
}

impl FieldSchema for Field {
    fn record_schema(&self) -> &'static str {
        match self {
            Field::Record(record) => record.schema(),
            _ => panic!("expected record field"),
        }
    }

    fn collection_item_schema(&self) -> &'static str {
        match self {
            Field::Collection(collection) => collection.item_schema(),
            _ => panic!("expected collection field"),
        }
    }
}
