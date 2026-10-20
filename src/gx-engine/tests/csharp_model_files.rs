//! The model-file groups a rebuild writes from the KB (`csharp::model_files`).
//!
//! A KB that stores none of the runtime properties gets the generator defaults, which are the files the
//! SDK wrote for KBTeste (fixtures in tests/fixtures/sdk_form). A KB whose languages are not English only
//! (here: an in-memory client, which reads none) has its messages refused by name, not written.

use gx_engine::csharp::model_files::model_files_from_kb;
use gx_engine::udm::client::UdmClient;
use std::path::Path;

const FIXTURES: &str = "tests/fixtures/sdk_form";

fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(format!("{FIXTURES}/{name}")).expect("fixture")
}

#[test]
fn unstored_runtime_properties_take_the_defaults_the_sdk_wrote_for_kbteste() {
    let client = UdmClient::new_in_memory();
    let groups = model_files_from_kb(Ok(&client), Path::new("C:/no-install"));
    let (label, runtime) = &groups[1];
    assert_eq!(label, "runtime configuration");
    let files = runtime.as_ref().expect("defaults are implemented");
    let get = |path: &str| {
        files
            .iter()
            .find(|(p, _)| p == path)
            .map(|(_, bytes)| bytes.clone())
            .unwrap_or_else(|| panic!("{path} not written"))
    };
    assert_eq!(get("log.config"), fixture("runtime_log.config"));
    assert_eq!(get("bin/log.config"), fixture("runtime_log.config"));
    assert_eq!(
        get("log.console.config"),
        fixture("runtime_log.console.config")
    );
    assert_eq!(
        get("CloudServices.config"),
        fixture("runtime_CloudServices.config")
    );
}

#[test]
fn messages_are_refused_when_the_kb_is_not_english_only_or_unreadable() {
    let client = UdmClient::new_in_memory();
    let groups = model_files_from_kb(Ok(&client), Path::new("C:/no-install"));
    let (label, messages) = &groups[0];
    assert_eq!(label, "web messages");
    let reason = messages.as_ref().expect_err("no languages read");
    assert!(reason.contains("English-only"), "{reason}");

    let groups = model_files_from_kb(Err("no KB connection".to_string()), Path::new("C:/x"));
    assert!(
        groups
            .iter()
            .all(|(_, group)| group.as_ref().is_err_and(|e| e == "no KB connection"))
    );
}
