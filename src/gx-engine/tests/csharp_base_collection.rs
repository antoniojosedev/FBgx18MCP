//! A variable of an SDT whose root is a collection (ListPrograms' `ProgramNames`) is the SDK's
//! `GXBaseCollection<..>`. The SDK declares it on the out parameter, allocates it on entry to `execute` and
//! `executeSubmit`, and allocates it in `initialize` (`general/ui/listprograms.cs`, lines 39-41, 53-55, 64, 108).

use gx_engine::csharp::{CsharpParam, ProcedureClass, to_csharp_var_name};
use gx_engine::model::{DataType, ParamMode};

/// The SDK's constructor for the collection, with its item name and namespace (listprograms.cs line 41).
const ALLOCATION: &str =
    "new GXBaseCollection<SdtProgramNames_ProgramName>( context, \"ProgramName\", \"KBTeste\")";

fn list_programs() -> ProcedureClass {
    let names = DataType::BaseCollection {
        schema: "ProgramNames_ProgramName".into(),
        item: "ProgramName".into(),
        namespace: "KBTeste".into(),
    };
    ProcedureClass::new("ListPrograms")
        .add_param(CsharpParam {
            name: "ProgramNames".into(),
            data_type: names.clone(),
            mode: ParamMode::Out,
            param_name: "aP0_ProgramNames".into(),
            var_name: to_csharp_var_name("ProgramNames"),
        })
        .add_variable("ProgramNames", names)
}

#[test]
fn new_assignment_allocates_the_declared_root_collection() {
    let mut procedure = list_programs();
    procedure.body = vec![gx_engine::frontend::Stmt::Assign(
        "ProgramNames".into(),
        gx_engine::frontend::Expr::Call("NEW".into(), vec![]),
    )];
    let code = procedure.emit_checked().expect("emit typed allocation");
    let body = code
        .split("protected override void ExecutePrivate")
        .nth(1)
        .unwrap();
    assert!(
        body.contains(&format!("AV_ProgramNames = {ALLOCATION};")),
        "{code}"
    );
    assert!(!body.contains("new Object()"), "{code}");
}

#[test]
fn collection_allocation_refuses_invalid_item_class_and_literal_values() {
    for (schema, item, namespace) in [
        ("Bad<Class>", "Item", "KB"),
        ("Probe_Item", "Bad\"Item", "KB"),
        ("Probe_Item", "Item", "Bad\"Namespace"),
        ("Probe_Item", "Item", "Bad\nNamespace"),
        ("Probe_Item", "Item", "Bad\0Namespace"),
        ("Probe_Item", "Bad\u{2028}Item", "KB"),
        ("Probe_Item", "Item", "Bad\u{2028}Namespace"),
        ("Probe_Item", "Bad\u{2029}Item", "KB"),
        ("Probe_Item", "Item", "Bad\u{2029}Namespace"),
    ] {
        let data_type = DataType::BaseCollection {
            schema: schema.into(),
            item: item.into(),
            namespace: namespace.into(),
        };
        for procedure in [
            ProcedureClass::new("InvalidCollection").add_variable("Items", data_type.clone()),
            ProcedureClass::new("InvalidCollection").add_param(CsharpParam {
                name: "Items".into(),
                data_type: data_type.clone(),
                mode: ParamMode::Out,
                param_name: "aP0_Items".into(),
                var_name: to_csharp_var_name("Items"),
            }),
        ] {
            let error = procedure
                .emit_checked()
                .expect_err("invalid allocation must be refused");
            assert!(
                error.contains("Items") && error.contains("collection"),
                "{error}"
            );
        }
    }
}

#[test]
fn an_out_collection_parameter_is_declared_as_the_sdk_declares_it() {
    let code = list_programs().emit_checked().expect("emits");
    assert!(
        code.contains("out GXBaseCollection<SdtProgramNames_ProgramName> aP0_ProgramNames"),
        "{code}"
    );
}

#[test]
fn an_out_collection_parameter_is_allocated_on_entry_to_execute_and_submit_as_the_sdk_does() {
    let code = list_programs().emit_checked().expect("emits");
    let statement = format!(
        "this.{} = {} ;",
        to_csharp_var_name("ProgramNames"),
        ALLOCATION
    );
    assert_eq!(
        code.matches(&statement).count(),
        2,
        "execute and executeSubmit each allocate the out collection: {code}"
    );
}

#[test]
fn a_collection_is_allocated_in_initialize_as_the_sdk_allocates_it() {
    let code = list_programs().emit_checked().expect("emits");
    let statement = format!("{} = {};", to_csharp_var_name("ProgramNames"), ALLOCATION);
    assert_eq!(
        code.matches(&statement).count(),
        1,
        "initialize allocates the collection once: {code}"
    );
}
