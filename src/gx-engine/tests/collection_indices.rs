use gx_engine::{
    build,
    model::{DataType, Project},
};
use std::{
    path::PathBuf,
    process::{Command, Output},
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

fn project(source: &str) -> Project {
    let mut input =
        Project::from_json(include_str!("../fixtures/typed-record-collection.json")).unwrap();
    input.procedures[0].source = source.into();
    input.procedures[0].variables.insert(
        "I".into(),
        DataType::Numeric {
            precision: 5,
            scale: 0,
        },
    );
    input
}

fn execute(input: &Project) -> Output {
    struct Folder(PathBuf);
    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let root = Folder(std::env::temp_dir().join(format!(
        "gx-index-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )));
    let artifact = build::build(input, &root.0).unwrap();
    Command::new(&artifact.executable).output().unwrap()
}

#[test]
fn numeric_indices_allow_for_iteration_and_member_updates() {
    let output = execute(&project(
        "&First = new()\n&Row = new()\n&Row.Amount = 7\n&First.Rows.Add(&Row)\n&Row = new()\n&Row.Amount = 9\n&First.Rows.Add(&Row)\nFor &I = 1 To &First.Rows.Count\nPrint(&First.Rows.Item(&I).Amount)\nEndFor\n&First.Rows.Item(&I - 1).Amount = 11\nPrint(&First.Rows.Item(2).Amount)",
    ));
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
        ["7", "9", "11"]
    );
}

#[test]
fn computed_indices_reject_nonpositive_and_out_of_range_values() {
    for (index, expected) in [("&I", "index"), ("&I - 1", "index"), ("&I + 1", "index")] {
        let output = execute(&project(&format!(
            "&First = new()\n&I = 0\n&Row = &First.Rows.Item({index})"
        )));
        assert_eq!(output.status.code(), Some(70), "index {index}");
        assert!(
            String::from_utf8_lossy(&output.stderr).contains(expected),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
    }
    assert!(gx_engine::compile(&project("&First = new()\n&Row = &First.Rows.Item(True)")).is_err());
}

#[test]
fn fractional_indices_truncate_toward_zero_like_native() {
    let mut input = project(
        "&First = new()\n&Row = new()\n&First.Rows.Add(&Row)\n&First.Rows.Item(1).Amount = 7\n&Row = new()\n&First.Rows.Add(&Row)\n&First.Rows.Item(2).Amount = 9\n&F = 1.9\nPrint(&First.Rows.Item(&F).Amount)\nPrint(&First.Rows.Item(2.7).Amount)\nPrint(&First.Rows.Item(1.5).Amount)",
    );
    input.procedures[0].variables.insert(
        "F".into(),
        DataType::Numeric {
            precision: 5,
            scale: 1,
        },
    );
    let output = execute(&input);
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
        ["7", "9", "7"]
    );

    for fraction in ["0.9", "-0.5"] {
        let mut failing = project(&format!(
            "&First = new()\n&Row = new()\n&First.Rows.Add(&Row)\n&F = {fraction}\n&Row = &First.Rows.Item(&F)"
        ));
        failing.procedures[0].variables.insert(
            "F".into(),
            DataType::Numeric {
                precision: 5,
                scale: 1,
            },
        );
        let output = execute(&failing);
        assert_eq!(output.status.code(), Some(70), "fraction {fraction}");
        assert!(
            String::from_utf8_lossy(&output.stderr).contains("index"),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
    }
}
