//! Reads of one model: active objects, parents, compositions, entity versions, entity types and cross references.

use super::live_rows::{entity_version_from_fields, parse_live_int, parse_live_rows};
use super::*;

impl UdmClient {
    /// The stored `GenerateObject` value of each object of `entity_type_id` in `model_id`, keyed by entity
    /// id, as written ("True" or "False"; any other text is returned as stored and judged by the caller).
    /// The property is a model-level row (`ModelEntityProperty`) whose property id is the entity id of the
    /// `Udm.Types.Property` row named `Property: GenerateObject`; the join resolves that id by name. A
    /// Knowledge Base without that row has no value, and an object without a row is absent here.
    pub fn generate_flags(
        &self,
        model_id: i32,
        entity_type_id: i32,
    ) -> Result<BTreeMap<i32, String>, String> {
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let in_memory: BTreeMap<i32, String> = store
                .generate_flags
                .iter()
                .filter(|((m, t, _), _)| *m == model_id && *t == entity_type_id)
                .map(|((_, _, id), value)| (*id, value.clone()))
                .collect();
            if !in_memory.is_empty() {
                return Ok(in_memory);
            }
        }
        if self.connection_info.is_none() {
            return Ok(BTreeMap::new());
        }
        let sql = format!(
            "SET NOCOUNT ON; SELECT m.EntityId, m.ModelEntityPropertyValue FROM ModelEntityProperty m JOIN EntityVersion p ON p.EntityId = m.ModelEntityPropertyId AND p.EntityVersionName = 'Property: GenerateObject' JOIN EntityType t ON t.EntityTypeId = p.EntityTypeId AND t.EntityTypeName = 'Udm.Types.Property' WHERE m.ModelId = {model_id} AND m.EntityTypeId = {entity_type_id}"
        );
        let raw = self.execute_live_sql(&sql)?;
        let mut flags: BTreeMap<i32, String> = BTreeMap::new();
        for row in parse_live_rows(&raw, 2)? {
            let id = parse_live_int(row[0], "EntityId")?;
            let value = row[1].trim().to_string();
            // The same object under two property rows with different values is refused, never resolved.
            if let Some(previous) = flags.get(&id) {
                if *previous != value {
                    return Err(format!(
                        "GenerateObject of entity {id} is stored twice with values '{previous}' and '{value}'"
                    ));
                }
            }
            flags.insert(id, value);
        }
        Ok(flags)
    }

    /// The stored `IsMain` ("Main program") property of each object of `entity_type_id`, by entity id. The SDK
    /// generates a Procedure that is not main only when a generated object calls it (spc0217, "Object is
    /// unreachable"). A client without a live database answers nothing, and every Procedure stays a root.
    pub fn main_flags(
        &self,
        model_id: i32,
        entity_type_id: i32,
    ) -> Result<BTreeMap<i32, bool>, String> {
        if self.connection_info.is_none() {
            return Ok(BTreeMap::new());
        }
        let sql = format!(
            "SET NOCOUNT ON; SELECT m.EntityId, m.ModelEntityPropertyValue FROM ModelEntityProperty m JOIN EntityVersion p ON p.EntityId = m.ModelEntityPropertyId AND p.EntityVersionName = 'Property: IsMain' JOIN EntityType t ON t.EntityTypeId = p.EntityTypeId AND t.EntityTypeName = 'Udm.Types.Property' WHERE m.ModelId = {model_id} AND m.EntityTypeId = {entity_type_id}"
        );
        let raw = self.execute_live_sql(&sql)?;
        let mut flags = BTreeMap::new();
        for row in parse_live_rows(&raw, 2)? {
            let id = parse_live_int(row[0], "EntityId")?;
            let value = match row[1].trim() {
                "True" => true,
                "False" => false,
                other => return Err(format!("IsMain of entity {id} is stored as '{other}'")),
            };
            if flags
                .insert(id, value)
                .is_some_and(|previous| previous != value)
            {
                return Err(format!(
                    "IsMain of entity {id} is stored twice with different values"
                ));
            }
        }
        Ok(flags)
    }

    pub fn get_active_objects(
        &self,
        model_id: i32,
    ) -> Result<Vec<ModelEntityVersionRecord>, String> {
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let in_memory: Vec<_> = store
                .model_entity_versions
                .iter()
                .filter(|m| m.model_id == model_id)
                .cloned()
                .collect();
            if !in_memory.is_empty() {
                return Ok(in_memory);
            }
        }

        if self.connection_info.is_some() {
            let sql = format!(
                "SET NOCOUNT ON; SELECT EntityTypeId, EntityId, EntityVersionId, ModelEntityVersionName FROM ModelEntityVersion WHERE ModelId = {model_id}"
            );
            // A live read that fails is an error: returning the (empty) in-memory
            // cache here reported "no objects" for a database the client could
            // not read.
            let raw = self.execute_live_sql(&sql)?;
            let rows = parse_live_rows(&raw, 4)?;
            let mut fetched = Vec::with_capacity(rows.len());
            for row in rows {
                fetched.push(ModelEntityVersionRecord {
                    model_id,
                    entity_type_id: parse_live_int(row[0], "EntityTypeId")?,
                    entity_id: parse_live_int(row[1], "EntityId")?,
                    entity_version_id: parse_live_int(row[2], "EntityVersionId")?,
                    name: row[3].to_string(),
                });
            }
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            for rec in fetched {
                if !store.model_entity_versions.iter().any(|m| {
                    m.model_id == rec.model_id
                        && m.entity_type_id == rec.entity_type_id
                        && m.entity_id == rec.entity_id
                }) {
                    store.model_entity_versions.push(rec);
                }
            }
            return Ok(store
                .model_entity_versions
                .iter()
                .filter(|m| m.model_id == model_id)
                .cloned()
                .collect());
        }

        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .model_entity_versions
            .iter()
            .filter(|m| m.model_id == model_id)
            .cloned()
            .collect())
    }

    /// The holder (module or folder) of every row of the model, read from the live database. A row without a
    /// holder has a zero parent type. An in-memory client holds no parent links and returns none.
    pub fn get_model_parents(&self, model_id: i32) -> Result<Vec<ModelParentRecord>, String> {
        if self.connection_info.is_none() {
            return Ok(Vec::new());
        }
        let sql = format!(
            "SET NOCOUNT ON; SELECT EntityTypeId, EntityId, ModelParentEntityTypeId, ModelParentEntityId FROM ModelEntityVersion WHERE ModelId = {model_id}"
        );
        let raw = self.execute_live_sql(&sql)?;
        parse_live_rows(&raw, 4)?
            .iter()
            .map(|row| {
                Ok(ModelParentRecord {
                    entity_type_id: parse_live_int(row[0], "EntityTypeId")?,
                    entity_id: parse_live_int(row[1], "EntityId")?,
                    parent_entity_type_id: parse_live_int(row[2], "ModelParentEntityTypeId")?,
                    parent_entity_id: parse_live_int(row[3], "ModelParentEntityId")?,
                })
            })
            .collect()
    }

    /// Retrieves part compositions for a compound entity version.
    pub fn get_compositions(
        &self,
        compound_type: i32,
        compound_id: i32,
        compound_ver: i32,
    ) -> Result<Vec<EntityVersionCompositionRecord>, String> {
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let in_memory: Vec<_> = store
                .entity_version_compositions
                .iter()
                .filter(|c| {
                    c.compound_entity_type_id == compound_type
                        && c.compound_entity_id == compound_id
                        && c.compound_entity_version_id == compound_ver
                })
                .cloned()
                .collect();
            if !in_memory.is_empty() {
                return Ok(in_memory);
            }
        }

        if self.connection_info.is_some()
            && !self.composition_is_covered(compound_type, compound_id, compound_ver)
        {
            let sql = format!(
                "SET NOCOUNT ON; SELECT ComponentEntityTypeId, ComponentEntityId, ComponentEntityVersionId FROM EntityVersionComposition WHERE CompoundEntityTypeId = {compound_type} AND CompoundEntityId = {compound_id} AND CompoundEntityVersionId = {compound_ver}"
            );
            let raw = self.execute_live_sql(&sql)?;
            let rows = parse_live_rows(&raw, 3)?;
            let mut fetched = Vec::with_capacity(rows.len());
            for row in rows {
                fetched.push(EntityVersionCompositionRecord {
                    compound_entity_type_id: compound_type,
                    compound_entity_id: compound_id,
                    compound_entity_version_id: compound_ver,
                    component_entity_type_id: parse_live_int(row[0], "ComponentEntityTypeId")?,
                    component_entity_id: parse_live_int(row[1], "ComponentEntityId")?,
                    component_entity_version_id: parse_live_int(
                        row[2],
                        "ComponentEntityVersionId",
                    )?,
                });
            }
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            for rec in fetched {
                if !store.entity_version_compositions.iter().any(|c| {
                    c.compound_entity_type_id == rec.compound_entity_type_id
                        && c.compound_entity_id == rec.compound_entity_id
                        && c.compound_entity_version_id == rec.compound_entity_version_id
                        && c.component_entity_type_id == rec.component_entity_type_id
                        && c.component_entity_id == rec.component_entity_id
                }) {
                    store.entity_version_compositions.push(rec);
                }
            }
            return Ok(store
                .entity_version_compositions
                .iter()
                .filter(|c| {
                    c.compound_entity_type_id == compound_type
                        && c.compound_entity_id == compound_id
                        && c.compound_entity_version_id == compound_ver
                })
                .cloned()
                .collect());
        }

        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .entity_version_compositions
            .iter()
            .filter(|c| {
                c.compound_entity_type_id == compound_type
                    && c.compound_entity_id == compound_id
                    && c.compound_entity_version_id == compound_ver
            })
            .cloned()
            .collect())
    }

    /// Retrieves an entity version payload record.
    pub fn get_entity_version(
        &self,
        entity_type: i32,
        entity_id: i32,
        version_id: i32,
    ) -> Result<Option<EntityVersionRecord>, String> {
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            if let Some(rec) = store.entity_versions.iter().find(|ev| {
                ev.entity_type_id == entity_type
                    && ev.entity_id == entity_id
                    && ev.entity_version_id == version_id
            }) {
                return Ok(Some(rec.clone()));
            }
        }

        if self.connection_info.is_some()
            && !self.entity_version_is_covered(entity_type, entity_id, version_id)
        {
            // One row when the version exists (an empty payload is still a row),
            // none when it does not: the caller tells a missing part from an empty one.
            // The properties are read as hex too: the column is free text (a value can hold a
            // newline), and hex keeps every row on one tab-separated line with a declared length.
            let sql = format!(
                "SET NOCOUNT ON; SELECT 'PRESENT', ISNULL(DATALENGTH(EntityVersionData), 0), CONVERT(VARCHAR(MAX), EntityVersionData, 2), ISNULL(DATALENGTH(EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST(EntityVersionProperties AS VARBINARY(MAX)), 2) FROM EntityVersion WHERE EntityTypeId = {entity_type} AND EntityId = {entity_id} AND EntityVersionId = {version_id}"
            );
            let raw = self.execute_live_sql(&sql)?;
            let rows = parse_live_rows(&raw, 5)?;
            match rows.as_slice() {
                [] => {}
                [row] => {
                    let rec = entity_version_from_fields(
                        entity_type,
                        entity_id,
                        version_id,
                        row[1],
                        row[2],
                        row[3],
                        row[4],
                    )?;
                    let mut store = self
                        .storage
                        .write()
                        .map_err(|e| format!("Storage lock poisoned: {e}"))?;
                    store.entity_versions.push(rec.clone());
                    return Ok(Some(rec));
                }
                _ => {
                    return Err(format!(
                        "EntityVersion ({entity_type}, {entity_id}, {version_id}) matched more than one row"
                    ));
                }
            }
        }

        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .entity_versions
            .iter()
            .find(|ev| {
                ev.entity_type_id == entity_type
                    && ev.entity_id == entity_id
                    && ev.entity_version_id == version_id
            })
            .cloned())
    }

    /// Returns the `EntityType` rows of this Knowledge Base. A live client reads
    /// its own table on every call (no seeds involved); an in-memory client
    /// returns its declared vocabulary.
    pub fn get_entity_types(&self) -> Result<Vec<EntityTypeRecord>, String> {
        if self.connection_info.is_some() {
            let raw = self.execute_live_sql(
                "SET NOCOUNT ON; SELECT EntityTypeId, EntityTypeName, ISNULL(EntityTypeNamespace, '') FROM EntityType",
            )?;
            let rows = parse_live_rows(&raw, 3)?;
            return rows
                .iter()
                .map(|row| {
                    Ok(EntityTypeRecord {
                        entity_type_id: parse_live_int(row[0], "EntityTypeId")?,
                        name: row[1].to_string(),
                        namespace: row[2].to_string(),
                    })
                })
                .collect();
        }
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store.entity_types.clone())
    }

    /// The `(EntityTypeId, EntityId)` of this model's Knowledge Base root module, or
    /// `None` when the model has no root.
    ///
    /// The root is identified by its owner data, never by its display name: a
    /// parent-less Module with `EntityId` 1 whose `Entity.EntityGuid` is
    /// [`SYSTEM_ROOT_MODULE_GUID`]. A user Module named `Root Module` under a parent
    /// is not the root, and neither is an `EntityId` 1 with a parent or another GUID.
    pub fn system_root_module(
        &self,
        model_id: i32,
        module_type_id: i32,
    ) -> Result<Option<(i32, i32)>, String> {
        if self.is_live() {
            let sql = format!(
                "SET NOCOUNT ON; SELECT m.EntityTypeId, m.EntityId, ISNULL(e.EntityGuid, '') FROM ModelEntityVersion m LEFT JOIN Entity e ON e.EntityTypeId = m.EntityTypeId AND e.EntityId = m.EntityId WHERE m.ModelId = {model_id} AND m.EntityTypeId = {module_type_id} AND m.EntityId = 1 AND m.ModelParentEntityTypeId = 0 AND m.ModelParentEntityId = 0"
            );
            let raw = self.execute_live_sql(&sql)?;
            let rows = parse_live_rows(&raw, 3)?;
            let mut root = None;
            for row in &rows {
                if row[2].eq_ignore_ascii_case(SYSTEM_ROOT_MODULE_GUID) {
                    let entity_type = parse_live_int(row[0], "EntityTypeId")?;
                    let entity_id = parse_live_int(row[1], "EntityId")?;
                    root = Some((entity_type, entity_id));
                }
            }
            return Ok(root);
        }
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        let present = store.model_entity_versions.iter().any(|m| {
            m.model_id == model_id && m.entity_type_id == module_type_id && m.entity_id == 1
        });
        let parent_less = store.model_parents.get(&(model_id, module_type_id, 1)) == Some(&(0, 0));
        let guid_matches = store
            .entity_guids
            .get(&(module_type_id, 1))
            .is_some_and(|guid| guid.eq_ignore_ascii_case(SYSTEM_ROOT_MODULE_GUID));
        Ok((present && parent_less && guid_matches).then_some((module_type_id, 1)))
    }

    /// The served vocabulary of this Knowledge Base, keyed by its own entity
    /// type ids. A live client reads `EntityType` once and keeps the result for
    /// every clone of the client; an in-memory client resolves its current table
    /// each time, so a test that declares a vocabulary sees it immediately.
    pub fn entity_type_resolver(&self) -> Result<Arc<EntityTypeResolver>, String> {
        if !self.is_live() {
            let records = self.get_entity_types()?;
            return Ok(Arc::new(EntityTypeResolver::from_records(&records)?));
        }
        if let Some(cached) = self.live_vocabulary.get() {
            return Ok(Arc::clone(cached));
        }
        let records = self.get_entity_types()?;
        let resolver = Arc::new(EntityTypeResolver::from_records(&records)?);
        // A concurrent first call may have stored an equal resolver already: both
        // were read from the same table, so either one is correct.
        let _ = self.live_vocabulary.set(Arc::clone(&resolver));
        Ok(resolver)
    }

    /// Retrieves cross-references originating from a specific object in a model.
    pub fn get_cross_references(
        &self,
        model_id: i32,
        from_type_id: i32,
        from_id: i32,
    ) -> Result<Vec<ModelCrossReferenceRecord>, String> {
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .model_cross_references
            .iter()
            .filter(|xr| {
                xr.model_id == model_id
                    && xr.from_entity_type_id == from_type_id
                    && xr.from_entity_id == from_id
            })
            .cloned()
            .collect())
    }

    /// Retrieves every cross-reference of a model. With a live connection the
    /// whole table is read once and merged into the in-memory cache, so
    /// per-object hydration stays in memory instead of issuing one query per
    /// object; in-memory clients just return the cached records.
    pub fn get_cross_references_for_model(
        &self,
        model_id: i32,
    ) -> Result<Vec<ModelCrossReferenceRecord>, String> {
        if self.connection_info.is_some() {
            let sql = format!(
                "SET NOCOUNT ON; SELECT FromEntityTypeId, FromEntityId, ToEntityTypeId, ToEntityId, ReferenceType FROM ModelCrossReference WHERE ModelId = {model_id}"
            );
            let raw = self.execute_live_sql(&sql)?;
            let rows = parse_live_rows(&raw, 5)?;
            let mut fetched = Vec::with_capacity(rows.len());
            for row in rows {
                fetched.push(ModelCrossReferenceRecord {
                    model_id,
                    from_entity_type_id: parse_live_int(row[0], "FromEntityTypeId")?,
                    from_entity_id: parse_live_int(row[1], "FromEntityId")?,
                    to_entity_type_id: parse_live_int(row[2], "ToEntityTypeId")?,
                    to_entity_id: parse_live_int(row[3], "ToEntityId")?,
                    reference_type: parse_live_int(row[4], "ReferenceType")?,
                });
            }
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            for rec in fetched {
                if !store.model_cross_references.iter().any(|x| {
                    x.model_id == rec.model_id
                        && x.from_entity_type_id == rec.from_entity_type_id
                        && x.from_entity_id == rec.from_entity_id
                        && x.to_entity_type_id == rec.to_entity_type_id
                        && x.to_entity_id == rec.to_entity_id
                        && x.reference_type == rec.reference_type
                }) {
                    store.model_cross_references.push(rec);
                }
            }
        }

        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store
            .model_cross_references
            .iter()
            .filter(|xr| xr.model_id == model_id)
            .cloned()
            .collect())
    }
}
