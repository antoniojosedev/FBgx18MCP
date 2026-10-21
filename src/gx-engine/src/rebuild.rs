//! High-performance parallel Rebuild All pipeline.
//!
//! Replaces the legacy 8-minute GeneXus IDE and MSBuild rebuild workflow by:
//! 1. Specifying the entire Knowledge Base concurrently with scoped threads.
//! 2. Emitting idiomatic C# (.NET) source and SDK-compatible project definitions.
//! 3. Driving multi-core Roslyn builds with structured diagnostic capture.

#![forbid(unsafe_code)]

mod classify;
mod compile;
mod emission;
mod parallel;
mod specify;

use crate::{
    catalog::{Catalog, ObjectId, ObjectKind},
    csharp::{ProcedureClass, webform::WebPanelEnvironment},
    navigation::NavigationReport,
    table::SqlDialect,
};
use classify::classify_objects;
use compile::compile_phase;
use emission::emission_phase;
use parallel::Findings;
use serde::{Deserialize, Serialize};
use specify::specify_phase;
use std::{
    collections::{BTreeMap, BTreeSet},
    path::{Path, PathBuf},
    time::Instant,
};

/// Configuration options for the Rebuild All pipeline.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RebuildOptions {
    /// Target .NET runtime framework (e.g. "net8.0", "net9.0").
    pub target_framework: String,
    /// Whether to clean the output directory before generating files.
    pub clean: bool,
    /// Concurrency level for parallel phases (0 = automatic based on available cores).
    pub concurrency: usize,
    /// SQL dialect for SQL navigation and table schema derivation.
    pub sql_dialect: SqlDialect,
    /// Whether to abort on the first encountered error.
    pub fail_fast: bool,
    /// Whether to skip invoking `dotnet build` compiler after emission.
    pub skip_compile: bool,
    /// Optional application assembly name.
    pub assembly_name: Option<String>,
    /// NuGet version of `GeneXus.Classes.Web.Core`, the runtime the generated classes
    /// derive from. Written as declared by the KB (`[1.30.0]`), see
    /// [`genexus_runtime_version_from_kb`].
    pub genexus_runtime_version: String,
    /// Explicit configuration for plain WebPanels (theme, generator, master class paths and the
    /// build number). An unset value refuses every plain panel by name; see [`WebPanelEnvironment`].
    pub web_environment: WebPanelEnvironment,
    /// The class whose `Main` starts the application when the main project is an executable. Set only
    /// by a single-object closure build (see [`closure_startup_object`]); the full rebuild stays a library.
    pub startup_object: Option<String>,
    /// The web model's client configuration script `gxcfg.js`, or why it is not written (see
    /// [`crate::csharp::webform::web_client_config_from_kb`]). Written with the web classes; a reason
    /// here fails a run that emits one.
    pub client_config: Option<Result<String, String>>,
    /// Names of GeneXus library structured data types whose classes the GeneXus runtime assemblies
    /// already define (see [`runtime_sdt_names_from_kb`]). Such an SDT is never emitted, even when a
    /// generated object reaches it: the SDK's generated code uses the runtime class (KBTeste `WWTrn353`
    /// uses the runtime `GridState`, and the SDK output holds no `type_SdtGridState.cs`).
    pub runtime_sdts: BTreeSet<String>,
    /// The model files the SDK writes beside the classes that this run reproduces from the KB and the
    /// install (`messages.<lang>.txt/.js`, `log.config`, `CloudServices.config`; see
    /// [`crate::csharp::model_files`]), each group with its label, or why the group is not written. Written
    /// with the support files; a reason here fails a run that writes them.
    pub model_files: Vec<crate::csharp::model_files::ModelFileGroup>,
}

/// The GeneXus runtime assemblies a web model links (`NetModel/web/bin`), searched for SDT classes.
const RUNTIME_ASSEMBLIES: [&str; 2] = ["GxClasses.dll", "GxClasses.Web.dll"];

/// Which of `library_sdts` the KB's GeneXus runtime assemblies define: a name whose class `Sdt<Name>` is a
/// metadata string (NUL-terminated) of `NetModel/web/bin/GxClasses.dll` or `GxClasses.Web.dll`. A KB
/// without those assemblies yields an empty set, so every reached library SDT is still emitted.
pub fn runtime_sdt_names_from_kb<'a>(
    kb_dir: &Path,
    library_sdts: impl IntoIterator<Item = &'a str>,
) -> BTreeSet<String> {
    let bin = kb_dir.join("NetModel").join("web").join("bin");
    let assemblies: Vec<Vec<u8>> = RUNTIME_ASSEMBLIES
        .iter()
        .filter_map(|name| std::fs::read(bin.join(name)).ok())
        .collect();
    // One pass over the assemblies: every `Sdt<name>\0` they contain, at any offset. A library SDT
    // is runtime-provided when its `Sdt<name>\0` occurs, as with a search per name.
    let mut defined: std::collections::HashSet<&[u8]> = std::collections::HashSet::new();
    for bytes in &assemblies {
        let mut at = 0;
        while let Some(found) = bytes[at..].windows(3).position(|w| w == b"Sdt") {
            let start = at + found + 3;
            if let Some(end) = bytes[start..].iter().position(|&b| b == 0) {
                defined.insert(&bytes[start..start + end]);
            }
            at = at + found + 1;
        }
    }
    library_sdts
        .into_iter()
        .filter(|name| defined.contains(name.as_bytes()))
        .map(str::to_string)
        .collect()
}

/// The runtime version used when the KB declares none (a KB that was never built has no
/// `NetModel/Directory.build.props`). This is the GeneXus 18 runtime available in the
/// NuGet cache; a GeneXus 16 KB without a declaration is compiled against it and the
/// result is reported as the build outcome, not assumed.
pub const DEFAULT_GENEXUS_RUNTIME_VERSION: &str = "[1.30.0]";

/// Reads `GenStdVersion` from a KB's `NetModel/Directory.build.props`, the file GeneXus
/// uses to pin the runtime for every object project. Returns `None` when the file or the
/// property is absent, and for a value that is not a version range.
pub fn genexus_runtime_version_from_kb(kb_dir: &Path) -> Option<String> {
    let props = kb_dir.join("NetModel").join("Directory.build.props");
    let text = std::fs::read_to_string(props).ok()?;
    let document = roxmltree::Document::parse(&text).ok()?;
    let value = document
        .descendants()
        .find(|node| node.has_tag_name("GenStdVersion"))?
        .text()?
        .trim()
        .to_string();
    let well_formed = value
        .trim_start_matches('[')
        .trim_end_matches(']')
        .chars()
        .all(|c| c.is_ascii_digit() || c == '.')
        && value.chars().any(|c| c.is_ascii_digit());
    well_formed.then_some(value)
}

impl Default for RebuildOptions {
    fn default() -> Self {
        Self {
            target_framework: "net8.0".to_string(),
            clean: true,
            concurrency: 0,
            sql_dialect: SqlDialect::SqlServer,
            fail_fast: false,
            skip_compile: false,
            assembly_name: None,
            genexus_runtime_version: DEFAULT_GENEXUS_RUNTIME_VERSION.to_string(),
            web_environment: WebPanelEnvironment::default(),
            startup_object: None,
            client_config: None,
            runtime_sdts: BTreeSet::new(),
            model_files: Vec::new(),
        }
    }
}

impl RebuildOptions {
    pub fn new(target_framework: impl Into<String>) -> Self {
        Self {
            target_framework: target_framework.into(),
            ..Default::default()
        }
    }

    pub fn with_model_files(
        mut self,
        model_files: Vec<crate::csharp::model_files::ModelFileGroup>,
    ) -> Self {
        self.model_files = model_files;
        self
    }

    pub fn with_client_config(mut self, client_config: Result<String, String>) -> Self {
        self.client_config = Some(client_config);
        self
    }

    pub fn with_web_environment(mut self, web_environment: WebPanelEnvironment) -> Self {
        self.web_environment = web_environment;
        self
    }

    pub fn with_concurrency(mut self, concurrency: usize) -> Self {
        self.concurrency = concurrency;
        self
    }

    pub fn with_sql_dialect(mut self, dialect: SqlDialect) -> Self {
        self.sql_dialect = dialect;
        self
    }

    pub fn with_clean(mut self, clean: bool) -> Self {
        self.clean = clean;
        self
    }

    pub fn with_fail_fast(mut self, fail_fast: bool) -> Self {
        self.fail_fast = fail_fast;
        self
    }

    /// Builds the main project as an executable that starts at `Main` of `startup_object`.
    pub fn with_startup_object(mut self, startup_object: impl Into<String>) -> Self {
        self.startup_object = Some(startup_object.into());
        self
    }

    pub fn with_skip_compile(mut self, skip: bool) -> Self {
        self.skip_compile = skip;
        self
    }

    pub fn with_assembly_name(mut self, name: impl Into<String>) -> Self {
        self.assembly_name = Some(name.into());
        self
    }

    pub fn with_genexus_runtime_version(mut self, version: impl Into<String>) -> Self {
        self.genexus_runtime_version = version.into();
        self
    }
}

/// A catalog object the rebuild did not turn into a compile unit, by name and kind.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SkippedObject {
    pub name: String,
    pub kind: String,
    pub reason: String,
}

/// Outcome of a rebuild. Only `Complete` is success.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum RebuildStatus {
    /// Every generated object was emitted and the project built; nothing was skipped.
    Complete,
    /// The run is not a full build: web UI objects were skipped, a code kind has no
    /// generator, or compilation did not run (for example, dotnet is missing).
    Partial,
    /// At least one error was recorded.
    Failed,
}

/// Structured execution summary and timing report produced by `rebuild_all`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RebuildReport {
    pub read_ms: u64,
    pub spec_ms: u64,
    pub emit_ms: u64,
    pub compile_ms: u64,
    pub total_ms: u64,
    /// Time to load the catalog before the pipeline (a KB read, or a snapshot when the cache is used). It is
    /// not part of `total_ms`. The CLI measures and sets it; a library call leaves it at zero.
    #[serde(default)]
    pub catalog_load_ms: u64,

    pub procedures_count: usize,
    pub transactions_count: usize,
    pub sdts_count: usize,
    pub webpanels_count: usize,
    pub total_objects: usize,

    /// `complete`, `partial` or `failed`; see [`RebuildStatus`].
    pub status: RebuildStatus,
    /// C# compile units emitted (Procedures and SDTs).
    pub generated_count: usize,
    /// Compile units of a project that built; 0 when compilation did not run or failed.
    pub compiled_count: usize,
    /// Web UI objects (Transactions and WebPanels) outside a matched shape, reason
    /// `web UI, not generated: <why>`.
    pub skipped_count: usize,
    pub skipped_objects: Vec<SkippedObject>,
    /// Objects of kinds this engine has no generator for (DataProvider, DataSelector,
    /// DataView), reason `no generator, not generated`.
    pub not_generated_count: usize,
    pub not_generated_objects: Vec<SkippedObject>,
    /// GeneXus library structured data types that no generated object reaches. They are not built and
    /// are not an error: the GeneXus runtime provides them (see `crate::reachability`).
    #[serde(default)]
    pub not_targeted_count: usize,
    #[serde(default)]
    pub not_targeted_objects: Vec<SkippedObject>,
    /// Procedures refused because they need a web UI or not-generated object. Each one is also
    /// an error, so the run is `failed`, never `partial`.
    pub refused_count: usize,
    pub refused_objects: Vec<SkippedObject>,
    /// Objects with no compile unit by nature (Module, Domain, Attribute, ...), by kind name.
    pub definition_kinds: BTreeMap<String, usize>,

    pub errors: Vec<String>,
    pub warnings: Vec<String>,
    pub emitted_files: Vec<PathBuf>,
    pub navigation_reports: Vec<NavigationReport>,
}

impl RebuildReport {
    /// True only for a complete rebuild. A partial run is never reported as success.
    pub fn is_success(&self) -> bool {
        self.status == RebuildStatus::Complete
    }
}

/// Executes the end-to-end parallel Rebuild All pipeline.
pub fn rebuild_all(
    catalog: &Catalog,
    output_dir: &Path,
    options: &RebuildOptions,
) -> Result<RebuildReport, String> {
    let total_start = Instant::now();

    // Phase 0: Object Classification
    let (manifest, read_ms) =
        classify_objects(catalog, &options.web_environment, &options.runtime_sdts);

    let worker_concurrency = if options.concurrency == 0 {
        std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(4)
    } else {
        options.concurrency
    };

    let mut findings = Findings::default();

    // A refused object was requested and cannot be built from this catalog: it is an error. The message names
    // the object's own kind: a refused Transaction is not a Procedure.
    for refused in &manifest.refused {
        findings.errors.push(format!(
            "refused to build {} '{}': {}",
            refused.kind, refused.name, refused.reason
        ));
    }

    // Phase 1: Parallel Specification
    let (nav_reports, spec_ms) = specify_phase(
        &manifest,
        catalog,
        worker_concurrency,
        options,
        &mut findings,
    )?;

    if options.fail_fast && !findings.errors.is_empty() {
        return Err(findings.errors.join("; "));
    }

    // Phase 2: Parallel C# & Project Code Emission
    let (emitted_files, main_csproj_path, emit_ms) = emission_phase(
        &manifest,
        output_dir,
        worker_concurrency,
        options,
        &mut findings,
    )?;

    // Phase 3: Parallel Compilation Driver (`dotnet build`)
    let (compile_ms, built) = if !options.skip_compile {
        compile_phase(&main_csproj_path, worker_concurrency, &mut findings)
    } else {
        (0, false)
    };

    let total_ms = total_start.elapsed().as_millis() as u64;
    let Findings {
        errors: final_errors,
        warnings: final_warnings,
    } = findings;

    let generated_count = emitted_files
        .iter()
        .filter(|path| path.extension().and_then(|ext| ext.to_str()) == Some("cs"))
        .count();
    // Partial whenever the run is not a full build: a web UI object was skipped, a code
    // kind has no generator, or the project did not build. A partial run is never success.
    let status = if !final_errors.is_empty() {
        RebuildStatus::Failed
    } else if !built || !manifest.skipped.is_empty() || !manifest.not_generated.is_empty() {
        RebuildStatus::Partial
    } else {
        RebuildStatus::Complete
    };
    let webpanels_count = manifest
        .skipped
        .iter()
        .filter(|skipped| skipped.kind == "WebPanel")
        .count();

    Ok(RebuildReport {
        read_ms,
        spec_ms,
        emit_ms,
        compile_ms,
        total_ms,
        catalog_load_ms: 0,
        // Every Procedure in the catalog: buildable, refused and not-generated alike. It counts Procedures
        // only; refused SDTs are reported under `refused_count`, and `sdts_count` is the SDTs emitted.
        procedures_count: catalog
            .objects()
            .filter(|object| object.kind == ObjectKind::Procedure)
            .count(),
        transactions_count: manifest.transactions.len(),
        sdts_count: manifest.sdts.len(),
        webpanels_count,
        total_objects: manifest.total_objects,
        status,
        generated_count,
        compiled_count: if built { generated_count } else { 0 },
        skipped_count: manifest.skipped.len(),
        skipped_objects: manifest.skipped,
        not_generated_count: manifest.not_generated.len(),
        not_generated_objects: manifest.not_generated,
        not_targeted_count: manifest.not_targeted.len(),
        not_targeted_objects: manifest.not_targeted,
        refused_count: manifest.refused.len(),
        refused_objects: manifest.refused,
        definition_kinds: manifest.definitions,
        errors: final_errors,
        warnings: final_warnings,
        emitted_files,
        navigation_reports: nav_reports,
    })
}

/// The entry point of a single-object closure build: the `Main` of the Procedure's core class, the class
/// the SDK starts. Only a parameterless Procedure has a `Main` (`ProcedureClass::is_split`); the class is
/// taken from the emitter, never guessed from a file name. Other kinds and parameterised Procedures are
/// refused by name, before anything is emitted.
pub fn closure_startup_object(catalog: &Catalog, entry: &ObjectId) -> Result<String, String> {
    let object = catalog
        .get(entry)
        .ok_or_else(|| format!("closure entry '{}' is not in the catalog", entry.as_str()))?;
    if object.kind != ObjectKind::Procedure {
        return Err(format!(
            "closure entry '{}' is a {:?}: only a Procedure has a Main to start",
            object.name, object.kind
        ));
    }
    let class = ProcedureClass::from_kb_object(object)?;
    if !class.is_split() {
        return Err(format!(
            "closure entry '{}' takes parameters, so it has no Main: a parameterised closure artifact is not implemented",
            object.name
        ));
    }
    Ok(format!(
        "{}.{}",
        class.namespace,
        class.emitted_class_name()
    ))
}
