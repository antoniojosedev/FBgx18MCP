//! The KB version tree (frozen versions and development branches) from stored
//! `Udm.Types.KnowledgeBaseVersions` entities. The trunk is not one of them: it is named after the
//! Knowledge Base, like the SDK's `KBVersion.GetAll`, which lists every version except the trunk.
//! Anything the SDK could interpret differently (hidden versions, dangling parents or users,
//! versions without exactly one Design model) is refused rather than guessed.

use super::UdmClient;
use super::live_rows::{
    decode_hex_value, decode_nvarchar, nvarchar_hex_columns, parse_live_int, parse_live_rows,
};
use crate::udm::decoder::decode_blob;
use crate::udm::version_scope::{
    ActiveVersion, ModelTopologyRow, ScopeUse, property_scalars, select_design_model,
};
use serde::Serialize;
use std::collections::BTreeMap;

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct KbVersionList {
    /// The trunk's name (the Knowledge Base name); parent of top-level versions.
    pub trunk: String,
    /// The active version's id (`0` is the trunk) and name.
    pub active_version_id: i32,
    pub active_version: String,
    /// Every non-trunk version, by ascending stored id.
    pub versions: Vec<KbVersionEntry>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct KbVersionEntry {
    pub id: i32,
    pub name: String,
    pub description: String,
    pub is_frozen: bool,
    pub parent_id: i32,
    pub parent: String,
    pub design_model_id: i32,
    /// `EntityVersionTimestamp` (stored in UTC) as .NET ticks, millisecond resolution.
    pub last_update_ticks: i64,
    pub user_name: String,
}

/// One stored version entity, before its parent, user and model are resolved.
#[derive(Debug, Clone, PartialEq, Eq)]
struct StoredVersion {
    id: i32,
    name: String,
    description: String,
    properties: String,
    user_id: i32,
    timestamp: String,
    data: String,
}

impl UdmClient {
    /// The version tree as the SDK lists it, read with the active scope in one fresh read view.
    pub fn kb_version_list(&self, active: Option<ActiveVersion>) -> Result<KbVersionList, String> {
        if !self.is_live() {
            return Err("the in-memory store has no KB version metadata".into());
        }
        self.with_read_view(|view| {
            let topology = view.model_topology()?;
            select_design_model(&topology, active, ScopeUse::Read).map_err(|refusal| refusal.0)?;
            let stored = view.stored_versions()?;
            let users = view.entity_names("Udm.Types.User")?;
            let knowledge_bases = view.entity_names("Udm.Types.KnowledgeBase")?;
            let [(_, trunk)] = <[(i32, String); 1]>::try_from(knowledge_bases).map_err(|_| {
                "the trunk is named after exactly one stored Knowledge Base entity".to_string()
            })?;
            assemble(
                &topology,
                active,
                trunk,
                &stored,
                &users.into_iter().collect(),
            )
        })
    }

    fn stored_versions(&self) -> Result<Vec<StoredVersion>, String> {
        let name = nvarchar_hex_columns("v.EntityVersionName");
        let description = nvarchar_hex_columns("v.EntityVersionDescription");
        let properties = nvarchar_hex_columns("v.EntityVersionProperties");
        // Each version's latest entity version; the entity count proves none was skipped.
        let sql = format!(
            "SET NOCOUNT ON; SELECT v.EntityId, v.UserId, CONVERT(VARCHAR(23), v.EntityVersionTimestamp, 121), {name}, {description}, {properties}, ISNULL(DATALENGTH(v.EntityVersionData), 0), CONVERT(VARCHAR(MAX), v.EntityVersionData, 2), (SELECT COUNT(*) FROM Entity x WHERE x.EntityTypeId = t.EntityTypeId) FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = 'Udm.Types.KnowledgeBaseVersions' JOIN Entity e ON e.EntityTypeId = v.EntityTypeId AND e.EntityId = v.EntityId AND e.EntityLastVersionId = v.EntityVersionId WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId) ORDER BY v.EntityId"
        );
        let raw = self.execute_live_sql(&sql)?;
        let rows = parse_live_rows(&raw, 12)?;
        let versions = rows
            .iter()
            .map(|row| {
                let id = parse_live_int(row[0].trim(), "version EntityId")?;
                let length = usize::try_from(parse_live_int(row[9].trim(), "version data length")?)
                    .map_err(|_| format!("version {id} data length is negative"))?;
                Ok(StoredVersion {
                    id,
                    user_id: parse_live_int(row[1].trim(), "version UserId")?,
                    timestamp: row[2].trim().to_string(),
                    name: decode_nvarchar(row[3], row[4], "version name")?,
                    description: decode_nvarchar(row[5], row[6], "version description")?,
                    properties: decode_nvarchar(row[7], row[8], "version properties")?,
                    data: decode_blob(&decode_hex_value(row[10].trim(), length)?)
                        .map_err(|error| format!("version {id} data: {error}"))?,
                })
            })
            .collect::<Result<Vec<_>, String>>()?;
        for row in &rows {
            if usize::try_from(parse_live_int(row[11].trim(), "version entity count")?)
                != Ok(versions.len())
            {
                return Err("a stored version entity has no matching latest entity version".into());
            }
        }
        Ok(versions)
    }

    /// The latest stored name of every entity of `entity_type`, by id.
    pub(super) fn entity_names(&self, entity_type: &str) -> Result<Vec<(i32, String)>, String> {
        let name = nvarchar_hex_columns("v.EntityVersionName");
        let sql = format!(
            "SET NOCOUNT ON; SELECT v.EntityId, {name} FROM EntityVersion v JOIN EntityType t ON t.EntityTypeId = v.EntityTypeId AND t.EntityTypeName = '{entity_type}' WHERE v.EntityVersionId = (SELECT MAX(l.EntityVersionId) FROM EntityVersion l WHERE l.EntityTypeId = v.EntityTypeId AND l.EntityId = v.EntityId) ORDER BY v.EntityId"
        );
        self.live_records(&sql, 3, |row| {
            Ok((
                parse_live_int(row[0].trim(), "EntityId")?,
                decode_nvarchar(row[1], row[2], entity_type)?,
            ))
        })
    }
}

fn assemble(
    topology: &[ModelTopologyRow],
    active: Option<ActiveVersion>,
    trunk: String,
    stored: &[StoredVersion],
    users: &BTreeMap<i32, String>,
) -> Result<KbVersionList, String> {
    usable_name(&trunk, "trunk")?;
    let mut names = BTreeMap::from([(0, trunk.clone())]);
    let mut entries = Vec::with_capacity(stored.len());
    for version in stored {
        let entry = entry(version, topology, users)?;
        if names.insert(entry.id, entry.name.clone()).is_some() {
            return Err(format!("version id {} is stored twice", entry.id));
        }
        entries.push(entry);
    }
    for entry in &mut entries {
        entry.parent = names
            .get(&entry.parent_id)
            .filter(|_| entry.parent_id != entry.id)
            .ok_or_else(|| format!("version {} names a missing parent", entry.name))?
            .clone();
    }
    // Every versioned Design model must belong to a listed version, or the SDK lists one we cannot.
    for design in topology.iter().filter(|row| row.parent_id == 0) {
        if !names.contains_key(&design.kb_version.unwrap_or(0)) {
            return Err(format!(
                "Design model {} belongs to an unlisted version",
                design.model_id
            ));
        }
    }
    let active_version_id = active.map_or(0, |active| active.kb_version_id);
    let active_version = names
        .get(&active_version_id)
        .ok_or("the active version is not stored")?
        .clone();
    Ok(KbVersionList {
        trunk,
        active_version_id,
        active_version,
        versions: entries,
    })
}

fn entry(
    version: &StoredVersion,
    topology: &[ModelTopologyRow],
    users: &BTreeMap<i32, String>,
) -> Result<KbVersionEntry, String> {
    let id = version.id;
    if id <= 0 {
        return Err(format!("version id {id} is not a positive id"));
    }
    let properties = property_scalars(
        &version.properties,
        &["ParentId", "IsFrozen", "Name", "Description", "Visible"],
    )
    .map_err(|error| format!("version {id}: {error}"))?;
    let property = |name: &str| {
        properties
            .get(name)
            .ok_or_else(|| format!("version {id} stores no {name} property"))
    };
    if property("Name")? != &version.name || property("Description")? != &version.description {
        return Err(format!(
            "version {id} name or description differs between its row and its properties"
        ));
    }
    usable_name(&version.name, "version")?;
    if property("Visible")? != "True" {
        return Err(format!(
            "version {id} is not stored as visible; the SDK's handling of hidden versions is unverified"
        ));
    }
    let is_frozen = match property("IsFrozen")?.as_str() {
        "True" => true,
        "False" => false,
        _ => return Err(format!("version {id} IsFrozen is not True or False")),
    };
    let parent_id = property("ParentId")?
        .parse::<i32>()
        .ok()
        .filter(|parent| *parent >= 0)
        .ok_or_else(|| format!("version {id} ParentId is not a nonnegative integer"))?;
    let design_model_id =
        model_reference(&version.data).map_err(|error| format!("version {id} data: {error}"))?;
    let designs: Vec<_> = topology
        .iter()
        .filter(|row| row.parent_id == 0 && row.kb_version == Some(id))
        .collect();
    if !matches!(designs.as_slice(), [design] if design.model_id == design_model_id) {
        return Err(format!(
            "version {id} does not own exactly the Design model its data names"
        ));
    }
    let user_name = users
        .get(&version.user_id)
        .ok_or_else(|| format!("version {id} names a missing user"))?
        .clone();
    Ok(KbVersionEntry {
        id,
        name: version.name.clone(),
        description: version.description.clone(),
        is_frozen,
        parent_id,
        parent: String::new(),
        design_model_id,
        last_update_ticks: sql_datetime_ticks(&version.timestamp)
            .ok_or_else(|| format!("version {id} timestamp is not a SQL datetime"))?,
        user_name,
    })
}

fn usable_name(name: &str, what: &str) -> Result<(), String> {
    if name.trim().is_empty() || name.chars().any(char::is_control) {
        return Err(format!("a stored {what} name is empty or malformed"));
    }
    Ok(())
}

/// The model id of a version's `<Model Id="n" />` data, the only shape accepted.
fn model_reference(xml: &str) -> Result<i32, String> {
    let document =
        roxmltree::Document::parse(xml).map_err(|error| format!("invalid XML: {error}"))?;
    let root = document.root_element();
    if !root.has_tag_name("Model")
        || root
            .children()
            .any(|node| !node.is_text() || !node.text().unwrap_or("").trim().is_empty())
        || root.attributes().count() != 1
    {
        return Err("expected a single <Model Id=\"n\" /> element".into());
    }
    root.attribute("Id")
        .and_then(|id| id.parse::<i32>().ok())
        .filter(|id| *id > 0)
        .ok_or_else(|| "Model Id is not a positive integer".into())
}

/// .NET ticks of a `CONVERT(VARCHAR(23), datetime, 121)` value (`yyyy-mm-dd hh:mi:ss.mmm`).
fn sql_datetime_ticks(text: &str) -> Option<i64> {
    let bytes = text.as_bytes();
    if bytes.len() != 23
        || bytes[4] != b'-'
        || bytes[7] != b'-'
        || bytes[10] != b' '
        || bytes[13] != b':'
        || bytes[16] != b':'
        || bytes[19] != b'.'
    {
        return None;
    }
    let number = |range: std::ops::Range<usize>| -> Option<i64> {
        let digits = text.get(range)?;
        digits
            .bytes()
            .all(|b| b.is_ascii_digit())
            .then(|| digits.parse().ok())?
    };
    let (year, month, day) = (number(0..4)?, number(5..7)?, number(8..10)?);
    let (hour, minute, second, milli) = (
        number(11..13)?,
        number(14..16)?,
        number(17..19)?,
        number(20..23)?,
    );
    if !(1..=12).contains(&month) || day < 1 || day > days_in_month(year, month) {
        return None;
    }
    if hour > 23 || minute > 59 || second > 59 {
        return None;
    }
    // Days since 0001-01-01 (proleptic Gregorian, as .NET DateTime counts).
    let years = year - 1;
    let mut days = years * 365 + years / 4 - years / 100 + years / 400;
    days += (1..month).map(|m| days_in_month(year, m)).sum::<i64>() + day - 1;
    let millis = ((days * 24 + hour) * 60 + minute) * 60_000 + second * 1000 + milli;
    Some(millis * 10_000)
}

fn days_in_month(year: i64, month: i64) -> i64 {
    match month {
        2 if year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) => 29,
        2 => 28,
        4 | 6 | 9 | 11 => 30,
        _ => 31,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn properties(parent: &str, frozen: &str, name: &str, visible: &str) -> String {
        format!(
            "<Properties><Property><Name>ParentId</Name><Value>{parent}</Value></Property><Property><Name>IsFrozen</Name><Value>{frozen}</Value></Property><Property><Name>Name</Name><Value>{name}</Value></Property><Property><Name>Description</Name><Value>d</Value></Property><Property><Name>Visible</Name><Value>{visible}</Value></Property></Properties>"
        )
    }

    fn stored(id: i32, name: &str, parent: i32, frozen: bool, model: i32) -> StoredVersion {
        StoredVersion {
            id,
            name: name.into(),
            description: "d".into(),
            properties: properties(
                &parent.to_string(),
                if frozen { "True" } else { "False" },
                name,
                "True",
            ),
            user_id: 1,
            timestamp: "2026-10-21 15:33:54.000".into(),
            data: format!("<Model Id=\"{model}\" />"),
        }
    }

    fn design(model_id: i32, kb_version: Option<i32>) -> ModelTopologyRow {
        ModelTopologyRow {
            model_id,
            parent_id: 0,
            kb_version,
        }
    }

    fn fixture() -> (
        Vec<ModelTopologyRow>,
        Vec<StoredVersion>,
        BTreeMap<i32, String>,
    ) {
        (
            vec![design(1, None), design(3, Some(1)), design(4, Some(2))],
            vec![
                stored(1, "Freeze", 0, true, 3),
                stored(2, "Branch", 1, false, 4),
            ],
            BTreeMap::from([(1, "DOMAIN\\user".to_string())]),
        )
    }

    fn active(design_model_id: i32, kb_version_id: i32) -> Option<ActiveVersion> {
        Some(ActiveVersion {
            design_model_id,
            kb_version_id,
        })
    }

    #[test]
    fn versions_resolve_parents_users_models_and_the_active_trunk() {
        let (topology, versions, users) = fixture();
        let list = assemble(&topology, active(1, 0), "KB".into(), &versions, &users).unwrap();
        assert_eq!(list.active_version, "KB");
        assert_eq!(list.active_version_id, 0);
        let freeze = &list.versions[0];
        assert_eq!(
            (
                freeze.name.as_str(),
                freeze.parent.as_str(),
                freeze.is_frozen,
                freeze.design_model_id
            ),
            ("Freeze", "KB", true, 3)
        );
        let branch = &list.versions[1];
        assert_eq!(
            (
                branch.name.as_str(),
                branch.parent.as_str(),
                branch.is_frozen,
                branch.design_model_id
            ),
            ("Branch", "Freeze", false, 4)
        );
        assert_eq!(branch.user_name, "DOMAIN\\user");
        assert_eq!(freeze.last_update_ticks, 639_281_936_340_000_000);
        let on_branch = assemble(&topology, active(4, 2), "KB".into(), &versions, &users).unwrap();
        assert_eq!(on_branch.active_version, "Branch");
    }

    #[test]
    fn ambiguous_or_unresolvable_versions_are_refused() {
        let (topology, versions, users) = fixture();
        let refuse = |topology: &[ModelTopologyRow],
                      versions: &[StoredVersion],
                      users: &BTreeMap<i32, String>| {
            assemble(topology, active(1, 0), "KB".into(), versions, users).is_err()
        };
        let mut hidden = versions.clone();
        hidden[0].properties = properties("0", "True", "Freeze", "False");
        assert!(refuse(&topology, &hidden, &users), "hidden version");
        let mut renamed = versions.clone();
        renamed[0].properties = properties("0", "True", "Other", "True");
        assert!(
            refuse(&topology, &renamed, &users),
            "row/property name mismatch"
        );
        let mut orphan = versions.clone();
        orphan[1].properties = properties("9", "False", "Branch", "True");
        assert!(refuse(&topology, &orphan, &users), "missing parent");
        let mut wrong_model = versions.clone();
        wrong_model[1].data = "<Model Id=\"3\" />".into();
        assert!(
            refuse(&topology, &wrong_model, &users),
            "model of another version"
        );
        assert!(
            refuse(&topology, &versions[..1], &users),
            "unlisted versioned Design model"
        );
        assert!(
            refuse(&topology, &versions, &BTreeMap::new()),
            "missing user"
        );
        let mut twice = versions.clone();
        twice[1].id = 1;
        assert!(refuse(&topology, &twice, &users), "duplicate id");
        let mut undated = versions.clone();
        undated[0].timestamp = "2026-02-30 00:00:00.000".into();
        assert!(refuse(&topology, &undated, &users), "invalid timestamp");
        assert!(
            assemble(&topology, active(1, 0), " ".into(), &versions, &users).is_err(),
            "blank trunk"
        );
        assert!(
            assemble(&topology, active(9, 7), "KB".into(), &versions, &users).is_err(),
            "unknown active version"
        );
    }

    #[test]
    fn model_reference_accepts_only_the_stored_shape() {
        assert_eq!(model_reference("<Model Id=\"3\" />"), Ok(3));
        for xml in [
            "<Model />",
            "<Model Id=\"x\" />",
            "<Model Id=\"3\" Other=\"1\" />",
            "<Model Id=\"3\"><X /></Model>",
            "<Other Id=\"3\" />",
            "<Model Id=\"0\" />",
        ] {
            assert!(model_reference(xml).is_err(), "{xml}");
        }
    }

    #[test]
    fn sql_datetime_ticks_match_dotnet() {
        // new DateTime(2026, 10, 21, 15, 33, 54, 123).Ticks
        assert_eq!(
            sql_datetime_ticks("2026-10-21 15:33:54.123"),
            Some(639_281_936_341_230_000)
        );
        assert_eq!(sql_datetime_ticks("0001-01-01 00:00:00.000"), Some(0));
        assert!(sql_datetime_ticks("2024-02-29 00:00:00.000").is_some());
        for bad in [
            "2023-02-29 00:00:00.000",
            "2026-10-21T15:33:54.123",
            "2026-10-21 15:33:54",
            "NULL",
            "2026-13-01 00:00:00.000",
        ] {
            assert_eq!(sql_datetime_ticks(bad), None, "{bad}");
        }
    }

    #[test]
    fn memory_store_cannot_claim_an_empty_version_tree() {
        assert!(UdmClient::new_in_memory().kb_version_list(None).is_err());
    }
}
