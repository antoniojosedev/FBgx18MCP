//! The SQL of the build facts, pinned to one Design model.
//!
//! A versioned Knowledge Base stores one `EntityVersion` row per change of an entity, whichever KB version
//! made it, and one `ModelEntityVersion` row per (model, entity) naming the row that model uses. The latest
//! `EntityVersion` row of an entity can therefore belong to another version's branch; only the model's own
//! `ModelEntityVersion` row says which version of the entity a build of that model sees. Every fact a build
//! reads that a model holds is read through that row, never by "latest".

use super::live_rows::parse_live_rows;
use super::*;

/// The `JOIN` that restricts the `EntityVersion` aliased `v` to the version Design model `model_id` holds
/// of its entity.
fn model_version_join(model_id: i32) -> String {
    format!(
        "JOIN ModelEntityVersion me ON me.ModelId = {model_id} AND me.EntityTypeId = v.EntityTypeId AND me.EntityId = v.EntityId AND me.EntityVersionId = v.EntityVersionId"
    )
}

pub(super) fn attribute_facts_sql(model_id: i32) -> String {
    format!(
        "SET NOCOUNT ON; SELECT v.EntityId, v.EntityVersionName, ISNULL(DATALENGTH(v.EntityVersionProperties), 0), CONVERT(VARCHAR(MAX), CAST(v.EntityVersionProperties AS VARBINARY(MAX)), 2) FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'Attribute' {}",
        model_version_join(model_id)
    )
}

pub(super) fn subtype_groups_sql(model_id: i32) -> String {
    format!(
        "SET NOCOUNT ON; SELECT DISTINCT a.EntityVersionName, ISNULL(DATALENGTH(g.EntityVersionDescription), 0), CONVERT(VARCHAR(MAX), CAST(g.EntityVersionDescription AS VARBINARY(MAX)), 2) FROM ModelCrossReference x JOIN EntityType gt ON gt.EntityTypeId = x.FromEntityTypeId AND gt.EntityTypeName = 'Group' JOIN EntityType at ON at.EntityTypeId = x.ToEntityTypeId AND at.EntityTypeName = 'Attribute' JOIN EntityVersion g ON g.EntityTypeId = x.FromEntityTypeId AND g.EntityId = x.FromEntityId JOIN ModelEntityVersion gm ON gm.ModelId = {model_id} AND gm.EntityTypeId = g.EntityTypeId AND gm.EntityId = g.EntityId AND gm.EntityVersionId = g.EntityVersionId JOIN EntityVersion a ON a.EntityTypeId = x.ToEntityTypeId AND a.EntityId = x.ToEntityId JOIN ModelEntityVersion am ON am.ModelId = {model_id} AND am.EntityTypeId = a.EntityTypeId AND am.EntityId = a.EntityId AND am.EntityVersionId = a.EntityVersionId WHERE x.ModelId = {model_id}"
    )
}

pub(super) fn table_keys_sql(model_id: i32) -> String {
    format!(
        "SET NOCOUNT ON; SELECT DISTINCT v.EntityVersionName, m.TablePK FROM ModelEntityToTable m JOIN EntityType t ON t.EntityTypeId = m.EntityTypeId AND t.EntityTypeName = 'Table' JOIN EntityVersion v ON v.EntityTypeId = m.EntityTypeId AND v.EntityId = m.EntityId {} WHERE m.ModelId = {model_id}",
        model_version_join(model_id)
    )
}

pub(super) fn language_names_sql(model_id: i32) -> String {
    format!(
        "SET NOCOUNT ON; SELECT v.EntityId, v.EntityVersionName FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'Language' {}",
        model_version_join(model_id)
    )
}

/// The stored property values of the Design model and of the environments below it. `list` is the quoted,
/// already validated `'Property: <name>'` names.
pub(super) fn stored_model_properties_sql(model_id: i32, list: &str) -> String {
    format!(
        "SET NOCOUNT ON; SELECT SUBSTRING(p.EntityVersionName, 11, 200), ISNULL(m.ModelEntityPropertyValue, '') FROM ModelEntityProperty m JOIN UdmModel sm ON sm.ModelId = m.ModelId AND (sm.ModelId = {model_id} OR sm.ModelParentId = {model_id}) JOIN EntityVersion p ON p.EntityId = m.ModelEntityPropertyId AND p.EntityVersionName IN ({list}) JOIN EntityType t ON t.EntityTypeId = p.EntityTypeId AND t.EntityTypeName = 'Udm.Types.Property'"
    )
}

/// `type_guid` is validated hex and dashes, `entity_id` a number: neither can break out of the literal.
pub(super) fn design_system_name_sql(model_id: i32, type_guid: &str, entity_id: i64) -> String {
    format!(
        "SET NOCOUNT ON; SELECT v.EntityVersionName FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'DesignSystem' JOIN Entity g ON g.EntityTypeId = 1 AND g.EntityId = t.EntityTypeId AND LOWER(CONVERT(VARCHAR(36), g.EntityGuid)) = '{type_guid}' {} WHERE v.EntityId = {entity_id}",
        model_version_join(model_id)
    )
}

/// The entity types a build reads that no model holds a version of (`ModelEntityVersion` has no row for
/// them), so they cannot be pinned to a Design model and are read at their latest version.
const UNPINNED_BUILD_ENTITY_TYPES: [&str; 2] = ["TranslationMessage", "DesignSystemClass"];

pub(super) fn unpinned_build_conflict_sql() -> String {
    let types = UNPINNED_BUILD_ENTITY_TYPES
        .iter()
        .map(|name| format!("'{name}'"))
        .collect::<Vec<_>>()
        .join(", ");
    format!(
        "SET NOCOUNT ON; SELECT TOP 1 t.EntityTypeName, v.EntityId, COUNT(*) FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName IN ({types}) GROUP BY t.EntityTypeName, v.EntityId HAVING COUNT(*) > 1 ORDER BY t.EntityTypeName, v.EntityId"
    )
}

impl UdmClient {
    /// Why a build of a versioned Knowledge Base cannot be pinned to this client's Design model, or `None`.
    /// A build reads `TranslationMessage` and `DesignSystemClass` entities at their latest version, because
    /// no model holds a version of them. That is exact while each stores one version; an entity with several
    /// records a change some KB version made that may not be this model's, so it is refused rather than
    /// guessed. An in-memory client has none.
    pub fn unpinned_build_fact_conflict(&self) -> Result<Option<String>, String> {
        if !self.is_live() {
            return Ok(None);
        }
        let raw = self.execute_live_sql(&unpinned_build_conflict_sql())?;
        describe_unpinned_conflict(&raw)
    }
}

/// The refusal text for the first row of the conflict query's answer, or `None` for an empty answer.
fn describe_unpinned_conflict(raw: &str) -> Result<Option<String>, String> {
    Ok(parse_live_rows(raw, 3)?.first().map(|row| {
        format!(
            "{} entity {} stores {} versions and no Design model holds one of them, so native build facts cannot be pinned to the active KB version",
            row[0], row[1], row[2]
        )
    }))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A model other than the default, so a query that ignores the model cannot pass by accident.
    const MODEL: i32 = 41;

    fn without_latest(sql: &str) -> bool {
        !sql.contains("MAX(") && !sql.contains("MAX (")
    }

    #[test]
    fn model_entity_facts_use_the_version_the_design_model_holds_not_the_latest() {
        for (name, sql) in [
            ("attributes", attribute_facts_sql(MODEL)),
            ("table keys", table_keys_sql(MODEL)),
            ("languages", language_names_sql(MODEL)),
            ("theme design system", design_system_name_sql(MODEL, "g", 6)),
        ] {
            assert!(
                sql.contains(&format!(
                    "JOIN ModelEntityVersion me ON me.ModelId = {MODEL} AND"
                )),
                "{name}: {sql}"
            );
            assert!(
                sql.contains("me.EntityVersionId = v.EntityVersionId"),
                "{name}"
            );
            assert!(
                without_latest(&sql),
                "{name} reads the latest version: {sql}"
            );
        }
    }

    #[test]
    fn table_keys_and_subtype_groups_read_only_the_design_models_rows() {
        assert!(table_keys_sql(MODEL).contains(&format!("WHERE m.ModelId = {MODEL}")));
        let groups = subtype_groups_sql(MODEL);
        assert!(
            groups.contains(&format!("WHERE x.ModelId = {MODEL}")),
            "{groups}"
        );
        for alias in ["gm", "am"] {
            assert!(
                groups.contains(&format!(
                    "JOIN ModelEntityVersion {alias} ON {alias}.ModelId = {MODEL} AND"
                )),
                "{alias}: {groups}"
            );
        }
        assert!(without_latest(&groups), "{groups}");
    }

    #[test]
    fn model_properties_come_from_the_design_model_and_its_environments_only() {
        let sql = stored_model_properties_sql(MODEL, "'Property: threshold'");
        assert!(
            sql.contains(&format!(
                "(sm.ModelId = {MODEL} OR sm.ModelParentId = {MODEL})"
            )),
            "{sql}"
        );
        assert!(sql.contains("IN ('Property: threshold')"), "{sql}");
    }

    #[test]
    fn the_unpinned_entity_guard_names_exactly_the_types_no_model_holds() {
        let sql = unpinned_build_conflict_sql();
        assert!(
            sql.contains("IN ('TranslationMessage', 'DesignSystemClass')"),
            "{sql}"
        );
        assert!(sql.contains("HAVING COUNT(*) > 1"), "{sql}");
    }

    #[test]
    fn an_entity_with_several_versions_is_named_in_the_refusal_and_none_is_no_refusal() {
        let refusal = describe_unpinned_conflict(
            "TranslationMessage	12	2
",
        )
        .unwrap()
        .unwrap();
        assert!(
            refusal.contains("TranslationMessage entity 12 stores 2 versions")
                && refusal.contains("cannot be pinned"),
            "{refusal}"
        );
        assert_eq!(describe_unpinned_conflict("").unwrap(), None);
        assert!(
            describe_unpinned_conflict(
                "TranslationMessage	12
"
            )
            .is_err()
        );
    }

    #[test]
    fn an_in_memory_client_has_no_unpinned_conflict() {
        assert_eq!(
            UdmClient::new_in_memory().unpinned_build_fact_conflict(),
            Ok(None)
        );
    }
}
