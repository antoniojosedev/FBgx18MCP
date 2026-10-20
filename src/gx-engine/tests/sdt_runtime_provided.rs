//! A GeneXus library SDT the KB's runtime assemblies define is never emitted, even when reached: the SDK's
//! generated code uses the runtime class (KBTeste `WWTrn353` reaches `GridState`, and the SDK output holds no
//! `type_SdtGridState.cs`). SYNTHETIC assembly bytes stand in for `NetModel/web/bin/GxClasses.dll`.

use gx_engine::rebuild::runtime_sdt_names_from_kb;

#[test]
fn only_names_whose_class_the_runtime_assemblies_define_are_runtime_provided() {
    let kb = std::env::temp_dir().join(format!("gx_runtime_sdts_{}", std::process::id()));
    let bin = kb.join("NetModel").join("web").join("bin");
    std::fs::create_dir_all(&bin).unwrap();
    std::fs::write(
        bin.join("GxClasses.dll"),
        b"\0SdtGridState\0SdtGridState_InputValuesItem\0SdtMessagesX\0",
    )
    .unwrap();
    let names = runtime_sdt_names_from_kb(&kb, ["GridState", "Messages", "SidebarItems"]);
    assert_eq!(names.into_iter().collect::<Vec<_>>(), ["GridState"]);
    let _ = std::fs::remove_dir_all(&kb);
}

#[test]
fn a_kb_without_runtime_assemblies_provides_nothing() {
    let kb = std::env::temp_dir().join(format!("gx_runtime_sdts_none_{}", std::process::id()));
    assert!(runtime_sdt_names_from_kb(&kb, ["GridState"]).is_empty());
}

#[test]
fn a_class_name_is_found_at_any_offset_including_overlapping_matches() {
    let kb = std::env::temp_dir().join(format!("gx_runtime_sdts_offsets_{}", std::process::id()));
    let bin = kb.join("NetModel").join("web").join("bin");
    std::fs::create_dir_all(&bin).unwrap();
    // `SdtSdtInner\0` holds both `SdtInner` and `SdtSdtInner`; `XSdtTail\0` holds `SdtTail`;
    // `SdtOpen` has no terminator, so it is not a class name.
    std::fs::write(
        bin.join("GxClasses.dll"),
        b"\0SdtSdtInner\0XSdtTail\0SdtOpen",
    )
    .unwrap();
    std::fs::write(bin.join("GxClasses.Web.dll"), b"SdtWebOnly\0").unwrap();
    let names = runtime_sdt_names_from_kb(
        &kb,
        [
            "Inner", "SdtInner", "Tail", "Open", "WebOnly", "Sdt", "Missing",
        ],
    );
    let _ = std::fs::remove_dir_all(&kb);
    assert_eq!(
        names.into_iter().collect::<Vec<_>>(),
        ["Inner", "SdtInner", "Tail", "WebOnly"]
    );
}
