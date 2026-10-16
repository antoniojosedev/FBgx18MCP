use gx_engine::{build::build, datetime::DateTime, model::Project};
use serde_json::json;
use std::{
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

fn parse_dt(s: &str) -> DateTime {
    if s == "empty" {
        return DateTime::empty();
    }
    if s.contains('_') {
        let (d_part, t_part) = s.split_once('_').unwrap();
        let d_nums: Vec<i64> = d_part.split('-').map(|v| v.parse().unwrap()).collect();
        let t_nums: Vec<i64> = t_part.split(':').map(|v| v.parse().unwrap()).collect();
        DateTime::from_parts(
            [
                d_nums[0], d_nums[1], d_nums[2], t_nums[0], t_nums[1], t_nums[2],
            ],
            None,
        )
        .unwrap()
    } else {
        let d_nums: Vec<i64> = s.split('-').map(|v| v.parse().unwrap()).collect();
        DateTime::from_parts([d_nums[0], d_nums[1], d_nums[2], 0, 0, 0], None).unwrap()
    }
}

fn fmt_dt(dt: DateTime) -> String {
    if dt.is_empty() {
        "empty".to_string()
    } else {
        format!(
            "{:04}-{:02}-{:02}_{:02}:{:02}:{:02}",
            dt.year(),
            dt.month(),
            dt.day(),
            dt.hour(),
            dt.minute(),
            dt.second()
        )
    }
}

#[test]
fn calendar_ops_match_recorded_native_runtime() {
    let fixture = include_str!("fixtures/calendar_ops_native.tsv");
    let mut total = 0_usize;
    let mut failures = Vec::new();

    for line in fixture.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() || trimmed.starts_with('#') {
            continue;
        }
        total += 1;
        let (left, right) = trimmed.split_once("=[").expect(line);
        let expected = right.strip_suffix(']').expect(line);
        let parts: Vec<&str> = left.split('|').collect();

        match parts[0] {
            "day" => {
                let dt = parse_dt(parts[1]);
                let got = dt.day().to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "month" => {
                let dt = parse_dt(parts[1]);
                let got = dt.month().to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "year" => {
                let dt = parse_dt(parts[1]);
                let got = dt.year().to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "hour" => {
                let dt = parse_dt(parts[1]);
                let got = dt.hour().to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "minute" => {
                let dt = parse_dt(parts[1]);
                let got = dt.minute().to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "second" => {
                let dt = parse_dt(parts[1]);
                let got = dt.second().to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "dow" => {
                let dt = parse_dt(parts[1]);
                let got = dt.day_of_week().to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "addyr" => {
                let dt = parse_dt(parts[1]);
                let n: i64 = parts[2].parse().unwrap();
                let res = dt.add_years(n).unwrap_or_else(|_| DateTime::empty());
                let got = fmt_dt(res);
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "addmth" => {
                let dt = parse_dt(parts[1]);
                let n: i64 = parts[2].parse().unwrap();
                let res = dt.add_months(n).unwrap_or_else(|_| DateTime::empty());
                let got = fmt_dt(res);
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "dadd" => {
                let dt = parse_dt(parts[1]);
                let n: i64 = parts[2].parse().unwrap();
                let res = dt.add_days(n).unwrap_or_else(|_| DateTime::empty());
                let got = fmt_dt(res);
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "tadd" => {
                let dt = parse_dt(parts[1]);
                let s: i64 = parts[2].parse().unwrap();
                let res = dt.add_seconds(s).unwrap_or_else(|_| DateTime::empty());
                let got = fmt_dt(res);
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "ddiff" => {
                let dt1 = parse_dt(parts[1]);
                let dt2 = parse_dt(parts[2]);
                let got = (dt1.difference(dt2) / 86400).to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "tdiff" => {
                let dt1 = parse_dt(parts[1]);
                let dt2 = parse_dt(parts[2]);
                let got = dt1.difference(dt2).to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "age" => {
                let birth = parse_dt(parts[1]);
                let current = parse_dt(parts[2]);
                let got = birth.age(current).to_string();
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "endofmonth" => {
                let dt = parse_dt(parts[1]);
                let got = fmt_dt(dt.end_of_month());
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            "resettime" => {
                let dt = parse_dt(parts[1]);
                let got = fmt_dt(dt.reset_time());
                if got != expected {
                    failures.push(format!("{line} => got [{got}] expected [{expected}]"));
                }
            }
            other => panic!("unknown test case: {other}"),
        }
    }

    assert_eq!(total, 83, "all 83 oracle rows checked");
    assert!(
        failures.is_empty(),
        "{} failures out of {total}:\n{}",
        failures.len(),
        failures.join("\n")
    );
}

#[test]
fn calendar_ops_execute_in_procedure_pipeline() {
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let scratch_dir = std::env::temp_dir().join(format!("gx_calendar_pipeline_{nonce}"));
    std::fs::create_dir_all(&scratch_dir).unwrap();

    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Dt": { "kind": "date_time" },
                    "D": { "kind": "date" },
                    "Dt2": { "kind": "date_time" },
                    "D2": { "kind": "date" },
                    "Num": { "kind": "numeric", "precision": 10, "scale": 0 }
                },
                "source": r#"
                    &Dt = YMDHMSToT(2024, 2, 29, 14, 30, 45)
                    &D = YMDToD(2024, 2, 29)
                    
                    // Function calls
                    &Num = Day(&Dt)
                    msg("day_fn=" + &Num.ToString(), status)
                    &Num = Month(&Dt)
                    msg("month_fn=" + &Num.ToString(), status)
                    &Num = Year(&Dt)
                    msg("year_fn=" + &Num.ToString(), status)
                    &Num = Hour(&Dt)
                    msg("hour_fn=" + &Num.ToString(), status)
                    &Num = Minute(&Dt)
                    msg("minute_fn=" + &Num.ToString(), status)
                    &Num = Sec(&Dt)
                    msg("second_fn=" + &Num.ToString(), status)
                    &Num = Dow(&Dt)
                    msg("dow_fn=" + &Num.ToString(), status)

                    // Method calls on Date & DateTime
                    &Num = &Dt.DayOfWeek()
                    msg("dow_method=" + &Num.ToString(), status)
                    &Num = &D.Dow()
                    msg("d_dow_method=" + &Num.ToString(), status)

                    // EndOfMonth and ResetTime
                    &Dt2 = EndOfMonth(&Dt)
                    msg("eom_day=" + &Dt2.Day().ToString(), status)
                    &Dt2 = ResetTime(&Dt)
                    msg("reset_hour=" + &Dt2.Hour().ToString(), status)

                    // AddYr, AddMth, AddDays, TAdd
                    &Dt2 = AddYr(&Dt, 1)
                    msg("addyr_day=" + &Dt2.Day().ToString(), status)
                    &Dt2 = AddMth(&Dt, 1)
                    msg("addmth_month=" + &Dt2.Month().ToString(), status)
                    &Dt2 = DAdd(&Dt, 2)
                    msg("dadd_month=" + &Dt2.Month().ToString(), status)
                    &Dt2 = TAdd(&Dt, 60)
                    msg("tadd_minute=" + &Dt2.Minute().ToString(), status)

                    // Methods: AddYears, AddMonths, AddDays, AddHours, AddMinutes, AddSeconds
                    &Dt2 = &Dt.AddYears(1)
                    msg("addyears_day=" + &Dt2.Day().ToString(), status)
                    &Dt2 = &Dt.AddMonths(1)
                    msg("addmonths_month=" + &Dt2.Month().ToString(), status)
                    &Dt2 = &Dt.AddDays(2)
                    msg("adddays_month=" + &Dt2.Month().ToString(), status)
                    &Dt2 = &Dt.AddHours(2)
                    msg("addhours_hour=" + &Dt2.Hour().ToString(), status)
                    &Dt2 = &Dt.AddMinutes(15)
                    msg("addminutes_min=" + &Dt2.Minute().ToString(), status)
                    &Dt2 = &Dt.AddSeconds(30)
                    msg("addsec_sec=" + &Dt2.Second().ToString(), status)

                    // DDiff, TDiff, Age
                    &D2 = YMDToD(2024, 2, 28)
                    &Num = DDiff(&D, &D2)
                    msg("ddiff=" + &Num.ToString(), status)
                    &Num = &Dt.Difference(&Dt2)
                    msg("diff_has_val=" + (&Num > 0).ToString(), status)
                    
                    &D2 = YMDToD(2000, 5, 15)
                    &D = YMDToD(2024, 5, 15)
                    &Num = Age(&D2, &D)
                    msg("age_fn=" + &Num.ToString(), status)
                    &Num = &D2.Age(&D)
                    msg("age_method=" + &Num.ToString(), status)
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let artifact = build(&project, &scratch_dir).expect("build succeeds");

    let output = Command::new(&artifact.executable)
        .output()
        .expect("run built binary");
    assert!(output.status.success(), "binary ran successfully");

    let stdout = String::from_utf8_lossy(&output.stdout);
    let lines: Vec<&str> = stdout.lines().collect();

    assert!(lines.contains(&"day_fn=29"));
    assert!(lines.contains(&"month_fn=2"));
    assert!(lines.contains(&"year_fn=2024"));
    assert!(lines.contains(&"hour_fn=14"));
    assert!(lines.contains(&"minute_fn=30"));
    assert!(lines.contains(&"second_fn=45"));
    assert!(lines.contains(&"dow_fn=5"));
    assert!(lines.contains(&"dow_method=5"));
    assert!(lines.contains(&"d_dow_method=5"));
    assert!(lines.contains(&"eom_day=29"));
    assert!(lines.contains(&"reset_hour=0"));
    assert!(lines.contains(&"addyr_day=28")); // 2024-02-29 + 1 yr = 2025-02-28
    assert!(lines.contains(&"addmth_month=3"));
    assert!(lines.contains(&"dadd_month=3"));
    assert!(lines.contains(&"tadd_minute=31"));
    assert!(lines.contains(&"addyears_day=28"));
    assert!(lines.contains(&"addmonths_month=3"));
    assert!(lines.contains(&"adddays_month=3"));
    assert!(lines.contains(&"addhours_hour=16"));
    assert!(lines.contains(&"addminutes_min=45"));
    assert!(lines.contains(&"addsec_sec=15"));
    assert!(lines.contains(&"ddiff=1"));
    assert!(lines.contains(&"age_fn=24"));
    assert!(lines.contains(&"age_method=24"));

    let _ = std::fs::remove_dir_all(&scratch_dir);
}

#[test]
fn calendar_ops_fail_closed_on_invalid_types_and_arguments() {
    // 1. Calling Day with non-Date/DateTime
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Txt": { "kind": "varchar", "length": 20 },
                    "Num": { "kind": "numeric", "precision": 10, "scale": 0 }
                },
                "source": r#"
                    &Txt = "not a date"
                    &Num = Day(&Txt)
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();
    let bin = std::env::temp_dir().join("fail_cal_1.exe");
    assert!(build(&project, &bin).is_err());

    // 2. Calling DDiff with mismatched types (Date and DateTime)
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "D": { "kind": "date" },
                    "Dt": { "kind": "date_time" },
                    "Num": { "kind": "numeric", "precision": 10, "scale": 0 }
                },
                "source": r#"
                    &D = YMDToD(2024, 1, 1)
                    &Dt = YMDHMSToT(2024, 1, 1, 0, 0, 0)
                    &Num = DDiff(&D, &Dt)
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();
    assert!(build(&project, &bin).is_err());

    // 3. TAdd with Date instead of DateTime
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "D": { "kind": "date" },
                    "Dt": { "kind": "date_time" }
                },
                "source": r#"
                    &D = YMDToD(2024, 1, 1)
                    &Dt = TAdd(&D, 60)
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();
    assert!(build(&project, &bin).is_err());
}
