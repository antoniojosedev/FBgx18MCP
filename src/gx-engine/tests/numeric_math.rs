use gx_engine::{build, model::Project, text::Text, types::Decimal};
use serde_json::json;
use std::{
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

fn project(source: &str) -> Project {
    Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Amount": { "kind": "numeric", "precision": 10, "scale": 3 },
                    "Digits": { "kind": "numeric", "precision": 4, "scale": 0 },
                    "Text": { "kind": "varchar", "length": 40 },
                    "Flag": { "kind": "boolean" },
                    "Code": { "kind": "numeric", "precision": 5, "scale": 0 }
                },
                "source": source
            }]
        })
        .to_string(),
    )
    .unwrap()
}

fn unescape(s: &str) -> String {
    s.replace("\\t", "\t")
        .replace("\\n", "\n")
        .replace("\\r", "\r")
}

#[test]
fn numeric_math_match_recorded_native_runtime() {
    let fixture = include_str!("fixtures/numeric_math_native.tsv");
    let mut total = 0_usize;
    let mut failures = Vec::new();

    for line in fixture.lines() {
        if line.is_empty() {
            continue;
        }
        total += 1;
        let (left, right) = line.split_once("=[").expect(line);
        let expected_raw = right.strip_suffix(']').expect(line);
        let expected = unescape(expected_raw);
        let parts: Vec<&str> = left.split('|').collect();

        match parts[0] {
            "int" => {
                let val: Decimal = parts[1].parse().unwrap();
                let got = val.floor_to_i64().unwrap().to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "round" => {
                let val: Decimal = parts[1].parse().unwrap();
                let digits: i64 = parts[2].parse().unwrap();
                let got = val.round(digits).unwrap();
                let exp: Decimal = expected.parse().unwrap();
                if got != exp {
                    failures.push(format!("{line} => got [{got}] expected [{exp}]"));
                }
            }
            "trunc" => {
                let val: Decimal = parts[1].parse().unwrap();
                let digits: i64 = parts[2].parse().unwrap();
                let got = val.trunc(digits).unwrap();
                let exp: Decimal = expected.parse().unwrap();
                if got != exp {
                    failures.push(format!("{line} => got [{got}] expected [{exp}]"));
                }
            }
            "val" => {
                let s = unescape(parts[1]);
                let got = Decimal::val(&s, None);
                let exp: Decimal = expected.parse().unwrap();
                if got != exp {
                    failures.push(format!("{line} => got [{got}] expected [{exp}]"));
                }
            }
            "val2" => {
                let s = unescape(parts[1]);
                let dsep = parts[2];
                let got = Decimal::val(&s, Some(dsep));
                let exp: Decimal = expected.parse().unwrap();
                if got != exp {
                    failures.push(format!("{line} => got [{got}] expected [{exp}]"));
                }
            }
            "left" => {
                let s = unescape(parts[1]);
                let size: i64 = parts[2].parse().unwrap();
                let got = Text::new(&s).unwrap().left(size).to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "right" => {
                let s = unescape(parts[1]);
                let size: i64 = parts[2].parse().unwrap();
                if expected.starts_with("ERR:") {
                    if Text::new(&s).unwrap().right(size).is_ok() {
                        failures.push(format!("{line} => expected error, got Ok"));
                    }
                } else {
                    let got = Text::new(&s).unwrap().right(size).unwrap().to_string();
                    if got != expected {
                        failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                    }
                }
            }
            "asc" => {
                let s = unescape(parts[1]);
                let got = Text::new(&s).unwrap().asc().to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "chr" => {
                let ascii: i64 = parts[1].parse().unwrap();
                let got = Text::chr(ascii).unwrap().to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "notnumeric" => {
                let s = unescape(parts[1]);
                let got = Text::new(&s).unwrap().not_numeric().to_string();
                if got != expected.to_lowercase() {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "ltrimstr" => {
                let val: Decimal = parts[1].parse().unwrap();
                let len: u8 = parts[2].parse().unwrap();
                let dec: u8 = parts[3].parse().unwrap();
                let formatted = gx_engine::types::format_str(val, len, dec).unwrap();
                let got = formatted.trim_start_matches(' ');
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            other => panic!("unknown fixture operation {other}"),
        }
    }

    assert!(total >= 100, "fixture unexpectedly small: {total}");
    assert!(failures.is_empty(), "Failures:\n{}", failures.join("\n"));
}

#[test]
fn numeric_math_calls_and_methods_run_like_the_recorded_runtime() {
    let source = r#"
&Amount = 123.456
&Text = "hello world"
Print(Int(&Amount))
Print(&Amount.Int())
Print(Round(&Amount, 2))
Print(&Amount.Round(2))
Print(Trunc(&Amount, 2))
Print(&Amount.Trunc(2))
Print(Round(1234.56, -2))
Print(Trunc(1234.56, -2))
Print(Val("456.78"))
Print("456.78".ToNumeric())
Print(Val("456,78", ","))
Print(Left(&Text, 5))
Print(&Text.Left(5))
Print(Right(&Text, 5))
Print(&Text.Right(5))
Print(Asc("A"))
Print("A".Asc())
Print(Asc(""))
Print(Chr(66))
Print(NotNumeric("12345"))
Print("12345".NotNumeric())
Print(NotNumeric("123.45"))
Print(LTrimStr(123.45, 10, 2))
"#;

    let input = project(source);
    let root = std::env::temp_dir().join(format!(
        "gx-numeric-math-{}-{}",
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
            "123", "123", "123.46", "123.46", "123.45", "123.45", "1200", "1200", "456.78",
            "456.78", "456.78", "hello", "hello", "world", "world", "65", "65", "32", "B", "false",
            "false", "true", "123.45",
        ]
    );
}

#[test]
fn numeric_math_rejects_wrong_arity_and_types() {
    for source in [
        "Print(Int())",
        "Print(Int(\"text\"))",
        "Print(Int(1, 2))",
        "Print(Round(123))",
        "Print(Round(\"a\", 2))",
        "Print(Round(123, \"b\"))",
        "Print(Trunc(123))",
        "Print(Val(123))",
        "Print(Val(\"a\", 2))",
        "Print(Left(123, 2))",
        "Print(Left(\"a\", \"b\"))",
        "Print(Right(123, 2))",
        "Print(Asc(123))",
        "Print(Chr(\"A\"))",
        "Print(NotNumeric(123))",
        "Print(LTrimStr(\"a\", 5, 2))",
        "Print(&Amount.Int(\"extra\"))",
        "Print(&Amount.Round())",
        "Print(&Text.Left())",
        "Print(&Text.Asc(\"extra\"))",
    ] {
        assert!(
            gx_engine::compile(&project(source)).is_err(),
            "admitted invalid source: {source}"
        );
    }
}
