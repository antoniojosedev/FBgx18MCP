use gx_engine::catalog::{
    Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
};
use gx_engine::impact::{
    BlastRadiusReport, DependencyEdge, DependencyKind, ImpactGraph, MAX_TRANSITIVE_DEPTH,
    ObjectRef, RiskLevel,
};

fn id(s: &str) -> ObjectId {
    ObjectId::new(s).unwrap()
}

#[test]
fn test_direct_procedure_dependencies() {
    let mut graph = ImpactGraph::new();
    let proc_a = ObjectRef::procedure("ProcA");
    let proc_b = ObjectRef::procedure("ProcB");
    let proc_c = ObjectRef::procedure("ProcC");
    let proc_d = ObjectRef::procedure("ProcD");

    graph.add_dependency(
        proc_a.clone(),
        proc_b.clone(),
        DependencyKind::CallsProcedure,
    );
    graph.add_dependency(
        proc_a.clone(),
        proc_c.clone(),
        DependencyKind::CallsProcedure,
    );
    graph.add_dependency(
        proc_b.clone(),
        proc_d.clone(),
        DependencyKind::CallsProcedure,
    );

    let deps_a = graph.direct_dependencies(&proc_a);
    assert_eq!(deps_a.len(), 2);
    assert!(
        deps_a
            .iter()
            .any(|e| e.to == proc_b && e.kind == DependencyKind::CallsProcedure)
    );
    assert!(
        deps_a
            .iter()
            .any(|e| e.to == proc_c && e.kind == DependencyKind::CallsProcedure)
    );

    let deps_b = graph.direct_dependencies(&proc_b);
    assert_eq!(deps_b.len(), 1);
    assert_eq!(deps_b[0].to, proc_d);

    let callers_b = graph.direct_dependents(&proc_b);
    assert_eq!(callers_b.len(), 1);
    assert_eq!(callers_b[0].from, proc_a);

    let callers_d = graph.direct_dependents(&proc_d);
    assert_eq!(callers_d.len(), 1);
    assert_eq!(callers_d[0].from, proc_b);
}

#[test]
fn test_transitive_dependents_and_callers() {
    let mut graph = ImpactGraph::new();
    let p1 = ObjectRef::procedure("Proc1");
    let p2 = ObjectRef::procedure("Proc2");
    let p3 = ObjectRef::procedure("Proc3");
    let p4 = ObjectRef::procedure("Proc4");
    let p5 = ObjectRef::procedure("Proc5");

    // P1 -> P2 -> P3 -> P4
    // P5 -> P3
    graph.add_dependency(p1.clone(), p2.clone(), DependencyKind::CallsProcedure);
    graph.add_dependency(p2.clone(), p3.clone(), DependencyKind::CallsProcedure);
    graph.add_dependency(p3.clone(), p4.clone(), DependencyKind::CallsProcedure);
    graph.add_dependency(p5.clone(), p3.clone(), DependencyKind::CallsProcedure);

    let trans_deps_1 = graph.transitive_dependencies(&p1, 16);
    assert_eq!(trans_deps_1, vec![p2.clone(), p3.clone(), p4.clone()]);

    let trans_dependents_4 = graph.transitive_dependents(&p4, 16);
    // Depth 1: P3. Depth 2: P2, P5. Depth 3: P1.
    assert_eq!(trans_dependents_4.len(), 4);
    assert!(trans_dependents_4.contains(&p3));
    assert!(trans_dependents_4.contains(&p2));
    assert!(trans_dependents_4.contains(&p5));
    assert!(trans_dependents_4.contains(&p1));

    let trans_dependents_3 = graph.transitive_dependents(&p3, 16);
    assert_eq!(trans_dependents_3.len(), 3);
    assert!(trans_dependents_3.contains(&p2));
    assert!(trans_dependents_3.contains(&p5));
    assert!(trans_dependents_3.contains(&p1));
}

#[test]
fn test_cycle_safety_in_recursion() {
    let mut graph = ImpactGraph::new();
    let pa = ObjectRef::procedure("ProcA");
    let pb = ObjectRef::procedure("ProcB");
    let pc = ObjectRef::procedure("ProcC");

    // Mutual recursion: ProcA <-> ProcB
    graph.add_dependency(pa.clone(), pb.clone(), DependencyKind::CallsProcedure);
    graph.add_dependency(pb.clone(), pa.clone(), DependencyKind::CallsProcedure);

    // Self recursion: ProcC -> ProcC
    graph.add_dependency(pc.clone(), pc.clone(), DependencyKind::CallsProcedure);

    // Transitive dependencies from ProcA should terminate and return [ProcB]
    let deps_a = graph.transitive_dependencies(&pa, 16);
    assert_eq!(deps_a, vec![pb.clone()]);

    // Transitive dependents of ProcB should terminate and return [ProcA]
    let dependents_b = graph.transitive_dependents(&pb, 16);
    assert_eq!(dependents_b, vec![pa.clone()]);

    // Transitive dependencies from ProcC should terminate and be empty
    let deps_c = graph.transitive_dependencies(&pc, 16);
    assert!(deps_c.is_empty());

    // Triangular cycle: X -> Y -> Z -> X
    let px = ObjectRef::procedure("ProcX");
    let py = ObjectRef::procedure("ProcY");
    let pz = ObjectRef::procedure("ProcZ");
    graph.add_dependency(px.clone(), py.clone(), DependencyKind::CallsProcedure);
    graph.add_dependency(py.clone(), pz.clone(), DependencyKind::CallsProcedure);
    graph.add_dependency(pz.clone(), px.clone(), DependencyKind::CallsProcedure);

    let deps_x = graph.transitive_dependencies(&px, 16);
    assert_eq!(deps_x, vec![py.clone(), pz.clone()]);
}

#[test]
fn test_attribute_impact_query() {
    let mut graph = ImpactGraph::new();
    let pa = ObjectRef::procedure("ProcA");
    let pb = ObjectRef::procedure("ProcB");
    let pc = ObjectRef::procedure("ProcC");
    let pd = ObjectRef::procedure("ProcD");

    let cust_name = ObjectRef::attribute("CustomerName");
    let cust_addr = ObjectRef::attribute("CustomerAddress");

    graph.add_dependency(
        pa.clone(),
        cust_name.clone(),
        DependencyKind::ReferencesAttribute,
    );
    graph.add_dependency(
        pb.clone(),
        cust_name.clone(),
        DependencyKind::ReferencesAttribute,
    );
    graph.add_dependency(
        pc.clone(),
        cust_name.clone(),
        DependencyKind::ReferencesAttribute,
    );
    graph.add_dependency(
        pd.clone(),
        cust_addr.clone(),
        DependencyKind::ReferencesAttribute,
    );

    let name_impact = graph.impact_of_attribute("CustomerName");
    assert_eq!(name_impact.len(), 3);
    assert!(name_impact.contains(&pa));
    assert!(name_impact.contains(&pb));
    assert!(name_impact.contains(&pc));

    // Case-insensitivity check
    let name_impact_ci = graph.impact_of_attribute("customername");
    assert_eq!(name_impact_ci.len(), 3);

    let addr_impact = graph.impact_of_attribute("CustomerAddress");
    assert_eq!(addr_impact, vec![pd]);

    let non_existent = graph.impact_of_attribute("NonExistentAttribute");
    assert!(non_existent.is_empty());
}

#[test]
fn test_table_impact_query() {
    let mut graph = ImpactGraph::new();
    let proc_read = ObjectRef::procedure("ProcReader");
    let proc_update = ObjectRef::procedure("ProcUpdater");
    let proc_insert = ObjectRef::procedure("ProcInserter");
    let proc_delete = ObjectRef::procedure("ProcDeleter");
    let trn_invoice = ObjectRef::transaction("Invoice");
    let proc_other = ObjectRef::procedure("ProcOther");

    let tbl_invoice = ObjectRef::table("Invoice");
    let tbl_customer = ObjectRef::table("Customer");

    graph.add_dependency(
        proc_read.clone(),
        tbl_invoice.clone(),
        DependencyKind::ReadsTable,
    );
    graph.add_dependency(
        proc_update.clone(),
        tbl_invoice.clone(),
        DependencyKind::UpdatesTable,
    );
    graph.add_dependency(
        proc_insert.clone(),
        tbl_invoice.clone(),
        DependencyKind::InsertsTable,
    );
    graph.add_dependency(
        proc_delete.clone(),
        tbl_invoice.clone(),
        DependencyKind::DeletesTable,
    );
    graph.add_dependency(
        trn_invoice.clone(),
        tbl_invoice.clone(),
        DependencyKind::UpdatesTable,
    );
    graph.add_dependency(
        proc_other.clone(),
        tbl_customer.clone(),
        DependencyKind::ReadsTable,
    );

    let invoice_impact = graph.impact_of_table("Invoice");
    assert_eq!(invoice_impact.len(), 5);
    assert!(invoice_impact.contains(&proc_read));
    assert!(invoice_impact.contains(&proc_update));
    assert!(invoice_impact.contains(&proc_insert));
    assert!(invoice_impact.contains(&proc_delete));
    assert!(invoice_impact.contains(&trn_invoice));

    // Case-insensitivity
    let invoice_impact_lower = graph.impact_of_table("invoice");
    assert_eq!(invoice_impact_lower.len(), 5);

    let customer_impact = graph.impact_of_table("Customer");
    assert_eq!(customer_impact, vec![proc_other]);

    let empty_impact = graph.impact_of_table("UnknownTable");
    assert!(empty_impact.is_empty());
}

#[test]
fn test_mixed_dependency_kinds() {
    let mut graph = ImpactGraph::new();
    let master = ObjectRef::procedure("MasterProc");
    let sub = ObjectRef::procedure("SubProc");
    let sdt_cust = ObjectRef::sdt("CustomerSdt");
    let attr_id = ObjectRef::attribute("CustomerId");
    let attr_name = ObjectRef::attribute("CustomerName");
    let tbl_cust = ObjectRef::table("Customer");
    let tbl_audit = ObjectRef::table("AuditLog");
    let tbl_temp = ObjectRef::table("TempOrders");

    graph.add_dependency(master.clone(), sub.clone(), DependencyKind::CallsProcedure);
    graph.add_dependency(master.clone(), sdt_cust.clone(), DependencyKind::UsesSdt);
    graph.add_dependency(
        master.clone(),
        attr_id.clone(),
        DependencyKind::ReferencesAttribute,
    );
    graph.add_dependency(
        master.clone(),
        attr_name.clone(),
        DependencyKind::ReferencesAttribute,
    );
    graph.add_dependency(master.clone(), tbl_cust.clone(), DependencyKind::ReadsTable);
    graph.add_dependency(
        master.clone(),
        tbl_cust.clone(),
        DependencyKind::UpdatesTable,
    );
    graph.add_dependency(
        master.clone(),
        tbl_audit.clone(),
        DependencyKind::InsertsTable,
    );
    graph.add_dependency(
        master.clone(),
        tbl_temp.clone(),
        DependencyKind::DeletesTable,
    );

    let deps = graph.direct_dependencies(&master);
    assert_eq!(deps.len(), 8);

    assert_eq!(
        graph.impact_of_attribute("CustomerId"),
        vec![master.clone()]
    );
    assert_eq!(graph.impact_of_table("Customer"), vec![master.clone()]);
    assert_eq!(graph.impact_of_table("AuditLog"), vec![master.clone()]);
    assert_eq!(graph.impact_of_table("TempOrders"), vec![master.clone()]);

    let callers_sub = graph.direct_dependents(&sub);
    assert_eq!(callers_sub.len(), 1);
    assert_eq!(callers_sub[0].from, master);
}

#[test]
fn test_catalog_wide_indexing() {
    let mut catalog = Catalog::empty();

    // 1. Transaction Customer
    let mut cust_obj = KbObject::new(id("cust-trn"), "Customer", ObjectKind::Transaction);
    cust_obj.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Customer",
                "attributes": [
                    {"name": "CustomerId", "is_key": true, "data_type": {"kind": "numeric", "precision": 6, "scale": 0}},
                    {"name": "CustomerName", "data_type": {"kind": "varchar", "length": 50}}
                ]
            }
        }"#.into(),
    });

    // 2. Transaction Invoice
    let mut inv_obj = KbObject::new(id("inv-trn"), "Invoice", ObjectKind::Transaction);
    inv_obj.source_parts.push(SourcePart {
        name: "STRUCTURE".into(),
        source: r#"{
            "root": {
                "name": "Invoice",
                "attributes": [
                    {"name": "InvoiceId", "is_key": true, "data_type": {"kind": "numeric", "precision": 6, "scale": 0}},
                    {"name": "CustomerId", "data_type": {"kind": "numeric", "precision": 6, "scale": 0}, "is_foreign_key": true}
                ]
            }
        }"#.into(),
    });
    inv_obj.references.push(Reference {
        target: id("cust-trn"),
        kind: ReferenceKind::Uses,
    });

    // 3. Procedure GetCustomer
    let mut get_cust = KbObject::new(id("get-cust"), "GetCustomer", ObjectKind::Procedure);
    get_cust.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "For each CustomerId\n    &name = CustomerName\nEndfor\n".into(),
    });

    // 4. Procedure CreateInvoice
    let mut create_inv = KbObject::new(id("create-inv"), "CreateInvoice", ObjectKind::Procedure);
    create_inv.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "GetCustomer()\nNew\n    InvoiceId = 1\n    CustomerId = 10\nEndnew\n".into(),
    });

    // 5. Procedure DeleteOldInvoices
    let mut del_inv = KbObject::new(id("del-inv"), "DeleteOldInvoices", ObjectKind::Procedure);
    del_inv.source_parts.push(SourcePart {
        name: "Source".into(),
        source: "For each InvoiceId\n    Delete\nEndfor\n".into(),
    });

    catalog
        .apply(
            0,
            &[
                Edit::Upsert(cust_obj),
                Edit::Upsert(inv_obj),
                Edit::Upsert(get_cust),
                Edit::Upsert(create_inv),
                Edit::Upsert(del_inv),
            ],
        )
        .unwrap();

    let mut graph = ImpactGraph::new();
    graph.index_catalog(&catalog);

    // Assert Procedure calls: CreateInvoice -> GetCustomer
    let create_inv_ref = ObjectRef::procedure("CreateInvoice");
    let get_cust_ref = ObjectRef::procedure("GetCustomer");
    let callers_get_cust = graph.direct_dependents(&get_cust_ref);
    assert!(
        callers_get_cust
            .iter()
            .any(|e| e.from == create_inv_ref && e.kind == DependencyKind::CallsProcedure)
    );

    // Assert Attribute impact: CustomerName referenced in Customer Transaction and GetCustomer Procedure
    let cust_name_impact = graph.impact_of_attribute("CustomerName");
    assert!(cust_name_impact.contains(&ObjectRef::transaction("Customer")));
    assert!(cust_name_impact.contains(&get_cust_ref));

    // Assert Table impact: Invoice table read/updated/inserted/deleted
    let invoice_impact = graph.impact_of_table("Invoice");
    assert!(invoice_impact.contains(&ObjectRef::transaction("Invoice")));
    assert!(invoice_impact.contains(&create_inv_ref));
    assert!(invoice_impact.contains(&ObjectRef::procedure("DeleteOldInvoices")));
}

#[test]
fn test_blast_radius_and_risk_assessment() {
    let mut graph = ImpactGraph::new();

    let target_low = ObjectRef::procedure("ProcLow");
    let report_low = graph.blast_radius(&target_low);
    assert_eq!(report_low.direct_callers, 0);
    assert_eq!(report_low.transitive_callers, 0);
    assert_eq!(report_low.risk_level, RiskLevel::Low);

    // Low with 2 callers (<= 2)
    let target_low2 = ObjectRef::procedure("ProcLow2");
    let caller1 = ObjectRef::procedure("Caller1");
    let caller2 = ObjectRef::procedure("Caller2");
    graph.add_dependency(
        caller1.clone(),
        target_low2.clone(),
        DependencyKind::CallsProcedure,
    );
    graph.add_dependency(
        caller2.clone(),
        target_low2.clone(),
        DependencyKind::CallsProcedure,
    );
    let report_low2 = graph.blast_radius(&target_low2);
    assert_eq!(report_low2.direct_callers, 2);
    assert_eq!(report_low2.risk_level, RiskLevel::Low);

    // Medium: 3 to 5 callers (> 2)
    let target_med = ObjectRef::procedure("ProcMed");
    let caller3 = ObjectRef::procedure("Caller3");
    graph.add_dependency(
        caller1.clone(),
        target_med.clone(),
        DependencyKind::CallsProcedure,
    );
    graph.add_dependency(
        caller2.clone(),
        target_med.clone(),
        DependencyKind::CallsProcedure,
    );
    graph.add_dependency(
        caller3.clone(),
        target_med.clone(),
        DependencyKind::CallsProcedure,
    );
    let report_med = graph.blast_radius(&target_med);
    assert_eq!(report_med.direct_callers, 3);
    assert_eq!(report_med.risk_level, RiskLevel::Medium);

    // High: 6 callers (> 5)
    let target_high = ObjectRef::procedure("ProcHigh");
    for i in 1..=6 {
        let c = ObjectRef::procedure(format!("CallerH{i}"));
        graph.add_dependency(c, target_high.clone(), DependencyKind::CallsProcedure);
    }
    let report_high = graph.blast_radius(&target_high);
    assert_eq!(report_high.direct_callers, 6);
    assert_eq!(report_high.risk_level, RiskLevel::High);

    // Critical: > 10 callers
    let target_crit = ObjectRef::procedure("ProcCrit");
    for i in 1..=12 {
        let c = ObjectRef::procedure(format!("CallerC{i}"));
        graph.add_dependency(c, target_crit.clone(), DependencyKind::CallsProcedure);
    }
    let report_crit = graph.blast_radius(&target_crit);
    assert_eq!(report_crit.direct_callers, 12);
    assert_eq!(report_crit.risk_level, RiskLevel::Critical);

    // Critical: core table affected
    let target_core = ObjectRef::procedure("ProcSecurityAudit");
    let core_tbl = ObjectRef::table("CORE_SECURITY");
    graph.add_dependency(target_core.clone(), core_tbl, DependencyKind::ReadsTable);
    let report_core = graph.blast_radius(&target_core);
    assert_eq!(report_core.direct_callers, 0);
    assert_eq!(report_core.risk_level, RiskLevel::Critical);
    assert!(
        report_core
            .affected_tables
            .contains(&"CORE_SECURITY".to_string())
    );
}

#[test]
fn test_max_depth_enforcement() {
    let mut graph = ImpactGraph::new();

    // Create a chain of 25 procedures: P0 -> P1 -> P2 -> ... -> P24
    let procs: Vec<ObjectRef> = (0..25)
        .map(|i| ObjectRef::procedure(format!("P{i}")))
        .collect();

    for i in 0..24 {
        graph.add_dependency(
            procs[i].clone(),
            procs[i + 1].clone(),
            DependencyKind::CallsProcedure,
        );
    }

    // Depth capped at 16 even when passing 25
    let trans_deps = graph.transitive_dependencies(&procs[0], 25);
    assert_eq!(trans_deps.len(), MAX_TRANSITIVE_DEPTH);
    assert_eq!(trans_deps, procs[1..=16].to_vec());

    let trans_callers = graph.transitive_dependents(&procs[24], 25);
    assert_eq!(trans_callers.len(), MAX_TRANSITIVE_DEPTH);
    // Depth 1: P23, Depth 2: P22, ..., Depth 16: P8
    let expected_callers: Vec<ObjectRef> = (8..=23).rev().map(|i| procs[i].clone()).collect();
    assert_eq!(trans_callers, expected_callers);

    // Custom smaller max_depth
    let deps_depth_3 = graph.transitive_dependencies(&procs[0], 3);
    assert_eq!(deps_depth_3.len(), 3);
    assert_eq!(deps_depth_3, procs[1..=3].to_vec());

    // Zero depth
    let deps_depth_0 = graph.transitive_dependencies(&procs[0], 0);
    assert!(deps_depth_0.is_empty());
}

#[test]
fn test_impact_graph_json_serialization() {
    let mut graph = ImpactGraph::new();
    let pa = ObjectRef::procedure("ProcA");
    let pb = ObjectRef::procedure("ProcB");
    let tbl = ObjectRef::table("Invoice");
    let attr = ObjectRef::attribute("InvoiceTotal");
    let sdt = ObjectRef::sdt("InvoiceSdt");

    graph.add_dependency(pa.clone(), pb.clone(), DependencyKind::CallsProcedure);
    graph.add_dependency(pa.clone(), tbl.clone(), DependencyKind::ReadsTable);
    graph.add_dependency(pa.clone(), tbl.clone(), DependencyKind::UpdatesTable);
    graph.add_dependency(
        pa.clone(),
        attr.clone(),
        DependencyKind::ReferencesAttribute,
    );
    graph.add_dependency(pb.clone(), sdt.clone(), DependencyKind::UsesSdt);

    // 1. Serialize and deserialize ImpactGraph
    let json = serde_json::to_string(&graph).expect("serialize graph");
    let deserialized: ImpactGraph = serde_json::from_str(&json).expect("deserialize graph");
    assert_eq!(graph, deserialized);

    // 2. Serialize and deserialize BlastRadiusReport
    let report = graph.blast_radius(&pa);
    let report_json = serde_json::to_string(&report).expect("serialize report");
    let deserialized_report: BlastRadiusReport =
        serde_json::from_str(&report_json).expect("deserialize report");
    assert_eq!(report, deserialized_report);

    // 3. Serialize and deserialize RiskLevel enum variants
    for risk in [
        RiskLevel::Low,
        RiskLevel::Medium,
        RiskLevel::High,
        RiskLevel::Critical,
    ] {
        let r_json = serde_json::to_string(&risk).unwrap();
        let r_deser: RiskLevel = serde_json::from_str(&r_json).unwrap();
        assert_eq!(risk, r_deser);
    }

    // 4. Serialize and deserialize DependencyEdge directly
    let edge = DependencyEdge::new(pa, pb, DependencyKind::CallsProcedure);
    let edge_json = serde_json::to_string(&edge).unwrap();
    let edge_deser: DependencyEdge = serde_json::from_str(&edge_json).unwrap();
    assert_eq!(edge, edge_deser);
}
