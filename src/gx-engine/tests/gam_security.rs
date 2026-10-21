use gx_engine::gam::{AccessType, GamPermission, GamRole, GamSecurityManager, GamUser};

#[test]
fn test_gam_basic_authorization_allow() {
    let dsl = r#"
        role Administrator {
            permission Customer_View allow
            permission Customer_Edit allow
        }

        user admin_user {
            role_assignment Administrator
        }
    "#;

    let mgr = GamSecurityManager::parse(dsl).expect("GAM DSL should parse");
    assert!(mgr.is_authorized("admin_user", "Customer_View"));
    assert!(mgr.is_authorized("admin_user", "Customer_Edit"));
    assert!(!mgr.is_authorized("admin_user", "Customer_Delete"));
}

#[test]
fn test_gam_explicit_deny_overrides_allow() {
    let dsl = r#"
        role StandardUser {
            permission Invoice_View allow
            permission Invoice_Delete deny
        }

        user john {
            role_assignment StandardUser
        }
    "#;

    let mgr = GamSecurityManager::parse(dsl).unwrap();
    assert!(mgr.is_authorized("john", "Invoice_View"));
    assert!(!mgr.is_authorized("john", "Invoice_Delete"));
}

#[test]
fn test_gam_hierarchical_role_inheritance() {
    let dsl = r#"
        role BaseRole {
            permission Report_View allow
        }

        role ManagerRole {
            parent_role = "BaseRole"
            permission Report_Approve allow
        }

        user alice {
            role_assignment ManagerRole
        }
    "#;

    let mgr = GamSecurityManager::parse(dsl).unwrap();
    // Inherited from BaseRole
    assert!(mgr.is_authorized("alice", "Report_View"));
    // Direct from ManagerRole
    assert!(mgr.is_authorized("alice", "Report_Approve"));
    assert!(!mgr.is_authorized("alice", "Report_Delete"));
}

#[test]
fn test_gam_direct_user_permission_override() {
    let mut mgr = GamSecurityManager::default();
    let role = GamRole {
        id: "Editor".to_string(),
        name: "Editor".to_string(),
        parent_role: None,
        permissions: vec![GamPermission {
            name: "Article_Publish".to_string(),
            access: AccessType::Allow,
        }],
    };
    mgr.roles.insert("Editor".to_string(), role);

    let user = GamUser {
        id: "bob".to_string(),
        username: "bob".to_string(),
        email: "bob@test.com".to_string(),
        is_active: true,
        roles: vec!["Editor".to_string()],
        direct_permissions: vec![GamPermission {
            name: "Article_Publish".to_string(),
            access: AccessType::Deny, // Direct deny override
        }],
    };
    mgr.users.insert("bob".to_string(), user);

    assert!(!mgr.is_authorized("bob", "Article_Publish"));
}

#[test]
fn test_gam_inactive_user_is_not_authorized() {
    let mut mgr = GamSecurityManager::default();
    let role = GamRole {
        id: "ActiveRole".to_string(),
        name: "ActiveRole".to_string(),
        parent_role: None,
        permissions: vec![GamPermission {
            name: "Login".to_string(),
            access: AccessType::Allow,
        }],
    };
    mgr.roles.insert("ActiveRole".to_string(), role);

    let user = GamUser {
        id: "inactive_user".to_string(),
        username: "inactive".to_string(),
        email: "inactive@test.com".to_string(),
        is_active: false, // Disabled
        roles: vec!["ActiveRole".to_string()],
        direct_permissions: vec![],
    };
    mgr.users.insert("inactive_user".to_string(), user);

    assert!(!mgr.is_authorized("inactive_user", "Login"));
}

#[test]
fn test_gam_case_insensitive_permission_lookup() {
    let dsl = r#"
        role Viewer {
            permission Sales_Summary allow
        }
        user viewer_user {
            role_assignment Viewer
        }
    "#;
    let mgr = GamSecurityManager::parse(dsl).unwrap();
    assert!(mgr.is_authorized("viewer_user", "sales_summary"));
    assert!(mgr.is_authorized("viewer_user", "SALES_SUMMARY"));
}

#[test]
fn test_gam_json_roundtrip() {
    let json = r#"{
        "roles": {
            "Support": {
                "id": "Support",
                "name": "Customer Support",
                "permissions": [
                    { "name": "Ticket_View", "access": "allow" },
                    { "name": "Ticket_Close", "access": "allow" }
                ]
            }
        },
        "users": {
            "sarah": {
                "id": "sarah",
                "username": "sarah",
                "email": "sarah@support.com",
                "is_active": true,
                "roles": ["Support"],
                "direct_permissions": []
            }
        }
    }"#;

    let mgr = GamSecurityManager::parse(json).expect("JSON should parse");
    assert!(mgr.is_authorized("sarah", "Ticket_View"));

    let serialized = serde_json::to_string(&mgr).expect("Should serialize");
    let roundtrip: GamSecurityManager =
        serde_json::from_str(&serialized).expect("Should deserialize");
    assert_eq!(mgr, roundtrip);
}

#[test]
fn test_gam_fail_closed_on_unknown_parent_role() {
    let dsl = r#"
        role Child {
            parent_role = "NonExistentParent"
        }
    "#;
    let err = GamSecurityManager::parse(dsl).unwrap_err();
    assert!(
        err.contains("references unknown parent role"),
        "Actual: {err}"
    );
}

#[test]
fn test_gam_fail_closed_on_role_hierarchy_cycle() {
    let mut mgr = GamSecurityManager::default();
    mgr.roles.insert(
        "A".to_string(),
        GamRole {
            id: "A".to_string(),
            name: "A".to_string(),
            parent_role: Some("B".to_string()),
            permissions: vec![],
        },
    );
    mgr.roles.insert(
        "B".to_string(),
        GamRole {
            id: "B".to_string(),
            name: "B".to_string(),
            parent_role: Some("A".to_string()),
            permissions: vec![],
        },
    );

    let err = mgr.validate().unwrap_err();
    assert!(
        err.contains("Cycle detected in GAM role hierarchy"),
        "Actual: {err}"
    );
}

#[test]
fn test_gam_fail_closed_on_empty_source() {
    let err = GamSecurityManager::parse("   ").unwrap_err();
    assert!(err.contains("source cannot be empty"), "Actual: {err}");
}
