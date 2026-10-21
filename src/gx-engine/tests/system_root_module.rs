//! The Knowledge Base root module (`Root Module`) is identified by its owner data, not
//! by its display name, and is the only Module allowed to carry a space in its name.
//! Objects it owns are emitted with no module segment.

#![forbid(unsafe_code)]

use gx_engine::catalog::{Catalog, KbObject, ObjectId, ObjectKind};
use gx_engine::rebuild::{RebuildOptions, rebuild_all};
use gx_engine::udm::{
    EntityTypeRecord, EntityVersionCompositionRecord, EntityVersionRecord,
    ModelEntityVersionRecord, SYSTEM_ROOT_MODULE_GUID, UdmClient, UdmReader, build_token_data_list,
    encode_blob,
};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;

/// Module 17 is the GeneXus 16 numbering (`KBTeste16`); the root is that Module's EntityId 1.
const MODULE_TYPE: i32 = 17;
const ROOT_ID: i32 = 1;

fn vocabulary() -> Vec<EntityTypeRecord> {
    let rec = |id: i32, name: &str, namespace: &str| EntityTypeRecord {
        entity_type_id: id,
        name: name.to_string(),
        namespace: namespace.to_string(),
    };
    vec![
        rec(17, "Module", "Objects"),
        rec(31, "Procedure", "Objects"),
        rec(81, "Procedure", ""),
    ]
}

fn client() -> Arc<UdmClient> {
    Arc::new(UdmClient::new_in_memory_with_entity_types(vocabulary()))
}

fn insert_module(
    client: &UdmClient,
    entity_id: i32,
    name: &str,
    parent: (i32, i32),
    guid: Option<&str>,
) {
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: MODULE_TYPE,
            entity_id,
            entity_version_id: 1,
            name: name.to_string(),
        })
        .expect("module row");
    client
        .insert_model_parent(1, MODULE_TYPE, entity_id, parent.0, parent.1)
        .expect("module parent");
    if let Some(guid) = guid {
        client
            .insert_entity_guid(MODULE_TYPE, entity_id, guid)
            .expect("module guid");
    }
}

/// The genuine root: parent-less, `EntityId` 1, the GeneXus root GUID.
fn insert_genuine_root(client: &UdmClient) {
    insert_module(
        client,
        ROOT_ID,
        "Root Module",
        (0, 0),
        Some(SYSTEM_ROOT_MODULE_GUID),
    );
}

fn insert_procedure_under(
    client: &UdmClient,
    name: &str,
    entity_id: i32,
    parent: (i32, i32),
    text: &str,
) {
    client
        .insert_model_entity_version(ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 31,
            entity_id,
            entity_version_id: 1,
            name: name.to_string(),
        })
        .expect("procedure row");
    client
        .insert_model_parent(1, 31, entity_id, parent.0, parent.1)
        .expect("procedure parent");
    let part_id = entity_id * 10 + 1;
    client
        .insert_entity_version(EntityVersionRecord {
            entity_type_id: 81,
            entity_id: part_id,
            entity_version_id: 1,
            data: encode_blob(&build_token_data_list(text), true).expect("encode"),
            properties: None,
        })
        .expect("source part");
    client
        .insert_composition(EntityVersionCompositionRecord {
            compound_entity_type_id: 31,
            compound_entity_id: entity_id,
            compound_entity_version_id: 1,
            component_entity_type_id: 81,
            component_entity_id: part_id,
            component_entity_version_id: 1,
        })
        .expect("composition");
}

fn walk(dir: &Path, files: &mut Vec<PathBuf>) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            walk(&path, files);
        } else {
            files.push(path);
        }
    }
}

// --- Catalog rule -----------------------------------------------------------

#[test]
fn flagged_root_module_is_accepted_under_its_exact_name_only() {
    let mut objects = BTreeMap::new();
    let mut root = KbObject::new(
        ObjectId::new("root-module").unwrap(),
        "Root Module",
        ObjectKind::Module,
    );
    root.system_root = true;
    objects.insert(root.id.clone(), root);
    let catalog = Catalog::from_objects_map(objects);
    let json = catalog
        .to_json()
        .expect("the flagged root is a valid catalog object");
    let reloaded = Catalog::from_json(&json).expect("the validated catalog reloads");
    let stored = reloaded
        .get(&ObjectId::new("root-module").unwrap())
        .expect("root");
    assert_eq!(stored.name, "Root Module", "names keep their stored case");
    assert!(stored.system_root, "the flag survives the round trip");
}

#[test]
fn unflagged_module_with_a_space_is_refused() {
    let mut objects = BTreeMap::new();
    let module = KbObject::new(
        ObjectId::new("obj-50").unwrap(),
        "Root Module",
        ObjectKind::Module,
    );
    objects.insert(module.id.clone(), module);
    let error = Catalog::from_objects_map(objects).to_json().unwrap_err();
    assert!(error.contains("object name must use ASCII"), "{error}");
}

#[test]
fn flagged_object_other_than_the_root_module_is_refused() {
    for (kind, name) in [
        (ObjectKind::Procedure, "Root Module"),
        (ObjectKind::Module, "Other Module"),
    ] {
        let mut objects = BTreeMap::new();
        let mut object = KbObject::new(ObjectId::new("root-module").unwrap(), name, kind);
        object.system_root = true;
        objects.insert(object.id.clone(), object);
        let error = Catalog::from_objects_map(objects).to_json().unwrap_err();
        assert!(
            error.contains("flagged as the system root module"),
            "{error}"
        );
    }
}

#[test]
fn a_catalog_with_two_system_roots_is_refused() {
    let mut objects = BTreeMap::new();
    for id in ["root-module", "root-module-2"] {
        let mut root = KbObject::new(
            ObjectId::new(id).unwrap(),
            "Root Module",
            ObjectKind::Module,
        );
        root.system_root = true;
        objects.insert(root.id.clone(), root);
    }
    let error = Catalog::from_objects_map(objects).to_json().unwrap_err();
    assert!(error.contains("more than one system root"), "{error}");
}

#[test]
fn the_flag_is_not_serialized_when_false_so_existing_catalogs_are_unchanged() {
    let mut objects = BTreeMap::new();
    let object = KbObject::new(
        ObjectId::new("pplain").unwrap(),
        "PPlain",
        ObjectKind::Procedure,
    );
    objects.insert(object.id.clone(), object);
    let json = Catalog::from_objects_map(objects).to_json().unwrap();
    assert!(!json.contains("system_root"), "{json}");
    assert!(Catalog::from_json(&json).is_ok());
}

// --- Identity ----------------------------------------------------------------

#[test]
fn genuine_root_is_read_as_the_flagged_system_root() {
    let client = client();
    insert_genuine_root(&client);
    assert_eq!(
        client.system_root_module(1, MODULE_TYPE).expect("identity"),
        Some((MODULE_TYPE, ROOT_ID))
    );
    let catalog = UdmReader::from_client(client.as_ref().clone())
        .read_catalog()
        .expect("catalog with the root");
    let root = catalog
        .get(&ObjectId::new("root-module").unwrap())
        .expect("root object");
    assert_eq!(root.kind, ObjectKind::Module);
    assert!(root.system_root);
}

/// A user Module named `Root Module` under a parent is not the root, so the ASCII rule
/// refuses it. Name alone never identifies the root.
#[test]
fn user_module_named_root_module_under_a_parent_is_refused() {
    let client = client();
    insert_genuine_root(&client);
    insert_module(&client, 60, "Root Module", (MODULE_TYPE, ROOT_ID), None);

    assert_eq!(
        client.system_root_module(1, MODULE_TYPE).expect("identity"),
        Some((MODULE_TYPE, ROOT_ID)),
        "the genuine root is still found"
    );
    let error = UdmReader::from_client(client.as_ref().clone())
        .read_catalog()
        .unwrap_err();
    assert!(error.contains("object name must use ASCII"), "{error}");
}

/// `EntityId` 1 with a parent is not the root: the root has no parent.
#[test]
fn entity_id_one_with_a_parent_is_not_the_root() {
    let client = client();
    insert_module(
        &client,
        ROOT_ID,
        "Root Module",
        (MODULE_TYPE, 2),
        Some(SYSTEM_ROOT_MODULE_GUID),
    );
    assert_eq!(
        client.system_root_module(1, MODULE_TYPE).expect("identity"),
        None
    );
    let error = UdmReader::from_client(client.as_ref().clone())
        .read_catalog()
        .unwrap_err();
    assert!(error.contains("object name must use ASCII"), "{error}");
}

/// A parent-less `EntityId` 1 with another GUID is not the GeneXus root.
#[test]
fn parent_less_module_with_another_guid_is_not_the_root() {
    let client = client();
    insert_module(
        &client,
        ROOT_ID,
        "Root Module",
        (0, 0),
        Some("00000000-0000-0000-0000-000000000001"),
    );
    assert_eq!(
        client.system_root_module(1, MODULE_TYPE).expect("identity"),
        None
    );
}

/// The root is found only in its own model.
#[test]
fn root_identity_is_per_model() {
    let client = client();
    insert_genuine_root(&client);
    assert_eq!(
        client.system_root_module(2, MODULE_TYPE).expect("identity"),
        None
    );
}

// --- Emission ----------------------------------------------------------------

/// An object owned by the root carries no module segment in the emitted output: the
/// root itself is not emitted, and the procedure's namespace is the default one.
#[test]
fn object_owned_by_the_root_is_emitted_without_a_module_segment() {
    let client = client();
    insert_genuine_root(&client);
    insert_procedure_under(
        &client,
        "PTopOwned",
        100,
        (MODULE_TYPE, ROOT_ID),
        "msg('root owned', status)",
    );

    let catalog = UdmReader::from_client(client.as_ref().clone())
        .read_catalog()
        .expect("catalog");

    let nonce = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or_default();
    let out = std::env::temp_dir().join(format!("gx_system_root_{}_{nonce}", std::process::id()));
    let options = RebuildOptions::new("net8.0").with_skip_compile(true);
    let report = rebuild_all(&catalog, &out, &options).expect("rebuild");
    assert!(
        report.errors.is_empty(),
        "rebuild errors: {:?}",
        report.errors
    );

    let mut files = Vec::new();
    walk(&out, &mut files);
    let sources: Vec<&PathBuf> = files
        .iter()
        .filter(|path| path.extension().is_some_and(|ext| ext == "cs"))
        .collect();
    assert!(
        !sources.is_empty(),
        "the root-owned procedure must be emitted; files: {files:?}"
    );

    for path in &files {
        let relative = path
            .strip_prefix(&out)
            .unwrap_or(path)
            .display()
            .to_string();
        assert!(
            !relative.to_ascii_lowercase().contains("root"),
            "no output path may carry the root module: {relative}"
        );
    }
    for path in sources {
        let text = std::fs::read_to_string(path).expect("read emitted source");
        for line in text
            .lines()
            .filter(|line| line.trim_start().starts_with("namespace "))
        {
            assert!(
                line.trim_start().starts_with("namespace GeneXus.Programs"),
                "{}: unexpected namespace line: {line}",
                path.display()
            );
            assert!(
                !line.to_ascii_lowercase().contains("root"),
                "{}: a root segment leaked into {line}",
                path.display()
            );
        }
    }

    let _ = std::fs::remove_dir_all(&out);
}

// --- Live identity (opt-in) ------------------------------------------------

/// Skipped when the KB is not on this machine. When present, the genuine root must be
/// found by its identity in the real GeneXus 16 and GeneXus 18 Knowledge Bases.
#[test]
fn live_knowledge_bases_have_the_genuine_root_identity() {
    for kb in ["C:\\KBs\\KBTeste16", "C:\\KBs\\KBTeste"] {
        if !Path::new(kb).join("knowledgebase.connection").exists() {
            continue;
        }
        let client = UdmClient::connect_kb(kb).expect("connect KB");
        let resolver = client.entity_type_resolver().expect("vocabulary");
        let module_type = resolver
            .object_type_id(ObjectKind::Module)
            .expect("Module type");
        assert_eq!(
            client.system_root_module(1, module_type).expect("identity"),
            Some((module_type, ROOT_ID)),
            "{kb}"
        );
    }
}
