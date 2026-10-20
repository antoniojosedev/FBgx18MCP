//! GeneXus library membership of the rows of a model.
//!
//! Every model row is held by a module or a folder (its parent). The GeneXus runtime ships two top-level
//! modules, and the objects inside them are the GeneXus library: their structured data types are provided
//! by the runtime, so a rebuild builds one only when a generated object reaches it (see
//! `crate::reachability`). The membership is computed here from the parent chain, never from a name alone:
//! a module only counts as library when it is a direct child of the Knowledge Base root.

use std::collections::{BTreeMap, BTreeSet};

/// The top-level modules GeneXus ships. A user module cannot take these names in a GeneXus Knowledge Base.
pub const LIBRARY_MODULE_NAMES: [&str; 2] = ["GeneXus", "GeneXusUnanimo"];

/// Bounds the walk up a parent chain, so a corrupt cycle in the model cannot loop forever.
const MAX_PARENT_DEPTH: usize = 64;

/// One model row and the row that holds it. `parent_entity_type_id` is zero when the row has no holder.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ModelParentRecord {
    pub entity_type_id: i32,
    pub entity_id: i32,
    pub parent_entity_type_id: i32,
    pub parent_entity_id: i32,
}

/// The keys (entity type, entity id) of every row that is GeneXus library: a library top-level module, and
/// every row whose parent chain reaches one. `parents` maps a row to its holder; `module_names` holds the
/// name of every module row; `system_root` is the Knowledge Base root, `None` when the model has none (then
/// nothing is library).
pub fn library_members(
    parents: &BTreeMap<(i32, i32), (i32, i32)>,
    module_names: &BTreeMap<(i32, i32), String>,
    system_root: Option<(i32, i32)>,
) -> BTreeSet<(i32, i32)> {
    let Some(root) = system_root else {
        return BTreeSet::new();
    };
    let top_library: BTreeSet<(i32, i32)> = module_names
        .iter()
        .filter(|(key, name)| {
            LIBRARY_MODULE_NAMES.contains(&name.as_str()) && parents.get(*key) == Some(&root)
        })
        .map(|(key, _)| *key)
        .collect();

    let mut members = BTreeSet::new();
    for start in parents.keys().chain(top_library.iter()).copied() {
        let mut current = start;
        for _ in 0..MAX_PARENT_DEPTH {
            if top_library.contains(&current) {
                members.insert(start);
                break;
            }
            match parents.get(&current) {
                Some(&holder) => current = holder,
                None => break,
            }
        }
    }
    members
}

/// The names of the Modules that hold `key`, outermost first, without the Knowledge Base root. Folders in the
/// chain hold objects without naming them, so they are passed over. The walk stops at the root, at a row with
/// no holder, or after `MAX_PARENT_DEPTH` steps.
pub fn module_path(
    key: (i32, i32),
    parents: &BTreeMap<(i32, i32), (i32, i32)>,
    module_names: &BTreeMap<(i32, i32), String>,
    system_root: Option<(i32, i32)>,
) -> Vec<String> {
    let mut path = Vec::new();
    let mut current = key;
    for _ in 0..MAX_PARENT_DEPTH {
        let Some(&holder) = parents.get(&current) else {
            break;
        };
        if Some(holder) == system_root {
            break;
        }
        if let Some(name) = module_names.get(&holder) {
            path.push(name.clone());
        }
        current = holder;
    }
    path.reverse();
    path
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn module_path_names_the_modules_outermost_first_and_skips_folders() {
        let folder = (20, 5);
        let general = (12, 22);
        let ui = (12, 21);
        let parents: BTreeMap<_, _> = [
            ((40, 2), folder),
            (folder, ui),
            (ui, general),
            (general, ROOT),
        ]
        .into_iter()
        .collect();
        let modules = names(&[(general, "General"), (ui, "UI")]);
        assert_eq!(
            module_path((40, 2), &parents, &modules, Some(ROOT)),
            ["General", "UI"]
        );
        assert!(module_path(general, &parents, &modules, Some(ROOT)).is_empty());
    }

    const ROOT: (i32, i32) = (12, 1);

    fn names(entries: &[((i32, i32), &str)]) -> BTreeMap<(i32, i32), String> {
        entries
            .iter()
            .map(|(key, name)| (*key, name.to_string()))
            .collect()
    }

    #[test]
    fn objects_inside_the_genexus_module_are_library() {
        // GeneXus (12,3) is a child of the root; Common (12,4) is inside it; an SDT sits in a folder of Common.
        let parents = BTreeMap::from([
            ((12, 3), ROOT),
            ((12, 4), (12, 3)),
            ((8, 2), (12, 4)),
            ((40, 10), (8, 2)),
        ]);
        let module_names = names(&[
            ((12, 1), "Root Module"),
            ((12, 3), "GeneXus"),
            ((12, 4), "Common"),
        ]);
        let members = library_members(&parents, &module_names, Some(ROOT));
        assert!(
            members.contains(&(40, 10)),
            "an SDT in a folder of Common is library"
        );
        assert!(members.contains(&(12, 4)));
        assert!(
            members.contains(&(12, 3)),
            "the GeneXus module itself is library"
        );
        assert!(!members.contains(&ROOT), "the root is not library");
    }

    #[test]
    fn objects_of_user_modules_are_not_library() {
        let parents = BTreeMap::from([((12, 2), ROOT), ((40, 1), (12, 2)), ((40, 2), ROOT)]);
        let module_names = names(&[((12, 1), "Root Module"), ((12, 2), "General")]);
        assert!(library_members(&parents, &module_names, Some(ROOT)).is_empty());
    }

    #[test]
    fn a_module_named_genexus_outside_the_root_is_not_a_library_top() {
        // A nested module that happens to be named GeneXus does not make its objects library objects.
        let parents = BTreeMap::from([((12, 9), (12, 2)), ((12, 2), ROOT), ((40, 5), (12, 9))]);
        let module_names = names(&[
            ((12, 1), "Root Module"),
            ((12, 2), "General"),
            ((12, 9), "GeneXus"),
        ]);
        assert!(library_members(&parents, &module_names, Some(ROOT)).is_empty());
    }

    #[test]
    fn genexus_unanimo_is_library_too() {
        let parents = BTreeMap::from([((12, 20), ROOT), ((8, 7), (12, 20)), ((40, 56), (8, 7))]);
        let module_names = names(&[((12, 1), "Root Module"), ((12, 20), "GeneXusUnanimo")]);
        let members = library_members(&parents, &module_names, Some(ROOT));
        assert!(members.contains(&(40, 56)));
    }

    #[test]
    fn a_cycle_in_the_parent_chain_terminates_and_is_not_library() {
        let parents = BTreeMap::from([((40, 1), (8, 1)), ((8, 1), (40, 1))]);
        let module_names = names(&[((12, 1), "Root Module")]);
        assert!(library_members(&parents, &module_names, Some(ROOT)).is_empty());
    }

    #[test]
    fn no_root_means_no_library() {
        let parents = BTreeMap::from([((12, 3), ROOT)]);
        let module_names = names(&[((12, 3), "GeneXus")]);
        assert!(library_members(&parents, &module_names, None).is_empty());
    }
}
