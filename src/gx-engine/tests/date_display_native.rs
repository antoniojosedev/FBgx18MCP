use gx_engine::build;
use gx_engine::date::Date;
use gx_engine::datetime::DateTime;
use gx_engine::model::Project;
use serde_json::json;
use std::{fs, process::Command};

const TWO_DIGIT_MAX: u16 = 2039; // measured KBTeste environment policy (typed-dates.json)

#[test]
fn date_and_datetime_display_match_recorded_native_runtime() {
    // `&Date.ToString()` / `&DateTime.ToString()` bytes captured from the
    // GeneXus 18 .NET runtime (`display-native` probes): MM/dd/yy with zero
    // padding and two-digit year modulo 100, datetime adds a 12-hour
    // `hh:mm AM/PM` time with seconds dropped, empty sentinels are
    // `  /  /  ` (Date) and `  /  /   12:00 AM` (DateTime), and an empty date
    // with a valid time keeps the `01/01/01` component override.
    // Fixture format: kind|year|month|day|hour|minute|second|expected.
    let lines = include_str!("fixtures/date_display_native.tsv");
    let mut total = 0_usize;
    let mut failures = Vec::new();
    for line in lines.lines().skip(1) {
        let parts: Vec<&str> = line.split('|').collect();
        assert_eq!(parts.len(), 8, "{line}");
        let expected = parts[7];
        let number = |index: usize| parts[index].parse::<i64>().unwrap();
        total += 1;
        let got = if parts[0] == "date" {
            Date::ymd_with_window(number(1), number(2), number(3), Some(TWO_DIGIT_MAX))
                .unwrap()
                .to_text()
        } else {
            DateTime::from_parts(
                [
                    number(1),
                    number(2),
                    number(3),
                    number(4),
                    number(5),
                    number(6),
                ],
                Some(TWO_DIGIT_MAX),
            )
            .unwrap()
            .to_text()
        };
        if got != expected {
            failures.push(format!("{line} engine [{got}]"));
        }
    }
    assert!(total >= 18, "fixture unexpectedly small: {total}");
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

#[test]
fn print_renders_dates_through_the_measured_picture() {
    // End-to-end admission: Print of a Date/DateTime value (variable or
    // arithmetic result) is the engine counterpart of native `&var.ToString()`
    // and must emit the measured bytes exactly, including the empty sentinels.
    let input = Project::from_json(
        &json!({
            "format_version": 2,
            "date_two_digit_year_max": TWO_DIGIT_MAX,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "DateValue": {"kind": "date"},
                    "DateTimeValue": {"kind": "date_time"}
                },
                "source": "&DateValue = YMDToD(2024, 3, 7)\nPrint(&DateValue)\n&DateValue = YMDToD(0, 0, 0)\nPrint(&DateValue)\n&DateTimeValue = YMDHMSToT(2024, 3, 7, 14, 5, 9)\nPrint(&DateTimeValue)\n&DateTimeValue = YMDHMSToT(0, 0, 0, 14, 5, 9)\nPrint(&DateTimeValue)\n&DateTimeValue = YMDHMSToT(2024, 3, 7, 0, 30, 0)\nPrint(&DateTimeValue)\n&DateValue = YMDToD(1, 1, 1)\nPrint(&DateValue)\nPrint(&DateValue + 1)\n&DateTimeValue = YMDHMSToT(0, 0, 0, 0, 0, 0)\nPrint(&DateTimeValue)\nReturn 0"
            }]
        })
        .to_string(),
    )
    .unwrap();
    let folder = std::env::temp_dir().join(format!("gx-date-display-{}", std::process::id()));
    fs::create_dir_all(&folder).unwrap();
    let artifact = build::build(&input, &folder).unwrap();
    let output = Command::new(artifact.executable).output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "03/07/24\n  /  /  \n03/07/24 02:05 PM\n01/01/01 02:05 PM\n03/07/24 12:30 AM\n01/01/01\n01/02/01\n  /  /   12:00 AM\n"
    );
    fs::remove_dir_all(folder).unwrap();
}
