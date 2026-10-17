//! In-memory database simulation and mock repository for UDM storage.

#![forbid(unsafe_code)]

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
