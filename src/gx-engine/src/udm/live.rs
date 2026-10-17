//! Live SQL Server / LocalDB subprocess driver for GeneXus physical storage.

#![forbid(unsafe_code)]

use crate::udm::connection::{AuthMode, KbConnectionInfo};

/// Executes raw SQL against a live SQL Server or LocalDB instance using `sqlcmd`.
pub fn execute_sqlcmd(info: &KbConnectionInfo, sql: &str) -> Result<String, String> {
    let mut cmd = std::process::Command::new("sqlcmd");
    cmd.arg("-S").arg(&info.server_instance);
    cmd.arg("-d").arg(&info.db_name);
    cmd.arg("-b"); // on error batch abort
    cmd.arg("-W"); // remove trailing whitespace
    cmd.arg("-s").arg("\t"); // tab delimited

    match &info.auth_mode {
        AuthMode::Integrated => {
            cmd.arg("-E");
        }
        AuthMode::SqlServer { user, password } => {
            cmd.arg("-U").arg(user);
            if let Some(p) = password {
                cmd.arg("-P").arg(p);
            }
        }
    }

    cmd.arg("-Q").arg(sql);

    let output = cmd
        .output()
        .map_err(|e| format!("Failed to invoke sqlcmd: {e}"))?;

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr);
        let stdout = String::from_utf8_lossy(&output.stdout);
        return Err(format!(
            "sqlcmd execution failed with exit code {:?}: {stderr}\n{stdout}",
            output.status.code()
        ));
    }

    let result = String::from_utf8_lossy(&output.stdout).to_string();
    Ok(result)
}
