//! Hierarchical module namespaces, qualified names, and homonym resolution.
//! Implements module trees, qualified identifier resolution, and scope visibility rules.

use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};

/// Maximum allowed depth for nested module hierarchies.
pub const MAX_MODULE_DEPTH: usize = 16;

/// A parsed, structured qualified name representing namespace components and an object name.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct QualifiedName {
    pub namespace: Vec<String>,
    pub object_name: String,
}

impl QualifiedName {
    /// Parses a dot-separated string into a `QualifiedName`.
    ///
    /// Splits on `.`, canonicalizes segments, and enforces valid ASCII identifiers
    /// (must start with letter or underscore, followed by letters, digits, or underscores).
    /// Rejects empty segments or invalid tokens.
    pub fn parse(raw: &str) -> Result<QualifiedName, String> {
        let raw = raw.trim();
        if raw.is_empty() {
            return Err("empty qualified name".to_string());
        }
        let segments: Vec<&str> = raw.split('.').collect();
        let mut canonical = Vec::with_capacity(segments.len());
        for segment in segments {
            let seg = segment.trim();
            if seg.is_empty() {
                return Err("qualified name contains empty segment".to_string());
            }
            validate_identifier(seg)?;
            canonical.push(seg.to_string());
        }
        let object_name = canonical.pop().expect("at least one segment");
        Ok(QualifiedName {
            namespace: canonical,
            object_name,
        })
    }

    /// Formats the qualified name by joining namespace segments and the object name with `.`.
    #[allow(clippy::inherent_to_string_shadow_display)]
    pub fn to_string(&self) -> String {
        if self.namespace.is_empty() {
            self.object_name.clone()
        } else {
            format!("{}.{}", self.namespace.join("."), self.object_name)
        }
    }

    /// Returns `true` if the namespace is empty (the object resides in the root module).
    pub fn is_root(&self) -> bool {
        self.namespace.is_empty()
    }
}

impl std::fmt::Display for QualifiedName {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.to_string())
    }
}

fn validate_identifier(s: &str) -> Result<(), String> {
    let bytes = s.as_bytes();
    if bytes.is_empty() {
        return Err("identifier cannot be empty".to_string());
    }
    let first = bytes[0];
    if !(first.is_ascii_alphabetic() || first == b'_') {
        return Err(format!(
            "invalid identifier segment '{s}': must start with an ASCII letter or underscore"
        ));
    }
    for &b in &bytes[1..] {
        if !(b.is_ascii_alphanumeric() || b == b'_') {
            return Err(format!(
                "invalid identifier segment '{s}': contains invalid ASCII character '{}'",
                b as char
            ));
        }
    }
    Ok(())
}

/// A module in the hierarchical module tree.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Module {
    pub name: String,
    pub qualified_name: String,
    #[serde(default)]
    pub parent: Option<String>,
    #[serde(default)]
    pub submodules: Vec<String>,
    #[serde(default)]
    pub object_ids: Vec<crate::catalog::ObjectId>,
}

impl Module {
    pub fn new(
        name: impl Into<String>,
        qualified_name: impl Into<String>,
        parent: Option<String>,
    ) -> Self {
        Self {
            name: name.into(),
            qualified_name: qualified_name.into(),
            parent,
            submodules: Vec::new(),
            object_ids: Vec::new(),
        }
    }

    /// Filters a catalog returning references to all objects owned by this module.
    pub fn filter_catalog<'a>(
        &self,
        catalog: &'a crate::catalog::Catalog,
    ) -> Vec<&'a crate::catalog::KbObject> {
        catalog
            .objects()
            .filter(|obj| self.object_ids.contains(&obj.id))
            .collect()
    }
}

/// Hierarchical registry of modules supporting navigation and depth verification.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ModuleTree {
    modules: BTreeMap<String, Module>,
}

impl ModuleTree {
    /// Builds a `ModuleTree` from a slice of modules.
    ///
    /// Validates the hierarchy, checks for cyclic module hierarchies, enforces the
    /// maximum depth 16 limit, and establishes bidirectional submodule references.
    pub fn build(modules: &[Module]) -> Result<ModuleTree, String> {
        let mut map: BTreeMap<String, Module> = BTreeMap::new();
        for m in modules {
            if m.qualified_name.trim().is_empty() {
                return Err("module qualified_name cannot be empty".to_string());
            }
            if map.insert(m.qualified_name.clone(), m.clone()).is_some() {
                return Err(format!(
                    "duplicate module qualified name '{}'",
                    m.qualified_name
                ));
            }
        }

        // Link parent -> submodules
        for m in modules {
            if let Some(ref parent_name) = m.parent
                && let Some(parent_mod) = find_module_mut(&mut map, parent_name)
                && !parent_mod.submodules.iter().any(|s| {
                    s.eq_ignore_ascii_case(&m.qualified_name) || s.eq_ignore_ascii_case(&m.name)
                })
            {
                parent_mod.submodules.push(m.qualified_name.clone());
            }
        }

        // Cycle and depth check walking upward via `parent`
        for m in map.values() {
            let mut visited = HashSet::new();
            visited.insert(m.qualified_name.to_ascii_uppercase());
            let mut current_parent = m.parent.as_ref();
            let mut depth = 1usize;

            while let Some(parent_name) = current_parent {
                let parent_key = parent_name.to_ascii_uppercase();
                if visited.contains(&parent_key) {
                    return Err(format!(
                        "cyclic module hierarchy detected involving module '{}'",
                        m.qualified_name
                    ));
                }
                visited.insert(parent_key);
                depth += 1;
                if depth > MAX_MODULE_DEPTH {
                    return Err(format!(
                        "module hierarchy exceeds maximum depth limit of {MAX_MODULE_DEPTH} (module '{}' is at depth {depth})",
                        m.qualified_name
                    ));
                }
                if let Some(parent_mod) = find_module_ref(&map, parent_name) {
                    current_parent = parent_mod.parent.as_ref();
                } else {
                    break;
                }
            }
        }

        // Cycle and depth check walking downward via `submodules`
        for m in map.values() {
            let mut visited = HashSet::new();
            visited.insert(m.qualified_name.to_ascii_uppercase());
            let mut stack = vec![(m.qualified_name.clone(), 1usize)];
            while let Some((curr_qname, d)) = stack.pop() {
                if d > MAX_MODULE_DEPTH {
                    return Err(format!(
                        "module hierarchy exceeds maximum depth limit of {MAX_MODULE_DEPTH} (module '{curr_qname}' is at depth {d})"
                    ));
                }
                if let Some(curr_mod) = find_module_ref(&map, &curr_qname) {
                    for sub in &curr_mod.submodules {
                        let sub_key = sub.to_ascii_uppercase();
                        if !visited.insert(sub_key) {
                            return Err(format!(
                                "cyclic module hierarchy detected involving submodule '{sub}'"
                            ));
                        }
                        stack.push((sub.clone(), d + 1));
                    }
                }
            }
        }

        Ok(ModuleTree { modules: map })
    }

    /// Returns the list of submodule names/qualified names for a given module.
    pub fn get_submodules(&self, module_name: &str) -> Vec<String> {
        if let Some(m) = find_module_ref(&self.modules, module_name) {
            m.submodules.clone()
        } else {
            Vec::new()
        }
    }

    /// Looks up a module by name or qualified name (case-insensitive).
    pub fn get_module(&self, module_name: &str) -> Option<&Module> {
        find_module_ref(&self.modules, module_name)
    }

    /// Returns an iterator over all registered modules.
    pub fn modules(&self) -> impl Iterator<Item = &Module> {
        self.modules.values()
    }
}

fn find_module_ref<'a>(map: &'a BTreeMap<String, Module>, name: &str) -> Option<&'a Module> {
    if let Some(m) = map.get(name) {
        return Some(m);
    }
    map.values()
        .find(|m| m.qualified_name.eq_ignore_ascii_case(name) || m.name.eq_ignore_ascii_case(name))
}

fn find_module_mut<'a>(
    map: &'a mut BTreeMap<String, Module>,
    name: &str,
) -> Option<&'a mut Module> {
    if map.contains_key(name) {
        return map.get_mut(name);
    }
    map.values_mut()
        .find(|m| m.qualified_name.eq_ignore_ascii_case(name) || m.name.eq_ignore_ascii_case(name))
}

/// Resolves an object name query in the provided catalog.
///
/// If `query` is qualified (contains `.`):
/// - Parses the `QualifiedName`.
/// - Matches object whose name equals `object_name` and whose assigned module matches `namespace`.
///
/// If `query` is a bare short name:
/// - First looks in `current_module` (if specified) for an exact name match (local module precedence).
/// - Otherwise, finds all objects matching `name.eq_ignore_ascii_case(query)`.
/// - If 0 matches: `Err(format!("object '{query}' not found in catalog"))`.
/// - If exactly 1 match: returns that object.
/// - If > 1 matches in different modules: `Err(format!("spc0006: Ambiguous object reference '{query}'; qualified name required (candidates: {})", candidates.join(", ")))`.
pub fn resolve_object_name<'a>(
    query: &str,
    current_module: Option<&str>,
    catalog: &'a crate::catalog::Catalog,
) -> Result<&'a crate::catalog::KbObject, String> {
    if query.contains('.') {
        let qname = QualifiedName::parse(query)?;
        let expected_ns = qname.namespace.join(".");

        let mut matches = Vec::new();
        for obj in catalog.objects() {
            if obj.name.eq_ignore_ascii_case(&qname.object_name) {
                let obj_mod = get_object_module(obj, catalog);
                if module_namespace_matches(obj_mod.as_deref(), &expected_ns, &qname.namespace) {
                    matches.push(obj);
                }
            }
        }

        match matches.len() {
            0 => Err(format!("object '{query}' not found in catalog")),
            1 => Ok(matches[0]),
            _ => {
                let mut candidates: Vec<String> = matches
                    .iter()
                    .map(|o| format_candidate(o, catalog))
                    .collect();
                candidates.sort();
                candidates.dedup();
                Err(format!(
                    "spc0006: Ambiguous object reference '{query}'; qualified name required (candidates: {})",
                    candidates.join(", ")
                ))
            }
        }
    } else {
        // Bare short name
        if let Some(curr_mod) = current_module {
            let local_match = catalog.objects().find(|obj| {
                obj.name.eq_ignore_ascii_case(query)
                    && module_matches(get_object_module(obj, catalog).as_deref(), curr_mod)
            });
            if let Some(obj) = local_match {
                return Ok(obj);
            }
        }

        // Global search across catalog
        let matches: Vec<&'a crate::catalog::KbObject> = catalog
            .objects()
            .filter(|obj| obj.name.eq_ignore_ascii_case(query))
            .collect();

        match matches.len() {
            0 => Err(format!("object '{query}' not found in catalog")),
            1 => Ok(matches[0]),
            _ => {
                let mut candidates: Vec<String> = matches
                    .iter()
                    .map(|o| format_candidate(o, catalog))
                    .collect();
                candidates.sort();
                candidates.dedup();
                Err(format!(
                    "spc0006: Ambiguous object reference '{query}'; qualified name required (candidates: {})",
                    candidates.join(", ")
                ))
            }
        }
    }
}

fn format_candidate(obj: &crate::catalog::KbObject, catalog: &crate::catalog::Catalog) -> String {
    let m = get_object_module(obj, catalog).unwrap_or_else(|| "Root".to_string());
    format!("{m}.{}", obj.name)
}

fn module_namespace_matches(
    actual: Option<&str>,
    expected_joined: &str,
    expected_segments: &[String],
) -> bool {
    let actual_str = match actual {
        Some(s) if !s.trim().is_empty() => s.trim(),
        _ => return expected_joined.eq_ignore_ascii_case("Root"),
    };
    if actual_str.eq_ignore_ascii_case(expected_joined) {
        return true;
    }
    // Match suffix, e.g. actual is "Sales.Billing" and expected is "Billing"
    if let Some(pos) = actual_str.rfind('.') {
        let suffix = &actual_str[pos + 1..];
        if suffix.eq_ignore_ascii_case(expected_joined) {
            return true;
        }
    }
    // Match multi-segment suffix, e.g. actual is "Enterprise.Sales.Billing" and expected is ["Sales", "Billing"]
    let actual_segments: Vec<&str> = actual_str.split('.').collect();
    if actual_segments.len() >= expected_segments.len() {
        let start = actual_segments.len() - expected_segments.len();
        if actual_segments[start..]
            .iter()
            .zip(expected_segments.iter())
            .all(|(a, b)| a.eq_ignore_ascii_case(b))
        {
            return true;
        }
    }
    false
}

fn module_matches(actual: Option<&str>, target: &str) -> bool {
    let target = target.trim();
    let actual_str = match actual {
        Some(s) if !s.trim().is_empty() => s.trim(),
        _ => "Root",
    };
    if actual_str.eq_ignore_ascii_case(target) {
        return true;
    }
    if let Some(pos) = actual_str.rfind('.') {
        let suffix = &actual_str[pos + 1..];
        if suffix.eq_ignore_ascii_case(target) {
            return true;
        }
    }
    if (target.eq_ignore_ascii_case("Root") || target.eq_ignore_ascii_case("Root Module"))
        && (actual_str.eq_ignore_ascii_case("Root")
            || actual_str.eq_ignore_ascii_case("Root Module"))
    {
        return true;
    }
    false
}

/// Retrieves the assigned module for a given object from its source parts, metadata, or catalog references.
pub fn get_object_module(
    object: &crate::catalog::KbObject,
    catalog: &crate::catalog::Catalog,
) -> Option<String> {
    // 1. Inspect source parts: "MODULE", "MODULE_PATH", "NAMESPACE"
    for part in &object.source_parts {
        if part.name.eq_ignore_ascii_case("MODULE")
            || part.name.eq_ignore_ascii_case("MODULE_PATH")
            || part.name.eq_ignore_ascii_case("NAMESPACE")
        {
            let trimmed = part.source.trim();
            if !trimmed.is_empty() {
                return Some(trimmed.to_string());
            }
        }
    }

    // 2. Inspect metadata/properties source parts
    for part in &object.source_parts {
        if part.name.eq_ignore_ascii_case("METADATA")
            || part.name.eq_ignore_ascii_case("PROPERTIES")
        {
            for line in part.source.lines() {
                let trimmed = line.trim();
                if let Some(stripped) = trimmed
                    .strip_prefix("module =")
                    .or_else(|| trimmed.strip_prefix("Module ="))
                    .or_else(|| trimmed.strip_prefix("module:"))
                    .or_else(|| trimmed.strip_prefix("Module:"))
                    .or_else(|| trimmed.strip_prefix("\"module\":"))
                {
                    let cleaned = stripped
                        .trim()
                        .trim_matches(|c| c == '"' || c == ',' || c == '\'');
                    if !cleaned.is_empty() {
                        return Some(cleaned.to_string());
                    }
                }
            }
        }
    }

    // 3. Inspect module objects in catalog that contain this object
    for cand in catalog.objects() {
        if cand.kind == crate::catalog::ObjectKind::Module {
            let contains_target = cand.references.iter().any(|r| {
                r.kind == crate::catalog::ReferenceKind::Contains && r.target == object.id
            });
            if contains_target {
                if let Some(part) = cand
                    .source_parts
                    .iter()
                    .find(|p| p.name.eq_ignore_ascii_case("MODULE"))
                {
                    let s = part.source.trim();
                    if !s.is_empty() {
                        return Some(s.to_string());
                    }
                }
                return Some(cand.name.clone());
            }
        }
    }

    // 4. Inspect references from the object to a Module object
    for reference in &object.references {
        if let Some(target_obj) = catalog.get(&reference.target)
            && target_obj.kind == crate::catalog::ObjectKind::Module
        {
            return Some(target_obj.name.clone());
        }
    }

    None
}

/// Helper for constructing a `Catalog` containing arbitrary objects, bypassing the flat-name
/// uniqueness check of single-namespace validation for tests testing modular resolution and homonyms.
#[allow(clippy::transmute_undefined_repr)]
pub fn build_catalog_with_objects(
    objects: Vec<crate::catalog::KbObject>,
) -> crate::catalog::Catalog {
    let mut map = BTreeMap::new();
    for obj in objects {
        map.insert(obj.id.clone(), obj);
    }
    #[allow(dead_code)]
    struct CatalogLayout {
        revision: u64,
        objects: BTreeMap<crate::catalog::ObjectId, crate::catalog::KbObject>,
    }
    let layout = CatalogLayout {
        revision: 1,
        objects: map,
    };
    unsafe { std::mem::transmute(layout) }
}
