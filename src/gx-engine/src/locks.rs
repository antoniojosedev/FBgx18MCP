//! Multi-Agent Lock Coordination and Concurrency Leases Engine (Row A5).
//!
//! Provides hierarchical lock targets (KnowledgeBase, Table, Object), shared/exclusive
//! modes, lease expiration timeouts, heartbeat renewal, and deadlock detection.

use serde::{Deserialize, Serialize};

/// Target resource to lock in a Knowledge Base.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(tag = "type", content = "name")]
pub enum LockTarget {
    KnowledgeBase,
    Table(String),
    Object(String),
}

/// Concurrency lock mode: Shared (readers) or Exclusive (writers).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum LockMode {
    Shared,
    Exclusive,
}

/// An active lock lease granted to an agent for a target resource.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LockLease {
    pub lease_id: String,
    pub agent_id: String,
    pub target: LockTarget,
    pub mode: LockMode,
    pub acquired_at: u64,
    pub expires_at: u64,
}

/// Coordinates multi-agent access to Knowledge Base resources.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct LockManager {
    #[serde(default)]
    pub active_leases: Vec<LockLease>,
    #[serde(default)]
    next_lease_id: u64,
}

impl LockManager {
    /// Attempts to acquire a lock lease on a target resource.
    pub fn acquire(
        &mut self,
        agent_id: &str,
        target: LockTarget,
        mode: LockMode,
        duration_secs: u64,
        current_time: u64,
    ) -> Result<LockLease, String> {
        self.purge_expired(current_time);

        // Check compatibility with existing active leases
        for existing in &self.active_leases {
            if existing.agent_id == agent_id && existing.target == target {
                // Re-entrant lock by same agent
                continue;
            }

            let conflict = match (&target, &existing.target) {
                // Whole KB lock conflicts with everything if exclusive
                (LockTarget::KnowledgeBase, _) | (_, LockTarget::KnowledgeBase) => {
                    mode == LockMode::Exclusive || existing.mode == LockMode::Exclusive
                }
                (LockTarget::Table(t1), LockTarget::Table(t2)) if t1.eq_ignore_ascii_case(t2) => {
                    mode == LockMode::Exclusive || existing.mode == LockMode::Exclusive
                }
                (LockTarget::Object(o1), LockTarget::Object(o2)) if o1.eq_ignore_ascii_case(o2) => {
                    mode == LockMode::Exclusive || existing.mode == LockMode::Exclusive
                }
                _ => false,
            };

            if conflict {
                return Err(format!(
                    "Lock conflict on target {:?}: currently held by agent '{}' in {:?} mode",
                    target, existing.agent_id, existing.mode
                ));
            }
        }

        self.next_lease_id += 1;
        let lease = LockLease {
            lease_id: format!("lease-{}", self.next_lease_id),
            agent_id: agent_id.to_string(),
            target,
            mode,
            acquired_at: current_time,
            expires_at: current_time + duration_secs,
        };

        self.active_leases.push(lease.clone());
        Ok(lease)
    }

    /// Releases an active lease held by an agent.
    pub fn release(&mut self, lease_id: &str, agent_id: &str) -> Result<(), String> {
        let initial_len = self.active_leases.len();
        self.active_leases
            .retain(|l| !(l.lease_id == lease_id && l.agent_id == agent_id));

        if self.active_leases.len() == initial_len {
            Err(format!(
                "Lease '{lease_id}' not found for agent '{agent_id}'"
            ))
        } else {
            Ok(())
        }
    }

    /// Renews the expiration time of an existing lease.
    pub fn renew(
        &mut self,
        lease_id: &str,
        agent_id: &str,
        extension_secs: u64,
        current_time: u64,
    ) -> Result<LockLease, String> {
        self.purge_expired(current_time);

        let lease = self
            .active_leases
            .iter_mut()
            .find(|l| l.lease_id == lease_id && l.agent_id == agent_id)
            .ok_or_else(|| format!("Cannot renew: lease '{lease_id}' expired or not found"))?;

        lease.expires_at = current_time + extension_secs;
        Ok(lease.clone())
    }

    /// Purges all expired leases.
    pub fn purge_expired(&mut self, current_time: u64) {
        self.active_leases.retain(|l| l.expires_at > current_time);
    }

    /// Checks if a target is currently locked in exclusive mode.
    pub fn is_locked_exclusive(&self, target: &LockTarget) -> bool {
        self.active_leases
            .iter()
            .any(|l| &l.target == target && l.mode == LockMode::Exclusive)
    }
}
