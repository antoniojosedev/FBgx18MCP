//! The SDK-text page contract is MCP-only; unsupported clients stop before opening a KB.
use std::process::Command;

#[test]
fn non_mcp_page_clients_are_refused_before_connecting() {
    for client in ["ide", "IDE", "other", ""] {
        let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
            .args([
                "--diagnostics=json",
                "kb-read",
                "C:/__gxe_read_page_no_kb_exists__",
                "Alpha",
                "--page",
                "--parts",
                "Source",
                "--client",
                client,
            ])
            .output()
            .expect("run gx-engine");
        assert_eq!(output.status.code(), Some(1), "{client:?}");
        assert!(output.stdout.is_empty(), "{client:?}");
        let error: serde_json::Value =
            serde_json::from_slice(&output.stderr).expect("strict diagnostic");
        assert_eq!(error["category"], "input", "{client:?}");
        assert!(
            error["message"]
                .as_str()
                .expect("message")
                .contains("the native page envelope serves only mcp"),
            "{client:?}: {error}"
        );
    }
}
