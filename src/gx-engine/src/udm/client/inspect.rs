//! Minimal inspection of one Procedure: identity, module path, description, lifecycle and the part
//! names the SDK exposes. Anything whose SDK rendering is unproven (name collisions across types,
//! folders in the parent chain, unknown part types, disagreeing model/entity metadata) is refused.

use super::UdmClient;
use super::live_rows::{decode_nvarchar, nvarchar_hex_columns, parse_live_int, parse_live_rows};
use crate::catalog::ObjectKind;
use crate::udm::ModelEntityVersionRecord;
use crate::udm::reader::{ObjectRow, UdmReader};
use crate::udm::version_scope::{ActiveVersion, ScopeUse, select_design_model};
use serde::Serialize;
use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;

/// Holder chains deeper than this are refused rather than walked further.
const MAX_MODULE_DEPTH: usize = 64;

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ObjectInspection {
    pub name: String,
    pub guid: String,
    pub entity_id: i32,
    /// `Root Module/<module>/.../<name>`.
    pub path: String,
    pub description: String,
    pub last_update_ticks: i64,
    pub user_name: String,
    /// The SDK display names of the stored parts, ordered like `OrdinalIgnoreCase`.
    pub available_parts: Vec<String>,
}

pub type ProcedureInspection = ObjectInspection;

/// The model row of the inspected object, with the entity version it points at.
#[derive(Debug, Clone, PartialEq, Eq)]
struct ObjectDetail {
    model_user_id: i32,
    entity_user_id: i32,
    model_description: String,
    entity_description: String,
    parent: (i32, i32),
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct ModuleRow {
    name: String,
    parent: (i32, i32),
}

impl UdmClient {
    /// One Procedure's minimal inspection, read with the active scope in one fresh read view.
    pub fn procedure_inspect_minimal(
        &self,
        active: Option<ActiveVersion>,
        name: &str,
    ) -> Result<ProcedureInspection, String> {
        self.object_inspect_minimal(active, ObjectKind::Procedure, name)
    }

    /// One object's minimal inspection for `kind`, read with the active scope in one view.
    pub fn object_inspect_minimal(
        &self,
        active: Option<ActiveVersion>,
        kind: ObjectKind,
        name: &str,
    ) -> Result<ObjectInspection, String> {
        if !self.is_live() {
            return Err("the in-memory store has no inspection metadata".into());
        }
        if name.is_empty() || !name.is_ascii() || name.chars().any(|c| c.is_control()) {
            return Err("native inspection requires a nonempty ASCII object name".into());
        }
        let kind_name = crate::kb_ops::type_name(kind);
        self.with_read_view(|view| {
            let model = select_design_model(&view.model_topology()?, active, ScopeUse::Read)
                .map_err(|refusal| refusal.0)?;
            let client = view.clone().with_design_model_id(model);
            let rows = UdmReader::new(Arc::new(client.clone())).list_objects()?;
            let row = select_object_of_kind(&rows, &client.get_active_objects(model)?, kind, name)?;
            let metadata = client
                .get_object_read_metadata(
                    model,
                    row.entity_type_id,
                    row.entity_id,
                    row.entity_version_id,
                )?
                .ok_or_else(|| format!("{kind_name} lifecycle metadata is missing"))?;
            let detail = client.object_detail(
                model,
                row.entity_type_id,
                row.entity_id,
                row.entity_version_id,
            )?;
            let modules = client.module_rows(model)?;
            let users: BTreeMap<i32, String> =
                client.entity_names("Udm.Types.User")?.into_iter().collect();
            let parts =
                client.part_type_names(row.entity_type_id, row.entity_id, row.entity_version_id)?;
            assemble(
                kind_name,
                row.name.clone(),
                row.entity_id,
                metadata.guid,
                metadata
                    .object_saved_ticks
                    .or(metadata.model_entity_version_ticks),
                &detail,
                &modules,
                &users,
                &parts,
            )
        })
    }

    fn object_detail(
        &self,
        model: i32,
        entity_type_id: i32,
        entity_id: i32,
        entity_version_id: i32,
    ) -> Result<ObjectDetail, String> {
        let model_description = nvarchar_hex_columns("m.ModelEntityVersionDescription");
        let entity_description = nvarchar_hex_columns("v.EntityVersionDescription");
        let sql = format!(
            "SET NOCOUNT ON; SELECT m.ModelUserId, v.UserId, {model_description}, {entity_description}, m.ModelParentEntityTypeId, m.ModelParentEntityId FROM ModelEntityVersion m JOIN EntityVersion v ON v.EntityTypeId = m.EntityTypeId AND v.EntityId = m.EntityId AND v.EntityVersionId = m.EntityVersionId WHERE m.ModelId = {model} AND m.EntityTypeId = {entity_type_id} AND m.EntityId = {entity_id} AND m.EntityVersionId = {entity_version_id}"
        );
        let raw = self.execute_live_sql(&sql)?;
        let rows = parse_live_rows(&raw, 8)?;
        let [row] = rows.as_slice() else {
            return Err("the inspected object has no single active model row".into());
        };
        Ok(ObjectDetail {
            model_user_id: parse_live_int(row[0].trim(), "ModelUserId")?,
            entity_user_id: parse_live_int(row[1].trim(), "UserId")?,
            model_description: decode_nvarchar(row[2], row[3], "model description")?,
            entity_description: decode_nvarchar(row[4], row[5], "entity description")?,
            parent: (
                parse_live_int(row[6].trim(), "ModelParentEntityTypeId")?,
                parse_live_int(row[7].trim(), "ModelParentEntityId")?,
            ),
        })
    }

    /// Every Module of the model, keyed by `(EntityTypeId, EntityId)`.
    fn module_rows(&self, model: i32) -> Result<BTreeMap<(i32, i32), ModuleRow>, String> {
        let name = nvarchar_hex_columns("m.ModelEntityVersionName");
        let sql = format!(
            "SET NOCOUNT ON; SELECT m.EntityTypeId, m.EntityId, {name}, m.ModelParentEntityTypeId, m.ModelParentEntityId FROM ModelEntityVersion m JOIN EntityType t ON t.EntityTypeId = m.EntityTypeId AND t.EntityTypeName = 'Module' AND t.EntityTypeNamespace = 'Objects' WHERE m.ModelId = {model}"
        );
        let raw = self.execute_live_sql(&sql)?;
        let mut modules = BTreeMap::new();
        for row in parse_live_rows(&raw, 6)? {
            let key = (
                parse_live_int(row[0].trim(), "Module EntityTypeId")?,
                parse_live_int(row[1].trim(), "Module EntityId")?,
            );
            let module = ModuleRow {
                name: decode_nvarchar(row[2], row[3], "module name")?,
                parent: (
                    parse_live_int(row[4].trim(), "Module ModelParentEntityTypeId")?,
                    parse_live_int(row[5].trim(), "Module ModelParentEntityId")?,
                ),
            };
            if modules.insert(key, module).is_some() {
                return Err("a module is stored twice in the model".into());
            }
        }
        Ok(modules)
    }

    /// `(EntityTypeName, EntityTypeNamespace)` of every stored part of one object version.
    fn part_type_names(
        &self,
        entity_type_id: i32,
        entity_id: i32,
        entity_version_id: i32,
    ) -> Result<Vec<(String, String)>, String> {
        let name = nvarchar_hex_columns("t.EntityTypeName");
        let namespace = nvarchar_hex_columns("ISNULL(t.EntityTypeNamespace, '')");
        let sql = format!(
            "SET NOCOUNT ON; SELECT {name}, {namespace} FROM EntityVersionComposition c JOIN EntityType t ON t.EntityTypeId = c.ComponentEntityTypeId WHERE c.CompoundEntityTypeId = {entity_type_id} AND c.CompoundEntityId = {entity_id} AND c.CompoundEntityVersionId = {entity_version_id}"
        );
        self.live_records(&sql, 4, |row| {
            Ok((
                decode_nvarchar(row[0], row[1], "part type name")?,
                decode_nvarchar(row[2], row[3], "part type namespace")?,
            ))
        })
    }
}

/// The only Procedure named `name` (ASCII case-insensitively). Any other model entity with that
/// name is refused: the SDK discloses same-named objects of other types from its index.
fn select_object_of_kind<'a>(
    objects: &'a [ObjectRow],
    entities: &[ModelEntityVersionRecord],
    kind: ObjectKind,
    name: &str,
) -> Result<&'a ObjectRow, String> {
    let kind_name = crate::kb_ops::type_name(kind);
    let mut matches = objects
        .iter()
        .filter(|row| row.kind == kind && row.name.eq_ignore_ascii_case(name));
    let (Some(row), None) = (matches.next(), matches.next()) else {
        return Err(format!(
            "exactly one {kind_name} must carry the requested name"
        ));
    };
    if entities.iter().any(|other| {
        other.name.eq_ignore_ascii_case(name)
            && (other.entity_type_id, other.entity_id) != (row.entity_type_id, row.entity_id)
    }) {
        return Err("another model entity shares the requested name".into());
    }
    Ok(row)
}

#[allow(clippy::too_many_arguments)]
fn assemble(
    kind_name: &str,
    name: String,
    entity_id: i32,
    guid: Option<String>,
    last_update_ticks: Option<i64>,
    detail: &ObjectDetail,
    modules: &BTreeMap<(i32, i32), ModuleRow>,
    users: &BTreeMap<i32, String>,
    parts: &[(String, String)],
) -> Result<ObjectInspection, String> {
    let guid = guid
        .filter(|guid| !guid.is_empty())
        .ok_or_else(|| format!("the {kind_name} GUID is missing"))?;
    let last_update_ticks =
        last_update_ticks.ok_or_else(|| format!("the {kind_name} has no stored last update"))?;
    if detail.model_user_id != detail.entity_user_id
        || detail.model_description != detail.entity_description
    {
        return Err(format!(
            "model and entity metadata disagree on the {kind_name}'s user or description"
        ));
    }
    let user_name = users
        .get(&detail.model_user_id)
        .filter(|user| !user.trim().is_empty())
        .ok_or_else(|| format!("the {kind_name}'s user is not stored"))?
        .clone();
    let mut segments = module_chain(kind_name, detail.parent, modules)?;
    segments.push(name.clone());
    Ok(ObjectInspection {
        name,
        guid,
        entity_id,
        path: segments.join("/"),
        description: detail.model_description.clone(),
        last_update_ticks,
        user_name,
        available_parts: available_parts(parts)?,
    })
}

/// Module names from the root module down to `parent`. Folders or any other holder are refused.
fn module_chain(
    kind_name: &str,
    parent: (i32, i32),
    modules: &BTreeMap<(i32, i32), ModuleRow>,
) -> Result<Vec<String>, String> {
    let mut names = Vec::new();
    let mut current = parent;
    while current != (0, 0) {
        if names.len() == MAX_MODULE_DEPTH {
            return Err("the module chain is too deep or cyclic".into());
        }
        let module = modules
            .get(&current)
            .ok_or_else(|| format!("the {kind_name} is held by a non-module object"))?;
        if module.name.trim().is_empty() || module.name.chars().any(char::is_control) {
            return Err("a module name is empty or malformed".into());
        }
        names.push(module.name.clone());
        current = module.parent;
    }
    if names.is_empty() {
        return Err(format!("the {kind_name} is not held by a module"));
    }
    names.reverse();
    Ok(names)
}

/// The SDK's display names (`PartAccessor.GetAvailableParts`) for stored parts: every source-like
/// part displays as `Source` (and `Rules`/`Events` also addressable by name), and structure/forms keep their type name.
fn available_parts(parts: &[(String, String)]) -> Result<Vec<String>, String> {
    let mut names = BTreeSet::new();
    for (name, namespace) in parts {
        if !namespace.is_empty() {
            return Err(format!("part type {name} has an unexpected namespace"));
        }
        let display = match name.as_str() {
            "Procedure" | "Conditions" => "Source",
            "Rules" => {
                names.insert("Rules".to_ascii_uppercase());
                "Source"
            }
            "Events" => {
                names.insert("Events".to_ascii_uppercase());
                "Source"
            }
            "Structure" | "Variables" | "Documentation" | "Help" | "Layout" | "WebForm"
            | "WinForm" => name.as_str(),
            other => return Err(format!("part type {other} has no proven SDK display name")),
        };
        names.insert(display.to_ascii_uppercase());
    }
    // Restore the display spelling; ASCII upper-case keys order like OrdinalIgnoreCase.
    names
        .into_iter()
        .map(|upper| {
            [
                "Source",
                "Rules",
                "Events",
                "Structure",
                "Variables",
                "Documentation",
                "Help",
                "Layout",
                "WebForm",
                "WinForm",
            ]
            .into_iter()
            .find(|display| display.to_ascii_uppercase() == upper)
            .map(str::to_string)
            .ok_or_else(|| "unexpected part display name".to_string())
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn part(name: &str) -> (String, String) {
        (name.into(), String::new())
    }

    fn modules() -> BTreeMap<(i32, i32), ModuleRow> {
        let module = |name: &str, parent: (i32, i32)| ModuleRow {
            name: name.into(),
            parent,
        };
        BTreeMap::from([
            ((12, 1), module("Root Module", (0, 0))),
            ((12, 3), module("GeneXus", (12, 1))),
            ((12, 4), module("Common", (12, 3))),
            ((12, 17), module("Notifications", (12, 4))),
        ])
    }

    fn detail(parent: (i32, i32)) -> ObjectDetail {
        ObjectDetail {
            model_user_id: 3,
            entity_user_id: 3,
            model_description: "Add Device Groups".into(),
            entity_description: "Add Device Groups".into(),
            parent,
        }
    }

    fn users() -> BTreeMap<i32, String> {
        BTreeMap::from([(3, "GeneXus".to_string())])
    }

    #[test]
    fn library_and_user_procedures_render_like_the_sdk() {
        let library = assemble(
            "Procedure",
            "AddDeviceGroups".into(),
            1,
            Some("83a55789-716f-4c13-a03c-215b167050e8".into()),
            Some(1),
            &detail((12, 17)),
            &modules(),
            &users(),
            &[part("Documentation"), part("Rules"), part("Variables")],
        )
        .unwrap();
        assert_eq!(
            library.path,
            "Root Module/GeneXus/Common/Notifications/AddDeviceGroups"
        );
        assert_eq!(
            library.available_parts,
            ["Documentation", "Rules", "Source", "Variables"]
        );
        assert_eq!(library.user_name, "GeneXus");
        let parts = [
            "Rules",
            "Conditions",
            "Layout",
            "Procedure",
            "Variables",
            "Documentation",
            "Help",
        ]
        .map(part);
        assert_eq!(
            available_parts(&parts).unwrap(),
            [
                "Documentation",
                "Help",
                "Layout",
                "Rules",
                "Source",
                "Variables"
            ]
        );
        assert_eq!(
            module_chain("Procedure", (12, 1), &modules()).unwrap(),
            ["Root Module"]
        );
    }

    #[test]
    fn unproven_renderings_are_refused() {
        let base = |detail: &ObjectDetail,
                    parts: &[(String, String)],
                    guid: Option<String>,
                    ticks: Option<i64>| {
            assemble(
                "Procedure",
                "P".into(),
                1,
                guid,
                ticks,
                detail,
                &modules(),
                &users(),
                parts,
            )
            .is_err()
        };
        let ok = [part("Rules")];
        let guid = || Some("g".to_string());
        assert!(!base(&detail((12, 1)), &ok, guid(), Some(1)), "baseline");
        assert!(
            base(&detail((12, 1)), &[part("BogusPart")], guid(), Some(1)),
            "unknown part"
        );
        assert!(
            base(
                &detail((12, 1)),
                &[("Rules".into(), "Objects".into())],
                guid(),
                Some(1)
            ),
            "namespaced part"
        );
        assert!(
            base(&detail((12, 99)), &ok, guid(), Some(1)),
            "folder or missing holder"
        );
        assert!(base(&detail((0, 0)), &ok, guid(), Some(1)), "no module");
        assert!(base(&detail((12, 1)), &ok, None, Some(1)), "missing guid");
        assert!(
            base(&detail((12, 1)), &ok, guid(), None),
            "missing timestamp"
        );
        let mut disagree = detail((12, 1));
        disagree.entity_user_id = 1;
        assert!(base(&disagree, &ok, guid(), Some(1)), "user disagreement");
        let mut described = detail((12, 1));
        described.entity_description = "Other".into();
        assert!(
            base(&described, &ok, guid(), Some(1)),
            "description disagreement"
        );
        let mut unknown_user = detail((12, 1));
        unknown_user.model_user_id = 9;
        unknown_user.entity_user_id = 9;
        assert!(base(&unknown_user, &ok, guid(), Some(1)), "unknown user");
        let cyclic = BTreeMap::from([(
            (12, 1),
            ModuleRow {
                name: "Loop".into(),
                parent: (12, 1),
            },
        )]);
        assert!(
            module_chain("Procedure", (12, 1), &cyclic).is_err(),
            "cycle"
        );
    }

    #[test]
    fn only_an_unshared_procedure_name_is_selected() {
        let object = |name: &str, kind: ObjectKind, id: i32| ObjectRow {
            name: name.into(),
            kind,
            entity_type_id: if kind == ObjectKind::Procedure { 38 } else { 7 },
            entity_id: id,
            entity_version_id: 1,
        };
        let entity = |row: &ObjectRow| ModelEntityVersionRecord {
            model_id: 1,
            entity_type_id: row.entity_type_id,
            entity_id: row.entity_id,
            entity_version_id: 1,
            name: row.name.clone(),
        };
        let proc_row = object("Proc", ObjectKind::Procedure, 1);
        let other = object("Other", ObjectKind::Procedure, 2);
        let objects = [proc_row.clone(), other.clone()];
        let entities = [entity(&proc_row), entity(&other)];
        assert_eq!(
            select_object_of_kind(&objects, &entities, ObjectKind::Procedure, "proc")
                .unwrap()
                .entity_id,
            1
        );
        assert!(select_object_of_kind(&objects, &entities, ObjectKind::Procedure, "Missing").is_err());
        let twin = object("PROC", ObjectKind::Procedure, 3);
        let twins = [proc_row.clone(), twin.clone()];
        assert!(select_object_of_kind(&twins, &[entity(&proc_row), entity(&twin)], ObjectKind::Procedure, "Proc").is_err());
        let table = object("proc", ObjectKind::Transaction, 4);
        assert!(select_object_of_kind(&objects, &[entity(&proc_row), entity(&table)], ObjectKind::Procedure, "Proc").is_err());
    }

    #[test]
    fn memory_store_cannot_claim_an_inspection() {
        let client = UdmClient::new_in_memory();
        assert!(
            client
                .procedure_inspect_minimal(None, "AddDeviceGroups")
                .is_err()
        );
    }
}
