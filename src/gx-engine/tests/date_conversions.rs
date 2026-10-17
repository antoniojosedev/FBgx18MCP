use gx_engine::{build, date::Date, datetime::DateTime, model::Project};
use serde_json::json;
use std::{
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

const TWO_DIGIT_MAX: u16 = 2039;

fn dated_project(source: &str) -> Project {
    Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "date_two_digit_year_max": TWO_DIGIT_MAX,
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Day": { "kind": "date" },
                    "Stamp": { "kind": "date_time" },
                    "Label": { "kind": "varchar", "length": 40 }
                },
                "source": source
            }]
        })
        .to_string(),
    )
    .unwrap()
}

/// Date-conversion rows encode spaces as `\s` so trimming cases survive
/// whitespace-insensitive storage.
fn unescape(s: &str) -> String {
    s.replace("\\s", " ")
        .replace("\\t", "\t")
        .replace("\\n", "\n")
        .replace("\\r", "\r")
}

fn iso_date(label: &str) -> Date {
    let mut parts = label.split('-');
    let year: i64 = parts.next().unwrap().parse().unwrap();
    let month: i64 = parts.next().unwrap().parse().unwrap();
    let day: i64 = parts.next().unwrap().parse().unwrap();
    Date::ymd(year, month, day).unwrap()
}

fn iso_datetime(label: &str) -> DateTime {
    let (date, time) = label.split_once('_').expect(label);
    let date = iso_date(date);
    let mut parts = time.split(':');
    let hour: i64 = parts.next().unwrap().parse().unwrap();
    let minute: i64 = parts.next().unwrap().parse().unwrap();
    let second: i64 = parts.next().unwrap().parse().unwrap();
    DateTime::from_parts(
        [date.year(), date.month(), date.day(), hour, minute, second],
        None,
    )
    .unwrap()
}

#[test]
fn date_conversions_match_recorded_native_runtime() {
    let fixture = include_str!("fixtures/date_conversions_native.tsv");
    let mut total = 0_usize;
    let mut failures = Vec::new();

    for line in fixture.lines() {
        if line.is_empty() {
            continue;
        }
        total += 1;
        let (left, right) = line.split_once("=[").expect(line);
        let expected = right.strip_suffix(']').expect(line);
        let parts: Vec<&str> = left.split('|').collect();

        match parts[0] {
            "ctod" => {
                let parsed = Date::parse_cto_d(&unescape(parts[1]), Some(TWO_DIGIT_MAX));
                let shown = if parsed.is_empty() {
                    "empty".to_owned()
                } else {
                    parsed.to_iso_text()
                };
                if shown != expected {
                    failures.push(format!("{line} => got [{shown}] expected [{expected}]"));
                }
            }
            "dtoc" => {
                let date = if parts[1] == "empty" {
                    Date::empty()
                } else {
                    iso_date(parts[1])
                };
                let shown = date.to_iso_text();
                if shown != expected {
                    failures.push(format!("{line} => got [{shown}] expected [{expected}]"));
                }
            }
            "ttoc" => {
                let stamp = if parts[1] == "empty" {
                    DateTime::empty()
                } else {
                    iso_datetime(parts[1])
                };
                let shown = stamp.to_iso_text();
                if shown != expected {
                    failures.push(format!("{line} => got [{shown}] expected [{expected}]"));
                }
            }
            other => panic!("unknown fixture operation {other}"),
        }
    }

    assert!(total >= 45, "fixture unexpectedly small: {total}");
    assert!(failures.is_empty(), "Failures:\n{}", failures.join("\n"));
}

#[test]
fn date_conversions_run_in_admitted_source() {
    let source = r#"
&Day = CToD("2024-02-29")
Print(DToC(&Day))
Print(DToC(CToD("02/29/24")))
Print(DToC(CToD("2024-02-30")))
Print(DToC(CToD("2024/2/9")))
Print(DToC(CToD("2024-02-29 14:30:45")))
&Label = DToC(CToD("12/31/39"))
Print(&Label)
&Stamp = YMDHMSToT(2024, 2, 29, 14, 30, 45)
Print(TToC(&Stamp))
Print(TToC(YMDHMSToT(2024, 2, 29, 0, 0, 0)))
&Day = CToD(" 2024-02-29 ")
Print(DToC(&Day))
"#;

    let input = dated_project(source);
    let root = std::env::temp_dir().join(format!(
        "gx-date-conversions-{}-{}",
        std::process::id(),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));

    let artifact = build::build(&input, &root).unwrap();
    let output = Command::new(&artifact.executable).output().unwrap();
    let _ = std::fs::remove_dir_all(root);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );

    let lines = String::from_utf8(output.stdout)
        .unwrap()
        .lines()
        .map(|s| s.to_string())
        .collect::<Vec<_>>();

    assert_eq!(
        lines,
        vec![
            "2024-02-29",
            "2024-02-29",
            "0000-00-00",
            "2024-02-09",
            "2024-02-29",
            "2039-12-31",
            "2024-02-29T14:30:45",
            "2024-02-29T00:00:00",
            "2024-02-29",
        ]
    );
}

#[test]
fn date_conversions_reject_wrong_arity_and_types() {
    for source in [
        "Print(CToD())",
        "Print(CToD(123))",
        "Print(CToD(\"a\", \"b\"))",
        "Print(DToC())",
        "Print(DToC(\"text\"))",
        "Print(DToC(123))",
        "Print(DToC(CToD(\"2024-02-29\"), 1))",
        "Print(TToC())",
        "Print(TToC(\"text\"))",
        "Print(TToC(123))",
        "&Day = CToD(\"2024-02-29\")\nPrint(TToC(&Day))",
        "&Stamp = YMDHMSToT(2024, 2, 29, 14, 30, 45)\nPrint(DToC(&Stamp))",
        "&Day = CToD(\"2024-02-29\")\nPrint(CToD(&Day))",
    ] {
        assert!(
            gx_engine::compile(&dated_project(source)).is_err(),
            "admitted invalid source: {source}"
        );
    }
}
