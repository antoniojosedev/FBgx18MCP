//! Direct Knowledge Base Writer and Transactional Mutation Engine for GeneXus UDM storage.
//!
//! Provides `UdmWriter` implementing atomic object mutations (`create_object`, `save_object`,
//! `delete_object`), updating `EntityVersion`, `ModelEntityVersion`, `EntityVersionComposition`,
//! and `ModelCrossReference`, and managing concurrent multi-user / agent locks in `State`.

#![forbid(unsafe_code)]

use crate::catalog::{KbObject, ReferenceKind, SourcePart};
use crate::udm::client::{
    EntityVersionCompositionRecord, EntityVersionRecord, ModelCrossReferenceRecord,
    ModelEntityVersionRecord, StateLockRecord, UdmClient,
};
use crate::udm::decoder::{UdmPartKind, build_token_data_list, encode_blob};
use crate::udm::entity_types::EntityTypeResolver;
use crate::udm::reader::DEFAULT_DESIGN_MODEL_ID;
use std::sync::Arc;

/// The stored part rows of an object, in write order: the Knowledge Base's own
/// entity type id, the engine part kind, and the part. A part name the engine does
/// not serve, or a part type the Knowledge Base does not declare for this object
/// kind, is refused before anything is written. A row stored under an undeclared
/// entity type (the old code stored `0`) is corrupt data.
fn stored_parts<'a>(
    resolver: &EntityTypeResolver,
    object: &'a KbObject,
) -> Result<Vec<(i32, UdmPartKind, &'a SourcePart)>, String> {
    object
        .source_parts
        .iter()
        .map(|part| {
            let kind = match UdmPartKind::from_name(&part.name) {
                UdmPartKind::Unknown(_) => {
                    return Err(format!(
                        "part '{}' of '{}' is not a part this engine stores; refusing to write it under an undeclared entity type",
                        part.name, object.name
                    ));
                }
                kind => kind,
            };
            let type_id = resolver.part_type_id(object.kind, kind)?;
            Ok((type_id, kind, part))
        })
        .collect()
}

/// The `(entity type, entity id)` a reference points to. The object being written
/// (a self-reference) is resolved from `self_entity`, because a new object is not in
/// the `active` snapshot yet. Any other target that is not an object of the model is
/// refused: storing it under an invented id left a dangling row in the model.
fn reference_target(
    active: &[ModelEntityVersionRecord],
    self_entity: (i32, i32),
    object_name: &str,
    target: &str,
) -> Result<(i32, i32), String> {
    if target.eq_ignore_ascii_case(object_name) {
        return Ok(self_entity);
    }
    active
        .iter()
        .find(|rec| rec.name.eq_ignore_ascii_case(target))
        .map(|rec| (rec.entity_type_id, rec.entity_id))
        .ok_or_else(|| {
            format!(
                "'{object_name}' references '{target}', which is not an object of this model; refusing to store a dangling reference"
            )
        })
}

/// Identifies an object entity and active version in the UDM database.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct UdmObjectIdentity {
    pub entity_type_id: i32,
    pub entity_id: i32,
    pub version_id: i32,
    pub name: String,
}

/// Universal Data Model writer executing transactional mutations directly against UDM storage.
#[derive(Debug, Clone)]
pub struct UdmWriter {
    client: Arc<UdmClient>,
    model_id: i32,
}

impl UdmWriter {
    /// Creates a writer targeting the default design model (ModelId = 1).
    pub fn new(client: Arc<UdmClient>) -> Self {
        Self {
            client,
            model_id: DEFAULT_DESIGN_MODEL_ID,
        }
    }

    /// Creates a writer targeting a specific model ID.
    pub fn with_model_id(client: Arc<UdmClient>, model_id: i32) -> Self {
        Self { client, model_id }
    }

    /// Creates a writer from a value client.
    pub fn from_client(client: UdmClient) -> Self {
        Self::new(Arc::new(client))
    }

    /// Returns the target model ID.
    pub fn model_id(&self) -> i32 {
        self.model_id
    }

    /// Returns a reference to the underlying database client.
    pub fn client(&self) -> &UdmClient {
        &self.client
    }

    /// Acquires an exclusive lock in the `State` table.
    pub fn acquire_lock(
        &self,
        state_name: &str,
        user_name: &str,
        guid: &str,
        machine_name: &str,
    ) -> Result<bool, String> {
        self.client
            .acquire_lock(state_name, user_name, guid, machine_name)
    }

    /// Releases an exclusive lock in the `State` table.
    pub fn release_lock(&self, state_name: &str, guid: &str) -> Result<bool, String> {
        self.client.release_lock(state_name, guid)
    }

    /// Checks if a state name is currently locked.
    pub fn is_locked(&self, state_name: &str) -> Result<bool, String> {
        self.client.get_lock(state_name).map(|opt| opt.is_some())
    }

    /// Retrieves lock details for a state name, if held.
    pub fn get_lock(&self, state_name: &str) -> Result<Option<StateLockRecord>, String> {
        self.client.get_lock(state_name)
    }

    /// Creates a new object in the UDM database. Fails if an object with the same name exists.
    pub fn create_object(&self, object: &KbObject) -> Result<UdmObjectIdentity, String> {
        let resolver = self.client.entity_type_resolver()?;
        let entity_type_id = resolver.object_type_id(object.kind)?;
        let parts = stored_parts(&resolver, object)?;
        self.client.begin_transaction()?;

        let result = (|| {
            let active = self.client.get_active_objects(self.model_id)?;
            if active
                .iter()
                .any(|r| r.name.eq_ignore_ascii_case(&object.name))
            {
                return Err(format!(
                    "Object '{}' already exists in Model {}",
                    object.name, self.model_id
                ));
            }

            let entity_id = self.client.allocate_entity_id()?;
            let version_id = 1;

            // 1. Insert ModelEntityVersion
            self.client
                .insert_model_entity_version(ModelEntityVersionRecord {
                    model_id: self.model_id,
                    entity_type_id,
                    entity_id,
                    entity_version_id: version_id,
                    name: object.name.clone(),
                })?;

            // 2. Insert component parts
            for &(component_type_id, part_kind, part) in &parts {
                let component_id = self.client.allocate_entity_id()?;

                let payload = if part_kind.is_tokenized_source() {
                    build_token_data_list(&part.source)
                } else {
                    part.source.clone()
                };

                let blob = encode_blob(&payload, true)?;

                self.client.insert_entity_version(EntityVersionRecord {
                    entity_type_id: component_type_id,
                    entity_id: component_id,
                    entity_version_id: version_id,
                    data: blob,
                    properties: None,
                })?;

                self.client
                    .insert_composition(EntityVersionCompositionRecord {
                        compound_entity_type_id: entity_type_id,
                        compound_entity_id: entity_id,
                        compound_entity_version_id: version_id,
                        component_entity_type_id: component_type_id,
                        component_entity_id: component_id,
                        component_entity_version_id: version_id,
                    })?;
            }

            // 3. Insert cross-references
            for r in &object.references {
                let (to_type_id, to_id) = reference_target(
                    &active,
                    (entity_type_id, entity_id),
                    &object.name,
                    r.target.as_str(),
                )?;

                let ref_type = match r.kind {
                    ReferenceKind::Calls => 1,
                    ReferenceKind::Uses => 2,
                    ReferenceKind::BasedOn => 3,
                    ReferenceKind::Contains => 4,
                };

                self.client
                    .insert_cross_reference(ModelCrossReferenceRecord {
                        model_id: self.model_id,
                        from_entity_type_id: entity_type_id,
                        from_entity_id: entity_id,
                        to_entity_type_id: to_type_id,
                        to_entity_id: to_id,
                        reference_type: ref_type,
                    })?;
            }

            Ok(UdmObjectIdentity {
                entity_type_id,
                entity_id,
                version_id,
                name: object.name.clone(),
            })
        })();

        match result {
            Ok(identity) => {
                self.client.commit_transaction()?;
                Ok(identity)
            }
            Err(err) => {
                let _ = self.client.rollback_transaction();
                Err(err)
            }
        }
    }

    /// Saves an object to the UDM database, creating it if it does not exist,
    /// or incrementing its version and replacing its component parts if it exists.
    pub fn save_object(&self, object: &KbObject) -> Result<UdmObjectIdentity, String> {
        let active = self.client.get_active_objects(self.model_id)?;
        let existing = active
            .iter()
            .find(|r| r.name.eq_ignore_ascii_case(&object.name))
            .cloned();

        let Some(existing_record) = existing else {
            return self.create_object(object);
        };

        let resolver = self.client.entity_type_resolver()?;
        if resolver.object_kind(existing_record.entity_type_id) != Some(object.kind) {
            return Err(format!(
                "'{}' exists as a different object kind; refusing to save it as {:?}",
                object.name, object.kind
            ));
        }
        let parts = stored_parts(&resolver, object)?;

        self.client.begin_transaction()?;

        let result = (|| {
            let entity_type_id = existing_record.entity_type_id;
            let entity_id = existing_record.entity_id;
            let new_version_id = existing_record.entity_version_id + 1;

            // 1. Update ModelEntityVersion
            self.client.update_model_entity_version(
                self.model_id,
                entity_type_id,
                entity_id,
                new_version_id,
                Some(&object.name),
            )?;

            // 2. Insert new version component parts
            for &(component_type_id, part_kind, part) in &parts {
                let component_id = self.client.allocate_entity_id()?;

                let payload = if part_kind.is_tokenized_source() {
                    build_token_data_list(&part.source)
                } else {
                    part.source.clone()
                };

                let blob = encode_blob(&payload, true)?;

                self.client.insert_entity_version(EntityVersionRecord {
                    entity_type_id: component_type_id,
                    entity_id: component_id,
                    entity_version_id: new_version_id,
                    data: blob,
                    properties: None,
                })?;

                self.client
                    .insert_composition(EntityVersionCompositionRecord {
                        compound_entity_type_id: entity_type_id,
                        compound_entity_id: entity_id,
                        compound_entity_version_id: new_version_id,
                        component_entity_type_id: component_type_id,
                        component_entity_id: component_id,
                        component_entity_version_id: new_version_id,
                    })?;
            }

            // 3. Refresh cross-references
            self.client
                .clear_cross_references(self.model_id, entity_type_id, entity_id)?;

            for r in &object.references {
                let (to_type_id, to_id) = reference_target(
                    &active,
                    (entity_type_id, entity_id),
                    &object.name,
                    r.target.as_str(),
                )?;

                let ref_type = match r.kind {
                    ReferenceKind::Calls => 1,
                    ReferenceKind::Uses => 2,
                    ReferenceKind::BasedOn => 3,
                    ReferenceKind::Contains => 4,
                };

                self.client
                    .insert_cross_reference(ModelCrossReferenceRecord {
                        model_id: self.model_id,
                        from_entity_type_id: entity_type_id,
                        from_entity_id: entity_id,
                        to_entity_type_id: to_type_id,
                        to_entity_id: to_id,
                        reference_type: ref_type,
                    })?;
            }

            Ok(UdmObjectIdentity {
                entity_type_id,
                entity_id,
                version_id: new_version_id,
                name: object.name.clone(),
            })
        })();

        match result {
            Ok(identity) => {
                self.client.commit_transaction()?;
                Ok(identity)
            }
            Err(err) => {
                let _ = self.client.rollback_transaction();
                Err(err)
            }
        }
    }

    /// Deletes an object by name from the active model in the UDM database.
    pub fn delete_object(&self, object_name: &str) -> Result<bool, String> {
        let active = self.client.get_active_objects(self.model_id)?;
        let existing = active
            .iter()
            .find(|r| r.name.eq_ignore_ascii_case(object_name))
            .cloned();

        let Some(record) = existing else {
            return Ok(false);
        };

        self.client.begin_transaction()?;

        let result = (|| {
            self.client
                .delete_object(self.model_id, record.entity_type_id, record.entity_id)?;
            self.client.clear_compositions(
                record.entity_type_id,
                record.entity_id,
                record.entity_version_id,
            )?;
            self.client.clear_cross_references(
                self.model_id,
                record.entity_type_id,
                record.entity_id,
            )?;
            let _ = self.client.release_lock(object_name, "");
            Ok(true)
        })();

        match result {
            Ok(deleted) => {
                self.client.commit_transaction()?;
                Ok(deleted)
            }
            Err(err) => {
                let _ = self.client.rollback_transaction();
                Err(err)
            }
        }
    }

    /// Saves an object with an exclusive lock lease, automatically releasing the lock upon completion.
    pub fn save_with_lock(
        &self,
        object: &KbObject,
        user_name: &str,
        guid: &str,
        machine_name: &str,
    ) -> Result<UdmObjectIdentity, String> {
        let acquired = self.acquire_lock(&object.name, user_name, guid, machine_name)?;
        if !acquired {
            return Err(format!(
                "Lock acquisition failed for object '{}': already locked by another agent",
                object.name
            ));
        }

        let save_res = self.save_object(object);
        let _ = self.release_lock(&object.name, guid);
        save_res
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::catalog::{ObjectId, ObjectKind, SourcePart};
    use crate::udm::reader::UdmReader;

    #[test]
    fn writer_create_and_save_roundtrip() {
        let client = Arc::new(UdmClient::new_in_memory());
        let writer = UdmWriter::new(client.clone());
        let reader = UdmReader::new(client);

        // 1. Create a Procedure
        let mut proc = KbObject::new(
            ObjectId::new("pcreateinvoice").unwrap(),
            "PCreateInvoice",
            ObjectKind::Procedure,
        );
        proc.source_parts.push(SourcePart {
            name: "SOURCE".to_string(),
            source: "For Each\n    Msg('Invoice Created')\nEndFor".to_string(),
        });
        proc.source_parts.push(SourcePart {
            name: "RULES".to_string(),
            source: "Parm(in:&CustomerId);".to_string(),
        });

        let id1 = writer.create_object(&proc).expect("create object");
        assert_eq!(id1.version_id, 1);
        assert_eq!(id1.name, "PCreateInvoice");

        // Verify with reader
        let read1 = reader
            .read_object_by_name("PCreateInvoice")
            .unwrap()
            .expect("read object");
        assert_eq!(read1.source_parts.len(), 2);
        assert_eq!(
            read1
                .source_parts
                .iter()
                .find(|p| p.name == "SOURCE")
                .unwrap()
                .source,
            "For Each\n    Msg('Invoice Created')\nEndFor"
        );

        // 2. Save an updated version
        proc.source_parts[0].source = "For Each\n    Msg('Invoice Created V2')\nEndFor".to_string();

        let id2 = writer.save_object(&proc).expect("save object");
        assert_eq!(id2.version_id, 2);

        // Verify version 2 read
        let read2 = reader
            .read_object_by_name("PCreateInvoice")
            .unwrap()
            .expect("read object v2");
        assert_eq!(
            read2
                .source_parts
                .iter()
                .find(|p| p.name == "SOURCE")
                .unwrap()
                .source,
            "For Each\n    Msg('Invoice Created V2')\nEndFor"
        );
    }

    #[test]
    fn writer_lock_management_and_save_with_lock() {
        let client = Arc::new(UdmClient::new_in_memory());
        let writer = UdmWriter::new(client);

        let proc = KbObject::new(
            ObjectId::new("plockedproc").unwrap(),
            "PLockedProc",
            ObjectKind::Procedure,
        );

        // Save with lock
        let identity = writer
            .save_with_lock(&proc, "agent_alice", "guid-999", "DEV-BOX")
            .expect("save with lock");
        assert_eq!(identity.name, "PLockedProc");

        // Verify lock was released automatically
        assert!(!writer.is_locked("PLockedProc").unwrap());
    }

    #[test]
    fn writer_delete_object() {
        let client = Arc::new(UdmClient::new_in_memory());
        let writer = UdmWriter::new(client.clone());
        let reader = UdmReader::new(client);

        let proc = KbObject::new(
            ObjectId::new("ptodelete").unwrap(),
            "PToDelete",
            ObjectKind::Procedure,
        );
        writer.create_object(&proc).expect("create object");
        assert!(reader.read_object_by_name("PToDelete").unwrap().is_some());

        let deleted = writer.delete_object("PToDelete").expect("delete object");
        assert!(deleted);
        assert!(reader.read_object_by_name("PToDelete").unwrap().is_none());
    }
}
