//! Database client interface and query execution for GeneXus UDM physical storage.
//!
//! Provides `UdmClient` coordinating in-memory mock storage and live SQL Server /
//! LocalDB execution.

#![forbid(unsafe_code)]

use crate::udm::connection::KbConnectionInfo;
use crate::udm::live;
pub use crate::udm::mock::{
    EntityTypeRecord, EntityVersionCompositionRecord, EntityVersionRecord, InMemoryDatabase,
    ModelCrossReferenceRecord, ModelEntityVersionRecord, StateLockRecord,
};
pub use crate::udm::row::{UdmRow, UdmValue};

use std::path::Path;
use std::sync::{Arc, RwLock};

/// Unified Data Model (UDM) physical storage client.
#[derive(Debug, Clone)]
pub struct UdmClient {
    connection_info: Option<KbConnectionInfo>,
    storage: Arc<RwLock<InMemoryDatabase>>,
}

impl UdmClient {
    /// Creates a purely in-memory UDM client for testing, emulation, and fast verification.
    pub fn new_in_memory() -> Self {
        Self {
            connection_info: None,
            storage: Arc::new(RwLock::new(InMemoryDatabase::new())),
        }
    }

    /// Creates a UDM client configured with connection info pointing to a SQL Server or LocalDB instance.
    pub fn from_connection_info(info: KbConnectionInfo) -> Self {
        Self {
            connection_info: Some(info),
            storage: Arc::new(RwLock::new(InMemoryDatabase::new())),
        }
    }

    /// Creates a UDM client connected to a Knowledge Base directory containing `knowledgebase.connection`.
    pub fn connect_kb<P: AsRef<Path>>(kb_dir: P) -> Result<Self, String> {
        let info = KbConnectionInfo::from_kb_dir(kb_dir)?;
        Ok(Self::from_connection_info(info))
    }

    /// Returns the target `KbConnectionInfo`, if configured.
    pub fn connection_info(&self) -> Option<&KbConnectionInfo> {
        self.connection_info.as_ref()
    }

    /// Begins a transactional boundary.
    pub fn begin_transaction(&self) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.begin_transaction();
        Ok(())
    }

    /// Commits the active transaction.
    pub fn commit_transaction(&self) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.commit_transaction()
    }

    /// Rolls back the active transaction.
    pub fn rollback_transaction(&self) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.rollback_transaction()
    }

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
            if let Ok(raw) = self.execute_live_sql(&sql) {
                let mut store = self
                    .storage
                    .write()
                    .map_err(|e| format!("Storage lock poisoned: {e}"))?;
                for line in raw.lines() {
                    let parts: Vec<&str> = line.split('\t').map(|s| s.trim()).collect();
                    if parts.len() >= 4 {
                        if let (Ok(et), Ok(ei), Ok(evi)) = (
                            parts[0].parse::<i32>(),
                            parts[1].parse::<i32>(),
                            parts[2].parse::<i32>(),
                        ) {
                            let rec = ModelEntityVersionRecord {
                                model_id,
                                entity_type_id: et,
                                entity_id: ei,
                                entity_version_id: evi,
                                name: parts[3].to_string(),
                            };
                            if !store.model_entity_versions.iter().any(|m| {
                                m.model_id == rec.model_id
                                    && m.entity_type_id == rec.entity_type_id
                                    && m.entity_id == rec.entity_id
                            }) {
                                store.model_entity_versions.push(rec);
                            }
                        }
                    }
                }
                return Ok(store
                    .model_entity_versions
                    .iter()
                    .filter(|m| m.model_id == model_id)
                    .cloned()
                    .collect());
            }
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

        if self.connection_info.is_some() {
            let sql = format!(
                "SET NOCOUNT ON; SELECT ComponentEntityTypeId, ComponentEntityId, ComponentEntityVersionId FROM EntityVersionComposition WHERE CompoundEntityTypeId = {compound_type} AND CompoundEntityId = {compound_id} AND CompoundEntityVersionId = {compound_ver}"
            );
            if let Ok(raw) = self.execute_live_sql(&sql) {
                let mut store = self
                    .storage
                    .write()
                    .map_err(|e| format!("Storage lock poisoned: {e}"))?;
                for line in raw.lines() {
                    let parts: Vec<&str> = line.split('\t').map(|s| s.trim()).collect();
                    if parts.len() >= 3 {
                        if let (Ok(ct), Ok(ci), Ok(cvi)) = (
                            parts[0].parse::<i32>(),
                            parts[1].parse::<i32>(),
                            parts[2].parse::<i32>(),
                        ) {
                            let rec = EntityVersionCompositionRecord {
                                compound_entity_type_id: compound_type,
                                compound_entity_id: compound_id,
                                compound_entity_version_id: compound_ver,
                                component_entity_type_id: ct,
                                component_entity_id: ci,
                                component_entity_version_id: cvi,
                            };
                            if !store.entity_version_compositions.iter().any(|c| {
                                c.compound_entity_type_id == rec.compound_entity_type_id
                                    && c.compound_entity_id == rec.compound_entity_id
                                    && c.compound_entity_version_id
                                        == rec.compound_entity_version_id
                                    && c.component_entity_type_id == rec.component_entity_type_id
                                    && c.component_entity_id == rec.component_entity_id
                            }) {
                                store.entity_version_compositions.push(rec);
                            }
                        }
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

        if self.connection_info.is_some() {
            let sql = format!(
                "SET NOCOUNT ON; SELECT CONVERT(VARCHAR(MAX), EntityVersionData, 2) AS HexData FROM EntityVersion WHERE EntityTypeId = {entity_type} AND EntityId = {entity_id} AND EntityVersionId = {version_id}"
            );
            if let Ok(raw) = self.execute_live_sql(&sql) {
                let hex_str = raw.trim();
                if !hex_str.is_empty() {
                    let mut data = Vec::new();
                    let hex_bytes = hex_str.as_bytes();
                    let mut i = 0;
                    while i + 1 < hex_bytes.len() {
                        if let (Some(h), Some(l)) =
                            (hex_val(hex_bytes[i]), hex_val(hex_bytes[i + 1]))
                        {
                            data.push((h << 4) | l);
                        }
                        i += 2;
                    }

                    if !data.is_empty() {
                        let rec = EntityVersionRecord {
                            entity_type_id: entity_type,
                            entity_id,
                            entity_version_id: version_id,
                            data,
                            properties: None,
                        };
                        let mut store = self
                            .storage
                            .write()
                            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
                        store.entity_versions.push(rec.clone());
                        return Ok(Some(rec));
                    }
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

    /// Retrieves all registered entity types in the catalog.
    pub fn get_entity_types(&self) -> Result<Vec<EntityTypeRecord>, String> {
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store.entity_types.clone())
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

    /// Inserts a new active model entity version record.
    pub fn insert_model_entity_version(&self, rec: ModelEntityVersionRecord) -> Result<(), String> {
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

    /// Executes raw SQL against live SQL Server or LocalDB instance using `sqlcmd`.
    pub fn execute_live_sql(&self, sql: &str) -> Result<String, String> {
        let info = self
            .connection_info
            .as_ref()
            .ok_or_else(|| "No connection info configured for live SQL execution".to_string())?;
        live::execute_sqlcmd(info, sql)
    }

    /// Tests the connection to the database.
    pub fn test_connection(&self) -> Result<bool, String> {
        if self.connection_info.is_some() {
            let res = self.execute_live_sql("SELECT 1")?;
            Ok(res.contains('1'))
        } else {
            Ok(true)
        }
    }

    /// Executes a SELECT query against in-memory storage, returning structured `UdmRow` records.
    pub fn query(&self, sql: &str) -> Result<Vec<UdmRow>, String> {
        let upper = sql.to_ascii_uppercase();
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;

        if upper.contains("FROM MODELENTITYVERSION") {
            let model_filter = parse_filter_int(sql, "ModelId");
            let columns = vec![
                "ModelId".to_string(),
                "EntityTypeId".to_string(),
                "EntityId".to_string(),
                "EntityVersionId".to_string(),
                "ModelEntityVersionName".to_string(),
            ];
            let rows = store
                .model_entity_versions
                .iter()
                .filter(|m| model_filter.is_none_or(|mf| m.model_id == mf))
                .map(|m| {
                    UdmRow::new(
                        columns.clone(),
                        vec![
                            UdmValue::Int(m.model_id as i64),
                            UdmValue::Int(m.entity_type_id as i64),
                            UdmValue::Int(m.entity_id as i64),
                            UdmValue::Int(m.entity_version_id as i64),
                            UdmValue::String(m.name.clone()),
                        ],
                    )
                })
                .collect();
            return Ok(rows);
        }

        if upper.contains("FROM ENTITYTYPE") {
            let columns = vec![
                "EntityTypeId".to_string(),
                "EntityTypeName".to_string(),
                "EntityTypeNamespace".to_string(),
            ];
            let rows = store
                .entity_types
                .iter()
                .map(|et| {
                    UdmRow::new(
                        columns.clone(),
                        vec![
                            UdmValue::Int(et.entity_type_id as i64),
                            UdmValue::String(et.name.clone()),
                            UdmValue::String(et.namespace.clone()),
                        ],
                    )
                })
                .collect();
            return Ok(rows);
        }

        Err(format!("Unsupported UDM query table in SQL: {sql}"))
    }

    /// Executes an ad-hoc write statement.
    pub fn execute(&self, sql: &str) -> Result<usize, String> {
        let upper = sql.to_ascii_uppercase();
        if upper.starts_with("DELETE FROM MODELENTITYVERSION") {
            let model_id = parse_filter_int(sql, "ModelId").unwrap_or(0);
            let entity_type = parse_filter_int(sql, "EntityTypeId").unwrap_or(0);
            let entity_id = parse_filter_int(sql, "EntityId").unwrap_or(0);
            return Ok(if self.delete_object(model_id, entity_type, entity_id)? {
                1
            } else {
                0
            });
        }
        Ok(1)
    }

    /// Executes multiple statements in a batch.
    pub fn execute_batch(&self, sqls: &[&str]) -> Result<usize, String> {
        let mut count = 0;
        for sql in sqls {
            count += self.execute(sql)?;
        }
        Ok(count)
    }
}

fn parse_filter_int(sql: &str, col: &str) -> Option<i32> {
    let upper = sql.to_ascii_uppercase();
    let col_upper = col.to_ascii_uppercase();
    if let Some(pos) = upper.find(&col_upper) {
        let rest = &sql[pos + col.len()..];
        let trimmed = rest.trim_start();
        if let Some(after_eq) = trimmed.strip_prefix('=') {
            let num_str: String = after_eq
                .trim_start()
                .chars()
                .take_while(|c| c.is_ascii_digit())
                .collect();
            return num_str.parse::<i32>().ok();
        }
    }
    None
}

fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}
