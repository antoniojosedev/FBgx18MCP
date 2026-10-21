//! The SDT C# emitter: a decoded structure becomes the GeneXus class shape, and an SDT that holds no
//! fields is refused rather than emitted as an empty class.

use gx_engine::catalog::{KbObject, ObjectId, ObjectKind, SourcePart};
use gx_engine::csharp::SdtClass;

fn sdt_object(name: &str, structure_json: &str) -> KbObject {
    KbObject {
        id: ObjectId::new(format!("sdt-{name}")).expect("object id"),
        kind: ObjectKind::StructuredDataType,
        name: name.to_string(),
        source_parts: vec![SourcePart {
            name: "STRUCTURE".to_string(),
            source: structure_json.to_string(),
        }],
        references: Vec::new(),
        system_root: false,
        refusal: None,
        not_generated: None,
        library: false,
        main: None,
        module_path: Vec::new(),
    }
}

#[test]
fn an_sdt_with_no_fields_is_refused_not_emitted_empty() {
    let object = sdt_object("Empty", r#"{"fields":[]}"#);
    let error = SdtClass::from_kb_object(&object).unwrap_err();
    assert!(error.contains("declares no items"), "{error}");
}

#[test]
fn an_sdt_without_a_structure_part_is_refused() {
    let mut object = sdt_object("NoPart", r#"{"fields":[]}"#);
    object.source_parts.clear();
    let error = SdtClass::from_kb_object(&object).unwrap_err();
    assert!(error.contains("has no structure part"), "{error}");
}

#[test]
fn a_simple_sdt_emits_the_gxtpr_members_of_the_generated_shape() {
    // RSN03944700 (KBTeste): Amount NUMERIC(4), Label VARCHAR(6), Fixed CHARACTER(6).
    let json = r#"{"fields":[
        {"node":"field","name":"Amount","data_type":{"kind":"numeric","precision":4,"scale":0}},
        {"node":"field","name":"Label","data_type":{"kind":"varchar","length":6}},
        {"node":"field","name":"Fixed","data_type":{"kind":"character","length":6}}
    ]}"#;
    let class = SdtClass::from_kb_object(&sdt_object("RSN03944700", json)).expect("decodes");
    let code = class.emit();
    assert!(
        code.contains("public class SdtRSN03944700 : GxUserType"),
        "{code}"
    );
    // The SDK shape (type_SdtRSN03944700.cs): a backing field holding the default, and a getter and a
    // setter over it; the setter marks the member dirty.
    assert!(
        code.contains("protected short gxTv_SdtRSN03944700_Amount = 0;"),
        "{code}"
    );
    assert!(
        code.contains("protected string gxTv_SdtRSN03944700_Label = \"\";"),
        "{code}"
    );
    assert!(
        code.contains("protected string gxTv_SdtRSN03944700_Fixed = \"\";"),
        "{code}"
    );
    assert!(code.contains("public short gxTpr_Amount"), "{code}");
    assert!(
        code.contains("get { return gxTv_SdtRSN03944700_Amount; }"),
        "{code}"
    );
    assert!(
        code.contains("set { gxTv_SdtRSN03944700_Amount = value; SetDirty(\"Amount\"); }"),
        "{code}"
    );
}

#[test]
fn member_names_keep_the_first_letter_and_lower_the_rest_as_the_generator_does() {
    // ChildValue -> gxTpr_Childvalue, the rule the SDK's generated members show.
    let json = r#"{"fields":[
        {"node":"field","name":"ChildValue","data_type":{"kind":"varchar","length":40}}
    ]}"#;
    let code = SdtClass::from_kb_object(&sdt_object("Case", json))
        .expect("decodes")
        .emit();
    assert!(code.contains("public string gxTpr_Childvalue"), "{code}");
}

#[test]
fn a_nested_level_is_its_own_class_and_a_collection_is_a_base_collection() {
    // NativeDeepSdt85299100 (KBTeste): RootValue, Child (a nested level with a collection inside).
    let json = r#"{"fields":[
        {"node":"field","name":"RootValue","data_type":{"kind":"varchar","length":30}},
        {"node":"level","name":"Child","collection":false,"item_name":null,"children":[
            {"node":"field","name":"ChildValue","data_type":{"kind":"varchar","length":40}},
            {"node":"level","name":"NestedCollection","collection":true,"item_name":null,"children":[
                {"node":"field","name":"NestedValue","data_type":{"kind":"varchar","length":24}}
            ]}
        ]}
    ]}"#;
    let code = SdtClass::from_kb_object(&sdt_object("NativeDeepSdt85299100", json))
        .expect("decodes")
        .emit();
    assert!(code.contains("public string gxTpr_Rootvalue"), "{code}");
    assert!(
        code.contains("public SdtNativeDeepSdt85299100_Child gxTpr_Child"),
        "{code}"
    );
    assert!(
        code.contains(
            "protected SdtNativeDeepSdt85299100_Child gxTv_SdtNativeDeepSdt85299100_Child = null;"
        ),
        "{code}"
    );
    assert!(
        code.contains(
            "gxTv_SdtNativeDeepSdt85299100_Child = new SdtNativeDeepSdt85299100_Child(context);"
        ),
        "{code}"
    );
    assert!(
        code.contains("public class SdtNativeDeepSdt85299100_Child : GxUserType"),
        "{code}"
    );
    // The collection's item class is the holder class plus the item name (the SDK names
    // `SdtNativeDeepSdt85299100_Child_NestedCollectionItem`).
    assert!(
        code.contains(
            "public GXBaseCollection<SdtNativeDeepSdt85299100_Child_NestedCollectionItem> gxTpr_Nestedcollection"
        ),
        "{code}"
    );
    assert!(
        code.contains(
            "gxTv_SdtNativeDeepSdt85299100_Child_Nestedcollection = new GXBaseCollection<SdtNativeDeepSdt85299100_Child_NestedCollectionItem>( context, \"NativeDeepSdt85299100.Child.NestedCollectionItem\", \"\");"
        ),
        "{code}"
    );
    assert!(
        code.contains(
            "public class SdtNativeDeepSdt85299100_Child_NestedCollectionItem : GxUserType"
        ),
        "{code}"
    );
}

#[test]
fn a_top_level_collection_item_is_named_from_the_stored_item_name() {
    // RSN03944700.Rows is a collection with no stored item name: the item class is `RowsItem`
    // (the SDK's `SdtRSN03944700_RowsItem`).
    let json = r#"{"fields":[
        {"node":"level","name":"Rows","collection":true,"item_name":null,"children":[
            {"node":"field","name":"Amount","data_type":{"kind":"numeric","precision":4,"scale":0}}
        ]}
    ]}"#;
    let code = SdtClass::from_kb_object(&sdt_object("RSN03944700", json))
        .expect("decodes")
        .emit();
    // The holder of `Rows` is the root class, so the item class is `SdtRSN03944700_RowsItem`.
    // The SDK creates the collection in the getter on first access (type_SdtRSN03944700.cs gxTpr_Rows).
    assert!(
        code.contains("public GXBaseCollection<SdtRSN03944700_RowsItem> gxTpr_Rows"),
        "{code}"
    );
    assert!(
        code.contains(
            "gxTv_SdtRSN03944700_Rows = new GXBaseCollection<SdtRSN03944700_RowsItem>( context, \"RSN03944700.RowsItem\", \"\");"
        ),
        "{code}"
    );
    assert!(
        code.contains("public class SdtRSN03944700_RowsItem : GxUserType"),
        "{code}"
    );
}

#[test]
fn a_portable_structure_with_an_unknown_field_is_refused() {
    let json = r#"{"fields":[],"extra":1}"#;
    let error = SdtClass::from_kb_object(&sdt_object("Extra", json)).unwrap_err();
    assert!(error.contains("not the portable form"), "{error}");
}

#[test]
fn a_record_field_takes_its_sdt_class_name() {
    let json = r#"{"fields":[
        {"node":"field","name":"Target","data_type":{"kind":"record","schema":"Messages"}}
    ]}"#;
    let code = SdtClass::from_kb_object(&sdt_object("Holder", json))
        .expect("decodes")
        .emit();
    assert!(code.contains("public SdtMessages gxTpr_Target"), "{code}");
    assert!(
        code.contains("gxTv_SdtHolder_Target = new SdtMessages(context);"),
        "{code}"
    );
}

#[test]
fn a_collection_with_a_stored_item_name_names_the_item_as_the_sdk_does() {
    // RCE06829400 (KBTeste), `Rows` with the stored item name `Entry`: the SDK's own text is
    // `gxTv_SdtRCE06829400_Rows = new GXBaseCollection<SdtRCE06829400_Entry>( context, "RCE06829400.Entry", "");`
    // (type_SdtRCE06829400.cs, line 96). The item class and the SDK name both come from the stored item name.
    let json = r#"{"fields":[
        {"node":"level","name":"Rows","collection":true,"item_name":"Entry","children":[
            {"node":"field","name":"Amount","data_type":{"kind":"numeric","precision":4,"scale":0}}
        ]}
    ]}"#;
    let code = SdtClass::from_kb_object(&sdt_object("RCE06829400", json))
        .expect("decodes")
        .emit();
    assert!(
        code.contains("public GXBaseCollection<SdtRCE06829400_Entry> gxTpr_Rows"),
        "{code}"
    );
    assert!(
        code.contains(
            "gxTv_SdtRCE06829400_Rows = new GXBaseCollection<SdtRCE06829400_Entry>( context, \"RCE06829400.Entry\", \"\");"
        ),
        "{code}"
    );
    assert!(
        code.contains("public class SdtRCE06829400_Entry : GxUserType"),
        "{code}"
    );
}

/// A collection root has no class of its own in the SDK. The SDT is its item class `<SDT>_<item>`, with
/// the items as its members (SDK: `listprograms.cs`, `GXBaseCollection<SdtProgramNames_ProgramName>`).
#[test]
fn a_collection_root_is_emitted_as_its_item_class_with_no_root_class() {
    let json = r#"{"fields":[
        {"node":"field","name":"Name","data_type":{"kind":"varchar","length":100}},
        {"node":"field","name":"Link","data_type":{"kind":"varchar","length":1000}}
    ],"root_collection":"ProgramName"}"#;
    let class = SdtClass::from_kb_object(&sdt_object("ProgramNames", json)).expect("decodes");
    assert_eq!(class.class_name(), "SdtProgramNames_ProgramName");
    let code = class.emit();
    assert!(
        code.contains("public class SdtProgramNames_ProgramName : GxUserType"),
        "{code}"
    );
    assert!(code.contains("public string gxTpr_Name"), "{code}");
    assert!(code.contains("public string gxTpr_Link"), "{code}");
    assert!(
        !code.contains("public class SdtProgramNames :"),
        "the root must not be a class of its own:\n{code}"
    );
}

/// A nested level under a collection root is named under the item class, as under any collection item.
#[test]
fn a_level_under_a_collection_root_is_named_under_its_item_class() {
    let json = r#"{"fields":[
        {"node":"level","name":"Child","collection":false,"item_name":null,"children":[
            {"node":"field","name":"Value","data_type":{"kind":"varchar","length":10}}
        ]}
    ],"root_collection":"ProgramName"}"#;
    let class = SdtClass::from_kb_object(&sdt_object("ProgramNames", json)).expect("decodes");
    let code = class.emit();
    assert!(
        code.contains("public class SdtProgramNames_ProgramName_Child : GxUserType"),
        "{code}"
    );
    let levels: Vec<String> = class.level_classes().into_iter().map(|c| c.name).collect();
    assert_eq!(levels, vec!["ProgramNames_ProgramName_Child".to_string()]);
}
