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
