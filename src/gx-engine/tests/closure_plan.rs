//! The single-object closure planner refuses by name and drops nothing (see `plan_single_object_closure`).
//! The catalogs here are synthetic: the planner's rules are checked without a KB.

use gx_engine::catalog::{
    CLOSURE_NAMESPACE_MODULE, Catalog, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind,
    plan_single_object_closure,
};
use gx_engine::udm::reader::SYSTEM_ROOT_OBJECT_ID;
use std::collections::BTreeMap;

fn object(id: &str, kind: ObjectKind) -> KbObject {
    KbObject::new(ObjectId::new(id).unwrap(), id, kind)
}

fn edge(from: &mut KbObject, kind: ReferenceKind, target: &str) {
    from.references.push(Reference {
        kind,
        target: ObjectId::new(target).unwrap(),
    });
}

fn catalog(objects: Vec<KbObject>) -> Catalog {
    Catalog::from_objects_map(
        objects
            .into_iter()
            .map(|object| (object.id.clone(), object))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn ids(values: &[&str]) -> Vec<ObjectId> {
    let mut ids: Vec<ObjectId> = values
        .iter()
        .map(|value| ObjectId::new(*value).unwrap())
        .collect();
    ids.sort();
    ids
}

fn entry() -> ObjectId {
    ObjectId::new("p").unwrap()
}

#[test]
fn a_leaf_procedure_is_its_own_closure() {
    let catalog = catalog(vec![object("p", ObjectKind::Procedure)]);

    let plan = plan_single_object_closure(&catalog, &entry()).expect("a leaf is planned");

    assert_eq!(plan.members, ids(&["p"]));
    assert!(plan.namespace_modules.is_empty());
}

#[test]
fn the_closure_follows_calls_and_uses_to_every_reached_object() {
    let mut p = object("p", ObjectKind::Procedure);
    edge(&mut p, ReferenceKind::Calls, "q");
    let mut q = object("q", ObjectKind::Procedure);
    edge(&mut q, ReferenceKind::Uses, "s");
    let s = object("s", ObjectKind::StructuredDataType);
    let catalog = catalog(vec![p, q, s]);

    let plan = plan_single_object_closure(&catalog, &entry()).expect("the chain is planned");

    assert_eq!(plan.members, ids(&["p", "q", "s"]));
}

#[test]
fn a_cycle_terminates_with_each_member_once() {
    let mut p = object("p", ObjectKind::Procedure);
    edge(&mut p, ReferenceKind::Calls, "q");
    let mut q = object("q", ObjectKind::Procedure);
    edge(&mut q, ReferenceKind::Calls, "p");
    let catalog = catalog(vec![p, q]);

    let plan = plan_single_object_closure(&catalog, &entry()).expect("a cycle is planned");

    assert_eq!(plan.members, ids(&["p", "q"]));
}

#[test]
fn the_default_module_is_namespace_membership_and_is_not_compiled() {
    assert_eq!(CLOSURE_NAMESPACE_MODULE, "Default");
    let mut p = object("p", ObjectKind::Procedure);
    edge(&mut p, ReferenceKind::Uses, "default");
    let default = KbObject::new(
        ObjectId::new("default").unwrap(),
        "Default",
        ObjectKind::Module,
    );
    let catalog = catalog(vec![p, default]);

    let plan =
        plan_single_object_closure(&catalog, &entry()).expect("the default module is planned");

    assert_eq!(plan.members, ids(&["p"]));
    assert_eq!(plan.namespace_modules, ids(&["default"]));
}

#[test]
fn another_module_is_refused_by_name_with_its_path() {
    let mut p = object("p", ObjectKind::Procedure);
    edge(&mut p, ReferenceKind::Calls, "sales");
    let sales = KbObject::new(ObjectId::new("sales").unwrap(), "Sales", ObjectKind::Module);
    let catalog = catalog(vec![p, sales]);

    let error = plan_single_object_closure(&catalog, &entry())
        .expect_err("a module other than the default namespace is refused");

    assert!(error.contains("module 'Sales'"), "{error}");
    assert!(error.contains("namespace not evidenced"), "{error}");
    assert!(
        error.starts_with("p -> module"),
        "the path is named: {error}"
    );
}

#[test]
fn containment_is_refused_by_name() {
    let mut p = object("p", ObjectKind::Procedure);
    edge(&mut p, ReferenceKind::Contains, "q");
    let q = object("q", ObjectKind::Procedure);
    let catalog = catalog(vec![p, q]);

    let error = plan_single_object_closure(&catalog, &entry())
        .expect_err("a containment reference is refused");

    assert!(error.contains("containment not evidenced"), "{error}");
}

#[test]
fn a_web_ui_member_is_refused_by_name_with_its_path() {
    let mut p = object("p", ObjectKind::Procedure);
    edge(&mut p, ReferenceKind::Calls, "t");
    let t = object("t", ObjectKind::Transaction);
    let catalog = catalog(vec![p, t]);

    let error =
        plan_single_object_closure(&catalog, &entry()).expect_err("a web UI member is refused");

    assert!(
        error.contains("Transaction 't' (web UI, not generated)"),
        "{error}"
    );
}

#[test]
fn a_not_generated_member_is_refused_by_name() {
    let mut p = object("p", ObjectKind::Procedure);
    edge(&mut p, ReferenceKind::Calls, "q");
    let mut q = object("q", ObjectKind::Procedure);
    q.not_generated = Some("no generator, not generated".into());
    let catalog = catalog(vec![p, q]);

    let error = plan_single_object_closure(&catalog, &entry())
        .expect_err("a not-generated member is refused");

    assert!(error.contains("no generator, not generated"), "{error}");
}

#[test]
fn a_reader_refusal_on_a_member_is_refused_by_name() {
    let mut p = object("p", ObjectKind::Procedure);
    edge(&mut p, ReferenceKind::Calls, "q");
    let mut q = object("q", ObjectKind::Procedure);
    q.refusal = Some("reference to object 9 of type 38 has no served row".into());
    let catalog = catalog(vec![p, q]);

    let error = plan_single_object_closure(&catalog, &entry())
        .expect_err("a member the reader refused is refused");

    assert!(error.contains("has no served row"), "{error}");
}

#[test]
fn a_target_missing_from_the_closure_is_refused_not_dropped() {
    let mut p = object("p", ObjectKind::Procedure);
    edge(&mut p, ReferenceKind::Calls, "ghost");
    let catalog = catalog(vec![p]);

    let error =
        plan_single_object_closure(&catalog, &entry()).expect_err("a missing target is refused");

    assert!(
        error.contains("'ghost': the target is not in the closure"),
        "{error}"
    );
}

#[test]
fn the_root_module_is_exempt_even_when_absent() {
    let mut p = object("p", ObjectKind::Procedure);
    edge(&mut p, ReferenceKind::Calls, SYSTEM_ROOT_OBJECT_ID);
    let catalog = catalog(vec![p]);

    let plan = plan_single_object_closure(&catalog, &entry()).expect("the root is exempt");

    assert_eq!(plan.members, ids(&["p"]));
    assert!(plan.namespace_modules.is_empty());
}

#[test]
fn a_structured_data_type_can_start_a_closure() {
    let s = object("s", ObjectKind::StructuredDataType);
    let catalog = catalog(vec![s]);

    let plan = plan_single_object_closure(&catalog, &ObjectId::new("s").unwrap())
        .expect("a structured data type is a closure entry");

    assert_eq!(plan.members, ids(&["s"]));
}

#[test]
fn other_kinds_cannot_start_a_closure() {
    let d = object("d", ObjectKind::Domain);
    let catalog = catalog(vec![d]);

    let error = plan_single_object_closure(&catalog, &ObjectId::new("d").unwrap())
        .expect_err("a domain is not a closure entry");

    assert!(error.contains("is a Domain"), "{error}");
}

#[test]
fn an_entry_that_is_not_in_the_catalog_is_refused() {
    let catalog = catalog(vec![]);

    let error =
        plan_single_object_closure(&catalog, &entry()).expect_err("an absent entry is refused");

    assert!(error.contains("is not in the catalog"), "{error}");
}
