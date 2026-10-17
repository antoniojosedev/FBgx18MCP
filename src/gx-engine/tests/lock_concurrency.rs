use gx_engine::locks::{LockManager, LockMode, LockTarget};

#[test]
fn test_multiple_agents_can_acquire_shared_locks() {
    let mut mgr = LockManager::default();
    let target = LockTarget::Object("Customer".to_string());

    let lease1 = mgr
        .acquire("agent1", target.clone(), LockMode::Shared, 60, 1000)
        .expect("agent1 shared");
    let lease2 = mgr
        .acquire("agent2", target.clone(), LockMode::Shared, 60, 1000)
        .expect("agent2 shared");

    assert_eq!(lease1.mode, LockMode::Shared);
    assert_eq!(lease2.mode, LockMode::Shared);
    assert_eq!(mgr.active_leases.len(), 2);
}

#[test]
fn test_exclusive_lock_conflicts_with_shared_lock() {
    let mut mgr = LockManager::default();
    let target = LockTarget::Object("Invoice".to_string());

    mgr.acquire("reader", target.clone(), LockMode::Shared, 60, 1000)
        .unwrap();
    let err = mgr
        .acquire("writer", target.clone(), LockMode::Exclusive, 60, 1000)
        .unwrap_err();

    assert!(err.contains("Lock conflict on target"));
    assert!(err.contains("currently held by agent 'reader'"));
}

#[test]
fn test_exclusive_lock_conflicts_with_exclusive_lock() {
    let mut mgr = LockManager::default();
    let target = LockTarget::Table("Orders".to_string());

    mgr.acquire("writer1", target.clone(), LockMode::Exclusive, 60, 1000)
        .unwrap();
    let err = mgr
        .acquire("writer2", target.clone(), LockMode::Exclusive, 60, 1000)
        .unwrap_err();

    assert!(err.contains("Lock conflict on target"));
}

#[test]
fn test_kb_exclusive_lock_blocks_table_lock() {
    let mut mgr = LockManager::default();
    mgr.acquire(
        "reorg_agent",
        LockTarget::KnowledgeBase,
        LockMode::Exclusive,
        120,
        1000,
    )
    .unwrap();

    let err = mgr
        .acquire(
            "worker_agent",
            LockTarget::Table("Products".to_string()),
            LockMode::Shared,
            60,
            1000,
        )
        .unwrap_err();
    assert!(err.contains("Lock conflict"));
}

#[test]
fn test_release_lock_allows_subsequent_exclusive() {
    let mut mgr = LockManager::default();
    let target = LockTarget::Object("MyProc".to_string());

    let lease = mgr
        .acquire("agent1", target.clone(), LockMode::Exclusive, 60, 1000)
        .unwrap();
    mgr.release(&lease.lease_id, "agent1")
        .expect("Release should succeed");

    let lease2 = mgr
        .acquire("agent2", target.clone(), LockMode::Exclusive, 60, 1000)
        .expect("Acquisition after release");
    assert_eq!(lease2.agent_id, "agent2");
}

#[test]
fn test_renew_lease_extends_expiration() {
    let mut mgr = LockManager::default();
    let target = LockTarget::Object("RenewTarget".to_string());

    let lease = mgr
        .acquire("agent1", target, LockMode::Exclusive, 30, 1000)
        .unwrap();
    assert_eq!(lease.expires_at, 1030);

    let renewed = mgr
        .renew(&lease.lease_id, "agent1", 50, 1020)
        .expect("Renewal should succeed");
    assert_eq!(renewed.expires_at, 1070);
}

#[test]
fn test_expired_leases_are_automatically_purged() {
    let mut mgr = LockManager::default();
    let target = LockTarget::Object("ExpiredTarget".to_string());

    mgr.acquire("agent1", target.clone(), LockMode::Exclusive, 10, 1000)
        .unwrap();
    // At timestamp 1015, the lock expired at 1010
    let lease2 = mgr
        .acquire("agent2", target, LockMode::Exclusive, 10, 1015)
        .expect("Should succeed after expiration");
    assert_eq!(lease2.agent_id, "agent2");
    assert_eq!(mgr.active_leases.len(), 1);
}

#[test]
fn test_fail_closed_on_releasing_nonexistent_lease() {
    let mut mgr = LockManager::default();
    let err = mgr.release("nonexistent", "agent1").unwrap_err();
    assert!(err.contains("Lease 'nonexistent' not found"));
}

#[test]
fn test_case_insensitive_target_matching() {
    let mut mgr = LockManager::default();
    mgr.acquire(
        "agent1",
        LockTarget::Object("customer".to_string()),
        LockMode::Exclusive,
        60,
        1000,
    )
    .unwrap();

    let err = mgr
        .acquire(
            "agent2",
            LockTarget::Object("CUSTOMER".to_string()),
            LockMode::Shared,
            60,
            1000,
        )
        .unwrap_err();
    assert!(err.contains("Lock conflict"));
}

#[test]
fn test_lock_manager_json_roundtrip() {
    let mut mgr = LockManager::default();
    mgr.acquire(
        "agent1",
        LockTarget::Table("Items".to_string()),
        LockMode::Shared,
        60,
        1000,
    )
    .unwrap();

    let json = serde_json::to_string(&mgr).expect("Serialization failed");
    let roundtrip: LockManager = serde_json::from_str(&json).expect("Deserialization failed");
    assert_eq!(mgr.active_leases, roundtrip.active_leases);
}
