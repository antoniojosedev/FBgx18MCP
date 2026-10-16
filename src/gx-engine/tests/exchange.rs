use gx_engine::{
    catalog::{ObjectId, ObjectKind, ReferenceKind},
    exchange,
    model::DataType,
};

// Hand-authored minimal exchange syntax, not a checked-in SDK export or KB artifact.
fn xml() -> String {
    r#"<ExportFile><Objects>
<Object guid="main-id" name="Main" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
<Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff"><Source><![CDATA[&First = new()
&First.Label = "sdk-free"
Msg(&First.Label, status)]]></Source></Part>
<Part type="9b0a32a3-de6d-4be1-a4dd-1b85d3741534"><Properties/></Part>
<Part type="e4c4ade7-53f0-4a56-bdfd-843735b66f47"><Variable Name="First"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Probe</Value></Property><Property><Name>DataTypeString</Name><Value>Probe</Value></Property></Properties></Variable></Part>
</Object>
<Object guid="probe-id" name="Probe" type="447527b5-9210-4523-898b-5dccb17be60a">
<Part type="5c2aa9da-8fc4-4b6b-ae02-8db4fa48976a"><Level Name="Probe">
<Item name="Label"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:VarChar</Value></Property><Property><Name>Length</Name><Value>12</Value></Property></Properties></Item>
</Level></Part></Object></Objects></ExportFile>"#.into()
}

// Synthetic, redacted structural equivalent of the SDK export's root + Child/Rows shape.
// Keep only test identities and content here; never check in the raw SDK export.
fn nested_xml(collection_item_name: Option<&str>) -> String {
    let collection_properties = match collection_item_name {
        Some(name) => format!(
            "<Properties><Property><Name>Name</Name><Value>Rows</Value></Property><Property><Name>AttCollection</Name><Value>True</Value></Property><Property><Name>idCollectionItemName</Name><Value>{name}</Value></Property></Properties>"
        ),
        None => "<Properties><Property><Name>Name</Name><Value>Rows</Value></Property><Property><Name>AttCollection</Name><Value>True</Value></Property></Properties>".into(),
    };
    let row_alias = collection_item_name.unwrap_or("RowsItem");
    format!(
        r#"<ExportFile><Objects>
<Object guid="main-id" name="Main" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
<Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff"><Source>Msg("nested", status)</Source></Part>
<Part type="e4c4ade7-53f0-4a56-bdfd-843735b66f47">
<Variable Name="First"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Nested</Value></Property></Properties></Variable>
<Variable Name="Second"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Nested</Value></Property></Properties></Variable>
<Variable Name="Child"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Nested.Child</Value></Property></Properties></Variable>
<Variable Name="Row"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Nested.{row_alias}</Value></Property></Properties></Variable>
<Variable Name="Index"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property><Property><Name>Length</Name><Value>2</Value></Property><Property><Name>Decimals</Name><Value>0</Value></Property></Properties></Variable>
</Part><Properties><Property><Name>CALL_PROTOCOL</Name><Value>CLINE</Value></Property></Properties></Object>
<Object guid="root-id" name="Nested" type="447527b5-9210-4523-898b-5dccb17be60a">
<Part type="5c2aa9da-8fc4-4b6b-ae02-8db4fa48976a"><Level Name="Nested">
<LevelInfo guid="11111111-1111-4111-8111-111111111111" name="Nested"><Properties><Property><Name>Name</Name><Value>Nested</Value></Property></Properties></LevelInfo>
<Item name="Amount"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property><Property><Name>Length</Name><Value>4</Value></Property><Property><Name>Decimals</Name><Value>0</Value></Property></Properties></Item>
<Item name="Label"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:VarChar</Value></Property><Property><Name>Length</Name><Value>12</Value></Property></Properties></Item>
<Item name="Fixed"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Character</Value></Property><Property><Name>Length</Name><Value>6</Value></Property></Properties></Item>
<Level Name="Child"><LevelInfo guid="22222222-2222-4222-8222-222222222222" name="Child"><Properties><Property><Name>Name</Name><Value>Child</Value></Property><Property><Name>AttCollection</Name><Value>False</Value></Property></Properties></LevelInfo>
<Item name="Amount"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property><Property><Name>Length</Name><Value>4</Value></Property><Property><Name>Decimals</Name><Value>0</Value></Property></Properties></Item></Level>
<Level Name="Rows"><LevelInfo guid="33333333-3333-4333-8333-333333333333" name="Rows">{collection_properties}</LevelInfo>
<Item name="Amount"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property><Property><Name>Length</Name><Value>4</Value></Property><Property><Name>Decimals</Name><Value>0</Value></Property></Properties></Item></Level>
</Level></Part></Object></Objects></ExportFile>"#
    )
}

fn with_main_source(xml: &str, source: &str) -> String {
    xml.replace(
        "<Source>Msg(\"nested\", status)</Source>",
        &format!("<Source><![CDATA[{source}]]></Source>"),
    )
}

fn unique_temp_dir(prefix: &str) -> std::path::PathBuf {
    use std::sync::atomic::{AtomicU64, Ordering};
    static SEQUENCE: AtomicU64 = AtomicU64::new(0);
    let directory = std::env::temp_dir().join(format!(
        "{prefix}-{}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos(),
        SEQUENCE.fetch_add(1, Ordering::Relaxed)
    ));
    std::fs::create_dir(&directory).unwrap();
    directory
}

fn run_xml(input_xml: &str, entry: &str) -> std::process::Output {
    let directory = unique_temp_dir("gx-xml-run");
    let input = directory.join("authored.xml");
    std::fs::write(&input, input_xml).unwrap();
    let output = std::process::Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("xml-run")
        .arg(&input)
        .arg(entry)
        .arg(directory.join("output"))
        .output()
        .unwrap();
    assert_eq!(std::fs::read_to_string(&input).unwrap(), input_xml);
    std::fs::remove_dir_all(&directory).unwrap();
    output
}

fn normalize_crlf(value: &str) -> String {
    value.replace("\r\n", "\n")
}

#[test]
fn authored_exchange_projects_keep_sdt_identity_and_compile_without_sdk() {
    let catalog = exchange::catalog_from_xml(&xml()).unwrap();
    assert_eq!(catalog.objects().count(), 2);
    assert_eq!(
        catalog
            .objects()
            .filter(|o| o.kind == ObjectKind::StructuredDataType)
            .count(),
        1
    );
    let project = exchange::project_from_xml(&xml(), "main", None).unwrap();
    assert_eq!(project.record_types.len(), 1);
    assert_eq!(project.procedures.len(), 1);
    assert!(gx_engine::compile(&project).is_ok());
}

#[test]
fn exchange_cli_builds_authored_xml_without_sdk_or_input_mutation() {
    let output = run_xml(&xml(), "Main");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    assert_eq!(normalize_crlf(&stdout), "sdk-free\n");
}

#[test]
fn exchange_rejects_unmodeled_call_protocol_instead_of_making_it_console() {
    let with_protocol = |protocol| {
        xml().replacen("</Object>", &format!("<Properties><Property><Name>CALL_PROTOCOL</Name><Value>{protocol}</Value></Property></Properties></Object>"), 1)
    };
    assert!(exchange::catalog_from_xml(&with_protocol("CLINE")).is_ok());
    for protocol in ["HTTP", "SOAP", "invalid", ""] {
        assert!(
            exchange::catalog_from_xml(&with_protocol(protocol)).is_err(),
            "admitted {protocol}"
        );
    }
}

#[test]
fn exchange_tracks_calls_with_the_shared_ast_dependency_visitor() {
    let input = xml().replace(
        "Msg(&First.Label, status)",
        "Msg(&First.Label, status)\nIf Len(&First.Label) > 0\nHelper.Call()\nEndIf",
    );
    let helper = r#"<Object guid="helper-id" name="Helper" type="84a12160-f59b-4ad7-a683-ea4481ac23e9"><Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff"><Source>Msg("helper", status)</Source></Part></Object>"#;
    let input = input.replace("</Objects>", &format!("{helper}</Objects>"));
    let project = exchange::project_from_xml(&input, "Main", None).unwrap();
    assert_eq!(project.procedures.len(), 2);
    assert!(
        project
            .procedures
            .iter()
            .any(|procedure| procedure.name.eq_ignore_ascii_case("Main"))
    );
    assert!(
        project
            .procedures
            .iter()
            .any(|procedure| procedure.name.eq_ignore_ascii_case("Helper"))
    );
    assert!(gx_engine::compile(&project).is_ok());
    assert!(exchange::catalog_from_xml(&input.replace("Helper.Call()", "Missing.Call()")).is_err());
}

#[test]
fn exchange_rejects_size_depth_namespaces_and_standalone_invalid_fields() {
    assert!(exchange::catalog_from_xml(&" ".repeat(exchange::MAX_XML_BYTES + 1)).is_err());
    let deep = format!(
        "<ExportFile><Objects>{}<Item/>{}</Objects></ExportFile>",
        "<Level>".repeat(130),
        "</Level>".repeat(130)
    );
    assert!(exchange::catalog_from_xml(&deep).is_err());
    assert!(
        exchange::catalog_from_xml(
            &xml().replace("<ExportFile>", "<ExportFile xmlns=\"urn:unsupported\">")
        )
        .is_err()
    );
    let no_procedure = xml();
    let a = no_procedure.find("<Object guid=\"main-id\"").unwrap();
    let b = no_procedure.find("<Object guid=\"probe-id\"").unwrap();
    let input = format!("{}{}", &no_procedure[..a], &no_procedure[b..]);
    assert!(
        exchange::catalog_from_xml(&input.replace("<Value>12</Value>", "<Value>0</Value>"))
            .is_err()
    );
    assert!(exchange::catalog_from_xml(&input).is_ok());
}

#[test]
fn exchange_rejects_hidden_declaration_content_collections_and_duplicate_ids() {
    for input in [
        xml().replace("guid=\"probe-id\"", "guid=\"main-id\""),
        xml().replace("<Level Name=\"Probe\">", "<Level Name=\"Probe\"><LevelInfo><Properties><Property><Name>Collection</Name><Value>True</Value></Property></Properties></LevelInfo>"),
        xml().replace("</Item>", "<UnsupportedSemantics/></Item>"),
        xml().replace("<Source><![CDATA[", "<UnsupportedSemantics/><Source><![CDATA["),
    ] {
        assert!(exchange::catalog_from_xml(&input).is_err(), "admitted hidden content");
    }
}

#[test]
fn exchange_rejects_external_entities_missing_types_duplicate_properties_and_nested_sdt() {
    for input in [
        format!("<!DOCTYPE ExportFile [<!ENTITY ext SYSTEM 'file:///not-read'>]>{}", xml()),
        xml().replace("sdt:Probe", "sdt:Missing"),
        xml().replace("<Name>Length</Name><Value>12</Value>", "<Name>Length</Name><Value>12</Value></Property><Property><Name>Length</Name><Value>7</Value>"),
        xml().replace("</Level>", "<Level Name=\"Nested\"/></Level>"),
        xml().replace("84a12160-f59b-4ad7-a683-ea4481ac23e9", "unsupported-kind"),
        xml().replace("</Objects>", "<Object guid=\"duplicate\" name=\"Probe\" type=\"447527b5-9210-4523-898b-5dccb17be60a\"/></Objects>"),
    ] {
        assert!(exchange::catalog_from_xml(&input).is_err());
    }
}

#[test]
fn nested_sdt_levels_project_to_record_and_collection_schemas_and_resolve_aliases() {
    let child_guid = "22222222-2222-4222-8222-222222222222";
    let rows_guid = "33333333-3333-4333-8333-333333333333";
    let child_schema = format!("GXL_{}", child_guid.to_ascii_uppercase().replace('-', "_"));
    let rows_schema = format!("GXL_{}", rows_guid.to_ascii_uppercase().replace('-', "_"));

    for (custom_item_name, alias) in [(None, "RowsItem"), (Some("Entry"), "Entry")] {
        let catalog = exchange::catalog_from_xml(&nested_xml(custom_item_name)).unwrap();
        assert_eq!(catalog.objects().count(), 4);
        let root = catalog.get(&ObjectId::new("root-id").unwrap()).unwrap();
        let fields: std::collections::BTreeMap<String, DataType> = serde_json::from_str(
            &root
                .source_parts
                .iter()
                .find(|part| part.name == "FIELDS")
                .unwrap()
                .source,
        )
        .unwrap();
        assert_eq!(
            fields.get("CHILD"),
            Some(&DataType::Record {
                schema: child_schema.clone()
            })
        );
        assert_eq!(
            fields.get("ROWS"),
            Some(&DataType::Collection {
                schema: rows_schema.clone()
            })
        );
        assert!(fields.contains_key("AMOUNT"));
        assert!(fields.contains_key("LABEL"));
        assert!(fields.contains_key("FIXED"));

        let child_id = ObjectId::new(format!("level-{child_guid}")).unwrap();
        let rows_id = ObjectId::new(format!("level-{rows_guid}")).unwrap();
        for id in [&child_id, &rows_id] {
            assert!(root.references.iter().any(|reference| {
                reference.kind == ReferenceKind::Uses && reference.target == *id
            }));
        }
        let child = catalog.get(&child_id).unwrap();
        assert_eq!(child.name, child_schema);
        let child_fields: std::collections::BTreeMap<String, DataType> = serde_json::from_str(
            &child
                .source_parts
                .iter()
                .find(|part| part.name == "FIELDS")
                .unwrap()
                .source,
        )
        .unwrap();
        assert_eq!(
            child_fields.get("AMOUNT"),
            Some(&DataType::Numeric {
                precision: 4,
                scale: 0
            })
        );
        assert_eq!(catalog.get(&rows_id).unwrap().name, rows_schema);

        let main = catalog.get(&ObjectId::new("main-id").unwrap()).unwrap();
        let variables: std::collections::BTreeMap<String, DataType> = serde_json::from_str(
            &main
                .source_parts
                .iter()
                .find(|part| part.name == "VARIABLES")
                .unwrap()
                .source,
        )
        .unwrap();
        assert_eq!(
            variables.get("FIRST"),
            Some(&DataType::Record {
                schema: "Nested".into()
            })
        );
        for variable in ["CHILD", "ROW"] {
            let schema = if variable == "CHILD" {
                &child_schema
            } else {
                &rows_schema
            };
            assert_eq!(
                variables.get(variable),
                Some(&DataType::Record {
                    schema: schema.clone()
                })
            );
        }
        for id in [ObjectId::new("root-id").unwrap(), child_id, rows_id] {
            assert!(main.references.iter().any(|reference| {
                reference.kind == ReferenceKind::Uses && reference.target == id
            }));
        }
        assert_eq!(
            alias,
            if custom_item_name.is_some() {
                "Entry"
            } else {
                "RowsItem"
            }
        );
    }
}

#[test]
fn nested_sdt_and_collection_xml_projection_runs_native_observed_operations() {
    let source = r#"&First = new()
&Index = 1
If &First.Child.Amount = 0
Msg("nested_default=0", status)
Else
Msg("nested_default=unexpected", status)
EndIf
&First.Child.Amount = 7
&Second = &First
&Second.Child.Amount = 9
If &First.Child.Amount = 9 And &Second.Child.Amount = 9
Msg("nested_alias=shared", status)
Else
Msg("nested_alias=unexpected", status)
EndIf
&Second = &First.Clone()
&Second.Child.Amount = 11
If &First.Child.Amount = 11 And &Second.Child.Amount = 11
Msg("nested_clone=shallow", status)
Else
Msg("nested_clone=unexpected", status)
EndIf
If &First.Rows.Count = 0
Msg("collection_default=0", status)
Else
Msg("collection_default=unexpected", status)
EndIf
&Row = new()
&First.Rows.Add(&Row)
&First.Rows.Item(1).Amount = 7
&Second = &First.Clone()
&Second.Rows.Item(1).Amount = 9
&Row = new()
&Second.Rows.Add(&Row)
If &First.Rows.Item(1).Amount = 9 And &First.Rows.Count = 2 And &Second.Rows.Count = 2
Msg("root_clone=shared", status)
Else
Msg("root_clone=unexpected", status)
EndIf
&Second.Rows = &First.Rows.Clone()
&Second.Rows.Item(1).Amount = 13
&Row = new()
&Second.Rows.Add(&Row)
If &First.Rows.Item(1).Amount = 13 And &Second.Rows.Item(1).Amount = 13 And &First.Rows.Count = 2 And &Second.Rows.Count = 3
Msg("collection_clone=shared_items_independent_list", status)
Else
Msg("collection_clone=unexpected", status)
EndIf"#;
    let source_xml = with_main_source(&nested_xml(None), source);
    let directory = unique_temp_dir("gx-xml-nested");
    let input = directory.join("authored.xml");
    std::fs::write(&input, &source_xml).unwrap();
    let output = std::process::Command::new(env!("CARGO_BIN_EXE_gx-engine"))
        .arg("xml-run")
        .arg(&input)
        .arg("Main")
        .arg(directory.join("output"))
        .output()
        .unwrap();
    let unchanged = std::fs::read_to_string(&input).unwrap();
    std::fs::remove_dir_all(&directory).unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(unchanged, source_xml);
    assert_eq!(
        String::from_utf8_lossy(&output.stdout).trim(),
        concat!(
            "nested_default=0\n",
            "nested_alias=shared\n",
            "nested_clone=shallow\n",
            "collection_default=0\n",
            "root_clone=shared\n",
            "collection_clone=shared_items_independent_list"
        )
    );
}

#[test]
fn nested_sdt_rejects_malformed_metadata_ambiguous_aliases_and_recursion() {
    let base = nested_xml(None);
    let child_guid = "22222222-2222-4222-8222-222222222222";
    let rows_guid = "33333333-3333-4333-8333-333333333333";
    let duplicate_name = base
        .replace("<Level Name=\"Child\">", "<Level Name=\"Rows\">")
        .replace("name=\"Child\"", "name=\"Rows\"")
        .replace("<Value>Child</Value>", "<Value>Rows</Value>");
    let duplicate_guid = base.replace(child_guid, rows_guid);
    let invalid_boolean = base.replace("<Value>False</Value>", "<Value>sometimes</Value>");
    let missing_guid = base.replace(&format!("guid=\"{child_guid}\""), "guid=\"\"");
    let unknown_property = base.replace(
        "<Value>False</Value></Property></Properties></LevelInfo>",
        "<Value>False</Value></Property><Property><Name>FutureMode</Name><Value>x</Value></Property></Properties></LevelInfo>",
    );
    let root_collection = base.replace(
        "<Value>Nested</Value></Property></Properties></LevelInfo>",
        "<Value>Nested</Value></Property><Property><Name>AttCollection</Name><Value>True</Value></Property></Properties></LevelInfo>",
    );
    let missing_root_info = base.replace(
        "<LevelInfo guid=\"11111111-1111-4111-8111-111111111111\" name=\"Nested\"><Properties><Property><Name>Name</Name><Value>Nested</Value></Property></Properties></LevelInfo>",
        "",
    );
    let wrong_level_name = base.replace("<Level Name=\"Child\">", "<Level Name=\"Other\">");
    let empty_collection_alias = nested_xml(Some(""));
    let id_name_without_collection = base.replace(
        "<Value>False</Value></Property></Properties></LevelInfo>",
        "<Value>False</Value></Property><Property><Name>idCollectionItemName</Name><Value>Entry</Value></Property></Properties></LevelInfo>",
    );
    let recursive_field = base.replace("<Value>bas:Numeric</Value>", "<Value>sdt:Nested</Value>");
    let unresolved_alias = base.replace("sdt:Nested.Child", "sdt:Nested.Missing");
    let ambiguous_alias = nested_xml(Some("Child"));
    let top_id_collision =
        base.replace("guid=\"main-id\"", &format!("guid=\"level-{child_guid}\""));
    let schema_name_collision = base.replace(
        "</Objects>",
        &format!(
            "<Object guid=\"shadow-id\" name=\"GXL_{}\" type=\"447527b5-9210-4523-898b-5dccb17be60a\"/></Objects>",
            child_guid.to_ascii_uppercase().replace('-', "_")
        ),
    );
    let unsupported_level_child = base.replace(
        "<Level Name=\"Child\"><LevelInfo",
        "<Level Name=\"Child\"><Unsupported/><LevelInfo",
    );

    for (label, input) in [
        ("duplicate level name", duplicate_name),
        ("duplicate level GUID", duplicate_guid),
        ("invalid collection boolean", invalid_boolean),
        ("missing level GUID", missing_guid),
        ("unknown LevelInfo property", unknown_property),
        ("root collection", root_collection),
        ("missing root LevelInfo", missing_root_info),
        ("Level/LevelInfo name mismatch", wrong_level_name),
        ("empty collection alias", empty_collection_alias),
        ("item alias without collection", id_name_without_collection),
        ("recursive field", recursive_field),
        ("missing type alias", unresolved_alias),
        ("ambiguous alias", ambiguous_alias),
        ("synthetic ID collision", top_id_collision),
        ("synthetic schema collision", schema_name_collision),
        ("unsupported Level child", unsupported_level_child),
    ] {
        assert!(
            exchange::catalog_from_xml(&input).is_err(),
            "admitted {label}"
        );
    }
}

#[test]
fn nested_sdt_schema_limit_counts_synthetic_schemas() {
    fn extra_schema(index: usize) -> String {
        format!(
            "<Object guid=\"extra-{index}\" name=\"Extra{index}\" type=\"447527b5-9210-4523-898b-5dccb17be60a\"><Part type=\"5c2aa9da-8fc4-4b6b-ae02-8db4fa48976a\"><Level Name=\"Extra{index}\"><Item name=\"Value\"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Character</Value></Property><Property><Name>Length</Name><Value>1</Value></Property></Properties></Item></Level></Part></Object>"
        )
    }

    let base = nested_xml(None);
    let at_limit = format!(
        "{}{}{}",
        &base[..base.find("</Objects>").unwrap()],
        (0..125).map(extra_schema).collect::<String>(),
        &base[base.find("</Objects>").unwrap()..]
    );
    let catalog = exchange::catalog_from_xml(&at_limit).unwrap();
    assert_eq!(catalog.objects().count(), 129);

    let over_limit = format!(
        "{}{}{}",
        &base[..base.find("</Objects>").unwrap()],
        (0..126).map(extra_schema).collect::<String>(),
        &base[base.find("</Objects>").unwrap()..]
    );
    assert!(exchange::catalog_from_xml(&over_limit).is_err());
}

// Synthetic, redacted structural equivalent of a captured native deep SDT export:
// RootValue, a Child level holding ChildValue, a Grandchild level, and a nested
// NestedCollection level without an explicit idCollectionItemName. Only shapes
// and test identities belong here; never check in the raw SDK export. Native
// readback resolves `Deep.Child.NestedCollectionItem` as the only alias beyond
// the root level; nothing deeper or composite beyond depth 1 is evidenced.
fn deep_nested_xml() -> String {
    r#"<ExportFile><Objects>
<Object guid="deep-main" name="Main" type="84a12160-f59b-4ad7-a683-ea4481ac23e9">
<Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff"><Source>Msg("deep", status)</Source></Part>
<Part type="e4c4ade7-53f0-4a56-bdfd-843735b66f47">
<Variable Name="First"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Deep</Value></Property></Properties></Variable>
</Part></Object>
<Object guid="deep-root" name="Deep" type="447527b5-9210-4523-898b-5dccb17be60a">
<Part type="5c2aa9da-8fc4-4b6b-ae02-8db4fa48976a"><Level Name="Deep">
<LevelInfo guid="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" name="Deep"><Properties><Property><Name>Name</Name><Value>Deep</Value></Property></Properties></LevelInfo>
<Item name="RootValue"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:VarChar</Value></Property><Property><Name>Length</Name><Value>30</Value></Property><Property><Name>AttMaxLen</Name><Value>30</Value></Property><Property><Name>AttCollection</Name><Value>False</Value></Property></Properties></Item>
<Level Name="Child"><LevelInfo guid="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb" name="Child"><Properties><Property><Name>Name</Name><Value>Child</Value></Property><Property><Name>AttCollection</Name><Value>False</Value></Property></Properties></LevelInfo>
<Item name="ChildValue"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:VarChar</Value></Property><Property><Name>Length</Name><Value>40</Value></Property><Property><Name>AttCollection</Name><Value>False</Value></Property></Properties></Item>
<Level Name="Grandchild"><LevelInfo guid="cccccccc-cccc-4ccc-8ccc-cccccccccccc" name="Grandchild"><Properties><Property><Name>Name</Name><Value>Grandchild</Value></Property><Property><Name>AttCollection</Name><Value>False</Value></Property></Properties></LevelInfo>
<Item name="GrandchildValue"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:Numeric</Value></Property><Property><Name>Length</Name><Value>8</Value></Property><Property><Name>Decimals</Name><Value>0</Value></Property><Property><Name>AttCollection</Name><Value>False</Value></Property></Properties></Item>
</Level>
<Level Name="NestedCollection"><LevelInfo guid="dddddddd-dddd-4ddd-8ddd-dddddddddddd" name="NestedCollection"><Properties><Property><Name>Name</Name><Value>NestedCollection</Value></Property><Property><Name>AttCollection</Name><Value>True</Value></Property></Properties></LevelInfo>
<Item name="NestedValue"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:VarChar</Value></Property><Property><Name>Length</Name><Value>24</Value></Property><Property><Name>AttCollection</Name><Value>False</Value></Property></Properties></Item>
</Level>
</Level></Level></Part></Object></Objects></ExportFile>"#.into()
}

fn with_deep_main_source(xml: &str, source: &str) -> String {
    xml.replace(
        "<Source>Msg(\"deep\", status)</Source>",
        &format!("<Source><![CDATA[{source}]]></Source>"),
    )
}

fn deep_nested_xml_with_item_variable() -> String {
    deep_nested_xml().replace(
        "<Variable Name=\"First\">",
        "<Variable Name=\"Nested\"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Deep.Child.NestedCollectionItem</Value></Property></Properties></Variable><Variable Name=\"First\">",
    )
}

fn fields_of(
    object: &gx_engine::catalog::KbObject,
) -> std::collections::BTreeMap<String, DataType> {
    serde_json::from_str(
        &object
            .source_parts
            .iter()
            .find(|part| part.name == "FIELDS")
            .unwrap()
            .source,
    )
    .unwrap()
}

#[test]
fn deep_nested_sdt_levels_project_recursively_and_wire_direct_uses() {
    let child_guid = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    let grand_guid = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    let collection_guid = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    let schema = |guid: &str| format!("GXL_{}", guid.to_ascii_uppercase().replace('-', "_"));
    let child_schema = schema(child_guid);
    let grand_schema = schema(grand_guid);
    let collection_schema = schema(collection_guid);

    let catalog = exchange::catalog_from_xml(&deep_nested_xml()).unwrap();
    assert_eq!(catalog.objects().count(), 5);

    let root = catalog.get(&ObjectId::new("deep-root").unwrap()).unwrap();
    let root_fields = fields_of(root);
    assert_eq!(
        root_fields.get("ROOTVALUE"),
        Some(&DataType::Varchar { length: 30 })
    );
    assert_eq!(
        root_fields.get("CHILD"),
        Some(&DataType::Record {
            schema: child_schema.clone()
        })
    );

    let child_id = ObjectId::new(format!("level-{child_guid}")).unwrap();
    let grand_id = ObjectId::new(format!("level-{grand_guid}")).unwrap();
    let collection_id = ObjectId::new(format!("level-{collection_guid}")).unwrap();
    assert_eq!(
        root.references
            .iter()
            .map(|reference| (reference.kind.clone(), reference.target.clone()))
            .collect::<Vec<_>>(),
        vec![(ReferenceKind::Uses, child_id.clone())],
        "the root Level must reference only its direct child Levels"
    );

    let child = catalog.get(&child_id).unwrap();
    assert_eq!(child.name, child_schema);
    let child_fields = fields_of(child);
    assert_eq!(
        child_fields.get("CHILDVALUE"),
        Some(&DataType::Varchar { length: 40 })
    );
    assert_eq!(
        child_fields.get("GRANDCHILD"),
        Some(&DataType::Record {
            schema: grand_schema.clone()
        })
    );
    assert_eq!(
        child_fields.get("NESTEDCOLLECTION"),
        Some(&DataType::Collection {
            schema: collection_schema.clone()
        })
    );
    assert_eq!(
        child
            .references
            .iter()
            .map(|reference| (reference.kind.clone(), reference.target.clone()))
            .collect::<Vec<_>>(),
        vec![
            (ReferenceKind::Uses, grand_id.clone()),
            (ReferenceKind::Uses, collection_id.clone())
        ],
        "an intermediate schema must reference only its direct child Levels"
    );

    let grand = catalog.get(&grand_id).unwrap();
    assert_eq!(grand.name, grand_schema);
    assert_eq!(
        fields_of(grand).get("GRANDCHILDVALUE"),
        Some(&DataType::Numeric {
            precision: 8,
            scale: 0
        })
    );
    assert!(grand.references.is_empty());

    let collection = catalog.get(&collection_id).unwrap();
    assert_eq!(collection.name, collection_schema);
    assert_eq!(
        fields_of(collection).get("NESTEDVALUE"),
        Some(&DataType::Varchar { length: 24 })
    );
    assert!(collection.references.is_empty());

    let project = exchange::project_from_xml(&deep_nested_xml(), "Main", None).unwrap();
    assert_eq!(project.record_types.len(), 4);
    assert!(gx_engine::compile(&project).is_ok());
}

#[test]
fn deep_nested_sdt_runtime_access_matches_the_bounded_model() {
    let source = r#"&First = new()
Msg("deep_default=" + Trim(Str(&First.Child.Grandchild.GrandchildValue)), status)
&First.Child.Grandchild.GrandchildValue = 42
Msg("deep_write=" + Trim(Str(&First.Child.Grandchild.GrandchildValue)), status)
Msg("deep_collection_count=" + Trim(Str(&First.Child.NestedCollection.Count)), status)
&First.Child.ChildValue = "child"
Msg("deep_text=" + &First.Child.ChildValue, status)"#;
    let output = run_xml(&with_deep_main_source(&deep_nested_xml(), source), "Main");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        normalize_crlf(&String::from_utf8(output.stdout).unwrap()),
        concat!(
            "deep_default=0\n",
            "deep_write=42\n",
            "deep_collection_count=0\n",
            "deep_text=child\n"
        )
    );
}

#[test]
fn deep_nested_sdt_depth_two_collection_item_alias_resolves_and_wires_uses() {
    let collection_guid = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    let collection_schema = format!(
        "GXL_{}",
        collection_guid.to_ascii_uppercase().replace('-', "_")
    );
    let collection_id = ObjectId::new(format!("level-{collection_guid}")).unwrap();

    let xml = deep_nested_xml_with_item_variable();
    let catalog = exchange::catalog_from_xml(&xml).unwrap();
    let main = catalog.get(&ObjectId::new("deep-main").unwrap()).unwrap();
    let variables: std::collections::BTreeMap<String, DataType> = serde_json::from_str(
        &main
            .source_parts
            .iter()
            .find(|part| part.name == "VARIABLES")
            .unwrap()
            .source,
    )
    .unwrap();
    assert_eq!(
        variables.get("NESTED"),
        Some(&DataType::Record {
            schema: collection_schema.clone()
        }),
        "the full-path depth-2 collection item alias must resolve to the item schema"
    );
    assert!(
        main.references.iter().any(|reference| {
            reference.kind == ReferenceKind::Uses && reference.target == collection_id
        }),
        "the item alias must wire a Uses reference to the collection level"
    );

    let project = exchange::project_from_xml(&xml, "Main", None).unwrap();
    assert!(gx_engine::compile(&project).is_ok());
}

#[test]
fn deep_nested_sdt_depth_two_collection_item_alias_runs_native_observed_operations() {
    let source = r#"&Nested = new()
&Nested.NestedValue = "i"
Msg("deep_item=" + &Nested.NestedValue, status)"#;
    let output = run_xml(
        &with_deep_main_source(&deep_nested_xml_with_item_variable(), source),
        "Main",
    );
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        normalize_crlf(&String::from_utf8(output.stdout).unwrap()),
        "deep_item=i\n"
    );
}

#[test]
fn deep_nested_sdt_rejects_item_collections_missing_scalars_and_unregistered_aliases() {
    let base = deep_nested_xml();
    let child_value_item = "<Item name=\"ChildValue\"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>bas:VarChar</Value></Property><Property><Name>Length</Name><Value>40</Value></Property><Property><Name>AttCollection</Name><Value>False</Value></Property></Properties></Item>";
    let item_collection = base.replace(
        "<Value>30</Value></Property><Property><Name>AttCollection</Name><Value>False</Value>",
        "<Value>30</Value></Property><Property><Name>AttCollection</Name><Value>True</Value>",
    );
    let missing_scalar_level = base.replace(child_value_item, "");
    let deep_alias = base.replace(
        "<Variable Name=\"First\">",
        "<Variable Name=\"Grand\"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Deep.Grandchild</Value></Property></Properties></Variable><Variable Name=\"First\">",
    );
    let collection_level_alias = base.replace(
        "<Variable Name=\"First\">",
        "<Variable Name=\"Second\"><Properties><Property><Name>ATTCUSTOMTYPE</Name><Value>sdt:Deep.Child.NestedCollection</Value></Property></Properties></Variable><Variable Name=\"First\">",
    );

    for (label, input) in [
        ("item-level collection", item_collection),
        ("level without scalar Items", missing_scalar_level),
        ("unregistered depth-2 composite alias", deep_alias),
        (
            "unregistered collection-level alias",
            collection_level_alias,
        ),
    ] {
        assert!(
            exchange::catalog_from_xml(&input).is_err(),
            "admitted {label}"
        );
    }
}
