use gx_engine::{build, model::Project};
use serde_json::json;
use std::{fs, process::Command};

fn project(source: &str) -> Result<Project, String> {
    Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[{"name":"Main","params":[],"variables":{"Value":{"kind":"date"},"Other":{"kind":"date"}},"source":source}]}).to_string())
}
#[test]
fn date_source_compiles_and_runs_observed_components_and_arithmetic() {
    let input=project("&Value = YMDToD(2024,2,29)\nPrint(&Value.Year())\nPrint(&Value.Month())\nPrint(&Value.Day())\n&Other = &Value + 1\nPrint(&Other.Month())\nPrint(&Other.Day())\nPrint(&Other - &Value)\n&Other = &Value - 1\nPrint(&Other.Day())\n&Value = YMDToD(2023,2,29)\nPrint(&Value.IsEmpty())\nPrint(&Value.Year())\n&Other = &Value + 1\nPrint(&Other.Year())\nPrint(&Other.Month())\nPrint(&Other.Day())\nPrint(&Value < &Other)\nReturn 0").unwrap();
    let path = std::env::temp_dir().join(format!("gx-date-{}", std::process::id()));
    fs::create_dir(&path).unwrap();
    {
        let artifact = build::build(&input, &path).unwrap();
        let output = Command::new(artifact.executable).output().unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(
            String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
            "2024\n2\n29\n3\n1\n1\n28\ntrue\n0\n1\n1\n2\ntrue\n"
        );
    }
    fs::remove_dir_all(path).unwrap();
}
#[test]
fn date_type_errors_fail_before_rustc() {
    for source in [
        "&Value = 1",
        "&Value = YMDToD(2024,2)",
        "&Value = YMDToD(True,2,1)",
        "&Value = YMDToD(2024,1,1)\nPrint(&Value.Length())",
        "&Value = YMDToD(2024,1,1) * 2",
    ] {
        assert!(
            gx_engine::compile(&project(source).unwrap()).is_err(),
            "accepted: {source}"
        );
    }
}

#[test]
fn date_fixture_matches_all_measured_native_observations() {
    let input = Project::from_json(include_str!("../fixtures/typed-dates.json")).unwrap();
    let output = execute(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        value_level_lines(&output).join("\n") + "\n",
        "2024\n2\n29\n3\n1\n1\n28\n1\n1\n0\n2001\n1949\n1950\n1999\n100\n1\n29\n2000\n2029\n2030\n2039\n1940\n0\n0\n0\n0\n0\n0\n9999\n12\n31\n100\n1\n1\n1\n1\n2\n"
    );
}

/// Observed stdout lines with the probe's value-level alignment: the native
/// date/datetime lanes recorded `.ToString().Trim()` results
/// (`prepare-date-fixture.py` rewrote the probes to `Print`), so picture
/// padding carries no evidence here. The raw padded picture is covered by
/// tests/numeric_render_native.rs.
fn value_level_lines(output: &std::process::Output) -> Vec<String> {
    String::from_utf8_lossy(&output.stdout)
        .replace("\r\n", "\n")
        .lines()
        .map(|line| line.trim().to_string())
        .collect()
}

fn execute(input: &Project) -> std::process::Output {
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(0);
    struct Folder(std::path::PathBuf);
    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    let folder = Folder(std::env::temp_dir().join(format!(
        "gx-date-regression-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )));
    fs::create_dir(&folder.0).unwrap();
    let artifact = build::build(input, &folder.0).unwrap();
    Command::new(artifact.executable).output().unwrap()
}

#[test]
fn dates_cross_typed_helper_boundaries_and_incremental_policy_changes() {
    let mut input=project("&Value = Echo(YMDToD(39,1,1))\nPrint(&Value.Year())\n&Value.SetEmpty()\nPrint(&Value.IsEmpty())\nReturn 0").unwrap();
    input.date_two_digit_year_max = Some(2039);
    input.procedures.push(serde_json::from_value(json!({"name":"Echo","params":["Input"],"variables":{"Input":{"kind":"date"}},"return_type":{"kind":"date"},"source":"Return &Input + 1"})).unwrap());
    let output = execute(&input);
    assert!(output.status.success());
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "2039\ntrue\n"
    );
    let mut compiler = gx_engine::incremental::IncrementalCompiler::new();
    compiler.update(&input).unwrap();
    input.date_two_digit_year_max = Some(2029);
    let next = compiler.update(&input).unwrap();
    assert_eq!(next.reused, ["ECHO", "MAIN"]);
    assert_eq!(next.affected, ["ECHO", "MAIN"]);
    assert_eq!(next.source, gx_engine::compile(&input).unwrap());
    assert_eq!(
        String::from_utf8_lossy(&execute(&input).stdout).replace("\r\n", "\n"),
        "1939\ntrue\n"
    );
    input.date_two_digit_year_max = Some(98);
    assert!(compiler.update(&input).is_err());
    input.date_two_digit_year_max = Some(2029);
    assert!(compiler.update(&input).unwrap().affected.is_empty());
}

#[test]
fn date_runtime_rejects_unsupported_years_fractions_and_overflow() {
    for source in [
        "&Value = YMDToD(-1,1,1)",
        "&Value = YMDToD(10000,1,1)",
        "&Value = YMDToD(39,1,1)",
        "&Value = YMDToD(2024,1.5,1)",
        "&Value = YMDToD(9999,12,31) + 1",
        "&Value.SetEmpty()\n&Value = &Value - 1",
        "&Value = YMDToD(2024,1,1) + 0.5",
    ] {
        let output = execute(&project(source).unwrap());
        assert_eq!(
            output.status.code(),
            Some(70),
            "{source}: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert!(output.stdout.is_empty());
        assert!(String::from_utf8_lossy(&output.stderr).contains("gx-engine runtime error:"));
    }
}

#[test]
fn calendar_methods_compile_with_clamping_and_checked_offsets() {
    let input = project("&Value = YMDToD(2024,1,31).AddMonths(1)\nPrint(&Value.Day())\n&Value = &Value.AddYears(1)\nPrint(&Value.Day())\n&Value.SetEmpty()\n&Value = &Value.AddMonths(1)\nPrint(&Value.Year())\nPrint(&Value.Month())\nPrint(&Value.Day())").unwrap();
    let output = execute(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "29\n28\n1\n2\n1\n"
    );
    for source in [
        "&Value = YMDToD(2024,1,31).AddMonths(True)",
        "&Value = 1.AddYears(1)",
        "&Value = YMDToD(2024,1,31).AddYears()",
    ] {
        assert!(
            gx_engine::compile(&project(source).unwrap()).is_err(),
            "{source}"
        );
    }
    for source in [
        "&Value = YMDToD(9999,1,31).AddYears(1)",
        "&Value = YMDToD(2024,1,31).AddMonths(0.5)",
    ] {
        let output = execute(&project(source).unwrap());
        assert_eq!(output.status.code(), Some(70));
    }
}

#[test]
fn calendar_fixture_matches_eighteen_native_observations() {
    let input = Project::from_json(include_str!("../fixtures/typed-calendar.json")).unwrap();
    let output = execute(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "2024\n2\n29\n2023\n12\n31\n2025\n2\n28\n2020\n2\n29\n1\n2\n1\n2\n1\n1\n"
    );
}

#[test]
fn datetime_fixture_matches_native_second_precision_observations() {
    let input = Project::from_json(include_str!("../fixtures/typed-datetime.json")).unwrap();
    let output = execute(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        value_level_lines(&output).join("\n") + "\n",
        "2024\n2\n29\n23\n59\n58\n3\n1\n0\n1\n3\n1\n0\n0\n1\n12\n1\n1\n2024\n0\n0\n0\n2024\n0\n0\n0\n2024\n0\n0\n0\n2039\n0\n0\n0\n1940\n0\n0\n0\n1\n0\n0\n1\n"
    );
}

#[test]
fn datetime_helpers_and_controlled_errors_are_typed() {
    let mut input = Project::from_json(include_str!("../fixtures/typed-datetime.json")).unwrap();
    input.procedures[0].source = "&DateTimeValue = Echo(YMDHMSToT(2024,2,29,23,59,58))\nPrint(&DateTimeValue.Second())\nPrint(&DateTimeValue < YMDHMSToT(2024,3,2,0,0,0))".into();
    input.procedures.push(serde_json::from_value(json!({"name":"Echo", "params":["Input"], "variables":{"Input":{"kind":"date_time"}}, "return_type":{"kind":"date_time"}, "source":"Return &Input.AddSeconds(3)"})).unwrap());
    let output = execute(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "1\ntrue\n"
    );
    for source in [
        "&DateTimeValue = YMDToD(2024,1,1)",
        "&DateTimeValue = YMDHMSToT(2024,1,1,0,0)",
        "&DateTimeValue.SetEmpty()\nPrint(&DateTimeValue.Difference(1))",
        "&DateTimeValue.SetEmpty()\nPrint(&DateTimeValue.AddSeconds(True))",
        "&DateTimeValue.SetEmpty()\nPrint(&DateTimeValue.Length())",
    ] {
        input.procedures[0].source = source.into();
        assert!(gx_engine::compile(&input).is_err(), "accepted {source}");
    }
    for source in [
        "&DateTimeValue.SetEmpty()\n&DateTimeValue = &DateTimeValue.AddSeconds(-1)",
        "&DateTimeValue = YMDHMSToT(9999,12,31,23,59,59).AddSeconds(1)",
        "&DateTimeValue = YMDHMSToT(2024,1,1,0,0,0).AddSeconds(0.5)",
    ] {
        input.procedures[0].source = source.into();
        let output = execute(&input);
        assert_eq!(output.status.code(), Some(70));
        assert!(output.stdout.is_empty());
    }
}
