//! Knowledge Base vocabulary: which `EntityType` rows of one Knowledge Base are
//! the objects and parts the engine serves.
//!
//! GeneXus numbers entity types per release. `Procedure` is object 38 on
//! GeneXus 18 but 31 on GeneXus 16 and 17; `Rules` is part 102, 83 and 90;
//! `DesignSystem` is 57, 145 and 50. What is stable across those releases is the
//! `EntityTypeName`, the `EntityTypeNamespace` of an object (`Objects`,
//! `Attributes`, `Domains`, `DataViews`, `PhysicalModel`, `Themes`), and the
//! empty namespace of a part. The resolver therefore reads the `EntityType`
//! table of the Knowledge Base it is connected to and keys the vocabulary by
//! name and namespace, never by a fixed id. The evidence is recorded in
//! `docs/udm_storage_notes.md`.
//!
//! Only names listed here are served. Every other entity type (`Documentation`,
//! `Help`, `Layout`, KB-tree nodes, IDE metadata) resolves to `None`, so a part
//! the engine does not decode is skipped instead of being labelled.

#![forbid(unsafe_code)]

use crate::catalog::ObjectKind;
use crate::udm::decoder::UdmPartKind;
use crate::udm::mock::EntityTypeRecord;
use std::collections::BTreeMap;

/// Object entity types the engine serves: `(EntityTypeName, EntityTypeNamespace, kind)`.
/// Identical names and namespaces on GeneXus 16, 17 and 18 (verified on the
/// disposable `KBTeste16`, `KBTeste17` and `KBTeste` Knowledge Bases).
pub const OBJECT_TYPES: &[(&str, &str, ObjectKind)] = &[
    ("Procedure", "Objects", ObjectKind::Procedure),
    ("Transaction", "Objects", ObjectKind::Transaction),
    ("WebPanel", "Objects", ObjectKind::WebPanel),
    ("SDT", "Objects", ObjectKind::StructuredDataType),
    ("DataProvider", "Objects", ObjectKind::DataProvider),
    ("DataSelector", "Objects", ObjectKind::DataSelector),
    ("Module", "Objects", ObjectKind::Module),
    ("Attribute", "Attributes", ObjectKind::Attribute),
    ("Domain", "Domains", ObjectKind::Domain),
    ("DataView", "DataViews", ObjectKind::DataView),
    ("Table", "PhysicalModel", ObjectKind::Table),
    ("DesignSystem", "Themes", ObjectKind::DesignSystem),
];

/// Part entity types the engine serves: `(EntityTypeName, part kind)`. Parts have
/// an empty namespace. `Procedure` and `DataProviderSource` are both source
/// parts; which one an object carries depends on the object kind (see
/// [`EntityTypeResolver::part_type_id`]).
/// Node entity types of a Structured Data Type's structure: a level (`SDTLevelEntity`) and an
/// item (`SDTItemEntity`). They are not served parts. They are resolved by name so the structure
/// decoder reads each node's row at its own type, never at a guessed id.
pub const SDT_NODE_TYPES: &[&str] = &["SDTLevelEntity", "SDTItemEntity"];

/// Part entity types the engine serves: `(EntityTypeName, part kind)`. Parts have
/// an empty namespace. `Procedure` and `DataProviderSource` are both source
/// parts; which one an object carries depends on the object kind (see
/// [`EntityTypeResolver::part_type_id`]).
pub const PART_TYPES: &[(&str, UdmPartKind)] = &[
    ("Procedure", UdmPartKind::Source),
    ("DataProviderSource", UdmPartKind::Source),
    ("Rules", UdmPartKind::Rules),
    ("Events", UdmPartKind::Events),
    ("Conditions", UdmPartKind::Conditions),
    ("Variables", UdmPartKind::Variables),
    ("Structure", UdmPartKind::Structure),
    // An SDT's fields are its `SDTStructure` part (entity type 103 on the GX18 KB, 305 rows, all SDTs).
    // A Transaction's structure is `Structure`. Both are the same engine kind; the owner picks the name.
    ("SDTStructure", UdmPartKind::Structure),
    ("WebForm", UdmPartKind::WebForm),
    ("PatternInstance", UdmPartKind::PatternInstance),
];

/// The served subset of one Knowledge Base's `EntityType` table, as ids of that
/// Knowledge Base.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct EntityTypeResolver {
    objects_by_id: BTreeMap<i32, ObjectKind>,
    object_ids: BTreeMap<ObjectKind, i32>,
    parts_by_id: BTreeMap<i32, UdmPartKind>,
    part_ids: BTreeMap<&'static str, i32>,
    nodes_by_id: BTreeMap<i32, &'static str>,
}

impl EntityTypeResolver {
    /// Builds the resolver from the rows of one `EntityType` table.
    ///
    /// Rows whose name is not in [`OBJECT_TYPES`] or [`PART_TYPES`] are ignored.
    /// A served name declared twice with different ids is an error: the
    /// vocabulary would be ambiguous, and an ambiguous answer is refused rather
    /// than guessed.
    pub fn from_records(records: &[EntityTypeRecord]) -> Result<Self, String> {
        let mut resolver = Self::default();
        for record in records {
            let name = record.name.trim();
            let namespace = record.namespace.trim();
            if namespace.is_empty() {
                let served = PART_TYPES.iter().find(|(part, _)| *part == name);
                if let Some((part_name, kind)) = served {
                    resolver.add_part(record.entity_type_id, part_name, *kind)?;
                }
                if let Some(node) = SDT_NODE_TYPES.iter().find(|node| **node == name) {
                    resolver.add_node(record.entity_type_id, node)?;
                }
            } else {
                let served = OBJECT_TYPES
                    .iter()
                    .find(|(object, ns, _)| *object == name && *ns == namespace);
                if let Some((_, _, kind)) = served {
                    resolver.add_object(record.entity_type_id, name, *kind)?;
                }
            }
        }
        Ok(resolver)
    }

    fn add_object(&mut self, id: i32, name: &str, kind: ObjectKind) -> Result<(), String> {
        if let Some(existing) = self.object_ids.get(&kind) {
            if *existing != id {
                return Err(format!(
                    "EntityType '{name}' is declared twice with ids {existing} and {id}; the Knowledge Base vocabulary is ambiguous"
                ));
            }
        }
        self.object_ids.insert(kind, id);
        self.objects_by_id.insert(id, kind);
        Ok(())
    }

    fn add_node(&mut self, id: i32, name: &'static str) -> Result<(), String> {
        if let Some((existing_id, _)) = self.nodes_by_id.iter().find(|(_, n)| **n == name) {
            if *existing_id != id {
                return Err(format!(
                    "EntityType node '{name}' is declared twice with ids {existing_id} and {id}; the Knowledge Base vocabulary is ambiguous"
                ));
            }
        }
        self.nodes_by_id.insert(id, name);
        Ok(())
    }

    /// The entity type id this Knowledge Base uses for an SDT node (`SDTLevelEntity` or
    /// `SDTItemEntity`). A node type missing from the vocabulary is an error, not a guess.
    pub fn node_type_id(&self, name: &str) -> Result<i32, String> {
        self.nodes_by_id
            .iter()
            .find(|(_, n)| **n == name)
            .map(|(id, _)| *id)
            .ok_or_else(|| format!("this Knowledge Base has no EntityType '{name}'"))
    }

    fn add_part(&mut self, id: i32, name: &'static str, kind: UdmPartKind) -> Result<(), String> {
        if let Some(existing) = self.part_ids.get(name) {
            if *existing != id {
                return Err(format!(
                    "EntityType part '{name}' is declared twice with ids {existing} and {id}; the Knowledge Base vocabulary is ambiguous"
                ));
            }
        }
        self.part_ids.insert(name, id);
        self.parts_by_id.insert(id, kind);
        Ok(())
    }

    /// The object kind served by entity type `id`, or `None` when the Knowledge
    /// Base does not declare that id as a served object type.
    pub fn object_kind(&self, id: i32) -> Option<ObjectKind> {
        self.objects_by_id.get(&id).copied()
    }

    /// The part kind served by entity type `id`, or `None` when the id is not a
    /// served part (such parts are skipped, never reported as unknown).
    pub fn part_kind(&self, id: i32) -> Option<UdmPartKind> {
        self.parts_by_id.get(&id).copied()
    }

    /// Every entity type id this Knowledge Base serves: object, part and SDT node types, sorted. A row of
    /// any other type (a File's `WikiBlob`, a Module's `ModuleContent`, documentation) is never decoded.
    pub fn served_type_ids(&self) -> Vec<i32> {
        let mut ids: Vec<i32> = self
            .objects_by_id
            .keys()
            .chain(self.parts_by_id.keys())
            .chain(self.nodes_by_id.keys())
            .copied()
            .collect();
        ids.sort_unstable();
        ids.dedup();
        ids
    }

    /// The entity type id this Knowledge Base uses for an object kind.
    pub fn object_type_id(&self, kind: ObjectKind) -> Result<i32, String> {
        self.object_ids.get(&kind).copied().ok_or_else(|| {
            format!(
                "the Knowledge Base does not declare the {kind:?} object type (EntityType name is not present in its vocabulary)"
            )
        })
    }

    /// The entity type id this Knowledge Base uses for a part of an object of
    /// kind `owner`. `Source` is `Procedure` for procedures and
    /// `DataProviderSource` for data providers; other owners have no source part.
    pub fn part_type_id(&self, owner: ObjectKind, part: UdmPartKind) -> Result<i32, String> {
        let name: &str = match part {
            UdmPartKind::Source => match owner {
                ObjectKind::Procedure => "Procedure",
                ObjectKind::DataProvider => "DataProviderSource",
                other => {
                    return Err(format!("{other:?} objects have no Source part"));
                }
            },
            UdmPartKind::Rules => "Rules",
            UdmPartKind::Events => "Events",
            UdmPartKind::Conditions => "Conditions",
            UdmPartKind::Variables => "Variables",
            UdmPartKind::Structure => match owner {
                ObjectKind::StructuredDataType => "SDTStructure",
                _ => "Structure",
            },
            UdmPartKind::WebForm => "WebForm",
            UdmPartKind::PatternInstance => "PatternInstance",
            UdmPartKind::Unknown(_) => {
                return Err("the part has no engine part kind".to_string());
            }
        };
        self.part_ids.get(name).copied().ok_or_else(|| {
            format!(
                "the Knowledge Base does not declare the {name} part type (EntityType name is not present in its vocabulary)"
            )
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn record(id: i32, name: &str, namespace: &str) -> EntityTypeRecord {
        EntityTypeRecord {
            entity_type_id: id,
            name: name.to_string(),
            namespace: namespace.to_string(),
        }
    }

    #[test]
    fn object_and_part_names_resolve_by_namespace_not_by_id() {
        // GeneXus 16 numbering: Procedure is 31, its source part 81.
        let resolver = EntityTypeResolver::from_records(&[
            record(31, "Procedure", "Objects"),
            record(81, "Procedure", ""),
        ])
        .expect("resolver");

        assert_eq!(resolver.object_kind(31), Some(ObjectKind::Procedure));
        assert_eq!(resolver.part_kind(81), Some(UdmPartKind::Source));
        // The same name in the other namespace class is not the other kind.
        assert_eq!(resolver.object_kind(81), None);
        assert_eq!(resolver.part_kind(31), None);
    }

    #[test]
    fn unserved_and_unknown_ids_resolve_to_none() {
        let resolver = EntityTypeResolver::from_records(&[
            record(95, "Documentation", ""),
            record(8, "Folder", "Folders"),
        ])
        .expect("resolver");

        assert_eq!(resolver.part_kind(95), None);
        assert_eq!(resolver.object_kind(8), None);
        assert_eq!(resolver.object_kind(999), None);
    }

    #[test]
    fn served_type_ids_are_the_object_part_and_node_types_only() {
        let resolver = EntityTypeResolver::from_records(&[
            record(38, "Procedure", "Objects"),
            record(81, "Procedure", ""),
            record(201, "SDTLevelEntity", ""),
            record(67, "ModuleContent", ""),
            record(130, "WikiBlob", ""),
            record(64, "File", "Objects"),
        ])
        .expect("resolver");
        assert_eq!(resolver.served_type_ids(), vec![38, 81, 201]);
    }

    #[test]
    fn duplicate_served_name_with_different_ids_is_refused() {
        let error = EntityTypeResolver::from_records(&[
            record(31, "Procedure", "Objects"),
            record(38, "Procedure", "Objects"),
        ])
        .unwrap_err();
        assert!(error.contains("declared twice"), "{error}");
    }

    #[test]
    fn repeated_identical_rows_are_accepted() {
        let resolver = EntityTypeResolver::from_records(&[
            record(38, "Procedure", "Objects"),
            record(38, "Procedure", "Objects"),
        ])
        .expect("same id twice is not ambiguous");
        assert_eq!(resolver.object_type_id(ObjectKind::Procedure), Ok(38));
    }

    /// The GX18 vocabulary: an SDT's fields are `SDTStructure` (103); a Transaction's are
    /// `Structure` (104). The writer must store each under its owner's id, and the reader must
    /// serve both. Before this, `SDTStructure` was not served, so every SDT read as empty.
    #[test]
    fn structure_part_depends_on_the_owning_object_kind() {
        let resolver = EntityTypeResolver::from_records(&[
            record(40, "SDT", "Objects"),
            record(43, "Transaction", "Objects"),
            record(103, "SDTStructure", ""),
            record(104, "Structure", ""),
        ])
        .expect("resolver");

        assert_eq!(
            resolver.part_type_id(ObjectKind::StructuredDataType, UdmPartKind::Structure),
            Ok(103)
        );
        assert_eq!(
            resolver.part_type_id(ObjectKind::Transaction, UdmPartKind::Structure),
            Ok(104)
        );
        assert_eq!(resolver.part_kind(103), Some(UdmPartKind::Structure));
        assert_eq!(resolver.part_kind(104), Some(UdmPartKind::Structure));
    }

    #[test]
    fn source_part_depends_on_the_owning_object_kind() {
        let resolver = EntityTypeResolver::from_records(&[
            record(29, "DataProvider", "Objects"),
            record(99, "DataProviderSource", ""),
            record(38, "Procedure", "Objects"),
            record(100, "Procedure", ""),
        ])
        .expect("resolver");

        assert_eq!(
            resolver.part_type_id(ObjectKind::Procedure, UdmPartKind::Source),
            Ok(100)
        );
        assert_eq!(
            resolver.part_type_id(ObjectKind::DataProvider, UdmPartKind::Source),
            Ok(99)
        );
        assert!(
            resolver
                .part_type_id(ObjectKind::Transaction, UdmPartKind::Source)
                .is_err()
        );
    }

    #[test]
    fn undeclared_types_are_reported_by_name_for_the_writer() {
        let resolver = EntityTypeResolver::default();
        let error = resolver.object_type_id(ObjectKind::Procedure).unwrap_err();
        assert!(error.contains("Procedure"), "{error}");
        let part_error = resolver
            .part_type_id(ObjectKind::Procedure, UdmPartKind::Rules)
            .unwrap_err();
        assert!(part_error.contains("Rules"), "{part_error}");
    }
}
