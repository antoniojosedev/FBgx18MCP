use super::{
    ModelEntityVersionRecord, ObjectReadMetadata, SYSTEM_ROOT_MODULE_GUID, UdmClient,
    object_read_metadata_from_rows, object_read_metadata_sql, parse_datetime_ticks,
    parse_stored_datetime_ticks, system_root_module_from_rows, system_root_module_sql,
};

const GUID: &str = "83a55789-716f-4c13-a03c-215b167050e8";
const PARENT_GUID: &str = "81eb782b-33a0-4f12-84bc-d6694a9d78f4";
const SAVED: &str = "2022-04-05T06:07:08.123";
const SAVED_TICKS: i64 = 637_847_356_281_230_000;
const MODEL: &str = "2026-07-31T14:57:38";
const MODEL_TICKS: i64 = 639_211_066_580_000_000;

/// Columns: GUID, parent type, parent id, parent GUID, parent-row marker, parent name,
/// ObjectSaved-row marker, ObjectSaved timestamp, model timestamp.
type Fields = [&'static str; 9];

fn interpret(rows: &[Fields]) -> Result<Option<ObjectReadMetadata>, String> {
    let rows: Vec<Vec<&str>> = rows.iter().map(|row| row.to_vec()).collect();
    object_read_metadata_from_rows(5, &rows)
}

fn root_row(saved_marker: &'static str, saved: &'static str, model: &'static str) -> Fields {
    [GUID, "0", "0", "", "ABSENT", "", saved_marker, saved, model]
}

fn parented_row(parent_marker: &'static str, name: &'static str) -> Fields {
    [
        GUID,
        "12",
        "17",
        PARENT_GUID,
        parent_marker,
        name,
        "PRESENT",
        SAVED,
        MODEL,
    ]
}

fn parent_version(model_id: i32, entity_version_id: i32, name: &str) -> ModelEntityVersionRecord {
    ModelEntityVersionRecord {
        model_id,
        entity_type_id: 12,
        entity_id: 17,
        entity_version_id,
        name: name.to_string(),
    }
}

fn in_memory_with_parent(rows: Vec<ModelEntityVersionRecord>) -> UdmClient {
    let client = UdmClient::new_in_memory();
    {
        let mut store = client.storage_for_test().write().expect("store");
        store.model_parents.insert((1, 3, 5), (12, 17));
        store.model_entity_versions.extend(rows);
    }
    client
}

#[test]
fn an_absent_object_saved_row_falls_back_to_the_model_timestamp() {
    let metadata = interpret(&[root_row("ABSENT", "", MODEL)])
        .expect("absent row is accepted")
        .expect("one row");
    assert_eq!(metadata.object_saved_ticks, None);
    assert_eq!(metadata.model_entity_version_ticks, Some(MODEL_TICKS));
}

#[test]
fn an_object_saved_row_with_a_null_timestamp_is_refused_not_treated_as_absent() {
    let error = interpret(&[root_row("PRESENT", "", MODEL)]).unwrap_err();
    assert!(error.contains("NULL OutputTimestamp"), "{error}");
}

#[test]
fn a_present_object_saved_row_keeps_its_own_timestamp_beside_the_model_one() {
    let metadata = interpret(&[root_row("PRESENT", SAVED, MODEL)])
        .expect("present row")
        .expect("one row");
    assert_eq!(metadata.object_saved_ticks, Some(SAVED_TICKS));
    assert_eq!(metadata.model_entity_version_ticks, Some(MODEL_TICKS));
}

#[test]
fn an_absent_object_saved_row_with_no_model_timestamp_reports_neither() {
    let metadata = interpret(&[root_row("ABSENT", "", "")])
        .expect("accepted")
        .expect("one row");
    assert_eq!(metadata.object_saved_ticks, None);
    assert_eq!(metadata.model_entity_version_ticks, None);
}

#[test]
fn inconsistent_markers_and_malformed_timestamps_are_refused() {
    let absent_with_value = interpret(&[root_row("ABSENT", SAVED, MODEL)]).unwrap_err();
    assert!(
        absent_with_value.contains("absent but reports"),
        "{absent_with_value}"
    );
    let unknown_marker = interpret(&[root_row("YES", SAVED, MODEL)]).unwrap_err();
    assert!(
        unknown_marker.contains("presence marker"),
        "{unknown_marker}"
    );
    let bad_saved = interpret(&[root_row("PRESENT", "2026-13-01T00:00:00", MODEL)]).unwrap_err();
    assert!(bad_saved.contains("OutputTimestamp"), "{bad_saved}");
    let bad_model = interpret(&[root_row("ABSENT", "", "not-a-date")]).unwrap_err();
    assert!(
        bad_model.contains("ModelEntityVersionTimestamp"),
        "{bad_model}"
    );
}

#[test]
fn no_row_is_none_and_a_duplicated_candidate_row_is_refused() {
    assert_eq!(interpret(&[]), Ok(None));
    // A second ObjectSaved row or a second parent row of the model multiplies the join.
    let duplicated = interpret(&[
        parented_row("PRESENT", "ParentModule"),
        parented_row("PRESENT", "ParentModule"),
    ])
    .unwrap_err();
    assert!(duplicated.contains("matched 2 rows"), "{duplicated}");
}

#[test]
fn the_parent_name_is_taken_only_from_a_present_parent_row() {
    let named = interpret(&[parented_row("PRESENT", "ParentModule")])
        .expect("accepted")
        .expect("one row");
    assert_eq!(named.parent, Some((12, 17)));
    assert_eq!(named.parent_guid.as_deref(), Some(PARENT_GUID));
    assert_eq!(named.parent_name.as_deref(), Some("ParentModule"));
    // No parent row in the active model: the name stays absent (the caller then needs
    // the GUID) instead of borrowing a name from elsewhere.
    let missing = interpret(&[parented_row("ABSENT", "")])
        .expect("accepted")
        .expect("one row");
    assert_eq!(missing.parent_name, None);
    assert_eq!(missing.parent_guid.as_deref(), Some(PARENT_GUID));
}

#[test]
fn the_parent_half_pair_rules_are_preserved() {
    let root = interpret(&[root_row("ABSENT", "", MODEL)])
        .expect("accepted")
        .expect("one row");
    assert_eq!(root.parent, None);
    let mut half = root_row("ABSENT", "", MODEL);
    half[1] = "12";
    let error = interpret(&[half]).unwrap_err();
    assert!(error.contains("zero half"), "{error}");
    let mut malformed = root_row("ABSENT", "", MODEL);
    malformed[2] = "x";
    assert!(interpret(&[malformed]).is_err());
}

#[test]
fn a_row_with_the_wrong_field_count_is_refused() {
    let short: Vec<Vec<&str>> = vec![vec![GUID, "0", "0"]];
    assert!(object_read_metadata_from_rows(5, &short).is_err());
}

#[test]
fn the_metadata_query_is_bound_to_the_exact_object_version_and_active_model() {
    let sql = object_read_metadata_sql(1, 3, 5, 9);
    assert!(
        sql.contains("WHERE m.ModelId = 1 AND m.EntityTypeId = 3"),
        "{sql}"
    );
    assert!(
        sql.contains("AND m.EntityId = 5 AND m.EntityVersionId = 9"),
        "{sql}"
    );
    // The ObjectSaved output is the exact version's output 301, version 0.
    assert!(
        sql.contains("o.OutputEntityVersionId = m.EntityVersionId"),
        "{sql}"
    );
    assert!(
        sql.contains("o.OutputTypeId = 301 AND o.OutputTypeVersion = 0"),
        "{sql}"
    );
    assert!(sql.contains("o.ModelId = m.ModelId"), "{sql}");
    // The parent row is the active model's, joined without a global-latest pick.
    assert!(
        sql.contains("LEFT JOIN ModelEntityVersion pm ON pm.ModelId = m.ModelId"),
        "{sql}"
    );
    assert!(
        sql.contains("pm.EntityTypeId = m.ModelParentEntityTypeId"),
        "{sql}"
    );
    assert!(sql.contains("pm.EntityId = m.ModelParentEntityId"), "{sql}");
    for forbidden in ["TOP", "ORDER BY", "OUTER APPLY"] {
        assert!(!sql.contains(forbidden), "{forbidden} in {sql}");
    }
}

#[test]
fn the_metadata_query_marks_row_presence_and_never_coerces_a_guid_from_an_empty_string() {
    let sql = object_read_metadata_sql(1, 3, 5, 9);
    assert!(
        sql.contains("CASE WHEN o.OutputTypeId IS NULL THEN 'ABSENT' ELSE 'PRESENT' END"),
        "{sql}"
    );
    assert!(
        sql.contains("CASE WHEN pm.EntityId IS NULL THEN 'ABSENT' ELSE 'PRESENT' END"),
        "{sql}"
    );
    assert!(
        sql.contains("ISNULL(CONVERT(VARCHAR(36), e.EntityGuid), '')"),
        "{sql}"
    );
    assert!(
        sql.contains("ISNULL(CONVERT(VARCHAR(36), p.EntityGuid), '')"),
        "{sql}"
    );
    assert!(!sql.contains("ISNULL(e.EntityGuid"), "{sql}");
    assert!(!sql.contains("ISNULL(p.EntityGuid"), "{sql}");
    // The timestamp stays a presence-independent column, read as ISO-8601 with no time-zone change.
    assert!(
        sql.contains("ISNULL(CONVERT(VARCHAR(33), o.OutputTimestamp, 126), '')"),
        "{sql}"
    );
    assert!(
        sql.contains("ISNULL(CONVERT(VARCHAR(33), m.ModelEntityVersionTimestamp, 126), '')"),
        "{sql}"
    );
}

#[test]
fn the_in_memory_parent_name_ignores_other_models_and_refuses_a_duplicate_in_the_active_one() {
    // A row of another model is not a candidate: no name is borrowed from it.
    let other_model = in_memory_with_parent(vec![parent_version(2, 4, "OtherModelParent")]);
    let metadata = other_model
        .get_object_read_metadata(1, 3, 5, 1)
        .expect("read")
        .expect("parent link makes metadata present");
    assert_eq!(metadata.parent, Some((12, 17)));
    assert_eq!(metadata.parent_name, None);

    // The active model's single row is the name, with a foreign model's row beside it.
    let single = in_memory_with_parent(vec![
        parent_version(1, 4, "ActiveParent"),
        parent_version(2, 4, "OtherModelParent"),
    ]);
    let metadata = single
        .get_object_read_metadata(1, 3, 5, 1)
        .expect("read")
        .expect("metadata");
    assert_eq!(metadata.parent_name.as_deref(), Some("ActiveParent"));

    // Two rows of the parent in the active model are refused, equal names included.
    for names in [("Same", "Same"), ("First", "Second")] {
        let duplicated = in_memory_with_parent(vec![
            parent_version(1, 4, names.0),
            parent_version(1, 5, names.1),
        ]);
        let error = duplicated.get_object_read_metadata(1, 3, 5, 1).unwrap_err();
        assert!(error.contains("expected one"), "{error}");
    }
}

fn root_candidates(rows: &[[&str; 3]]) -> Result<Option<(i32, i32)>, String> {
    let rows: Vec<Vec<&str>> = rows.iter().map(|row| row.to_vec()).collect();
    system_root_module_from_rows(1, 17, &rows)
}

#[test]
fn the_root_module_query_never_coerces_a_guid_from_an_empty_string() {
    let sql = system_root_module_sql(1, 17);
    assert!(
        sql.contains("ISNULL(CONVERT(VARCHAR(36), e.EntityGuid), '')"),
        "{sql}"
    );
    assert!(!sql.contains("ISNULL(e.EntityGuid"), "{sql}");
}

#[test]
fn the_root_module_query_is_bound_to_the_model_module_type_and_parent_less_entity_one() {
    let sql = system_root_module_sql(3, 17);
    assert!(
        sql.contains("WHERE m.ModelId = 3 AND m.EntityTypeId = 17 AND m.EntityId = 1"),
        "{sql}"
    );
    assert!(
        sql.contains("m.ModelParentEntityTypeId = 0 AND m.ModelParentEntityId = 0"),
        "{sql}"
    );
    assert!(sql.contains("LEFT JOIN Entity e"), "{sql}");
    for forbidden in ["TOP", "ORDER BY"] {
        assert!(!sql.contains(forbidden), "{forbidden} in {sql}");
    }
}

#[test]
fn the_root_module_is_the_single_candidate_row_with_the_root_guid() {
    assert_eq!(
        root_candidates(&[["17", "1", SYSTEM_ROOT_MODULE_GUID]]),
        Ok(Some((17, 1)))
    );
    let upper = SYSTEM_ROOT_MODULE_GUID.to_ascii_uppercase();
    assert_eq!(
        root_candidates(&[["17", "1", upper.as_str()]]),
        Ok(Some((17, 1)))
    );
}

#[test]
fn a_null_or_other_guid_or_no_candidate_is_no_root() {
    assert_eq!(root_candidates(&[]), Ok(None));
    // A NULL GUID or a missing Entity row arrives as an empty field.
    assert_eq!(root_candidates(&[["17", "1", ""]]), Ok(None));
    assert_eq!(
        root_candidates(&[["17", "1", "00000000-0000-0000-0000-000000000000"]]),
        Ok(None)
    );
    assert_eq!(
        root_candidates(&[["17", "1", "00000000-0000-0000-0000-000000000001"]]),
        Ok(None)
    );
}

#[test]
fn duplicate_root_candidates_are_refused_instead_of_picking_one() {
    for rows in [
        [
            ["17", "1", SYSTEM_ROOT_MODULE_GUID],
            ["17", "1", SYSTEM_ROOT_MODULE_GUID],
        ],
        [["17", "1", SYSTEM_ROOT_MODULE_GUID], ["17", "1", ""]],
        [["17", "1", ""], ["17", "1", SYSTEM_ROOT_MODULE_GUID]],
    ] {
        let error = root_candidates(&rows).unwrap_err();
        assert!(error.contains("expected at most one"), "{error}");
    }
}

#[test]
fn a_candidate_that_is_not_the_requested_module_entity_one_is_refused() {
    for row in [
        ["31", "1", SYSTEM_ROOT_MODULE_GUID],
        ["17", "2", SYSTEM_ROOT_MODULE_GUID],
        ["x", "1", SYSTEM_ROOT_MODULE_GUID],
        ["17", "", SYSTEM_ROOT_MODULE_GUID],
    ] {
        assert!(root_candidates(&[row]).is_err(), "{row:?}");
    }
    let short: Vec<Vec<&str>> = vec![vec!["17", "1"]];
    assert!(system_root_module_from_rows(1, 17, &short).is_err());
}

#[test]
fn parses_stored_datetimes_into_raw_dotnet_ticks() {
    // Vectors below were read from .NET `DateTime.Ticks` independently of this parser.
    assert_eq!(parse_datetime_ticks("0001-01-01T00:00:00"), Ok(0));
    assert_eq!(
        parse_datetime_ticks("1900-01-01T00:00:00"),
        Ok(599_266_080_000_000_000)
    );
    assert_eq!(
        parse_datetime_ticks("2026-07-31T14:57:38"),
        Ok(639_211_066_580_000_000)
    );
    assert_eq!(
        parse_datetime_ticks("2022-04-05T06:07:08.123"),
        Ok(637_847_356_281_230_000)
    );
    assert_eq!(
        parse_datetime_ticks("2022-04-05T06:07:08.1234567"),
        Ok(637_847_356_281_234_567)
    );
    assert_eq!(
        parse_datetime_ticks("2024-02-29T23:59:59.9"),
        Ok(parse_datetime_ticks("2024-02-29T23:59:59.9000000").unwrap())
    );
}

#[test]
fn a_missing_timestamp_is_absent_and_malformed_values_are_refused() {
    assert_eq!(parse_stored_datetime_ticks("", "OutputTimestamp"), Ok(None));
    for bad in [
        "2026-07-31",
        "2026-07-31 14:57:38",
        "2026-13-01T00:00:00",
        "2026-02-30T00:00:00",
        "2026-01-01T24:00:00",
        "2026-01-01T00:00:60",
        "2026-01-01T00:00:00.",
        "2026-01-01T00:00:00.12345678",
        "2026-01-01T00:00:00.x",
        "2026-01-01T00:00:00.0000000T",
    ] {
        assert!(
            parse_datetime_ticks(bad).is_err(),
            "{bad} was accepted as a datetime"
        );
    }
    let error = parse_stored_datetime_ticks("not-a-date", "OutputTimestamp").unwrap_err();
    assert!(error.contains("OutputTimestamp"), "{error}");
}
