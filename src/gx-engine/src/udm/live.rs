//! Live SQL Server / LocalDB subprocess driver for GeneXus physical storage.

#![forbid(unsafe_code)]

use crate::udm::connection::{AuthMode, KbConnectionInfo};

fn sqlcmd_command(info: &KbConnectionInfo) -> std::process::Command {
    let mut cmd = std::process::Command::new("sqlcmd");
    cmd.arg("-S").arg(&info.server_instance);
    cmd.arg("-d").arg(&info.db_name);
    cmd.arg("-b"); // on error batch abort
    // `-y 0` removes the 256-character limit sqlcmd applies to variable-length
    // columns. Without it every `CONVERT(VARCHAR(MAX), <varbinary>, 2)` hex
    // column (part blobs) was cut mid-stream and `decode_blob` failed with
    // "incomplete deflate stream". This sqlcmd build rejects `-y 0` together
    // with `-W`/`-h`, so callers trim their own fields (they already do).
    cmd.arg("-y").arg("0");
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

    cmd
}

/// Executes raw SQL against a live SQL Server or LocalDB instance using `sqlcmd`.
pub fn execute_sqlcmd(info: &KbConnectionInfo, sql: &str) -> Result<String, String> {
    let output = sqlcmd_command(info)
        .arg("-Q")
        .arg(sql)
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

/// One sqlcmd connection, scoped to a reader and never shared across Knowledge Bases.
/// Only generated SELECT batches use this path; writes retain their one-shot execution.
#[derive(Debug)]
pub struct ReadSession {
    child: std::process::Child,
    input: std::process::ChildStdin,
    output: std::io::BufReader<std::process::ChildStdout>,
    stderr: Option<std::thread::JoinHandle<Vec<u8>>>,
}

fn read_batch(sql: &str, marker: &str) -> Result<String, String> {
    if !sql.trim_start().starts_with("SET NOCOUNT ON; SELECT ") || sql.contains(['\r', '\n']) {
        return Err("persistent reads require a generated SELECT without sqlcmd directives".into());
    }
    Ok(format!("{sql}\nGO\nPRINT '{marker}';\nGO\n"))
}

impl ReadSession {
    pub fn start(info: &KbConnectionInfo) -> Result<Self, String> {
        use std::io::Read;
        use std::process::Stdio;
        let mut command = sqlcmd_command(info);
        let mut child = command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|error| format!("cannot start sqlcmd reader: {error}"))?;
        let input = child.stdin.take().expect("piped stdin");
        let output =
            std::io::BufReader::with_capacity(1 << 20, child.stdout.take().expect("piped stdout"));
        let mut pipe = child.stderr.take().expect("piped stderr");
        let stderr = std::thread::spawn(move || {
            let mut kept = Vec::new();
            let _ = pipe.by_ref().take(32768).read_to_end(&mut kept);
            let _ = std::io::copy(&mut pipe, &mut std::io::sink());
            kept
        });
        Ok(Self {
            child,
            input,
            output,
            stderr: Some(stderr),
        })
    }

    pub fn query(&mut self, sql: &str) -> Result<String, String> {
        use std::io::{BufRead, Write};
        use std::sync::atomic::{AtomicU64, Ordering};
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let marker = format!(
            "__gxe_read_{}_{}_{}__",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_err(|error| error.to_string())?
                .as_nanos(),
        );
        let batch = read_batch(sql, &marker)?;
        self.input
            .write_all(batch.as_bytes())
            .and_then(|_| self.input.flush())
            .map_err(|error| format!("cannot send sqlcmd read: {error}"))?;
        let mut bytes = Vec::new();
        let mut line = Vec::new();
        loop {
            line.clear();
            if self
                .output
                .read_until(b'\n', &mut line)
                .map_err(|error| format!("cannot read sqlcmd response: {error}"))?
                == 0
            {
                let status = self.child.wait().map_err(|error| error.to_string())?;
                let detail = self
                    .stderr
                    .take()
                    .and_then(|task| task.join().ok())
                    .unwrap_or_default();
                return Err(format!(
                    "sqlcmd read ended before its completion marker ({status}): {}{}",
                    String::from_utf8_lossy(&bytes),
                    String::from_utf8_lossy(&detail)
                ));
            }
            let mut content = line.as_slice();
            while let [rest @ .., b'\r' | b'\n'] = content {
                content = rest;
            }
            if content == marker.as_bytes() {
                return Ok(String::from_utf8(bytes).unwrap_or_else(|error| {
                    String::from_utf8_lossy(error.as_bytes()).into_owned()
                }));
            }
            bytes.extend_from_slice(&line);
        }
    }
}

impl Drop for ReadSession {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(task) = self.stderr.take() {
            let _ = task.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn persistent_select_batches_reject_sqlcmd_directives() {
        let batch = read_batch("SET NOCOUNT ON; SELECT 42;", "finished").unwrap();
        assert_eq!(
            batch,
            "SET NOCOUNT ON; SELECT 42;\nGO\nPRINT 'finished';\nGO\n"
        );
        for sql in [
            "DELETE FROM EntityVersion",
            "SET NOCOUNT ON; SELECT 1;\n:r file",
            "SET NOCOUNT ON; SELECT 1;\nGO\nSELECT 2",
            "SET NOCOUNT ON; SELECT 1;\n!!command",
        ] {
            assert!(read_batch(sql, "finished").is_err(), "{sql}");
        }
    }

    #[test]
    #[ignore = "explicit local-only SQL integration against disposable KBTeste"]
    fn live_read_session_reuses_connection_and_fails_closed() {
        let info = KbConnectionInfo::from_kb_dir("C:/KBs/KBTeste").unwrap();
        let mut session = ReadSession::start(&info).unwrap();
        let pid = session.child.id();
        for value in [42, 73] {
            let output = session
                .query(&format!("SET NOCOUNT ON; SELECT {value};"))
                .unwrap();
            assert_eq!(output.trim(), value.to_string());
            assert_eq!(session.child.id(), pid);
        }
        for sql in [
            "SET NOCOUNT ON; SELECT REPLICATE('a', 4096), NULL, 73;",
            "SET NOCOUNT ON; SELECT NCHAR(233), 42;",
            "SET NOCOUNT ON; SELECT 1 WHERE 1 = 0;",
        ] {
            assert_eq!(
                session.query(sql).unwrap(),
                execute_sqlcmd(&info, sql).unwrap()
            );
        }
        assert!(
            session
                .query("SET NOCOUNT ON; SELECT * FROM __GXE_Absent_Session_Test_Table;")
                .is_err()
        );
        assert!(
            session.child.try_wait().unwrap().is_some(),
            "failed query process must be reaped"
        );
    }
}
