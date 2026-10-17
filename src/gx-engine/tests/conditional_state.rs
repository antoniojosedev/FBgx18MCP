//! Differential and end-to-end tests for conditional expressions (`Iif`), universal
//! empty/null state operations (`IsEmpty`, `SetEmpty`, `Nullvalue`), `.UDP` calls,
//! and `StringUtil` formatting/search/conversion builtins (`Format`, `NewLine`,
//! `Replicate`, `CharAt`, `StrSearchRev`/`LastIndexOf`, `BoolToStr`, `ToBoolean`/`StrToBool`).

use gx_engine::build;
use gx_engine::model::Project;
use gx_engine::text::Text;
use gx_engine::types::Decimal;
use serde_json::json;
use std::{
    fs,
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

const NATIVE_CORPUS: &str = include_str!("fixtures/conditional_state_native.tsv");
static NEXT: AtomicU64 = AtomicU64::new(0);

fn unescape(value: &str) -> String {
    let mut result = String::with_capacity(value.len());
    let mut chars = value.chars();
    while let Some(ch) = chars.next() {
        if ch == '\\' {
            match chars.next() {
                Some('t') => result.push('\t'),
                Some('n') => result.push('\n'),
                Some('r') => result.push('\r'),
                Some(other) => {
                    result.push('\\');
                    result.push(other);
                }
                None => result.push('\\'),
            }
        } else {
            result.push(ch);
        }
    }
    result
}

fn run_project(json_val: serde_json::Value) -> Vec<String> {
    struct Folder(std::path::PathBuf);
    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    let project = Project::from_json(&json_val.to_string()).expect("valid project");
    let folder = Folder(std::env::temp_dir().join(format!(
        "gx-cond-state-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )));
    fs::create_dir(&folder.0).expect("temp dir");
    let artifact = build::build(&project, &folder.0).expect("rustc succeeds");
    let output = Command::new(artifact.executable).output().expect("runs");
    assert!(
        output.status.success(),
        "stderr: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout)
        .unwrap()
        .replace("\r\n", "\n")
        .lines()
        .map(|l| l.trim().to_owned())
        .collect()
}

#[test]
fn conditional_state_matches_recorded_native_runtime() {
    let mut rows = 0usize;
    for line in NATIVE_CORPUS.lines().filter(|l| !l.trim().is_empty()) {
        rows += 1;
        let (left, right) = line.split_once("=[").expect("row has =[");
        let expected = unescape(right.strip_suffix(']').expect("row ends with ]"));
        let parts: Vec<&str> = left.split('|').collect();
        match parts[0] {
            "format" => {
                let pattern = Text::new(&unescape(parts[1])).unwrap();
                let args: Vec<Text> = if parts[2].is_empty() {
                    Vec::new()
                } else {
                    parts[2]
                        .split(',')
                        .map(|s| Text::new(&unescape(s)).unwrap())
                        .collect()
                };
                let actual = Text::format_template(&pattern, &args).unwrap();
                assert_eq!(actual.to_string(), expected, "failed on row: {line}");
            }
            "replicate" => {
                let text = Text::new(&unescape(parts[1])).unwrap();
                let count: i64 = parts[2].parse().unwrap();
                let actual = text.replicate(count).unwrap();
                assert_eq!(actual.to_string(), expected, "failed on row: {line}");
            }
            "charat" => {
                let text = Text::new(&unescape(parts[1])).unwrap();
                let idx: i64 = parts[2].parse().unwrap();
                let actual = text.char_at(idx);
                assert_eq!(actual.to_string(), expected, "failed on row: {line}");
            }
            "strsearchrev" => {
                let s1 = Text::new(&unescape(parts[1])).unwrap();
                let s2 = Text::new(&unescape(parts[2])).unwrap();
                let start: i64 = parts[3].parse().unwrap();
                let actual = s1.str_search_rev(&s2, start);
                assert_eq!(actual.to_string(), expected, "failed on row: {line}");
            }
            "booltostr" => {
                let b: bool = parts[1].parse().unwrap();
                let actual = Text::bool_to_str(b);
                assert_eq!(actual.to_string(), expected, "failed on row: {line}");
            }
            "strtobool" => {
                let s = Text::new(&unescape(parts[1])).unwrap();
                let actual = s.to_boolean();
                assert_eq!(actual.to_string(), expected, "failed on row: {line}");
            }
            "newline" => {
                let actual = Text::newline();
                assert_eq!(actual.to_string(), expected, "failed on row: {line}");
            }
            "isempty_text" => {
                let s = Text::new(&unescape(parts[1])).unwrap();
                let actual = s.trim_end_spaces().utf16_length() == 0;
                assert_eq!(actual.to_string(), expected, "failed on row: {line}");
            }
            "isempty_num" => {
                let n: Decimal = parts[1].parse().unwrap();
                let zero: Decimal = "0".parse().unwrap();
                let actual = n.checked_cmp(zero).unwrap().is_eq();
                assert_eq!(actual.to_string(), expected, "failed on row: {line}");
            }
            "isempty_bool" => {
                let b: bool = parts[1].parse().unwrap();
                let actual = !b;
                assert_eq!(actual.to_string(), expected, "failed on row: {line}");
            }
            other => panic!("unexpected op {other}"),
        }
    }
    assert_eq!(rows, 57, "expected all 57 native oracle rows");
}

#[test]
fn iif_short_circuits_and_supports_all_scalar_types() {
    let lines = run_project(json!({
        "format_version": 2,
        "entry": "Main",
        "procedures": [{
            "name": "Main",
            "params": [],
            "variables": {
                "Num": {"kind": "numeric", "precision": 10, "scale": 2},
                "Den": {"kind": "numeric", "precision": 10, "scale": 2},
                "Res": {"kind": "numeric", "precision": 10, "scale": 2},
                "Label": {"kind": "varchar", "length": 60},
                "Flag": {"kind": "boolean"},
                "D": {"kind": "date"}
            },
            "source": "
&Num = 100
&Den = 0
&Res = Iif(&Den <> 0, &Num / &Den, 42.50)
Print(&Res)
&Den = 4
&Res = Iif(&Den <> 0, &Num / &Den, 42.50)
Print(&Res)
&Label = Iif(&Res > 20, Format('High: %1', &Res), 'Low')
Print(&Label)
&Flag = Iif(StrToBool('TRUE') and not IsEmpty(&Label), True, False)
Print(BoolToStr(&Flag))
&D = Iif(&Flag, YMDToD(2024, 2, 29), Nullvalue(&D))
Print(&D)
Return 0
"
        }]
    }));
    assert_eq!(
        lines,
        vec!["42.50", "25.00", "High: 25", "true", "02/29/24"]
    );
}

#[test]
fn universal_is_empty_set_empty_and_nullvalue_work_across_all_types() {
    let lines = run_project(json!({
        "format_version": 2,
        "entry": "Main",
        "record_types": [{
            "name": "Item",
            "fields": {
                "Code": {"kind": "numeric", "precision": 6, "scale": 0},
                "Title": {"kind": "varchar", "length": 40},
                "Active": {"kind": "boolean"},
                "Created": {"kind": "date"}
            }
        }],
        "procedures": [{
            "name": "Main",
            "params": [],
            "variables": {
                "N": {"kind": "numeric", "precision": 6, "scale": 0},
                "S": {"kind": "varchar", "length": 40},
                "B": {"kind": "boolean"},
                "D": {"kind": "date"},
                "DT": {"kind": "date_time"},
                "Rec": {"kind": "record", "schema": "Item"},
                "Col": {"kind": "collection", "schema": "Item"}
            },
            "source": "
&N = Nullvalue(&N)
&S = Nullvalue(&S)
&B = Nullvalue(&B)
&D = Nullvalue(&D)
&DT = Nullvalue(&DT)
Print(Format('%1|%2|%3|%4|%5', &N.IsEmpty(), &S.IsEmpty(), &B.IsEmpty(), &D.IsEmpty(), IsEmpty(&DT)))

&N = 99
&S = '   '
Print(Format('spaces_empty=%1,num_empty=%2', &S.IsEmpty(), IsEmpty(&N)))
&S = 'Gx'
Print(&S.IsEmpty())
&N.SetEmpty()
&S.SetEmpty()
&B = True
&B.SetEmpty()
Print(Format('after_setempty=%1|%2|%3', &N.IsEmpty(), &S.IsEmpty(), &B.IsEmpty()))

&Rec = New()
&Rec.Code = 10
&Rec.Title = 'Widget'
&Rec.Active = True
&Rec.Created = YMDToD(2024, 5, 1)
&Col = New()
&Col.Add(&Rec)
Print(Format('col_empty_before=%1,count=%2', &Col.IsEmpty(), &Col.Count))

&Rec.Code.SetEmpty()
&Rec.Title.SetEmpty()
&Rec.Active.SetEmpty()
&Rec.Created.SetEmpty()
Print(Format('rec_fields_empty=%1|%2|%3|%4', &Rec.Code.IsEmpty(), &Rec.Title.IsEmpty(), &Rec.Active.IsEmpty(), &Rec.Created.IsEmpty()))

&Col.SetEmpty()
Print(Format('col_empty_after=%1,count=%2', IsEmpty(&Col), &Col.Count))
Return 0
"
        }]
    }));
    assert_eq!(
        lines,
        vec![
            "true|true|true|true|true",
            "spaces_empty=true,num_empty=false",
            "false",
            "after_setempty=true|true|true",
            "col_empty_before=false,count=1",
            "rec_fields_empty=true|true|true|true",
            "col_empty_after=true,count=0"
        ]
    );
}

#[test]
fn udp_calls_and_string_utilities_execute_end_to_end() {
    let lines = run_project(json!({
        "format_version": 2,
        "entry": "Main",
        "procedures": [
            {
                "name": "Main",
                "params": [],
                "variables": {
                    "Out": {"kind": "numeric", "precision": 8, "scale": 0},
                    "Txt": {"kind": "varchar", "length": 60},
                    "HasCrLf": {"kind": "boolean"}
                },
                "source": "
&Out = CalcDouble.UDP(21)
Print(&Out)
&Txt = Replicate('ab', 3)
Print(Format('%1|char2=%2|rev=%3|rev4=%4', &Txt, &Txt.CharAt(2), StrSearchRev(&Txt, 'ab'), &Txt.LastIndexOf('ab', 4)))
&HasCrLf = NewLine().Length() = 2 and Asc(CharAt(NewLine(), 1)) = 13 and Asc(CharAt(NewLine(), 2)) = 10
Print(Format('crlf=%1|tobool=%2', &HasCrLf, 'True'.ToBoolean()))
Return 0
"
            },
            {
                "name": "CalcDouble",
                "params": ["in:InVal", "out:OutVal"],
                "variables": {
                    "InVal": {"kind": "numeric", "precision": 8, "scale": 0},
                    "OutVal": {"kind": "numeric", "precision": 8, "scale": 0}
                },
                "source": "
&OutVal = &InVal * 2
Return 0
"
            }
        ]
    }));
    assert_eq!(
        lines,
        vec!["42", "ababab|char2=b|rev=5|rev4=3", "crlf=true|tobool=true"]
    );
}

#[test]
fn conditional_and_state_ops_fail_closed_on_invalid_usage() {
    let cases = [
        // Iif with mismatched branch types
        "Print(Iif(True, 10, 'ten'))\nReturn 0",
        // Iif with wrong arity
        "Print(Iif(True, 10))\nReturn 0",
        // Nullvalue on literal
        "Print(Nullvalue(10))\nReturn 0",
        // Format with 0 args
        "Print(Format())\nReturn 0",
        // NewLine with arguments
        "Print(NewLine('x'))\nReturn 0",
    ];
    for source in cases {
        let project = Project::from_json(
            &json!({
                "format_version": 2,
                "entry": "Main",
                "procedures": [{
                    "name": "Main",
                    "params": [],
                    "variables": {},
                    "source": source
                }]
            })
            .to_string(),
        )
        .unwrap();
        assert!(
            gx_engine::compile(&project).is_err(),
            "expected error for source: {source}"
        );
    }
}
