use gx_engine::{build, model::Project, text::Text};
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
                    "Text": { "kind": "varchar", "length": 30 },
                    "Other": { "kind": "varchar", "length": 30 },
                    "Result": { "kind": "varchar", "length": 60 },
                    "Flag": { "kind": "boolean" },
                    "Pos": { "kind": "numeric", "precision": 4, "scale": 0 }
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
fn text_extended_match_recorded_native_runtime() {
    let fixture = include_str!("fixtures/text_extended_native.tsv");
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

        let got = match parts[0] {
            "ltrim" => {
                let s = unescape(parts[1]);
                Text::new(&s).unwrap().trim_start_spaces().to_string()
            }
            "rtrim" => {
                let s = unescape(parts[1]);
                Text::new(&s).unwrap().trim_end_spaces().to_string()
            }
            "padl" => {
                let s = unescape(parts[1]);
                let size: i64 = parts[2].parse().unwrap();
                let fill = unescape(parts[3]);
                Text::new(&s)
                    .unwrap()
                    .pad_left(size, &Text::new(&fill).unwrap())
                    .unwrap()
                    .to_string()
            }
            "padr" => {
                let s = unescape(parts[1]);
                let size: i64 = parts[2].parse().unwrap();
                let fill = unescape(parts[3]);
                Text::new(&s)
                    .unwrap()
                    .pad_right(size, &Text::new(&fill).unwrap())
                    .unwrap()
                    .to_string()
            }
            "space" => {
                let count: i64 = parts[1].parse().unwrap();
                Text::space(count).unwrap().to_string()
            }
            "concat" => {
                let s1 = unescape(parts[1]);
                let s2 = unescape(parts[2]);
                Text::new(&s1)
                    .unwrap()
                    .concat_with_sep(&Text::new(&s2).unwrap(), None)
                    .unwrap()
                    .to_string()
            }
            "concat3" => {
                let s1 = unescape(parts[1]);
                let s2 = unescape(parts[2]);
                let sep = unescape(parts[3]);
                Text::new(&s1)
                    .unwrap()
                    .concat_with_sep(&Text::new(&s2).unwrap(), Some(&Text::new(&sep).unwrap()))
                    .unwrap()
                    .to_string()
            }
            "startswith" => {
                let s1 = unescape(parts[1]);
                let s2 = unescape(parts[2]);
                let b = Text::new(&s1)
                    .unwrap()
                    .starts_with(&Text::new(&s2).unwrap());
                if b {
                    "True".to_string()
                } else {
                    "False".to_string()
                }
            }
            "endswith" => {
                let s1 = unescape(parts[1]);
                let s2 = unescape(parts[2]);
                let b = Text::new(&s1).unwrap().ends_with(&Text::new(&s2).unwrap());
                if b {
                    "True".to_string()
                } else {
                    "False".to_string()
                }
            }
            "contains" => {
                let s1 = unescape(parts[1]);
                let s2 = unescape(parts[2]);
                let b = Text::new(&s1).unwrap().contains(&Text::new(&s2).unwrap());
                if b {
                    "True".to_string()
                } else {
                    "False".to_string()
                }
            }
            "strsearch" => {
                let s1 = unescape(parts[1]);
                let s2 = unescape(parts[2]);
                let start: i64 = parts[3].parse().unwrap();
                Text::new(&s1)
                    .unwrap()
                    .str_search(&Text::new(&s2).unwrap(), start)
                    .to_string()
            }
            other => panic!("unknown fixture function {other}"),
        };

        if got != expected {
            failures.push(format!(
                "{line} => engine got [{got}] expected [{expected}]"
            ));
        }
    }

    assert!(total >= 90, "fixture unexpectedly small: {total}");
    assert!(failures.is_empty(), "Failures:\n{}", failures.join("\n"));
}

#[test]
fn text_extended_calls_and_methods_run_like_the_recorded_runtime() {
    let source = r#"
&Text = "  GeneXus 18  "
&Other = "Engine"
Print(LTrim(&Text))
Print(&Text.TrimStart())
Print(RTrim(&Text))
Print(&Text.TrimEnd())
Print(PadL("abc", 7, "*"))
Print("abc".PadLeft(7, "*"))
Print(PadR("abc", 7, "-"))
Print("abc".PadRight(7, "-"))
Print(Space(4))
Print(Concat("hello  ", "world"))
Print(Concat("hello  ", "world", "::"))
Print("hello  ".Concat("world", "::"))
Print(StartsWith("GeneXus", "Gene"))
Print("GeneXus".StartsWith("Gene"))
Print(EndsWith("GeneXus", "Xus"))
Print("GeneXus".EndsWith("Xus"))
Print(Contains("GeneXus", "neX"))
Print("GeneXus".Contains("neX"))
Print(StrSearch("banana", "na", 1))
Print("banana".IndexOf("na", 4))
"#;

    let input = project(source);
    let root = std::env::temp_dir().join(format!(
        "gx-text-extended-{}-{}",
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
            "GeneXus 18  ",
            "GeneXus 18  ",
            "  GeneXus 18",
            "  GeneXus 18",
            "****abc",
            "****abc",
            "abc----",
            "abc----",
            "    ",
            "helloworld",
            "hello::world",
            "hello::world",
            "true",
            "true",
            "true",
            "true",
            "true",
            "true",
            "3",
            "5",
        ]
    );
}

#[test]
fn text_extended_rejects_wrong_arity_and_nontext_values() {
    for source in [
        "Print(LTrim(123))",
        "Print(LTrim(\"a\", \"b\"))",
        "Print(RTrim(True))",
        "Print(PadL(123, 5))",
        "Print(PadL(\"a\", \"bad\"))",
        "Print(PadR(\"a\", 5, 123))",
        "Print(Space(\"bad\"))",
        "Print(Concat(\"a\"))",
        "Print(Concat(\"a\", \"b\", \"c\", \"d\"))",
        "Print(StartsWith(123, \"a\"))",
        "Print(EndsWith(\"a\", 123))",
        "Print(Contains(\"a\"))",
        "Print(StrSearch(\"a\"))",
        "Print(StrSearch(\"a\", \"b\", \"bad\"))",
        "Print(&Text.TrimStart(\"extra\"))",
        "Print(&Text.PadLeft(\"bad\"))",
        "Print(&Text.StartsWith())",
        "Print(&Text.IndexOf())",
    ] {
        assert!(
            gx_engine::compile(&project(source)).is_err(),
            "admitted invalid source: {source}"
        );
    }
}
