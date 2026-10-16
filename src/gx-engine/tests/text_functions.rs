use gx_engine::{build, model::Project, text::Text};
use serde_json::json;
use std::{
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

fn project(source: &str) -> Project {
    Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[{"name":"Main","params":[],"variables":{"Text":{"kind":"varchar","length":20},"Mixed":{"kind":"varchar","length":20},"Fixed":{"kind":"character","length":6}},"source":source}]}).to_string()).unwrap()
}

#[test]
fn text_functions_match_recorded_native_runtime() {
    // Recorded native oracle: `text-functions-native-1792124242829986500`
    // (in-range and out-of-range Substr/Upper/Lower/StrReplace objects),
    // `text-functions-semantics-1792124395268051600` (space handling, empty-find
    // StrReplace, CP1252 case mapping where the Unicode mapping is not a
    // one-character replacement: `ß` stays `ß`, `µ` maps to `Μ`, `ÿ` to `Ÿ`)
    // and `text-functions-charspace-1792124464499013100` (Character values keep
    // explicitly assigned trailing spaces for Substr). Fixture format:
    // `function|args=[result]`; the expected values are the exact native bytes,
    // spaces included. `Substr` windows clamp to the raw value, a start below 1
    // or past the last character returns the empty value, and a negative length
    // takes everything from the start.
    let lines = include_str!("fixtures/text_functions_native.tsv");
    let mut total = 0_usize;
    let mut failures = Vec::new();
    for line in lines.lines() {
        let (left, right) = line.split_once("=[").expect(line);
        let expected = right.strip_suffix(']').expect(line);
        let parts: Vec<&str> = left.split('|').collect();
        total += 1;
        let got = match parts[0] {
            "substr" => Text::new(parts[1])
                .unwrap()
                .substring(parts[2].parse().unwrap(), parts[3].parse().unwrap())
                .to_string(),
            "upper" => Text::new(parts[1]).unwrap().to_upper().to_string(),
            "lower" => Text::new(parts[1]).unwrap().to_lower().to_string(),
            "replace" => Text::new(parts[1])
                .unwrap()
                .str_replace(&Text::new(parts[2]).unwrap(), &Text::new(parts[3]).unwrap())
                .unwrap()
                .to_string(),
            other => panic!("unknown fixture function {other}"),
        };
        if got != expected {
            failures.push(format!("{line} engine [{got}]"));
        }
    }
    assert!(total > 60, "fixture unexpectedly small: {total}");
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

#[test]
fn text_function_calls_and_methods_run_like_the_recorded_runtime() {
    let input = project(
        "&Text = \"banana\"\n&Mixed = \"aBc123\"\n&Fixed = \"ab \"\nPrint(Substr(&Text, 2, 3))\nPrint(&Text.Substring(2, 3))\nPrint(Substr(&Text, 2, -1))\nPrint(Substr(&Text, 0, 2))\nPrint(Substr(&Text, 11, 1))\nPrint(StrReplace(&Text, \"na\", \"X\"))\nPrint(&Text.Replace(\"na\", \"X\"))\nPrint(StrReplace(&Text, \"\", \"X\"))\nPrint(Upper(&Mixed))\nPrint(&Mixed.ToUpper())\nPrint(Lower(&Mixed))\nPrint(&Mixed.ToLower())\nPrint(Substr(&Fixed, 1, 3))\nPrint(Substr(&Fixed, 3, 1))\nPrint(Upper(\"ßµ\"))",
    );
    let root = std::env::temp_dir().join(format!(
        "gx-text-functions-{}-{}",
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
    assert_eq!(
        String::from_utf8(output.stdout)
            .unwrap()
            .lines()
            .collect::<Vec<_>>(),
        [
            "ana", "ana", "anana", "", "", "baXX", "baXX", "banana", "ABC123", "ABC123", "abc123",
            "abc123", "ab ", " ", "ßΜ"
        ]
    );
}

#[test]
fn text_functions_reject_wrong_arity_and_nontext_values() {
    for source in [
        "Print(Substr(\"abc\", 1))",
        "Print(Substr(1, 2, 3))",
        "Print(Substr(\"abc\", 1, 2, 3))",
        "Print(Upper(1))",
        "Print(Lower(True))",
        "Print(StrReplace(\"a\", \"b\"))",
        "Print(StrReplace(1, \"a\", \"b\"))",
        "Print(&Text.Substring(1))",
        "Print(&Text.Replace(\"a\"))",
    ] {
        assert!(
            gx_engine::compile(&project(source)).is_err(),
            "admitted {source}"
        );
    }
}
