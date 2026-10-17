//! GAM (GeneXus Access Manager) Security and Permissions Engine (Row J2).
//!
//! Models security levels (None, Authentication, Authorization), users, roles,
//! permissions, hierarchical role inheritance, and fail-closed authorization checks.

use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

use crate::dataview::strip_comments;

/// Object-level security enforcement setting in GeneXus.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SecurityLevel {
    None,
    Authentication,
    Authorization,
}

/// Access disposition of a GAM permission.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AccessType {
    Allow,
    Deny,
    Inherit,
}

/// A specific named permission in a GAM application.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct GamPermission {
    pub name: String,
    pub access: AccessType,
}

/// A security role containing permissions and optional parent role inheritance.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct GamRole {
    pub id: String,
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parent_role: Option<String>,
    #[serde(default)]
    pub permissions: Vec<GamPermission>,
}

/// A user account with role assignments and direct permission overrides.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct GamUser {
    pub id: String,
    pub username: String,
    pub email: String,
    #[serde(default)]
    pub is_active: bool,
    #[serde(default)]
    pub roles: Vec<String>,
    #[serde(default)]
    pub direct_permissions: Vec<GamPermission>,
}

/// Authorization and security manager maintaining users, roles, and permission catalogs.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct GamSecurityManager {
    #[serde(default)]
    pub roles: HashMap<String, GamRole>,
    #[serde(default)]
    pub users: HashMap<String, GamUser>,
}

impl GamSecurityManager {
    /// Parses a GAM security catalog from JSON or DSL.
    pub fn parse(source: &str) -> Result<Self, String> {
        let clean = strip_comments(source);
        let trimmed = clean.trim();

        if trimmed.is_empty() {
            return Err("GAM security source cannot be empty".to_string());
        }

        if trimmed.starts_with('{') {
            let mgr: GamSecurityManager = serde_json::from_str(trimmed)
                .map_err(|e| format!("Failed to parse GAM JSON: {e}"))?;
            mgr.validate()?;
            return Ok(mgr);
        }

        // Support DSL format
        let mut mgr = GamSecurityManager::default();
        let mut current_role: Option<GamRole> = None;
        let mut current_user: Option<GamUser> = None;

        for line in clean.lines() {
            let l = line.trim();
            if l.is_empty() {
                continue;
            }

            if l.starts_with("role ") {
                if let Some(r) = current_role.take() {
                    mgr.roles.insert(r.id.clone(), r);
                }
                let id = l.strip_prefix("role ").unwrap().trim_matches('{').trim();
                current_role = Some(GamRole {
                    id: id.to_string(),
                    name: id.to_string(),
                    parent_role: None,
                    permissions: Vec::new(),
                });
            } else if l.starts_with("user ") {
                if let Some(u) = current_user.take() {
                    mgr.users.insert(u.id.clone(), u);
                }
                let id = l.strip_prefix("user ").unwrap().trim_matches('{').trim();
                current_user = Some(GamUser {
                    id: id.to_string(),
                    username: id.to_string(),
                    email: format!("{id}@example.com"),
                    is_active: true,
                    roles: Vec::new(),
                    direct_permissions: Vec::new(),
                });
            } else if l.starts_with("parent_role") && l.contains('=') {
                if let (Some(r), Some((_, p))) = (current_role.as_mut(), l.split_once('=')) {
                    r.parent_role = Some(p.trim().trim_matches(';').trim_matches('"').to_string());
                }
            } else if l.starts_with("permission ") {
                let parts: Vec<&str> = l.split_whitespace().collect();
                if parts.len() >= 3 {
                    let perm_name = parts[1].trim_matches(';').trim_matches('"');
                    let access = match parts[2].trim_matches(';').to_ascii_lowercase().as_str() {
                        "allow" => AccessType::Allow,
                        "deny" => AccessType::Deny,
                        _ => AccessType::Inherit,
                    };
                    let perm = GamPermission {
                        name: perm_name.to_string(),
                        access,
                    };
                    if let Some(ref mut r) = current_role {
                        r.permissions.push(perm);
                    } else if let Some(ref mut u) = current_user {
                        u.direct_permissions.push(perm);
                    }
                }
            } else if l.starts_with("role_assignment ") {
                if let Some(ref mut u) = current_user {
                    let role_id = l
                        .strip_prefix("role_assignment ")
                        .unwrap()
                        .trim_matches(';')
                        .trim();
                    u.roles.push(role_id.to_string());
                }
            } else if l == "}" {
                if let Some(r) = current_role.take() {
                    mgr.roles.insert(r.id.clone(), r);
                }
                if let Some(u) = current_user.take() {
                    mgr.users.insert(u.id.clone(), u);
                }
            }
        }

        if let Some(r) = current_role.take() {
            mgr.roles.insert(r.id.clone(), r);
        }
        if let Some(u) = current_user.take() {
            mgr.users.insert(u.id.clone(), u);
        }

        mgr.validate()?;
        Ok(mgr)
    }

    /// Validates the security graph, checking for missing roles or cycles.
    pub fn validate(&self) -> Result<(), String> {
        for (id, role) in &self.roles {
            if let Some(parent) = &role.parent_role {
                if !self.roles.contains_key(parent) {
                    return Err(format!(
                        "Role '{id}' references unknown parent role '{parent}'"
                    ));
                }
                // Check cycle
                let mut visited = HashSet::new();
                visited.insert(id.clone());
                let mut cur = parent.clone();
                while let Some(parent_role) =
                    self.roles.get(&cur).and_then(|r| r.parent_role.as_ref())
                {
                    if !visited.insert(cur.clone()) {
                        return Err(format!(
                            "Cycle detected in GAM role hierarchy involving '{id}'"
                        ));
                    }
                    cur = parent_role.clone();
                }
            }
        }

        for (id, user) in &self.users {
            for role_id in &user.roles {
                if !self.roles.contains_key(role_id) {
                    return Err(format!("User '{id}' assigned nonexistent role '{role_id}'"));
                }
            }
        }

        Ok(())
    }

    /// Evaluates whether a user is authorized for a specific permission.
    /// Explicit Deny wins. Traverses direct permissions then hierarchical roles.
    pub fn is_authorized(&self, user_id: &str, permission: &str) -> bool {
        let user = match self.users.get(user_id) {
            Some(u) if u.is_active => u,
            _ => return false,
        };

        let perm_key = permission.to_ascii_lowercase();

        // 1. Direct permissions check
        for p in &user.direct_permissions {
            if p.name.to_ascii_lowercase() == perm_key {
                return match p.access {
                    AccessType::Allow => true,
                    AccessType::Deny => false,
                    AccessType::Inherit => false,
                };
            }
        }

        // 2. Roles check (Deny wins if encountered anywhere)
        let mut allowed = false;
        let mut visited_roles = HashSet::new();

        for role_id in &user.roles {
            if self.check_role_permission(role_id, &perm_key, &mut visited_roles, &mut allowed) {
                // Denied explicitly
                return false;
            }
        }

        allowed
    }

    /// Returns true if an explicit Deny was found.
    fn check_role_permission(
        &self,
        role_id: &str,
        perm_key: &str,
        visited: &mut HashSet<String>,
        allowed: &mut bool,
    ) -> bool {
        if !visited.insert(role_id.to_string()) {
            return false;
        }

        if let Some(role) = self.roles.get(role_id) {
            for p in &role.permissions {
                if p.name.to_ascii_lowercase() == perm_key {
                    match p.access {
                        AccessType::Deny => return true, // Immediate deny
                        AccessType::Allow => *allowed = true,
                        AccessType::Inherit => {}
                    }
                }
            }

            if role.parent_role.as_ref().is_some_and(|parent| {
                self.check_role_permission(parent, perm_key, visited, allowed)
            }) {
                return true;
            }
        }

        false
    }
}
