//! The CLI reports the catalog load time apart from the pipeline's `total_ms`. The load happens before the
//! pipeline, so a wall-clock comparison that includes it needs the number. Read through the real binary,
//! with the JSON report enabled.

use gx_engine::catalog::{Catalog, KbObject, ObjectId, ObjectKind, SourcePart};
use std::collections::BTreeMap;
use std::process::Command;

#[test]
fn rebuild_all_reports_the_catalog_load_time_apart_from_the_pipeline_total() {
    let dir = std::env::temp_dir().join(format!("gx_cli_catalog_load_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();

    let id = ObjectId::new("p1").unwrap();
    let mut procedure = KbObject::new(id.clone(), "POne", ObjectKind::Procedure);
    procedure.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&Page = 1\n".into(),
    });
    let mut objects = BTreeMap::new();
    objects.insert(id, procedure);
    let catalog_file = dir.join("catalog.json");
    std::fs::write(
        &catalog_file,
        Catalog::from_objects_map(objects).to_json().unwrap(),
    )
    .unwrap();

    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .env("GXMCP_JSON_REPORT", "1")
        .args(["rebuild-all", "--kb"])
        .arg(&catalog_file)
        .arg("--output")
        .arg(dir.join("out"))
        .arg("--skip-compile")
        .output()
        .expect("run gx-engine");
    let report: serde_json::Value = serde_json::from_slice(&output.stdout)
        .unwrap_or_else(|error| panic!("the JSON report on stdout: {error}"));

    assert!(
        report["catalog_load_ms"].is_u64(),
        "the report carries the catalog load time: {report}"
    );
    assert_eq!(report["procedures_count"], 1);
    assert!(
        report["total_ms"].is_u64(),
        "the pipeline total stays a separate field"
    );
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn rebuild_all_refuses_a_kb_path_that_is_neither_a_kb_folder_nor_a_catalog_in_one_line() {
    let dir = std::env::temp_dir().join(format!("gx_cli_not_a_kb_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    for kb in [dir.join("missing"), dir.clone()] {
        let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
            .args(["rebuild-all", "--kb"])
            .arg(&kb)
            .arg("--output")
            .arg(dir.join("out"))
            .output()
            .expect("run gx-engine");
        let stderr = String::from_utf8_lossy(&output.stderr);
        assert_eq!(output.status.code(), Some(1), "{stderr}");
        assert_eq!(
            stderr.trim().lines().count(),
            1,
            "one clear error, no read noise: {stderr}"
        );
        assert!(stderr.contains("knowledgebase.connection"), "{stderr}");
        assert!(
            !dir.join("out").exists(),
            "nothing is written for a refused KB"
        );
    }
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn rebuild_all_refuses_a_target_framework_it_does_not_build_before_reading_the_kb() {
    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .args([
            "rebuild-all",
            "--kb",
            "unused",
            "--output",
            "unused",
            "--target",
            "net7.0",
        ])
        .output()
        .expect("run gx-engine");
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert_eq!(output.status.code(), Some(1), "{stderr}");
    assert!(
        stderr.contains("net7.0") && stderr.contains("net8.0"),
        "{stderr}"
    );
}
