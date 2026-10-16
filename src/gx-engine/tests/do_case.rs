use gx_engine::{
    build,
    frontend::{self, BinOp, Expr, Stmt},
    model::Project,
};
use serde_json::json;
use std::{
    fs,
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

struct Scratch(std::path::PathBuf);

impl Scratch {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "gx-do-case-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn project(main: &str) -> Project {
    Project::from_json(
        &json!({
            "format_version": 2,
            "entry": "Main",
            "procedures": [
                {
                    "name": "Main",
                    "params": [],
                    "variables": {"Flag": {"kind": "boolean"}},
                    "source": main
                },
                {"name": "First", "params": [], "source": "Print(1)"},
                {"name": "Second", "params": [], "source": "Print(2)"},
                {"name": "Fallback", "params": [], "source": "Print(3)"},
                {"name": "Nested", "params": [], "source": "Print(4)"}
            ]
        })
        .to_string(),
    )
    .unwrap()
}

fn run(input: &Project) -> std::process::Output {
    let scratch = Scratch::new();
    let artifact = build::build(input, &scratch.0).unwrap();
    Command::new(artifact.executable).output().unwrap()
}

#[test]
fn do_case_lowers_in_order_to_nested_if_statements() {
    let body = frontend::parse_body(
        "Do Case\nCase False\nPrint(1)\nCase True\nPrint(2)\nOtherwise\nPrint(3)\nEndCase",
    )
    .unwrap();
    assert_eq!(
        body,
        vec![Stmt::If(
            Expr::Boolean(false),
            vec![Stmt::Print(Expr::Integer(1))],
            vec![Stmt::If(
                Expr::Boolean(true),
                vec![Stmt::Print(Expr::Integer(2))],
                vec![Stmt::Print(Expr::Integer(3))],
            )],
        )]
    );
}

#[test]
fn nested_cases_run_only_the_first_matching_branch_and_allow_call_statements() {
    let input = project(
        "Do Case\nCase True\nDo Case\nCase False\nFallback()\nCase True\nNested()\nOtherwise\nFallback()\nEndCase\nCase True\nSecond()\nOtherwise\nFallback()\nEndCase",
    );
    let output = run(&input);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "4\n"
    );
}

#[test]
fn do_case_conditions_must_be_typed_boolean() {
    let input = project("Do Case\nCase 1\nFirst()\nEndCase");
    assert!(gx_engine::compile(&input).is_err());
}

#[test]
fn rejects_malformed_case_sequences_and_missing_endcase() {
    for (source, expected) in [
        ("Do Case\nEndCase", "at least one Case"),
        ("Do Case\nCase\nEndCase", "expected an integer"),
        (
            "Do Case\nCase True\nFirst()",
            "expected Case, Otherwise, or EndCase",
        ),
        (
            "Do Case\nOtherwise\nFirst()\nEndCase",
            "Otherwise must follow a Case",
        ),
        (
            "Do Case\nCase True\nFirst()\nOtherwise\nSecond()\nCase False\nEndCase",
            "expected EndCase after Otherwise",
        ),
        (
            "Do Case\nCase True\nFirst()\nOtherwise\nSecond()\nOtherwise\nEndCase",
            "expected EndCase after Otherwise",
        ),
    ] {
        let error = frontend::parse_body(source).unwrap_err();
        assert!(error.contains(expected), "{source:?}: {error}");
        assert!(
            error.contains("line ") && error.contains("column "),
            "{error}"
        );
    }
}

#[test]
fn do_case_respects_the_lowered_block_nesting_limit() {
    for depth in [127, 128] {
        let mut source = String::new();
        for _ in 0..depth {
            source.push_str("Do Case\nCase True\n");
        }
        for _ in 0..depth {
            source.push_str("EndCase\n");
        }

        let parsed = frontend::parse_body(&source);
        if depth == 127 {
            assert!(parsed.is_ok(), "{:#?}", parsed.err());
        } else {
            let error = parsed.unwrap_err();
            assert!(
                error.contains("block nesting exceeds the 128-level limit"),
                "{error}"
            );
        }
    }
}

#[test]
fn case_condition_uses_the_existing_boolean_expression_ast() {
    let body = frontend::parse_body("Do Case\nCase True And Not False\nFirst()\nEndCase").unwrap();
    let [Stmt::If(condition, then_body, otherwise)] = body.as_slice() else {
        panic!("expected a single lowered If statement: {body:#?}");
    };
    assert!(matches!(condition, Expr::Binary(_, BinOp::And, _)));
    assert!(matches!(
        then_body.as_slice(),
        [Stmt::Call(Expr::Call(name, args))] if name == "FIRST" && args.is_empty()
    ));
    assert!(otherwise.is_empty());
}
