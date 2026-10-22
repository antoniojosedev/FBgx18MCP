use serde_json::Value;
use std::{
    fs,
    path::PathBuf,
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

struct Scratch(PathBuf);
impl Scratch {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "gx-sql-auth-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        fs::write(path.join("knowledgebase.connection"), "<ConnectionInformation><ServerInstance>invalid-gxe-635</ServerInstance><DBName>GX_KB_KBTeste635</DBName><IntegratedSecurity>False</IntegratedSecurity></ConnectionInformation>").unwrap();
        Self(path)
    }
    fn command(&self) -> Command {
        let mut cmd = Command::new(env!("CARGO_BIN_EXE_gx-engine"));
        // No SQL process may run during this contract test.
        cmd.env("PATH", &self.0)
            .env_remove("SQLCMDUSER")
            .env_remove("SQLCMDPASSWORD");
        cmd
    }
}
impl Drop for Scratch {
    fn drop(&mut self) {
        fs::remove_dir_all(&self.0).unwrap();
    }
}

#[test]
fn missing_sql_credentials_are_a_proven_prebuild_refusal() {
    let scratch = Scratch::new();
    let out = scratch.0.join("not-created");
    let output = scratch
        .command()
        .args(["--diagnostics=json", "rebuild-all", "--kb"])
        .arg(&scratch.0)
        .arg("--output")
        .arg(&out)
        .args(["--object", "Main", "--closure"])
        .output()
        .unwrap();
    let stderr = String::from_utf8(output.stderr).unwrap();
    assert_eq!(output.status.code(), Some(5), "{stderr}");
    assert!(output.stdout.is_empty());
    assert!(!out.exists(), "credential refusal must precede any output");
    let diagnostic: Value = serde_json::from_str(&stderr).unwrap();
    assert_eq!(diagnostic["category"], "sql_credentials_unavailable");
    assert!(diagnostic["location"].is_null());
    let message = diagnostic["message"].as_str().unwrap();
    assert!(message.contains("SQLCMDUSER") && message.contains("SQLCMDPASSWORD"));
    assert!(!message.contains("invalid-gxe-635"));
    assert!(!message.contains("Failed to invoke sqlcmd"));
}

#[test]
fn complete_environment_credentials_pass_preflight_without_leaking() {
    let scratch = Scratch::new();
    let output = scratch
        .command()
        .env("SQLCMDUSER", "fake-gxe-user-635")
        .env("SQLCMDPASSWORD", "fake-gxe-password-635")
        .args(["--diagnostics=json", "rebuild-all", "--kb"])
        .arg(&scratch.0)
        .arg("--output")
        .arg(scratch.0.join("out"))
        .output()
        .unwrap();
    // With no sqlcmd on PATH, the following topology read fails. Credential validation passed.
    assert_eq!(output.status.code(), Some(4));
    let diagnostic: Value = serde_json::from_slice(&output.stderr).unwrap();
    assert_eq!(diagnostic["category"], "version_scope");
    let message = diagnostic["message"].as_str().unwrap();
    assert!(message.contains("Failed to invoke sqlcmd"), "{message}");
    assert!(!message.contains("fake-gxe-user-635") && !message.contains("fake-gxe-password-635"));
    assert!(output.stdout.is_empty());
}

#[test]
fn queries_without_sql_credentials_fail_before_spawning_sqlcmd() {
    let scratch = Scratch::new();
    let output = scratch
        .command()
        .args(["--diagnostics=json", "kb-query"])
        .arg(&scratch.0)
        .args(["--type", "Procedure"])
        .output()
        .unwrap();
    assert_eq!(output.status.code(), Some(4));
    let diagnostic: Value = serde_json::from_slice(&output.stderr).unwrap();
    let message = diagnostic["message"].as_str().unwrap();
    assert!(
        message.contains("SQLCMDUSER") && message.contains("SQLCMDPASSWORD"),
        "{message}"
    );
    assert!(!message.contains("Failed to invoke sqlcmd"));
    assert!(output.stdout.is_empty());
}
