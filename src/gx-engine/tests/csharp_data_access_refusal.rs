//! A Procedure that holds a database statement (`For Each`, `New`, `Delete`, or an attribute assignment
//! inside them) has no SDK form in this build: the SDK writes a cursor class and `GxCommand`s for it.
//! Before this guard the emitter wrote a comment and the loop body, so the rebuild reported a generated
//! Procedure that read no row and wrote no row. These tests parse real Procedure sources and run the real
//! rebuild: the Procedure must be refused by name, never written, never counted as generated.

use gx_engine::{
    catalog::{Catalog, Edit, KbObject, ObjectId, ObjectKind, SourcePart},
    csharp::ProcedureClass,
    rebuild::{RebuildOptions, RebuildStatus, rebuild_all},
};
use std::fs;

const VARIABLES: &str = r#"{
    "Total": {"kind": "numeric", "precision": 5, "scale": 0}
}"#;

fn procedure(name: &str, source: &str) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new(name.to_ascii_lowercase()).unwrap(),
        name,
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: source.into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: VARIABLES.into(),
    });
    object
}

fn emit_error(source: &str) -> Result<String, String> {
    ProcedureClass::from_kb_object(&procedure("Probe", source))
        .expect("the Procedure is read")
        .emit_checked()
}

struct Rebuilt {
    status: RebuildStatus,
    generated_count: usize,
    errors: Vec<String>,
    /// Every `.cs` file left in the output directory.
    cs_files: Vec<String>,
    /// The `Compile Include` entries of the main project.
    project_includes: String,
}

fn rebuild_objects(label: &str, objects: &[KbObject]) -> Rebuilt {
    let mut catalog = Catalog::empty();
    let edits: Vec<Edit> = objects.iter().cloned().map(Edit::Upsert).collect();
    catalog.apply(0, &edits).expect("catalog setup");
    let dir = std::env::temp_dir().join(format!(
        "gx_data_access_{}_{}",
        label.to_ascii_lowercase(),
        std::process::id()
    ));
    let _ = fs::remove_dir_all(&dir);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_concurrency(1)
        .with_skip_compile(true)
        .with_assembly_name("DataAccessApp");
    let report = rebuild_all(&catalog, &dir, &options).expect("the rebuild runs");
    let mut cs_files: Vec<String> = fs::read_dir(&dir)
        .expect("generated directory is readable")
        .map(|entry| {
            entry
                .expect("generated entry is readable")
                .file_name()
                .to_string_lossy()
                .into_owned()
        })
        .filter(|name| name.ends_with(".cs"))
        .collect();
    cs_files.sort();
    let project_includes = fs::read_to_string(dir.join("DataAccessApp.csproj"))
        .expect("the generated project is readable")
        .lines()
        .filter(|line| line.contains("Compile"))
        .collect::<Vec<_>>()
        .join("\n");
    let _ = fs::remove_dir_all(&dir);
    Rebuilt {
        status: report.status,
        generated_count: report.generated_count,
        errors: report.errors,
        cs_files,
        project_includes,
    }
}

fn rebuild(name: &str, source: &str) -> Rebuilt {
    rebuild_objects(name, &[procedure(name, source)])
}

/// What a refusal looks like in a rebuild that does not compile (`skip_compile`). `compiled_count` is 0
/// in such a run whatever was emitted, so it proves nothing here; the evidence that a refused Procedure
/// never reaches the compiler is that no `.cs` file exists for it, that the main project lists no compile
/// item for it, that it is not counted as generated, and that the run is `Failed` with the Procedure
/// named (a `Failed` run is never reported as built).
fn assert_not_emitted(rebuilt: &Rebuilt, name: &str) {
    let stem = name.to_ascii_lowercase();
    assert!(
        !rebuilt
            .cs_files
            .iter()
            .any(|file| file.to_ascii_lowercase().contains(&stem)),
        "{name}: a refused Procedure must not be written: {:?}",
        rebuilt.cs_files
    );
    assert!(
        !rebuilt
            .project_includes
            .to_ascii_lowercase()
            .contains(&stem),
        "{name}: the project must not compile a refused Procedure: {}",
        rebuilt.project_includes
    );
}

fn assert_refused_by_name(name: &str, source: &str, construct: &str) {
    let error = emit_error(source).expect_err("a database statement has no SDK form here");
    assert!(error.contains(construct), "{construct}: {error}");

    let rebuilt = rebuild(name, source);
    assert_not_emitted(&rebuilt, name);
    assert!(
        rebuilt.cs_files.is_empty(),
        "{name}: no .cs file at all: {:?}",
        rebuilt.cs_files
    );
    assert_eq!(rebuilt.generated_count, 0, "{name}: not generated");
    assert_eq!(
        rebuilt.status,
        RebuildStatus::Failed,
        "{name}: the run fails"
    );
    assert!(
        rebuilt
            .errors
            .iter()
            .any(|e| e.contains(&format!("Procedure '{name}'")) && e.contains(construct)),
        "{name}: refused by name with the construct: {:?}",
        rebuilt.errors
    );
}

#[test]
fn a_for_each_is_refused_by_name() {
    assert_refused_by_name(
        "ListAll",
        "For Each\n    &Total = &Total + 1\nEndFor\n",
        "For Each",
    );
}

#[test]
fn a_new_block_is_refused_by_name() {
    assert_refused_by_name("AddRow", "New\n    &Total = 1\nEndNew\n", "New");
}

#[test]
fn a_delete_is_refused_by_name() {
    assert_refused_by_name("DropRow", "Delete\n", "Delete");
}

#[test]
fn a_database_statement_nested_in_control_flow_is_refused_by_name() {
    assert_refused_by_name(
        "Nested",
        "If &Total = 0\n    Do While &Total < 3\n        New\n            &Total = 1\n        EndNew\n    EndDo\nEndIf\n",
        "New",
    );
    assert_refused_by_name(
        "NestedDelete",
        "For &Total = 1 to 3\n    If &Total = 2\n        Delete\n    EndIf\nEndFor\n",
        "Delete",
    );
}

#[test]
fn every_database_statement_nested_in_a_for_each_is_named() {
    let error =
        emit_error("For Each\n    New\n        Total = 1\n    EndNew\n    Delete\nEndFor\n")
            .expect_err("refused");
    for construct in ["For Each", "New", "Delete", "attribute assignment 'TOTAL'"] {
        assert!(error.contains(construct), "{construct} is named: {error}");
    }
}

#[test]
fn an_attribute_assignment_is_refused_by_name() {
    let error = emit_error("For Each\n    CustomerName = 'x'\nEndFor\n").expect_err("refused");
    assert!(
        error.contains("attribute assignment 'CUSTOMERNAME'"),
        "{error}"
    );
}

#[test]
fn a_procedure_without_database_statements_is_still_generated() {
    let code = emit_error("&Total = 1\nIf &Total = 1\n    &Total = 2\nEndIf\n")
        .expect("a Procedure with no database statement is written");
    assert!(!code.contains("GeneXus For Each block"), "{code}");

    let rebuilt = rebuild("Plain", "&Total = 1\n");
    assert!(!rebuilt.cs_files.is_empty(), "the Procedure is written");
    assert!(
        rebuilt.generated_count > 0,
        "the Procedure is counted as generated"
    );
    assert!(rebuilt.errors.is_empty(), "{:?}", rebuilt.errors);
}

#[test]
fn malformed_database_source_is_an_error_not_a_generated_procedure() {
    // An unterminated For Each never parses; the Procedure must not be written as if it were empty.
    let object = procedure("Broken", "For Each\n    &Total = 1\n");
    let result = ProcedureClass::from_kb_object(&object).and_then(|class| class.emit_checked());
    assert!(result.is_err(), "malformed source is refused: {result:?}");
}

#[test]
fn a_database_statement_inside_a_subroutine_is_refused_by_name() {
    assert_refused_by_name(
        "InSub",
        "&Total = 1
Do 'Load'
Sub 'Load'
    For Each
        &Total = &Total + 1
    EndFor
EndSub
",
        "For Each",
    );
}

#[test]
fn a_database_statement_inside_a_do_case_is_refused_by_name() {
    assert_refused_by_name(
        "InCase",
        "Do Case
    Case &Total = 1
        &Total = 2
    Case &Total = 2
        Delete
    Otherwise
        &Total = 3
EndCase
",
        "Delete",
    );
    assert_refused_by_name(
        "InOtherwise",
        "Do Case
    Case &Total = 1
        &Total = 2
    Otherwise
        New
            &Total = 1
        EndNew
EndCase
",
        "New",
    );
}

/// `Top` calls `Middle`, `Middle` calls `Leaf`, and only `Leaf` holds a database statement. Each caller
/// is refused by the real rebuild with the callee and why it is not generated, so no class that calls a
/// missing one is written; the unrelated Procedure is still generated.
#[test]
fn every_transitive_caller_of_a_database_procedure_is_refused_by_name() {
    let rebuilt = rebuild_objects(
        "Transitive",
        &[
            procedure(
                "Top",
                "Middle()
",
            ),
            procedure(
                "Middle", "Leaf()
",
            ),
            procedure(
                "Leaf",
                "For Each
    &Total = &Total + 1
EndFor
",
            ),
            procedure(
                "Unrelated",
                "&Total = 1
",
            ),
        ],
    );

    for name in ["Top", "Middle", "Leaf"] {
        assert_not_emitted(&rebuilt, name);
    }
    assert_eq!(rebuilt.status, RebuildStatus::Failed);
    let find = |name: &str| {
        rebuilt
            .errors
            .iter()
            .find(|e| e.starts_with(&format!("failed to generate C# for Procedure '{name}'")))
            .unwrap_or_else(|| panic!("{name} is refused by name: {:?}", rebuilt.errors))
    };
    assert!(find("Leaf").contains("For Each"), "{:?}", rebuilt.errors);
    let middle = find("Middle");
    assert!(
        middle.contains("needs Procedure 'Leaf' (not generated: ") && middle.contains("For Each"),
        "{middle}"
    );
    let top = find("Top");
    assert!(
        top.contains("needs Procedure 'Middle' (not generated: ")
            && top.contains("needs Procedure 'Leaf'")
            && top.contains("For Each"),
        "{top}"
    );
    assert!(
        rebuilt
            .cs_files
            .iter()
            .any(|file| file.to_ascii_lowercase().contains("unrelated")),
        "the Procedure with no database statement is still generated: {:?}",
        rebuilt.cs_files
    );
}

/// A formula such as `Sum(Amount)` reads rows from an expression. It parses as a call, and no Procedure
/// of that name exists, so the rebuild (which resolves calls against the Procedures it emits) refuses it
/// rather than writing a call to a class that is not there. Only the rebuild is pinned: `emit_checked`
/// alone has no call table and still writes `new SUM(context).executeUdp(..)` for it, a library-only
/// path no build reaches.
#[test]
fn a_database_formula_in_an_expression_is_refused_by_name() {
    for (name, formula) in [("Summed", "Sum"), ("Counted", "Count"), ("Found", "Find")] {
        let source = format!("&Total = {formula}(Amount)\n");
        let rebuilt = rebuild(name, &source);
        assert_not_emitted(&rebuilt, name);
        assert_eq!(rebuilt.generated_count, 0, "{name}: not generated");
        assert_eq!(
            rebuilt.status,
            RebuildStatus::Failed,
            "{name}: the run fails"
        );
        assert!(
            rebuilt
                .errors
                .iter()
                .any(|e| e.contains(&format!("Procedure '{name}'"))
                    && e.contains(&format!(
                        "needs Procedure '{}'",
                        formula.to_ascii_uppercase()
                    ))),
            "{name}: refused by name: {:?}",
            rebuilt.errors
        );
    }
}
