//! Local Subroutine Control Flow Core Parity tests.
//!
//! Covers:
//! - Sub 'Name' ... EndSub definitions and Do 'Name' invocations
//! - Case-insensitive resolution and quote styles ('Name', "Name", bare identifier)
//! - Procedure-scoped variable mutation across subroutines and main body
//! - Subroutine chaining (SubA calls SubB)
//! - Early Return propagation out of subroutines exiting the entire procedure (matching native returnInSub semantics)
//! - Early Return <value> inside subroutines in value-returning procedures
//! - Subroutines bypassed without sequential fallthrough
//! - Recursion depth limit (128) and execution step limits
//! - Compile-time rejections of undeclared, duplicate, and nested subroutines

use gx_engine::{build, model::Project};
use serde_json::json;
use std::{
    fs,
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

fn run_project(project: &Project) -> Result<Vec<String>, String> {
    struct Folder(std::path::PathBuf);
    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    let folder = Folder(std::env::temp_dir().join(format!(
        "gx-subroutine-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )));
    fs::create_dir(&folder.0).map_err(|e| e.to_string())?;
    let artifact = build::build(project, &folder.0).map_err(|e| e.to_string())?;
    let output = Command::new(artifact.executable)
        .output()
        .map_err(|e| e.to_string())?;
    if !output.status.success() {
        return Err(String::from_utf8_lossy(&output.stderr).to_string());
    }
    Ok(String::from_utf8(output.stdout)
        .unwrap()
        .replace("\r\n", "\n")
        .lines()
        .map(|line| line.trim().to_string())
        .collect())
}

#[test]
fn basic_subroutine_mutates_procedure_scoped_variables() {
    let number = json!({"kind":"numeric","precision":10,"scale":0});
    let text = json!({"kind":"varchar","length":100});
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Count": number,
                    "Msg": text
                },
                "source": r#"
                    &Count = 10
                    &Msg = 'Initial'
                    Do 'Increment'
                    Print(&Count)
                    Print(&Msg)

                    Sub 'Increment'
                        &Count = &Count + 5
                        &Msg = 'Updated in Sub'
                    EndSub
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let output = run_project(&project).unwrap();
    assert_eq!(output, ["15", "Updated in Sub"]);
}

#[test]
fn subroutine_name_resolution_is_case_insensitive_and_supports_all_quote_styles() {
    let number = json!({"kind":"numeric","precision":10,"scale":0});
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Total": number
                },
                "source": r#"
                    &Total = 0
                    Do 'SingleQuote'
                    Do "DoubleQuote"
                    Do BareIdentifier
                    Print(&Total)

                    Sub 'singlequote'
                        &Total = &Total + 1
                    EndSub

                    Sub "DOUBLEQUOTE"
                        &Total = &Total + 10
                    EndSub

                    Sub bareidentifier
                        &Total = &Total + 100
                    EndSub
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let output = run_project(&project).unwrap();
    assert_eq!(output, ["111"]);
}

#[test]
fn subroutines_can_call_other_subroutines_cooperatively() {
    let number = json!({"kind":"numeric","precision":10,"scale":0});
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Val": number
                },
                "source": r#"
                    &Val = 1
                    Do 'SubA'
                    Print(&Val)

                    Sub 'SubA'
                        &Val = &Val * 2
                        Do 'SubB'
                    EndSub

                    Sub 'SubB'
                        &Val = &Val + 10
                        Do 'SubC'
                    EndSub

                    Sub 'SubC'
                        &Val = &Val * 3
                    EndSub
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    // 1 * 2 = 2; 2 + 10 = 12; 12 * 3 = 36
    let output = run_project(&project).unwrap();
    assert_eq!(output, ["36"]);
}

#[test]
fn early_return_in_subroutine_propagates_and_exits_procedure_immediately() {
    let number = json!({"kind":"numeric","precision":10,"scale":0});
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Status": number
                },
                "source": r#"
                    &Status = 1
                    Print(&Status)
                    Do 'CheckExit'
                    &Status = 99
                    Print(&Status)

                    Sub 'CheckExit'
                        &Status = 2
                        Print(&Status)
                        Return
                        &Status = 3
                        Print(&Status)
                    EndSub
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let output = run_project(&project).unwrap();
    // 1 printed in Main, 2 printed in CheckExit, Return exits entire Main, 99 is never reached
    assert_eq!(output, ["1", "2"]);
}

#[test]
fn early_return_in_nested_subroutine_propagates_through_callers() {
    let number = json!({"kind":"numeric","precision":10,"scale":0});
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Step": number
                },
                "source": r#"
                    &Step = 10
                    Print(&Step)
                    Do 'Outer'
                    &Step = 99
                    Print(&Step)

                    Sub 'Outer'
                        &Step = 20
                        Print(&Step)
                        Do 'Inner'
                        &Step = 30
                        Print(&Step)
                    EndSub

                    Sub 'Inner'
                        &Step = 25
                        Print(&Step)
                        Return
                    EndSub
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let output = run_project(&project).unwrap();
    assert_eq!(output, ["10", "20", "25"]);
}

#[test]
fn early_return_with_value_in_subroutine_in_helper_procedure() {
    let number = json!({"kind":"numeric","precision":10,"scale":0});
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [
                {
                    "name": "Main",
                    "params": [],
                    "variables": {
                        "Result": number
                    },
                    "source": r#"
                        &Result = Calc(5)
                        Print(&Result)
                        &Result = Calc(-3)
                        Print(&Result)
                    "#
                },
                {
                    "name": "Calc",
                    "params": ["in:Input"],
                    "return_type": number,
                    "variables": {
                        "Input": number,
                        "Val": number
                    },
                    "source": r#"
                        &Val = &Input
                        Do 'Evaluate'
                        Return &Val * 10

                        Sub 'Evaluate'
                            If &Val < 0
                                Return 999
                            EndIf
                        EndSub
                    "#
                }
            ]
        })
        .to_string(),
    )
    .unwrap();

    let output = run_project(&project).unwrap();
    // When Calc(5): Val = 5, Evaluate does not Return, helper returns 5 * 10 = 50.
    // When Calc(-3): Val = -3, Evaluate hits Return 999, which returns 999 immediately.
    assert_eq!(output, ["50", "999"]);
}

#[test]
fn subroutines_are_not_executed_on_fallthrough() {
    let number = json!({"kind":"numeric","precision":10,"scale":0});
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "X": number
                },
                "source": r#"
                    &X = 1
                    Print(&X)

                    Sub 'UncalledSub'
                        &X = 999
                        Print(&X)
                    EndSub
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let output = run_project(&project).unwrap();
    // 'UncalledSub' is never called, so output is only ["1"]
    assert_eq!(output, ["1"]);
}

#[test]
fn subroutine_recursion_depth_is_bounded_and_fails_safely() {
    let number = json!({"kind":"numeric","precision":10,"scale":0});
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {
                    "Counter": number
                },
                "source": r#"
                    &Counter = 0
                    Do 'InfiniteRecursion'

                    Sub 'InfiniteRecursion'
                        &Counter = &Counter + 1
                        Do 'InfiniteRecursion'
                    EndSub
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let err = run_project(&project).unwrap_err();
    assert!(
        err.contains("subroutine call depth limit exceeded")
            || err.contains("execution step limit exceeded"),
        "unexpected error message: {err}"
    );
}

#[test]
fn compile_time_rejects_unknown_subroutine() {
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {},
                "source": r#"
                    Do 'GhostSub'
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let err = gx_engine::compile(&project).unwrap_err().to_string();
    assert!(
        err.contains("undefined subroutine 'GHOSTSUB'"),
        "error was: {err}"
    );
}

#[test]
fn compile_time_rejects_duplicate_subroutines() {
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {},
                "source": r#"
                    Do 'SubA'

                    Sub 'SubA'
                        Print(1)
                    EndSub

                    Sub 'suba'
                        Print(2)
                    EndSub
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let err = gx_engine::compile(&project).unwrap_err().to_string();
    assert!(
        err.contains("duplicate subroutine definition 'SUBA'"),
        "error was: {err}"
    );
}

#[test]
fn compile_time_rejects_nested_subroutine_definitions() {
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {},
                "source": r#"
                    Sub 'Outer'
                        Sub 'Inner'
                        EndSub
                    EndSub
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let err = gx_engine::compile(&project).unwrap_err().to_string();
    assert!(
        err.contains("subroutines cannot be nested"),
        "error was: {err}"
    );
}

#[test]
fn format_version_1_rejects_subroutines() {
    let project = Project::from_json(
        &json!({
            "format_version": 1,
            "entry": "Main",
            "procedures": [{
                "name": "Main",
                "params": [],
                "variables": {},
                "source": r#"
                    Do 'SubA'
                    Sub 'SubA'
                    EndSub
                "#
            }]
        })
        .to_string(),
    )
    .unwrap();

    let err = gx_engine::compile(&project).unwrap_err().to_string();
    assert!(
        err.contains("subroutines require format_version 2"),
        "error was: {err}"
    );
}

#[test]
fn subroutines_support_loops_collections_and_procedure_calls() {
    let number = json!({"kind":"numeric","precision":10,"scale":0});
    let item_rec = json!({"kind":"record","schema":"Item"});
    let item_col = json!({"kind":"collection","schema":"Item"});
    let project = Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "record_types": [{
                "name": "Item",
                "fields": {
                    "Amount": number
                }
            }],
            "procedures": [
                {
                    "name": "Main",
                    "params": [],
                    "variables": {
                        "Items": item_col,
                        "It": item_rec,
                        "I": number,
                        "Sum": number
                    },
                    "source": r#"
                        Do 'Populate'
                        Do 'Aggregate'
                        Print(&Sum)

                        Sub 'Populate'
                            For &I = 1 To 4 Step 1
                                &It = New()
                                &It.Amount = DoubleVal(&I)
                                &Items.Add(&It.Clone())
                            EndFor
                        EndSub

                        Sub 'Aggregate'
                            &Sum = 0
                            For &It In &Items
                                &Sum = &Sum + &It.Amount
                            EndFor
                        EndSub
                    "#
                },
                {
                    "name": "DoubleVal",
                    "params": ["in:X"],
                    "return_type": number,
                    "variables": {
                        "X": number
                    },
                    "source": "Return &X * 2"
                }
            ]
        })
        .to_string(),
    )
    .unwrap();

    // 2 + 4 + 6 + 8 = 20
    let output = run_project(&project).unwrap();
    assert_eq!(output, ["20"]);
}
