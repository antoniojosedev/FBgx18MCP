//! Which compile units a rebuild targets, by reachability from the objects a Knowledge Base generates.
//!
//! The roots are the objects that are generated on their own: every Procedure or DataProvider whose `GenerateObject` is not
//! False (a refused one included, so that the dependencies of a refused Procedure stay visible and refused
//! with it). Every object a root reaches through a reference is targeted, transitively.
//!
//! A structured data type is never a root: the SDK generates one only when a generated object reaches it.
//! On KBTeste the SDK's full specification and generation reports `LiveSdtMemberProc00025500` and
//! `LiveSdtMemberProc44066400` unreachable (spc0217), and the SDK output has no class for the SDTs only they
//! use, nor for an SDT nothing uses (`SDTLiveTest`, `LiveSdtMember82347100`); every SDT class it does hold is
//! used by a generated object. A GeneXus library structured data type that no root reaches is not targeted
//! either: the GeneXus runtime provides it, so it is neither emitted nor refused, even when this engine
//! cannot decode it. Module membership is an
//! ordinary reference from an object to its module, and a module has no references to its members, so
//! membership alone never makes an object targeted.

use std::collections::BTreeSet;

use crate::catalog::{Catalog, KbObject, ObjectId, ObjectKind};

/// Why a Procedure stored as not main is not built when no generated object reaches it.
pub const UNREACHABLE_REASON: &str = "not main and no generated object reaches it (SDK spc0217, Object is unreachable), so the SDK does not generate it";

/// Why a GeneXus library structured data type is not built when no generated object reaches it.
pub const LIBRARY_NOT_TARGETED_REASON: &str = "GeneXus library structured data type that no generated object reaches: the GeneXus runtime provides it, so it is not generated";

/// Why a structured data type outside the GeneXus library is not built when no generated object reaches it.
pub const UNREACHED_SDT_REASON: &str =
    "no generated object reaches this structured data type, so the SDK does not generate it";

/// Why a GeneXus library structured data type is not built even when a generated object reaches it.
pub const RUNTIME_PROVIDED_REASON: &str = "GeneXus library structured data type the GeneXus runtime assemblies already define, so the SDK does not generate it";

/// Whether the object is generated on its own, and so is a root of reachability.
pub fn is_root(object: &KbObject) -> bool {
    match object.kind {
        // A Procedure stored as not main is generated only when reached (spc0217).
        ObjectKind::Procedure => object.not_generated.is_none() && object.main != Some(false),
        ObjectKind::DataProvider => object.not_generated.is_none(),
        // Every Transaction and WebPanel is generated: a web object is reached by its URL.
        ObjectKind::Transaction | ObjectKind::WebPanel => true,
        _ => false,
    }
}

/// The objects a rebuild targets: the roots and every object a root reaches by reference, sorted by id.
/// A reference to an identity the catalog does not hold leads nowhere, and is not in the result.
pub fn targeted_objects(catalog: &Catalog) -> BTreeSet<ObjectId> {
    let mut targeted = BTreeSet::new();
    let mut pending: Vec<ObjectId> = catalog
        .objects()
        .filter(|object| is_root(object))
        .map(|object| object.id.clone())
        .collect();
    while let Some(id) = pending.pop() {
        let Some(object) = catalog.get(&id) else {
            continue;
        };
        if !targeted.insert(id) {
            continue;
        }
        pending.extend(
            object
                .references
                .iter()
                .map(|reference| reference.target.clone()),
        );
    }
    targeted
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::catalog::{Reference, ReferenceKind};
    use std::collections::BTreeMap;

    fn object(id: &str, kind: ObjectKind) -> KbObject {
        KbObject::new(ObjectId::new(id).unwrap(), id, kind)
    }

    fn calls(mut object: KbObject, targets: &[&str]) -> KbObject {
        object.references = targets
            .iter()
            .map(|target| Reference {
                kind: ReferenceKind::Uses,
                target: ObjectId::new(*target).unwrap(),
            })
            .collect();
        object
    }

    fn library_sdt(id: &str) -> KbObject {
        let mut sdt = object(id, ObjectKind::StructuredDataType);
        sdt.library = true;
        sdt
    }

    fn catalog(objects: Vec<KbObject>) -> Catalog {
        let map: BTreeMap<ObjectId, KbObject> = objects
            .into_iter()
            .map(|object| (object.id.clone(), object))
            .collect();
        Catalog::from_objects_map(map)
    }

    fn targets(catalog: &Catalog) -> Vec<String> {
        targeted_objects(catalog)
            .iter()
            .map(|id| id.as_str().to_string())
            .collect()
    }

    #[test]
    fn a_library_output_sdt_reached_by_a_data_provider_is_targeted() {
        let provider = calls(
            object("provider", ObjectKind::DataProvider),
            &["library-output"],
        );
        let catalog = catalog(vec![provider, library_sdt("library-output")]);
        assert_eq!(targets(&catalog), ["library-output", "provider"]);
    }

    #[test]
    fn a_library_sdt_reached_by_a_procedure_through_an_sdt_chain_is_targeted() {
        let procedure = calls(object("proc", ObjectKind::Procedure), &["user-sdt"]);
        let user_sdt = calls(
            object("user-sdt", ObjectKind::StructuredDataType),
            &["library-sdt"],
        );
        let catalog = catalog(vec![procedure, user_sdt, library_sdt("library-sdt")]);
        assert_eq!(targets(&catalog), ["library-sdt", "proc", "user-sdt"]);
    }

    #[test]
    fn an_unreached_library_sdt_is_not_targeted() {
        let procedure = object("proc", ObjectKind::Procedure);
        let catalog = catalog(vec![procedure, library_sdt("library-sdt")]);
        assert_eq!(targets(&catalog), ["proc"]);
    }

    #[test]
    fn a_library_sdt_only_a_generate_object_false_procedure_reaches_is_not_targeted() {
        let mut skipped = calls(
            object("not-generated", ObjectKind::Procedure),
            &["library-sdt"],
        );
        skipped.not_generated = Some("GenerateObject is False".into());
        let catalog = catalog(vec![skipped, library_sdt("library-sdt")]);
        assert!(targets(&catalog).is_empty());
    }

    #[test]
    fn a_user_sdt_nothing_reaches_is_not_targeted() {
        let user_sdt = object("user-sdt", ObjectKind::StructuredDataType);
        let catalog = catalog(vec![user_sdt]);
        assert!(targets(&catalog).is_empty());
    }

    #[test]
    fn a_user_sdt_only_an_unreachable_procedure_uses_is_not_targeted() {
        let mut unreachable = calls(object("proc", ObjectKind::Procedure), &["user-sdt"]);
        unreachable.main = Some(false);
        let user_sdt = object("user-sdt", ObjectKind::StructuredDataType);
        let catalog = catalog(vec![unreachable, user_sdt]);
        assert!(targets(&catalog).is_empty());
    }

    #[test]
    fn a_refused_procedure_stays_a_root_so_its_dependencies_stay_targeted() {
        let mut refused = calls(object("refused", ObjectKind::Procedure), &["library-sdt"]);
        refused.refusal = Some("cannot decode a variable".into());
        let catalog = catalog(vec![refused, library_sdt("library-sdt")]);
        assert_eq!(targets(&catalog), ["library-sdt", "refused"]);
    }

    #[test]
    fn a_cycle_terminates_and_a_cycle_no_root_reaches_stays_untargeted() {
        let reached = calls(object("proc", ObjectKind::Procedure), &["a"]);
        let a = calls(library_sdt("a"), &["b"]);
        let b = calls(library_sdt("b"), &["a"]);
        let island_1 = calls(library_sdt("c"), &["d"]);
        let island_2 = calls(library_sdt("d"), &["c"]);
        let catalog = catalog(vec![reached, a, b, island_1, island_2]);
        assert_eq!(targets(&catalog), ["a", "b", "proc"]);
    }

    #[test]
    fn a_reference_to_an_identity_outside_the_catalog_leads_nowhere() {
        // An External Object is not served by the reader, so a reference to it is dropped there; a catalog
        // that still holds one must not error or follow it. The library SDT beside it is reached by no root,
        // so an External Object edge cannot carry reach to it.
        let procedure = calls(
            object("proc", ObjectKind::Procedure),
            &["external-object-not-served"],
        );
        let catalog = catalog(vec![procedure, library_sdt("library-sdt")]);
        assert_eq!(targets(&catalog), ["proc"]);
    }

    #[test]
    fn module_membership_does_not_make_a_library_sdt_targeted() {
        // The library SDT refers to its module; the procedure refers to the same module. The module has no
        // reference back to its members, so the SDT stays untargeted.
        let module = object("genexus", ObjectKind::Module);
        let sdt = calls(library_sdt("library-sdt"), &["genexus"]);
        let procedure = calls(object("proc", ObjectKind::Procedure), &["genexus"]);
        let catalog = catalog(vec![module, sdt, procedure]);
        assert_eq!(targets(&catalog), ["genexus", "proc"]);
    }

    #[test]
    fn a_web_object_is_a_root_and_targets_what_it_reaches() {
        // A WebPanel is generated (reached by its URL), so what it uses is generated too.
        let web = calls(object("panel", ObjectKind::WebPanel), &["library-sdt"]);
        let catalog = catalog(vec![web, library_sdt("library-sdt")]);
        assert_eq!(targets(&catalog), ["library-sdt", "panel"]);
    }

    #[test]
    fn a_procedure_stored_as_not_main_is_targeted_only_when_reached() {
        let mut called = object("called", ObjectKind::Procedure);
        called.main = Some(false);
        let mut orphan = object("orphan", ObjectKind::Procedure);
        orphan.main = Some(false);
        let mut main = calls(object("main", ObjectKind::Procedure), &["called"]);
        main.main = Some(true);
        let unknown = object("unknown", ObjectKind::Procedure);
        let catalog = catalog(vec![called, orphan, main, unknown]);
        assert_eq!(targets(&catalog), ["called", "main", "unknown"]);
    }
}
