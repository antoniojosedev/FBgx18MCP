//! Writes to the Knowledge Base: ids, flags, entities, versions, compositions, cross references and locks.

use super::*;

impl UdmClient {
    /// Allocates a new unique entity ID.
    pub fn allocate_entity_id(&self) -> Result<i32, String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store.allocate_entity_id())
    }

    /// Retrieves active object descriptors for a model. If live connection is configured,
    /// populates the internal cache on demand.
    /// Stores the `GenerateObject` value of one Procedure of a model (in-memory client only; tests).
    pub fn set_generate_flag(
        &self,
        model_id: i32,
        entity_type_id: i32,
        entity_id: i32,
        value: &str,
    ) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store
            .generate_flags
            .insert((model_id, entity_type_id, entity_id), value.to_string());
        Ok(())
    }

    /// Records the `Entity.EntityGuid` of an entity (test and emulation storage).
    pub fn insert_entity_guid(
        &self,
        entity_type_id: i32,
        entity_id: i32,
        guid: &str,
    ) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store
            .entity_guids
            .insert((entity_type_id, entity_id), guid.to_string());
        Ok(())
    }

    /// Records the `ModelEntityVersion` parent of an object (test and emulation storage).
    pub fn insert_model_parent(
        &self,
        model_id: i32,
        entity_type_id: i32,
        entity_id: i32,
        parent_type_id: i32,
        parent_id: i32,
    ) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.model_parents.insert(
            (model_id, entity_type_id, entity_id),
            (parent_type_id, parent_id),
        );
        Ok(())
    }

    /// Inserts a new active model entity version record.
    pub fn insert_model_entity_version(&self, rec: ModelEntityVersionRecord) -> Result<(), String> {
        self.clear_bulk_coverage();
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.model_entity_versions.retain(|m| {
            !(m.model_id == rec.model_id
                && m.entity_type_id == rec.entity_type_id
                && m.entity_id == rec.entity_id)
        });
        store.model_entity_versions.push(rec);
        Ok(())
    }

    /// Updates the version ID or name of an existing active model entity.
    pub fn update_model_entity_version(
        &self,
        model_id: i32,
        entity_type_id: i32,
        entity_id: i32,
        new_version_id: i32,
        new_name: Option<&str>,
    ) -> Result<(), String> {
        self.clear_bulk_coverage();
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        if let Some(record) = store.model_entity_versions.iter_mut().find(|m| {
            m.model_id == model_id && m.entity_type_id == entity_type_id && m.entity_id == entity_id
        }) {
            record.entity_version_id = new_version_id;
            if let Some(name) = new_name {
                record.name = name.to_string();
            }
            Ok(())
        } else {
            Err(format!(
                "ModelEntityVersion not found for ModelId {model_id}, Type {entity_type_id}, Entity {entity_id}"
            ))
        }
    }

    /// Inserts a version payload record into `EntityVersion`.
    pub fn insert_entity_version(&self, rec: EntityVersionRecord) -> Result<(), String> {
        self.clear_bulk_coverage();
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.entity_versions.retain(|ev| {
            !(ev.entity_type_id == rec.entity_type_id
                && ev.entity_id == rec.entity_id
                && ev.entity_version_id == rec.entity_version_id)
        });
        store.entity_versions.push(rec);
        Ok(())
    }

    /// Inserts a part composition link into `EntityVersionComposition`.
    pub fn insert_composition(&self, rec: EntityVersionCompositionRecord) -> Result<(), String> {
        self.clear_bulk_coverage();
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.entity_version_compositions.push(rec);
        Ok(())
    }

    /// Clears part composition links for a specific compound version.
    pub fn clear_compositions(
        &self,
        compound_type: i32,
        compound_id: i32,
        compound_ver: i32,
    ) -> Result<(), String> {
        self.clear_bulk_coverage();
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.entity_version_compositions.retain(|c| {
            !(c.compound_entity_type_id == compound_type
                && c.compound_entity_id == compound_id
                && c.compound_entity_version_id == compound_ver)
        });
        Ok(())
    }

    /// Inserts a cross-reference link into `ModelCrossReference`.
    pub fn insert_cross_reference(&self, rec: ModelCrossReferenceRecord) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.model_cross_references.push(rec);
        Ok(())
    }

    /// Clears cross-references originating from a specific object in a model.
    pub fn clear_cross_references(
        &self,
        model_id: i32,
        from_type: i32,
        from_id: i32,
    ) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.model_cross_references.retain(|xr| {
            !(xr.model_id == model_id
                && xr.from_entity_type_id == from_type
                && xr.from_entity_id == from_id)
        });
        Ok(())
    }

    /// Deletes an object from `ModelEntityVersion` and related links.
    pub fn delete_object(
        &self,
        model_id: i32,
        entity_type_id: i32,
        entity_id: i32,
    ) -> Result<bool, String> {
        self.clear_bulk_coverage();
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        let len_before = store.model_entity_versions.len();
        store.model_entity_versions.retain(|m| {
            !(m.model_id == model_id
                && m.entity_type_id == entity_type_id
                && m.entity_id == entity_id)
        });

        let found = store.model_entity_versions.len() < len_before;
        if found {
            store.model_cross_references.retain(|xr| {
                !(xr.model_id == model_id
                    && xr.from_entity_type_id == entity_type_id
                    && xr.from_entity_id == entity_id)
            });
        }
        Ok(found)
    }

    /// Attempts to acquire a lock in the `State` table.
    pub fn acquire_lock(
        &self,
        state_name: &str,
        user_name: &str,
        guid: &str,
        machine_name: &str,
    ) -> Result<bool, String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;

        if let Some(existing) = store
            .state_locks
            .iter()
            .find(|l| l.state_name.eq_ignore_ascii_case(state_name))
        {
            if existing.state_guid == guid {
                return Ok(true);
            }
            return Ok(false);
        }

        store.state_locks.push(StateLockRecord {
            state_name: state_name.to_string(),
            state_user_name: user_name.to_string(),
            state_guid: guid.to_string(),
            owner_machine_name: machine_name.to_string(),
        });
        Ok(true)
    }

    /// Releases a previously acquired lock from the `State` table.
    pub fn release_lock(&self, state_name: &str, guid: &str) -> Result<bool, String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        let len_before = store.state_locks.len();
        store
            .state_locks
            .retain(|l| !(l.state_name.eq_ignore_ascii_case(state_name) && l.state_guid == guid));
        Ok(store.state_locks.len() < len_before)
    }

    /// Inspects the status of a specific lock by name.
    pub fn get_lock(&self, state_name: &str) -> Result<Option<StateLockRecord>, String> {
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .state_locks
            .iter()
            .find(|l| l.state_name.eq_ignore_ascii_case(state_name))
            .cloned())
    }

    /// Lists all currently active state locks.
    pub fn list_locks(&self) -> Result<Vec<StateLockRecord>, String> {
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store.state_locks.clone())
    }
}
