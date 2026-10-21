use gx_engine::versioning::{DiffLine, ObjectHistory, compute_diff};
use std::collections::HashMap;

#[test]
fn test_history_commit_and_sequential_ids() {
    let mut history = ObjectHistory::new("CalculateTax");
    let mut parts1 = HashMap::new();
    parts1.insert("Source".to_string(), "&Tax = &Amount * 0.1".to_string());

    let rev1 = history.commit("dev1", "Initial version", parts1);
    assert_eq!(rev1, 1);
    assert_eq!(history.current_revision_id, 1);

    let mut parts2 = HashMap::new();
    parts2.insert("Source".to_string(), "&Tax = &Amount * 0.15".to_string());

    let rev2 = history.commit("dev2", "Updated tax rate", parts2);
    assert_eq!(rev2, 2);
    assert_eq!(history.current_revision_id, 2);
    assert_eq!(history.revisions.len(), 2);
}

#[test]
fn test_history_get_revision() {
    let mut history = ObjectHistory::new("CustomerProc");
    let mut parts = HashMap::new();
    parts.insert("Source".to_string(), "Print(\"v1\")".to_string());
    history.commit("author1", "v1", parts);

    let r = history.get_revision(1).expect("Revision 1 should exist");
    assert_eq!(r.author, "author1");
    assert_eq!(r.comment, "v1");
    assert_eq!(r.parts.get("Source").unwrap(), "Print(\"v1\")");

    assert!(history.get_revision(99).is_none());
}

#[test]
fn test_compute_diff_additions_and_deletions() {
    let old_text = "line1\nline2\nline3";
    let new_text = "line1\nline2_modified\nline3\nline4";

    let delta = compute_diff(old_text, new_text);
    assert_eq!(delta.unchanged, 2); // line1 and line3
    assert_eq!(delta.deletions, 1); // line2 removed
    assert_eq!(delta.additions, 2); // line2_modified and line4 added

    assert!(
        delta
            .lines
            .iter()
            .any(|l| matches!(l, DiffLine::Removed(s) if s == "line2"))
    );
    assert!(
        delta
            .lines
            .iter()
            .any(|l| matches!(l, DiffLine::Added(s) if s == "line2_modified"))
    );
    assert!(
        delta
            .lines
            .iter()
            .any(|l| matches!(l, DiffLine::Added(s) if s == "line4"))
    );
}

#[test]
fn test_history_diff_between_revisions() {
    let mut history = ObjectHistory::new("PriceCalculator");

    let mut parts1 = HashMap::new();
    parts1.insert(
        "Source".to_string(),
        "For each\n&Total = &Total + Price\nEndfor".to_string(),
    );
    history.commit("dev", "v1", parts1);

    let mut parts2 = HashMap::new();
    parts2.insert(
        "Source".to_string(),
        "For each\nWhere Active = true\n&Total = &Total + Price\nEndfor".to_string(),
    );
    history.commit("dev", "v2", parts2);

    let diff = history
        .diff_part(1, 2, "Source")
        .expect("Diff should succeed");
    assert_eq!(diff.additions, 1);
    assert_eq!(diff.deletions, 0);
    assert!(
        diff.lines
            .iter()
            .any(|l| matches!(l, DiffLine::Added(s) if s == "Where Active = true"))
    );
}

#[test]
fn test_history_rollback_restores_snapshot() {
    let mut history = ObjectHistory::new("RollbackProc");

    let mut parts1 = HashMap::new();
    parts1.insert("Source".to_string(), "GoodVersion".to_string());
    history.commit("dev", "Initial good", parts1);

    let mut parts2 = HashMap::new();
    parts2.insert("Source".to_string(), "BadBrokenVersion".to_string());
    history.commit("dev", "Buggy edit", parts2);

    let restored = history
        .rollback_to(1, "dev_rollback")
        .expect("Rollback should succeed");
    assert_eq!(restored.get("Source").unwrap(), "GoodVersion");
    assert_eq!(history.current_revision_id, 3);

    let latest_rev = history.get_revision(3).unwrap();
    assert_eq!(latest_rev.comment, "Rollback to revision 1");
    assert_eq!(latest_rev.parts.get("Source").unwrap(), "GoodVersion");
}

#[test]
fn test_history_fail_closed_on_diff_invalid_revision() {
    let history = ObjectHistory::new("Empty");
    let err = history.diff_part(1, 2, "Source").unwrap_err();
    assert!(err.contains("Revision 1 not found"));
}

#[test]
fn test_history_fail_closed_on_rollback_invalid_revision() {
    let mut history = ObjectHistory::new("Empty");
    let err = history.rollback_to(5, "author").unwrap_err();
    assert!(err.contains("Revision 5 not found"));
}

#[test]
fn test_compute_diff_empty_inputs() {
    let delta = compute_diff("", "");
    assert_eq!(delta.additions, 0);
    assert_eq!(delta.deletions, 0);
    assert_eq!(delta.unchanged, 0);
}

#[test]
fn test_compute_diff_identical_inputs() {
    let text = "For each\n&Count += 1\nEndfor";
    let delta = compute_diff(text, text);
    assert_eq!(delta.additions, 0);
    assert_eq!(delta.deletions, 0);
    assert_eq!(delta.unchanged, 3);
}

#[test]
fn test_history_json_roundtrip() {
    let mut history = ObjectHistory::new("SerializationTest");
    let mut parts = HashMap::new();
    parts.insert("Rules".to_string(), "Parm(in:&Id);".to_string());
    history.commit("dev", "parm rule", parts);

    let json = serde_json::to_string(&history).expect("Serialization failed");
    let roundtrip: ObjectHistory = serde_json::from_str(&json).expect("Deserialization failed");
    assert_eq!(history, roundtrip);
}
