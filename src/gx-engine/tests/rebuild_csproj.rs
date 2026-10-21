//! The emitted `RebuildApp.csproj` lists its `Compile` items explicitly. The .NET SDK
//! also includes every `.cs` in the project directory by default, so an explicit item
//! on a file the glob already matches is a duplicate (NETSDK1022). The project must
//! either turn the default off, as GeneXus does, or not list overlapping items.
//!
//! The project also references the GeneXus runtime at the version the KB declares, and a
//! failed `dotnet build` is a rebuild error whatever its diagnostics.

#![forbid(unsafe_code)]

use gx_engine::catalog::{Catalog, KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::csharp::{CsprojConfig, CsprojEmitter};
use gx_engine::rebuild::{
    DEFAULT_GENEXUS_RUNTIME_VERSION, RebuildOptions, genexus_runtime_version_from_kb, rebuild_all,
};
use std::collections::BTreeMap;
use std::path::PathBuf;

/// The `Include` value of every `<Compile>` item, in document order.
fn compile_items(csproj: &str) -> Vec<String> {
    csproj
        .split("<Compile Include=\"")
        .skip(1)
        .map(|rest| rest.split('"').next().unwrap_or_default().to_string())
        .collect()
}

fn default_compile_items_disabled(csproj: &str) -> bool {
    csproj.contains("<EnableDefaultCompileItems>false</EnableDefaultCompileItems>")
}

fn procedure(id: &str, name: &str, source: &str) -> KbObject {
    let mut object = KbObject::new(ObjectId::new(id).unwrap(), name, ObjectKind::Procedure);
    object.source_parts.push(SourcePart {
        name: "Source".to_string(),
        source: source.to_string(),
    });
    object
}

fn two_procedures() -> Catalog {
    let mut objects = BTreeMap::new();
    for object in [
        procedure("palpha", "PAlpha", "msg('alpha', status)"),
        procedure("pbeta", "PBeta", "msg('beta', status)"),
    ] {
        objects.insert(object.id.clone(), object);
    }
    Catalog::from_objects_map(objects)
}

fn scratch_dir(tag: &str) -> PathBuf {
    let nonce = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or_default();
    std::env::temp_dir().join(format!(
        "gx_rebuild_csproj_{tag}_{}_{nonce}",
        std::process::id()
    ))
}

fn dotnet_available() -> bool {
    std::process::Command::new("dotnet")
        .arg("--version")
        .output()
        .is_ok()
}

#[test]
fn rebuild_app_csproj_has_no_duplicate_or_missing_compile_items() {
    let catalog = two_procedures();

    let out = scratch_dir("rebuild");
    let options = RebuildOptions::new("net8.0").with_skip_compile(true);
    let report = rebuild_all(&catalog, &out, &options).expect("rebuild");
    assert!(
        report.errors.is_empty(),
        "rebuild errors: {:?}",
        report.errors
    );

    let csproj = std::fs::read_to_string(out.join("RebuildApp.csproj")).expect("RebuildApp.csproj");
    let items = compile_items(&csproj);

    let mut emitted: Vec<String> = std::fs::read_dir(&out)
        .expect("read output dir")
        .flatten()
        .map(|entry| entry.file_name().to_string_lossy().to_string())
        .filter(|name| name.ends_with(".cs"))
        .collect();
    emitted.sort();
    assert!(!emitted.is_empty(), "the procedures must be emitted");

    let mut sorted_items = items.clone();
    sorted_items.sort();
    let mut deduplicated = sorted_items.clone();
    deduplicated.dedup();
    assert_eq!(
        sorted_items, deduplicated,
        "a Compile item is listed twice: {items:?}"
    );
    assert_eq!(
        sorted_items, emitted,
        "every emitted .cs must be listed exactly once"
    );

    // The SDK's default glob matches each emitted .cs. Wherever the explicit list
    // overlaps that glob, the default must be off.
    let overlapping: Vec<&String> = items.iter().filter(|item| emitted.contains(item)).collect();
    assert!(
        overlapping.is_empty() || default_compile_items_disabled(&csproj),
        "explicit Compile items overlap the SDK default glob without EnableDefaultCompileItems=false"
    );

    let _ = std::fs::remove_dir_all(&out);
}

/// A project with no explicit items keeps the SDK default: the property is only written
/// when the emitter lists items itself.
#[test]
fn csproj_without_explicit_compile_items_keeps_the_sdk_default() {
    let mut config = CsprojConfig::net8("NoItems", false);
    config.compile_includes.clear();
    let xml = CsprojEmitter::emit(&config);
    assert!(compile_items(&xml).is_empty());
    assert!(!default_compile_items_disabled(&xml), "{xml}");
}

#[test]
fn csproj_with_explicit_compile_items_turns_the_default_off() {
    let xml = CsprojEmitter::emit(&CsprojConfig::net8("WithItems", false));
    assert_eq!(compile_items(&xml), vec!["WithItems.cs".to_string()]);
    assert!(default_compile_items_disabled(&xml), "{xml}");
}

/// The generated classes derive from the GeneXus runtime, so the project references it at
/// the declared version, as GeneXus's own object projects do.
#[test]
fn rebuild_app_references_the_genexus_runtime_at_the_declared_version() {
    let out = scratch_dir("runtime");
    let options = RebuildOptions::new("net8.0")
        .with_skip_compile(true)
        .with_genexus_runtime_version("[1.30.0]");
    rebuild_all(&two_procedures(), &out, &options).expect("rebuild");
    let csproj = std::fs::read_to_string(out.join("RebuildApp.csproj")).expect("RebuildApp.csproj");
    assert!(
        csproj.contains(
            r#"<PackageReference Include="GeneXus.Classes.Web.Core" Version="[1.30.0]" />"#
        ),
        "{csproj}"
    );
    let _ = std::fs::remove_dir_all(&out);
}

#[test]
fn default_runtime_version_is_the_one_the_cache_provides() {
    assert_eq!(DEFAULT_GENEXUS_RUNTIME_VERSION, "[1.30.0]");
}

fn kb_with_props(tag: &str, props: Option<&str>) -> PathBuf {
    let kb = scratch_dir(tag);
    let netmodel = kb.join("NetModel");
    std::fs::create_dir_all(&netmodel).expect("create NetModel");
    if let Some(props) = props {
        std::fs::write(netmodel.join("Directory.build.props"), props).expect("write props");
    }
    kb
}

#[test]
fn kb_declared_runtime_version_is_read_from_directory_build_props() {
    let kb = kb_with_props(
        "declared",
        Some(
            "<Project>\n  <PropertyGroup>\n    <GenStdVersion>[1.30.0]</GenStdVersion>\n  </PropertyGroup>\n</Project>\n",
        ),
    );
    assert_eq!(
        genexus_runtime_version_from_kb(&kb).as_deref(),
        Some("[1.30.0]")
    );
    let _ = std::fs::remove_dir_all(&kb);
}

#[test]
fn kb_without_a_declaration_or_with_a_malformed_one_has_no_version() {
    let absent = kb_with_props("absent", None);
    assert_eq!(genexus_runtime_version_from_kb(&absent), None);
    let _ = std::fs::remove_dir_all(&absent);

    let malformed = kb_with_props(
        "malformed",
        Some(
            "<Project><PropertyGroup><GenStdVersion>$(Evil)</GenStdVersion></PropertyGroup></Project>",
        ),
    );
    assert_eq!(genexus_runtime_version_from_kb(&malformed), None);
    let _ = std::fs::remove_dir_all(&malformed);
}

/// A project MSBuild cannot load fails without any `error CS` line. That failure used to
/// become a warning and the rebuild exited 0; it is now a build error.
#[test]
fn failed_dotnet_build_is_a_rebuild_error_even_without_a_csharp_diagnostic() {
    if !dotnet_available() {
        return;
    }
    // An unescaped `&` makes the project XML invalid; the file name stays valid on Windows.
    let options = RebuildOptions::new("net8.0")
        .with_assembly_name("Bad&Name")
        .with_skip_compile(false);
    let out = scratch_dir("failed_build");
    let report = rebuild_all(&two_procedures(), &out, &options).expect("rebuild");
    assert!(
        report
            .errors
            .iter()
            .any(|e| e.contains("dotnet build failed with exit code")),
        "a failed build must be an error: {:?}",
        report.errors
    );
    assert!(
        report.errors.iter().any(|e| e.contains(": error ")),
        "the MSBuild diagnostic must be kept: {:?}",
        report.errors
    );
    let _ = std::fs::remove_dir_all(&out);
}
