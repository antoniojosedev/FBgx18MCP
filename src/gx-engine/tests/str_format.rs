use gx_engine::{
    build,
    model::Project,
    types::{Decimal, format_str},
};
use serde_json::json;
use std::{
    process::Command,
    str::FromStr,
    time::{SystemTime, UNIX_EPOCH},
};

fn project(source: &str) -> Project {
    Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[{"name":"Main","params":[],"source":source}]}).to_string()).unwrap()
}

#[test]
fn explicit_str_width_and_scale_match_recorded_native_output() {
    // GeneXus oracle: str-trim-native-1792078418337983400/report.json cases 7..9.
    let input = project(
        "Print(\"[\" + Str(1.25,6,2) + \"]\")\nPrint(\"[\" + Str(9.99,3,1) + \"]\")\nPrint(\"[\" + Str(9.99,3,0) + \"]\")",
    );
    let root = std::env::temp_dir().join(format!(
        "gx-str-format-{}-{}",
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
        ["[  1.25]", "[***]", "[ 10]"]
    );
}

#[test]
fn str_matrix_matches_recorded_native_runtime() {
    // Black-box `StringUtil.Str` matrix captured from the GeneXus 18 runtime
    // (~40k-case scans; the rows here are the discriminating small-width
    // cases, the extended Numeric(p,s) grid for widths 9..18, the bound
    // boundary families (exact powers, strict carries, exact halves, 2^53
    // neighborhoods) and exact-power samples, all inside the admitted 1..18
    // subset). The fixture format is the raw probe line: `value|width|scale=[result]`.
    let lines = include_str!("fixtures/str_matrix.tsv");
    let mut total = 0_usize;
    let mut failures = Vec::new();
    for line in lines.lines() {
        let (left, right) = line.split_once("=[").expect(line);
        let expected = right.strip_suffix(']').expect(line);
        let parts: Vec<&str> = left.split('|').collect();
        let value = Decimal::from_str(parts[0]).expect(parts[0]);
        let width: u8 = parts[1].parse().unwrap();
        let scale: u8 = parts[2].parse().unwrap();
        total += 1;
        let got = format_str(value, width, scale).unwrap();
        if got != expected {
            failures.push(format!("{line} engine [{got}]"));
        }
    }
    assert!(total > 6000, "matrix fixture unexpectedly small: {total}");
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

#[test]
fn str_rejects_out_of_subset_width_scale_and_arity_before_rustc() {
    for source in [
        "Print(Str(1,0))",
        "Print(Str(1,19))",
        "Print(Str(1,3,4))",
        "Print(Str(1,3,-1))",
        "Print(Str(1,3,True))",
        "Print(Str(1,3,0,1))",
        "Print(Str(1,3.5))",
    ] {
        assert!(
            gx_engine::compile(&project(source)).is_err(),
            "admitted {source}"
        );
    }
}
