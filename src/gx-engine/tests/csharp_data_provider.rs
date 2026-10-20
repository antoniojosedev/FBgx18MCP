use gx_engine::{
    catalog::{KbObject, ObjectId, ObjectKind, SourcePart},
    csharp::data_provider::{DataProviderOutput, procedure_class},
    model::DataType,
};
use std::collections::BTreeMap;

fn provider(source: &str) -> KbObject {
    let mut object = KbObject::new(
        ObjectId::new("dp").unwrap(),
        "MenuDP",
        ObjectKind::DataProvider,
    );
    object.source_parts = vec![
        SourcePart {
            name: "SOURCE".into(),
            source: source.into(),
        },
        SourcePart {
            name: "RULES".into(),
            source: String::new(),
        },
        SourcePart {
            name: "VARIABLES".into(),
            source: serde_json::to_string(&BTreeMap::from([
                (
                    "Items",
                    DataType::BaseCollection {
                        schema: "Names_Name".into(),
                        item: "Name".into(),
                        namespace: "Test".into(),
                    },
                ),
                (
                    "Item",
                    DataType::Record {
                        schema: "Names_Name".into(),
                    },
                ),
            ]))
            .unwrap(),
        },
        SourcePart {
            name: "DP_OUTPUT".into(),
            source: serde_json::to_string(&DataProviderOutput {
                name: "Menus".into(),
                data_type: DataType::BaseCollection {
                    schema: "Menus_Menu".into(),
                    item: "Menu".into(),
                    namespace: "Test".into(),
                },
            })
            .unwrap(),
        },
    ];
    object
}

#[test]
fn collection_provider_lowers_input_and_assignments_without_losing_types() {
    let object = provider(
        "Menus\n{\n&Items = LoadNames()\nMenu\ninput &Item in &Items\n{\nTitle = &Item.Name\nActive = false\n}\n}",
    );
    let class = procedure_class(&object).unwrap();
    assert_eq!(class.params.len(), 1);
    assert_eq!(
        class.params[0].data_type,
        DataType::BaseCollection {
            schema: "Menus_Menu".into(),
            item: "Menu".into(),
            namespace: "Test".into()
        }
    );
    assert_eq!(class.body.len(), 2);
    assert!(format!("{:?}", class.body).contains("ForIn"));
    assert!(format!("{:?}", class.body).contains("CollectionAdd"));
    assert!(format!("{:?}", class.body).contains("AssignField"));
}

#[test]
fn collection_provider_preserves_braces_and_comment_markers_in_strings() {
    let class = procedure_class(&provider("Menus { Menu { Title = 'a { b } // c'\n } }")).unwrap();
    assert!(format!("{:?}", class.body).contains("a { b } // c"));
}

#[test]
fn unsupported_provider_syntax_never_silently_disappears() {
    for source in [
        "Other { Menu { Title = 'x' } }",
        "Menus { Menu input &Item in &Items where Active { Title = 'x' } }",
        "Menus { Menu { Title = 'x'\nChild { Id = 1 } } }",
        "Menus { Menu { Title = 'x' } } Trailing",
        "Menus { Menu { Title = 'x' }\nCommit }",
        "Menus { Menu { Title = 'x'\nTitle = 'y' } }",
        "Menus { Menu { &Item.Name = 'mutate input' } }",
        "Menus { WrongItem { Title = 'x' } }",
        "Menus { &Missing = LoadNames()\nMenu { Title = 'x' } }",
    ] {
        assert!(
            procedure_class(&provider(source)).is_err(),
            "must refuse: {source}"
        );
    }
}

#[test]
fn provider_requires_explicit_output_and_supported_rules() {
    let mut object = provider("Menus { Menu { Title = 'x' } }");
    object.source_parts.retain(|p| p.name != "DP_OUTPUT");
    assert!(procedure_class(&object).is_err());
    let mut object = provider("Menus { Menu { Title = 'x' } }");
    object
        .source_parts
        .iter_mut()
        .find(|p| p.name == "RULES")
        .unwrap()
        .source = "parm(in:&Item);".into();
    assert!(procedure_class(&object).is_err());
    let mut object = provider("Menus { Menu { Title = 'x' } }");
    object.source_parts.push(SourcePart {
        name: "CONDITIONS".into(),
        source: "Active = true".into(),
    });
    assert!(procedure_class(&object).unwrap_err().contains("CONDITIONS"));
}

#[test]
fn collection_provider_rebuild_executes_against_the_real_genexus_runtime() {
    use gx_engine::catalog::{Catalog, Edit, Reference, ReferenceKind};
    use gx_engine::rebuild::{RebuildOptions, RebuildStatus, rebuild_all};
    let id = |s: &str| ObjectId::new(s.to_string()).unwrap();
    let sdt = |name: &str, item: &str, fields: serde_json::Value| {
        let mut object = KbObject::new(id(name), name, ObjectKind::StructuredDataType);
        object.source_parts.push(SourcePart {
            name: "STRUCTURE".into(),
            source: serde_json::json!({"root_collection": item, "fields": fields}).to_string(),
        });
        object
    };
    let names = sdt(
        "Names",
        "Name",
        serde_json::json!([
            {"node":"field", "name":"Name", "data_type":{"kind":"varchar","length":80}}
        ]),
    );
    let menus = sdt(
        "Menus",
        "Menu",
        serde_json::json!([
            {"node":"field", "name":"Title", "data_type":{"kind":"varchar","length":80}},
            {"node":"field", "name":"Active", "data_type":{"kind":"boolean"}}
        ]),
    );
    let mut producer = KbObject::new(id("producer"), "LoadNames", ObjectKind::Procedure);
    let declarations = provider("")
        .source_parts
        .into_iter()
        .find(|p| p.name == "VARIABLES")
        .unwrap();
    producer.source_parts = vec![
        declarations,
        SourcePart {
            name: "RULES".into(),
            source: "parm(out:&Items);".into(),
        },
        SourcePart {
            name: "SOURCE".into(),
            source: "&Item.Name = 'one'
&Items.Add(&Item)
&Item = new()
&Item.Name = 'two'
&Items.Add(&Item)"
                .into(),
        },
    ];
    producer.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: names.id.clone(),
    });
    let mut dp = provider(
        "Menus
{
&Items = LoadNames()
Menu
input &Item in &Items
{
Title = &Item.Name
Active = false
}
}",
    );
    dp.references = vec![
        Reference {
            kind: ReferenceKind::Calls,
            target: producer.id.clone(),
        },
        Reference {
            kind: ReferenceKind::Uses,
            target: names.id.clone(),
        },
        Reference {
            kind: ReferenceKind::Uses,
            target: menus.id.clone(),
        },
    ];
    let mut catalog = Catalog::empty();
    catalog
        .apply(
            0,
            &[names, menus, producer, dp]
                .into_iter()
                .map(Edit::Upsert)
                .collect::<Vec<_>>(),
        )
        .unwrap();
    let directory = std::env::temp_dir().join(format!(
        "gxe_dp_runtime_{}_{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    let build = directory.join("build");
    let report = rebuild_all(&catalog, &build, &RebuildOptions::default()).unwrap();
    assert_eq!(
        report.status,
        RebuildStatus::Complete,
        "{report:?}; fixture: {}",
        directory.display()
    );
    assert_eq!(report.not_generated_count, 0);
    let runner = directory.join("runner");
    std::fs::create_dir_all(&runner).unwrap();
    std::fs::write(runner.join("Runner.csproj"), r#"<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../build/RebuildApp.csproj" /></ItemGroup></Project>"#).unwrap();
    std::fs::write(runner.join("Program.cs"), r#"
using System;
using GeneXus.Application;
using GeneXus.Programs;
using GeneXus.Utils;
var context = new GxContext();
var provider = new menudp(context);
for (int invocation = 0; invocation < 2; invocation++) {
    provider.execute(out GXBaseCollection<SdtMenus_Menu> items);
    var first = (SdtMenus_Menu)items.Item(1);
    var second = (SdtMenus_Menu)items.Item(2);
    if (items.Count != 2 || first.gxTpr_Title != "one" || second.gxTpr_Title != "two" || first.gxTpr_Active || Object.ReferenceEquals(first, second)) throw new Exception("DataProvider values/allocation differ");
}
Console.WriteLine("GXE-DP-RUNTIME:OK");
"#).unwrap();
    let output = std::process::Command::new("dotnet")
        .args(["run", "--project"])
        .arg(runner.join("Runner.csproj"))
        .args(["--configuration", "Release"])
        .output()
        .unwrap();
    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(
        output.status.success() && stdout.contains("GXE-DP-RUNTIME:OK"),
        "DataProvider runtime failed; fixture {}: {stdout}
{stderr}",
        directory.display()
    );
    std::fs::remove_dir_all(directory).unwrap();
}

#[test]
fn collection_result_call_requires_the_evidenced_output_signature() {
    use gx_engine::csharp::{
        calls::{CallParam, CallSignatures, CallTarget},
        data_provider::resolve_collection_calls,
    };
    use gx_engine::model::ParamMode;
    let object = provider(
        "Menus { &Items = LoadNames()
Menu input &Item in &Items { Title = &Item.Name } }",
    );
    let class = procedure_class(&object).unwrap();
    assert!(resolve_collection_calls(class.clone(), &CallSignatures::new()).is_err());
    for mode in [ParamMode::In, ParamMode::InOut, ParamMode::Out] {
        let mut calls = CallSignatures::new();
        calls
            .insert(CallTarget {
                class_name: "loadnames".into(),
                namespace: String::new(),
                params: vec![CallParam {
                    mode,
                    data_type: class.variables["Items"].clone(),
                }],
            })
            .unwrap();
        let result = resolve_collection_calls(class.clone(), &calls);
        if mode == ParamMode::Out {
            let lowered = result.unwrap();
            assert_eq!(lowered.body.len(), 4);
            assert!(format!("{:?}", lowered.body).contains("GxeDpCallResult0"));
        } else {
            assert!(result.is_err());
        }
    }
    let singleton = procedure_class(&provider("Menus { Menu { Title = 'one' } }")).unwrap();
    assert!(resolve_collection_calls(singleton, &CallSignatures::new()).is_ok());
}
