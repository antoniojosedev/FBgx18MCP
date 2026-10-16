use gx_engine::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    kb,
};

fn object(id: &str, name: &str, source: &str, signature: Option<&str>) -> KbObject {
    let mut value = KbObject::new(ObjectId::new(id).unwrap(), name, ObjectKind::Procedure);
    value.source_parts.push(SourcePart {
        name: "Source".into(),
        source: source.into(),
    });
    if let Some(signature) = signature {
        value.source_parts.push(SourcePart {
            name: "Signature".into(),
            source: signature.into(),
        });
    }
    value
}

fn record_catalog(fields: &str) -> Catalog {
    let mut catalog = Catalog::empty();
    let mut main = object(
        "main",
        "Main",
        "&First = new()\n&First.Amount = 9\n&Second = &First\n&Second.Amount = 11\nPrint(&First.Amount)",
        None,
    );
    main.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{"First":{"kind":"record","schema":"Probe"},"Second":{"kind":"record","schema":"Probe"}}"#.into(),
    });
    main.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new("probe").unwrap(),
    });
    let mut schema = KbObject::new(
        ObjectId::new("probe").unwrap(),
        "Probe",
        ObjectKind::StructuredDataType,
    );
    schema.source_parts.push(SourcePart {
        name: "Fields".into(),
        source: fields.into(),
    });
    catalog
        .apply(0, &[Edit::Upsert(main), Edit::Upsert(schema)])
        .unwrap();
    catalog
}

#[test]
fn portable_catalog_loads_explicitly_used_numeric_record_schemas() {
    let catalog = record_catalog(r#"{"Amount":{"kind":"numeric","precision":4,"scale":0}}"#);
    let original = catalog.to_json().unwrap();
    let project =
        kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap();
    assert_eq!(project.procedures.len(), 1);
    assert_eq!(project.record_types.len(), 1);
    assert_eq!(project.record_types[0].name, "PROBE");
    assert!(gx_engine::compile(&project).is_ok());
    assert_eq!(catalog.to_json().unwrap(), original);
}

#[test]
fn portable_catalog_rejects_invalid_record_fields_and_missing_dependencies() {
    for fields in [
        r#"{"Amount":{"kind":"numeric","precision":4,"scale":0},"Amount":{"kind":"numeric","precision":4,"scale":0}}"#,
        r#"{"Amount":{"kind":"record","schema":"Probe"}}"#,
        r#"{"Amount":{"kind":"numeric","precision":4,"scale":0}}},{"name":"Injected","fields":{}"#,
        r#"{}"#,
    ] {
        let catalog = record_catalog(fields);
        assert!(kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).is_err());
    }
    let mut catalog = record_catalog(r#"{"Amount":{"kind":"numeric","precision":4,"scale":0}}"#);
    let mut main = catalog
        .get(&ObjectId::new("main").unwrap())
        .unwrap()
        .clone();
    main.references.clear();
    catalog.apply(1, &[Edit::Upsert(main)]).unwrap();
    let error =
        kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap_err();
    assert!(error.contains("Uses"), "{error}");
}

#[test]
fn portable_catalog_requires_direct_calls_and_preserves_recursive_edges() {
    let mut main = object(
        "main",
        "Main",
        "A()\nPrint(STR(12))\nPrint(TRIM(\" x \"))\nPrint(LEN(\"hi\"))\nPrint(YMDTOD(20250101))\nPrint(YMDHMSTOT(20250101010203))",
        None,
    );
    main.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("a").unwrap(),
    });
    let mut a = object("a", "A", "B()", None);
    a.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("b").unwrap(),
    });
    let b = object("b", "B", "Print(1)", None);
    let mut catalog = Catalog::empty();
    catalog
        .apply(0, &[Edit::Upsert(main), Edit::Upsert(a), Edit::Upsert(b)])
        .unwrap();
    let original = catalog.to_json().unwrap();

    let direct = kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap();
    assert_eq!(direct.procedures.len(), 3);
    assert!(direct.parse().is_ok());
    assert_eq!(catalog.to_json().unwrap(), original);

    // B is reachable through Main -> A -> B, but Main's source call still needs
    // its own direct Calls reference.
    let mut transitive_only = catalog.clone();
    let mut main = transitive_only
        .get(&ObjectId::new("main").unwrap())
        .unwrap()
        .clone();
    main.source_parts
        .iter_mut()
        .find(|part| part.name == "SOURCE")
        .unwrap()
        .source = "B()".into();
    transitive_only.apply(1, &[Edit::Upsert(main)]).unwrap();
    let unchanged = transitive_only.to_json().unwrap();
    let error = kb::project_from_catalog(&transitive_only, &ObjectId::new("main").unwrap(), None)
        .unwrap_err();
    assert!(error.contains("direct Calls"), "{error}");
    assert_eq!(transitive_only.to_json().unwrap(), unchanged);

    // A recursive graph remains valid when every source call has its own edge.
    let mut recursive = catalog.clone();
    let mut b = recursive.get(&ObjectId::new("b").unwrap()).unwrap().clone();
    b.source_parts
        .iter_mut()
        .find(|part| part.name == "SOURCE")
        .unwrap()
        .source = "A()".into();
    b.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("a").unwrap(),
    });
    recursive.apply(1, &[Edit::Upsert(b)]).unwrap();
    let unchanged = recursive.to_json().unwrap();
    let project =
        kb::project_from_catalog(&recursive, &ObjectId::new("main").unwrap(), None).unwrap();
    assert_eq!(project.procedures.len(), 3);
    assert_eq!(recursive.to_json().unwrap(), unchanged);
}

#[test]
fn portable_catalog_requires_explicit_uses_for_record_return_types() {
    let mut main = object(
        "main",
        "Main",
        "Return 1",
        Some(r#"{"params":[],"return_type":{"kind":"record","schema":"Result"}}"#),
    );
    main.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new("result").unwrap(),
    });
    let mut result = KbObject::new(
        ObjectId::new("result").unwrap(),
        "Result",
        ObjectKind::StructuredDataType,
    );
    result.source_parts.push(SourcePart {
        name: "Fields".into(),
        source: r#"{"Amount":{"kind":"numeric","precision":4,"scale":0}}"#.into(),
    });
    let mut catalog = Catalog::empty();
    catalog
        .apply(0, &[Edit::Upsert(main.clone()), Edit::Upsert(result)])
        .unwrap();
    let project =
        kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap();
    assert_eq!(project.record_types.len(), 1);
    assert!(project.procedures[0].return_type.is_some());

    main.references.clear();
    catalog.apply(1, &[Edit::Upsert(main)]).unwrap();
    let error =
        kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap_err();
    assert!(error.contains("Uses"), "{error}");
}

#[test]
fn portable_catalog_projects_nested_records_and_collections_with_explicit_uses() {
    let mut catalog = record_catalog(
        r#"{"Child":{"kind":"record","schema":"Child"},"Rows":{"kind":"collection","schema":"Row"}}"#,
    );
    let mut root = catalog
        .get(&ObjectId::new("probe").unwrap())
        .unwrap()
        .clone();
    let mut edits = Vec::new();
    for (name, fields) in [
        (
            "Child",
            r#"{"Amount":{"kind":"numeric","precision":4,"scale":0},"Nested":{"kind":"record","schema":"Grandchild"}}"#,
        ),
        (
            "Row",
            r#"{"Amount":{"kind":"numeric","precision":4,"scale":0}}"#,
        ),
    ] {
        let id = ObjectId::new(name).unwrap();
        root.references.push(Reference {
            kind: ReferenceKind::Uses,
            target: id.clone(),
        });
        let mut schema = KbObject::new(id, name, ObjectKind::StructuredDataType);
        if name == "Child" {
            schema.references.push(Reference {
                kind: ReferenceKind::Uses,
                target: ObjectId::new("grandchild").unwrap(),
            });
        }
        schema.source_parts.push(SourcePart {
            name: "Fields".into(),
            source: fields.into(),
        });
        edits.push(Edit::Upsert(schema));
    }
    let mut grandchild = KbObject::new(
        ObjectId::new("grandchild").unwrap(),
        "Grandchild",
        ObjectKind::StructuredDataType,
    );
    grandchild.source_parts.push(SourcePart {
        name: "Fields".into(),
        source: r#"{"Amount":{"kind":"numeric","precision":4,"scale":0}}"#.into(),
    });
    edits.push(Edit::Upsert(grandchild));
    let mut main = catalog
        .get(&ObjectId::new("main").unwrap())
        .unwrap()
        .clone();
    main.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new("row").unwrap(),
    });
    main.source_parts
        .iter_mut()
        .find(|p| p.name == "VARIABLES")
        .unwrap()
        .source =
        r#"{"First":{"kind":"record","schema":"Probe"},"Row":{"kind":"record","schema":"Row"}}"#
            .into();
    main.source_parts
        .iter_mut()
        .find(|part| part.name == "SOURCE")
        .unwrap()
        .source = "&First = new()\n&First.Child.Amount = 7\n&First.Child.Nested.Amount = 8\n&Row = new()\n&Row.Amount = 9\n&First.Rows.Add(&Row)\nPrint(&First.Child.Amount)\nPrint(&First.Child.Nested.Amount)\nPrint(&First.Rows.Item(1).Amount)".into();
    edits.extend([Edit::Upsert(root.clone()), Edit::Upsert(main)]);
    catalog.apply(1, &edits).unwrap();
    let original = catalog.to_json().unwrap();
    let project =
        kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap();
    assert_eq!(project.record_types.len(), 4);
    assert!(gx_engine::compile(&project).is_ok());
    assert_eq!(catalog.to_json().unwrap(), original);

    // Exercise catalog projection through the actual check/build/run CLI path.
    use std::{fs, process::Command};
    struct Folder(std::path::PathBuf);
    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    let folder = Folder(std::env::temp_dir().join(format!("gx-kb-nested-{}", std::process::id())));
    fs::create_dir(&folder.0).unwrap();
    let input = folder.0.join("catalog.json");
    catalog.save_atomic(&input).unwrap();
    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("kb-run")
        .arg(&input)
        .arg("main")
        .arg(folder.0.join("output"))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "7\n8\n9\n"
    );
    assert_eq!(Catalog::load(&input).unwrap(), catalog);

    // Every nested field schema needs an explicit Uses dependency from its parent SDT.
    let mut missing_nested_use = catalog.clone();
    let mut child = missing_nested_use
        .get(&ObjectId::new("child").unwrap())
        .unwrap()
        .clone();
    child.references.clear();
    missing_nested_use.apply(2, &[Edit::Upsert(child)]).unwrap();
    let error =
        kb::project_from_catalog(&missing_nested_use, &ObjectId::new("main").unwrap(), None)
            .unwrap_err();
    assert!(error.contains("Uses"), "{error}");

    root.references
        .retain(|reference| reference.target.as_str() != "child");
    catalog.apply(2, &[Edit::Upsert(root)]).unwrap();
    let error =
        kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap_err();
    assert!(error.contains("Uses"), "{error}");
}

#[test]
fn portable_catalog_rejects_unknown_sdt_field_references() {
    let unknown = record_catalog(r#"{"Child":{"kind":"record","schema":"Missing"}}"#);
    let error =
        kb::project_from_catalog(&unknown, &ObjectId::new("main").unwrap(), None).unwrap_err();
    assert!(error.contains("Uses"), "{error}");
}

#[test]
fn portable_catalog_rejects_recursive_sdt_field_references() {
    let mut catalog = record_catalog(r#"{"Child":{"kind":"record","schema":"Child"}}"#);
    let mut root = catalog
        .get(&ObjectId::new("probe").unwrap())
        .unwrap()
        .clone();
    root.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new("child").unwrap(),
    });
    let mut child = KbObject::new(
        ObjectId::new("child").unwrap(),
        "Child",
        ObjectKind::StructuredDataType,
    );
    child.source_parts.push(SourcePart {
        name: "Fields".into(),
        source: r#"{"Parent":{"kind":"record","schema":"Probe"}}"#.into(),
    });
    child.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new("probe").unwrap(),
    });
    catalog
        .apply(1, &[Edit::Upsert(root), Edit::Upsert(child)])
        .unwrap();
    let error =
        kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap_err();
    assert!(error.contains("recursive"), "{error}");
}

#[test]
fn portable_catalog_enforces_the_record_schema_count_limit() {
    let mut main = object("main", "Main", "Print(1)", None);
    main.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: ObjectId::new("schema-0").unwrap(),
    });
    let mut edits = vec![Edit::Upsert(main)];
    for index in 0..129 {
        let name = format!("Schema{index}");
        let mut schema = KbObject::new(
            ObjectId::new(format!("schema-{index}")).unwrap(),
            &name,
            ObjectKind::StructuredDataType,
        );
        let fields = if index < 128 {
            schema.references.push(Reference {
                kind: ReferenceKind::Uses,
                target: ObjectId::new(format!("schema-{}", index + 1)).unwrap(),
            });
            format!(
                r#"{{"Next":{{"kind":"record","schema":"Schema{}"}}}}"#,
                index + 1
            )
        } else {
            r#"{"Value":{"kind":"numeric","precision":4,"scale":0}}"#.into()
        };
        schema.source_parts.push(SourcePart {
            name: "Fields".into(),
            source: fields,
        });
        edits.push(Edit::Upsert(schema));
    }
    let mut catalog = Catalog::empty();
    catalog.apply(0, &edits).unwrap();
    let error =
        kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap_err();
    assert!(error.contains("128 record schemas"), "{error}");
}

#[test]
fn portable_catalog_enforces_the_procedure_count_limit() {
    let mut edits = Vec::new();
    for index in 0..1025 {
        let id = format!("procedure-{index}");
        let mut procedure = object(&id, &format!("Procedure{index}"), "Print(1)", None);
        if index < 1023 {
            procedure.references.push(Reference {
                kind: ReferenceKind::Calls,
                target: ObjectId::new(format!("procedure-{}", index + 1)).unwrap(),
            });
        }
        edits.push(Edit::Upsert(procedure));
    }
    let mut catalog = Catalog::empty();
    catalog.apply(0, &edits).unwrap();
    let entry = ObjectId::new("procedure-0").unwrap();
    let project = kb::project_from_catalog(&catalog, &entry, None).unwrap();
    assert_eq!(project.procedures.len(), 1024);

    let mut last = catalog
        .get(&ObjectId::new("procedure-1023").unwrap())
        .unwrap()
        .clone();
    last.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("procedure-1024").unwrap(),
    });
    catalog.apply(1, &[Edit::Upsert(last)]).unwrap();
    let error = kb::project_from_catalog(&catalog, &entry, None).unwrap_err();
    assert_eq!(error, "catalog compile exceeds 1024 procedures");
}

#[test]
fn portable_catalog_produces_a_compilable_project_with_stable_entry_identity() {
    let mut catalog = Catalog::empty();
    let mut main = object("entry-id", "Main", "Print(Double(21))\nReturn 0", None);
    main.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("helper-id").unwrap(),
    });
    let mut helper = object(
        "helper-id",
        "Double",
        "Return &Value * 2",
        Some(r#"{"params":["Value"],"return_type":{"kind":"numeric","precision":10,"scale":0}}"#),
    );
    helper.source_parts.push(SourcePart {
        name: "Variables".into(),
        source: r#"{"Value":{"kind":"numeric","precision":10,"scale":0}}"#.into(),
    });
    catalog
        .apply(0, &[Edit::Upsert(main), Edit::Upsert(helper)])
        .unwrap();
    let project =
        kb::project_from_catalog(&catalog, &ObjectId::new("entry-id").unwrap(), None).unwrap();
    assert_eq!(project.entry, "MAIN");
    assert_eq!(project.procedures.len(), 2);
    assert!(gx_engine::compile(&project).is_ok());
    catalog
        .apply(
            1,
            &[Edit::Rename {
                id: ObjectId::new("entry-id").unwrap(),
                name: "Renamed".into(),
            }],
        )
        .unwrap();
    assert_eq!(
        kb::project_from_catalog(&catalog, &ObjectId::new("entry-id").unwrap(), None)
            .unwrap()
            .entry,
        "RENAMED"
    );
}

#[test]
fn portable_rules_preserve_native_parm_modes_without_rewriting_catalog_parts() {
    let mut catalog = Catalog::empty();
    let mut main = object("main", "Main", "Print(Double(21))", None);
    main.references.push(Reference {
        kind: ReferenceKind::Calls,
        target: ObjectId::new("double").unwrap(),
    });
    let mut helper = object("double", "Double", "&Result = &Value * 2", None);
    helper.source_parts.extend([
        SourcePart { name: "Rules".into(), source: "Parm(in:&Value, out:&Result);".into() },
        SourcePart { name: "Variables".into(), source: r#"{"Value":{"kind":"numeric","precision":10,"scale":0},"Result":{"kind":"numeric","precision":10,"scale":0}}"#.into() },
    ]);
    catalog
        .apply(0, &[Edit::Upsert(main), Edit::Upsert(helper)])
        .unwrap();
    let original = catalog.to_json().unwrap();
    let project =
        kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap();
    assert_eq!(project.procedures[1].params, ["IN:VALUE", "OUT:RESULT"]);
    assert!(project.procedures[1].return_type.is_none());
    assert!(gx_engine::compile(&project).is_ok());
    assert_eq!(catalog.to_json().unwrap(), original);

    let mut helper = catalog
        .get(&ObjectId::new("double").unwrap())
        .unwrap()
        .clone();
    helper.source_parts.push(SourcePart {
        name: "Signature".into(),
        source: r#"{"params":["Value"]}"#.into(),
    });
    catalog.apply(1, &[Edit::Upsert(helper)]).unwrap();
    let error =
        kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).unwrap_err();
    assert!(error.contains("both Rules and Signature"), "{error}");
}

#[test]
fn catalog_compile_fails_closed_on_unsupported_or_ambiguous_source_parts() {
    for parts in [
        vec![],
        vec![
            SourcePart {
                name: "Source".into(),
                source: "Print(1)".into(),
            },
            SourcePart {
                name: "Variables".into(),
                source: r#"{"A":{"kind":"boolean"},"A":{"kind":"date"}}"#.into(),
            },
        ],
        vec![
            SourcePart {
                name: "Source".into(),
                source: "Print(1)".into(),
            },
            SourcePart {
                name: "Rules".into(),
                source: "anything".into(),
            },
        ],
    ] {
        let mut item = KbObject::new(
            ObjectId::new("main").unwrap(),
            "Main",
            ObjectKind::Procedure,
        );
        item.source_parts = parts;
        let mut catalog = Catalog::empty();
        catalog.apply(0, &[Edit::Upsert(item)]).unwrap();
        assert!(kb::project_from_catalog(&catalog, &ObjectId::new("main").unwrap(), None).is_err());
    }
}

#[test]
fn catalog_cli_loads_saved_objects_builds_and_runs_without_sdk() {
    use std::{fs, process::Command};
    struct Folder(std::path::PathBuf);
    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    let folder =
        Folder(std::env::temp_dir().join(format!("gx-kb-pipeline-{}", std::process::id())));
    fs::create_dir(&folder.0).unwrap();
    let catalog = Catalog::from_json(include_str!("../fixtures/catalog-demo.json")).unwrap();
    let input = folder.0.join("catalog.json");
    catalog.save_atomic(&input).unwrap();
    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("kb-check")
        .arg(&input)
        .arg("main-id")
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(!folder.0.join(".gx-engine").exists());
    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("kb-run")
        .arg(&input)
        .arg("main-id")
        .arg(folder.0.join("output"))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "42\n"
    );
    let parm_catalog =
        Catalog::from_json(include_str!("../fixtures/catalog-parm-demo.json")).unwrap();
    parm_catalog.save_atomic(&input).unwrap();
    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("kb-run")
        .arg(&input)
        .arg("main-id")
        .arg(folder.0.join("parm-output"))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "42\n"
    );
    assert_eq!(Catalog::load(&input).unwrap(), parm_catalog);
    let records = record_catalog(r#"{"Amount":{"kind":"numeric","precision":4,"scale":0}}"#);
    records.save_atomic(&input).unwrap();
    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("kb-run")
        .arg(&input)
        .arg("main")
        .arg(folder.0.join("record-output"))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).replace("\r\n", "\n"),
        "11\n"
    );
    assert_eq!(Catalog::load(&input).unwrap(), records);
    for args in [
        vec!["kb-check"],
        vec!["kb-build", "missing"],
        vec!["kb-explain", "missing", "main-id"],
    ] {
        assert!(
            !Command::new(env!("CARGO_BIN_EXE_gx-engine"))
                .args(args)
                .output()
                .unwrap()
                .status
                .success()
        );
    }
    let absent = folder.0.join("absent-output");
    let output = Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("kb-build")
        .arg(&input)
        .arg("missing-id")
        .arg(&absent)
        .output()
        .unwrap();
    assert!(!output.status.success());
    assert!(!absent.exists());
}
