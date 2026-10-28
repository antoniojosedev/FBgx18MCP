//! Bounded Procedure browsing; other kinds and non-ASCII/colliding names retain SDK ordering.

use super::UdmClient;
use crate::catalog::ObjectKind;
use crate::udm::reader::{ObjectRow, UdmReader};
use crate::udm::version_scope::{ActiveVersion, ScopeUse, select_design_model};
use serde::Serialize;
use std::collections::BTreeSet;
use std::sync::Arc;

#[derive(Debug, Serialize)]
pub struct ObjectPage {
    pub objects: Vec<ObjectListItem>,
    pub total: usize,
    pub offset: usize,
    pub limit: usize,
}

pub type ProcedurePage = ObjectPage;

#[derive(Debug, Serialize)]
pub struct ObjectListItem {
    pub name: String,
    #[serde(rename = "type")]
    pub object_type: &'static str,
    pub last_update_ticks: Option<i64>,
}

impl UdmClient {
    /// Resolve active scope, enumerate, and read page lifecycle facts in one fresh read view.
    pub fn procedure_list_page(
        &self,
        active: Option<ActiveVersion>,
        offset: usize,
        limit: usize,
    ) -> Result<ProcedurePage, String> {
        self.object_list_page(active, ObjectKind::Procedure, offset, limit)
    }

    /// Resolve active scope, enumerate objects of `kind`, and read page lifecycle facts in one view.
    pub fn object_list_page(
        &self,
        active: Option<ActiveVersion>,
        kind: ObjectKind,
        offset: usize,
        limit: usize,
    ) -> Result<ObjectPage, String> {
        if !(1..=200).contains(&limit) || offset > i32::MAX as usize {
            return Err(
                "native listing requires limit 1..200 and an Int32 nonnegative offset".into(),
            );
        }
        let kind_name = crate::kb_ops::type_name(kind);
        self.with_read_view(|view| {
            let model = select_design_model(&view.model_topology()?, active, ScopeUse::Read)
                .map_err(|refusal| refusal.0)?;
            let client = view.clone().with_design_model_id(model);
            let rows = UdmReader::new(Arc::new(client.clone())).list_objects()?;
            let ordered = ordered_objects_of_kind(&rows, kind)?;
            // A zero result cannot certify the SDK catalog is empty.
            if ordered.is_empty() {
                return Err(format!(
                    "an empty native {kind_name} catalog is not authoritative"
                ));
            }
            let page: Vec<&ObjectRow> = ordered.iter().copied().skip(offset).take(limit).collect();
            let entity_type_id = page
                .first()
                .map_or(ordered[0].entity_type_id, |row| row.entity_type_id);
            if page.iter().any(|row| row.entity_type_id != entity_type_id) {
                return Err(format!(
                    "{kind_name} listing requires a single {kind_name} EntityTypeId"
                ));
            }
            let page_versions: Vec<(i32, i32)> = page
                .iter()
                .map(|row| (row.entity_id, row.entity_version_id))
                .collect();
            let lifecycle_ticks =
                client.get_objects_lifecycle_ticks(model, entity_type_id, &page_versions)?;
            let objects = page
                .into_iter()
                .map(|row| {
                    let last_update_ticks = lifecycle_ticks
                        .get(&row.entity_id)
                        .copied()
                        .ok_or_else(|| format!("{kind_name} lifecycle metadata is missing"))?;
                    Ok(ObjectListItem {
                        name: row.name.clone(),
                        object_type: kind_name,
                        last_update_ticks,
                    })
                })
                .collect::<Result<Vec<_>, String>>()?;
            Ok(ObjectPage {
                objects,
                total: ordered.len(),
                offset,
                limit,
            })
        })
    }
}

fn ordered_objects_of_kind<'a>(
    rows: &'a [ObjectRow],
    kind: ObjectKind,
) -> Result<Vec<&'a ObjectRow>, String> {
    let mut seen = BTreeSet::new();
    let mut objects = Vec::new();
    let kind_name = crate::kb_ops::type_name(kind);
    for row in rows.iter().filter(|row| row.kind == kind) {
        // ASCII case folding agrees with OrdinalIgnoreCase. A case collision would need the
        // SDK's GUID tie-break; refuse it rather than borrowing another operation's ordering.
        if row.name.is_empty()
            || !row.name.is_ascii()
            || row.name.chars().any(char::is_control)
            || !seen.insert(row.name.to_ascii_uppercase())
        {
            return Err(format!(
                "{kind_name} listing requires nonempty unique ASCII names for SDK ordering"
            ));
        }
        objects.push(row);
    }
    // .NET OrdinalIgnoreCase compares upper-case code units: `_` sorts after `Z`, not before `a`.
    objects.sort_by_cached_key(|row| row.name.to_ascii_uppercase());
    Ok(objects)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn procedure(name: &str, id: i32) -> ObjectRow {
        ObjectRow {
            name: name.into(),
            kind: ObjectKind::Procedure,
            entity_type_id: 1,
            entity_id: id,
            entity_version_id: 1,
        }
    }

    #[test]
    fn listing_uses_sdk_case_insensitive_name_order_not_query_order() {
        let mut rows = vec![
            procedure("alpha", 1),
            procedure("Zoo", 2),
            procedure("beta", 3),
            procedure("_tail", 5),
        ];
        let mut other = procedure("ignored", 4);
        other.kind = ObjectKind::Transaction;
        rows.push(other);
        let names: Vec<_> = ordered_objects_of_kind(&rows, ObjectKind::Procedure)
            .unwrap()
            .iter()
            .map(|row| row.name.as_str())
            .collect();
        assert_eq!(names, ["alpha", "beta", "Zoo", "_tail"]);
    }

    #[test]
    fn listing_refuses_names_whose_sdk_order_is_not_proven() {
        for name in ["alpha", "ALPHA", "", "a\nb", "Produ\u{e7}\u{e3}o"] {
            assert!(
                ordered_objects_of_kind(&[procedure("alpha", 1), procedure(name, 2)], ObjectKind::Procedure).is_err(),
                "{name:?}"
            );
        }
    }

    #[test]
    fn listing_rejects_unbounded_and_overflowing_pages_before_reading() {
        let client = UdmClient::new_in_memory();
        for (offset, limit) in [(0, 0), (0, 201), (usize::MAX, 3)] {
            assert!(
                client
                    .procedure_list_page(None, offset, limit)
                    .unwrap_err()
                    .contains("limit 1..200")
            );
        }
    }
}

#[cfg(test)]
mod kinds_tests {
    use super::*;

    #[test]
    fn empty_memory_catalog_cannot_claim_empty_sdk_listing() {
        assert!(
            UdmClient::new_in_memory()
                .procedure_list_page(None, 0, 3)
                .unwrap_err()
                .contains("not authoritative")
        );
        assert!(
            UdmClient::new_in_memory()
                .object_list_page(None, ObjectKind::Transaction, 0, 3)
                .unwrap_err()
                .contains("not authoritative")
        );
    }

    #[test]
    fn listing_orders_transactions_independently() {
        let rows = vec![
            ObjectRow {
                name: "Customer".into(),
                kind: ObjectKind::Transaction,
                entity_type_id: 2,
                entity_id: 1,
                entity_version_id: 1,
            },
            ObjectRow {
                name: "Account".into(),
                kind: ObjectKind::Transaction,
                entity_type_id: 2,
                entity_id: 2,
                entity_version_id: 1,
            },
            ObjectRow {
                name: "AlphaProc".into(),
                kind: ObjectKind::Procedure,
                entity_type_id: 1,
                entity_id: 3,
                entity_version_id: 1,
            },
        ];
        let ordered = ordered_objects_of_kind(&rows, ObjectKind::Transaction).unwrap();
        let names: Vec<_> = ordered.iter().map(|r| r.name.as_str()).collect();
        assert_eq!(names, ["Account", "Customer"]);
    }
}
