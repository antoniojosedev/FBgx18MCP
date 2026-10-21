//! A failed clean of the output directory must fail the rebuild: a stale `.cs` left behind would be
//! compiled into the project and counted in "N/N compiled" although the catalog no longer has it.

#![cfg(windows)]

use gx_engine::{
    catalog::{Catalog, Edit, KbObject, ObjectId, ObjectKind, SourcePart},
    rebuild::{RebuildOptions, rebuild_all},
};
use std::{fs, os::windows::fs::OpenOptionsExt};

fn hello_catalog() -> Catalog {
    let mut hello = KbObject::new(
        ObjectId::new("hello").unwrap(),
        "Hello",
        ObjectKind::Procedure,
    );
    hello.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "msg('hi', status)".into(),
    });
    let mut catalog = Catalog::empty();
    catalog
        .apply(0, &[Edit::Upsert(hello)])
        .expect("catalog setup");
    catalog
}

#[test]
fn clean_that_cannot_remove_the_output_directory_fails_the_rebuild() {
    let out = std::env::temp_dir().join(format!("gx_rebuild_clean_{}", std::process::id()));
    let _ = fs::remove_dir_all(&out);
    fs::create_dir_all(&out).unwrap();
    let stale = out.join("Stale.cs");
    fs::write(&stale, "class Stale {}").unwrap();
    // Share mode 0 keeps the file from being deleted while the handle is open.
    let _lock = fs::OpenOptions::new()
        .read(true)
        .share_mode(0)
        .open(&stale)
        .unwrap();

    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_skip_compile(true);
    let outcome = rebuild_all(&hello_catalog(), &out, &options);

    let error = outcome.expect_err("a failed clean must fail the rebuild");
    assert!(
        error.contains("cannot clean output directory"),
        "error does not name the failed clean: {error}"
    );
    drop(_lock);
    let _ = fs::remove_dir_all(&out);
}

#[test]
fn clean_of_an_absent_output_directory_is_not_an_error() {
    let out = std::env::temp_dir().join(format!("gx_rebuild_absent_{}", std::process::id()));
    let _ = fs::remove_dir_all(&out);

    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_skip_compile(true);
    let report = rebuild_all(&hello_catalog(), &out, &options).expect("absent dir is fine");

    assert!(out.join("ahello.cs").exists());
    assert!(report.errors.is_empty(), "{:?}", report.errors);
    let _ = fs::remove_dir_all(&out);
}
