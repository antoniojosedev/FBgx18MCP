//! The runtime configuration files of a GeneXus 18 .NET web model (`NetModel/web`), byte for byte.
//!
//! Fixtures (tests/fixtures/sdk_form, copied from the SDK output in KBTeste's NetModel/web):
//! `runtime_log.config` (web appender), `runtime_log.console.config` (console appender) and
//! `runtime_CloudServices.config`. The settings are the ones the SDK's output corresponds to: the
//! `netcoreDefinition.xml` defaults (threshold OFF, LOG_USER_LEVEL ERROR, LOG_OUTPUT RollingFile,
//! TRACE_FILE client.log, WEBNOTIFICATIONS_PROVIDER INPROCESS). The stored KB values were not read here.

use gx_engine::csharp::runtime_config::{RuntimeFile, RuntimeSettings, runtime_config_files};

const FIXTURES: &str = "tests/fixtures/sdk_form";

const SERVICE_PROVIDERS: [(&str, &str); 12] = [
    ("NOTIFICATIONS", "NONE"),
    ("STORAGE", "NONE"),
    ("CACHE", "NONE"),
    ("WEBNOTIFICATIONS", "INPROCESS"),
    ("MAPS", "NONE"),
    ("MAPSAPPLE", "NONE"),
    ("ADS", "NONE"),
    ("LOG", "NONE"),
    ("ANALYTICS", "NONE"),
    ("REMOTECONFIG", "NONE"),
    ("OBSERVABILITY", "NONE"),
    ("SESSION", "NONE"),
];

fn kbteste() -> RuntimeSettings<'static> {
    RuntimeSettings {
        log_output: "RollingFile",
        threshold: "OFF",
        log_user_level: "ERROR",
        log_format: "",
        trace_file: "client.log",
        providers: &SERVICE_PROVIDERS,
        webnotifications_handlers: ["", "", "", ""],
    }
}

fn fixture(name: &str) -> Vec<u8> {
    let path = format!("{FIXTURES}/{name}");
    std::fs::read(&path).unwrap_or_else(|err| panic!("cannot read {path}: {err}"))
}

fn file<'a>(files: &'a [RuntimeFile], path: &str) -> &'a [u8] {
    files
        .iter()
        .find(|file| file.path == path)
        .unwrap_or_else(|| panic!("no runtime file {path}"))
        .bytes
        .as_slice()
}

#[test]
fn the_kbteste_runtime_files_match_the_sdk_bytes() {
    let files = runtime_config_files(&kbteste()).expect("KBTeste settings are implemented");
    let paths: Vec<&str> = files.iter().map(|file| file.path).collect();
    assert_eq!(
        paths,
        [
            "log.config",
            "bin/log.config",
            "log.console.config",
            "bin/log.console.config",
            "CloudServices.config"
        ]
    );
    let log = fixture("runtime_log.config");
    let console = fixture("runtime_log.console.config");
    assert_eq!(file(&files, "log.config"), log.as_slice());
    assert_eq!(file(&files, "bin/log.config"), log.as_slice());
    assert_eq!(file(&files, "log.console.config"), console.as_slice());
    assert_eq!(file(&files, "bin/log.console.config"), console.as_slice());
    assert_eq!(
        file(&files, "CloudServices.config"),
        fixture("runtime_CloudServices.config").as_slice()
    );
}

#[test]
fn cloud_services_config_keeps_the_utf8_bom_and_has_no_trailing_newline() {
    let files = runtime_config_files(&kbteste()).unwrap();
    let cloud = file(&files, "CloudServices.config");
    assert_eq!(&cloud[..3], &[0xEF, 0xBB, 0xBF]);
    assert!(cloud.ends_with(b"</Services>"));
}

#[test]
fn the_threshold_and_user_level_are_the_only_level_lines_that_change() {
    let mut settings = kbteste();
    settings.threshold = "INFO";
    settings.log_user_level = "WARN";
    let files = runtime_config_files(&settings).unwrap();
    let text = String::from_utf8(file(&files, "log.config").to_vec()).unwrap();
    assert!(text.contains("\t\t<level value=\"INFO\"/>\r\n"));
    assert!(text.contains("\t\t<level value=\"WARN\" />\r\n"));
    assert!(text.contains("\t\t\t<level value=\"INFO\" />\r\n"));
    assert!(
        text.contains("\t\t<level value=\"off\" />\r\n"),
        "AllAppenders stays off"
    );
    assert!(!text.contains("value=\"OFF\""));
}

#[test]
fn the_trace_file_goes_into_the_appender_of_both_variants() {
    let mut settings = kbteste();
    settings.trace_file = "logs/app client.log";
    let files = runtime_config_files(&settings).unwrap();
    for path in ["log.config", "log.console.config"] {
        let text = String::from_utf8(file(&files, path).to_vec()).unwrap();
        assert!(
            text.contains("<file  value=\"logs/app client.log\"/>"),
            "{path}"
        );
    }
}

#[test]
fn settings_the_emitter_does_not_understand_are_refused() {
    let refused = |change: fn(&mut RuntimeSettings<'static>)| {
        let mut settings = kbteste();
        change(&mut settings);
        assert!(runtime_config_files(&settings).is_err());
    };
    refused(|s| s.log_output = "Syslog");
    refused(|s| s.log_output = "ConsoleAppender");
    refused(|s| s.threshold = "TRACE");
    refused(|s| s.log_user_level = "");
    refused(|s| s.log_format = "idTRACE_FORMAT_JSON");
    refused(|s| s.trace_file = "");
    refused(|s| s.trace_file = "a\"b.log");
    refused(|s| s.trace_file = "a&b.log");
    refused(|s| s.webnotifications_handlers[0] = "Objects.OnMessage");
    refused(|s| s.providers = &[("FOO", "NONE"), ("WEBNOTIFICATIONS", "INPROCESS")]);
    refused(|s| s.providers = &[("WEBNOTIFICATIONS", "AZURE")]);
    refused(|s| {
        s.providers = &[
            ("STORAGE", "AZURESTORAGE"),
            ("WEBNOTIFICATIONS", "INPROCESS"),
        ]
    });
    refused(|s| s.providers = &[("WEBNOTIFICATIONS", "NONE")]);
}
