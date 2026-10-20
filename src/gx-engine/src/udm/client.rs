//! Database client interface and query execution for GeneXus UDM physical storage.
//!
//! Provides `UdmClient` coordinating in-memory mock storage and live SQL Server /
//! LocalDB execution.

#![forbid(unsafe_code)]

use crate::udm::connection::KbConnectionInfo;
use crate::udm::entity_types::EntityTypeResolver;
use crate::udm::library::ModelParentRecord;
use crate::udm::live;
pub use crate::udm::mock::{
    EntityTypeRecord, EntityVersionCompositionRecord, EntityVersionRecord, InMemoryDatabase,
    ModelCrossReferenceRecord, ModelEntityVersionRecord, StateLockRecord,
};
pub use crate::udm::row::{UdmRow, UdmValue};

use std::collections::BTreeMap;
use std::path::Path;
use std::sync::{Arc, Mutex, OnceLock, RwLock};

/// GUID GeneXus gives the Knowledge Base root module (`Entity.EntityGuid` of the Module
/// with `EntityId` 1). It is the same in the GeneXus 16, 17 and 18 KBs checked.
pub const SYSTEM_ROOT_MODULE_GUID: &str = "afa47377-41d5-4ae8-9755-6f53150aa361";

/// Keys whose compositions and `EntityVersion` parts were read set-wise for a model.
/// A key listed here is answered from memory: the bulk read returned every database row
/// for it, so a row missing from memory is missing from the database as well.
#[derive(Debug, Default)]
struct BulkCoverage {
    /// Compound keys whose `EntityVersionComposition` rows are all in memory.
    compositions: std::collections::HashSet<(i32, i32, i32)>,
    /// `EntityVersion` keys whose rows are all in memory, present or absent.
    entity_versions: std::collections::HashSet<(i32, i32, i32)>,
    /// The latest version id of the Knowledge Base root entity, once read (`None` inside: no version).
    root_version: Option<Option<i32>>,
}

/// Unified Data Model (UDM) physical storage client.
#[derive(Debug, Clone)]
pub struct UdmClient {
    connection_info: Option<KbConnectionInfo>,
    storage: Arc<RwLock<InMemoryDatabase>>,
    /// The served vocabulary of a live Knowledge Base, read from its own
    /// `EntityType` table once and shared by every clone of this client.
    live_vocabulary: Arc<OnceLock<Arc<EntityTypeResolver>>>,
    /// Keys answered from memory after a set-wise read; any write clears it.
    bulk: Arc<RwLock<BulkCoverage>>,
    read_session: Option<Arc<Mutex<Option<live::ReadSession>>>>,
}

impl UdmClient {
    /// Creates a purely in-memory UDM client for testing, emulation, and fast verification.
    pub fn new_in_memory() -> Self {
        Self {
            connection_info: None,
            storage: Arc::new(RwLock::new(InMemoryDatabase::new())),
            live_vocabulary: Arc::new(OnceLock::new()),
            bulk: Arc::new(RwLock::new(BulkCoverage::default())),
            read_session: None,
        }
    }

    /// Creates an in-memory client whose `EntityType` table is `entity_types`,
    /// for example the GeneXus 16 numbering of a Knowledge Base.
    pub fn new_in_memory_with_entity_types(entity_types: Vec<EntityTypeRecord>) -> Self {
        let mut database = InMemoryDatabase::without_vocabulary();
        database.entity_types = entity_types;
        Self {
            connection_info: None,
            storage: Arc::new(RwLock::new(database)),
            live_vocabulary: Arc::new(OnceLock::new()),
            bulk: Arc::new(RwLock::new(BulkCoverage::default())),
            read_session: None,
        }
    }

    /// Creates a UDM client configured with connection info pointing to a SQL Server or LocalDB instance.
    /// Its vocabulary comes from the Knowledge Base itself, never from seeds.
    pub fn from_connection_info(info: KbConnectionInfo) -> Self {
        Self {
            connection_info: Some(info),
            storage: Arc::new(RwLock::new(InMemoryDatabase::without_vocabulary())),
            live_vocabulary: Arc::new(OnceLock::new()),
            bulk: Arc::new(RwLock::new(BulkCoverage::default())),
            read_session: None,
        }
    }

    /// Creates a UDM client connected to a Knowledge Base directory containing `knowledgebase.connection`.
    pub fn connect_kb<P: AsRef<Path>>(kb_dir: P) -> Result<Self, String> {
        let info = KbConnectionInfo::from_kb_dir(kb_dir)?;
        Ok(Self::from_connection_info(info))
    }

    /// Reuses one subprocess connection for this reader's generated SELECT batches.
    /// The connection is shared only with clones of this client; writes stay one-shot.
    pub fn with_persistent_reads(mut self) -> Self {
        self.read_session = Some(Arc::new(Mutex::new(None)));
        self
    }

    /// Returns the target `KbConnectionInfo`, if configured.
    pub fn connection_info(&self) -> Option<&KbConnectionInfo> {
        self.connection_info.as_ref()
    }

    /// Returns `true` when reads go through a live SQL Server / LocalDB
    /// connection instead of the in-memory store alone.
    pub fn is_live(&self) -> bool {
        self.connection_info.is_some()
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
        {
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            store.rollback_transaction()?;
        }
        // The rollback restores the snapshot taken at begin. Rows a prefetch loaded inside the
        // transaction are gone from memory now, so their coverage must go too (begin and commit
        // leave the rows in place, so they keep the coverage).
        self.clear_bulk_coverage();
        Ok(())
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

    /// Reads, with three set-based queries, the compositions of every object of `model_id`
    /// and the `EntityVersion` parts those objects and compositions reach, then marks those
    /// keys covered. Hydrating a Knowledge Base otherwise issued one `sqlcmd` process per
    /// object part. An in-memory client has no database to read, so this does nothing.
    pub fn prefetch_model_parts(&self, model_id: i32) -> Result<(), String> {
        if self.connection_info.is_none() {
            return Ok(());
        }
        let objects: Vec<(i32, i32, i32)> = self
            .get_active_objects(model_id)?
            .iter()
            .map(|m| (m.entity_type_id, m.entity_id, m.entity_version_id))
            .collect();

        let compositions_sql = format!(
            "SET NOCOUNT ON; SELECT c.CompoundEntityTypeId, c.CompoundEntityId, c.CompoundEntityVersionId, c.ComponentEntityTypeId, c.ComponentEntityId, c.ComponentEntityVersionId FROM EntityVersionComposition c JOIN ModelEntityVersion m ON m.EntityTypeId = c.CompoundEntityTypeId AND m.EntityId = c.CompoundEntityId AND m.EntityVersionId = c.CompoundEntityVersionId WHERE m.ModelId = {model_id}"
        );
        let raw = self.execute_live_sql(&compositions_sql)?;
        let mut compositions = Vec::new();
        for row in parse_live_rows(&raw, 6)? {
            compositions.push(EntityVersionCompositionRecord {
                compound_entity_type_id: parse_live_int(row[0], "CompoundEntityTypeId")?,
                compound_entity_id: parse_live_int(row[1], "CompoundEntityId")?,
                compound_entity_version_id: parse_live_int(row[2], "CompoundEntityVersionId")?,
                component_entity_type_id: parse_live_int(row[3], "ComponentEntityTypeId")?,
                component_entity_id: parse_live_int(row[4], "ComponentEntityId")?,
                component_entity_version_id: parse_live_int(row[5], "ComponentEntityVersionId")?,
            });
        }
        let components: Vec<(i32, i32, i32)> = compositions
            .iter()
            .map(|c| {
                (
                    c.component_entity_type_id,
                    c.component_entity_id,
                    c.component_entity_version_id,
                )
            })
            .collect();

        // The parts are the bulk of a catalog read (tens of MB of hex): read in shards on parallel
        // sqlcmd sessions, each decoded on its own thread, then put back in key order.
        let parts_sql = |shard: usize| {
            format!(
                "SET NOCOUNT ON; SELECT e.EntityTypeId, e.EntityId, e.EntityVersionId, ISNULL(DATALENGTH(e.EntityVersionData), 0), CONVERT(VARCHAR(MAX), e.EntityVersionData, 2), ISNULL(DATALENGTH(e.EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST(e.EntityVersionProperties AS VARBINARY(MAX)), 2) FROM EntityVersion e WHERE (EXISTS (SELECT 1 FROM ModelEntityVersion m WHERE m.ModelId = {model_id} AND m.EntityTypeId = e.EntityTypeId AND m.EntityId = e.EntityId AND m.EntityVersionId = e.EntityVersionId) OR EXISTS (SELECT 1 FROM EntityVersionComposition c JOIN ModelEntityVersion m ON m.EntityTypeId = c.CompoundEntityTypeId AND m.EntityId = c.CompoundEntityId AND m.EntityVersionId = c.CompoundEntityVersionId WHERE m.ModelId = {model_id} AND c.ComponentEntityTypeId = e.EntityTypeId AND c.ComponentEntityId = e.EntityId AND c.ComponentEntityVersionId = e.EntityVersionId)) AND e.EntityId % {PART_READ_SHARDS} = {shard}"
            )
        };
        let read_shard = |shard: usize, sql: String| -> Result<Vec<EntityVersionRecord>, String> {
            let raw = match (shard, &self.connection_info, &self.read_session) {
                (0, _, _) | (_, None, _) => self.execute_live_sql(&sql)?,
                (_, Some(info), Some(_)) => live::ReadSession::start(info)?.query(&sql)?,
                (_, Some(info), None) => live::execute_sqlcmd(info, &sql)?,
            };
            parse_live_rows(&raw, 7)?
                .into_iter()
                .map(|row| {
                    entity_version_from_fields(
                        parse_live_int(row[0], "EntityTypeId")?,
                        parse_live_int(row[1], "EntityId")?,
                        parse_live_int(row[2], "EntityVersionId")?,
                        row[3],
                        row[4],
                        row[5],
                        row[6],
                    )
                })
                .collect()
        };
        let mut parts = std::thread::scope(|scope| -> Result<Vec<EntityVersionRecord>, String> {
            let shards: Vec<_> = (0..PART_READ_SHARDS)
                .map(|shard| {
                    let sql = parts_sql(shard);
                    scope.spawn(move || read_shard(shard, sql))
                })
                .collect();
            let mut parts = Vec::new();
            for shard in shards {
                parts.extend(
                    shard
                        .join()
                        .map_err(|_| "a part reader thread panicked".to_string())??,
                );
            }
            Ok(parts)
        })?;
        parts.sort_by_key(|part| (part.entity_type_id, part.entity_id, part.entity_version_id));

        {
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let mut known_compositions: std::collections::HashSet<(i32, i32, i32, i32, i32, i32)> =
                store
                    .entity_version_compositions
                    .iter()
                    .map(|c| {
                        (
                            c.compound_entity_type_id,
                            c.compound_entity_id,
                            c.compound_entity_version_id,
                            c.component_entity_type_id,
                            c.component_entity_id,
                            c.component_entity_version_id,
                        )
                    })
                    .collect();
            for rec in compositions {
                let key = (
                    rec.compound_entity_type_id,
                    rec.compound_entity_id,
                    rec.compound_entity_version_id,
                    rec.component_entity_type_id,
                    rec.component_entity_id,
                    rec.component_entity_version_id,
                );
                if known_compositions.insert(key) {
                    store.entity_version_compositions.push(rec);
                }
            }
            let mut known_parts: std::collections::HashSet<(i32, i32, i32)> = store
                .entity_versions
                .iter()
                .map(|ev| (ev.entity_type_id, ev.entity_id, ev.entity_version_id))
                .collect();
            for rec in parts {
                let key = (rec.entity_type_id, rec.entity_id, rec.entity_version_id);
                if known_parts.insert(key) {
                    store.entity_versions.push(rec);
                }
            }
        }

        let mut bulk = self
            .bulk
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        bulk.compositions.extend(objects.iter().copied());
        bulk.entity_versions.extend(objects.iter().copied());
        bulk.entity_versions.extend(components);
        Ok(())
    }

    /// The distinct keys of `keys`, sorted, as the set-based prefetch takes them.
    fn distinct_keys(keys: &[(i32, i32, i32)]) -> Vec<(i32, i32, i32)> {
        keys.iter()
            .copied()
            .collect::<std::collections::BTreeSet<_>>()
            .into_iter()
            .collect()
    }

    /// The `(t, i, v)` rows of a `VALUES` list for one chunk of keys. Every value is an `i32`, so no text
    /// is injected.
    fn values_list(chunk: &[(i32, i32, i32)]) -> String {
        chunk
            .iter()
            .map(|(t, i, v)| format!("({t}, {i}, {v})"))
            .collect::<Vec<_>>()
            .join(", ")
    }

    /// Reads, with set-based queries, the compositions of the given compounds and the `EntityVersion` rows of
    /// the given keys and of their components, then marks exactly those keys covered: a key with no row is
    /// covered as absent. The cost follows the keys passed, not the model. A key that is not passed still reads
    /// live, so an incomplete call is slower, never wrong. An in-memory client has no database and does nothing.
    pub fn prefetch_keys(
        &self,
        compounds: &[(i32, i32, i32)],
        parts: &[(i32, i32, i32)],
    ) -> Result<(), String> {
        if self.connection_info.is_none() {
            return Ok(());
        }
        // A `VALUES` list takes at most 1000 rows; a chunk stays well below that.
        const KEYS_PER_QUERY: usize = 500;
        let compounds = Self::distinct_keys(compounds);
        let mut compositions = Vec::new();
        for chunk in compounds.chunks(KEYS_PER_QUERY) {
            let sql = format!(
                "SET NOCOUNT ON; SELECT c.CompoundEntityTypeId, c.CompoundEntityId, c.CompoundEntityVersionId, c.ComponentEntityTypeId, c.ComponentEntityId, c.ComponentEntityVersionId FROM EntityVersionComposition c JOIN (VALUES {}) AS k(t, i, v) ON c.CompoundEntityTypeId = k.t AND c.CompoundEntityId = k.i AND c.CompoundEntityVersionId = k.v",
                Self::values_list(chunk)
            );
            let raw = self.execute_live_sql(&sql)?;
            for row in parse_live_rows(&raw, 6)? {
                compositions.push(EntityVersionCompositionRecord {
                    compound_entity_type_id: parse_live_int(row[0], "CompoundEntityTypeId")?,
                    compound_entity_id: parse_live_int(row[1], "CompoundEntityId")?,
                    compound_entity_version_id: parse_live_int(row[2], "CompoundEntityVersionId")?,
                    component_entity_type_id: parse_live_int(row[3], "ComponentEntityTypeId")?,
                    component_entity_id: parse_live_int(row[4], "ComponentEntityId")?,
                    component_entity_version_id: parse_live_int(
                        row[5],
                        "ComponentEntityVersionId",
                    )?,
                });
            }
        }
        let mut version_keys: Vec<(i32, i32, i32)> = parts.to_vec();
        version_keys.extend(compositions.iter().map(|c| {
            (
                c.component_entity_type_id,
                c.component_entity_id,
                c.component_entity_version_id,
            )
        }));
        let version_keys = Self::distinct_keys(&version_keys);
        let mut versions = Vec::new();
        for chunk in version_keys.chunks(KEYS_PER_QUERY) {
            let sql = format!(
                "SET NOCOUNT ON; SELECT e.EntityTypeId, e.EntityId, e.EntityVersionId, ISNULL(DATALENGTH(e.EntityVersionData), 0), CONVERT(VARCHAR(MAX), e.EntityVersionData, 2), ISNULL(DATALENGTH(e.EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST(e.EntityVersionProperties AS VARBINARY(MAX)), 2) FROM EntityVersion e JOIN (VALUES {}) AS k(t, i, v) ON e.EntityTypeId = k.t AND e.EntityId = k.i AND e.EntityVersionId = k.v",
                Self::values_list(chunk)
            );
            let raw = self.execute_live_sql(&sql)?;
            for row in parse_live_rows(&raw, 7)? {
                versions.push(entity_version_from_fields(
                    parse_live_int(row[0], "EntityTypeId")?,
                    parse_live_int(row[1], "EntityId")?,
                    parse_live_int(row[2], "EntityVersionId")?,
                    row[3],
                    row[4],
                    row[5],
                    row[6],
                )?);
            }
        }

        {
            let mut store = self
                .storage
                .write()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            let mut known_compositions: std::collections::HashSet<(i32, i32, i32, i32, i32, i32)> =
                store
                    .entity_version_compositions
                    .iter()
                    .map(|c| {
                        (
                            c.compound_entity_type_id,
                            c.compound_entity_id,
                            c.compound_entity_version_id,
                            c.component_entity_type_id,
                            c.component_entity_id,
                            c.component_entity_version_id,
                        )
                    })
                    .collect();
            for rec in compositions {
                let key = (
                    rec.compound_entity_type_id,
                    rec.compound_entity_id,
                    rec.compound_entity_version_id,
                    rec.component_entity_type_id,
                    rec.component_entity_id,
                    rec.component_entity_version_id,
                );
                if known_compositions.insert(key) {
                    store.entity_version_compositions.push(rec);
                }
            }
            let mut known_parts: std::collections::HashSet<(i32, i32, i32)> = store
                .entity_versions
                .iter()
                .map(|ev| (ev.entity_type_id, ev.entity_id, ev.entity_version_id))
                .collect();
            for rec in versions {
                let key = (rec.entity_type_id, rec.entity_id, rec.entity_version_id);
                if known_parts.insert(key) {
                    store.entity_versions.push(rec);
                }
            }
        }

        let mut bulk = self
            .bulk
            .write()
            .map_err(|e| format!("Storage lock poisoned: {e}"))?;
        bulk.compositions.extend(compounds);
        bulk.entity_versions.extend(version_keys);
        Ok(())
    }

    /// Whether the compositions of this compound are answered from memory.
    fn composition_is_covered(
        &self,
        compound_type: i32,
        compound_id: i32,
        compound_ver: i32,
    ) -> bool {
        self.bulk.read().is_ok_and(|bulk| {
            bulk.compositions
                .contains(&(compound_type, compound_id, compound_ver))
        })
    }

    /// Whether this `EntityVersion` key is answered from memory, present or absent.
    fn entity_version_is_covered(&self, entity_type: i32, entity_id: i32, version_id: i32) -> bool {
        self.bulk.read().is_ok_and(|bulk| {
            bulk.entity_versions
                .contains(&(entity_type, entity_id, version_id))
        })
    }

    /// Drops the set-wise coverage after a write, so later reads go to the database again.
    fn clear_bulk_coverage(&self) {
        let mut bulk = self
            .bulk
            .write()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        *bulk = BulkCoverage::default();
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

    /// The name of this Knowledge Base: the `Name` of its root entity (EntityType 9, EntityId 1), at the
    /// latest stored version. The root is not a model object, so it is read by its entity id. `None` when no
    /// version of it is stored or it has no `Name`.
    pub fn knowledge_base_name(&self) -> Result<Option<String>, String> {
        const KB_ENTITY_TYPE: i32 = 9;
        const KB_ENTITY_ID: i32 = 1;
        let version = if self.is_live() {
            let cached = self
                .bulk
                .read()
                .map_err(|e| format!("Bulk coverage lock poisoned: {e}"))?
                .root_version;
            if let Some(known) = cached {
                match known {
                    Some(version) => version,
                    None => return Ok(None),
                }
            } else {
                let sql = format!(
                    "SET NOCOUNT ON; SELECT EntityVersionId FROM EntityVersion WHERE EntityTypeId = {KB_ENTITY_TYPE} AND EntityId = {KB_ENTITY_ID} ORDER BY EntityVersionId DESC"
                );
                let raw = self.execute_live_sql(&sql)?;
                let rows = parse_live_rows(&raw, 1)?;
                let latest = match rows.first() {
                    Some(row) => Some(parse_live_int(row[0], "EntityVersionId")?),
                    None => None,
                };
                self.bulk
                    .write()
                    .map_err(|e| format!("Bulk coverage lock poisoned: {e}"))?
                    .root_version = Some(latest);
                match latest {
                    Some(version) => version,
                    None => return Ok(None),
                }
            }
        } else {
            let store = self
                .storage
                .read()
                .map_err(|e| format!("Storage lock poisoned: {e}"))?;
            match store
                .entity_versions
                .iter()
                .filter(|ev| ev.entity_type_id == KB_ENTITY_TYPE && ev.entity_id == KB_ENTITY_ID)
                .map(|ev| ev.entity_version_id)
                .max()
            {
                Some(version) => version,
                None => return Ok(None),
            }
        };
        let Some(rec) = self.get_entity_version(KB_ENTITY_TYPE, KB_ENTITY_ID, version)? else {
            return Ok(None);
        };
        let Some(raw) = rec.properties else {
            return Ok(None);
        };
        crate::udm::stored_variables::named_property(&raw, "Name")
    }

    /// The entity GUID of every Image, SDT, SDT level and Domain of the Knowledge Base, by the reference a stored
    /// part uses for it: the GUID of its entity type, `-`, and its entity id (`9fb193d9-…-74` ->
    /// `ef73725b-…`). An entity GUID is the object's identity across Knowledge Bases (a module's
    /// objects keep theirs when the module is imported); its entity id is local. An in-memory client
    /// has none.
    pub fn object_guids(&self) -> Result<BTreeMap<String, String>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let sql = "SET NOCOUNT ON; SELECT LOWER(CONVERT(VARCHAR(36), g.EntityGuid)), e.EntityId, LOWER(CONVERT(VARCHAR(36), e.EntityGuid)) FROM Entity e JOIN EntityType t ON t.EntityTypeId = e.EntityTypeId AND t.EntityTypeName IN ('Image', 'SDT', 'SDTLevelEntity', 'Domain') JOIN Entity g ON g.EntityTypeId = 1 AND g.EntityId = t.EntityTypeId";
        let raw = self.execute_live_sql(sql)?;
        let mut guids = BTreeMap::new();
        for row in parse_live_rows(&raw, 3)? {
            let id = parse_live_int(row[1], "EntityId")?;
            guids.insert(format!("{}-{id}", row[0].trim()), row[2].trim().to_string());
        }
        Ok(guids)
    }

    /// The Attributes of the Knowledge Base, by declared name: the entity id and whether the latest
    /// version stores a title of its own (`Title`, `Description`, `ContextualTitle` or `ColumnTitle`),
    /// which replaces the title GeneXus derives from the name. An in-memory client has none.
    pub fn attribute_facts(&self) -> Result<BTreeMap<String, AttributeFacts>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let sql = "SET NOCOUNT ON; SELECT v.EntityId, v.EntityVersionName, ISNULL(DATALENGTH(v.EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST(v.EntityVersionProperties AS VARBINARY(MAX)), 2) FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'Attribute' WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId)";
        let raw = self.execute_live_sql(sql)?;
        let mut facts = BTreeMap::new();
        for row in parse_live_rows(&raw, 4)? {
            let id = parse_live_int(row[0], "EntityId")?;
            let id =
                u32::try_from(id).map_err(|_| format!("Attribute entity id {id} is negative"))?;
            let declared = parse_live_int(row[2], "DATALENGTH(EntityVersionProperties)")?;
            let declared = usize::try_from(declared)
                .map_err(|_| format!("Attribute {id} properties length {declared} is negative"))?;
            let bytes = decode_hex_value(row[3].trim(), declared)?;
            let text = String::from_utf8(bytes)
                .map_err(|e| format!("Attribute {id} properties are not UTF-8: {e}"))?;
            let custom_title =
                attribute_has_own_title(&text).map_err(|e| format!("Attribute {id}: {e}"))?;
            facts.insert(
                row[1].trim().to_string(),
                AttributeFacts { id, custom_title },
            );
        }
        Ok(facts)
    }

    /// The description of the subtype group each attribute belongs to, by attribute name: the
    /// latest version of every `Group` and the attributes its cross-references name, in any model.
    /// An attribute in more than one group is left out. An in-memory client has none.
    pub fn subtype_groups(&self) -> Result<BTreeMap<String, String>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let latest = |alias: &str| {
            format!(
                "{alias}.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = {alias}.EntityTypeId AND l.EntityId = {alias}.EntityId)"
            )
        };
        let sql = format!(
            "SET NOCOUNT ON; SELECT DISTINCT a.EntityVersionName, ISNULL(DATALENGTH(g.EntityVersionDescription), 0), CONVERT(VARCHAR(MAX), CAST(g.EntityVersionDescription AS VARBINARY(MAX)), 2) FROM ModelCrossReference x JOIN EntityType gt ON gt.EntityTypeId = x.FromEntityTypeId AND gt.EntityTypeName = 'Group' JOIN EntityType at ON at.EntityTypeId = x.ToEntityTypeId AND at.EntityTypeName = 'Attribute' JOIN EntityVersion g ON g.EntityTypeId = x.FromEntityTypeId AND g.EntityId = x.FromEntityId JOIN EntityVersion a ON a.EntityTypeId = x.ToEntityTypeId AND a.EntityId = x.ToEntityId WHERE {} AND {}",
            latest("g"),
            latest("a")
        );
        let raw = self.execute_live_sql(&sql)?;
        let mut seen: BTreeMap<String, Vec<String>> = BTreeMap::new();
        for row in parse_live_rows(&raw, 3)? {
            let declared = parse_live_int(row[1], "DATALENGTH(EntityVersionDescription)")?;
            let declared = usize::try_from(declared)
                .map_err(|_| format!("group description length {declared} is negative"))?;
            let bytes = decode_hex_value(row[2].trim(), declared)?;
            let description = String::from_utf8(bytes)
                .map_err(|e| format!("a group description is not UTF-8: {e}"))?;
            let groups = seen.entry(row[0].trim().to_string()).or_default();
            if !groups.contains(&description) {
                groups.push(description);
            }
        }
        Ok(seen
            .into_iter()
            .filter_map(|(name, groups)| match groups.as_slice() {
                [only] => Some((name, only.clone())),
                _ => None,
            })
            .collect())
    }

    /// The `TablePK` of each Table, by name, when every model stores the same one. A table the
    /// models disagree on is left out. An in-memory client has none.
    pub fn table_keys(&self) -> Result<BTreeMap<String, u32>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let sql = "SET NOCOUNT ON; SELECT DISTINCT v.EntityVersionName, m.TablePK FROM ModelEntityToTable m JOIN EntityType t ON t.EntityTypeId = m.EntityTypeId AND t.EntityTypeName = 'Table' JOIN EntityVersion v ON v.EntityTypeId = m.EntityTypeId AND v.EntityId = m.EntityId WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId)";
        let raw = self.execute_live_sql(sql)?;
        let mut seen: BTreeMap<String, Vec<u32>> = BTreeMap::new();
        for row in parse_live_rows(&raw, 2)? {
            let key = parse_live_int(row[1], "TablePK")?;
            let key = u32::try_from(key).map_err(|_| format!("TablePK {key} is negative"))?;
            seen.entry(row[0].trim().to_string()).or_default().push(key);
        }
        Ok(seen
            .into_iter()
            .filter_map(|(name, keys)| match keys.as_slice() {
                [only] => Some((name, *only)),
                _ => None,
            })
            .collect())
    }

    /// The design-system classes of the Knowledge Base, by the reference a stored layout uses for a
    /// class: the GUID of the `DesignSystemClass` entity type, `-`, and the class entity id
    /// (`d52f9833-…-443` -> `ContainerFluid`). The latest version of each class names it. An
    /// in-memory client has none.
    pub fn design_system_classes(&self) -> Result<BTreeMap<String, String>, String> {
        if !self.is_live() {
            return Ok(BTreeMap::new());
        }
        let sql = "SET NOCOUNT ON; SELECT LOWER(CONVERT(VARCHAR(36), g.EntityGuid)), v.EntityId, v.EntityVersionName FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'DesignSystemClass' JOIN Entity g ON g.EntityTypeId = 1 AND g.EntityId = t.EntityTypeId WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId)";
        let raw = self.execute_live_sql(sql)?;
        let mut classes = BTreeMap::new();
        for row in parse_live_rows(&raw, 3)? {
            let id = parse_live_int(row[1], "EntityId")?;
            classes.insert(format!("{}-{id}", row[0].trim()), row[2].trim().to_string());
        }
        Ok(classes)
    }

    /// The Knowledge Base's `TranslationMessage` entities (the `GXM_*` web messages): entity id, key
    /// (`FullName`, the latest version's name) and `MessageType` (`Internal` or `User`), in id order. The KB
    /// stores no text for them; the SDK reads it from the install (see `crate::csharp::messages`). An
    /// in-memory client has none.
    pub fn translation_messages(&self) -> Result<Vec<(i64, String, String)>, String> {
        if !self.is_live() {
            return Ok(Vec::new());
        }
        let sql = "SET NOCOUNT ON; SELECT v.EntityId, v.EntityVersionName, CASE WHEN v.EntityVersionProperties LIKE '%<Name>MessageType</Name><Value>Internal</Value>%' THEN 'Internal' WHEN v.EntityVersionProperties LIKE '%<Name>MessageType</Name><Value>User</Value>%' THEN 'User' ELSE 'Unknown' END FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'TranslationMessage' WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId) ORDER BY v.EntityId";
        let raw = self.execute_live_sql(sql)?;
        parse_live_rows(&raw, 3)?
            .into_iter()
            .map(|row| {
                let id = parse_live_int(row[0], "EntityId")?;
                Ok((
                    i64::from(id),
                    row[1].trim().to_string(),
                    row[2].trim().to_string(),
                ))
            })
            .collect()
    }

    /// The names of the Knowledge Base's `Language` objects (the latest version of each), sorted. An
    /// in-memory client has none.
    pub fn language_names(&self) -> Result<Vec<String>, String> {
        if !self.is_live() {
            return Ok(Vec::new());
        }
        let sql = "SET NOCOUNT ON; SELECT v.EntityId, v.EntityVersionName FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'Language' WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId)";
        let raw = self.execute_live_sql(sql)?;
        let mut names: Vec<String> = parse_live_rows(&raw, 2)?
            .into_iter()
            .map(|row| row[1].trim().to_string())
            .collect();
        names.sort();
        Ok(names)
    }

    /// The stored values of the named model properties (`Property: <name>` entities), by name, from every
    /// model and entity that stores one. A property the Knowledge Base never stored is absent: GeneXus then
    /// uses the generator default. Two different stored values for one name are an error, not a choice.
    pub fn stored_model_properties(
        &self,
        names: &[&str],
    ) -> Result<BTreeMap<String, String>, String> {
        if !self.is_live() || names.is_empty() {
            return Ok(BTreeMap::new());
        }
        for name in names {
            if !name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_') {
                return Err(format!("invalid model property name '{name}'"));
            }
        }
        let list = names
            .iter()
            .map(|name| format!("'Property: {name}'"))
            .collect::<Vec<_>>()
            .join(", ");
        let sql = format!(
            "SET NOCOUNT ON; SELECT SUBSTRING(p.EntityVersionName, 11, 200), ISNULL(m.ModelEntityPropertyValue, '') FROM ModelEntityProperty m JOIN EntityVersion p ON p.EntityId = m.ModelEntityPropertyId AND p.EntityVersionName IN ({list}) JOIN EntityType t ON t.EntityTypeId = p.EntityTypeId AND t.EntityTypeName = 'Udm.Types.Property'"
        );
        let raw = self.execute_live_sql(&sql)?;
        let mut values = BTreeMap::new();
        for row in parse_live_rows(&raw, 2)? {
            let name = row[0].trim().to_string();
            let value = row[1].trim().to_string();
            if let Some(previous) = values.insert(name.clone(), value.clone())
                && previous != value
            {
                return Err(format!(
                    "model property {name} is stored with different values ('{previous}', '{value}')"
                ));
            }
        }
        Ok(values)
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

    /// Executes raw SQL against live SQL Server or LocalDB instance using `sqlcmd`.
    pub fn execute_live_sql(&self, sql: &str) -> Result<String, String> {
        let info = self
            .connection_info
            .as_ref()
            .ok_or_else(|| "No connection info configured for live SQL execution".to_string())?;
        if let Some(shared) = &self.read_session {
            if sql.trim_start().starts_with("SET NOCOUNT ON; SELECT ") {
                let mut slot = shared
                    .lock()
                    .map_err(|_| "SQL reader session lock poisoned")?;
                if slot.is_none() {
                    *slot = Some(live::ReadSession::start(info)?);
                }
                let result = slot.as_mut().expect("initialized reader").query(sql);
                if result.is_err() {
                    *slot = None; // Reap the failed process; never replay this query.
                }
                return result;
            }
        }
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
        self.clear_bulk_coverage();
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
        self.clear_bulk_coverage();
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

/// Builds one `EntityVersion` record from its declared lengths and hex columns, as the
/// per-object read and the set-wise read both return them. A payload whose hex does not
/// match its declared length is an error, never a silently shortened part.
fn entity_version_from_fields(
    entity_type: i32,
    entity_id: i32,
    version_id: i32,
    data_len: &str,
    data_hex: &str,
    props_len: &str,
    props_hex: &str,
) -> Result<EntityVersionRecord, String> {
    let declared = parse_live_int(data_len, "DATALENGTH")?;
    let declared = usize::try_from(declared)
        .map_err(|_| format!("EntityVersion DATALENGTH {declared} is negative"))?;
    let data = decode_hex_value(data_hex, declared)?;
    let declared_props = parse_live_int(props_len, "DATALENGTH(EntityVersionProperties)")?;
    let declared_props = usize::try_from(declared_props)
        .map_err(|_| format!("EntityVersionProperties DATALENGTH {declared_props} is negative"))?;
    let props_bytes = decode_hex_value(props_hex, declared_props)?;
    // A NULL column and an empty one both store no properties.
    let properties = if props_bytes.is_empty() {
        None
    } else {
        Some(String::from_utf8(props_bytes).map_err(|e| {
            format!("EntityVersionProperties of ({entity_type}, {entity_id}) is not UTF-8: {e}")
        })?)
    };
    Ok(EntityVersionRecord {
        entity_type_id: entity_type,
        entity_id,
        entity_version_id: version_id,
        data,
        properties,
    })
}

/// Splits `sqlcmd -s <tab>` output into rows of exactly `columns` fields.
///
/// `live.rs` passes `-y 0`, which makes sqlcmd print no header. A header followed
/// by a dashed separator is still skipped, and blank lines are ignored. Any other
/// line with the wrong field count is an error: a row dropped here would be a
/// missing object, part or reference in the answer, which is a false answer.
fn parse_live_rows(raw: &str, columns: usize) -> Result<Vec<Vec<&str>>, String> {
    let lines: Vec<&str> = raw
        .lines()
        .map(|line| line.trim_end_matches('\r'))
        .filter(|line| !line.trim().is_empty())
        .collect();
    let mut rows = Vec::with_capacity(lines.len());
    for (index, line) in lines.iter().enumerate() {
        if is_separator_line(line) {
            continue;
        }
        if lines
            .get(index + 1)
            .is_some_and(|next| is_separator_line(next))
        {
            continue;
        }
        let fields: Vec<&str> = line.split('\t').map(str::trim).collect();
        if fields.len() != columns {
            return Err(format!(
                "sqlcmd returned a row with {} tab-separated fields; expected {columns}",
                fields.len()
            ));
        }
        rows.push(fields);
    }
    Ok(rows)
}

fn is_separator_line(line: &str) -> bool {
    line.contains('-') && line.chars().all(|c| matches!(c, '-' | ' ' | '\t'))
}

/// Parses one integer column. The value is not echoed into the error.
fn parse_live_int(text: &str, column: &str) -> Result<i32, String> {
    text.parse::<i32>()
        .map_err(|_| format!("sqlcmd returned a non-integer value for {column}"))
}

/// Decodes one `CONVERT(VARCHAR(MAX), <varbinary>, 2)` value. `declared_len` is
/// the byte length the server reports (`DATALENGTH`). A value whose hex length is
/// not exactly twice that length is refused: a truncated blob must fail here, not
/// later as "incomplete deflate stream".
fn decode_hex_value(hex: &str, declared_len: usize) -> Result<Vec<u8>, String> {
    if declared_len == 0 {
        return if hex.is_empty() || hex == "NULL" {
            Ok(Vec::new())
        } else {
            Err("EntityVersionData reports zero bytes but returned a value".to_string())
        };
    }
    let digits = hex.as_bytes();
    if digits.len() != declared_len * 2 {
        return Err(format!(
            "EntityVersionData hex value has {} digits for {declared_len} bytes: the payload was truncated before it was decoded",
            digits.len()
        ));
    }
    // Table lookup: this decodes every part payload of a catalog load (tens of MB).
    const INVALID: u8 = 0xFF;
    const TABLE: [u8; 256] = {
        let mut table = [INVALID; 256];
        let mut index = 0;
        while index < 256 {
            if let Some(value) = hex_val(index as u8) {
                table[index] = value;
            }
            index += 1;
        }
        table
    };
    let mut invalid = 0u8;
    let data: Vec<u8> = digits
        .chunks_exact(2)
        .map(|pair| {
            let (high, low) = (TABLE[pair[0] as usize], TABLE[pair[1] as usize]);
            invalid |= (high | low) & 0xF0;
            (high << 4) | (low & 0x0F)
        })
        .collect();
    if invalid != 0 {
        return Err("EntityVersionData hex value contains a non-hex digit".to_string());
    }
    Ok(data)
}

const fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn live_rows_keep_an_empty_trailing_field_and_skip_no_data() {
        // sqlcmd with -y 0 prints no header; an empty last column keeps its tab.
        let raw = "31\tProcedure\tObjects\r\n81\tProcedure\t\r\n";
        let rows = parse_live_rows(raw, 3).expect("rows");
        assert_eq!(rows[1], vec!["81", "Procedure", ""]);
        assert_eq!(rows.len(), 2);
    }

    #[test]
    fn live_rows_skip_a_header_followed_by_a_separator() {
        let raw = "EntityTypeId\tEntityTypeName\t\r\n------------\t-----------\t\r\n           1\tRoot\t\r\n";
        let rows = parse_live_rows(raw, 3).expect("rows");
        assert_eq!(
            rows,
            vec![vec!["1".to_string(), "Root".to_string(), String::new()]]
        );
    }

    #[test]
    fn live_rows_refuse_a_row_with_the_wrong_field_count() {
        // A name containing a tab would split into an extra field: refused, not dropped.
        let error = parse_live_rows("1\tA\tB\tC\r\n", 3).unwrap_err();
        assert!(error.contains("expected 3"), "{error}");
    }

    #[test]
    fn live_integers_refuse_non_numeric_values() {
        assert_eq!(parse_live_int("38", "EntityTypeId"), Ok(38));
        let error = parse_live_int("Procedure", "EntityTypeId").unwrap_err();
        assert!(error.contains("EntityTypeId"), "{error}");
    }

    #[test]
    fn hex_value_decodes_the_declared_length() {
        assert_eq!(
            decode_hex_value("0102030405060708090A", 10).expect("decode"),
            vec![1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
        );
        assert!(decode_hex_value("", 0).expect("empty payload").is_empty());
    }

    #[test]
    fn hex_value_refuses_a_truncated_payload() {
        // The old default sqlcmd width cut values at 256 characters: 201 bytes
        // declared but a 256-digit value must fail here, not as a corrupt blob.
        let cut = "01".repeat(128);
        let error = decode_hex_value(&cut, 201).unwrap_err();
        assert!(error.contains("truncated"), "{error}");
    }

    #[test]
    fn hex_value_decodes_both_cases_and_refuses_every_non_hex_digit() {
        assert_eq!(
            decode_hex_value("00ffFFa0", 4).expect("decode"),
            vec![0x00, 0xFF, 0xFF, 0xA0]
        );
        // A non-hex digit in either nibble, including bytes next to the hex ranges, is refused.
        for bad in ["0g", "g0", "0/", ":0", "@0", "0G", "`0", "\u{e9}"] {
            assert!(decode_hex_value(bad, 1).is_err(), "{bad} was decoded");
        }
    }

    #[test]
    fn hex_value_refuses_a_value_for_an_empty_payload() {
        assert!(decode_hex_value("00", 0).is_err());
    }

    #[test]
    fn entity_version_fields_decode_the_declared_payload() {
        let record = entity_version_from_fields(7, 8, 9, "3", "414243", "0", "").expect("record");
        assert_eq!(record.data, b"ABC".to_vec());
        assert_eq!(record.properties, None);
        assert_eq!(
            (
                record.entity_type_id,
                record.entity_id,
                record.entity_version_id
            ),
            (7, 8, 9)
        );
    }

    #[test]
    fn entity_version_fields_refuse_a_payload_that_disagrees_with_its_length() {
        assert!(entity_version_from_fields(7, 8, 9, "2", "414243", "0", "").is_err());
    }

    #[test]
    fn entity_version_fields_refuse_a_negative_declared_length() {
        // Mutation check: a negative length read as zero would decode the empty hex as an empty part.
        assert!(entity_version_from_fields(7, 8, 9, "-1", "", "0", "").is_err());
        assert!(entity_version_from_fields(7, 8, 9, "0", "", "-1", "").is_err());
    }

    #[test]
    fn a_write_drops_the_entity_version_coverage() {
        let client = UdmClient::new_in_memory();
        client
            .bulk
            .write()
            .expect("bulk")
            .entity_versions
            .insert((7, 8, 9));
        assert!(client.entity_version_is_covered(7, 8, 9));
        client
            .insert_entity_version(EntityVersionRecord {
                entity_type_id: 1,
                entity_id: 1,
                entity_version_id: 1,
                data: Vec::new(),
                properties: None,
            })
            .expect("insert");
        assert!(!client.entity_version_is_covered(7, 8, 9));
    }

    #[test]
    fn a_write_drops_the_composition_coverage() {
        let client = UdmClient::new_in_memory();
        client
            .bulk
            .write()
            .expect("bulk")
            .compositions
            .insert((7, 8, 9));
        assert!(client.composition_is_covered(7, 8, 9));
        client
            .insert_composition(EntityVersionCompositionRecord {
                compound_entity_type_id: 1,
                compound_entity_id: 1,
                compound_entity_version_id: 1,
                component_entity_type_id: 2,
                component_entity_id: 2,
                component_entity_version_id: 2,
            })
            .expect("insert");
        assert!(!client.composition_is_covered(7, 8, 9));
    }

    /// A connection that cannot answer: any query fails, so an `Ok` answer proves the lookup stayed
    /// in memory.
    fn unreachable_live_client() -> UdmClient {
        use crate::udm::connection::AuthMode;
        UdmClient::from_connection_info(KbConnectionInfo {
            server_instance: "(LocalDB)\\GxEngineNoSuchInstance".to_string(),
            db_name: "gx_engine_no_such_database".to_string(),
            auth_mode: AuthMode::Integrated,
            create_db_in_kb_folder: None,
            directory: None,
            data_file: None,
            log_file: None,
            host_name: None,
            detach_on_close: None,
        })
    }

    #[test]
    fn a_covered_key_answers_a_missing_part_with_no_row_and_no_query() {
        let client = unreachable_live_client();
        client
            .bulk
            .write()
            .expect("bulk")
            .entity_versions
            .insert((47, 3, 9));
        assert_eq!(client.get_entity_version(47, 3, 9).expect("no query"), None);
    }

    #[test]
    fn an_uncovered_key_still_queries_on_a_miss() {
        // Mutation check for the coverage test: an uncovered miss must reach the database, which
        // fails on this connection. A broken shortcut would answer Ok(None) here.
        let client = unreachable_live_client();
        assert!(client.get_entity_version(47, 3, 9).is_err());
    }

    #[test]
    fn a_rollback_drops_the_entity_version_coverage_of_a_prefetch_inside_the_transaction() {
        // Rollback restores the whole snapshot taken at begin, so a row a prefetch loaded inside the
        // transaction is gone from memory afterwards. Its key must not stay covered, or a lookup
        // would answer "absent" for a row that exists in the database.
        let client = unreachable_live_client();
        client.begin_transaction().expect("begin");
        client
            .storage
            .write()
            .expect("storage")
            .entity_versions
            .push(EntityVersionRecord {
                entity_type_id: 47,
                entity_id: 3,
                entity_version_id: 1,
                data: vec![1],
                properties: None,
            });
        client
            .bulk
            .write()
            .expect("bulk")
            .entity_versions
            .insert((47, 3, 1));
        client.rollback_transaction().expect("rollback");

        assert!(client.get_entity_version(47, 3, 1).is_err());
    }

    #[test]
    fn a_rollback_drops_the_composition_coverage_of_a_prefetch_inside_the_transaction() {
        let client = unreachable_live_client();
        client.begin_transaction().expect("begin");
        client
            .storage
            .write()
            .expect("storage")
            .entity_version_compositions
            .push(EntityVersionCompositionRecord {
                compound_entity_type_id: 47,
                compound_entity_id: 3,
                compound_entity_version_id: 1,
                component_entity_type_id: 2,
                component_entity_id: 2,
                component_entity_version_id: 2,
            });
        client
            .bulk
            .write()
            .expect("bulk")
            .compositions
            .insert((47, 3, 1));
        client.rollback_transaction().expect("rollback");

        assert!(client.get_compositions(47, 3, 1).is_err());
    }
}

/// What a transaction emitter needs to know of one Attribute (see [`UdmClient::attribute_facts`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct AttributeFacts {
    /// The Attribute's entity id (`A<id><name>` in generated code).
    pub id: u32,
    /// The Attribute stores a title of its own, so its title is not the one derived from its name.
    pub custom_title: bool,
}

/// Whether stored Attribute properties set a title of their own. Empty properties set none.
fn attribute_has_own_title(properties: &str) -> Result<bool, String> {
    if properties.trim().is_empty() {
        return Ok(false);
    }
    let document = roxmltree::Document::parse(properties)
        .map_err(|e| format!("its properties are not XML: {e}"))?;
    Ok(document
        .descendants()
        .filter(|node| node.has_tag_name("Property"))
        .filter_map(|property| {
            property
                .children()
                .find(|c| c.has_tag_name("Name"))
                .and_then(|c| c.text())
        })
        .any(|name| {
            matches!(
                name,
                "Title" | "Description" | "ContextualTitle" | "ColumnTitle"
            )
        }))
}

#[cfg(test)]
mod attribute_title_tests {
    use super::attribute_has_own_title;

    #[test]
    fn only_a_stored_title_property_counts_as_an_own_title() {
        let named = "<Properties><Property><Name>Name</Name><Value>LiveMoveAttributeRulesId</Value></Property></Properties>";
        assert!(!attribute_has_own_title(named).unwrap());
        assert!(!attribute_has_own_title("").unwrap());
        let titled = "<Properties><Property><Name>Name</Name><Value>A</Value></Property><Property><Name>ContextualTitle</Name><Value>Code</Value></Property></Properties>";
        assert!(attribute_has_own_title(titled).unwrap());
        assert!(attribute_has_own_title("<Properties>").is_err());
    }
}

/// Parallel sqlcmd sessions that read a model's parts (`prefetch_model_parts`).
const PART_READ_SHARDS: usize = 4;
