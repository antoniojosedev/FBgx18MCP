//! Object History, Versioning, and Differential Revision Engine (Row I4).
//!
//! Tracks immutable object revisions, timestamps, author commits, differential
//! line-by-line delta computation, and time-travel rollback capabilities.

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// A single line in a computed source diff.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "type", content = "text")]
pub enum DiffLine {
    Unchanged(String),
    Added(String),
    Removed(String),
}

/// Statistics and line details of a differential comparison between two versions.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct DiffDelta {
    pub additions: usize,
    pub deletions: usize,
    pub unchanged: usize,
    pub lines: Vec<DiffLine>,
}

/// An immutable revision snapshot of an object's parts at a point in time.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ObjectRevision {
    pub revision_id: u32,
    pub timestamp: String,
    pub author: String,
    pub comment: String,
    pub parts: HashMap<String, String>,
}

/// Revision history ledger for a single Knowledge Base object.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ObjectHistory {
    pub object_name: String,
    pub current_revision_id: u32,
    pub revisions: Vec<ObjectRevision>,
}

impl ObjectHistory {
    pub fn new(object_name: &str) -> Self {
        Self {
            object_name: object_name.to_string(),
            current_revision_id: 0,
            revisions: Vec::new(),
        }
    }

    /// Commits a new revision snapshot to the object's history.
    pub fn commit(&mut self, author: &str, comment: &str, parts: HashMap<String, String>) -> u32 {
        self.current_revision_id += 1;
        let rev_id = self.current_revision_id;

        self.revisions.push(ObjectRevision {
            revision_id: rev_id,
            timestamp: "2026-10-17T12:00:00Z".to_string(),
            author: author.to_string(),
            comment: comment.to_string(),
            parts,
        });

        rev_id
    }

    /// Retrieves an immutable revision by ID.
    pub fn get_revision(&self, revision_id: u32) -> Option<&ObjectRevision> {
        self.revisions.iter().find(|r| r.revision_id == revision_id)
    }

    /// Computes the differential diff between two revisions of a named part.
    pub fn diff_part(&self, rev_a: u32, rev_b: u32, part_name: &str) -> Result<DiffDelta, String> {
        let r_a = self
            .get_revision(rev_a)
            .ok_or_else(|| format!("Revision {rev_a} not found"))?;
        let r_b = self
            .get_revision(rev_b)
            .ok_or_else(|| format!("Revision {rev_b} not found"))?;

        let source_a = r_a.parts.get(part_name).map(|s| s.as_str()).unwrap_or("");
        let source_b = r_b.parts.get(part_name).map(|s| s.as_str()).unwrap_or("");

        Ok(compute_diff(source_a, source_b))
    }

    /// Restores the object parts snapshot from a target revision.
    pub fn rollback_to(
        &mut self,
        target_rev: u32,
        author: &str,
    ) -> Result<HashMap<String, String>, String> {
        let target_parts = self
            .get_revision(target_rev)
            .ok_or_else(|| format!("Cannot rollback: Revision {target_rev} not found"))?
            .parts
            .clone();

        self.commit(
            author,
            &format!("Rollback to revision {target_rev}"),
            target_parts.clone(),
        );

        Ok(target_parts)
    }
}

/// Computes line-by-line diff deltas between old and new source text.
pub fn compute_diff(old_source: &str, new_source: &str) -> DiffDelta {
    let old_lines: Vec<&str> = old_source.lines().collect();
    let new_lines: Vec<&str> = new_source.lines().collect();

    let mut delta = DiffDelta::default();

    // Standard LCS-based diff for lines
    let m = old_lines.len();
    let n = new_lines.len();

    let mut dp = vec![vec![0; n + 1]; m + 1];
    for i in 0..m {
        for j in 0..n {
            if old_lines[i] == new_lines[j] {
                dp[i + 1][j + 1] = dp[i][j] + 1;
            } else {
                dp[i + 1][j + 1] = dp[i + 1][j].max(dp[i][j + 1]);
            }
        }
    }

    let mut i = m;
    let mut j = n;
    let mut reversed_lines = Vec::new();

    while i > 0 || j > 0 {
        if i > 0 && j > 0 && old_lines[i - 1] == new_lines[j - 1] {
            reversed_lines.push(DiffLine::Unchanged(old_lines[i - 1].to_string()));
            delta.unchanged += 1;
            i -= 1;
            j -= 1;
        } else if j > 0 && (i == 0 || dp[i][j - 1] >= dp[i - 1][j]) {
            reversed_lines.push(DiffLine::Added(new_lines[j - 1].to_string()));
            delta.additions += 1;
            j -= 1;
        } else if i > 0 {
            reversed_lines.push(DiffLine::Removed(old_lines[i - 1].to_string()));
            delta.deletions += 1;
            i -= 1;
        }
    }

    reversed_lines.reverse();
    delta.lines = reversed_lines;
    delta
}
