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

fn make_item_record(empty: bool) -> Record {
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
        fields.insert("Label", Field::Text(Text::new("Hello World").unwrap()));
        fields.insert("Fixed", Field::Text(Text::new("ABC").unwrap()));
        fields.insert("Flag", Field::Boolean(true));
        fields.insert("Day", Field::Date(Date::ymd(2026, 3, 29).unwrap()));
        fields.insert(
            "Stamp",
            Field::DateTime(DateTime::from_parts([2026, 3, 29, 14, 30, 45], None).unwrap()),
        );
    }
    Record::new("TestItem", fields).expect("valid item record")
}

fn make_special_item_record() -> Record {
    let mut fields = BTreeMap::new();
    fields.insert("Amount", Field::Numeric("10".parse::<Decimal>().unwrap()));
    fields.insert(
        "Label",
        Field::Text(Text::new("<xml & 'special' \"test\">").unwrap()),
    );
    fields.insert("Fixed", Field::Text(Text::new("X&Y").unwrap()));
    fields.insert("Flag", Field::Boolean(false));
    fields.insert("Day", Field::Date(Date::empty()));
    fields.insert("Stamp", Field::DateTime(DateTime::empty()));
    Record::new("TestItem", fields).expect("valid special item record")
}

fn make_parent_record() -> Record {
    let mut p_fields = BTreeMap::new();
    p_fields.insert("Code", Field::Text(Text::new("P100").unwrap()));

    let mut child_fields = BTreeMap::new();
    child_fields.insert("Amount", Field::Numeric("55".parse::<Decimal>().unwrap()));
    child_fields.insert("Label", Field::Text(Text::new("ChildLabel").unwrap()));
    child_fields.insert("Fixed", Field::Text(Text::new("").unwrap()));
    child_fields.insert("Flag", Field::Boolean(false));
    child_fields.insert("Day", Field::Date(Date::empty()));
    child_fields.insert("Stamp", Field::DateTime(DateTime::empty()));
    let child = Record::new("TestItem", child_fields).unwrap();
    p_fields.insert("Child", Field::Record(child.clone()));

    let col = Collection::with_prototype("TestItem", child.empty_clone());
    let mut i1_fields = BTreeMap::new();
    i1_fields.insert("Amount", Field::Numeric("11".parse::<Decimal>().unwrap()));
    i1_fields.insert("Label", Field::Text(Text::new("FirstItem").unwrap()));
    i1_fields.insert("Fixed", Field::Text(Text::new("").unwrap()));
    i1_fields.insert("Flag", Field::Boolean(false));
    i1_fields.insert("Day", Field::Date(Date::empty()));
    i1_fields.insert("Stamp", Field::DateTime(DateTime::empty()));
    col.add(Record::new("TestItem", i1_fields).unwrap())
        .unwrap();

    let mut i2_fields = BTreeMap::new();
    i2_fields.insert("Amount", Field::Numeric("22".parse::<Decimal>().unwrap()));
    i2_fields.insert("Label", Field::Text(Text::new("SecondItem").unwrap()));
    i2_fields.insert("Fixed", Field::Text(Text::new("").unwrap()));
    i2_fields.insert("Flag", Field::Boolean(false));
    i2_fields.insert("Day", Field::Date(Date::empty()));
    i2_fields.insert("Stamp", Field::DateTime(DateTime::empty()));
    col.add(Record::new("TestItem", i2_fields).unwrap())
        .unwrap();

    p_fields.insert("Items", Field::Collection(col));
    Record::new("TestParent", p_fields).expect("valid parent record")
}

fn make_empty_parent_record() -> Record {
    let mut p_fields = BTreeMap::new();
    p_fields.insert("Code", Field::Text(Text::new("").unwrap()));

    let child = make_item_record(true);
    p_fields.insert("Child", Field::Record(child.clone()));

    let col = Collection::with_prototype("TestItem", child.empty_clone());
    p_fields.insert("Items", Field::Collection(col));
    Record::new("TestParent", p_fields).expect("valid empty parent record")
}

#[test]
fn sdt_xml_matches_native_runtime_oracle() {
    let fixture_path = Path::new("tests/fixtures/sdt_xml_native.tsv");
    let content = fs::read_to_string(fixture_path)
        .or_else(|_| {
            fs::read_to_string(Path::new("src/gx-engine/tests/fixtures/sdt_xml_native.tsv"))
        })
        .expect("sdt_xml_native.tsv exists");

    let sdt_empty = make_item_record(true);
    let sdt_filled = make_item_record(false);
    let sdt_special = make_special_item_record();
    let parent_filled = make_parent_record();

    let col_empty = Collection::with_prototype("TestItem", sdt_empty.empty_clone());
    let col_filled = {
        let col = Collection::with_prototype("TestItem", sdt_empty.empty_clone());
        let mut i1 = BTreeMap::new();
        i1.insert("Amount", Field::Numeric("11".parse::<Decimal>().unwrap()));
        i1.insert("Label", Field::Text(Text::new("FirstItem").unwrap()));
        i1.insert("Fixed", Field::Text(Text::new("").unwrap()));
        i1.insert("Flag", Field::Boolean(false));
        i1.insert("Day", Field::Date(Date::empty()));
        i1.insert("Stamp", Field::DateTime(DateTime::empty()));
        col.add(Record::new("TestItem", i1).unwrap()).unwrap();

        let mut i2 = BTreeMap::new();
        i2.insert("Amount", Field::Numeric("22".parse::<Decimal>().unwrap()));
        i2.insert("Label", Field::Text(Text::new("SecondItem").unwrap()));
        i2.insert("Fixed", Field::Text(Text::new("").unwrap()));
        i2.insert("Flag", Field::Boolean(false));
        i2.insert("Day", Field::Date(Date::empty()));
        i2.insert("Stamp", Field::DateTime(DateTime::empty()));
        col.add(Record::new("TestItem", i2).unwrap()).unwrap();
        col
    };

    let sdt_round = make_item_record(true);
    let filled_xml = sdt_filled.to_xml(None);
    let sdt_round_ok = sdt_round.from_xml(&filled_xml, None);

    let sdt_special_round = make_item_record(true);
    let special_xml = sdt_special.to_xml(None);
    let sdt_special_ok = sdt_special_round.from_xml(&special_xml, None);

    let sdt_partial = make_item_record(false);
    let partial_xml = "<TestItem><Amount>99</Amount></TestItem>";
    let sdt_partial_ok = sdt_partial.from_xml(partial_xml, None);

    let malformed_xml = "<TestItem><Amount>10</Unclosed>";
    let sdt_malformed_round = make_item_record(true);
    let sdt_malformed_ok = sdt_malformed_round.from_xml(malformed_xml, None);

    let sdt_mismatch_round = make_item_record(true);
    let sdt_mismatch_ok = sdt_mismatch_round.from_xml(&filled_xml, Some("WrongRoot"));

    let sdt_empty_round = make_item_record(true);
    let sdt_empty_ok = sdt_empty_round.from_xml("", None);

    let parent_round = make_empty_parent_record();
    let parent_xml = parent_filled.to_xml(None);
    let parent_round_ok = parent_round.from_xml(&parent_xml, None);

    let col_round = Collection::with_prototype("TestItem", sdt_empty.empty_clone());
    let col_xml = col_filled.to_xml(Some("TestItems"));
    let col_round_ok = col_round.from_xml(&col_xml, Some("TestItems"));

    let col_empty_round = Collection::with_prototype("TestItem", sdt_empty.empty_clone());
    col_empty_round.add(make_item_record(false)).unwrap();
    let col_empty_xml = col_empty.to_xml(Some("TestItems"));
    let col_empty_round_ok = col_empty_round.from_xml(&col_empty_xml, Some("TestItems"));

    let col_malformed_round = Collection::with_prototype("TestItem", sdt_empty.empty_clone());
    let col_malformed_ok = col_malformed_round.from_xml(malformed_xml, Some("TestItems"));

    let mut checked_rows = 0;
    for line in content.lines() {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        let parts: Vec<&str> = line.split("|=").collect();
        assert_eq!(parts.len(), 2, "line format: key|=[value]");
        let key = parts[0];
        let expected = parts[1]
            .strip_prefix('[')
            .and_then(|s| s.strip_suffix(']'))
            .unwrap()
            .replace("\\r", "\r")
            .replace("\\n", "\n")
            .replace("\\t", "\t");

        let actual = match key {
            "sdt_empty_to_xml" => sdt_empty.to_xml(None),
            "sdt_filled_to_xml" => sdt_filled.to_xml(None),
            "sdt_special_to_xml" => sdt_special.to_xml(None),
            "sdt_from_xml_roundtrip_ok" => if sdt_round_ok { "True" } else { "False" }.to_string(),
            "sdt_round_amount" => match sdt_round.get("Amount").unwrap() {
                Field::Numeric(n) => n.to_string(),
                _ => panic!("Amount must be numeric"),
            },
            "sdt_round_label" => match sdt_round.get("Label").unwrap() {
                Field::Text(t) => t.as_str().to_string(),
                _ => panic!("Label must be text"),
            },
            "sdt_round_fixed" => match sdt_round.get("Fixed").unwrap() {
                Field::Text(t) => t.as_str().to_string(),
                _ => panic!("Fixed must be text"),
            },
            "sdt_round_flag" => match sdt_round.get("Flag").unwrap() {
                Field::Boolean(b) => if b { "True" } else { "False" }.to_string(),
                _ => panic!("Flag must be boolean"),
            },
            "sdt_round_day" => match sdt_round.get("Day").unwrap() {
                Field::Date(d) => format!("{:04}-{:02}-{:02}", d.year(), d.month(), d.day()),
                _ => panic!("Day must be date"),
            },
            "sdt_round_stamp" => match sdt_round.get("Stamp").unwrap() {
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
            },
            "sdt_special_from_xml_ok" => if sdt_special_ok { "True" } else { "False" }.to_string(),
            "sdt_special_round_label" => match sdt_special_round.get("Label").unwrap() {
                Field::Text(t) => t.as_str().to_string(),
                _ => panic!("Label must be text"),
            },
            "sdt_special_round_fixed" => match sdt_special_round.get("Fixed").unwrap() {
                Field::Text(t) => t.as_str().to_string(),
                _ => panic!("Fixed must be text"),
            },
            "sdt_from_xml_partial_ok" => if sdt_partial_ok { "True" } else { "False" }.to_string(),
            "sdt_partial_amount" => match sdt_partial.get("Amount").unwrap() {
                Field::Numeric(n) => n.to_string(),
                _ => panic!("Amount must be numeric"),
            },
            "sdt_partial_label" => match sdt_partial.get("Label").unwrap() {
                Field::Text(t) => t.as_str().to_string(),
                _ => panic!("Label must be text"),
            },
            "sdt_from_xml_malformed" => if sdt_malformed_ok { "True" } else { "False" }.to_string(),
            "sdt_from_xml_mismatch_root" => {
                if sdt_mismatch_ok { "True" } else { "False" }.to_string()
            }
            "sdt_from_xml_empty" => if sdt_empty_ok { "True" } else { "False" }.to_string(),
            "parent_filled_to_xml" => parent_filled.to_xml(None),
            "parent_from_xml_ok" => if parent_round_ok { "True" } else { "False" }.to_string(),
            "parent_round_code" => match parent_round.get("Code").unwrap() {
                Field::Text(t) => t.as_str().to_string(),
                _ => panic!("Code must be text"),
            },
            "parent_round_child_amount" => {
                let child = match parent_round.get("Child").unwrap() {
                    Field::Record(r) => r,
                    _ => panic!("Child must be record"),
                };
                match child.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                }
            }
            "parent_round_child_label" => {
                let child = match parent_round.get("Child").unwrap() {
                    Field::Record(r) => r,
                    _ => panic!("Child must be record"),
                };
                match child.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_string(),
                    _ => panic!("Label must be text"),
                }
            }
            "parent_round_items_count" => {
                let col = match parent_round.get("Items").unwrap() {
                    Field::Collection(c) => c,
                    _ => panic!("Items must be collection"),
                };
                format!("{}", col.len())
            }
            "parent_round_item1_amount" => {
                let col = match parent_round.get("Items").unwrap() {
                    Field::Collection(c) => c,
                    _ => panic!("Items must be collection"),
                };
                let item = col.item(1).unwrap();
                match item.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                }
            }
            "parent_round_item1_label" => {
                let col = match parent_round.get("Items").unwrap() {
                    Field::Collection(c) => c,
                    _ => panic!("Items must be collection"),
                };
                let item = col.item(1).unwrap();
                match item.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_string(),
                    _ => panic!("Label must be text"),
                }
            }
            "parent_round_item2_amount" => {
                let col = match parent_round.get("Items").unwrap() {
                    Field::Collection(c) => c,
                    _ => panic!("Items must be collection"),
                };
                let item = col.item(2).unwrap();
                match item.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                }
            }
            "parent_round_item2_label" => {
                let col = match parent_round.get("Items").unwrap() {
                    Field::Collection(c) => c,
                    _ => panic!("Items must be collection"),
                };
                let item = col.item(2).unwrap();
                match item.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_string(),
                    _ => panic!("Label must be text"),
                }
            }
            "col_empty_to_xml" => col_empty.to_xml(Some("TestItems")),
            "col_filled_to_xml" => col_filled.to_xml(Some("TestItems")),
            "col_from_xml_ok" => if col_round_ok { "True" } else { "False" }.to_string(),
            "col_round_count" => format!("{}", col_round.len()),
            "col_round_item1_amount" => {
                let item = col_round.item(1).unwrap();
                match item.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                }
            }
            "col_round_item1_label" => {
                let item = col_round.item(1).unwrap();
                match item.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_string(),
                    _ => panic!("Label must be text"),
                }
            }
            "col_round_item2_amount" => {
                let item = col_round.item(2).unwrap();
                match item.get("Amount").unwrap() {
                    Field::Numeric(n) => n.to_string(),
                    _ => panic!("Amount must be numeric"),
                }
            }
            "col_round_item2_label" => {
                let item = col_round.item(2).unwrap();
                match item.get("Label").unwrap() {
                    Field::Text(t) => t.as_str().to_string(),
                    _ => panic!("Label must be text"),
                }
            }
            "col_from_xml_empty" => if col_empty_round_ok { "True" } else { "False" }.to_string(),
            "col_empty_round_count" => format!("{}", col_empty_round.len()),
            "col_from_xml_malformed" => if col_malformed_ok { "True" } else { "False" }.to_string(),
            other => panic!("unknown oracle key: {other}"),
        };

        // Normalize CRLF to LF for comparison if comparing multi-line XML
        let actual_norm = actual.replace("\r\n", "\n");
        let expected_norm = expected.replace("\r\n", "\n");
        assert_eq!(
            actual_norm, expected_norm,
            "mismatch for oracle key: {key}\nActual:\n{actual}\nExpected:\n{expected}"
        );
        checked_rows += 1;
    }

    assert_eq!(checked_rows, 40, "all 40 oracle rows checked");
}

fn unique_temp_dir(prefix: &str) -> std::path::PathBuf {
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let dir = std::env::temp_dir().join(format!("{prefix}_{nonce}"));
    fs::create_dir_all(&dir).unwrap();
    dir
}

#[test]
fn sdt_and_collection_xml_methods_e2e() {
    let source = r#"
        &Item = new()
        &Item.Id = 123
        &Item.Name = "TestItem"
        &Xml = &Item.ToXml()
        Print(&Xml)

        &Item.Id = 0
        &Item.Name = ""
        &Item.FromXml(&Xml)
        Print(&Item.Id)
        Print(&Item.Name)

        &Items = new()
        &Items.Add(&Item)
        &Xml = &Items.ToXml("ItemsRoot")
        Print(&Xml)

        &Items.Clear()
        &Ok = &Items.FromXml(&Xml, "ItemsRoot")
        Print(&Ok)
        Print(&Items.Count)
    "#;

    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "record_types": [{
                "name": "Item",
                "fields": {
                    "Id": { "kind": "numeric", "precision": 10, "scale": 0 },
                    "Name": { "kind": "varchar", "length": 50 }
                }
            }],
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Item": { "kind": "record", "schema": "Item" },
                    "Items": { "kind": "collection", "schema": "Item" },
                    "Xml": { "kind": "varchar", "length": 1000 },
                    "Ok": { "kind": "boolean" }
                },
                "source": source
            }]
        })
        .to_string(),
    )
    .expect("valid project");
    let out_dir = unique_temp_dir("sdt_xml_e2e");
    let result = build::build(&project, &out_dir).expect("build succeeded");

    let run = Command::new(result.executable)
        .output()
        .expect("run program");
    assert!(run.status.success(), "execution succeeded");
    let stdout = String::from_utf8(run.stdout).expect("valid utf-8 output");
    // Verify printed outputs
    assert!(stdout.contains("<ITEM>"), "ToXml has root tag: {stdout}");
    assert!(
        stdout.contains("<ID>123</ID>"),
        "ToXml has Id tag: {stdout}"
    );
    assert!(
        stdout.contains("<NAME>TestItem</NAME>"),
        "ToXml has Name tag: {stdout}"
    );
    assert!(stdout.contains("123"), "FromXml restored Id: {stdout}");
    assert!(
        stdout.contains("TestItem"),
        "FromXml restored Name: {stdout}"
    );
    assert!(
        stdout.contains("<ItemsRoot>"),
        "Collection ToXml has ItemsRoot: {stdout}"
    );
    assert!(stdout.contains("true"), "FromXml returned true: {stdout}");
    assert!(stdout.contains("1"), "Collection count is 1: {stdout}");

    let _ = fs::remove_dir_all(out_dir);
}
