//! Integration tests for hierarchical modules, qualified names, and homonym resolution.

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart};
use gx_engine::module::{
    MAX_MODULE_DEPTH, Module, ModuleTree, QualifiedName, build_catalog_with_objects,
    resolve_object_name,
};

#[test]
fn test_parse_qualified_name_valid_and_invalid() {
    // 1. Bare short name (root module)
    let qn = QualifiedName::parse("Invoice").expect("valid bare name");
    assert!(qn.is_root());
    assert!(qn.namespace.is_empty());
    assert_eq!(qn.object_name, "Invoice");
    assert_eq!(qn.to_string(), "Invoice");
    assert_eq!(format!("{qn}"), "Invoice");

    // 2. Two-segment qualified name
    let qn = QualifiedName::parse("Sales.Invoice").expect("valid two-segment name");
    assert!(!qn.is_root());
    assert_eq!(qn.namespace, vec!["Sales".to_string()]);
    assert_eq!(qn.object_name, "Invoice");
    assert_eq!(qn.to_string(), "Sales.Invoice");

    // 3. Three-segment qualified name
    let qn = QualifiedName::parse("Sales.Billing.Invoice").expect("valid three-segment name");
    assert!(!qn.is_root());
    assert_eq!(
        qn.namespace,
        vec!["Sales".to_string(), "Billing".to_string()]
    );
    assert_eq!(qn.object_name, "Invoice");
    assert_eq!(qn.to_string(), "Sales.Billing.Invoice");

    // 4. Valid with underscores and digits
    let qn = QualifiedName::parse("Core_v2.Sec_01.User_Auth").expect("valid name with underscores");
    assert_eq!(
        qn.namespace,
        vec!["Core_v2".to_string(), "Sec_01".to_string()]
    );
    assert_eq!(qn.object_name, "User_Auth");
    assert_eq!(qn.to_string(), "Core_v2.Sec_01.User_Auth");

    // 5. Trimming outer and segment whitespace
    let qn = QualifiedName::parse("  Sales  .  Invoicing  .  Invoice  ")
        .expect("whitespace trimmed segments");
    assert_eq!(
        qn.namespace,
        vec!["Sales".to_string(), "Invoicing".to_string()]
    );
    assert_eq!(qn.object_name, "Invoice");

    // 6. Invalid: empty and whitespace-only
    assert!(QualifiedName::parse("").is_err());
    assert!(QualifiedName::parse("   ").is_err());

    // 7. Invalid: empty segments
    assert!(QualifiedName::parse(".").is_err());
    assert!(QualifiedName::parse(".Invoice").is_err());
    assert!(QualifiedName::parse("Sales.").is_err());
    assert!(QualifiedName::parse("Sales..Invoice").is_err());

    // 8. Invalid: digits at start of segment
    assert!(QualifiedName::parse("123Invoice").is_err());
    assert!(QualifiedName::parse("Sales.123Invoice").is_err());

    // 9. Invalid: forbidden characters
    assert!(QualifiedName::parse("Sales.Inv@oice").is_err());
    assert!(QualifiedName::parse("Sales-Billing.Invoice").is_err());
    assert!(QualifiedName::parse("Sales.Bill ing.Invoice").is_err());
    assert!(QualifiedName::parse("Sales.Billing.Inv#oice").is_err());
}

#[test]
fn test_module_tree_hierarchy_and_submodules() {
    let modules = vec![
        Module::new("Root", "Root", None),
        Module::new("Sales", "Sales", Some("Root".into())),
        Module::new("Billing", "Sales.Billing", Some("Sales".into())),
        Module::new("Invoicing", "Sales.Invoicing", Some("Sales".into())),
        Module::new("Core", "Core", Some("Root".into())),
        Module::new("Security", "Core.Security", Some("Core".into())),
    ];

    let tree = ModuleTree::build(&modules).expect("tree should build successfully");

    // Inspect submodules of Sales
    let sales_subs = tree.get_submodules("Sales");
    assert_eq!(sales_subs.len(), 2);
    assert!(sales_subs.iter().any(|s| s == "Sales.Billing"));
    assert!(sales_subs.iter().any(|s| s == "Sales.Invoicing"));

    // Case-insensitive lookup
    let sales_subs_lower = tree.get_submodules("sales");
    assert_eq!(sales_subs_lower.len(), 2);

    // Leaf module has no submodules
    let billing_subs = tree.get_submodules("Sales.Billing");
    assert!(billing_subs.is_empty());

    // Core has Security
    let core_subs = tree.get_submodules("Core");
    assert_eq!(core_subs.len(), 1);
    assert!(core_subs.iter().any(|s| s == "Core.Security"));

    // Root has Sales and Core
    let root_subs = tree.get_submodules("Root");
    assert_eq!(root_subs.len(), 2);
    assert!(root_subs.iter().any(|s| s == "Sales"));
    assert!(root_subs.iter().any(|s| s == "Core"));

    // Non-existent module returns empty
    assert!(tree.get_submodules("NonExistent").is_empty());

    // Lookup module
    let mod_billing = tree.get_module("Sales.Billing").expect("billing module");
    assert_eq!(mod_billing.name, "Billing");
    assert_eq!(mod_billing.qualified_name, "Sales.Billing");
    assert_eq!(mod_billing.parent.as_deref(), Some("Sales"));
}

#[test]
fn test_module_tree_rejects_cycles() {
    // 1. Direct self-cycle: A -> A
    let m = Module::new("A", "A", Some("A".into()));
    let err = ModuleTree::build(&[m]).expect_err("direct self-cycle must fail");
    assert!(
        err.to_lowercase().contains("cyclic"),
        "expected error to mention cyclic, got: {err}"
    );

    // 2. Two-node cycle: A -> B -> A
    let m1 = Module::new("A", "A", Some("B".into()));
    let m2 = Module::new("B", "B", Some("A".into()));
    let err = ModuleTree::build(&[m1, m2]).expect_err("two-node cycle must fail");
    assert!(
        err.to_lowercase().contains("cyclic"),
        "expected error to mention cyclic, got: {err}"
    );

    // 3. Three-node cycle: A -> C -> B -> A
    let m1 = Module::new("A", "A", Some("C".into()));
    let m2 = Module::new("B", "B", Some("A".into()));
    let m3 = Module::new("C", "C", Some("B".into()));
    let err = ModuleTree::build(&[m1, m2, m3]).expect_err("three-node cycle must fail");
    assert!(
        err.to_lowercase().contains("cyclic"),
        "expected error to mention cyclic, got: {err}"
    );

    // 4. Cycle through submodules declaration: A submodules [A]
    let mut m_sub = Module::new("Loop", "Loop", None);
    m_sub.submodules.push("Loop".into());
    let err = ModuleTree::build(&[m_sub]).expect_err("submodule self-loop must fail");
    assert!(
        err.to_lowercase().contains("cyclic"),
        "expected error to mention cyclic, got: {err}"
    );
}

#[test]
fn test_resolve_exact_qualified_name() {
    let mut obj_sales_inv = KbObject::new(
        ObjectId::new("sales-inv").unwrap(),
        "Invoice",
        ObjectKind::Procedure,
    );
    obj_sales_inv.source_parts.push(SourcePart {
        name: "MODULE".into(),
        source: "Sales".into(),
    });

    let mut obj_bill_inv = KbObject::new(
        ObjectId::new("bill-inv").unwrap(),
        "Invoice",
        ObjectKind::Procedure,
    );
    obj_bill_inv.source_parts.push(SourcePart {
        name: "MODULE".into(),
        source: "Sales.Billing".into(),
    });

    let mut obj_sec_auth = KbObject::new(
        ObjectId::new("sec-auth").unwrap(),
        "AuthService",
        ObjectKind::Procedure,
    );
    obj_sec_auth.source_parts.push(SourcePart {
        name: "PROPERTIES".into(),
        source: "Module = Core.Security\nIsMain = True".into(),
    });

    let catalog = build_catalog_with_objects(vec![obj_sales_inv, obj_bill_inv, obj_sec_auth]);

    // Resolve Sales.Invoice
    let resolved = resolve_object_name("Sales.Invoice", None, &catalog).expect("Sales.Invoice");
    assert_eq!(resolved.id.as_str(), "sales-inv");

    // Resolve Sales.Billing.Invoice
    let resolved = resolve_object_name("Sales.Billing.Invoice", None, &catalog)
        .expect("Sales.Billing.Invoice");
    assert_eq!(resolved.id.as_str(), "bill-inv");

    // Resolve Core.Security.AuthService (from PROPERTIES metadata)
    let resolved = resolve_object_name("Core.Security.AuthService", None, &catalog)
        .expect("Core.Security.AuthService");
    assert_eq!(resolved.id.as_str(), "sec-auth");

    // Case-insensitive resolution
    let resolved =
        resolve_object_name("sales.billing.invoice", None, &catalog).expect("case-insensitive");
    assert_eq!(resolved.id.as_str(), "bill-inv");

    // Non-existent module
    let err = resolve_object_name("Marketing.Invoice", None, &catalog).unwrap_err();
    assert_eq!(err, "object 'Marketing.Invoice' not found in catalog");

    // Non-existent object in valid module
    let err = resolve_object_name("Sales.Customer", None, &catalog).unwrap_err();
    assert_eq!(err, "object 'Sales.Customer' not found in catalog");
}

#[test]
fn test_resolve_short_name_in_local_module_precedence() {
    let mut obj_sales_inv = KbObject::new(
        ObjectId::new("sales-inv").unwrap(),
        "Invoice",
        ObjectKind::Procedure,
    );
    obj_sales_inv.source_parts.push(SourcePart {
        name: "MODULE".into(),
        source: "Sales".into(),
    });

    let mut obj_bill_inv = KbObject::new(
        ObjectId::new("bill-inv").unwrap(),
        "Invoice",
        ObjectKind::Procedure,
    );
    obj_bill_inv.source_parts.push(SourcePart {
        name: "MODULE".into(),
        source: "Billing".into(),
    });

    let catalog = build_catalog_with_objects(vec![obj_sales_inv, obj_bill_inv]);

    // With current_module = Some("Sales"), resolves to Sales.Invoice
    let resolved =
        resolve_object_name("Invoice", Some("Sales"), &catalog).expect("resolves in Sales");
    assert_eq!(resolved.id.as_str(), "sales-inv");

    // With current_module = Some("Billing"), resolves to Billing.Invoice
    let resolved =
        resolve_object_name("Invoice", Some("Billing"), &catalog).expect("resolves in Billing");
    assert_eq!(resolved.id.as_str(), "bill-inv");

    // Case-insensitive module matching
    let resolved =
        resolve_object_name("Invoice", Some("sales"), &catalog).expect("case-insensitive Sales");
    assert_eq!(resolved.id.as_str(), "sales-inv");
}

#[test]
fn test_resolve_short_name_unambiguous_global() {
    let mut obj_cust = KbObject::new(
        ObjectId::new("sales-cust").unwrap(),
        "Customer",
        ObjectKind::Procedure,
    );
    obj_cust.source_parts.push(SourcePart {
        name: "MODULE".into(),
        source: "Sales".into(),
    });

    let mut obj_prod = KbObject::new(
        ObjectId::new("inv-prod").unwrap(),
        "Product",
        ObjectKind::Procedure,
    );
    obj_prod.source_parts.push(SourcePart {
        name: "MODULE".into(),
        source: "Inventory".into(),
    });

    let catalog = build_catalog_with_objects(vec![obj_cust, obj_prod]);

    // Unambiguous global query with no current module
    let resolved =
        resolve_object_name("Customer", None, &catalog).expect("Customer resolves globally");
    assert_eq!(resolved.id.as_str(), "sales-cust");

    // Query for Product from within current_module = Some("Sales")
    // Product does not exist in Sales, but is unambiguous globally
    let resolved = resolve_object_name("Product", Some("Sales"), &catalog)
        .expect("Product resolves globally despite current_module");
    assert_eq!(resolved.id.as_str(), "inv-prod");

    // Unknown object fails
    let err = resolve_object_name("NonExistent", None, &catalog).unwrap_err();
    assert_eq!(err, "object 'NonExistent' not found in catalog");
}

#[test]
fn test_fail_closed_spc0006_homonym_ambiguity() {
    let mut obj_sales_inv = KbObject::new(
        ObjectId::new("sales-inv").unwrap(),
        "Invoice",
        ObjectKind::Procedure,
    );
    obj_sales_inv.source_parts.push(SourcePart {
        name: "MODULE".into(),
        source: "Sales".into(),
    });

    let mut obj_bill_inv = KbObject::new(
        ObjectId::new("bill-inv").unwrap(),
        "Invoice",
        ObjectKind::Procedure,
    );
    obj_bill_inv.source_parts.push(SourcePart {
        name: "MODULE".into(),
        source: "Billing".into(),
    });

    let mut obj_core_inv = KbObject::new(
        ObjectId::new("core-inv").unwrap(),
        "Invoice",
        ObjectKind::Procedure,
    );
    obj_core_inv.source_parts.push(SourcePart {
        name: "MODULE".into(),
        source: "Core".into(),
    });

    let catalog = build_catalog_with_objects(vec![obj_sales_inv, obj_bill_inv, obj_core_inv]);

    // Querying "Invoice" with current_module = None must fail closed with spc0006
    let err = resolve_object_name("Invoice", None, &catalog).unwrap_err();
    assert!(
        err.starts_with("spc0006: Ambiguous object reference 'Invoice'; qualified name required"),
        "error must start with spc0006 envelope: {err}"
    );
    assert!(
        err.contains("Billing.Invoice"),
        "must list Billing candidate: {err}"
    );
    assert!(
        err.contains("Core.Invoice"),
        "must list Core candidate: {err}"
    );
    assert!(
        err.contains("Sales.Invoice"),
        "must list Sales candidate: {err}"
    );

    // Querying from an unrelated current_module (e.g. "Inventory") also fails with spc0006
    let err = resolve_object_name("Invoice", Some("Inventory"), &catalog).unwrap_err();
    assert!(
        err.starts_with("spc0006: Ambiguous object reference 'Invoice'; qualified name required"),
        "error must start with spc0006 envelope: {err}"
    );
}

#[test]
fn test_module_tree_depth_limit_16() {
    // Build a linear chain of exactly 16 modules: M1 -> M2 -> ... -> M16
    let mut modules_16 = Vec::new();
    modules_16.push(Module::new("M1", "M1", None));
    for i in 2..=MAX_MODULE_DEPTH {
        let parent = format!("M{}", i - 1);
        let name = format!("M{i}");
        modules_16.push(Module::new(name.clone(), name, Some(parent)));
    }
    assert_eq!(modules_16.len(), 16);

    let tree = ModuleTree::build(&modules_16);
    assert!(
        tree.is_ok(),
        "depth of 16 must be permitted, failed with: {:?}",
        tree.err()
    );

    // Adding 17th module: M16 -> M17
    let mut modules_17 = modules_16;
    modules_17.push(Module::new("M17", "M17", Some("M16".into())));
    assert_eq!(modules_17.len(), 17);

    let err = ModuleTree::build(&modules_17).expect_err("depth of 17 must be rejected");
    assert!(
        err.to_lowercase().contains("depth") && err.contains("16"),
        "error must mention depth limit 16: {err}"
    );
}

#[test]
fn test_module_serialization_and_catalog_filtering() {
    // 1. QualifiedName serialization and deserialization
    let qn = QualifiedName::parse("Sales.Billing.Invoice").unwrap();
    let qn_json = serde_json::to_string(&qn).expect("serialize QualifiedName");
    let qn_roundtrip: QualifiedName =
        serde_json::from_str(&qn_json).expect("deserialize QualifiedName");
    assert_eq!(qn, qn_roundtrip);

    // 2. Module serialization and deserialization
    let mut module = Module::new("Billing", "Sales.Billing", Some("Sales".into()));
    let id1 = ObjectId::new("proc-01").unwrap();
    let id2 = ObjectId::new("proc-02").unwrap();
    let id3 = ObjectId::new("proc-03").unwrap();
    module.object_ids.push(id1.clone());
    module.object_ids.push(id2.clone());

    let mod_json = serde_json::to_string(&module).expect("serialize Module");
    let mod_roundtrip: Module = serde_json::from_str(&mod_json).expect("deserialize Module");
    assert_eq!(module, mod_roundtrip);

    // 3. ModuleTree serialization and deserialization
    let tree = ModuleTree::build(&[Module::new("Sales", "Sales", None), module.clone()])
        .expect("build tree");
    let tree_json = serde_json::to_string(&tree).expect("serialize ModuleTree");
    let tree_roundtrip: ModuleTree =
        serde_json::from_str(&tree_json).expect("deserialize ModuleTree");
    assert_eq!(tree, tree_roundtrip);

    // 4. Catalog filtering by module object_ids
    let obj1 = KbObject::new(id1.clone(), "ProcOne", ObjectKind::Procedure);
    let obj2 = KbObject::new(id2.clone(), "ProcTwo", ObjectKind::Procedure);
    let obj3 = KbObject::new(id3.clone(), "ProcThree", ObjectKind::Procedure);

    let catalog = build_catalog_with_objects(vec![obj1, obj2, obj3]);

    let filtered = module.filter_catalog(&catalog);
    assert_eq!(filtered.len(), 2);
    assert!(filtered.iter().any(|o| o.id == id1));
    assert!(filtered.iter().any(|o| o.id == id2));
    assert!(!filtered.iter().any(|o| o.id == id3));
}

#[test]
fn test_module_resolution_via_module_kind_object_in_catalog() {
    let mod_id = ObjectId::new("mod-core").unwrap();
    let proc_id = ObjectId::new("proc-core-worker").unwrap();

    let mut mod_obj = KbObject::new(mod_id, "Core", ObjectKind::Module);
    mod_obj.references.push(Reference {
        kind: ReferenceKind::Contains,
        target: proc_id.clone(),
    });

    let proc_obj = KbObject::new(proc_id.clone(), "Worker", ObjectKind::Procedure);

    let catalog = build_catalog_with_objects(vec![mod_obj, proc_obj]);

    // Should resolve via ObjectKind::Module reference
    let resolved = resolve_object_name("Core.Worker", None, &catalog).expect("Core.Worker");
    assert_eq!(resolved.id, proc_id);

    // Short name query with current_module = Some("Core")
    let resolved = resolve_object_name("Worker", Some("Core"), &catalog).expect("Worker in Core");
    assert_eq!(resolved.id, proc_id);
}
