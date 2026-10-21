//! Two objects of one model may share a name: GeneXus allows two Modules named alike under
//! different parents (`KBTeste16` and `KBTeste` both hold two `Notifications` Modules).
//!
//! - Every object gets its own catalog id, so the catalog loads instead of refusing the KB.
//! - A reference names the object it points at, not the name it shares with another object.
//! - Reading by a name that two objects share is refused, never resolved to one of them.

#![forbid(unsafe_code)]

use gx_engine::catalog::{ObjectId, ObjectKind};
use gx_engine::udm::{
    EntityTypeRecord, ModelCrossReferenceRecord, ModelEntityVersionRecord, UdmClient, UdmReader,
};
use std::sync::Arc;

const MODULE_TYPE: i32 = 17;
const PROCEDURE_TYPE: i32 = 31;

fn vocabulary() -> Vec<EntityTypeRecord> {
    let rec = |id: i32, name: &str, namespace: &str| EntityTypeRecord {
        entity_type_id: id,
        name: name.to_string(),
        namespace: namespace.to_string(),
    };
    vec![
        rec(MODULE_TYPE, "Module", "Objects"),
        rec(PROCEDURE_TYPE, "Procedure", "Objects"),
    ]
}

fn client() -> Arc<UdmClient> {
    Arc::new(UdmClient::new_in_memory_with_entity_types(vocabulary()))
}

fn insert_object(client: &UdmClient, entity_type_id: i32, entity_id: i32, name: &str) {
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id,
            entity_id,
            entity_version_id: 1,
            name: name.to_string(),
        })
        .expect("object row");
}

/// Two Modules named `Notifications` (entity ids 5 and 14), and a Procedure that references the
/// second one.
fn two_notifications_modules() -> Arc<UdmClient> {
    let client = client();
    insert_object(&client, MODULE_TYPE, 5, "Notifications");
    insert_object(&client, MODULE_TYPE, 14, "Notifications");
    insert_object(&client, PROCEDURE_TYPE, 7, "SendAlert");
    client
        .insert_cross_reference(ModelCrossReferenceRecord {
            model_id: 1,
            from_entity_type_id: PROCEDURE_TYPE,
            from_entity_id: 7,
            to_entity_type_id: MODULE_TYPE,
            to_entity_id: 14,
            reference_type: 2,
        })
        .expect("cross reference");
    client
}

fn reader(client: &Arc<UdmClient>) -> UdmReader {
    UdmReader::from_client(client.as_ref().clone())
}

#[test]
fn two_objects_with_one_name_get_distinct_catalog_ids() {
    let client = two_notifications_modules();

    let catalog = reader(&client)
        .read_catalog()
        .expect("a shared name must not refuse the whole KB");

    let mut ids: Vec<String> = catalog
        .objects()
        .filter(|object| object.kind == ObjectKind::Module)
        .map(|object| object.id.as_str().to_string())
        .collect();
    ids.sort();
    assert_eq!(ids, vec!["notifications-17-14", "notifications-17-5"]);
}

#[test]
fn a_reference_points_at_the_object_it_names_not_its_namesake() {
    let client = two_notifications_modules();

    let catalog = reader(&client).read_catalog().expect("catalog loads");

    let send_alert = catalog
        .get(&ObjectId::new("sendalert").unwrap())
        .expect("SendAlert is an object");
    let targets: Vec<&str> = send_alert
        .references
        .iter()
        .map(|reference| reference.target.as_str())
        .collect();
    assert_eq!(targets, vec!["notifications-17-14"]);
}

#[test]
fn reading_by_a_name_two_objects_share_is_refused() {
    let client = two_notifications_modules();

    let error = reader(&client)
        .read_object_by_name("Notifications")
        .expect_err("an ambiguous name must not resolve to one of its objects");

    assert!(error.contains("ambiguous"), "{error}");
    let load_error = reader(&client)
        .load_object_by_name("Notifications", None)
        .expect_err("the same refusal on the load path");
    assert!(load_error.contains("ambiguous"), "{load_error}");
}

#[test]
fn a_unique_name_still_resolves_by_name() {
    let client = two_notifications_modules();

    let found = reader(&client)
        .read_object_by_name("SendAlert")
        .expect("a unique name reads")
        .expect("SendAlert exists");

    assert_eq!(found.name, "SendAlert");
    assert_eq!(found.kind, ObjectKind::Procedure);
}

#[test]
fn a_type_qualifier_filters_the_name_by_object_kind() {
    let client = two_notifications_modules();
    let reader = reader(&client);

    let found = reader
        .read_object_by_name("Procedure:SendAlert")
        .expect("a qualified unique name reads")
        .expect("SendAlert is a Procedure");
    assert_eq!(found.kind, ObjectKind::Procedure);

    let wrong_kind = reader
        .read_object_by_name("Module:SendAlert")
        .expect("a qualified miss is not an error");
    assert!(wrong_kind.is_none(), "SendAlert is not a Module");

    let still_ambiguous = reader
        .read_object_by_name("Module:Notifications")
        .expect_err("two objects of the same kind stay ambiguous");
    assert!(still_ambiguous.contains("ambiguous"), "{still_ambiguous}");

    let unknown = reader
        .read_object_by_name("Bogus:SendAlert")
        .expect_err("an unknown type is refused");
    assert!(unknown.contains("unknown object type"), "{unknown}");
}

// --- Catalog name rule ------------------------------------------------------

#[test]
fn two_procedures_with_one_name_are_still_refused_by_the_catalog() {
    let mut catalog = gx_engine::catalog::Catalog::empty();
    let first = gx_engine::catalog::KbObject::new(
        ObjectId::new("first").unwrap(),
        "Same",
        ObjectKind::Procedure,
    );
    let second = gx_engine::catalog::KbObject::new(
        ObjectId::new("second").unwrap(),
        "Same",
        ObjectKind::Procedure,
    );

    let error = catalog
        .apply(
            0,
            &[
                gx_engine::catalog::Edit::Upsert(first),
                gx_engine::catalog::Edit::Upsert(second),
            ],
        )
        .expect_err("artifact-producing objects keep unique names");

    assert!(error.contains("duplicate object name"), "{error}");
}

#[test]
fn two_modules_with_one_name_are_accepted_by_the_catalog() {
    let mut catalog = gx_engine::catalog::Catalog::empty();
    let first = gx_engine::catalog::KbObject::new(
        ObjectId::new("first").unwrap(),
        "Same",
        ObjectKind::Module,
    );
    let second = gx_engine::catalog::KbObject::new(
        ObjectId::new("second").unwrap(),
        "Same",
        ObjectKind::Module,
    );

    catalog
        .apply(
            0,
            &[
                gx_engine::catalog::Edit::Upsert(first),
                gx_engine::catalog::Edit::Upsert(second),
            ],
        )
        .expect("a Module emits no artifact, so its name need not be unique");
    assert_eq!(catalog.objects().count(), 2);
}

#[test]
fn a_transaction_and_its_physical_table_may_share_a_name() {
    let mut catalog = gx_engine::catalog::Catalog::empty();
    let transaction = gx_engine::catalog::KbObject::new(
        ObjectId::new("plan070trn").unwrap(),
        "Plan070Trn",
        ObjectKind::Transaction,
    );
    let table = gx_engine::catalog::KbObject::new(
        ObjectId::new("plan070trn_table").unwrap(),
        "Plan070Trn",
        ObjectKind::Table,
    );

    catalog
        .apply(
            0,
            &[
                gx_engine::catalog::Edit::Upsert(transaction),
                gx_engine::catalog::Edit::Upsert(table),
            ],
        )
        .expect("a Table takes its Transaction's name by GeneXus design");
    assert_eq!(catalog.objects().count(), 2);
}

#[test]
fn a_procedure_and_a_transaction_with_one_name_are_still_refused() {
    let mut catalog = gx_engine::catalog::Catalog::empty();
    let procedure = gx_engine::catalog::KbObject::new(
        ObjectId::new("p").unwrap(),
        "Same",
        ObjectKind::Procedure,
    );
    let transaction = gx_engine::catalog::KbObject::new(
        ObjectId::new("t").unwrap(),
        "Same",
        ObjectKind::Transaction,
    );

    let error = catalog
        .apply(
            0,
            &[
                gx_engine::catalog::Edit::Upsert(procedure),
                gx_engine::catalog::Edit::Upsert(transaction),
            ],
        )
        .expect_err("two classes of one name would collide");
    assert!(error.contains("duplicate object name"), "{error}");
}
