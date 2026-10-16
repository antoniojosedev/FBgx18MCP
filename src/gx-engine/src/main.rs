use gx_engine::{
    build,
    diagnostics::{Diagnostic, DiagnosticCategory},
    model::Project,
};
use std::{
    env,
    fs::File,
    io::Read,
    path::PathBuf,
    process::{Command, ExitCode},
    time::{Duration, Instant},
};

const HELP: &str = "gx-engine — experimental independent compiler\n\nUsage:\n  gx-engine check <project.json>\n  gx-engine emit <project.json>\n  gx-engine explain <previous.json> <next.json>\n  gx-engine build <project.json> [output-directory]\n  gx-engine run <project.json> [output-directory]\n  gx-engine kb-check <catalog.json> <entry-id>\n  gx-engine kb-emit <catalog.json> <entry-id>\n  gx-engine kb-build <catalog.json> <entry-id> [output-directory]\n  gx-engine kb-run <catalog.json> <entry-id> [output-directory]\n  gx-engine xml-check <authored.xml> <entry-name>\n  gx-engine xml-emit <authored.xml> <entry-name>\n  gx-engine xml-build <authored.xml> <entry-name> [output-directory]\n  gx-engine xml-run <authored.xml> <entry-name> [output-directory]\n  gx-engine --version\n\nPrefix a command with --diagnostics=json for structured errors on stderr.\nBuild diagnostics cover the whole build operation, including code generation.\nrun executes authored code with inherited console I/O and a 10-second deadline.\nNo native KB access, SDK fallback, SQL, MSBuild, or full GeneXus compatibility.\n";

fn load(path: &str) -> Result<Project, String> {
    let file = File::open(path).map_err(|e| format!("cannot open project: {e}"))?;
    let mut text = String::new();
    file.take(1_048_577)
        .read_to_string(&mut text)
        .map_err(|e| format!("cannot read project: {e}"))?;
    if text.len() > 1_048_576 {
        return Err("portable project exceeds 1 MiB".into());
    }
    Project::from_json(&text)
}

struct CapturedStderr(PathBuf);

impl CapturedStderr {
    fn describe(&self, reason: String) -> String {
        let mut bytes = Vec::new();
        if let Err(error) =
            File::open(&self.0).and_then(|file| file.take(16_385).read_to_end(&mut bytes))
        {
            return format!("{reason}; cannot read generated program stderr: {error}");
        }
        let truncated = bytes.len() > 16_384;
        bytes.truncate(16_384);
        if bytes.is_empty() {
            return reason;
        }
        format!(
            "{reason}\n{}{}",
            String::from_utf8_lossy(&bytes),
            if truncated {
                "\n[generated program stderr truncated to 16384 bytes]"
            } else {
                ""
            }
        )
    }
}

impl Drop for CapturedStderr {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

fn execution_error(reason: String, capture: &Option<CapturedStderr>) -> String {
    match capture {
        Some(capture) => capture.describe(reason),
        None => reason,
    }
}

fn execute(
    args: Vec<String>,
    category: &mut DiagnosticCategory,
    json_errors: bool,
) -> Result<(), String> {
    if args.is_empty() || args == ["--help"] || args == ["-h"] {
        print!("{HELP}");
        return Ok(());
    }
    let raw_command = args[0].as_str();
    let from_xml = raw_command.starts_with("xml-");
    let (command, from_objects) = match raw_command
        .strip_prefix("kb-")
        .or_else(|| raw_command.strip_prefix("xml-"))
    {
        Some(command) if matches!(command, "check" | "emit" | "build" | "run") => (command, true),
        _ => (raw_command, false),
    };
    if !matches!(command, "check" | "emit" | "explain" | "build" | "run") {
        return Err(format!("unknown command {command:?}; use --help"));
    }
    let max = if matches!(command, "build" | "run" | "explain") {
        3
    } else {
        2
    };
    let max = max + usize::from(from_objects);
    if args.len() < 2 + usize::from(from_objects) || args.len() > max {
        return Err("invalid arguments; use --help".into());
    }
    if command == "explain" && args.len() != 3 {
        return Err("explain requires previous and next project paths".into());
    }
    *category = DiagnosticCategory::Input;
    let project = if from_xml {
        let file = File::open(&args[1]).map_err(|e| format!("cannot open XML: {e}"))?;
        let mut input = String::new();
        file.take(gx_engine::exchange::MAX_XML_BYTES as u64 + 1)
            .read_to_string(&mut input)
            .map_err(|e| format!("cannot read XML: {e}"))?;
        gx_engine::exchange::project_from_xml(&input, &args[2], None)?
    } else if from_objects {
        let catalog = gx_engine::catalog::Catalog::load(&args[1])?;
        let entry = gx_engine::catalog::ObjectId::new(&args[2])?;
        gx_engine::kb::project_from_catalog(&catalog, &entry, None)?
    } else {
        load(&args[1])?
    };
    if command == "explain" {
        let next = load(&args[2])?;
        *category = DiagnosticCategory::Compilation;
        let mut session = gx_engine::incremental::IncrementalCompiler::new();
        session.update(&project)?;
        let result = session.update(&next)?;
        println!(
            "{}",
            serde_json::json!({
                "reparsed": result.reparsed, "reused": result.reused,
                "affected": result.affected, "artifact_cache": false
            })
        );
        return Ok(());
    }
    *category = DiagnosticCategory::Compilation;
    if matches!(command, "check" | "emit") {
        let source = gx_engine::compile(&project)?;
        if command == "emit" {
            print!("{source}");
        } else {
            println!(
                "verified: admitted portable subset; {} procedures; SDK parity unverified",
                project.procedures.len()
            );
        }
        return Ok(());
    }
    let output = args
        .get(if from_objects { 3 } else { 2 })
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            PathBuf::from(&args[1])
                .parent()
                .unwrap_or_else(|| std::path::Path::new("."))
                .join(".gx-engine")
        });
    // Build is an operation-level phase: it includes code generation and rustc.
    *category = DiagnosticCategory::Build;
    let artifact = build::build(&project, &output)?;
    if command == "build" {
        println!("{}", artifact.executable.display());
        return Ok(());
    }
    *category = DiagnosticCategory::Execution;
    let mut command = Command::new(&artifact.executable);
    // A file avoids pipe backpressure while keeping machine-readable stderr clean.
    // Each build generation is unique; only guard a file we created successfully.
    let capture = if json_errors {
        let path = artifact.executable.with_file_name(".run-stderr.log");
        let file = File::create_new(&path)
            .map_err(|e| format!("cannot create generated program stderr capture: {e}"))?;
        command.stderr(file);
        Some(CapturedStderr(path))
    } else {
        None
    };
    let mut child = command
        .spawn()
        .map_err(|e| execution_error(format!("cannot run generated program: {e}"), &capture))?;
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        match child.try_wait() {
            Ok(Some(status)) => {
                return if status.success() {
                    Ok(())
                } else {
                    Err(execution_error(
                        format!("generated program failed ({status})"),
                        &capture,
                    ))
                };
            }
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(10)),
            result => {
                let killed = child.kill();
                let reaped = child.wait();
                let reason = match result {
                    Err(e) => format!("cannot observe generated program: {e}"),
                    _ => "generated program exceeded 10-second deadline".into(),
                };
                return Err(execution_error(
                    format!("{reason}; cleanup kill={killed:?}, wait={reaped:?}"),
                    &capture,
                ));
            }
        }
    }
}

fn main() -> ExitCode {
    let mut args: Vec<String> = env::args().skip(1).collect();
    let json_errors = args.first().is_some_and(|arg| arg == "--diagnostics=json");
    if json_errors {
        args.remove(0);
    }
    // Version probe for host integrations (the MCP Gateway's engine backend gate
    // reads this line); kept dependency-free so it cannot fail on a broken project.
    if args
        .first()
        .is_some_and(|arg| arg == "--version" || arg == "-V")
    {
        println!("gx-engine {}", env!("CARGO_PKG_VERSION"));
        return ExitCode::SUCCESS;
    }
    let mut category = DiagnosticCategory::Usage;
    match execute(args, &mut category, json_errors) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            if json_errors {
                let diagnostic = Diagnostic::new(category, error);
                eprintln!(
                    "{}",
                    serde_json::to_string(&diagnostic).expect("diagnostic serialization")
                );
            } else {
                eprintln!("gx-engine: {error}");
            }
            ExitCode::FAILURE
        }
    }
}
