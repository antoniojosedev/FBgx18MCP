//! SDT member access in a Procedure is emitted in the SDK's form, and a member access that does not
//! resolve against the SDTs this build emits fails closed, naming the variable and the member.
//!
//! The real-object test compares the emitted member tokens of KBTeste's `SDM87563700` with the token
//! sequence of the GeneXus-generated `asdm87563700.cs` (fixtures in `tests/fixtures/sdt_members/`, read
//! from the KB, not typed by hand). Variable names differ (`AV8First` in the SDK, `AV_First` here), so
//! the comparison normalises them; everything else, including order, must match.

use gx_engine::{
    catalog::{KbObject, ObjectId, ObjectKind, SourcePart},
    csharp::{
        ProcedureClass,
        sdt::{SdtClass, SdtIndex},
    },
    model::DataType,
    udm::stored_sdt::SdtNode,
};
use std::sync::Arc;

const SDM_SOURCE: &str = include_str!("fixtures/sdt_members/SDM87563700.SOURCE.txt");
const SDM_VARIABLES: &str = include_str!("fixtures/sdt_members/SDM87563700.VARIABLES.json");
const SDT_STRUCTURE: &str = include_str!("fixtures/sdt_members/RustFreshSdt091817.STRUCTURE.json");
const SDK_LINES: &str = include_str!("fixtures/sdt_members/SDM87563700.sdk-lines.txt");

const PROBE_VARIABLES: &str = r#"{
    "First": {"kind": "record", "schema": "Probe"},
    "Second": {"kind": "record", "schema": "Probe"},
    "Count": {"kind": "numeric", "precision": 4, "scale": 0}
}"#;

fn procedure(source: &str, variables: &str) -> ProcedureClass {
    let mut object = KbObject::new(
        ObjectId::new("probe_proc").unwrap(),
        "ProbeProc",
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
    ProcedureClass::from_kb_object(&object).expect("the procedure parses")
}

/// Substring match that ignores case: the parser writes member names in upper case.
fn contains_ci(text: &str, needle: &str) -> bool {
    text.to_ascii_uppercase()
        .contains(&needle.to_ascii_uppercase())
}

fn field(name: &str) -> SdtNode {
    SdtNode::Field {
        name: name.into(),
        data_type: DataType::Numeric {
            precision: 4,
            scale: 0,
        },
    }
}

fn level(name: &str, collection: bool, children: Vec<SdtNode>) -> SdtNode {
    SdtNode::Level {
        name: name.into(),
        collection,
        item_name: None,
        children,
    }
}

/// An index holding one SDT `Probe` with the given top-level nodes.
fn probe_index(nodes: Vec<SdtNode>) -> Arc<SdtIndex> {
    let mut class = SdtClass::new("Probe");
    class.nodes = nodes;
    Arc::new(SdtIndex::from_classes([class]).expect("one class"))
}

fn probe_with_amount() -> Arc<SdtIndex> {
    probe_index(vec![field("Amount")])
}

fn emit(source: &str, index: Arc<SdtIndex>) -> Result<String, String> {
    procedure(source, PROBE_VARIABLES)
        .with_sdts(index)
        .emit_checked()
}

// ---- resolved member access: the SDK's gxTpr_ form ----------------------------------------------

#[test]
fn a_declared_sdt_member_is_emitted_in_the_sdk_gxtpr_form_whatever_the_case_written() {
    let code = emit(
        "&First = new()\n&First.Amount = 7\n&First.amount = 8\n",
        probe_with_amount(),
    )
    .expect("every member resolves");

    assert!(code.contains("AV_First = new SdtProbe(context);"), "{code}");
    assert!(code.contains("AV_First.gxTpr_Amount = 7;"), "{code}");
    assert!(code.contains("AV_First.gxTpr_Amount = 8;"), "{code}");
    assert!(
        !code.contains("AV_First.AMOUNT") && !code.contains("AV_First.amount"),
        "a raw member name must not reach the output:\n{code}"
    );
}

#[test]
fn a_nested_level_member_resolves_through_its_class() {
    let index = probe_index(vec![level("Child", false, vec![field("Amount")])]);
    let code = emit("&First.Child.Amount = 3\n", index).expect("resolves");
    assert!(
        code.contains("AV_First.gxTpr_Child.gxTpr_Amount = 3;"),
        "{code}"
    );
}

#[test]
fn a_collection_item_is_cast_to_its_class_as_the_sdk_casts_it() {
    let index = probe_index(vec![level("Rows", true, vec![field("Amount")])]);
    let code = emit(
        "&First.Rows.Item(1).Amount = 9\n&First.Rows.Item(2).Amount = &First.Rows.Item(1).Amount\n",
        index,
    )
    .expect("resolves");
    // The item class of a top-level collection is `Sdt<Holder>_<Level>Item` (see sdt.rs).
    assert!(
        code.contains("((SdtProbe_RowsItem)AV_First.gxTpr_Rows.Item((int)(1))).gxTpr_Amount = 9;"),
        "{code}"
    );
    assert!(
        code.contains("((SdtProbe_RowsItem)AV_First.gxTpr_Rows.Item((int)(2))).gxTpr_Amount"),
        "{code}"
    );
}

#[test]
fn a_clone_of_an_sdt_is_cast_to_its_class_as_the_sdk_casts_it() {
    let code = emit(
        "&Second = &First.Clone()\n&Second.Amount = 11\n",
        probe_with_amount(),
    )
    .expect("resolves");
    assert!(
        code.contains("AV_Second = (SdtProbe)(AV_First.Clone());"),
        "{code}"
    );
    assert!(code.contains("AV_Second.gxTpr_Amount = 11;"), "{code}");
}

// ---- the family rule applies to a member as it does to a variable ------------------------------

/// An index holding one SDT `Probe` with a `Name` member of `VARCHAR(40)`.
fn varchar_probe() -> Arc<SdtIndex> {
    probe_index(vec![SdtNode::Field {
        name: "Name".into(),
        data_type: DataType::Varchar { length: 40 },
    }])
}

#[test]
fn a_numeric_literal_assigned_to_a_varchar_member_is_refused_by_name_not_written() {
    // The SDK text of LiveSdtMember60744600.Amount is `VARCHAR(40)`; a procedure that assigns it a
    // number has no conversion the SDK evidences, so it is refused, not written.
    let error = emit("&First.Name = 7\n", varchar_probe()).unwrap_err();
    // The parser upper-cases member names, so the member is compared case-insensitively.
    assert!(
        contains_ci(&error, "assignment to member 'Name'"),
        "{error}"
    );
    assert!(
        error.contains("numeric literal value cannot be assigned to a varchar member"),
        "{error}"
    );
}

#[test]
fn a_text_literal_assigned_to_a_varchar_member_is_written() {
    let code = emit("&First.Name = \"x\"\n", varchar_probe()).expect("text into a varchar member");
    assert!(contains_ci(&code, "gxTpr_Name"), "{code}");
}

// ---- fail closed: a member access that does not resolve is an error, by name --------------------

#[test]
fn a_member_of_an_sdt_this_build_does_not_emit_fails_closed_by_name() {
    let empty = Arc::new(SdtIndex::default());
    let error = emit("&First.Amount = 7\n", empty).unwrap_err();
    assert!(contains_ci(&error, "Amount"), "{error}");
    assert!(contains_ci(&error, "SdtProbe"), "{error}");
    assert!(error.contains("not emitted by this build"), "{error}");
}

#[test]
fn an_unknown_member_fails_closed_naming_the_member_and_its_sdt() {
    let error = emit("&First.Missing = 7\n", probe_with_amount()).unwrap_err();
    assert!(contains_ci(&error, "member 'Missing'"), "{error}");
    assert!(contains_ci(&error, "no such member in SdtProbe"), "{error}");
}

#[test]
fn a_member_of_a_scalar_variable_fails_closed() {
    let error = emit("&Count.Amount = 7\n", probe_with_amount()).unwrap_err();
    assert!(error.contains("is not an SDT variable"), "{error}");
    assert!(error.contains("AMOUNT"), "{error}");
}

#[test]
fn a_member_of_an_undeclared_variable_fails_closed() {
    let error = emit("&Missing.Amount = 7\n", probe_with_amount()).unwrap_err();
    assert!(error.contains("is not a declared variable"), "{error}");
}

#[test]
fn item_on_a_non_collection_member_fails_closed() {
    let index = probe_index(vec![level("Child", false, vec![field("Amount")])]);
    let error = emit("&First.Child.Item(1).Amount = 1\n", index).unwrap_err();
    assert!(error.contains("Item(n)"), "{error}");
}

#[test]
fn a_collection_member_without_item_fails_closed() {
    let index = probe_index(vec![level("Rows", true, vec![field("Amount")])]);
    let error = emit("&First.Rows.Amount = 1\n", index).unwrap_err();
    assert!(error.contains("Item(n) must come first"), "{error}");
}

#[test]
fn a_member_of_a_scalar_member_fails_closed() {
    let error = emit("&First.Amount.Deeper = 1\n", probe_with_amount()).unwrap_err();
    assert!(
        error.contains("the receiver is a scalar, which has no members"),
        "{error}"
    );
}

#[test]
fn add_on_a_non_collection_member_fails_closed() {
    let index = probe_index(vec![level("Child", false, vec![field("Amount")])]);
    let error = emit("&First.Child.Add(&First)\n", index).unwrap_err();
    assert!(
        error.contains("the receiver is not a collection"),
        "{error}"
    );
}

#[test]
fn every_unresolved_access_is_named_not_just_the_first() {
    let error = emit(
        "&First.Missing = 1\n&First.Other = 2\n",
        probe_with_amount(),
    )
    .unwrap_err();
    assert!(contains_ci(&error, "member 'Missing'"), "{error}");
    assert!(contains_ci(&error, "member 'Other'"), "{error}");
}

#[test]
fn an_sdt_whose_name_starts_with_sdt_is_not_mis_resolved() {
    // to_csharp_type keeps a declared name that already starts with `Sdt`, while the class is emitted
    // as `Sdt<Name>`. The lookup misses, so the access fails closed instead of reaching another class.
    let mut class = SdtClass::new("SdtFoo");
    class.nodes = vec![field("Amount")];
    let index = Arc::new(SdtIndex::from_classes([class]).unwrap());
    let variables = r#"{"First": {"kind": "record", "schema": "SdtFoo"}}"#;
    let error = procedure("&First.Amount = 1\n", variables)
        .with_sdts(index)
        .emit_checked()
        .unwrap_err();
    assert!(error.contains("'SdtFoo' is not emitted"), "{error}");
}

#[test]
fn emit_without_checking_still_writes_the_output_the_checked_path_refuses() {
    // `emit` is the unchecked writer; a build uses `emit_checked`, which refuses this same access.
    let code = procedure("&First.Missing = 1\n", PROBE_VARIABLES)
        .with_sdts(probe_with_amount())
        .emit();
    // The parser writes a member name in upper case; GeneXus names are case-insensitive.
    assert!(code.contains("AV_First.MISSING = 1;"), "{code}");
}

// ---- the SDT index -------------------------------------------------------------------------------

#[test]
fn two_sdts_with_one_class_name_are_refused_not_resolved_to_either() {
    let a = SdtClass::new("Probe");
    let b = SdtClass::new("probe");
    let error = SdtIndex::from_classes([a, b]).unwrap_err();
    assert!(error.contains("share the class name"), "{error}");
}

#[test]
fn the_index_finds_a_class_without_regard_to_case() {
    let index = probe_with_amount();
    assert!(index.class("SDTPROBE").is_some());
    assert!(index.class("SdtOther").is_none());
}

// ---- the real object: SDM87563700 against the SDK's generated Procedure ---------------------------

/// The member-access tokens of generated C#, in order: `<recv>.gxTpr_<Member>` (the receiver is a
/// variable, or `)` after an item cast), `(Sdt<Name>)` casts, and `<var>.Clone()`. Variables are
/// normalised: `AV8First`, `AV_First` and `First` are all `First`.
fn member_tokens(code: &str) -> Vec<String> {
    let atoms = atoms(code);
    let mut out = Vec::new();
    for i in 0..atoms.len() {
        // `.gxTpr_X`: the receiver is the atom before the dot.
        if atoms[i] == "." && i + 1 < atoms.len() && atoms[i + 1].starts_with("gxTpr_") {
            let receiver = if i >= 1 { atoms[i - 1].as_str() } else { "" };
            let receiver = if receiver == ")" {
                ")".to_string()
            } else {
                normalise(receiver)
            };
            out.push(format!("{receiver}.{}", atoms[i + 1]));
        }
        // `.Clone()`
        if atoms[i] == "Clone"
            && i >= 1
            && atoms[i - 1] == "."
            && i + 2 < atoms.len()
            && atoms[i + 1] == "("
            && atoms[i + 2] == ")"
        {
            let receiver = if i >= 2 {
                normalise(&atoms[i - 2])
            } else {
                String::new()
            };
            out.push(format!("{receiver}.Clone()"));
        }
        // `(Sdt<Name>)` cast
        if atoms[i] == "("
            && i + 2 < atoms.len()
            && atoms[i + 1].starts_with("Sdt")
            && atoms[i + 2] == ")"
        {
            out.push(format!("cast:{}", atoms[i + 1]));
        }
    }
    out
}

/// Identifiers, `.`, `(` and `)` as separate atoms; everything else separates.
fn atoms(code: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut current = String::new();
    for ch in code.chars() {
        if ch.is_ascii_alphanumeric() || ch == '_' {
            current.push(ch);
            continue;
        }
        if !current.is_empty() {
            out.push(std::mem::take(&mut current));
        }
        if ch == '.' || ch == '(' || ch == ')' {
            out.push(ch.to_string());
        }
    }
    if !current.is_empty() {
        out.push(current);
    }
    out
}

/// `AV8First`, `AV_First`, `First` → `First`.
fn normalise(name: &str) -> String {
    let stripped = name.strip_prefix("AV").unwrap_or(name);
    let stripped = stripped.trim_start_matches(|c: char| c.is_ascii_digit());
    stripped.trim_start_matches('_').to_string()
}

#[test]
fn the_real_object_emits_the_sdk_member_tokens_in_the_sdk_order() {
    // The STRUCTURE fixture is the reader's portable text, exactly as `kb-read` returned it.
    let mut sdt = KbObject::new(
        ObjectId::new("rustfresh").unwrap(),
        "RustFreshSdt091817",
        ObjectKind::StructuredDataType,
    );
    sdt.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: SDT_STRUCTURE.into(),
    });
    let class = SdtClass::from_kb_object(&sdt).expect("the real structure decodes");
    let index = Arc::new(SdtIndex::from_classes([class]).unwrap());

    let code = procedure(SDM_SOURCE, SDM_VARIABLES)
        .with_sdts(index)
        .emit_checked()
        .expect("every member of the real object resolves");

    // The SDK writes `AV8First.gxTpr_Amount = 7;`; this engine writes `AV_First.gxTpr_Amount = 7;`.
    assert!(code.contains("AV_First.gxTpr_Amount = 7;"), "{code}");
    assert!(code.contains("AV_Second.gxTpr_Amount = 9;"), "{code}");
    assert!(
        code.contains("AV_Second = (SdtRustFreshSdt091817)(AV_First.Clone());"),
        "{code}"
    );

    let ours = member_tokens(&code);
    let sdk = member_tokens(SDK_LINES);
    assert!(!sdk.is_empty(), "the SDK fixture has member tokens");
    assert_eq!(
        ours, sdk,
        "the member tokens of the emitted Procedure match the SDK's, in order"
    );
}

// ---- the build: a Procedure whose member does not resolve is an error and is not written ---------

#[test]
fn a_rebuild_refuses_a_procedure_whose_member_does_not_resolve_and_writes_no_file() {
    use gx_engine::{
        catalog::Catalog,
        rebuild::{RebuildOptions, RebuildStatus, rebuild_all},
    };
    use std::collections::BTreeMap;

    let mut sdt = KbObject::new(
        ObjectId::new("probe_sdt").unwrap(),
        "Probe",
        ObjectKind::StructuredDataType,
    );
    sdt.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{"fields": [{"node": "field", "name": "Amount", "data_type": {"kind": "numeric", "precision": 4, "scale": 0}}]}"#.into(),
    });
    let mut proc = KbObject::new(
        ObjectId::new("probe_proc").unwrap(),
        "ProbeProc",
        ObjectKind::Procedure,
    );
    proc.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "&First = new()\n&First.Missing = 7\n".into(),
    });
    proc.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: r#"{"First": {"kind": "record", "schema": "Probe"}}"#.into(),
    });
    // The reader builds its catalog from the KB's own names (`from_objects_map`), so the test does too:
    // `Catalog::apply` would upper-case them.
    // The reader records the variable's SDT as a reference; an SDT nothing reaches is not built.
    proc.references.push(gx_engine::catalog::Reference {
        kind: gx_engine::catalog::ReferenceKind::Uses,
        target: sdt.id.clone(),
    });
    let mut objects = BTreeMap::new();
    objects.insert(sdt.id.clone(), sdt);
    objects.insert(proc.id.clone(), proc);
    let catalog = Catalog::from_objects_map(objects);

    let out = std::env::temp_dir().join(format!("gx_sdt_members_refused_{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&out);
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_skip_compile(true);
    let report = rebuild_all(&catalog, &out, &options).expect("rebuild completes");

    assert_eq!(
        report.status,
        RebuildStatus::Failed,
        "an unresolved member is an error"
    );
    assert!(
        report.errors.iter().any(|error| {
            error.contains("failed to generate C# for Procedure 'ProbeProc'")
                && contains_ci(error, "member 'Missing'")
                && contains_ci(error, "SdtProbe")
        }),
        "the error names the procedure and the member: {:?}",
        report.errors
    );
    assert!(
        !out.join("ProbeProc.cs").exists(),
        "a Procedure with an unresolved member is never written"
    );
    // The SDT that does resolve is still written.
    assert!(out.join("SdtProbe.cs").exists(), "the valid SDT is emitted");
    let _ = std::fs::remove_dir_all(&out);
}

// ---- ToString() of a value, and collection Count and Clone -------------------------------------
// The text of a numeric is `StringUtil.Str` with the value's own precision and decimals, as the SDK
// writes it; the SDK's collection members are `gxTpr_Rows.Count` and `(GXBaseCollection<Item>)(…Clone())`.

const TEXT_VARIABLES: &str = r#"{
    "First": {"kind": "record", "schema": "Probe"},
    "Hits": {"kind": "numeric", "precision": 5, "scale": 2},
    "Label": {"kind": "character", "length": 20}
}"#;

fn emit_with_variables(
    source: &str,
    variables: &str,
    index: Arc<SdtIndex>,
) -> Result<String, String> {
    procedure(source, variables).with_sdts(index).emit_checked()
}

#[test]
fn trim_of_a_numeric_tostring_is_the_str_form_with_the_declared_precision_and_scale() {
    let code = emit_with_variables(
        "Msg(\"hits=\" + &Hits.ToString().Trim(), status)\n",
        TEXT_VARIABLES,
        probe_with_amount(),
    )
    .expect("a numeric ToString() is mapped");
    assert!(
        code.contains("StringUtil.Trim(StringUtil.Str((decimal)(AV_Hits), 5, 2))"),
        "the SDK writes StringUtil.Trim(StringUtil.Str( AV8Hits, 5, 2)) for N(5,2):\n{code}"
    );
}

#[test]
fn tostring_of_a_text_variable_is_refused_by_name_not_guessed() {
    let error = emit_with_variables(
        "Msg(&Label.ToString().Trim(), status)\n",
        TEXT_VARIABLES,
        probe_with_amount(),
    )
    .unwrap_err();
    assert!(
        contains_ci(&error, "ToString()") && contains_ci(&error, "numeric values only"),
        "{error}"
    );
}

#[test]
fn trim_of_a_non_variable_expression_is_still_refused_by_the_parser() {
    // The receiver is an arithmetic expression, not a variable, member, or ToString() of one. The
    // parser refuses it, so the Procedure never reaches the emitter.
    let mut object = KbObject::new(
        ObjectId::new("probe_proc").unwrap(),
        "ProbeProc",
        ObjectKind::Procedure,
    );
    object.source_parts.push(SourcePart {
        name: "SOURCE".into(),
        source: "Msg(\"x\" + (&Hits + 1).ToString().Trim(), status)\n".into(),
    });
    object.source_parts.push(SourcePart {
        name: "VARIABLES".into(),
        source: TEXT_VARIABLES.into(),
    });
    let error = ProcedureClass::from_kb_object(&object).unwrap_err();
    assert!(contains_ci(&error, "Trim()"), "{error}");
}

#[test]
fn a_collection_count_is_the_gxbasecollection_count_property() {
    let index = probe_index(vec![level("Rows", true, vec![field("Amount")])]);
    let code = emit_with_variables(
        "&Total = &First.Rows.Count\n",
        r#"{
    "First": {"kind": "record", "schema": "Probe"},
    "Total": {"kind": "numeric", "precision": 4, "scale": 0}
}"#,
        index,
    )
    .expect("Count resolves on a collection");
    assert!(
        code.contains("AV_First.gxTpr_Rows.Count"),
        "the SDK writes AV8First.gxTpr_Rows.Count:\n{code}"
    );
}

#[test]
fn a_collection_clone_is_cast_to_its_collection_class_as_the_sdk_casts_it() {
    let index = probe_index(vec![level("Rows", true, vec![field("Amount")])]);
    let code = emit_with_variables(
        "&Second.Rows = &First.Rows.Clone()\n",
        r#"{
    "First": {"kind": "record", "schema": "Probe"},
    "Second": {"kind": "record", "schema": "Probe"}
}"#,
        index,
    )
    .expect("Clone resolves on a collection");
    assert!(
        code.contains("AV_Second.gxTpr_Rows = (GXBaseCollection<SdtProbe_RowsItem>)(AV_First.gxTpr_Rows.Clone());"),
        "the SDK writes AV9Second.gxTpr_Rows = (GXBaseCollection<SdtRSN03944700_RowsItem>)(AV8First.gxTpr_Rows.Clone()):\n{code}"
    );
}

// ---- a built-in this build does not write is refused by name, not written as a Procedure call ----
// `CHR` is accepted by the parser but has no C# arm anywhere in csharp/. Without the refusal, the default
// arm would write `new CHR(context).executeUdp(...)`, a class that does not exist. (STRREPLACE was refused
// the same way until it got its SDK arm; see tests/csharp_sdk_builtins.rs.)

#[test]
fn an_unmapped_builtin_is_refused_by_name_not_written_as_a_procedure_call() {
    let error = emit_with_variables("&Text = Chr(&Label)\n", TEXT_VARIABLES, probe_with_amount())
        .unwrap_err();
    assert!(
        contains_ci(&error, "built-in CHR()") && contains_ci(&error, "writes no C#"),
        "the refusal names the built-in: {error}"
    );
}

#[test]
fn a_call_to_a_procedure_is_still_written_as_an_execute_udp_call() {
    // A name the parser does not know as a built-in is a Procedure, and its call form is unchanged.
    let code = emit_with_variables(
        "&Result = MyOtherProc(&Hits)\n",
        TEXT_VARIABLES,
        probe_with_amount(),
    )
    .expect("a Procedure call emits");
    // The parser upper-cases call names, so the class is written upper-cased: the call form is the same.
    assert!(
        contains_ci(&code, "new MyOtherProc(context).executeUdp("),
        "a user Procedure call keeps the executeUdp form:\n{code}"
    );
}

// ---- collection append: the SDK's two-argument form --------------------------------------------

const ROW_VARIABLES: &str = r#"{
    "First": {"kind": "record", "schema": "Probe"},
    "Row": {"kind": "record", "schema": "Probe_RowsItem"}
}"#;

/// The SDK's append line, copied from `C:\KBs\KBTeste\NetModel\Web\artdx765000a.cs` (line 72). The SDK
/// numbers its variables (`AV8First`, `AV9Row`); the emitter writes `AV_First` and `AV_Row`.
const SDK_APPEND: &str = "AV8First.gxTpr_Rows.Add(AV9Row, 0);";

/// Writes every variable name in one form: `AV8First` and `AV_First` both become `AVFirst`.
fn without_variable_numbers(text: &str) -> String {
    let mut out = String::new();
    let mut rest = text;
    while let Some(pos) = rest.find("AV") {
        out.push_str(&rest[..pos]);
        out.push_str("AV");
        rest = rest[pos + 2..].trim_start_matches(|c: char| c.is_ascii_digit() || c == '_');
    }
    out.push_str(rest);
    out
}

#[test]
fn a_collection_append_is_emitted_in_the_sdk_two_argument_form() {
    let index = probe_index(vec![level("Rows", true, vec![field("Amount")])]);
    let code = procedure("&First.Rows.Add(&Row)\n", ROW_VARIABLES)
        .with_sdts(index)
        .emit_checked()
        .expect("the append resolves");
    let appends: Vec<&str> = code
        .lines()
        .map(str::trim)
        .filter(|line| line.contains("gxTpr_Rows.Add("))
        .collect();
    assert_eq!(appends.len(), 1, "one append is emitted:\n{code}");
    assert_eq!(
        without_variable_numbers(appends[0]),
        without_variable_numbers(SDK_APPEND),
        "the append must have the SDK's two-argument form:\n{code}"
    );
}

#[test]
fn a_collection_append_refuses_the_root_sdt_instead_of_its_item_type() {
    // SDK GEN41 RTM91046200.xml: spc0023, Row is RSN03944700 rather than
    // RSN03944700.RowsItem. This synthetic shape exercises the same mismatch.
    let index = probe_index(vec![level("Rows", true, vec![field("Amount")])]);
    let error = procedure("&First.Rows.Add(&Second)\n", PROBE_VARIABLES)
        .with_sdts(index)
        .emit_checked()
        .expect_err("an SDT root is not a collection item");
    assert!(error.contains("Add()"), "{error}");
    assert!(error.contains("SdtProbe_RowsItem"), "{error}");
    assert!(error.contains("SdtProbe"), "{error}");
}

#[test]
fn a_collection_append_checks_member_and_item_expression_types() {
    let index = probe_index(vec![
        level("Rows", true, vec![field("Amount")]),
        level("OtherRows", true, vec![field("Amount")]),
        level("Child", false, vec![field("Amount")]),
    ]);
    for value in [
        "&First.OtherRows.Item(1)",
        "&First.OtherRows.Item(1).Clone()",
        "&First.Child",
        "&Count",
        "7",
    ] {
        let source = format!("&First.Rows.Add({value})\n");
        let error = procedure(&source, PROBE_VARIABLES)
            .with_sdts(index.clone())
            .emit_checked()
            .expect_err("different types must not be silently added");
        assert!(error.contains("Add()"), "{value}: {error}");
        assert!(error.contains("SdtProbe_RowsItem"), "{value}: {error}");
    }
    for value in ["&First.Rows.Item(1)", "&First.Rows.Item(1).Clone()"] {
        procedure(&format!("&First.Rows.Add({value})\n"), PROBE_VARIABLES)
            .with_sdts(index.clone())
            .emit_checked()
            .expect("the item type matches");
    }
}

#[test]
fn a_root_collection_checks_its_item_type_and_resolves_item_members() {
    let variables = r#"{
        "First":{"kind":"base_collection","schema":"Probe_RowsItem","item":"Probe.RowsItem","namespace":"Test"},
        "Second":{"kind":"record","schema":"Probe"},
        "Row":{"kind":"record","schema":"Probe_RowsItem"}
    }"#;
    let mut root = SdtClass::new("Probe");
    root.nodes = vec![level("Rows", true, vec![field("Amount")])];
    let mut classes = root.level_classes();
    classes.push(root);
    let index = Arc::new(SdtIndex::from_classes(classes).expect("unique root and item classes"));
    for source in ["&First.Add(&Second)\n", "&First.Add(&Second.Clone())\n"] {
        let error = procedure(source, variables)
            .with_sdts(index.clone())
            .emit_checked()
            .expect_err("root collection rejects the containing record");
        assert!(
            error.contains("Add()") && error.contains("SdtProbe_RowsItem"),
            "{error}"
        );
    }
    let code = procedure(
        "&First.Add(&Row)\n&First.Add(&Row.Clone())\n&First.Item(1).Amount = 2\n",
        variables,
    )
    .with_sdts(index)
    .emit_checked()
    .expect("matching root collection item and its scalar member resolve");
    assert!(code.contains(".gxTpr_Amount"), "{code}");
}

#[test]
fn a_rebuild_does_not_emit_a_procedure_with_a_wrong_collection_item_type() {
    use gx_engine::{
        catalog::Catalog,
        rebuild::{RebuildOptions, RebuildStatus, rebuild_all},
    };
    use std::collections::BTreeMap;

    let mut sdt = KbObject::new(
        ObjectId::new("append_sdt").unwrap(),
        "Probe",
        ObjectKind::StructuredDataType,
    );
    sdt.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{"fields":[{"node":"level","name":"Rows","collection":true,"item_name":null,"children":[{"node":"field","name":"Amount","data_type":{"kind":"numeric","precision":4,"scale":0}}]}]}"#.into(),
    });
    let mut proc = KbObject::new(
        ObjectId::new("append_proc").unwrap(),
        "BadAppend",
        ObjectKind::Procedure,
    );
    proc.source_parts.extend([
        SourcePart {
            name: "SOURCE".into(),
            source: "&First.Rows.Add(&Second)\n".into(),
        },
        SourcePart {
            name: "VARIABLES".into(),
            source: PROBE_VARIABLES.into(),
        },
    ]);
    // The reader records the variable's SDT as a reference; an SDT nothing reaches is not built.
    proc.references.push(gx_engine::catalog::Reference {
        kind: gx_engine::catalog::ReferenceKind::Uses,
        target: sdt.id.clone(),
    });
    let mut objects = BTreeMap::new();
    objects.insert(sdt.id.clone(), sdt);
    objects.insert(proc.id.clone(), proc);
    let catalog = Catalog::from_objects_map(objects);
    let out = std::env::temp_dir().join(format!("gx_bad_append_{}", std::process::id()));
    let options = RebuildOptions::new("net8.0")
        .with_clean(true)
        .with_skip_compile(true);
    let report = rebuild_all(&catalog, &out, &options).expect("report is produced");
    assert_eq!(report.status, RebuildStatus::Failed);
    assert!(
        report.errors.iter().any(|error| {
            error.contains("BadAppend")
                && error.contains("Add()")
                && error.contains("SdtProbe_RowsItem")
        }),
        "{:?}",
        report.errors
    );
    assert!(!out.join("BadAppend.cs").exists());
    assert!(!out.join("aBadAppend.cs").exists());
    assert!(out.join("SdtProbe.cs").exists());
    std::fs::remove_dir_all(out).expect("remove synthetic output");
}

// ---- the SDK's allocation and literal form -------------------------------------------------------

/// A Procedure with a RULES part, so that its parameters are known (`Parm(out:&First);`).
fn procedure_with_rules(source: &str, variables: &str, rules: &str) -> ProcedureClass {
    let mut object = KbObject::new(
        ObjectId::new("probe_proc").unwrap(),
        "ProbeProc",
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
    object.source_parts.push(SourcePart {
        name: "RULES".into(),
        source: rules.into(),
    });
    ProcedureClass::from_kb_object(&object).expect("the procedure parses")
}

/// The emitted `initialize()`, from its signature up to the formula comment that ends it.
fn initialize_body(code: &str) -> &str {
    let start = code
        .find("public override void initialize( )")
        .unwrap_or_else(|| panic!("no initialize(): {code}"));
    let rest = &code[start..];
    let end = rest
        .find("/* GeneXus formulas. */")
        .unwrap_or_else(|| panic!("no formula comment: {rest}"));
    &rest[..end]
}

#[test]
fn a_local_sdt_variable_is_allocated_in_initialize_as_the_sdk_does() {
    // SYNTHETIC shape, from the SDK's own output: `arpm64375900.cs` allocates each local SDT in
    // `initialize()` (`AV8First = new SdtRustFreshSdt091817(context);`) before the formulas.
    let code = emit("&First.Name = \"x\"\n", varchar_probe()).expect("emits");
    let init = initialize_body(&code);
    assert!(init.contains("AV_First = new SdtProbe(context);"), "{init}");
    assert!(
        init.contains("AV_Second = new SdtProbe(context);"),
        "{init}"
    );
    assert!(
        !init.contains("AV_Count"),
        "a numeric local is not allocated: {init}"
    );
}

#[test]
fn an_input_sdt_parameter_is_not_allocated_in_initialize_because_the_caller_supplies_it() {
    // From the SDK's own output: `rphin64375900.cs` takes `SdtX aP0_Value` and its initialize()
    // allocates nothing; an input parameter is the caller's object.
    let code = procedure_with_rules(
        "&First.Name = \"x\"\n&Second.Name = \"y\"\n",
        PROBE_VARIABLES,
        "Parm(in:&First);",
    )
    .with_sdts(varchar_probe())
    .emit_checked()
    .expect("emits");
    let init = initialize_body(&code);
    assert!(
        !init.contains("AV_First = new"),
        "an input parameter is not allocated: {init}"
    );
    assert!(
        init.contains("AV_Second = new SdtProbe(context);"),
        "{init}"
    );
}

#[test]
fn an_output_sdt_parameter_is_allocated_in_initialize_as_the_sdk_does() {
    // `rphout64375900.cs` takes `out SdtX aP0_Value` and its initialize() allocates it:
    // `AV8Value = new SdtRustFreshSdt091817(context);`.
    let code = procedure_with_rules(
        "&First.Name = \"x\"\n",
        PROBE_VARIABLES,
        "Parm(out:&First);",
    )
    .with_sdts(varchar_probe())
    .emit_checked()
    .expect("emits");
    let init = initialize_body(&code);
    assert!(init.contains("AV_First = new SdtProbe(context);"), "{init}");
}

#[test]
fn an_out_sdt_parameter_is_allocated_on_entry_to_execute_and_submit_as_the_sdk_does() {
    // SYNTHETIC shape, from the SDK's own output: `rphout68979100.cs` has `execute( out SdtX aP0_Value )`
    // and `executeSubmit( out SdtX aP0_Value )`, and each starts with `this.AV8Value = new SdtX(context) ;`
    // before the body. A null here would throw when the body reads a member before writing it.
    let code = procedure_with_rules(
        "&First.Name = \"x\"\n",
        PROBE_VARIABLES,
        "Parm(out:&First);",
    )
    .with_sdts(varchar_probe())
    .emit_checked()
    .expect("emits");
    let allocation = "this.AV_First = new SdtProbe(context) ;";
    assert_eq!(
        code.matches(allocation).count(),
        2,
        "execute and executeSubmit each allocate the out SDT: {code}"
    );
    assert!(
        !code.contains("this.AV_First = null ;"),
        "an out SDT is never null on entry: {code}"
    );
}

#[test]
fn a_text_literal_is_written_as_the_sdk_compiles_it_in_ascii() {
    // The stored SOURCE of RTM41852400 (KBTeste, read with `kb-read`) holds the characters U+00C2 U+00A0
    // around each `a`. The SDK's compiled literal holds one U+00A0 per pair (its output prints
    // `case13=[\xa0a\xa0]`). The emitter writes the stored characters as escapes; the writer resolves the
    // literal for the whole file, which is valid UTF-8, so the file's literal is the escape \u00A0.
    let source = "&First.Name = \"\u{00C2}\u{00A0}a\u{00C2}\u{00A0}\"\n";
    let code = emit(source, varchar_probe()).expect("emits");
    assert!(
        code.contains("\"\\u00C2\\u00A0a\\u00C2\\u00A0\""),
        "the emitter writes the stored characters: {code}"
    );
    let file = gx_engine::csharp::literals::resolve_sdk_literals(&code);
    assert!(file.contains("\"\\u00A0a\\u00A0\""), "{file}");
    assert!(file.is_ascii(), "the file is ASCII: {file}");
}
