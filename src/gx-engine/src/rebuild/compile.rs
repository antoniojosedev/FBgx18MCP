//! Phase 3: compiles the emitted main project with `csc` or `dotnet build`.

use super::parallel::Findings;
use std::{path::Path, process::Command, time::Instant};

/// Runs `dotnet build` on the main project. Returns the elapsed milliseconds and whether the
/// project built.
pub(super) fn compile_phase(
    main_csproj_path: &Path,
    concurrency: usize,
    findings: &mut Findings,
) -> (u64, bool) {
    let compile_start = Instant::now();
    // A kept compiler command runs first. It is used only while the project's non-source content matches
    // the content the command was captured from; otherwise `dotnet build` runs and captures a new one.
    // Executables reuse only unchanged SDK-produced runtime files; a missing or changed
    // bootstrap makes the guarded direct path fall back to MSBuild.
    match crate::csc_direct::compile(main_csproj_path) {
        crate::csc_direct::Verdict::Built { warnings } => {
            findings.warnings.extend(warnings);
            eprintln!("compile path: csc-direct");
            return (compile_start.elapsed().as_millis() as u64, true);
        }
        crate::csc_direct::Verdict::Diagnostics { errors, warnings } => {
            findings.errors.extend(errors);
            findings.warnings.extend(warnings);
            eprintln!("compile path: csc-direct (diagnostics)");
            return (compile_start.elapsed().as_millis() as u64, false);
        }
        crate::csc_direct::Verdict::Unavailable(reason) => {
            eprintln!("compile path: dotnet build ({reason})");
        }
    }
    let built = dotnet_build(main_csproj_path, concurrency, findings);
    (compile_start.elapsed().as_millis() as u64, built)
}

fn dotnet_build(main_csproj_path: &Path, concurrency: usize, findings: &mut Findings) -> bool {
    if Command::new("dotnet").arg("--version").output().is_err() {
        findings
            .warnings
            .push("dotnet CLI is not installed; skipped compilation driver".to_string());
        return false;
    }

    let mut cmd = Command::new("dotnet");
    cmd.arg("build");
    cmd.arg(main_csproj_path);
    cmd.arg("-c");
    cmd.arg("Release");
    cmd.arg("--nologo");
    // Detailed output carries the compiler command that the direct path replays.
    cmd.arg("-v:detailed");

    if concurrency > 0 {
        cmd.arg(format!("-maxcpucount:{concurrency}"));
    } else {
        cmd.arg("-m");
    }

    let output = match cmd.output() {
        Ok(output) => output,
        Err(err) => {
            findings
                .errors
                .push(format!("cannot invoke dotnet build: {err}"));
            return false;
        }
    };
    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr);

    // MSBuild reports project and restore failures as `: error NETSDK...` or
    // `: error NU...`, not as `error CS`; all of them are build errors. Repeats and
    // node prefixes are removed by `compiler_diagnostics`.
    let (found_errors, found_warnings) =
        crate::csc_direct::compiler_diagnostics(&format!("{stdout}\n{stderr}"));
    findings.errors.extend(found_errors);
    findings.warnings.extend(found_warnings);

    let built = output.status.success();
    if built {
        let _ = crate::csc_direct::capture(main_csproj_path, &stdout);
    } else {
        // A failed build is an error whatever its diagnostics. It used to become a
        // warning when no `error CS` line was printed, and the rebuild exited 0.
        findings.errors.push(format!(
            "dotnet build failed with exit code {} (check package restore and runtime availability)",
            output.status
        ));
    }
    built
}
