use gx_engine::{build, model::Project};
use serde_json::json;
use std::{
    path::PathBuf,
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

fn project(source: &str) -> Project {
    Project::from_json(&json!({"format_version":2,"entry":"Main","procedures":[{"name":"Main","params":[],"variables":{"Fixed":{"kind":"character","length":6},"Text":{"kind":"varchar","length":20}},"source":source}]}).to_string()).unwrap()
}

#[test]
fn len_matches_length_method_for_trailing_spaces_and_utf16() {
    let input = project(
        "&Fixed = \" a  \"\n&Text = \"a😀b  \"\nPrint(Len(&Fixed))\nPrint(&Fixed.Length())\nPrint(Len(&Text))\nPrint(&Text.Length())\nPrint(Len(\"\"))\nPrint(Len(\"C:\\new\\test\"))",
    );
    struct Folder(PathBuf);
    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    let root = Folder(std::env::temp_dir().join(format!(
            "gx-len-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        )));
    let artifact = build::build(&input, &root.0).unwrap();
    let output = Command::new(&artifact.executable).output().unwrap();
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
        ["2", "2", "4", "4", "0", "11"]
    );
}

#[test]
fn len_rejects_wrong_arity_and_nontext_values() {
    for source in [
        "Print(Len())",
        "Print(Len(1))",
        "Print(Len(True))",
        "Print(Len(\"a\",\"b\"))",
    ] {
        assert!(
            gx_engine::compile(&project(source)).is_err(),
            "admitted {source}"
        );
    }
}
