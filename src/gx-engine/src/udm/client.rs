//! Database client interface and query execution for GeneXus UDM physical storage.
//!
//! Provides `UdmClient`, `UdmRow`, and `UdmValue` supporting query execution, row extraction,
//! transaction boundaries (commit/rollback), in-memory simulation, and live SQL Server /
//! LocalDB execution via `KbConnectionInfo`.

#![forbid(unsafe_code)]

use crate::udm::connection::{AuthMode, KbConnectionInfo};
use std::fmt;
use std::path::Path;
use std::sync::{Arc, RwLock};

/// A typed value in a UDM database row.
#[derive(Debug, Clone, PartialEq)]
pub enum UdmValue {
    Null,
    Int(i64),
    String(String),
    Binary(Vec<u8>),
    Bool(bool),
}

impl UdmValue {
    pub fn as_str(&self) -> Option<&str> {
        match self {
            Self::String(s) => Some(s),
            _ => None,
        }
    }

    pub fn as_i64(&self) -> Option<i64> {
        match self {
            Self::Int(n) => Some(*n),
            _ => None,
        }
    }

    pub fn as_i32(&self) -> Option<i32> {
        self.as_i64().and_then(|n| i32::try_from(n).ok())
    }

    pub fn as_bytes(&self) -> Option<&[u8]> {
        match self {
            Self::Binary(b) => Some(b.as_slice()),
            _ => None,
        }
    }

    pub fn as_bool(&self) -> Option<bool> {
        match self {
            Self::Bool(b) => Some(*b),
            Self::Int(n) => Some(*n != 0),
            _ => None,
        }
    }

    pub fn is_null(&self) -> bool {
        matches!(self, Self::Null)
    }
}

impl fmt::Display for UdmValue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Null => write!(f, "NULL"),
            Self::Int(n) => write!(f, "{n}"),
            Self::String(s) => write!(f, "{s}"),
            Self::Binary(b) => write!(f, "<binary {} bytes>", b.len()),
            Self::Bool(b) => write!(f, "{b}"),
        }
    }
}

/// A row returned from a UDM query, providing column-name and index-based access.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct UdmRow {
    columns: Vec<String>,
    values: Vec<UdmValue>,
}

impl UdmRow {
    pub fn new(columns: Vec<String>, values: Vec<UdmValue>) -> Self {
        Self { columns, values }
    }

    pub fn get(&self, col_name: &str) -> Option<&UdmValue> {
        self.columns
            .iter()
            .position(|c| c.eq_ignore_ascii_case(col_name))
            .and_then(|idx| self.values.get(idx))
    }

    pub fn get_by_index(&self, index: usize) -> Option<&UdmValue> {
        self.values.get(index)
    }

    pub fn get_str(&self, col_name: &str) -> Option<&str> {
        self.get(col_name).and_then(UdmValue::as_str)
    }

    pub fn get_i32(&self, col_name: &str) -> Option<i32> {
        self.get(col_name).and_then(UdmValue::as_i32)
    }

    pub fn get_i64(&self, col_name: &str) -> Option<i64> {
        self.get(col_name).and_then(UdmValue::as_i64)
    }

    pub fn get_bytes(&self, col_name: &str) -> Option<&[u8]> {
        self.get(col_name).and_then(UdmValue::as_bytes)
    }

    pub fn get_bool(&self, col_name: &str) -> Option<bool> {
        self.get(col_name).and_then(UdmValue::as_bool)
    }

    pub fn columns(&self) -> &[String] {
        &self.columns
    }

    pub fn values(&self) -> &[UdmValue] {
        &self.values
    }

    pub fn len(&self) -> usize {
        self.values.len()
    }

    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }
}

/// ModelEntityVersion row: active object version descriptor in a model.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct ModelEntityVersionRecord {
    pub model_id: i32,
    pub entity_type_id: i32,
    pub entity_id: i32,
    pub entity_version_id: i32,
    pub name: String,
}

/// EntityVersionComposition row: parent-to-child component part linkage.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct EntityVersionCompositionRecord {
    pub compound_entity_type_id: i32,
    pub compound_entity_id: i32,
    pub compound_entity_version_id: i32,
    pub component_entity_type_id: i32,
    pub component_entity_id: i32,
    pub component_entity_version_id: i32,
}

/// EntityVersion row: payload envelope blob and metadata.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct EntityVersionRecord {
    pub entity_type_id: i32,
    pub entity_id: i32,
    pub entity_version_id: i32,
    pub data: Vec<u8>,
    pub properties: Option<String>,
}

/// EntityType row: catalog descriptor of object or part types.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct EntityTypeRecord {
    pub entity_type_id: i32,
    pub name: String,
    pub namespace: String,
}

/// ModelCrossReference row: inter-object dependency graph link.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct ModelCrossReferenceRecord {
    pub model_id: i32,
    pub from_entity_type_id: i32,
    pub from_entity_id: i32,
    pub to_entity_type_id: i32,
    pub to_entity_id: i32,
    pub reference_type: i32,
}

/// State row: multi-user and agent lock coordinator in UDM.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct StateLockRecord {
    pub state_name: String,
    pub state_user_name: String,
    pub state_guid: String,
    pub owner_machine_name: String,
}

/// In-memory storage backing the UDM database for simulation and fast tests.
#[derive(Debug, Clone, Default)]
pub struct InMemoryDatabase {
    pub model_entity_versions: Vec<ModelEntityVersionRecord>,
    pub entity_version_compositions: Vec<EntityVersionCompositionRecord>,
    pub entity_versions: Vec<EntityVersionRecord>,
    pub entity_types: Vec<EntityTypeRecord>,
    pub model_cross_references: Vec<ModelCrossReferenceRecord>,
    pub state_locks: Vec<StateLockRecord>,
    pub next_entity_id: i32,
    savepoint: Option<Box<InMemoryDatabase>>,
}

impl InMemoryDatabase {
    pub fn new() -> Self {
        let mut db = Self {
            next_entity_id: 1000,
            ..Default::default()
        };
        db.seed_default_entity_types();
        db
    }

    pub fn seed_default_entity_types(&mut self) {
        if !self.entity_types.is_empty() {
            return;
        }

        let defaults = [
            (1, "Procedure", "Artech.Architecture.Common.Objects"),
            (2, "Transaction", "Artech.Architecture.Common.Objects"),
            (4, "Attribute", "Artech.Architecture.Common.Objects"),
            (5, "Domain", "Artech.Architecture.Common.Objects"),
            (7, "Table", "Artech.Architecture.Common.Objects"),
            (8, "DataView", "Artech.Architecture.Common.Objects"),
            (12, "WebPanel", "Artech.Architecture.Common.Objects"),
            (29, "SDT", "Artech.Architecture.Common.Objects"),
            (33, "DataProvider", "Artech.Architecture.Common.Objects"),
            (37, "DataSelector", "Artech.Architecture.Common.Objects"),
            (38, "Module", "Artech.Architecture.Common.Objects"),
            (45, "DesignSystem", "Artech.Architecture.Common.Objects"),
            // Standard Component Parts
            (100, "Source", "Artech.Genexus.Common.Parts"),
            (102, "Rules", "Artech.Genexus.Common.Parts"),
            (97, "Events", "Artech.Genexus.Common.Parts"),
            (89, "Conditions", "Artech.Genexus.Common.Parts"),
            (105, "Variables", "Artech.Genexus.Common.Parts"),
            (104, "Structure", "Artech.Genexus.Common.Parts"),
            (107, "WebForm", "Artech.Genexus.Common.Parts"),
            (78, "PatternInstance", "Artech.Genexus.Common.Parts"),
        ];

        for (id, name, ns) in defaults {
            self.entity_types.push(EntityTypeRecord {
                entity_type_id: id,
                name: name.to_string(),
                namespace: ns.to_string(),
            });
        }
    }

    pub fn allocate_entity_id(&mut self) -> i32 {
        self.next_entity_id += 1;
        self.next_entity_id
    }

    pub fn begin_transaction(&mut self) {
        let mut snapshot = self.clone();
        snapshot.savepoint = None;
        self.savepoint = Some(Box::new(snapshot));
    }

    pub fn commit_transaction(&mut self) -> Result<(), String> {
        if self.savepoint.take().is_some() {
            Ok(())
        } else {
            Err("No active transaction to commit".to_string())
        }
    }

    pub fn rollback_transaction(&mut self) -> Result<(), String> {
        if let Some(snapshot) = self.savepoint.take() {
            *self = *snapshot;
            Ok(())
        } else {
            Err("No active transaction to rollback".to_string())
        }
    }
}

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

    /// Seeds standard GeneXus entity types if not already populated.
    pub fn seed_default_entity_types(&self) -> Result<(), String> {
        let mut store = self
            .storage
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        store.seed_default_entity_types();
        Ok(())
    }

    /// Retrieves all active objects in a model from `ModelEntityVersion`.
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
                "SET NOCOUNT ON; SELECT ModelId, EntityTypeId, EntityId, EntityVersionId, ModelEntityVersionName FROM ModelEntityVersion WHERE ModelId = {model_id}"
            );
            if let Ok(raw) = self.execute_live_sql(&sql) {
                let mut store = self
                    .storage
                    .write()
                    .map_err(|e| format!("Storage lock poisoned: {e}"))?;
                for line in raw.lines() {
                    let parts: Vec<&str> = line.split('\t').map(|s| s.trim()).collect();
                    if parts.len() >= 5 {
                        if let (Ok(m_id), Ok(t_id), Ok(e_id), Ok(v_id)) = (
                            parts[0].parse::<i32>(),
                            parts[1].parse::<i32>(),
                            parts[2].parse::<i32>(),
                            parts[3].parse::<i32>(),
                        ) {
                            if !store.model_entity_versions.iter().any(|m| {
                                m.model_id == m_id
                                    && m.entity_type_id == t_id
                                    && m.entity_id == e_id
                            }) {
                                store.model_entity_versions.push(ModelEntityVersionRecord {
                                    model_id: m_id,
                                    entity_type_id: t_id,
                                    entity_id: e_id,
                                    entity_version_id: v_id,
                                    name: parts[4].to_string(),
                                });
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

    /// Retrieves all component parts linked to a compound object version.
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
                        if let (Ok(comp_t), Ok(comp_i), Ok(comp_v)) = (
                            parts[0].parse::<i32>(),
                            parts[1].parse::<i32>(),
                            parts[2].parse::<i32>(),
                        ) {
                            let rec = EntityVersionCompositionRecord {
                                compound_entity_type_id: compound_type,
                                compound_entity_id: compound_id,
                                compound_entity_version_id: compound_ver,
                                component_entity_type_id: comp_t,
                                component_entity_id: comp_i,
                                component_entity_version_id: comp_v,
                            };
                            if !store.entity_version_compositions.iter().any(|c| {
                                c.compound_entity_type_id == rec.compound_entity_type_id
                                    && c.compound_entity_id == rec.compound_entity_id
                                    && c.compound_entity_version_id
                                        == rec.compound_entity_version_id
                                    && c.component_entity_type_id == rec.component_entity_type_id
                                    && c.component_entity_id == rec.component_entity_id
                                    && c.component_entity_version_id
                                        == rec.component_entity_version_id
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

    /// Retrieves a specific entity version record.
    pub fn get_entity_version(
        &self,
        type_id: i32,
        id: i32,
        ver_id: i32,
    ) -> Result<Option<EntityVersionRecord>, String> {
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let existing = store
                .entity_versions
                .iter()
                .find(|ev| {
                    ev.entity_type_id == type_id
                        && ev.entity_id == id
                        && ev.entity_version_id == ver_id
                })
                .cloned();
            if existing.is_some() {
                return Ok(existing);
            }
        }

        if self.connection_info.is_some() {
            let sql = format!(
                "SET NOCOUNT ON; SELECT CONVERT(varchar(max), EntityVersionData, 2) FROM EntityVersion WHERE EntityTypeId = {type_id} AND EntityId = {id} AND EntityVersionId = {ver_id}"
            );
            if let Ok(raw) = self.execute_live_sql(&sql) {
                let hex_str = raw.trim();
                let bytes = if !hex_str.is_empty() && hex_str.len() % 2 == 0 {
                    let mut b = Vec::with_capacity(hex_str.len() / 2);
                    let chars: Vec<char> = hex_str.chars().collect();
                    for chunk in chars.chunks(2) {
                        let pair: String = chunk.iter().collect();
                        if let Ok(byte) = u8::from_str_radix(&pair, 16) {
                            b.push(byte);
                        }
                    }
                    Some(b)
                } else {
                    None
                };

                let rec = EntityVersionRecord {
                    entity_type_id: type_id,
                    entity_id: id,
                    entity_version_id: ver_id,
                    data: bytes.unwrap_or_default(),
                    properties: None,
                };
                let mut store = self
                    .storage
                    .write()
                    .map_err(|e| format!("Storage lock poisoned: {e}"))?;
                store.entity_versions.retain(|ev| {
                    !(ev.entity_type_id == type_id
                        && ev.entity_id == id
                        && ev.entity_version_id == ver_id)
                });
                store.entity_versions.push(rec.clone());
                return Ok(Some(rec));
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
                ev.entity_type_id == type_id && ev.entity_id == id && ev.entity_version_id == ver_id
            })
            .cloned())
    }

    /// Retrieves all entity types registered in the database.
    pub fn get_entity_types(&self) -> Result<Vec<EntityTypeRecord>, String> {
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            if !store.entity_types.is_empty() {
                return Ok(store.entity_types.clone());
            }
        }

        if self.connection_info.is_some() {
            let sql = "SET NOCOUNT ON; SELECT EntityTypeId, EntityTypeName FROM EntityType";
            if let Ok(raw) = self.execute_live_sql(sql) {
                let mut store = self
                    .storage
                    .write()
                    .map_err(|e| format!("Storage lock poisoned: {e}"))?;
                for line in raw.lines() {
                    let parts: Vec<&str> = line.split('\t').map(|s| s.trim()).collect();
                    if parts.len() >= 2 {
                        if let Ok(id) = parts[0].parse::<i32>() {
                            if !store.entity_types.iter().any(|t| t.entity_type_id == id) {
                                store.entity_types.push(EntityTypeRecord {
                                    entity_type_id: id,
                                    name: parts[1].to_string(),
                                    namespace: String::new(),
                                });
                            }
                        }
                    }
                }
                return Ok(store.entity_types.clone());
            }
        }

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
        {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let in_memory: Vec<_> = store
                .model_cross_references
                .iter()
                .filter(|xr| {
                    xr.model_id == model_id
                        && xr.from_entity_type_id == from_type_id
                        && xr.from_entity_id == from_id
                })
                .cloned()
                .collect();
            if !in_memory.is_empty() {
                return Ok(in_memory);
            }
        }

        if self.connection_info.is_some() {
            let sql = format!(
                "SET NOCOUNT ON; SELECT ToEntityTypeId, ToEntityId, ReferenceType FROM ModelCrossReference WHERE ModelId = {model_id} AND FromEntityTypeId = {from_type_id} AND FromEntityId = {from_id}"
            );
            if let Ok(raw) = self.execute_live_sql(&sql) {
                let mut store = self
                    .storage
                    .write()
                    .map_err(|e| format!("Storage lock poisoned: {e}"))?;
                for line in raw.lines() {
                    let parts: Vec<&str> = line.split('\t').map(|s| s.trim()).collect();
                    if parts.len() >= 3 {
                        if let (Ok(to_t), Ok(to_i), Ok(r_t)) = (
                            parts[0].parse::<i32>(),
                            parts[1].parse::<i32>(),
                            parts[2].parse::<i32>(),
                        ) {
                            let rec = ModelCrossReferenceRecord {
                                model_id,
                                from_entity_type_id: from_type_id,
                                from_entity_id: from_id,
                                to_entity_type_id: to_t,
                                to_entity_id: to_i,
                                reference_type: r_t,
                            };
                            if !store.model_cross_references.iter().any(|xr| {
                                xr.model_id == rec.model_id
                                    && xr.from_entity_type_id == rec.from_entity_type_id
                                    && xr.from_entity_id == rec.from_entity_id
                                    && xr.to_entity_type_id == rec.to_entity_type_id
                                    && xr.to_entity_id == rec.to_entity_id
                            }) {
                                store.model_cross_references.push(rec);
                            }
                        }
                    }
                }
                return Ok(store
                    .model_cross_references
                    .iter()
                    .filter(|xr| {
                        xr.model_id == model_id
                            && xr.from_entity_type_id == from_type_id
                            && xr.from_entity_id == from_id
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
                // Re-entrant lock by the same session
                return Ok(true);
            }
            // Held by another user/session
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

    /// Releases a lock in the `State` table if held by the given guid.
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

    /// Retrieves active lock details for a specific state name.
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

    /// Lists all current locks in the `State` table.
    pub fn list_locks(&self) -> Result<Vec<StateLockRecord>, String> {
        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        Ok(store.state_locks.clone())
    }

    /// Executes a SQL query against the UDM database, returning tabular rows.
    pub fn query(&self, sql: &str) -> Result<Vec<UdmRow>, String> {
        let trimmed = sql.trim();
        let upper = trimmed.to_ascii_uppercase();

        if !upper.starts_with("SELECT") {
            return Err("UdmClient::query requires a SELECT statement".to_string());
        }

        let store = self
            .storage
            .read()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;

        if upper.contains("FROM MODELENTITYVERSION") {
            let model_filter = extract_where_int(trimmed, "ModelId");
            let mut rows = Vec::new();
            let columns = vec![
                "ModelId".to_string(),
                "EntityTypeId".to_string(),
                "EntityId".to_string(),
                "EntityVersionId".to_string(),
                "ModelEntityVersionName".to_string(),
            ];

            for rec in &store.model_entity_versions {
                if let Some(mid) = model_filter {
                    if rec.model_id != mid {
                        continue;
                    }
                }
                rows.push(UdmRow::new(
                    columns.clone(),
                    vec![
                        UdmValue::Int(rec.model_id as i64),
                        UdmValue::Int(rec.entity_type_id as i64),
                        UdmValue::Int(rec.entity_id as i64),
                        UdmValue::Int(rec.entity_version_id as i64),
                        UdmValue::String(rec.name.clone()),
                    ],
                ));
            }
            return Ok(rows);
        }

        if upper.contains("FROM ENTITYVERSIONCOMPOSITION") {
            let compound_type = extract_where_int(trimmed, "CompoundEntityTypeId");
            let compound_id = extract_where_int(trimmed, "CompoundEntityId");
            let compound_ver = extract_where_int(trimmed, "CompoundEntityVersionId");

            let mut rows = Vec::new();
            let columns = vec![
                "CompoundEntityTypeId".to_string(),
                "CompoundEntityId".to_string(),
                "CompoundEntityVersionId".to_string(),
                "ComponentEntityTypeId".to_string(),
                "ComponentEntityId".to_string(),
                "ComponentEntityVersionId".to_string(),
            ];

            for rec in &store.entity_version_compositions {
                if let Some(ct) = compound_type {
                    if rec.compound_entity_type_id != ct {
                        continue;
                    }
                }
                if let Some(ci) = compound_id {
                    if rec.compound_entity_id != ci {
                        continue;
                    }
                }
                if let Some(cv) = compound_ver {
                    if rec.compound_entity_version_id != cv {
                        continue;
                    }
                }
                rows.push(UdmRow::new(
                    columns.clone(),
                    vec![
                        UdmValue::Int(rec.compound_entity_type_id as i64),
                        UdmValue::Int(rec.compound_entity_id as i64),
                        UdmValue::Int(rec.compound_entity_version_id as i64),
                        UdmValue::Int(rec.component_entity_type_id as i64),
                        UdmValue::Int(rec.component_entity_id as i64),
                        UdmValue::Int(rec.component_entity_version_id as i64),
                    ],
                ));
            }
            return Ok(rows);
        }

        if upper.contains("FROM ENTITYVERSION") {
            let entity_type = extract_where_int(trimmed, "EntityTypeId");
            let entity_id = extract_where_int(trimmed, "EntityId");
            let version_id = extract_where_int(trimmed, "EntityVersionId");

            let mut rows = Vec::new();
            let columns = vec![
                "EntityTypeId".to_string(),
                "EntityId".to_string(),
                "EntityVersionId".to_string(),
                "EntityVersionData".to_string(),
                "EntityVersionProperties".to_string(),
            ];

            for rec in &store.entity_versions {
                if let Some(et) = entity_type {
                    if rec.entity_type_id != et {
                        continue;
                    }
                }
                if let Some(ei) = entity_id {
                    if rec.entity_id != ei {
                        continue;
                    }
                }
                if let Some(vi) = version_id {
                    if rec.entity_version_id != vi {
                        continue;
                    }
                }

                rows.push(UdmRow::new(
                    columns.clone(),
                    vec![
                        UdmValue::Int(rec.entity_type_id as i64),
                        UdmValue::Int(rec.entity_id as i64),
                        UdmValue::Int(rec.entity_version_id as i64),
                        UdmValue::Binary(rec.data.clone()),
                        rec.properties
                            .as_ref()
                            .map_or(UdmValue::Null, |p| UdmValue::String(p.clone())),
                    ],
                ));
            }
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

        if upper.contains("FROM MODELCROSSREFERENCE") {
            let model_filter = extract_where_int(trimmed, "ModelId");
            let from_type = extract_where_int(trimmed, "FromEntityTypeId");
            let from_id = extract_where_int(trimmed, "FromEntityId");

            let mut rows = Vec::new();
            let columns = vec![
                "ModelId".to_string(),
                "FromEntityTypeId".to_string(),
                "FromEntityId".to_string(),
                "ToEntityTypeId".to_string(),
                "ToEntityId".to_string(),
                "ReferenceType".to_string(),
            ];

            for rec in &store.model_cross_references {
                if let Some(mid) = model_filter {
                    if rec.model_id != mid {
                        continue;
                    }
                }
                if let Some(ft) = from_type {
                    if rec.from_entity_type_id != ft {
                        continue;
                    }
                }
                if let Some(fi) = from_id {
                    if rec.from_entity_id != fi {
                        continue;
                    }
                }

                rows.push(UdmRow::new(
                    columns.clone(),
                    vec![
                        UdmValue::Int(rec.model_id as i64),
                        UdmValue::Int(rec.from_entity_type_id as i64),
                        UdmValue::Int(rec.from_entity_id as i64),
                        UdmValue::Int(rec.to_entity_type_id as i64),
                        UdmValue::Int(rec.to_entity_id as i64),
                        UdmValue::Int(rec.reference_type as i64),
                    ],
                ));
            }
            return Ok(rows);
        }

        if upper.contains("FROM STATE") {
            let name_filter = extract_where_string(trimmed, "StateName");
            let mut rows = Vec::new();
            let columns = vec![
                "StateName".to_string(),
                "StateUserName".to_string(),
                "StateGuid".to_string(),
                "OwnerMachineName".to_string(),
            ];

            for rec in &store.state_locks {
                if let Some(ref name) = name_filter {
                    if !rec.state_name.eq_ignore_ascii_case(name) {
                        continue;
                    }
                }
                rows.push(UdmRow::new(
                    columns.clone(),
                    vec![
                        UdmValue::String(rec.state_name.clone()),
                        UdmValue::String(rec.state_user_name.clone()),
                        UdmValue::String(rec.state_guid.clone()),
                        UdmValue::String(rec.owner_machine_name.clone()),
                    ],
                ));
            }
            return Ok(rows);
        }

        Err(format!("Unsupported UDM query table in SQL: {sql}"))
    }

    /// Executes a SQL DML command (INSERT, UPDATE, DELETE) against the UDM database.
    pub fn execute(&self, sql: &str) -> Result<u64, String> {
        let trimmed = sql.trim();
        let upper = trimmed.to_ascii_uppercase();

        if upper.starts_with("INSERT INTO STATE") {
            // Extract VALUES ('StateName', 'UserName', 'Guid', 'Machine')
            if let Some(values) = extract_parenthesized_values(trimmed) {
                if values.len() >= 4 {
                    let acquired =
                        self.acquire_lock(&values[0], &values[1], &values[2], &values[3])?;
                    return if acquired { Ok(1) } else { Ok(0) };
                }
            }
            return Err("Malformed INSERT INTO State statement".to_string());
        }

        if upper.starts_with("DELETE FROM STATE") {
            let name_filter = extract_where_string(trimmed, "StateName");
            let guid_filter = extract_where_string(trimmed, "StateGuid");

            if let Some(name) = name_filter {
                let guid = guid_filter.unwrap_or_default();
                let mut store = self
                    .storage
                    .write()
                    .map_err(|e| format!("Storage lock poisoned: {e}"))?;
                let before = store.state_locks.len();
                store.state_locks.retain(|l| {
                    if !l.state_name.eq_ignore_ascii_case(&name) {
                        return true;
                    }
                    if !guid.is_empty() && l.state_guid != guid {
                        return true;
                    }
                    false
                });
                return Ok((before - store.state_locks.len()) as u64);
            }
        }

        if upper.starts_with("DELETE FROM MODELENTITYVERSION") {
            let model_id = extract_where_int(trimmed, "ModelId").unwrap_or(1);
            let entity_type_id = extract_where_int(trimmed, "EntityTypeId").unwrap_or(0);
            let entity_id = extract_where_int(trimmed, "EntityId").unwrap_or(0);

            let deleted = self.delete_object(model_id, entity_type_id, entity_id)?;
            return Ok(if deleted { 1 } else { 0 });
        }

        Err(format!("Unsupported UDM execute command in SQL: {sql}"))
    }

    /// Executes multiple statements in sequence.
    pub fn execute_batch(&self, statements: &[&str]) -> Result<u64, String> {
        let mut total = 0;
        for stmt in statements {
            total += self.execute(stmt)?;
        }
        Ok(total)
    }

    /// Executes raw SQL against a live SQL Server or LocalDB instance using `sqlcmd`.
    pub fn execute_live_sql(&self, sql: &str) -> Result<String, String> {
        let info = self
            .connection_info
            .as_ref()
            .ok_or_else(|| "No connection info configured for live SQL execution".to_string())?;

        let mut cmd = std::process::Command::new("sqlcmd");
        cmd.arg("-S").arg(&info.server_instance);
        cmd.arg("-d").arg(&info.db_name);
        cmd.arg("-b"); // on error batch abort
        cmd.arg("-W"); // remove trailing whitespace
        cmd.arg("-s").arg("\t"); // tab delimited

        match &info.auth_mode {
            AuthMode::Integrated => {
                cmd.arg("-E");
            }
            AuthMode::SqlServer { user, password } => {
                cmd.arg("-U").arg(user);
                if let Some(p) = password {
                    cmd.arg("-P").arg(p);
                }
            }
        }

        cmd.arg("-Q").arg(sql);

        let output = cmd
            .output()
            .map_err(|e| format!("Failed to invoke sqlcmd: {e}"))?;

        if !output.status.success() {
            let stderr = String::from_utf8_lossy(&output.stderr);
            let stdout = String::from_utf8_lossy(&output.stdout);
            return Err(format!(
                "sqlcmd execution failed with exit code {:?}: {stderr}\n{stdout}",
                output.status.code()
            ));
        }

        let result = String::from_utf8_lossy(&output.stdout).to_string();
        Ok(result)
    }

    /// Tests the connection to the UDM database.
    pub fn test_connection(&self) -> Result<bool, String> {
        if self.connection_info.is_some() {
            match self.execute_live_sql("SELECT 1") {
                Ok(_) => Ok(true),
                Err(err) => Err(format!("Connection test failed: {err}")),
            }
        } else {
            Ok(true)
        }
    }
}

// Helpers for SQL string parsing in the client

fn extract_where_int(sql: &str, column: &str) -> Option<i32> {
    let lower_sql = sql.to_ascii_lowercase();
    let lower_col = column.to_ascii_lowercase();
    let pattern = format!("{lower_col} =");
    let pattern_alt = format!("{lower_col}=");

    let pos = lower_sql
        .find(&pattern)
        .map(|p| p + pattern.len())
        .or_else(|| lower_sql.find(&pattern_alt).map(|p| p + pattern_alt.len()))?;

    let rest = &sql[pos..].trim_start();
    let num_str: String = rest
        .chars()
        .take_while(|c| c.is_ascii_digit() || *c == '-')
        .collect();
    num_str.parse::<i32>().ok()
}

fn extract_where_string(sql: &str, column: &str) -> Option<String> {
    let lower_sql = sql.to_ascii_lowercase();
    let lower_col = column.to_ascii_lowercase();
    let pattern = format!("{lower_col} =");
    let pattern_alt = format!("{lower_col}=");

    let pos = lower_sql
        .find(&pattern)
        .map(|p| p + pattern.len())
        .or_else(|| lower_sql.find(&pattern_alt).map(|p| p + pattern_alt.len()))?;

    let rest = sql[pos..].trim_start();
    if let Some(inside) = rest.strip_prefix('\'') {
        inside.find('\'').map(|end| inside[..end].to_string())
    } else {
        let token: String = rest
            .chars()
            .take_while(|c| !c.is_whitespace() && *c != ';' && *c != ')')
            .collect();
        if token.is_empty() { None } else { Some(token) }
    }
}

fn extract_parenthesized_values(sql: &str) -> Option<Vec<String>> {
    let open_idx = sql.find('(')?;
    let close_idx = sql.rfind(')')?;
    if close_idx <= open_idx {
        return None;
    }

    let inner = &sql[open_idx + 1..close_idx];
    let mut values = Vec::new();
    let mut current = String::new();
    let mut in_quote = false;

    for ch in inner.chars() {
        if ch == '\'' {
            in_quote = !in_quote;
        } else if ch == ',' && !in_quote {
            values.push(current.trim().trim_matches('\'').to_string());
            current.clear();
        } else {
            current.push(ch);
        }
    }

    if !current.trim().is_empty() {
        values.push(current.trim().trim_matches('\'').to_string());
    }

    Some(values)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn in_memory_client_crud_and_query() {
        let client = UdmClient::new_in_memory();

        // 1. Insert ModelEntityVersion
        let rec = ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: 1, // Procedure
            entity_id: 101,
            entity_version_id: 1,
            name: "ProcMonthlyBilling".to_string(),
        };
        client
            .insert_model_entity_version(rec.clone())
            .expect("insert version");

        // 2. Query via SQL
        let rows = client
            .query("SELECT * FROM ModelEntityVersion WHERE ModelId = 1")
            .expect("query model entity");
        assert_eq!(rows.len(), 1);
        let row = &rows[0];
        assert_eq!(row.get_i32("ModelId"), Some(1));
        assert_eq!(row.get_i32("EntityTypeId"), Some(1));
        assert_eq!(row.get_i32("EntityId"), Some(101));
        assert_eq!(row.get_i32("EntityVersionId"), Some(1));
        assert_eq!(
            row.get_str("ModelEntityVersionName"),
            Some("ProcMonthlyBilling")
        );

        // 3. Update ModelEntityVersion
        client
            .update_model_entity_version(1, 1, 101, 2, Some("ProcMonthlyBillingV2"))
            .expect("update version");

        let active = client.get_active_objects(1).expect("get active objects");
        assert_eq!(active.len(), 1);
        assert_eq!(active[0].entity_version_id, 2);
        assert_eq!(active[0].name, "ProcMonthlyBillingV2");
    }

    #[test]
    fn transactional_rollback_restores_state() {
        let client = UdmClient::new_in_memory();

        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 1,
                entity_id: 50,
                entity_version_id: 1,
                name: "OriginalProc".to_string(),
            })
            .unwrap();

        // Begin transaction and mutate
        client.begin_transaction().expect("begin transaction");
        client
            .insert_model_entity_version(ModelEntityVersionRecord {
                model_id: 1,
                entity_type_id: 1,
                entity_id: 51,
                entity_version_id: 1,
                name: "TransientProc".to_string(),
            })
            .unwrap();

        assert_eq!(client.get_active_objects(1).unwrap().len(), 2);

        // Rollback
        client.rollback_transaction().expect("rollback transaction");
        let after_rollback = client.get_active_objects(1).unwrap();
        assert_eq!(after_rollback.len(), 1);
        assert_eq!(after_rollback[0].name, "OriginalProc");
    }

    #[test]
    fn state_lock_acquisition_and_release() {
        let client = UdmClient::new_in_memory();

        // Acquire lock
        let acquired = client
            .acquire_lock("Invoice", "agent_alpha", "guid-111", "HOST1")
            .expect("acquire lock");
        assert!(acquired);

        // Conflict check
        let second_attempt = client
            .acquire_lock("Invoice", "agent_beta", "guid-222", "HOST2")
            .expect("second attempt");
        assert!(!second_attempt, "Lock conflict should fail acquisition");

        // Re-entrant by same guid
        let reentrant = client
            .acquire_lock("Invoice", "agent_alpha", "guid-111", "HOST1")
            .expect("reentrant");
        assert!(reentrant);

        // Release lock
        let released = client
            .release_lock("Invoice", "guid-111")
            .expect("release lock");
        assert!(released);

        // Acquire after release
        let acquired_now = client
            .acquire_lock("Invoice", "agent_beta", "guid-222", "HOST2")
            .expect("acquire after release");
        assert!(acquired_now);
    }
}
