//! `--object NAME` rebuilds one object alone. Its references must resolve inside that one-object catalog
//! unless they target the root module, which emits nothing. These tests use SYNTHETIC objects built in
//! the test; the rule under test is `catalog::single_object_gap`.

use gx_engine::catalog::{
    KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, single_object_gap,
};
use gx_engine::udm::reader::SYSTEM_ROOT_OBJECT_ID;

fn id(value: &str) -> ObjectId {
    ObjectId::new(value).expect("a valid synthetic id")
}

/// SYNTHETIC: a procedure that holds the given references.
fn procedure_referring_to(targets: &[&str]) -> KbObject {
    let mut object = KbObject::new(id("callerproc"), "CallerProc", ObjectKind::Procedure);
    object.references = targets
        .iter()
        .map(|target| Reference {
            kind: ReferenceKind::Calls,
            target: id(target),
        })
        .collect();
    object
}

#[test]
fn a_reference_to_the_root_module_is_not_a_gap() {
    // Every object refers to the root module; a Module emits nothing, so it never blocks a single build.
    let object = procedure_referring_to(&[SYSTEM_ROOT_OBJECT_ID]);
    assert_eq!(single_object_gap(&object), None);
}

#[test]
fn a_reference_to_the_object_itself_is_not_a_gap() {
    let object = procedure_referring_to(&["callerproc"]);
    assert_eq!(single_object_gap(&object), None);
}

#[test]
fn a_reference_to_another_object_is_a_gap_and_is_reported() {
    // A callee outside the one-object catalog would be emitted without its class: refuse it.
    let object = procedure_referring_to(&[SYSTEM_ROOT_OBJECT_ID, "calleeproc"]);
    assert_eq!(single_object_gap(&object), Some(id("calleeproc")));
}

#[test]
fn the_first_gap_is_the_one_reported_in_reference_order() {
    let object = procedure_referring_to(&["first", "second"]);
    assert_eq!(single_object_gap(&object), Some(id("first")));
}

#[test]
fn an_object_with_no_references_has_no_gap() {
    let object = procedure_referring_to(&[]);
    assert_eq!(single_object_gap(&object), None);
}
