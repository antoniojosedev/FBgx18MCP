//! High-performance parallel Rebuild All pipeline.
//!
//! Replaces the legacy 8-minute GeneXus IDE and MSBuild rebuild workflow by:
//! 1. Specifying the entire Knowledge Base concurrently with scoped threads.
//! 2. Emitting idiomatic C# (.NET) source and SDK-compatible project definitions.
//! 3. Driving multi-core Roslyn builds with structured diagnostic capture.

#![forbid(unsafe_code)]

use crate::{
    catalog::{Catalog, KbObject, ObjectId, ObjectKind},
    csharp::{
        CsprojConfig, CsprojEmitter, ProcedureClass, SdtClass,
        calls::CallSignatures,
        procedure::CheckedEmit,
        sdt::SdtIndex,
        webform::{self, WebClass, WebPanelEnvironment},
    },
    navigation::{
        NavigationReport, compute_extended_tables, specify_procedure_with_tables,
        specify_transaction_attribute_formula,
    },
    table::{SqlDialect, Table, derive_tables_from_transaction, derive_tables_partial},
    transaction::TransactionStructure,
};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, BTreeSet},
    path::{Path, PathBuf},
    process::Command,
    sync::{Arc, Mutex},
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

/// Categorized catalog objects.
struct ObjectManifest<'a> {
    procedures: Vec<&'a KbObject>,
    /// Transactions are not compile units; they stay listed so their formulas are verified.
    transactions: Vec<&'a KbObject>,
    sdts: Vec<&'a KbObject>,
    skipped: Vec<SkippedObject>,
    not_generated: Vec<SkippedObject>,
    not_targeted: Vec<SkippedObject>,
    refused: Vec<SkippedObject>,
    definitions: BTreeMap<String, usize>,
    total_objects: usize,
    /// WebPanels the plain emitter planned: one class each (see `csharp::webform`).
    webpanels: Vec<WebClass>,
    /// The values class of each enumerated Domain of the model, as (file name, text) (see `csharp::domain`).
    domain_files: Vec<(String, String)>,
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

    let errors = Mutex::new(Vec::new());
    let warnings = Mutex::new(Vec::new());

    // A refused object was requested and cannot be built from this catalog: it is an error. The message names
    // the object's own kind: a refused Transaction is not a Procedure.
    for refused in &manifest.refused {
        errors.lock().unwrap().push(format!(
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
        &errors,
        &warnings,
    )?;

    if options.fail_fast && !errors.lock().unwrap().is_empty() {
        return Err(errors.lock().unwrap().join("; "));
    }

    // Phase 2: Parallel C# & Project Code Emission
    let (emitted_files, main_csproj_path, emit_ms) =
        emission_phase(&manifest, output_dir, worker_concurrency, options, &errors)?;

    // Phase 3: Parallel Compilation Driver (`dotnet build`)
    let (compile_ms, built) = if !options.skip_compile {
        compile_phase(&main_csproj_path, worker_concurrency, &errors, &warnings)
    } else {
        (0, false)
    };

    let total_ms = total_start.elapsed().as_millis() as u64;
    let final_errors = errors.into_inner().unwrap();
    let final_warnings = warnings.into_inner().unwrap();

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

/// A web UI or not-generated object, by name and kind, with the reason it is not built.
fn skipped(obj: &KbObject) -> SkippedObject {
    SkippedObject {
        name: obj.name.clone(),
        kind: format!("{:?}", obj.kind),
        reason: obj
            .kind
            .not_compile_unit_reason()
            .unwrap_or_default()
            .to_string(),
    }
}

// -----------------------------------------------------------------------------
// Phase Functions
// -----------------------------------------------------------------------------

fn classify_objects<'a>(
    catalog: &'a Catalog,
    web_environment: &WebPanelEnvironment,
    runtime_sdts: &BTreeSet<String>,
) -> (ObjectManifest<'a>, u64) {
    let read_start = Instant::now();
    let mut manifest = ObjectManifest {
        procedures: Vec::new(),
        transactions: Vec::new(),
        sdts: Vec::new(),
        skipped: Vec::new(),
        not_generated: Vec::new(),
        not_targeted: Vec::new(),
        refused: Vec::new(),
        definitions: BTreeMap::new(),
        total_objects: 0,
        webpanels: Vec::new(),
        domain_files: Vec::new(),
    };
    let targeted = crate::reachability::targeted_objects(catalog);

    for obj in catalog.objects() {
        manifest.total_objects += 1;
        // A structured data type no generated object reaches is not built, and is not refused even when
        // this engine cannot decode it (see `crate::reachability`). A library one the runtime assemblies
        // define is not built even when reached: the generated code uses the runtime class.
        if obj.kind == ObjectKind::StructuredDataType {
            let reason = if !targeted.contains(&obj.id) {
                Some(if obj.library {
                    crate::reachability::LIBRARY_NOT_TARGETED_REASON
                } else {
                    crate::reachability::UNREACHED_SDT_REASON
                })
            } else if obj.library && runtime_sdts.contains(&obj.name) {
                Some(crate::reachability::RUNTIME_PROVIDED_REASON)
            } else {
                None
            };
            if let Some(reason) = reason {
                manifest.not_targeted.push(SkippedObject {
                    name: obj.name.clone(),
                    kind: format!("{:?}", obj.kind),
                    reason: reason.into(),
                });
                continue;
            }
        }
        // A Procedure stored as not main that no generated object reaches is not generated by the SDK
        // (spc0217), so it is not built here either, nor refused.
        if obj.kind == ObjectKind::Procedure
            && obj.main == Some(false)
            && !targeted.contains(&obj.id)
        {
            manifest.not_targeted.push(SkippedObject {
                name: obj.name.clone(),
                kind: format!("{:?}", obj.kind),
                reason: crate::reachability::UNREACHABLE_REASON.into(),
            });
            continue;
        }
        // An object the reader refused is never emitted empty: it is an error, named, and counts as
        // refused. Its dependants are refused by the dependency walk below.
        if let Some(reason) = &obj.refusal {
            manifest.refused.push(SkippedObject {
                name: obj.name.clone(),
                kind: format!("{:?}", obj.kind),
                reason: format!("refused by the reader: {reason}"),
            });
            continue;
        }
        // A stored not-generated object (a Procedure the SDK does not generate) is skipped by name, and
        // is neither emitted nor an error. A caller that needs it is refused in the walk below.
        if let Some(reason) = &obj.not_generated {
            manifest.skipped.push(SkippedObject {
                name: obj.name.clone(),
                kind: format!("{:?}", obj.kind),
                reason: format!("not generated: {reason}"),
            });
            continue;
        }
        match obj.kind {
            ObjectKind::Procedure => manifest.procedures.push(obj),
            ObjectKind::StructuredDataType => manifest.sdts.push(obj),
            ObjectKind::Transaction => {
                manifest.transactions.push(obj);
                match crate::csharp::transaction_form::plan_transaction_form(
                    obj,
                    catalog,
                    web_environment,
                ) {
                    Ok(plan) => manifest
                        .webpanels
                        .push(webform::WebClass::Transaction(plan)),
                    Err(reason) => match crate::csharp::work_with::plan_work_with(
                        obj,
                        catalog,
                        web_environment,
                    ) {
                        Ok(plan) => manifest.webpanels.push(webform::WebClass::WorkWith(plan)),
                        Err(work_with) => manifest.skipped.push(SkippedObject {
                            name: obj.name.clone(),
                            kind: format!("{:?}", obj.kind),
                            reason: format!(
                                "web UI, not generated: {reason}; as a Work With Transaction: {work_with}"
                            ),
                        }),
                    },
                }
            }
            ObjectKind::WebPanel => match webform::plan_web_class(obj, catalog, web_environment) {
                Ok(plan) => manifest.webpanels.push(plan),
                Err(reason) => manifest.skipped.push(SkippedObject {
                    name: obj.name.clone(),
                    kind: format!("{:?}", obj.kind),
                    reason: format!("web UI, not generated: {reason}"),
                }),
            },
            ObjectKind::DataProvider => match ProcedureClass::from_kb_object(obj) {
                Ok(_) => manifest.procedures.push(obj),
                Err(reason) => manifest.not_generated.push(SkippedObject {
                    name: obj.name.clone(),
                    kind: "DataProvider".into(),
                    reason,
                }),
            },
            ObjectKind::DataSelector | ObjectKind::DataView => {
                manifest.not_generated.push(skipped(obj));
            }
            _ => {
                // An enumerated Domain of the model has a values class; one whose stored shape the
                // emitter does not cover is skipped by name, never written in a guessed shape.
                if obj.kind == ObjectKind::Domain {
                    match crate::csharp::domain::enum_domain_file(obj) {
                        Some(Ok(file)) => manifest.domain_files.push(file),
                        Some(Err(reason)) => manifest.skipped.push(SkippedObject {
                            name: obj.name.clone(),
                            kind: "Domain".into(),
                            reason: format!("values class not generated: {reason}"),
                        }),
                        None => {}
                    }
                }
                *manifest
                    .definitions
                    .entry(format!("{:?}", obj.kind))
                    .or_default() += 1;
            }
        }
    }

    // A Procedure that needs a web UI or not-generated object is refused, never built. It leaves
    // the buildable set; `rebuild_all` reports it as an error.
    let mut buildable = Vec::with_capacity(manifest.procedures.len());
    for procedure in std::mem::take(&mut manifest.procedures) {
        match first_unbuildable_dependency(catalog, procedure) {
            Some(dependency) => manifest.refused.push(SkippedObject {
                name: procedure.name.clone(),
                kind: "Procedure".into(),
                reason: blocker_reason(&dependency),
            }),
            None => buildable.push(procedure),
        }
    }
    manifest.procedures = buildable;

    // An SDT whose structure uses a refused or unbuildable object is refused by the same rule: its
    // class would name a type that is never generated, so it is an error, not a class that does not
    // compile. The walk is the one a Procedure uses.
    let mut emitted_sdts = Vec::with_capacity(manifest.sdts.len());
    for sdt in std::mem::take(&mut manifest.sdts) {
        match first_unbuildable_dependency(catalog, sdt) {
            Some(dependency) => manifest.refused.push(SkippedObject {
                name: sdt.name.clone(),
                kind: "StructuredDataType".into(),
                reason: blocker_reason(&dependency),
            }),
            None => emitted_sdts.push(sdt),
        }
    }
    manifest.sdts = emitted_sdts;

    let read_ms = read_start.elapsed().as_millis() as u64;
    (manifest, read_ms)
}

/// One Procedure's emission, settled before any file is written.
struct ProcedureEmission {
    name: String,
    /// The class name, upper-cased: the key a call site records for this Procedure.
    class: String,
    /// The class the main file declares, and so its file stem (`a<name>` for a split main program).
    stem: String,
    /// The plain class name in lower case (`<name>`), and the file stem of the wrapper.
    class_name: String,
    /// The wrapper file of a split main program (see `ProcedureClass::emit_wrapper`).
    wrapper: Option<String>,
    emitted: CheckedEmit,
}

/// A caller is generated only when every Procedure it calls is generated. A Procedure whose callee is
/// not generated (refused, or refused at emission) is refused by name, and so is each caller of it, until
/// nothing changes. The reason names the callee and says why it is not generated.
fn refuse_callers_of_ungenerated(emissions: &mut [ProcedureEmission]) {
    // class key -> (name as declared, reason it is not generated)
    let mut ungenerated: BTreeMap<String, (String, String)> = BTreeMap::new();
    for emission in emissions.iter() {
        if let Err(reason) = &emission.emitted.code {
            ungenerated.insert(
                emission.class.clone(),
                (emission.name.clone(), reason.clone()),
            );
        }
    }
    loop {
        let mut changed = false;
        for emission in emissions.iter_mut() {
            if emission.emitted.code.is_err() {
                continue;
            }
            let Some(callee) = emission
                .emitted
                .callees
                .iter()
                .find(|key| ungenerated.contains_key(*key))
                .cloned()
            else {
                continue;
            };
            let (callee_name, callee_reason) = ungenerated[&callee].clone();
            let reason =
                format!("needs Procedure '{callee_name}' (not generated: {callee_reason})");
            emission.emitted.code = Err(reason.clone());
            ungenerated.insert(emission.class.clone(), (emission.name.clone(), reason));
            changed = true;
        }
        if !changed {
            break;
        }
    }
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

/// Why a dependency cannot be built: the reader's refusal, or the reason its kind is not a compile unit.
fn unbuildable_reason(object: &KbObject) -> String {
    match (&object.refusal, &object.not_generated) {
        (Some(reason), _) => format!("refused by the reader: {reason}"),
        (None, Some(reason)) => format!("not generated: {reason}"),
        (None, None) => object
            .kind
            .not_compile_unit_reason()
            .unwrap_or_default()
            .to_string(),
    }
}

/// What stops a dependency from being built: a web UI or not-generated object, or a reference target
/// the catalog does not hold.
enum Blocker<'a> {
    Object(&'a KbObject),
    Missing(ObjectId),
}

/// The refusal reason of a blocked dependency, in the words the report carries.
fn blocker_reason(blocker: &Blocker<'_>) -> String {
    match blocker {
        Blocker::Object(object) => format!(
            "needs {:?} '{}' ({})",
            object.kind,
            object.name,
            unbuildable_reason(object)
        ),
        Blocker::Missing(id) => format!(
            "needs '{}', which is not in the catalog (refused: every reference target must be present)",
            id.as_str()
        ),
    }
}

/// The first web UI or not-generated object that a Procedure needs, directly or through its
/// dependency chain, or the first reference target the catalog does not hold. Such an object is
/// terminal: it is not a compile unit, so the walk does not continue past it. The root module emits
/// nothing and every object refers to it, so its absence is not a blocker.
fn first_unbuildable_dependency<'a>(
    catalog: &'a Catalog,
    procedure: &KbObject,
) -> Option<Blocker<'a>> {
    let mut seen: BTreeSet<ObjectId> = BTreeSet::new();
    let mut pending: Vec<ObjectId> = procedure
        .references
        .iter()
        .map(|reference| reference.target.clone())
        .collect();
    while let Some(id) = pending.pop() {
        if !seen.insert(id.clone()) {
            continue;
        }
        let Some(object) = catalog.get(&id) else {
            if id.as_str() == crate::udm::reader::SYSTEM_ROOT_OBJECT_ID {
                continue;
            }
            return Some(Blocker::Missing(id));
        };
        if object.refusal.is_some()
            || object.not_generated.is_some()
            || (object.kind.not_compile_unit_reason().is_some()
                && !(object.kind == ObjectKind::DataProvider
                    && ProcedureClass::from_kb_object(object).is_ok()))
        {
            return Some(Blocker::Object(object));
        }
        pending.extend(
            object
                .references
                .iter()
                .map(|reference| reference.target.clone()),
        );
    }
    None
}

fn specify_phase(
    manifest: &ObjectManifest<'_>,
    catalog: &Catalog,
    concurrency: usize,
    options: &RebuildOptions,
    errors: &Mutex<Vec<String>>,
    warnings: &Mutex<Vec<String>>,
) -> Result<(Vec<NavigationReport>, u64), String> {
    let spec_start = Instant::now();

    // Each Transaction derives or is refused on its own. A refused one is an error naming it, and the tables
    // of the others are still derived, so the Procedures that do not use it are specified.
    let (tables, physical_failures) = derive_tables_partial(catalog);
    if options.fail_fast {
        if let Some(err) = physical_failures.first() {
            return Err(format!("failed to derive physical tables: {err}"));
        }
    }
    if !physical_failures.is_empty() {
        let mut errs = errors.lock().unwrap();
        for err in &physical_failures {
            errs.push(format!("physical schema error: {err}"));
        }
    }
    let extended_tables = compute_extended_tables(&tables);
    let nav_reports: Mutex<Vec<NavigationReport>> = Mutex::new(Vec::new());

    // A Transaction whose web class is emitted checks its referenced keys there.
    let web_checked: std::collections::BTreeSet<String> = manifest
        .webpanels
        .iter()
        .filter_map(|class| match class {
            webform::WebClass::Transaction(form) => Some(form.name.clone()),
            webform::WebClass::WorkWith(class) => class.transaction_name().map(str::to_string),
            _ => None,
        })
        .collect();
    let web_checked = &web_checked;
    std::thread::scope(|s| {
        // 1. Procedures parallel specification
        if !manifest.procedures.is_empty() {
            let thread_count = concurrency.min(manifest.procedures.len()).max(1);
            let chunk_size = (manifest.procedures.len() + thread_count - 1) / thread_count;

            for chunk in manifest.procedures.chunks(chunk_size) {
                let tables_ref = &tables;
                let ext_ref = &extended_tables;
                let dialect = options.sql_dialect;
                let reports_ref = &nav_reports;
                let errors_ref = errors;
                let warnings_ref = warnings;

                s.spawn(move || {
                    for proc in chunk {
                        // Admitted DataProviders have no database navigation: their typed AST was
                        // validated during classification and uses the checked Procedure emitter.
                        if proc.kind == ObjectKind::DataProvider {
                            continue;
                        }
                        match specify_procedure_with_tables(proc, tables_ref, ext_ref, dialect) {
                            Ok(report) => {
                                if !report.warnings.is_empty() {
                                    warnings_ref.lock().unwrap().extend(report.warnings.clone());
                                }
                                reports_ref.lock().unwrap().push(report);
                            }
                            Err(err) => {
                                errors_ref.lock().unwrap().push(format!(
                                    "Procedure '{}' specification error: {err}",
                                    proc.name
                                ));
                            }
                        }
                    }
                });
            }
        }

        // 2. Transactions formula specification in parallel
        if !manifest.transactions.is_empty() {
            let thread_count = concurrency.min(manifest.transactions.len()).max(1);
            let chunk_size = (manifest.transactions.len() + thread_count - 1) / thread_count;

            for chunk in manifest.transactions.chunks(chunk_size) {
                let all_tables_ref = &tables;
                let dialect = options.sql_dialect;
                let errors_ref = errors;
                let warnings_ref = warnings;

                s.spawn(move || {
                    for txn in chunk {
                        if let Some(part) = txn.source_parts.iter().find(|p| p.name == "STRUCTURE")
                        {
                            if let Ok(structure) =
                                serde_json::from_str::<TransactionStructure>(&part.source)
                            {
                                if !structure.key_references.is_empty() {
                                    let warning = if web_checked.contains(&txn.name) {
                                        format!(
                                            "Transaction '{}': its web class checks its referenced keys, but foreign keys are metadata only, and no DDL is emitted or applied by rebuild-all",
                                            txn.name
                                        )
                                    } else {
                                        format!(
                                            "Transaction '{}': key-existence checks are not generated; foreign keys are metadata only, and no DDL is emitted or applied by rebuild-all",
                                            txn.name
                                        )
                                    };
                                    warnings_ref.lock().unwrap().push(warning);
                                }
                                let resolve_attr = |name: &str| -> Option<crate::model::DataType> {
                                    let mut visited = std::collections::BTreeSet::new();
                                    crate::kb::resolve_domain_or_attribute_data_type(
                                        catalog,
                                        name,
                                        &mut visited,
                                        0,
                                    )
                                    .ok()
                                };
                                let txn_tables =
                                    derive_tables_from_transaction(&structure, resolve_attr)
                                        .unwrap_or_default();

                                verify_level_formulas(
                                    &structure.root,
                                    &txn_tables,
                                    all_tables_ref,
                                    dialect,
                                    errors_ref,
                                );
                            } else {
                                // Not a silent skip: the STRUCTURE part is not the JSON form
                                // this reader parses, so formula verification did not run.
                                warnings_ref.lock().unwrap().push(format!(
                                    "Transaction '{}': STRUCTURE part is not the JSON form this build reads; its formula verification was skipped",
                                    txn.name
                                ));
                            }
                        }
                    }
                });
            }
        }
    });

    let spec_ms = spec_start.elapsed().as_millis() as u64;
    let final_reports = nav_reports.into_inner().unwrap();
    Ok((final_reports, spec_ms))
}

fn verify_level_formulas(
    level: &crate::transaction::TransactionLevel,
    txn_tables: &[Table],
    all_tables: &[Table],
    dialect: SqlDialect,
    errors: &Mutex<Vec<String>>,
) {
    for attr in &level.attributes {
        if let Some(formula_src) = &attr.formula {
            if let Err(e) = specify_transaction_attribute_formula(
                &attr.name,
                formula_src,
                txn_tables,
                all_tables,
                dialect,
            ) {
                errors
                    .lock()
                    .unwrap()
                    .push(format!("Transaction formula error on '{}': {e}", attr.name));
            }
        }
    }
    for sub in &level.levels {
        verify_level_formulas(sub, txn_tables, all_tables, dialect, errors);
    }
}

/// The support classes of the model, as the SDK writes them (see the support step of the emitter).
const SUPPORT_FILES: [(&str, &str); 5] = [
    (
        "GxFullTextSearchReindexer.cs",
        include_str!("csharp/support/GxFullTextSearchReindexer.cs.tmpl"),
    ),
    (
        "GxModelInfoProvider.cs",
        include_str!("csharp/support/GxModelInfoProvider.cs.tmpl"),
    ),
    (
        "GxObjectCollection.cs",
        include_str!("csharp/support/GxObjectCollection.cs.tmpl"),
    ),
    (
        "SoapParm.cs",
        include_str!("csharp/support/SoapParm.cs.tmpl"),
    ),
    ("GxWebServicesConfig.cs", ""),
];

/// The runtime configuration the SDK writes beside each class of a main program (`<class>.exe.config`).
const EXE_CONFIG: &str = include_str!("csharp/exe_config.tmpl");

/// Writes one model file as the given bytes (no newline or encoding rewriting), creating its folder.
fn emit_model_file(
    out_dir: &Path,
    relative: &str,
    bytes: &[u8],
    files_ref: &Mutex<Vec<PathBuf>>,
    errors_ref: &Mutex<Vec<String>>,
) {
    let file_path = out_dir.join(relative);
    let written = file_path
        .parent()
        .map_or(Ok(()), std::fs::create_dir_all)
        .and_then(|()| std::fs::write(&file_path, bytes));
    match written {
        Ok(()) => files_ref.lock().unwrap().push(file_path),
        Err(e) => errors_ref
            .lock()
            .unwrap()
            .push(format!("{relative}: not written: {e}")),
    }
}

fn emit_generated_file(
    out_dir: &Path,
    file_name: &str,
    content: &str,
    files_ref: &Mutex<Vec<PathBuf>>,
    errors_ref: &Mutex<Vec<String>>,
) {
    let file_path = out_dir.join(file_name);
    // A C# file's literal values depend on the whole file's bytes (see `csharp::literals`), so its text
    // is resolved here, once, before it is written.
    // It is written in the SDK's bytes: CRLF and cp1252 (`csharp::literals::sdk_file_bytes`).
    // A file in the SDK's form already holds each stored character raw (`Expr::Text` in the emitter), so
    // its bytes are the SDK's bytes and the compiler reads them as the SDK's file; only an ASCII file
    // with escapes is resolved (artm41852400.cs holds the stored C2 A0 as two bytes).
    let bytes = if file_name.ends_with(".cs") {
        if content.is_ascii() {
            crate::csharp::literals::sdk_file_bytes(&crate::csharp::literals::resolve_sdk_literals(
                content,
            ))
        } else {
            crate::csharp::literals::sdk_file_bytes(content)
        }
    } else {
        content.as_bytes().to_vec()
    };
    if let Err(e) = std::fs::write(&file_path, bytes) {
        errors_ref
            .lock()
            .unwrap()
            .push(format!("failed to write '{}': {e}", file_path.display()));
    } else {
        files_ref.lock().unwrap().push(file_path);
    }
}

fn spawn_parallel_chunks<'scope, 'env: 'scope, T: Sync>(
    items: &'env [T],
    concurrency: usize,
    scope: &'scope std::thread::Scope<'scope, 'env>,
    worker: impl Fn(&'env [T]) + Send + Sync + Copy + 'scope,
) {
    if items.is_empty() {
        return;
    }
    let thread_count = concurrency.min(items.len()).max(1);
    let chunk_size = (items.len() + thread_count - 1) / thread_count;
    for chunk in items.chunks(chunk_size) {
        scope.spawn(move || worker(chunk));
    }
}

fn emission_phase(
    manifest: &ObjectManifest<'_>,
    output_dir: &Path,
    concurrency: usize,
    options: &RebuildOptions,
    errors: &Mutex<Vec<String>>,
) -> Result<(Vec<PathBuf>, PathBuf, u64), String> {
    let emit_start = Instant::now();

    if options.clean && output_dir.exists() {
        let _ = std::fs::remove_dir_all(output_dir);
    }
    std::fs::create_dir_all(output_dir).map_err(|e| {
        format!(
            "cannot create output directory '{}': {e}",
            output_dir.display()
        )
    })?;

    let emitted_files: Mutex<Vec<PathBuf>> = Mutex::new(Vec::new());

    // The SDT classes are built before any Procedure: a Procedure resolves its member accesses against
    // exactly the SDTs emitted here. An SDT that does not build is absent from the index, so a member of
    // it fails closed in every Procedure that uses it.
    let mut sdt_classes: Vec<SdtClass> = Vec::with_capacity(manifest.sdts.len());
    for sdt in &manifest.sdts {
        match SdtClass::from_kb_object(sdt) {
            Ok(class) => sdt_classes.push(class),
            Err(e) => errors
                .lock()
                .unwrap()
                .push(format!("failed to generate C# for SDT '{}': {e}", sdt.name)),
        }
    }
    // An SDT held by a module is declared in that module's namespace; every emitted class imports
    // those namespaces, so a type of an SDT another module holds resolves by its class name. Root-only
    // builds import nothing extra.
    let sdt_usings: Vec<String> = sdt_classes
        .iter()
        .map(|class| class.namespace.clone())
        .filter(|namespace| namespace != crate::csharp::types::ROOT_NAMESPACE)
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect();
    let sdt_classes: Vec<SdtClass> = sdt_classes
        .into_iter()
        .map(|class| class.with_usings(sdt_usings.clone()))
        .collect();
    // A Procedure variable typed by a nested SDT level resolves its members through that level's
    // class, so each level class an SDT emits is indexed beside the SDT. A clash fails the index.
    let indexed: Vec<SdtClass> = sdt_classes
        .iter()
        .flat_map(|class| std::iter::once(class.clone()).chain(class.level_classes()))
        .collect();
    let sdt_index = Arc::new(match SdtIndex::from_classes(indexed) {
        Ok(index) => index,
        Err(e) => {
            errors.lock().unwrap().push(format!(
                "the SDT index is not built, so no Procedure member resolves: {e}"
            ));
            SdtIndex::default()
        }
    });

    // The parameters of every Procedure this build emits. A call site is written from its callee's
    // parameters (`ref`/`out` keywords and temporaries), so the table is complete before any emission.
    let mut call_table = CallSignatures::new();
    for proc in &manifest.procedures {
        if let Ok(class) = ProcedureClass::from_kb_object(proc) {
            if let Err(e) = call_table.insert(class.call_target()) {
                errors
                    .lock()
                    .unwrap()
                    .push(format!("Procedure '{}': {e}", proc.name));
            }
        }
    }
    let call_table = Arc::new(call_table);

    // 1. Emit every Procedure. Nothing is written yet: a caller is generated only if every Procedure it
    // calls is generated (csc would fail on a call to a class that is not written), so the outcomes are
    // settled first.
    let emissions: Mutex<Vec<ProcedureEmission>> = Mutex::new(Vec::new());
    std::thread::scope(|s| {
        spawn_parallel_chunks(&manifest.procedures, concurrency, s, |chunk| {
            for proc in chunk {
                let (stem, class_name, wrapper, emitted) =
                    match ProcedureClass::from_kb_object(proc).and_then(|class| {
                        if proc.kind == ObjectKind::DataProvider {
                            crate::csharp::data_provider::resolve_collection_calls(
                                class,
                                &call_table,
                            )
                        } else {
                            Ok(class)
                        }
                    }) {
                        Ok(class) => {
                            let stem = class.emitted_class_name();
                            let class_name = class.class_name();
                            let wrapper = class.emit_wrapper();
                            let emitted = class
                                .with_usings(sdt_usings.clone())
                                .with_sdts(sdt_index.clone())
                                .with_calls(call_table.clone())
                                .emit_detailed();
                            (stem, class_name, wrapper, emitted)
                        }
                        Err(e) => (
                            String::new(),
                            String::new(),
                            None,
                            CheckedEmit {
                                code: Err(e),
                                callees: BTreeSet::new(),
                            },
                        ),
                    };
                emissions.lock().unwrap().push(ProcedureEmission {
                    name: proc.name.clone(),
                    class: proc.name.to_ascii_uppercase(),
                    stem,
                    class_name,
                    wrapper,
                    emitted,
                });
            }
        });
    });
    let mut emissions = emissions.into_inner().unwrap();
    emissions.sort_by(|a, b| a.name.cmp(&b.name));
    refuse_callers_of_ungenerated(&mut emissions);

    // 2. Write the Procedures that are generated; each refusal is reported by name.
    for emission in &emissions {
        match &emission.emitted.code {
            Ok(code) => {
                emit_generated_file(
                    output_dir,
                    &format!("{}.cs", emission.stem),
                    code,
                    &emitted_files,
                    errors,
                );
                let mut includes = vec![format!("{}.cs", emission.stem)];
                if let Some(wrapper) = &emission.wrapper {
                    emit_generated_file(
                        output_dir,
                        &format!("{}.cs", emission.class_name),
                        wrapper,
                        &emitted_files,
                        errors,
                    );
                    includes.push(format!("{}.cs", emission.class_name));
                    // The SDK writes one runtime configuration per class of a main program, the same
                    // text for every one (all 262 `*.exe.config` of the KBTeste NetModel are equal).
                    for stem in [&emission.stem, &emission.class_name] {
                        emit_generated_file(
                            output_dir,
                            &format!("{stem}.exe.config"),
                            &EXE_CONFIG.replace('\n', "\r\n"),
                            &emitted_files,
                            errors,
                        );
                    }
                }

                let mut cfg = if options.target_framework.starts_with("net9") {
                    CsprojConfig::net9(&emission.class_name, false)
                } else {
                    CsprojConfig::net8(&emission.class_name, false)
                };
                cfg.compile_includes = includes;
                let proj_xml = CsprojEmitter::emit(&cfg);
                emit_generated_file(
                    output_dir,
                    &format!("{}.csproj", emission.class_name),
                    &proj_xml,
                    &emitted_files,
                    errors,
                );
            }
            Err(e) => {
                errors.lock().unwrap().push(format!(
                    "failed to generate C# for Procedure '{}': {e}",
                    emission.name
                ));
            }
        }
    }

    // Plain WebPanels: one class each, in the SDK shape (see csharp::webform). The stamp is the
    // generation time, the token the SDK writes into a panel's script URLs.
    let stamp = webform::current_stamp();
    for panel in &manifest.webpanels {
        emit_generated_file(
            output_dir,
            &panel.file_name(),
            &panel.emit(&stamp),
            &emitted_files,
            errors,
        );
        match panel.script() {
            Ok((file_name, text)) => {
                emit_generated_file(output_dir, &file_name, &text, &emitted_files, errors)
            }
            Err(error) => errors
                .lock()
                .unwrap()
                .push(format!("client script of '{}': {error}", panel.file_name())),
        }
    }
    // The web model's client configuration, which every emitted class's script loads.
    if !manifest.webpanels.is_empty() {
        match &options.client_config {
            Some(Ok(text)) => {
                emit_generated_file(output_dir, "gxcfg.js", text, &emitted_files, errors)
            }
            Some(Err(error)) => errors.lock().unwrap().push(format!("gxcfg.js: {error}")),
            None => {}
        }
    }
    // The KB's own GxWebStd helper, which every emitted panel calls; written only with a panel.
    if let (false, Some(support)) = (
        manifest.webpanels.is_empty(),
        options.web_environment.support_class.as_deref(),
    ) {
        emit_generated_file(output_dir, "GxWebStd.cs", support, &emitted_files, errors);
    }

    // The model's support classes, which the SDK writes on every generation whatever the KB holds
    // (KBTeste NetModel/Web after SpecifyAllOnly + GenerateOnly). Their text is fixed for the
    // `GeneXus.Programs` namespace; GxWebServicesConfig.cs is written empty, as the SDK writes it.
    // They belong to a KB's model, so they are written for a run read from a KB (its GxWebStd is
    // configured) that generates anything at all.
    if options.web_environment.support_class.is_some() && !emitted_files.lock().unwrap().is_empty()
    {
        for (file_name, text) in SUPPORT_FILES {
            emit_generated_file(output_dir, file_name, text, &emitted_files, errors);
        }
        for (label, group) in &options.model_files {
            match group {
                Ok(files) => {
                    for (path, bytes) in files {
                        emit_model_file(output_dir, path, bytes, &emitted_files, errors);
                    }
                }
                Err(error) => errors.lock().unwrap().push(format!("{label}: {error}")),
            }
        }
    }

    // The values class of each enumerated Domain (`gxdomain<name>.cs`), written with the model's classes.
    for (file_name, text) in &manifest.domain_files {
        emit_generated_file(output_dir, file_name, text, &emitted_files, errors);
    }

    // A Transaction is emitted with the web classes when its form is a matched shape
    // (`transaction_form`); any other is skipped by name (see SkippedObject).

    // 3. Emit SDTs (the classes built above, the same ones the Procedures resolved against)
    std::thread::scope(|s| {
        spawn_parallel_chunks(&sdt_classes, concurrency, s, |chunk| {
            for sdt_class in chunk {
                // The SDK's form (`type_Sdt<class>.cs`, one file per class) when the SDT is a shape the
                // SDK evidence covers; otherwise the portable class, one file for the SDT and its levels.
                match crate::csharp::sdt_sdk::sdk_files(sdt_class) {
                    Ok(files) => {
                        for (file_name, text) in files {
                            emit_generated_file(
                                output_dir,
                                &file_name,
                                &text,
                                &emitted_files,
                                errors,
                            );
                        }
                    }
                    Err(_) => emit_generated_file(
                        output_dir,
                        &format!("{}.cs", sdt_class.class_name()),
                        &sdt_class.emit(),
                        &emitted_files,
                        errors,
                    ),
                }
            }
        });
    });

    // 5. Emit Main Unified Project File (.csproj)
    let main_asm_name = options
        .assembly_name
        .clone()
        .unwrap_or_else(|| "RebuildApp".to_string());

    let mut main_cfg = if options.target_framework.starts_with("net9") {
        CsprojConfig::net9(&main_asm_name, false)
    } else {
        CsprojConfig::net8(&main_asm_name, false)
    };

    let mut cs_includes = Vec::new();
    for f in emitted_files.lock().unwrap().iter() {
        if f.extension().and_then(|ext| ext.to_str()) == Some("cs") {
            if let Some(file_name) = f.file_name().and_then(|n| n.to_str()) {
                // The SDK writes GxObjectCollection.cs but compiles it into no project: its
                // genexus.programs.common.bld lists the other support files only.
                if file_name != "GxObjectCollection.cs" {
                    cs_includes.push(file_name.to_string());
                }
            }
        }
    }
    cs_includes.sort();
    main_cfg.compile_includes = cs_includes;
    // The generated classes derive from the GeneXus runtime. GeneXus adds this reference
    // to every object project through DotNetCoreBaseProject.targets; without it the
    // emitted transactions and procedures cannot resolve their base types.
    main_cfg = main_cfg.add_package_reference(
        "GeneXus.Classes.Web.Core",
        options.genexus_runtime_version.clone(),
    );

    // A closure build is an executable that starts at its entry's core class, as the SDK's own build of
    // that object does. The full rebuild keeps the library contract.
    if let Some(startup) = &options.startup_object {
        main_cfg = main_cfg.as_executable(startup.clone());
    }
    let main_csproj_xml = CsprojEmitter::emit(&main_cfg);
    let main_csproj_path = output_dir.join(format!("{main_asm_name}.csproj"));
    std::fs::write(&main_csproj_path, main_csproj_xml).map_err(|e| {
        format!(
            "failed to write main project file '{}': {e}",
            main_csproj_path.display()
        )
    })?;
    emitted_files.lock().unwrap().push(main_csproj_path.clone());

    let emit_ms = emit_start.elapsed().as_millis() as u64;
    let mut final_emitted = emitted_files.into_inner().unwrap();
    final_emitted.sort();

    Ok((final_emitted, main_csproj_path, emit_ms))
}

/// Runs `dotnet build` on the main project. Returns the elapsed milliseconds and whether the
/// project built.
fn compile_phase(
    main_csproj_path: &Path,
    concurrency: usize,
    errors: &Mutex<Vec<String>>,
    warnings: &Mutex<Vec<String>>,
) -> (u64, bool) {
    let compile_start = Instant::now();
    let mut built = false;
    // A kept compiler command runs first. It is used only while the project's non-source content matches
    // the content the command was captured from; otherwise `dotnet build` runs and captures a new one.
    // Executables reuse only unchanged SDK-produced runtime files; a missing or changed
    // bootstrap makes the guarded direct path fall back to MSBuild.
    match crate::csc_direct::compile(main_csproj_path) {
        crate::csc_direct::Verdict::Built { warnings: found } => {
            warnings.lock().unwrap().extend(found);
            eprintln!("compile path: csc-direct");
            return (compile_start.elapsed().as_millis() as u64, true);
        }
        crate::csc_direct::Verdict::Diagnostics {
            errors: found,
            warnings: found_warnings,
        } => {
            errors.lock().unwrap().extend(found);
            warnings.lock().unwrap().extend(found_warnings);
            eprintln!("compile path: csc-direct (diagnostics)");
            return (compile_start.elapsed().as_millis() as u64, false);
        }
        crate::csc_direct::Verdict::Unavailable(reason) => {
            eprintln!("compile path: dotnet build ({reason})");
        }
    }
    let dotnet_available = Command::new("dotnet").arg("--version").output().is_ok();

    if dotnet_available {
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

        match cmd.output() {
            Ok(output) => {
                let stdout = String::from_utf8_lossy(&output.stdout);
                let stderr = String::from_utf8_lossy(&output.stderr);

                // MSBuild reports project and restore failures as `: error NETSDK...` or
                // `: error NU...`, not as `error CS`; all of them are build errors. Repeats and
                // node prefixes are removed by `compiler_diagnostics`.
                let (found_errors, found_warnings) =
                    crate::csc_direct::compiler_diagnostics(&format!("{stdout}\n{stderr}"));
                errors.lock().unwrap().extend(found_errors);
                warnings.lock().unwrap().extend(found_warnings);

                built = output.status.success();
                if built {
                    let _ = crate::csc_direct::capture(main_csproj_path, &stdout);
                }
                // A failed build is an error whatever its diagnostics. It used to become a
                // warning when no `error CS` line was printed, and the rebuild exited 0.
                if !built {
                    errors.lock().unwrap().push(format!(
                        "dotnet build failed with exit code {} (check package restore and runtime availability)",
                        output.status
                    ));
                }
            }
            Err(err) => {
                errors
                    .lock()
                    .unwrap()
                    .push(format!("cannot invoke dotnet build: {err}"));
            }
        }
    } else {
        warnings
            .lock()
            .unwrap()
            .push("dotnet CLI is not installed; skipped compilation driver".to_string());
    }

    (compile_start.elapsed().as_millis() as u64, built)
}
