//! Direct compiler invocation for the generated project.
//!
//! `dotnet build` restores packages, generates MSBuild inputs and then runs the C# compiler. After a
//! `dotnet build` has run, the compiler command MSBuild logged is kept (a template), and later compiles
//! run that same compiler directly with `/shared`, skipping restore and MSBuild. The template is reused
//! only while the project's non-source content is unchanged: its properties, its package references
//! (through `project.assets.json`), the compiler path, and the `.props` and `.targets` files of the SDK folder
//! that holds the compiler. The source list is rebuilt from the project's
//! `Compile` items on every run. A missing or mismatched template, or a compiler run that fails without
//! diagnostics, falls back to `dotnet build`.
//!
//! The compiler reads the same response file MSBuild would, so the output is the same assembly. This is
//! checked by byte comparison in the integration tests and in the live gates, not assumed.

use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::ffi::OsString;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::process::Command;

/// Directory, relative to the project directory, that holds the kept command and its key.
pub const CACHE_DIR: &str = "obj/gx-csc";
const TEMPLATE_FILE: &str = "template.json";
const KEY_FILE: &str = "key.txt";
const RSP_FILE: &str = "build.rsp";
const ASSETS_FILE: &str = "obj/project.assets.json";

/// The compiler command MSBuild used for the project, without its project sources.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Template {
    /// The compiler executable, as MSBuild invoked it.
    pub compiler: String,
    /// Options logged before the first source file, in order.
    pub options_before: Vec<String>,
    /// Options logged after the sources, in order.
    pub options_after: Vec<String>,
    /// Generated sources (under `obj`), logged after the project sources, in order.
    pub generated_sources: Vec<String>,
}

/// What one direct compile produced.
#[derive(Debug, PartialEq, Eq)]
pub enum Verdict {
    /// The compiler succeeded and the outputs were published to `bin`.
    Built { warnings: Vec<String> },
    /// The compiler reported compile errors; the outcome is the same as `dotnet build`'s.
    Diagnostics {
        errors: Vec<String>,
        warnings: Vec<String>,
    },
    /// The direct path cannot be used or cannot be trusted; `dotnet build` must run instead.
    Unavailable(String),
}

/// Splits a logged command line into arguments. A double quote groups text, spaces included, and is kept
/// in the argument so the response file quotes it again.
pub fn split_command_line(text: &str) -> Vec<String> {
    let mut args = Vec::new();
    let mut current = String::new();
    let mut in_quotes = false;
    let mut has_token = false;
    for ch in text.chars() {
        match ch {
            '"' => {
                in_quotes = !in_quotes;
                current.push(ch);
                has_token = true;
            }
            c if c.is_whitespace() && !in_quotes => {
                if has_token {
                    args.push(std::mem::take(&mut current));
                    has_token = false;
                }
            }
            c => {
                current.push(c);
                has_token = true;
            }
        }
    }
    if has_token {
        args.push(current);
    }
    args
}

fn is_source(arg: &str) -> bool {
    !arg.starts_with('/') && (arg.ends_with(".cs") || arg.ends_with(".cs\""))
}

fn is_generated(arg: &str) -> bool {
    arg.trim_matches('"').contains("obj\\")
}

/// Reads the compiler command MSBuild logged for the project at `-v:detailed`. Returns `None` when the
/// log has no such command, or when the command holds an argument that is neither an option nor a
/// source file, or a project source after a generated one. Each of those would make a rebuilt response
/// file differ from the logged one, so the template is refused rather than guessed.
pub fn template_from_log(log: &str) -> Option<Template> {
    let line = log
        .lines()
        .find(|line| line.contains("csc.exe /noconfig"))?
        .trim();
    let split = line.find(" /noconfig")?;
    let compiler = line[..split].trim().to_string();
    if !compiler.to_ascii_lowercase().ends_with("csc.exe") {
        return None;
    }
    let mut template = Template {
        compiler,
        options_before: Vec::new(),
        options_after: Vec::new(),
        generated_sources: Vec::new(),
    };
    let mut seen_source = false;
    let mut seen_generated = false;
    for arg in split_command_line(&line[split + 1..]) {
        if arg.starts_with('/') {
            if seen_source {
                template.options_after.push(arg);
            } else {
                template.options_before.push(arg);
            }
        } else if is_source(&arg) {
            seen_source = true;
            if is_generated(&arg) {
                seen_generated = true;
                template.generated_sources.push(arg);
            } else if seen_generated {
                return None;
            }
        } else {
            return None;
        }
    }
    Some(template)
}

/// The project's `Compile` items, in project order (the order MSBuild passes them to the compiler).
pub fn project_sources(csproj: &str) -> Vec<String> {
    csproj
        .lines()
        .filter_map(|line| {
            let rest = line.trim_start().strip_prefix("<Compile Include=\"")?;
            let end = rest.find('"')?;
            Some(rest[..end].to_string())
        })
        .collect()
}

/// The text that identifies the project's non-source content. `Compile` items are excluded because the
/// source list is rebuilt on every run; everything else, including properties and the package set, is
/// part of the key, as are the compiler path and the digest of `project.assets.json`.
pub fn key_text(csproj: &str, assets: &[u8], compiler: &str) -> String {
    let mut key = format!("compiler={compiler}\nassets={:016x}\n", fnv1a64(assets));
    for line in csproj
        .lines()
        .filter(|line| !line.trim_start().starts_with("<Compile "))
    {
        key.push_str(line);
        key.push('\n');
    }
    key
}

/// Files MSBuild, the SDK and the compiler read from a project's directory and each of its parents. The kept
/// template freezes the options MSBuild evaluated at capture but not the properties these files set, so a
/// direct compile would reuse stale options after one of them changes. `.editorconfig` is the analyzer
/// configuration Roslyn discovers from the source directories upward. Any of them present refuses the direct
/// path.
const AMBIENT_IMPORTS: [&str; 6] = [
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "Directory.Build.rsp",
    "global.json",
    ".editorconfig",
];

/// The first file MSBuild or the SDK would read for `project_dir` from itself or a parent directory.
pub fn ambient_import(project_dir: &Path) -> Option<PathBuf> {
    project_dir.ancestors().find_map(|dir| {
        AMBIENT_IMPORTS
            .iter()
            .map(|name| dir.join(name))
            .find(|path| path.is_file())
    })
}

/// Whether the project file imports a file of its own. The generated project has no `<Import>`; one that
/// does imports something this module does not evaluate, so the direct path refuses it.
pub fn declares_import(csproj: &str) -> bool {
    csproj.lines().any(|line| {
        let line = line.trim_start().to_ascii_lowercase();
        line.starts_with("<import ") || line.starts_with("<import>")
    })
}

/// The restore outputs MSBuild imports from the project's `obj` directory (`*.nuget.g.props` and
/// `*.nuget.g.targets`), each by name and digest, in name order.
fn restore_imports(project_dir: &Path) -> io::Result<String> {
    let obj = project_dir.join("obj");
    let mut names = Vec::new();
    if obj.is_dir() {
        for entry in fs::read_dir(&obj)? {
            let name = entry?.file_name().to_string_lossy().into_owned();
            if name.ends_with(".nuget.g.props") || name.ends_with(".nuget.g.targets") {
                names.push(name);
            }
        }
    }
    names.sort();
    let mut text = String::new();
    for name in names {
        let bytes = fs::read(obj.join(&name))?;
        text.push_str(&format!("restore={name}:{:016x}\n", fnv1a64(&bytes)));
    }
    Ok(text)
}

/// The first entry under `dir` whose file name `wanted` accepts. Directories are searched recursively; a
/// link counts as an entry, not a directory, so the walk cannot loop. An absent `dir` has none.
fn find_under(dir: &Path, wanted: &dyn Fn(&str) -> bool) -> io::Result<Option<PathBuf>> {
    if !dir.is_dir() {
        return Ok(None);
    }
    for entry in fs::read_dir(dir)? {
        let entry = entry?;
        let path = entry.path();
        let found = if entry.file_type()?.is_dir() {
            find_under(&path, wanted)?
        } else if wanted(&entry.file_name().to_string_lossy()) {
            Some(path)
        } else {
            None
        };
        if found.is_some() {
            return Ok(found);
        }
    }
    Ok(None)
}

/// A digest of the environment. MSBuild reads environment variables as properties, so a kept command evaluated
/// under one environment must not be reused under another. The digest keeps no values, so no secret reaches
/// the key file.
pub fn environment_digest(vars: impl IntoIterator<Item = (OsString, OsString)>) -> String {
    let mut pairs: Vec<(OsString, OsString)> = vars.into_iter().collect();
    pairs.sort();
    let mut bytes = Vec::new();
    for (name, value) in pairs {
        bytes.extend_from_slice(name.as_encoded_bytes());
        bytes.push(b'=');
        bytes.extend_from_slice(value.as_encoded_bytes());
        bytes.push(0);
    }
    format!("{:016x}", fnv1a64(&bytes))
}

/// What the kept command's compiler is, by content and by metadata. A servicing update replaces the compiler
/// under the same path, so the path alone cannot tell two compilers apart. `csc.exe` and `csc.dll` are hashed by
/// content. The other files in the compiler's directory are keyed by name, size and modification time, which
/// is what an in-place update changes without the cost of hashing every assembly on each compile.
pub fn compiler_identity(compiler: &str) -> io::Result<String> {
    let path = Path::new(compiler);
    let dir = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    let mut text = format!("compiler-content={:016x}\n", fnv1a64(&fs::read(path)?));
    let library = dir.join("csc.dll");
    if library.is_file() {
        text.push_str(&format!(
            "compiler-library-content={:016x}\n",
            fnv1a64(&fs::read(&library)?)
        ));
    }
    let mut entries: Vec<(String, u64, u128)> = Vec::new();
    for entry in fs::read_dir(dir)? {
        let entry = entry?;
        let metadata = entry.metadata()?;
        if !metadata.is_file() {
            continue;
        }
        let modified = metadata
            .modified()?
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(io::Error::other)?
            .as_nanos();
        entries.push((
            entry.file_name().to_string_lossy().into_owned(),
            metadata.len(),
            modified,
        ));
    }
    entries.sort();
    for (name, len, modified) in entries {
        text.push_str(&format!("compiler-file={name}:{len}:{modified}\n"));
    }
    Ok(text)
}

/// The SDK folder that holds the compiler: `<sdk>/Roslyn/bincore/csc.exe` gives `<sdk>`. Any other layout has no
/// known SDK folder, so the direct path cannot name the SDK inputs its options come from.
pub fn sdk_root_of(compiler: &str) -> Option<PathBuf> {
    let bincore = Path::new(compiler).parent()?;
    let roslyn = bincore.parent()?;
    let sdk = roslyn.parent()?;
    let in_layout = bincore.file_name()?.eq_ignore_ascii_case("bincore")
        && roslyn.file_name()?.eq_ignore_ascii_case("roslyn");
    in_layout.then(|| sdk.to_path_buf())
}

/// Content identities of the `.props` and `.targets` files under `sdk_root`, in relative-path order. MSBuild and
/// the SDK evaluate these to choose the options the kept template froze, and the template's key does not cover
/// them. A link, or any other entry that is neither a file nor a folder, is an error: nothing is skipped silently.
pub fn sdk_inputs(sdk_root: &Path) -> io::Result<String> {
    let mut files = Vec::new();
    collect_sdk_inputs(sdk_root, sdk_root, &mut files)?;
    files.sort();
    let mut text = format!("sdk-inputs={}\n", files.len());
    for (name, path) in files {
        text.push_str(&format!(
            "sdk-input={name}:{:016x}\n",
            fnv1a64(&fs::read(&path)?)
        ));
    }
    Ok(text)
}

fn collect_sdk_inputs(
    root: &Path,
    dir: &Path,
    files: &mut Vec<(String, PathBuf)>,
) -> io::Result<()> {
    for entry in fs::read_dir(dir)? {
        let entry = entry?;
        let path = entry.path();
        let kind = entry.file_type()?;
        if kind.is_dir() {
            collect_sdk_inputs(root, &path, files)?;
        } else if kind.is_file() {
            let name = entry.file_name().to_string_lossy().to_ascii_lowercase();
            if name.ends_with(".props") || name.ends_with(".targets") {
                let relative = path.strip_prefix(root).map_err(io::Error::other)?;
                files.push((relative.to_string_lossy().replace('\\', "/"), path));
            }
        } else {
            return Err(io::Error::other(format!(
                "{} is a link or another entry that the SDK inputs cannot be read through",
                path.display()
            )));
        }
    }
    Ok(())
}

/// The compiler's identity together with the SDK inputs of the folder it belongs to: what the kept template
/// depends on outside the project. Refused when the compiler is not in an SDK's `Roslyn/bincore` folder, or when
/// one of those files cannot be read.
pub fn compiler_and_sdk_identity(compiler: &str) -> Result<String, String> {
    let compiler_text = compiler_identity(compiler)
        .map_err(|error| format!("cannot read the compiler: {error}"))?;
    let sdk_root = sdk_root_of(compiler).ok_or_else(|| {
        format!(
            "{compiler} is not in the Roslyn/bincore folder of a .NET SDK, so the SDK inputs its options come from cannot be fingerprinted"
        )
    })?;
    let inputs = sdk_inputs(&sdk_root).map_err(|error| {
        format!(
            "cannot read the SDK inputs under {}: {error}",
            sdk_root.display()
        )
    })?;
    Ok(format!("{compiler_text}{inputs}"))
}

/// The key the direct path compares, or the reason the direct path cannot be used: an explicit import, an
/// ambient import file, analyzer configuration under the project, a file in the user-level MSBuild folder,
/// restore outputs that cannot be read, or a compiler or SDK input that cannot be read. The environment, the
/// compiler's identity and the SDK inputs are part of the key.
pub fn guarded_key(
    project_dir: &Path,
    csproj: &str,
    assets: &[u8],
    compiler: &str,
) -> Result<String, String> {
    let user_dir = std::env::var_os("LOCALAPPDATA")
        .map(|local| PathBuf::from(local).join("Microsoft").join("MSBuild"));
    let environment = environment_digest(std::env::vars_os());
    let identity = compiler_and_sdk_identity(compiler)?;
    guarded_key_with(
        project_dir,
        csproj,
        assets,
        compiler,
        user_dir.as_deref(),
        &environment,
        &identity,
    )
}

/// `guarded_key` with its ambient inputs passed in, so each refusal can be tested without the real user
/// profile or environment.
pub fn guarded_key_with(
    project_dir: &Path,
    csproj: &str,
    assets: &[u8],
    compiler: &str,
    user_dir: Option<&Path>,
    environment: &str,
    identity: &str,
) -> Result<String, String> {
    if declares_import(csproj) {
        return Err(
            "the project imports a file of its own; the direct path does not evaluate it"
                .to_string(),
        );
    }
    if let Some(path) = ambient_import(project_dir) {
        return Err(format!(
            "MSBuild would import {}; the direct path does not evaluate it",
            path.display()
        ));
    }
    match find_under(project_dir, &|name| {
        name.eq_ignore_ascii_case(".editorconfig")
    }) {
        Ok(Some(path)) => {
            return Err(format!(
                "{} is analyzer configuration the direct path does not evaluate",
                path.display()
            ));
        }
        Ok(None) => {}
        Err(error) => return Err(format!("cannot read the project directory: {error}")),
    }
    if let Some(dir) = user_dir {
        match find_under(dir, &|_| true) {
            Ok(Some(path)) => {
                return Err(format!(
                    "MSBuild would import {}; the direct path does not evaluate the user-level extensions",
                    path.display()
                ));
            }
            Ok(None) => {}
            Err(error) => {
                return Err(format!(
                    "cannot read the user-level MSBuild folder: {error}"
                ));
            }
        }
    }
    let restore = restore_imports(project_dir)
        .map_err(|error| format!("cannot read the restore outputs: {error}"))?;
    let runtime = executable_runtime_identity(project_dir, csproj)?;
    Ok(format!(
        "{}{restore}environment={environment}\n{identity}{runtime}",
        key_text(csproj, assets, compiler)
    ))
}

/// Executable replay reuses only SDK-produced launch/dependency files that still match
/// the captured build. It never synthesizes an apphost or a runtime configuration.
fn executable_runtime_identity(project_dir: &Path, csproj: &str) -> Result<String, String> {
    let document = roxmltree::Document::parse(csproj)
        .map_err(|error| format!("cannot read project properties: {error}"))?;
    let value = |name: &str| {
        document
            .descendants()
            .find(|node| node.has_tag_name(name))
            .and_then(|node| node.text())
    };
    if value("OutputType") != Some("Exe") {
        return Ok(String::new());
    }
    let name = value("AssemblyName").ok_or("executable has no AssemblyName")?;
    let target = value("TargetFramework").ok_or("executable has no TargetFramework")?;
    for component in [name, target] {
        if component.is_empty()
            || component == "."
            || component == ".."
            || !component
                .bytes()
                .all(|ch| ch.is_ascii_alphanumeric() || matches!(ch, b'_' | b'.' | b'-'))
        {
            return Err("executable output layout is not a simple assembly/framework name".into());
        }
    }
    let bin = project_dir.join("bin").join("Release").join(target);
    for required in [
        format!("{name}.deps.json"),
        format!("{name}.runtimeconfig.json"),
        format!("{name}{}", std::env::consts::EXE_SUFFIX),
    ] {
        if !bin.join(required).is_file() {
            return Err("executable needs SDK-produced apphost, deps and runtimeconfig".into());
        }
    }
    let mut paths = Vec::new();
    runtime_files(&bin, &mut paths)
        .map_err(|error| format!("cannot fingerprint executable runtime: {error}"))?;
    paths.sort();
    let mut identity = String::from("executable-runtime\n");
    for path in paths {
        let relative = path.strip_prefix(&bin).map_err(|error| error.to_string())?;
        if relative == Path::new(&format!("{name}.dll"))
            || relative == Path::new(&format!("{name}.pdb"))
        {
            continue;
        }
        let bytes =
            fs::read(&path).map_err(|error| format!("cannot read executable runtime: {error}"))?;
        identity.push_str(&format!(
            "{}:{:016x}\n",
            relative.display(),
            fnv1a64(&bytes)
        ));
    }
    Ok(identity)
}

fn runtime_files(dir: &Path, paths: &mut Vec<PathBuf>) -> io::Result<()> {
    for entry in fs::read_dir(dir)? {
        let entry = entry?;
        let kind = entry.file_type()?;
        if kind.is_dir() {
            runtime_files(&entry.path(), paths)?;
        } else if kind.is_file() {
            paths.push(entry.path());
        } else {
            return Err(io::Error::other(
                "runtime contains a link or unsupported entry",
            ));
        }
    }
    Ok(())
}

/// The response file for `sources`: logged options and sources in the logged order.
pub fn render_rsp(template: &Template, sources: &[String]) -> String {
    let mut lines: Vec<&str> = Vec::new();
    // `compile` passes `/noconfig` on the command line: csc reports CS2023 for it in a response file.
    lines.extend(
        template
            .options_before
            .iter()
            .map(String::as_str)
            .filter(|option| *option != "/noconfig"),
    );
    lines.extend(sources.iter().map(String::as_str));
    lines.extend(template.generated_sources.iter().map(String::as_str));
    lines.extend(template.options_after.iter().map(String::as_str));
    let mut text = lines.join("\n");
    text.push('\n');
    text
}

/// Turns the compiler's exit status and output into a verdict. Compile errors are reported as they are;
/// a failure with no `error CS` line is not a verdict, and `dotnet build` decides instead.
pub fn classify(success: bool, output: &str) -> Verdict {
    let (errors, warnings) = compiler_diagnostics(output);
    if success {
        Verdict::Built { warnings }
    } else if !errors.is_empty() {
        Verdict::Diagnostics { errors, warnings }
    } else {
        Verdict::Unavailable("the compiler failed without diagnostics".to_string())
    }
}

/// Errors and warnings in compiler or MSBuild output: each distinct message once, in first-seen order.
///
/// MSBuild prints a warning inline and again in its summary, and appends ` [<project>.csproj]` to it. At
/// detailed verbosity each line also gets a node prefix such as `1:7>`. Both are removed, so the
/// `dotnet build` path and the direct compiler report the same list.
pub fn compiler_diagnostics(output: &str) -> (Vec<String>, Vec<String>) {
    let mut errors = Vec::new();
    let mut warnings = Vec::new();
    let mut seen = HashSet::new();
    for line in output.lines() {
        let message = strip_project_suffix(strip_node_prefix(line.trim()));
        if message.contains("error CS") || message.contains(": error ") {
            if seen.insert(message.to_string()) {
                errors.push(message.to_string());
            }
        } else if message.contains("warning CS") && seen.insert(message.to_string()) {
            warnings.push(message.to_string());
        }
    }
    (errors, warnings)
}

/// Removes a leading MSBuild node prefix (`1:7>`) from a line of detailed output.
fn strip_node_prefix(line: &str) -> &str {
    let Some((node, rest)) = line.split_once('>') else {
        return line;
    };
    let is_node = node.split_once(':').is_some_and(|(project, _)| {
        !project.is_empty()
            && project.bytes().all(|c| c.is_ascii_digit())
            && node.len() > project.len() + 1
            && node[project.len() + 1..]
                .bytes()
                .all(|c| c.is_ascii_digit())
    });
    if is_node { rest } else { line }
}

/// Removes MSBuild's trailing ` [<project>.csproj]` from a diagnostic line.
fn strip_project_suffix(line: &str) -> &str {
    if !line.ends_with(']') {
        return line;
    }
    match line.rfind(" [") {
        Some(at) if line[at..].contains(".csproj]") => line[..at].trim_end(),
        _ => line,
    }
}

/// Keeps the compiler command from a `dotnet build -v:detailed` log for the project at `csproj_path`.
/// Returns `false` when the log holds no usable command. Call it after `dotnet build` has succeeded, so
/// `project.assets.json` exists.
pub fn capture(csproj_path: &Path, log: &str) -> io::Result<bool> {
    let Some(template) = template_from_log(log) else {
        return Ok(false);
    };
    let project_dir = project_dir_of(csproj_path);
    let csproj = fs::read_to_string(csproj_path)?;
    let assets = fs::read(project_dir.join(ASSETS_FILE))?;
    // A project the direct path cannot evaluate keeps no command: MSBuild stays the only compiler for it.
    let Ok(key) = guarded_key(&project_dir, &csproj, &assets, &template.compiler) else {
        return Ok(false);
    };
    let dir = project_dir.join(CACHE_DIR);
    fs::create_dir_all(&dir)?;
    // The key goes last and the old key goes first, so a key file always belongs to a complete template.
    let _ = fs::remove_file(dir.join(KEY_FILE));
    let encoded = serde_json::to_vec(&template).map_err(io::Error::other)?;
    fs::write(dir.join(TEMPLATE_FILE), encoded)?;
    fs::write(dir.join(KEY_FILE), key)?;
    Ok(true)
}

/// Compiles the project directly with the kept compiler command, when the kept template matches the
/// project. On success the outputs are published to `bin` the way MSBuild publishes them.
pub fn compile(csproj_path: &Path) -> Verdict {
    let project_dir = project_dir_of(csproj_path);
    let dir = project_dir.join(CACHE_DIR);
    let Some(template) = fs::read(dir.join(TEMPLATE_FILE))
        .ok()
        .and_then(|bytes| serde_json::from_slice::<Template>(&bytes).ok())
    else {
        return Verdict::Unavailable("no kept compiler command".to_string());
    };
    let csproj = match fs::read_to_string(csproj_path) {
        Ok(text) => text,
        Err(error) => return Verdict::Unavailable(format!("cannot read the project: {error}")),
    };
    let assets = match fs::read(project_dir.join(ASSETS_FILE)) {
        Ok(bytes) => bytes,
        Err(error) => return Verdict::Unavailable(format!("cannot read the assets: {error}")),
    };
    let expected = match guarded_key(&project_dir, &csproj, &assets, &template.compiler) {
        Ok(key) => key,
        Err(reason) => return Verdict::Unavailable(reason),
    };
    match fs::read_to_string(dir.join(KEY_FILE)) {
        Ok(stored) if stored == expected => {}
        _ => {
            return Verdict::Unavailable(
                "the project changed since its compiler command was kept".to_string(),
            );
        }
    }
    let Some(out) = output_path(&template) else {
        return Verdict::Unavailable("the kept command has no /out option".to_string());
    };
    let sources: Vec<String> = project_sources(&csproj)
        .into_iter()
        .map(|source| {
            if source.contains(' ') {
                format!("\"{source}\"")
            } else {
                source
            }
        })
        .collect();
    let rsp_path = dir.join(RSP_FILE);
    if let Err(error) = fs::write(&rsp_path, render_rsp(&template, &sources)) {
        return Verdict::Unavailable(format!("cannot write the response file: {error}"));
    }
    for option in template
        .options_before
        .iter()
        .chain(&template.options_after)
    {
        if let Some(path) = option
            .strip_prefix("/out:")
            .or_else(|| option.strip_prefix("/refout:"))
            .or_else(|| option.strip_prefix("/pdb:"))
        {
            if let Some(parent) = project_dir.join(unquote(path)).parent() {
                let _ = fs::create_dir_all(parent);
            }
        }
    }
    let output = match Command::new(&template.compiler)
        .current_dir(&project_dir)
        .arg("/noconfig")
        .arg("/shared")
        .arg(format!("@{}", rsp_path.display()))
        .output()
    {
        Ok(output) => output,
        Err(error) => {
            return Verdict::Unavailable(format!("cannot start the compiler: {error}"));
        }
    };
    let text = format!(
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let verdict = classify(output.status.success(), &text);
    if matches!(verdict, Verdict::Built { .. }) {
        if let Err(error) = publish(&project_dir, &project_dir.join(unquote(&out))) {
            return Verdict::Unavailable(format!("cannot publish the outputs: {error}"));
        }
    }
    verdict
}

/// The `/out:` path of a kept command, unquoted.
fn output_path(template: &Template) -> Option<String> {
    template
        .options_before
        .iter()
        .chain(&template.options_after)
        .find_map(|option| option.strip_prefix("/out:"))
        .map(|path| unquote(path).to_string())
}

/// Copies the assembly and its PDB from the compiler output directory (`obj/<config>/<tfm>`) to the
/// MSBuild output directory (`bin/<config>/<tfm>`).
fn publish(project_dir: &Path, out: &Path) -> io::Result<()> {
    let file_name = out
        .file_name()
        .ok_or_else(|| io::Error::other("the output path has no file name"))?;
    let tfm_dir = out
        .parent()
        .and_then(Path::file_name)
        .ok_or_else(|| io::Error::other("the output path has no target directory"))?;
    let config_dir = out
        .parent()
        .and_then(Path::parent)
        .and_then(Path::file_name)
        .ok_or_else(|| io::Error::other("the output path has no configuration directory"))?;
    let bin = project_dir.join("bin").join(config_dir).join(tfm_dir);
    fs::create_dir_all(&bin)?;
    fs::copy(out, bin.join(file_name))?;
    let pdb = out.with_extension("pdb");
    if pdb.is_file() {
        fs::copy(&pdb, bin.join(pdb.file_name().unwrap_or_default()))?;
    }
    Ok(())
}

fn unquote(text: &str) -> &str {
    text.trim_matches('"')
}

/// The project's folder, absolute: the compiler runs inside it, and the ambient-import walk climbs from
/// it, so a relative project path (`rebuild-all --output out`) would resolve against the wrong folder.
fn project_dir_of(csproj_path: &Path) -> PathBuf {
    let csproj_path =
        std::path::absolute(csproj_path).unwrap_or_else(|_| csproj_path.to_path_buf());
    csproj_path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or(Path::new("."))
        .to_path_buf()
}

fn fnv1a64(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0100_0000_01b3);
    }
    hash
}
