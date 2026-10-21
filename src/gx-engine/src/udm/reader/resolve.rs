//! Object lookup, target resolution and the refusals of objects the engine cannot read whole.

use super::*;

/// The refusal of a non-Procedure object whose active version has no parts. The engine reads only the active
/// version, and an empty object is refused rather than returned as if it were complete.
pub const NO_PARTS_REFUSAL: &str = "its active version has no parts; the engine reads only the active version and refuses an empty object rather than emit it";

/// The refusal of a Procedure whose active version has no SOURCE part among the composition rows the reader
/// reads. A Procedure is generated from its SOURCE part, so one without it is refused by name, never skipped.
pub const NO_SOURCE_REFUSAL: &str = "its active version has no SOURCE part among the parts the reader reads; the engine generates a Procedure only from its SOURCE and refuses it rather than emit it";

/// Whether the composition rows of an object's active version hold a SOURCE part the engine serves.
pub(super) fn has_source_part(
    compositions: &[EntityVersionCompositionRecord],
    resolver: &EntityTypeResolver,
) -> bool {
    compositions
        .iter()
        .any(|c| resolver.part_kind(c.component_entity_type_id) == Some(UdmPartKind::Source))
}

/// Refuses an object whose active version lacks the part it is generated from, by name.
///
/// A Procedure is generated from its SOURCE part, so one without a SOURCE part is refused whatever other parts
/// it has. Any other kind with no parts is refused. A Module has no source of its own and is exempt (an unproven
/// assumption, unchanged here). A Procedure the Knowledge Base stores as `GenerateObject` False is returned before
/// this check, with its own reason (`object.not_generated`), and is not refused here. No object is marked not
/// generated from its parts: only the stored flag does that. Earlier versions are not read.
pub(super) fn refuse_without_parts(
    object: &mut KbObject,
    kind: ObjectKind,
    parts: usize,
    has_source: bool,
) {
    if kind == ObjectKind::Module || object.refusal.is_some() || object.not_generated.is_some() {
        return;
    }
    if matches!(kind, ObjectKind::Procedure | ObjectKind::DataProvider) {
        if !has_source {
            object.refusal = Some(if kind == ObjectKind::Procedure {
                NO_SOURCE_REFUSAL.into()
            } else {
                "DataProvider active version has no SOURCE part; refusing an empty generator".into()
            });
        }
        return;
    }
    if parts == 0 {
        object.refusal = Some(NO_PARTS_REFUSAL.into());
    }
}

/// What a Procedure's VARIABLES decode reads from the Knowledge Base: the SDT and Domain type ids (to
/// name each SDT and Domain a declaration uses), the targets, and the lookup that gives a Domain its
/// stored type.
pub(super) struct VariablesLookup<'a> {
    pub(super) sdt_type: Option<i32>,
    pub(super) domain_type: Option<i32>,
    pub(super) targets: &'a BTreeMap<(i32, i32), Target>,
    pub(super) domain_of: &'a dyn Fn(i32) -> Option<DataType>,
    pub(super) level_of: &'a dyn Fn(i32) -> Result<Option<stored_variables::NestedLevel>, String>,
    /// Read collection metadata only when an SDT variable needs it; failures remain errors.
    pub(super) collection_of:
        &'a dyn Fn(i32) -> Result<Option<stored_variables::CollectionRoot>, String>,
}

/// The source the catalog holds for one stored part. A Procedure's stored VARIABLES are decoded
/// into the portable declarations, and the SDTs they name become explicit `Uses` references. A
/// declaration the engine does not map refuses the object, naming the variable; the part is not
/// held, so no emitter can read it as if it were complete.
pub(super) fn held_source(
    object: &mut KbObject,
    kind: ObjectKind,
    part_kind: UdmPartKind,
    stored: String,
    lookup: &VariablesLookup<'_>,
) -> Option<String> {
    if !matches!(kind, ObjectKind::Procedure | ObjectKind::DataProvider)
        || part_kind != UdmPartKind::Variables
    {
        return Some(stored);
    }
    let targets = lookup.targets;
    let sdt_name = |id: i32| {
        match (lookup.collection_of)(id) {
            Ok(None) => {}
            // An unreadable root is not a record. The fallible collection lookup below
            // propagates its error instead of accepting an unproven type.
            Ok(Some(_)) | Err(_) => return None,
        }
        lookup
            .sdt_type
            .and_then(|ty| targets.get(&(ty, id)))
            .map(|target| target.name.clone())
    };
    let decoded = match stored_variables::decode_with_collections(
        &stored,
        &sdt_name,
        lookup.collection_of,
        lookup.domain_of,
        lookup.level_of,
    ) {
        Ok(decoded) => decoded,
        Err(error) => {
            object.refusal.get_or_insert(format!("VARIABLES: {error}"));
            return None;
        }
    };
    for id in decoded.sdt_entity_ids {
        let Some(target) = lookup.sdt_type.and_then(|ty| targets.get(&(ty, id))) else {
            continue;
        };
        let reference = Reference {
            kind: ReferenceKind::Uses,
            target: target.id.clone(),
        };
        if !object.references.contains(&reference) {
            object.references.push(reference);
        }
    }
    // A based-on variable takes its type from its Domain, so the Domain is an explicit `BasedOn`
    // reference, as a based-on SDT item's is.
    for id in decoded.domain_entity_ids {
        let Some(target) = lookup.domain_type.and_then(|ty| targets.get(&(ty, id))) else {
            continue;
        };
        let reference = Reference {
            kind: ReferenceKind::BasedOn,
            target: target.id.clone(),
        };
        if !object.references.contains(&reference) {
            object.references.push(reference);
        }
    }
    match serde_json::to_string(&decoded.variables) {
        Ok(portable) => Some(portable),
        Err(error) => {
            object.refusal.get_or_insert(format!(
                "VARIABLES cannot be written in portable form: {error}"
            ));
            None
        }
    }
}

/// The object whose name matches `name` (case-insensitive), with its kind. Parts and KB-tree
/// nodes that share the name are not objects and never match. A name held by more than one
/// object is ambiguous: it is refused, never resolved to one of them. `Type:Name` (for example
/// `Transaction:Customer`) keeps only the objects of that type, so a Transaction and the Table it
/// defines, which share a name, are each readable; an unknown type is refused.
pub(super) fn find_object<'a>(
    records: &'a [ModelEntityVersionRecord],
    resolver: &EntityTypeResolver,
    name: &str,
) -> Result<Option<(&'a ModelEntityVersionRecord, ObjectKind)>, String> {
    let (wanted_kind, name) = match name.split_once(':') {
        Some((type_name, bare)) => {
            let kind = crate::kb_ops::kind_from_type_name(type_name)
                .ok_or_else(|| format!("unknown object type '{type_name}' in '{name}'"))?;
            (Some(kind), bare)
        }
        None => (None, name),
    };
    let mut found: Vec<(&ModelEntityVersionRecord, ObjectKind)> = records
        .iter()
        .filter_map(|record| {
            resolver
                .object_kind(record.entity_type_id)
                .filter(|kind| wanted_kind.is_none_or(|wanted| wanted == *kind))
                .filter(|_| record.name.eq_ignore_ascii_case(name))
                .map(|kind| (record, kind))
        })
        .collect();
    match found.len() {
        0 => Ok(None),
        1 => Ok(found.pop()),
        count => Err(format!(
            "'{name}' is ambiguous: {count} objects of this model have that name; refusing to pick one (qualify it as Type:Name)"
        )),
    }
}

/// Whether `record` is the Knowledge Base root module identified by the model.
pub(super) fn is_system_root(
    record: &ModelEntityVersionRecord,
    system_root: Option<(i32, i32)>,
) -> bool {
    system_root == Some((record.entity_type_id, record.entity_id))
}

/// The catalog id of every served object of the model, keyed by its entity type and id. The
/// target of a reference is the id the target object carries, so a reference never points at a
/// name the object was not given.
///
/// Ids are unique by construction. Two objects may share a name (GeneXus allows two Modules named
/// alike under different parents), so a name held by more than one object takes the entity
/// identity as a suffix. Names cannot contain `-`, so a suffixed id never equals a plain name.
pub(super) fn object_targets(
    records: &[ModelEntityVersionRecord],
    resolver: &EntityTypeResolver,
    system_root: Option<(i32, i32)>,
) -> BTreeMap<(i32, i32), Target> {
    let served: Vec<&ModelEntityVersionRecord> = records
        .iter()
        .filter(|record| resolver.object_kind(record.entity_type_id).is_some())
        .collect();
    let mut name_counts: BTreeMap<String, usize> = BTreeMap::new();
    for record in &served {
        if !is_system_root(record, system_root) {
            *name_counts
                .entry(record.name.to_ascii_lowercase())
                .or_default() += 1;
        }
    }
    served
        .into_iter()
        .filter_map(|record| {
            let id = if is_system_root(record, system_root) {
                ObjectId::new(SYSTEM_ROOT_OBJECT_ID).ok()?
            } else {
                let shared = name_counts
                    .get(&record.name.to_ascii_lowercase())
                    .is_some_and(|count| *count > 1);
                object_id(record, shared)
            };
            Some((
                (record.entity_type_id, record.entity_id),
                Target {
                    id,
                    name: record.name.clone(),
                    entity_type_id: record.entity_type_id,
                    entity_version_id: record.entity_version_id,
                },
            ))
        })
        .collect()
}

/// One served row as the catalog sees it: its catalog id, and the name the model holds it under.
/// The name is what a reference to the row must carry (an SDT is named in its variables).
#[derive(Debug, Clone)]
pub(super) struct Target {
    pub(super) id: ObjectId,
    pub(super) name: String,
    /// The entity type of the row, and its active version. A domain's stored type is read at this
    /// version, the one the model holds.
    pub(super) entity_type_id: i32,
    pub(super) entity_version_id: i32,
}

/// The catalog id of one model row, from the map `object_targets` built.
pub(super) fn target_of(
    targets: &BTreeMap<(i32, i32), Target>,
    record: &ModelEntityVersionRecord,
) -> Result<ObjectId, String> {
    targets
        .get(&(record.entity_type_id, record.entity_id))
        .map(|target| target.id.clone())
        .ok_or_else(|| {
            format!(
                "object {} of type {} has no catalog id",
                record.entity_id, record.entity_type_id
            )
        })
}

/// The catalog object of one model row, under the id the target map gives it. The Knowledge Base
/// root module is flagged as such, which is the only way the catalog accepts its display name.
pub(super) fn object_for(
    record: &ModelEntityVersionRecord,
    kind: ObjectKind,
    id: ObjectId,
    is_root: bool,
) -> KbObject {
    let mut object = KbObject::new(id, &record.name, kind);
    object.system_root = is_root;
    object
}

/// The id of one object row. A shared name takes the entity identity as a suffix. A name that is
/// not a valid identity falls back to the entity identity, which is unique as well.
fn object_id(record: &ModelEntityVersionRecord, shared_name: bool) -> ObjectId {
    let identity = format!("{}-{}", record.entity_type_id, record.entity_id);
    let base = if shared_name {
        format!("{}-{identity}", record.name)
    } else {
        record.name.clone()
    };
    ObjectId::new(&base).unwrap_or_else(|_| {
        ObjectId::new(format!("obj-{identity}")).expect(
            "`obj-` plus two integers is only ASCII digits and `-`, always a valid identity",
        )
    })
}
