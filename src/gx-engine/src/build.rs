//! Fresh native builds with generation-isolated outputs. No incomplete cache hits.
use crate::model::Project;
use serde::Serialize;
use std::{
    fs,
    io::Read,
    path::{Path, PathBuf},
    process::{Child, Command, ExitStatus, Stdio},
    sync::atomic::{AtomicU64, Ordering},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

static NEXT: AtomicU64 = AtomicU64::new(0);

#[derive(Debug)]
pub struct BuildArtifact {
    pub executable: PathBuf,
    pub source: PathBuf,
    pub manifest: PathBuf,
}

#[derive(Serialize)]
struct Manifest<'a> {
    format_version: u32,
    engine_version: &'a str,
    backend: &'a str,
    compiler: &'a str,
    compiler_args: Vec<&'a str>,
    entry: &'a str,
    procedures: usize,
    source: &'a str,
    executable: &'a str,
    project: &'a str,
    cache_hit: bool,
}

struct Staging(PathBuf);
impl Drop for Staging {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

struct CompilerOutput {
    status: ExitStatus,
    stdout: Vec<u8>,
    stderr: Vec<u8>,
}

fn read_log(path: &Path) -> Result<Vec<u8>, String> {
    let mut bytes = Vec::new();
    fs::File::open(path)
        .and_then(|file| file.take(16_384).read_to_end(&mut bytes))
        .map_err(|e| format!("cannot read compiler log: {e}"))?;
    Ok(bytes)
}

fn wait_bounded(child: &mut Child, timeout: Duration) -> Result<ExitStatus, String> {
    let deadline = Instant::now() + timeout;
    loop {
        match child.try_wait() {
            Ok(Some(status)) => return Ok(status),
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(10)),
            Ok(None) => return Err(format!("exceeded {}ms deadline", timeout.as_millis())),
            Err(error) => return Err(format!("cannot observe process: {error}")),
        }
    }
}

fn stop_compiler(child: &mut Child) -> String {
    // Keep Child's process handle alive: Windows cannot reuse this PID while
    // the handle exists. Never terminate compiler processes by executable name.
    #[cfg(windows)]
    let tree = match Command::new("taskkill")
        .args(["/PID", &child.id().to_string(), "/T", "/F"])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
    {
        Ok(mut taskkill) => match wait_bounded(&mut taskkill, Duration::from_secs(2)) {
            Ok(status) => format!("{status}"),
            Err(error) => {
                let kill = taskkill.kill();
                let wait = wait_bounded(&mut taskkill, Duration::from_secs(2));
                format!("{error}; cleanup kill={kill:?}, wait={wait:?}")
            }
        },
        Err(error) => format!("cannot start tree cleanup: {error}"),
    };
    let kill = child.kill();
    let wait = wait_bounded(child, Duration::from_secs(2));
    #[cfg(windows)]
    return format!("tree={tree}, kill={kill:?}, wait={wait:?}");
    #[cfg(not(windows))]
    format!("kill={kill:?}, wait={wait:?}; descendant cleanup not guaranteed on this platform")
}

fn run_compiler(
    command: &mut Command,
    directory: &Path,
    prefix: &str,
    timeout: Duration,
) -> Result<CompilerOutput, String> {
    let stdout = directory.join(format!("{prefix}.stdout.log"));
    let stderr = directory.join(format!("{prefix}.stderr.log"));
    let out =
        fs::File::create_new(&stdout).map_err(|e| format!("cannot create compiler log: {e}"))?;
    let err =
        fs::File::create_new(&stderr).map_err(|e| format!("cannot create compiler log: {e}"))?;
    let mut child = command
        .current_dir(directory)
        .stdin(Stdio::null())
        .stdout(out)
        .stderr(err)
        .spawn()
        .map_err(|e| format!("cannot start compiler: {e}"))?;
    let status = wait_bounded(&mut child, timeout).map_err(|reason| {
        let cleanup = stop_compiler(&mut child);
        format!("compiler {reason}; {cleanup}")
    })?;
    Ok(CompilerOutput {
        status,
        stdout: read_log(&stdout)?,
        stderr: read_log(&stderr)?,
    })
}

/// Build an admitted project using rustc directly, without loading SDK assemblies.
/// Each success publishes a distinct generation only after rustc has succeeded.
/// Never reuse an executable based solely on timestamps or source-only cache keys.
pub fn build(project: &Project, output: &Path) -> Result<BuildArtifact, String> {
    let source = crate::compile(project)?;
    fs::create_dir_all(output).map_err(|e| format!("cannot create build directory: {e}"))?;
    let output =
        fs::canonicalize(output).map_err(|e| format!("cannot resolve build directory: {e}"))?;
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|e| format!("invalid system clock: {e}"))?
        .as_nanos();
    let name = format!(
        "{}-{stamp}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    );
    let staging = Staging(output.join(format!(".pending-{name}")));
    fs::create_dir(&staging.0).map_err(|e| format!("cannot create staging directory: {e}"))?;
    let compiler = run_compiler(
        Command::new("rustc").arg("-vV"),
        &staging.0,
        "version",
        Duration::from_secs(10),
    )?;
    if !compiler.status.success() {
        return Err(format!(
            "rustc version probe failed: {}",
            String::from_utf8_lossy(&compiler.stderr)
        ));
    }
    let compiler = String::from_utf8(compiler.stdout)
        .map_err(|_| "rustc version output is not UTF-8".to_string())?;
    let executable_name = if cfg!(windows) {
        "program.exe"
    } else {
        "program"
    };
    fs::write(staging.0.join("program.rs"), source)
        .map_err(|e| format!("cannot write generated source: {e}"))?;
    fs::write(staging.0.join("project.json"), project.to_json()?)
        .map_err(|e| format!("cannot write portable input: {e}"))?;
    let args = [
        "--crate-name",
        "gx_program",
        "--edition=2024",
        "-C",
        "opt-level=1",
        "-C",
        "overflow-checks=yes",
    ];
    let compiled = run_compiler(
        Command::new("rustc")
            .args(args)
            .arg("program.rs")
            .arg("-o")
            .arg(executable_name),
        &staging.0,
        "compile",
        Duration::from_secs(120),
    )?;
    if !compiled.status.success() {
        let len = compiled.stderr.len();
        let detail = if len > 16_384 {
            &compiled.stderr[len - 16_384..]
        } else {
            &compiled.stderr[..]
        };
        return Err(format!(
            "native compilation failed ({}):\n{}",
            compiled.status,
            String::from_utf8_lossy(detail)
        ));
    }
    if !staging.0.join(executable_name).is_file() {
        return Err("compiler succeeded without producing the executable".into());
    }
    let manifest = Manifest {
        format_version: 1,
        engine_version: env!("CARGO_PKG_VERSION"),
        backend: if project.format_version == 2 {
            "independent-rust-typed-subset"
        } else {
            "independent-rust-i64-subset"
        },
        compiler: &compiler,
        compiler_args: args.to_vec(),
        entry: &project.entry,
        procedures: project.procedures.len(),
        source: "program.rs",
        executable: executable_name,
        project: "project.json",
        cache_hit: false,
    };
    fs::write(
        staging.0.join("manifest.json"),
        serde_json::to_vec_pretty(&manifest).map_err(|e| e.to_string())?,
    )
    .map_err(|e| format!("cannot write build manifest: {e}"))?;
    for log in [
        "version.stdout.log",
        "version.stderr.log",
        "compile.stdout.log",
        "compile.stderr.log",
    ] {
        fs::remove_file(staging.0.join(log))
            .map_err(|e| format!("cannot remove temporary compiler log: {e}"))?;
    }
    let published = output.join(format!("build-{name}"));
    fs::rename(&staging.0, &published)
        .map_err(|e| format!("cannot publish complete build: {e}"))?;
    Ok(BuildArtifact {
        executable: published.join(executable_name),
        source: published.join("program.rs"),
        manifest: published.join("manifest.json"),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compiler_deadline_reaps_the_owned_process() {
        let path = std::env::temp_dir().join(format!(
            "gx-compiler-deadline-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        let staging = Staging(path);
        #[cfg(windows)]
        let mut command = {
            let mut command = Command::new("cmd");
            command.args(["/C", "ping -n 3 127.0.0.1 >NUL"]);
            command
        };
        #[cfg(not(windows))]
        let mut command = {
            let mut command = Command::new("sh");
            command.args(["-c", "sleep 2"]);
            command
        };
        let error = run_compiler(&mut command, &staging.0, "test", Duration::from_millis(50))
            .err()
            .expect("long-running compiler must time out");
        assert!(error.contains("exceeded 50ms deadline"), "{error}");
        assert!(error.contains("wait=Ok"), "{error}");
    }
}
