//! The model files a GeneXus 18 .NET web model holds beside the generated classes that the engine
//! reproduces from the Knowledge Base and the GeneXus install: the web messages
//! ([`crate::csharp::messages`]) and the runtime configuration ([`crate::csharp::runtime_config`]).
//!
//! Inputs, each refused by name when it cannot be read rather than guessed:
//! - Messages: the KB's `TranslationMessage` entities (key, type, id order) and the English text in the
//!   install's `Library/GAM/Platforms/NetCoreSql/messages.eng.resources`. Only an English-only KB is
//!   matched (KBTeste); another language is refused.
//! - Runtime configuration: the stored model properties it depends on. A property the KB never stored
//!   takes the GeneXus 18 .NET generator default (`netcoreDefinition.xml`): `LOG_OUTPUT` RollingFile,
//!   `threshold` OFF, `LOG_USER_LEVEL` ERROR, `TRACE_FILE` client.log, `WEBNOTIFICATIONS_PROVIDER`
//!   INPROCESS, every other provider NONE. KBTeste stores none of them, and its SDK output is these files.
//!
//! The Developer Menu ([`crate::csharp::developer_menu`]) is not written here: which objects the SDK lists
//! is not established.

use std::path::{Path, PathBuf};

use crate::csharp::messages::{
    MessageEntry, MessageKind, emit_message_files, parse_resource_strings,
};
use crate::csharp::runtime_config::{RuntimeSettings, runtime_config_files};
use crate::udm::client::UdmClient;

/// The GeneXus 18 install the engine reads when `GX18_INSTALL_DIR` is not set.
pub const DEFAULT_GENEXUS_INSTALL: &str = "C:/Program Files (x86)/GeneXus/GeneXus18";
/// The install resource holding the English text of the `GXM_*` messages.
pub const MESSAGES_RESOURCE: &str = "Library/GAM/Platforms/NetCoreSql/messages.eng.resources";

/// One group of model files: its label and the `(relative path, bytes)` pairs, or why it is not written.
pub type ModelFileGroup = (String, Result<Vec<(String, Vec<u8>)>, String>);

const SERVICE_TYPES: [&str; 12] = [
    "NOTIFICATIONS",
    "STORAGE",
    "CACHE",
    "WEBNOTIFICATIONS",
    "MAPS",
    "MAPSAPPLE",
    "ADS",
    "LOG",
    "ANALYTICS",
    "REMOTECONFIG",
    "OBSERVABILITY",
    "SESSION",
];

const HANDLERS: [&str; 4] = [
    "WEBNOTIFICATIONS_RECEIVED_HANDLER",
    "WEBNOTIFICATIONS_ONOPEN_HANDLER",
    "WEBNOTIFICATIONS_ONCLOSE_HANDLER",
    "WEBNOTIFICATIONS_ONERROR_HANDLER",
];

/// The GeneXus 18 install: `GX18_INSTALL_DIR`, else [`DEFAULT_GENEXUS_INSTALL`].
pub fn genexus_install_dir() -> PathBuf {
    std::env::var_os("GX18_INSTALL_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(DEFAULT_GENEXUS_INSTALL))
}

/// Both groups for the KB behind `client`, reading the message text from `install`.
pub fn model_files_from_kb(
    client: Result<&UdmClient, String>,
    install: &Path,
) -> Vec<ModelFileGroup> {
    let messages = client
        .clone()
        .and_then(|client| message_files(client, install));
    let runtime = client.and_then(runtime_files);
    vec![
        ("web messages".to_string(), messages),
        ("runtime configuration".to_string(), runtime),
    ]
}

fn message_files(client: &UdmClient, install: &Path) -> Result<Vec<(String, Vec<u8>)>, String> {
    let languages = client.language_names()?;
    if languages != ["English"] {
        return Err(format!(
            "only an English-only KB is matched; the KB's languages are {languages:?}"
        ));
    }
    let resource = install.join(MESSAGES_RESOURCE);
    let bytes = std::fs::read(&resource)
        .map_err(|e| format!("the message text {} is not read: {e}", resource.display()))?;
    let texts: std::collections::HashMap<String, String> = parse_resource_strings(&bytes)
        .map_err(|e| e.to_string())?
        .into_iter()
        .collect();
    let entries = client
        .translation_messages()?
        .into_iter()
        .map(|(id, key, kind)| {
            let kind = match kind.as_str() {
                "Internal" => MessageKind::Internal,
                "User" => MessageKind::User,
                other => return Err(format!("message {key} has MessageType '{other}'")),
            };
            let text = texts.get(&key).cloned();
            Ok(MessageEntry {
                id,
                key,
                kind,
                text,
            })
        })
        .collect::<Result<Vec<_>, String>>()?;
    let files = emit_message_files("eng", &entries).map_err(|e| e.to_string())?;
    Ok(vec![
        (files.txt.name, files.txt.bytes),
        (files.js.name, files.js.bytes),
    ])
}

fn runtime_files(client: &UdmClient) -> Result<Vec<(String, Vec<u8>)>, String> {
    let provider_names: Vec<String> = SERVICE_TYPES
        .iter()
        .map(|kind| format!("{kind}_PROVIDER"))
        .collect();
    let mut names: Vec<&str> = vec![
        "LOG_OUTPUT",
        "threshold",
        "LOG_USER_LEVEL",
        "LOG_FORMAT",
        "TRACE_FILE",
    ];
    names.extend(provider_names.iter().map(String::as_str));
    names.extend(HANDLERS);
    let stored = client.stored_model_properties(&names)?;
    let value = |name: &str, default: &'static str| -> String {
        stored
            .get(name)
            .cloned()
            .unwrap_or_else(|| default.to_string())
    };
    let log_output = value("LOG_OUTPUT", "RollingFile");
    let threshold = value("threshold", "OFF");
    let log_user_level = value("LOG_USER_LEVEL", "ERROR");
    let log_format = value("LOG_FORMAT", "");
    let trace_file = value("TRACE_FILE", "client.log");
    let providers: Vec<(&str, String)> = SERVICE_TYPES
        .iter()
        .zip(&provider_names)
        .map(|(kind, name)| {
            let default = if *kind == "WEBNOTIFICATIONS" {
                "INPROCESS"
            } else {
                "NONE"
            };
            (*kind, value(name, default))
        })
        .collect();
    let provider_refs: Vec<(&str, &str)> = providers
        .iter()
        .map(|(kind, provider)| (*kind, provider.as_str()))
        .collect();
    let handlers: Vec<String> = HANDLERS.iter().map(|name| value(name, "")).collect();
    let settings = RuntimeSettings {
        log_output: &log_output,
        threshold: &threshold,
        log_user_level: &log_user_level,
        log_format: &log_format,
        trace_file: &trace_file,
        providers: &provider_refs,
        webnotifications_handlers: [&handlers[0], &handlers[1], &handlers[2], &handlers[3]],
    };
    Ok(runtime_config_files(&settings)?
        .into_iter()
        .map(|file| (file.path.to_string(), file.bytes))
        .collect())
}
