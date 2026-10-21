//! Synthetic execution against the real GeneXus .NET runtime, not an SDK parity oracle.
//! Uses an explicit empty context; no KB, datastore, generator or SDK process is opened.

use gx_engine::{
    csharp::{
        CsharpParam, CsprojConfig, CsprojEmitter, ProcedureClass,
        sdt::{SdtClass, SdtIndex},
        to_csharp_var_name,
    },
    model::{DataType, ParamMode, ProcedureSource},
    udm::stored_sdt::SdtNode,
};
use std::{collections::BTreeMap, process::Command, sync::Arc};

#[test]
fn root_collection_out_parameter_executes_with_real_genexus_runtime() {
    let directory =
        std::env::temp_dir().join(format!("gxe_collection_runtime_{}", std::process::id()));
    std::fs::create_dir_all(&directory).expect("create runtime fixture directory");
    let collection = DataType::BaseCollection {
        schema: "CollectionProbe_Item".into(),
        item: "CollectionProbe.Item".into(),
        namespace: "Test".into(),
    };
    let mut item = SdtClass::new("CollectionProbe_Item")
        .add_field(
            "Amount",
            DataType::Numeric {
                precision: 4,
                scale: 0,
            },
        )
        .add_field("Label", DataType::Varchar { length: 40 })
        .add_field("Flag", DataType::Boolean {})
        .add_field("Day", DataType::Date {})
        .add_field("Stamp", DataType::DateTime {})
        .add_field("Id", DataType::Guid {});
    for (name, collection) in [("Child", false), ("Rows", true)] {
        item.nodes.push(SdtNode::Level {
            name: name.into(),
            collection,
            item_name: None,
            children: vec![SdtNode::Field {
                name: "Label".into(),
                data_type: DataType::Varchar { length: 40 },
            }],
        });
    }
    let source = ProcedureSource {
        name: "CollectionRuntimeProbe".into(),
        params: vec![],
        variables: BTreeMap::from([
            ("Items".into(), collection.clone()),
            ("Json".into(), DataType::LongVarchar {}),
            (
                "Row".into(),
                DataType::Record {
                    schema: "CollectionProbe_Item".into(),
                },
            ),
        ]),
        return_type: None,
        source: "&Items.Add(&Row)\n&Items = new()\n&Row.Amount = 7\n&Items.Add(&Row)\n&Items.Add(&Row.Clone())\n&Items.Item(1).Amount = 11\n&Json = &Items.ToJson()\n".into(),
    };
    let procedure = ProcedureClass::from_source(&source)
        .expect("parse synthetic collection procedure")
        .add_param(CsharpParam {
            name: "Items".into(),
            data_type: collection,
            mode: ParamMode::Out,
            param_name: "aP0_Items".into(),
            var_name: to_csharp_var_name("Items"),
        })
        .add_param(CsharpParam {
            name: "Json".into(),
            data_type: DataType::LongVarchar {},
            mode: ParamMode::Out,
            param_name: "aP1_Json".into(),
            var_name: to_csharp_var_name("Json"),
        })
        .with_sdts(Arc::new(
            SdtIndex::from_classes(vec![item.clone()]).expect("unique SDT class"),
        ));
    std::fs::write(
        directory.join("collectionruntimeprobe.cs"),
        procedure.emit_checked().expect("emit collection procedure"),
    )
    .expect("write generated procedure");
    std::fs::write(directory.join("SdtCollectionProbe_Item.cs"), item.emit())
        .expect("write generated item");
    std::fs::write(
        directory.join("Program.cs"),
        r#"using System;
using GeneXus.Application;
using GeneXus.Programs;
using GeneXus.Utils;
using System.Text.Json;

static class Program
{
    static void Main()
    {
        var procedure = new collectionruntimeprobe(new GxContext());
        procedure.execute(out GXBaseCollection<SdtCollectionProbe_Item> first, out string firstWire);
        Check(first);
        using var emittedWire = JsonDocument.Parse(firstWire);
        if (emittedWire.RootElement[0].GetProperty("Amount").GetInt32() != 11)
            throw new Exception("generated ToJson expression loses item values");
        procedure.execute(out GXBaseCollection<SdtCollectionProbe_Item> second, out _);
        Check(second);
        if (Object.ReferenceEquals(first, second)) throw new Exception("out collection reused");
        ((SdtCollectionProbe_Item)first.Item(1)).gxTpr_Amount = 99;
        Check(second);
        var row = (SdtCollectionProbe_Item)second.Item(1);
        row.gxTpr_Label = "say \"hi\"\n";
        row.gxTpr_Flag = true;
        row.gxTpr_Day = new DateTime(2024, 2, 29);
        row.gxTpr_Stamp = new DateTime(2024, 2, 29, 14, 15, 16);
        row.gxTpr_Id = Guid.Parse("dd134d52-d477-4ac0-97bb-29aa654b7ef9");
        var other = (SdtCollectionProbe_Item)second.Item(2);
        other.gxTpr_Child.gxTpr_Label = "nested";
        var leaf = new SdtCollectionProbe_Item_RowsItem(new GxContext());
        leaf.gxTpr_Label = "leaf";
        other.gxTpr_Rows.Add(leaf, 0);
        using var json = JsonDocument.Parse(second.ToJSonString(false));
        if (json.RootElement.GetArrayLength() != 2 ||
            json.RootElement[0].GetProperty("Amount").GetInt32() != 11 ||
            json.RootElement[1].GetProperty("Amount").GetInt32() != 7)
            throw new Exception("collection JSON loses item values");
        var firstJson = json.RootElement[0];
        var secondJson = json.RootElement[1];
        if (firstJson.GetProperty("Label").GetString() != "say \"hi\"\n" ||
            !firstJson.GetProperty("Flag").GetBoolean() ||
            firstJson.GetProperty("Day").GetString() != "2024-02-29" ||
            firstJson.GetProperty("Stamp").GetString() != "2024-02-29T14:15:16" ||
            firstJson.GetProperty("Id").GetString() != "dd134d52-d477-4ac0-97bb-29aa654b7ef9" ||
            firstJson.TryGetProperty("Child", out _) || firstJson.TryGetProperty("Rows", out _) ||
            secondJson.GetProperty("Day").GetString() != "0000-00-00" ||
            secondJson.GetProperty("Stamp").GetString() != "0000-00-00T00:00:00" ||
            secondJson.GetProperty("Child").GetProperty("Label").GetString() != "nested" ||
            secondJson.GetProperty("Rows")[0].GetProperty("Label").GetString() != "leaf")
            throw new Exception("SDT JSON loses primitive or initialized-child values");
        var restored = new GXBaseCollection<SdtCollectionProbe_Item>(new GxContext(), "CollectionProbe.Item", "Test");
        if (!restored.FromJSonString(second.ToJSonString(false), null))
            throw new Exception("collection JSON cannot be read back");
        Check(restored);
        if (((SdtCollectionProbe_Item)restored.Item(1)).gxTpr_Label != row.gxTpr_Label ||
            ((SdtCollectionProbe_Item)restored.Item(2)).gxTpr_Child.gxTpr_Label != "nested")
            throw new Exception("collection JSON round trip loses field values");
        Console.WriteLine("GXE-COLLECTION-RUNTIME:OK");
    }

    static void Check(GXBaseCollection<SdtCollectionProbe_Item> items)
    {
        if (items == null || items.Count != 2 ||
            ((SdtCollectionProbe_Item)items.Item(1)).gxTpr_Amount != 11 ||
            ((SdtCollectionProbe_Item)items.Item(2)).gxTpr_Amount != 7)
            throw new Exception("collection allocation, Clone or Item semantics differ");
    }
}
"#,
    )
    .expect("write runtime assertions");
    let config = CsprojConfig::net8("collectionruntimeprobe", true)
        .as_executable("Program")
        .add_compile_include("SdtCollectionProbe_Item.cs")
        .add_compile_include("Program.cs");
    let project = directory.join("CollectionRuntime.csproj");
    std::fs::write(&project, CsprojEmitter::emit(&config)).expect("write generated project");
    let output = Command::new("dotnet")
        .args(["run", "--project"])
        .arg(&project)
        .args(["--configuration", "Release", "--no-launch-profile"])
        .output()
        .expect(".NET SDK is required for generated-code execution");
    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(
        output.status.success() && stdout.contains("GXE-COLLECTION-RUNTIME:OK"),
        "generated root collection failed; fixture retained at {}:\n{stdout}\n{stderr}",
        directory.display()
    );
    std::fs::remove_dir_all(&directory).expect("remove successful runtime fixture");
}
