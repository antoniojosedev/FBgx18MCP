//! A rebuild writes each Procedure's call sites from its callees' parameters (the call table), so a
//! caller's `ref`/`out` keywords come from the callee's RULES. This test runs the real rebuild over a
//! caller and a callee, without the compiler (`skip_compile`, so the status is `Partial` by contract and
//! the test reads the emitted file and the refusal lists, not the status).

use gx_engine::{
    catalog::{Catalog, Edit, KbObject, ObjectId, ObjectKind, SourcePart},
    rebuild::{RebuildOptions, rebuild_all},
};
use std::fs;

fn procedure(name: &str, source: &str, variables: &str, rules: &str) -> KbObject {
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
        source: variables.into(),
    });
    if !rules.is_empty() {
        object.source_parts.push(SourcePart {
            name: "RULES".into(),
            source: rules.into(),
        });
    }
    object
}

const CALLEE_VARIABLES: &str = r#"{
    "A": {"kind": "numeric", "precision": 5, "scale": 2},
    "B": {"kind": "numeric", "precision": 5, "scale": 2},
    "C": {"kind": "numeric", "precision": 5, "scale": 2}
}"#;

const DATE_CALLEE_VARIABLES: &str = r#"{
    "A": {"kind": "numeric", "precision": 5, "scale": 2},
    "B": {"kind": "numeric", "precision": 5, "scale": 2},
    "C": {"kind": "numeric", "precision": 5, "scale": 2},
    "D": {"kind": "date"},
    "T": {"kind": "character", "length": 10}
}"#;

const CALLER_VARIABLES: &str = r#"{
    "X": {"kind": "numeric", "precision": 5, "scale": 2},
    "Y": {"kind": "numeric", "precision": 5, "scale": 2},
    "Z": {"kind": "numeric", "precision": 5, "scale": 2}
}"#;

#[test]
fn a_rebuild_writes_each_call_site_from_the_callees_rules() {
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[
                Edit::Upsert(procedure(
                    "CallerProc",
                    "CalleeProc(&X, &Y, &Z)\n",
                    CALLER_VARIABLES,
                    "",
                )),
                Edit::Upsert(procedure(
                    "CalleeProc",
                    "msg('hi', status)\n",
                    CALLEE_VARIABLES,
                    "Parm(in:&A, out:&B, inout:&C);",
                )),
            ],
        )
        .expect("catalog setup");

    let temp_dir = std::env::temp_dir().join(format!("gx_call_sites_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_concurrency(1)
        .with_skip_compile(true)
        .with_assembly_name("CallSitesApp");
    let report = rebuild_all(&catalog, &temp_dir, &options).expect("the rebuild runs");

    assert!(report.errors.is_empty(), "{:?}", report.errors);
    assert!(
        report.refused_objects.is_empty(),
        "{:?}",
        report.refused_objects
    );
    // The caller takes no parameters, so it is split (`acallerproc.cs` holds the body and the call;
    // `callerproc.cs` is the wrapper that starts it).
    let code = fs::read_to_string(temp_dir.join("acallerproc.cs")).expect("the caller was written");
    let callee =
        fs::read_to_string(temp_dir.join("CalleeProc.cs")).expect("the callee was written");
    let _ = fs::remove_dir_all(&temp_dir);
    // The callee's class is its name in lower case, as the SDK writes it (`new rustcallechoprobe(context)
    // .executeUdp(` in the SDK output), and the call names that same class: the two sides of a call must
    // agree in case (C# is case-sensitive).
    assert!(
        callee.contains("public class calleeproc : GXProcedure"),
        "{callee}"
    );
    assert!(
        code.contains("new calleeproc(context).execute(AV_X, out AV_Y, ref AV_Z);"),
        "the caller was written from the callee's RULES: {code}"
    );
}

/// A callee this build refuses at emission (a `Trim()` on a call result has no evidenced form), so it is
/// not generated, and a caller that names it must not be written: csc would fail on a class that does not
/// exist. The caller is refused by name, with the callee and its reason, and nothing is written for it.
#[test]
fn a_caller_of_a_procedure_that_is_not_generated_is_refused_by_name() {
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[
                Edit::Upsert(procedure(
                    "CallerProc",
                    "CalleeProc(&X, &Y, &Z)\n",
                    CALLER_VARIABLES,
                    "",
                )),
                Edit::Upsert(procedure(
                    "CalleeProc",
                    "Msg(&A.Length().Trim(), status)\n",
                    CALLEE_VARIABLES,
                    "Parm(in:&A, out:&B, inout:&C);",
                )),
            ],
        )
        .expect("catalog setup");

    let temp_dir = std::env::temp_dir().join(format!("gx_caller_refused_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_concurrency(1)
        .with_skip_compile(true)
        .with_assembly_name("CallerRefusedApp");
    let report = rebuild_all(&catalog, &temp_dir, &options).expect("the rebuild runs");

    let caller_written = temp_dir.join("CallerProc.cs").exists();
    let callee_written = temp_dir.join("CalleeProc.cs").exists();
    let _ = fs::remove_dir_all(&temp_dir);

    assert!(!callee_written, "the callee's refusal stands");
    assert!(
        !caller_written,
        "a caller of a Procedure that is not generated must not be written"
    );
    // The callee's specification is refused (a `Trim()` on a call result), so it never enters the call
    // table and the caller's call has no signature. The caller is refused by name with the callee.
    let caller_error = report
        .errors
        .iter()
        .find(|e| e.contains("for Procedure 'CallerProc'"))
        .unwrap_or_else(|| panic!("the caller is refused by name: {:?}", report.errors));
    assert!(
        caller_error.contains("needs Procedure 'CALLEEPROC'"),
        "the refusal names the callee: {caller_error}"
    );
    assert!(
        caller_error.contains("does not generate it"),
        "the refusal says the callee is not generated: {caller_error}"
    );
    assert!(
        report
            .errors
            .iter()
            .any(|e| e.contains("Procedure 'CalleeProc' specification error")
                && e.contains("Trim()")),
        "the callee's own refusal is reported: {:?}",
        report.errors
    );
}

/// The callee is generated, so the caller is written with the callee's declared class name.
#[test]
fn a_caller_of_a_generated_procedure_is_written() {
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[
                Edit::Upsert(procedure(
                    "CallerProc",
                    "CalleeProc(&X, &Y, &Z)\n",
                    CALLER_VARIABLES,
                    "",
                )),
                Edit::Upsert(procedure(
                    "CalleeProc",
                    "msg('hi', status)\n",
                    CALLEE_VARIABLES,
                    "Parm(in:&A, out:&B, inout:&C);",
                )),
            ],
        )
        .expect("catalog setup");

    let temp_dir = std::env::temp_dir().join(format!("gx_caller_kept_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_concurrency(1)
        .with_skip_compile(true)
        .with_assembly_name("CallerKeptApp");
    let report = rebuild_all(&catalog, &temp_dir, &options).expect("the rebuild runs");

    let code = fs::read_to_string(temp_dir.join("acallerproc.cs"));
    let _ = fs::remove_dir_all(&temp_dir);
    assert!(report.errors.is_empty(), "{:?}", report.errors);
    let code = code.expect("the caller is written when its callee is generated");
    assert!(
        code.contains("new calleeproc(context).execute("),
        "the caller names the callee's class: {code}"
    );
}

/// A callee whose signature is in the call table but whose body the emitter refuses (a `ToString()` of a
/// character variable, which this build does not write; a date's ToString is written now), so the callee is not generated. Its caller must not be written
/// either: the caller is refused by the callee's emission outcome, and names the callee and its reason.
#[test]
fn a_caller_of_a_procedure_whose_emission_is_refused_is_refused_by_name() {
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[
                Edit::Upsert(procedure(
                    "CallerProc",
                    "CalleeProc(&X, &Y, &Z)
",
                    CALLER_VARIABLES,
                    "",
                )),
                Edit::Upsert(procedure(
                    "CalleeProc",
                    "msg(&T.ToString(), status)
",
                    DATE_CALLEE_VARIABLES,
                    "Parm(in:&A, out:&B, inout:&C);",
                )),
            ],
        )
        .expect("catalog setup");

    let temp_dir = std::env::temp_dir().join(format!("gx_caller_emit_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_concurrency(1)
        .with_skip_compile(true)
        .with_assembly_name("CallerEmitApp");
    let report = rebuild_all(&catalog, &temp_dir, &options).expect("the rebuild runs");

    let caller_written = temp_dir.join("CallerProc.cs").exists();
    let callee_written = temp_dir.join("CalleeProc.cs").exists();
    let _ = fs::remove_dir_all(&temp_dir);

    assert!(!callee_written, "the callee's refusal stands");
    assert!(
        !caller_written,
        "a caller of a Procedure whose emission is refused must not be written"
    );
    // The propagated reason (not the emitter's "does not generate it") is what refuses the caller here:
    // the callee is in the call table, and its own emission was refused.
    assert!(
        report
            .errors
            .iter()
            .any(|e| e.contains("for Procedure 'CallerProc'")
                && e.contains("needs Procedure 'CalleeProc' (not generated: ")),
        "the caller is refused by the callee's emission outcome: {:?}",
        report.errors
    );
}

/// The callee's class is written in lower case, as the SDK writes it, whatever case the name is stored in.
/// The call must name that same class: C# is case-sensitive, so the two sides must agree.
#[test]
fn a_call_names_the_lowercase_class_the_sdk_writes() {
    let caller = procedure(
        "CallerProc",
        "CalleeProc(&X, &Y, &Z)\n",
        CALLER_VARIABLES,
        "",
    );
    let callee = procedure(
        "CalleeProc",
        "msg('hi', status)\n",
        CALLEE_VARIABLES,
        "Parm(in:&A, out:&B, inout:&C);",
    );
    let objects = [caller, callee]
        .into_iter()
        .map(|object| (object.id.clone(), object))
        .collect();
    let catalog = Catalog::from_objects_map(objects);

    let temp_dir = std::env::temp_dir().join(format!("gx_call_case_{}", std::process::id()));
    let _ = fs::remove_dir_all(&temp_dir);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_concurrency(1)
        .with_skip_compile(true)
        .with_assembly_name("CallCaseApp");
    let report = rebuild_all(&catalog, &temp_dir, &options).expect("the rebuild runs");

    let callee_code = fs::read_to_string(temp_dir.join("CalleeProc.cs"));
    let caller_code = fs::read_to_string(temp_dir.join("acallerproc.cs"));
    let _ = fs::remove_dir_all(&temp_dir);
    assert!(report.errors.is_empty(), "{:?}", report.errors);
    let callee_code = callee_code.expect("the callee is written under its declared name");
    let caller_code = caller_code.expect("the caller is written");
    assert!(
        callee_code.contains("public class calleeproc : GXProcedure"),
        "{callee_code}"
    );
    assert!(
        caller_code.contains("new calleeproc(context).execute(AV_X, out AV_Y, ref AV_Z);"),
        "the call names the declared class, not the upper-cased token: {caller_code}"
    );
}
