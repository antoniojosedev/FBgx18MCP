use gx_engine::{
    build,
    date::Date,
    datetime::DateTime,
    model::Project,
    record_runtime::{Collection, Field, Record},
    text::Text,
    types::Decimal,
};
use serde_json::json;
use std::{
    collections::BTreeMap,
    fs,
    path::Path,
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

fn make_sample_record(empty: bool) -> Record {
    let mut fields = BTreeMap::new();
    if empty {
        fields.insert("Amount", Field::Numeric("0".parse::<Decimal>().unwrap()));
        fields.insert("Label", Field::Text(Text::new("").unwrap()));
        fields.insert("Fixed", Field::Text(Text::new("").unwrap()));
        fields.insert("Flag", Field::Boolean(false));
        fields.insert("Day", Field::Date(Date::empty()));
        fields.insert("Stamp", Field::DateTime(DateTime::empty()));
    } else {
        fields.insert("Amount", Field::Numeric("42".parse::<Decimal>().unwrap()));
        fields.insert("Label", Field::Text(Text::new("Hello \"world\"").unwrap()));
        fields.insert("Fixed", Field::Text(Text::new("ABC").unwrap()));
        fields.insert("Flag", Field::Boolean(true));
        fields.insert("Day", Field::Date(Date::ymd(2026, 3, 29).unwrap()));
        fields.insert(
            "Stamp",
            Field::DateTime(DateTime::from_parts([2026, 3, 29, 14, 30, 45], None).unwrap()),
        );
    }
    Record::new("Sample", fields).expect("valid sample record")
}

#[test]
fn sdt_json_matches_native_runtime_oracle() {
    let fixture_path = Path::new("tests/fixtures/sdt_json_native.tsv");
    let content = fs::read_to_string(fixture_path)
        .or_else(|_| {
            fs::read_to_string(Path::new(
                "src/gx-engine/tests/fixtures/sdt_json_native.tsv",
            ))
        })
        .expect("sdt_json_native.tsv exists");

    let sample_empty = make_sample_record(true);
    let sample_filled = make_sample_record(false);
    let sample_round = make_sample_record(true);
    let sample_partial = make_sample_record(true);

    let parent = {
        let mut p_fields = BTreeMap::new();
        p_fields.insert("Code", Field::Text(Text::new("P01").unwrap()));
        let mut child_fields = BTreeMap::new();
        child_fields.insert("Amount", Field::Numeric("10".parse::<Decimal>().unwrap()));
        child_fields.insert("Label", Field::Text(Text::new("Sub").unwrap()));
        child_fields.insert("Fixed", Field::Text(Text::new("").unwrap()));
        child_fields.insert("Flag", Field::Boolean(false));
        child_fields.insert("Day", Field::Date(Date::empty()));
        child_fields.insert("Stamp", Field::DateTime(DateTime::empty()));
        let child = Record::new("Sample", child_fields).unwrap();
        p_fields.insert("Child", Field::Record(child.clone()));

        let col = Collection::with_prototype("Sample", child.empty_clone());
        let mut row_fields = BTreeMap::new();
        row_fields.insert("Amount", Field::Numeric("100".parse::<Decimal>().unwrap()));
        row_fields.insert("Label", Field::Text(Text::new("Row1").unwrap()));
        row_fields.insert("Fixed", Field::Text(Text::new("").unwrap()));
        row_fields.insert("Flag", Field::Boolean(false));
        row_fields.insert("Day", Field::Date(Date::empty()));
        row_fields.insert("Stamp", Field::DateTime(DateTime::empty()));
        let row = Record::new("Sample", row_fields).unwrap();
        col.add(row).unwrap();
        p_fields.insert("Items", Field::Collection(col));

        Record::new("Parent", p_fields).unwrap()
    };

    let col_empty = Collection::with_prototype("Sample", sample_empty.clone());
    let col_filled = Collection::with_prototype("Sample", sample_empty.clone());
    {
        let mut r1_fields = BTreeMap::new();
        r1_fields.insert("Amount", Field::Numeric("1".parse::<Decimal>().unwrap()));
        r1_fields.insert("Label", Field::Text(Text::new("One").unwrap()));
        r1_fields.insert("Fixed", Field::Text(Text::new("").unwrap()));
        r1_fields.insert("Flag", Field::Boolean(false));
        r1_fields.insert("Day", Field::Date(Date::empty()));
        r1_fields.insert("Stamp", Field::DateTime(DateTime::empty()));
        col_filled
            .add(Record::new("Sample", r1_fields).unwrap())
            .unwrap();

        let mut r2_fields = BTreeMap::new();
        r2_fields.insert("Amount", Field::Numeric("2".parse::<Decimal>().unwrap()));
        r2_fields.insert("Label", Field::Text(Text::new("Two").unwrap()));
        r2_fields.insert("Fixed", Field::Text(Text::new("").unwrap()));
        r2_fields.insert("Flag", Field::Boolean(false));
        r2_fields.insert("Day", Field::Date(Date::empty()));
        r2_fields.insert("Stamp", Field::DateTime(DateTime::empty()));
        col_filled
            .add(Record::new("Sample", r2_fields).unwrap())
            .unwrap();
    }

    let parent_round = parent.empty_clone();
    let col_round = Collection::with_prototype("Sample", sample_empty.clone());

    for line in content.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() || trimmed.starts_with('#') {
            continue;
        }

        let parts: Vec<&str> = trimmed.split('=').collect();
        assert_eq!(parts.len(), 2, "line must be key=[expected]: {trimmed}");
        let key = parts[0];
        let expected = parts[1]
            .strip_prefix('[')
            .and_then(|s| s.strip_suffix(']'))
            .unwrap_or(parts[1]);

        match key {
            "sdt_empty_to_json|" => {
                let actual_val: serde_json::Value =
                    serde_json::from_str(&sample_empty.to_json()).unwrap();
                let expected_val: serde_json::Value = serde_json::from_str(expected).unwrap();
                assert_eq!(actual_val, expected_val);
            }
            "sdt_to_json|" => {
                let json_str = sample_filled.to_json();
                let actual_val: serde_json::Value = serde_json::from_str(&json_str).unwrap();
                let expected_val: serde_json::Value = serde_json::from_str(expected).unwrap();
                assert_eq!(actual_val, expected_val);
                // Also trigger from_json for subsequent roundtrip checks
                let ok = sample_round.from_json(&json_str);
                assert!(ok);
            }
            "sdt_from_json_ok|" => {
                assert_eq!("True", expected);
            }
            "sdt_round_amount|" => {
                let val = match sample_round.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                };
                assert_eq!(val, expected);
            }
            "sdt_round_label|" => {
                let val = match sample_round.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_owned(),
                    _ => panic!("Label must be text"),
                };
                assert_eq!(val, expected);
            }
            "sdt_round_fixed|" => {
                let val = match sample_round.get("Fixed").unwrap() {
                    Field::Text(t) => t.as_str().to_owned(),
                    _ => panic!("Fixed must be text"),
                };
                assert_eq!(val, expected);
            }
            "sdt_round_flag|" => {
                let val = match sample_round.get("Flag").unwrap() {
                    Field::Boolean(b) => {
                        if b {
                            "True"
                        } else {
                            "False"
                        }
                    }
                    _ => panic!("Flag must be boolean"),
                };
                assert_eq!(val, expected);
            }
            "sdt_round_day|" => {
                let val = match sample_round.get("Day").unwrap() {
                    Field::Date(d) => format!("{:04}-{:02}-{:02}", d.year(), d.month(), d.day()),
                    _ => panic!("Day must be date"),
                };
                assert_eq!(val, expected);
            }
            "sdt_round_stamp|" => {
                let val = match sample_round.get("Stamp").unwrap() {
                    Field::DateTime(dt) => format!(
                        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}",
                        dt.year(),
                        dt.month(),
                        dt.day(),
                        dt.hour(),
                        dt.minute(),
                        dt.second()
                    ),
                    _ => panic!("Stamp must be datetime"),
                };
                assert_eq!(val, expected);
            }
            "sdt_partial_ok|" => {
                // Initialize partial record with amount 100
                sample_partial
                    .set("Amount", Field::Numeric("100".parse::<Decimal>().unwrap()))
                    .unwrap();
                let partial_json = r#"{"label":"Updated","FLAG":true}"#;
                let ok = sample_partial.from_json(partial_json);
                assert!(ok);
            }
            "sdt_partial_amount_preserved|" => {
                let val = match sample_partial.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                };
                assert_eq!(val, expected);
            }
            "sdt_partial_label_updated|" => {
                let val = match sample_partial.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_owned(),
                    _ => panic!("Label must be text"),
                };
                assert_eq!(val, expected);
            }
            "sdt_partial_flag_updated|" => {
                let val = match sample_partial.get("Flag").unwrap() {
                    Field::Boolean(b) => {
                        if b {
                            "True"
                        } else {
                            "False"
                        }
                    }
                    _ => panic!("Flag must be boolean"),
                };
                assert_eq!(val, expected);
            }
            "sdt_from_json_empty|" => {
                let ok = sample_empty.from_json("");
                assert_eq!(if ok { "True" } else { "False" }, expected);
            }
            "sdt_from_json_malformed|" => {
                let ok = sample_empty.from_json("{broken");
                assert_eq!(if ok { "True" } else { "False" }, expected);
            }
            "sdt_from_json_not_obj|" => {
                let ok = sample_empty.from_json("[1, 2, 3]");
                assert_eq!(if ok { "True" } else { "False" }, expected);
            }
            "parent_to_json|" => {
                let json_str = parent.to_json();
                let actual_val: serde_json::Value = serde_json::from_str(&json_str).unwrap();
                let expected_val: serde_json::Value = serde_json::from_str(expected).unwrap();
                assert_eq!(actual_val, expected_val);
                let ok = parent_round.from_json(&json_str);
                assert!(ok);
            }
            "parent_from_json_ok|" => {
                assert_eq!("True", expected);
            }
            "parent_round_code|" => {
                let val = match parent_round.get("Code").unwrap() {
                    Field::Text(t) => t.as_str().to_owned(),
                    _ => panic!("Code must be text"),
                };
                assert_eq!(val, expected);
            }
            "parent_round_child_amount|" => {
                let child = match parent_round.get("Child").unwrap() {
                    Field::Record(r) => r,
                    _ => panic!("Child must be record"),
                };
                let val = match child.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                };
                assert_eq!(val, expected);
            }
            "parent_round_child_label|" => {
                let child = match parent_round.get("Child").unwrap() {
                    Field::Record(r) => r,
                    _ => panic!("Child must be record"),
                };
                let val = match child.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_owned(),
                    _ => panic!("Label must be text"),
                };
                assert_eq!(val, expected);
            }
            "parent_round_items_count|" => {
                let items = match parent_round.get("Items").unwrap() {
                    Field::Collection(c) => c,
                    _ => panic!("Items must be collection"),
                };
                assert_eq!(items.len().to_string(), expected);
            }
            "parent_round_item1_amount|" => {
                let items = match parent_round.get("Items").unwrap() {
                    Field::Collection(c) => c,
                    _ => panic!("Items must be collection"),
                };
                let item1 = items.item(1).unwrap();
                let val = match item1.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                };
                assert_eq!(val, expected);
            }
            "parent_round_item1_label|" => {
                let items = match parent_round.get("Items").unwrap() {
                    Field::Collection(c) => c,
                    _ => panic!("Items must be collection"),
                };
                let item1 = items.item(1).unwrap();
                let val = match item1.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_owned(),
                    _ => panic!("Label must be text"),
                };
                assert_eq!(val, expected);
            }
            "col_empty_to_json|" => {
                assert_eq!(col_empty.to_json(), expected);
            }
            "col_to_json|" => {
                let json_str = col_filled.to_json();
                let actual_val: serde_json::Value = serde_json::from_str(&json_str).unwrap();
                let expected_val: serde_json::Value = serde_json::from_str(expected).unwrap();
                assert_eq!(actual_val, expected_val);
                let ok = col_round.from_json(&json_str);
                assert!(ok);
            }
            "col_from_json_ok|" => {
                assert_eq!("True", expected);
            }
            "col_round_count|" => {
                assert_eq!(col_round.len().to_string(), expected);
            }
            "col_round_item1_amount|" => {
                let item1 = col_round.item(1).unwrap();
                let val = match item1.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                };
                assert_eq!(val, expected);
            }
            "col_round_item1_label|" => {
                let item1 = col_round.item(1).unwrap();
                let val = match item1.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_owned(),
                    _ => panic!("Label must be text"),
                };
                assert_eq!(val, expected);
            }
            "col_round_item2_amount|" => {
                let item2 = col_round.item(2).unwrap();
                let val = match item2.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                };
                assert_eq!(val, expected);
            }
            "col_round_item2_label|" => {
                let item2 = col_round.item(2).unwrap();
                let val = match item2.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_owned(),
                    _ => panic!("Label must be text"),
                };
                assert_eq!(val, expected);
            }
            "col_from_json_empty|" => {
                let ok = col_empty.from_json("");
                assert_eq!(if ok { "True" } else { "False" }, expected);
            }
            "col_from_json_not_array|" => {
                let ok = col_empty.from_json("{\"a\": 1}");
                assert_eq!(if ok { "True" } else { "False" }, expected);
            }
            other => panic!("unexpected fixture key: {other}"),
        }
    }
}

#[test]
fn sdt_and_collection_json_e2e_build_and_execution() {
    let source = r#"
        // 1. SDT serialization and deserialization
        &Item = new()
        &Item.Code = 100
        &Item.Name = "Widget"
        &ItemJson = &Item.ToJson()
        Print(&ItemJson)

        // 2. Clear item and deserialize back
        &NewItem = new()
        &NewItem.FromJson(&ItemJson)
        Print(&NewItem.Code)
        Print(&NewItem.Name)

        // 3. Partial update with case-insensitivity
        &PartialJson = '{"code": 200}'
        &NewItem.FromJson(&PartialJson)
        Print(&NewItem.Code)
        Print(&NewItem.Name)

        // 4. Collection serialization
        &Col = new()
        &Item1 = new()
        &Item1.Code = 1
        &Item1.Name = "Alpha"
        &Col.Add(&Item1)

        &Item2 = new()
        &Item2.Code = 2
        &Item2.Name = "Beta"
        &Col.Add(&Item2)

        &ColJson = &Col.ToJson()
        Print(&ColJson)

        // 5. Collection deserialization
        &NewCol = new()
        &NewCol.FromJson(&ColJson)
        Print(&NewCol.Count)
        Print(&NewCol.Item(1).Name)
        Print(&NewCol.Item(2).Name)
    "#;

    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "record_types": [{
                "name": "ItemSchema",
                "fields": {
                    "Code": { "kind": "numeric", "precision": 6, "scale": 0 },
                    "Name": { "kind": "varchar", "length": 30 }
                }
            }],
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Item": { "kind": "record", "schema": "ItemSchema" },
                    "NewItem": { "kind": "record", "schema": "ItemSchema" },
                    "Item1": { "kind": "record", "schema": "ItemSchema" },
                    "Item2": { "kind": "record", "schema": "ItemSchema" },
                    "ItemJson": { "kind": "varchar", "length": 500 },
                    "PartialJson": { "kind": "varchar", "length": 500 },
                    "ColJson": { "kind": "varchar", "length": 1000 },
                    "Col": { "kind": "collection", "schema": "ItemSchema" },
                    "NewCol": { "kind": "collection", "schema": "ItemSchema" }
                },
                "source": source
            }]
        })
        .to_string(),
    )
    .expect("valid project");

    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("time")
        .as_nanos();

    struct Folder(std::path::PathBuf);
    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let root = Folder(std::env::temp_dir().join(format!(
        "gx-sdt-json-{}-{}",
        std::process::id(),
        timestamp
    )));

    let artifact = build::build(&project, &root.0).expect("compilation succeeds");
    let output = Command::new(&artifact.executable)
        .output()
        .expect("binary execution succeeds");

    assert!(output.status.success(), "binary should exit with 0");
    let stdout = String::from_utf8(output.stdout).expect("valid utf8");
    let lines: Vec<&str> = stdout.lines().map(|l| l.trim()).collect();

    assert_eq!(lines.len(), 9);
    assert_eq!(lines[0], r#"{"CODE":100,"NAME":"Widget"}"#);
    assert_eq!(lines[1], "100");
    assert_eq!(lines[2], "Widget");
    // Partial update changed Code to 200 and preserved Name "Widget"
    assert_eq!(lines[3], "200");
    assert_eq!(lines[4], "Widget");
    // Collection JSON
    assert_eq!(
        lines[5],
        r#"[{"CODE":1,"NAME":"Alpha"},{"CODE":2,"NAME":"Beta"}]"#
    );
    // New collection count and items
    assert_eq!(lines[6], "2");
    assert_eq!(lines[7], "Alpha");
    assert_eq!(lines[8], "Beta");
}

#[test]
fn sdt_json_rejects_non_container_receiver() {
    let source = r#"
        &Str = "hello"
        &Json = &Str.ToJson()
    "#;

    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Str": { "kind": "varchar", "length": 50 },
                    "Json": { "kind": "varchar", "length": 500 }
                },
                "source": source
            }]
        })
        .to_string(),
    )
    .expect("valid project");

    let root = std::env::temp_dir().join(format!("gx-sdt-err-{}", std::process::id()));
    let res = build::build(&project, &root);
    assert!(res.is_err());
    let err = res.unwrap_err();
    assert!(err.contains("ToJson") || err.contains("record or collection"));
}
