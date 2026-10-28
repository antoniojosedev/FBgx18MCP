//! Bounded Procedure browsing; other kinds and non-ASCII/colliding names retain SDK ordering.

use super::UdmClient;
use crate::catalog::ObjectKind;
use crate::udm::reader::{ObjectRow, UdmReader};
use crate::udm::version_scope::{ActiveVersion, ScopeUse, select_design_model};
use serde::Serialize;
use std::collections::BTreeSet;
use std::sync::Arc;

#[derive(Debug, Serialize)]
pub struct ProcedurePage {
    pub objects: Vec<ProcedureListItem>,
    pub total: usize,
    pub offset: usize,
    pub limit: usize,
}

#[derive(Debug, Serialize)]
pub struct ProcedureListItem {
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
        if !(1..=200).contains(&limit) || offset > i32::MAX as usize {
            return Err(
                "native listing requires limit 1..200 and an Int32 nonnegative offset".into(),
            );
        }
        self.with_read_view(|view| {
            let model = select_design_model(&view.model_topology()?, active, ScopeUse::Read)
                .map_err(|refusal| refusal.0)?;
            let client = view.clone().with_design_model_id(model);
            let rows = UdmReader::new(Arc::new(client.clone())).list_objects()?;
            let ordered = ordered_procedures(&rows)?;
            // A zero result cannot certify the SDK catalog is empty.
            if ordered.is_empty() {
                return Err("an empty native Procedure catalog is not authoritative".into());
            }
            let objects = ordered
                .iter()
                .skip(offset)
                .take(limit)
                .map(|row| {
                    let metadata = client
                        .get_object_read_metadata(
                            model,
                            row.entity_type_id,
                            row.entity_id,
                            row.entity_version_id,
                        )?
                        .ok_or_else(|| "Procedure lifecycle metadata is missing".to_string())?;
                    Ok(ProcedureListItem {
                        name: row.name.clone(),
                        object_type: "Procedure",
                        last_update_ticks: metadata
                            .object_saved_ticks
                            .or(metadata.model_entity_version_ticks),
                    })
                })
                .collect::<Result<Vec<_>, String>>()?;
            Ok(ProcedurePage {
                objects,
                total: ordered.len(),
                offset,
                limit,
            })
        })
    }
}

fn ordered_procedures(rows: &[ObjectRow]) -> Result<Vec<&ObjectRow>, String> {
    let mut seen = BTreeSet::new();
    let mut procedures = Vec::new();
    for row in rows.iter().filter(|row| row.kind == ObjectKind::Procedure) {
        // ASCII case folding agrees with OrdinalIgnoreCase. A case collision would need the
        // SDK's GUID tie-break; refuse it rather than borrowing another operation's ordering.
        if row.name.is_empty()
            || !row.name.is_ascii()
            || row.name.chars().any(char::is_control)
            || !seen.insert(row.name.to_ascii_uppercase())
        {
            return Err(
                "Procedure listing requires nonempty unique ASCII names for SDK ordering".into(),
            );
        }
        procedures.push(row);
    }
    // .NET OrdinalIgnoreCase compares upper-case code units: `_` sorts after `Z`, not before `a`.
    procedures.sort_by_cached_key(|row| row.name.to_ascii_uppercase());
    Ok(procedures)
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
        let names: Vec<_> = ordered_procedures(&rows)
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
                ordered_procedures(&[procedure("alpha", 1), procedure(name, 2)]).is_err(),
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

    #[test]
    fn empty_memory_catalog_cannot_claim_empty_sdk_listing() {
        assert!(
            UdmClient::new_in_memory()
                .procedure_list_page(None, 0, 3)
                .unwrap_err()
                .contains("not authoritative")
        );
    }
}
