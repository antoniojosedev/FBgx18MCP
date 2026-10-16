use gx_engine::{
    build,
    model::Project,
    record_runtime::{Collection, Field, Record},
    text::Text,
};
use serde_json::json;
use std::{
    collections::BTreeMap,
    fs,
    path::Path,
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

fn make_record(val: &str) -> Record {
    let mut fields = BTreeMap::new();
    fields.insert("Value", Field::Text(Text::new(val).expect("valid text")));
    Record::new("Item", fields).expect("valid record")
}

fn get_record_val(rec: &Record) -> String {
    match rec.get("Value").expect("field exists") {
        Field::Text(t) => t.as_str().to_owned(),
        _ => panic!("expected text"),
    }
}

#[test]
fn collection_ops_match_recorded_native_runtime() {
    let fixture_path = Path::new("tests/fixtures/collection_ops_native.tsv");
    let content = fs::read_to_string(fixture_path)
        .or_else(|_| {
            fs::read_to_string(Path::new(
                "src/gx-engine/tests/fixtures/collection_ops_native.tsv",
            ))
        })
        .expect("collection_ops_native.tsv exists");

    let col = Collection::new("Item");

    let item_alpha = make_record("alpha");
    let item_beta = make_record("beta");
    let item_gamma = make_record("gamma");
    let item_first = make_record("first");
    let item_third = make_record("third");
    let item_end = make_record("end");
    let item_missing = make_record("missing");

    for line in content.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() || trimmed.starts_with('#') {
            continue;
        }

        let parts: Vec<&str> = trimmed.split('=').collect();
        assert_eq!(parts.len(), 2, "line must be key=[expected]: {trimmed}");
        let key = parts[0];
        let expected = parts[1].trim_start_matches('[').trim_end_matches(']');

        match key {
            "count_initial|" => {
                assert_eq!(col.len().to_string(), expected);
                col.add(item_alpha.clone()).unwrap();
                col.add(item_beta.clone()).unwrap();
                col.add(item_gamma.clone()).unwrap();
            }
            "count_after_3_adds|" => {
                assert_eq!(col.len().to_string(), expected);
            }
            "item|1" => {
                assert_eq!(get_record_val(&col.item(1).unwrap()), expected);
            }
            "item|2" => {
                assert_eq!(get_record_val(&col.item(2).unwrap()), expected);
            }
            "item|3" => {
                assert_eq!(get_record_val(&col.item(3).unwrap()), expected);
            }
            "index_of|alpha" => {
                assert_eq!(col.index_of(&item_alpha).to_string(), expected);
            }
            "index_of|beta" => {
                assert_eq!(col.index_of(&item_beta).to_string(), expected);
            }
            "index_of|gamma" => {
                assert_eq!(col.index_of(&item_gamma).to_string(), expected);
            }
            "index_of|missing" => {
                assert_eq!(col.index_of(&item_missing).to_string(), expected);
                col.insert(1, item_first.clone()).unwrap();
            }
            "count_after_insert_1|" => {
                assert_eq!(col.len().to_string(), expected);
            }
            "item_after_insert_1|1" => {
                assert_eq!(get_record_val(&col.item(1).unwrap()), expected);
            }
            "item_after_insert_1|2" => {
                assert_eq!(get_record_val(&col.item(2).unwrap()), expected);
                col.insert(3, item_third.clone()).unwrap();
            }
            "count_after_insert_3|" => {
                assert_eq!(col.len().to_string(), expected);
            }
            "item_after_insert_3|3" => {
                assert_eq!(get_record_val(&col.item(3).unwrap()), expected);
                col.insert(0, item_end.clone()).unwrap();
            }
            "count_after_insert_0|" => {
                assert_eq!(col.len().to_string(), expected);
            }
            "item_after_insert_0|last" => {
                assert_eq!(
                    get_record_val(&col.item(col.len() as i64).unwrap()),
                    expected
                );
                col.remove(1).unwrap();
            }
            "count_after_remove_1|" => {
                assert_eq!(col.len().to_string(), expected);
            }
            "item_after_remove_1|1" => {
                assert_eq!(get_record_val(&col.item(1).unwrap()), expected);
                col.remove(col.len() as i64).unwrap();
            }
            "count_after_remove_last|" => {
                assert_eq!(col.len().to_string(), expected);
            }
            "item_after_remove_last|last" => {
                assert_eq!(
                    get_record_val(&col.item(col.len() as i64).unwrap()),
                    expected
                );
                col.remove(0).unwrap();
                col.remove(-1).unwrap();
                col.remove(999).unwrap();
            }
            "count_after_invalid_removes|" => {
                assert_eq!(col.len().to_string(), expected);
                col.clear();
            }
            "count_after_clear|" => {
                assert_eq!(col.len().to_string(), expected);
            }
            "index_of_cleared|alpha" => {
                assert_eq!(col.index_of(&item_alpha).to_string(), expected);
                col.remove(1).unwrap();
            }
            "count_after_remove_empty|" => {
                assert_eq!(col.len().to_string(), expected);
            }
            other => panic!("unexpected fixture key: {other}"),
        }
    }
}

#[test]
fn collection_ops_and_for_in_run_like_the_recorded_runtime() {
    let source = r#"
        &Items = new()
        &Item1 = new()
        &Item1.Code = 10
        &Item1.Name = "alpha"
        &Items.Add(&Item1)

        &Item2 = new()
        &Item2.Code = 20
        &Item2.Name = "beta"
        &Items.Add(&Item2)

        &Item3 = new()
        &Item3.Code = 30
        &Item3.Name = "gamma"
        &Items.Add(&Item3)

        // Count and IndexOf
        Print(&Items.Count)
        Print(&Items.IndexOf(&Item2))

        // Insert at 1
        &ItemFirst = new()
        &ItemFirst.Code = 1
        &ItemFirst.Name = "first"
        &Items.Add(&ItemFirst, 1)

        Print(&Items.Count)
        Print(&Items.IndexOf(&ItemFirst))
        Print(&Items.IndexOf(&Item2))

        // For in loop iterating items
        For &Item in &Items
            Print(&Item.Code)
            Print(&Item.Name)
        EndFor

        // For in loop with Exit
        For &Item in &Items
            If &Item.Code = 10
                Exit
            EndIf
            Print(&Item.Code)
        EndFor

        // Remove item at 1
        &Items.Remove(1)
        Print(&Items.Count)

        // Clear
        &Items.Clear()
        Print(&Items.Count)
    "#;

    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "record_types": [{
                "name": "Item",
                "fields": {
                    "Code": { "kind": "numeric", "precision": 6, "scale": 0 },
                    "Name": { "kind": "varchar", "length": 30 }
                }
            }],
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Items": { "kind": "collection", "schema": "Item" },
                    "Item": { "kind": "record", "schema": "Item" },
                    "Item1": { "kind": "record", "schema": "Item" },
                    "Item2": { "kind": "record", "schema": "Item" },
                    "Item3": { "kind": "record", "schema": "Item" },
                    "ItemFirst": { "kind": "record", "schema": "Item" }
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
        "gx-col-ops-{}-{}",
        std::process::id(),
        timestamp
    )));

    let artifact = build::build(&project, &root.0).expect("compilation succeeds");

    let run_output = Command::new(&artifact.executable)
        .output()
        .expect("executable runs");

    assert!(
        run_output.status.success(),
        "execution failed: {}",
        String::from_utf8_lossy(&run_output.stderr)
    );

    let stdout = String::from_utf8_lossy(&run_output.stdout);
    let lines: Vec<&str> = stdout
        .lines()
        .map(|l| l.trim())
        .filter(|l| !l.is_empty())
        .collect();

    assert_eq!(
        lines,
        vec![
            "3", // initial count
            "2", // IndexOf(Item2) is 2
            "4", // count after insert at 1
            "1", // IndexOf(ItemFirst) is 1
            "3", // IndexOf(Item2) shifted to 3
            // For in loop items:
            "1", "first", "10", "alpha", "20", "beta", "30", "gamma",
            // For in loop with Exit (breaks when Code == 10, so only prints 1):
            "1", // Count after Remove(1):
            "3", // Count after Clear():
            "0",
        ]
    );
}

#[test]
fn collection_ops_rejects_schema_mismatch_and_invalid_operations() {
    // Adding wrong record schema to collection
    let p1 = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "record_types": [
                {
                    "name": "ItemA",
                    "fields": { "Code": { "kind": "numeric", "precision": 6, "scale": 0 } }
                },
                {
                    "name": "ItemB",
                    "fields": { "Val": { "kind": "varchar", "length": 20 } }
                }
            ],
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Items": { "kind": "collection", "schema": "ItemA" },
                    "Other": { "kind": "record", "schema": "ItemB" }
                },
                "source": "&Items.Add(&Other)"
            }]
        })
        .to_string(),
    )
    .expect("valid project");

    let res = gx_engine::compile(&p1);
    assert!(res.is_err(), "schema mismatch in Add must be rejected");

    // For in with wrong iterator schema
    let p2 = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "record_types": [
                {
                    "name": "ItemA",
                    "fields": { "Code": { "kind": "numeric", "precision": 6, "scale": 0 } }
                },
                {
                    "name": "ItemB",
                    "fields": { "Val": { "kind": "varchar", "length": 20 } }
                }
            ],
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Items": { "kind": "collection", "schema": "ItemA" },
                    "Item": { "kind": "record", "schema": "ItemB" }
                },
                "source": "For &Item in &Items\nEndFor"
            }]
        })
        .to_string(),
    )
    .expect("valid project");

    let res2 = gx_engine::compile(&p2);
    assert!(res2.is_err(), "schema mismatch in ForIn must be rejected");
}
