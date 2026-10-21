//! The GeneXus 18 Developer Menu files of a .NET web model (`NetModel/web`).
//!
//! Two kinds of output, classified by evidence:
//!
//! - `DeveloperMenu.xml` is generated. The SDK's `Artech.Generator.DotNetCore` writes it from the
//!   objects it lists for the menu (`DMENU_MAK`, `write_xml_node`, `ObjCls`/`ObjName`/`ObjDesc`/
//!   `ObjLink`, `<?xml-stylesheet ... href="prgs.xsl"?>`). The menu script
//!   `devmenu/developermenu.js` fetches it at runtime (`url: 'DeveloperMenu.xml?'`) and reads those
//!   four elements.
//! - `developermenu.html`, `prgs.xsl` and the `devmenu/` folder are copied verbatim from the install
//!   (`gxnetcore/`). Their MD5s match the KBTeste output, so they are copied, not rendered.
//!
//! Not emitted here: `Developermenu.bld` and the `GX*PRO41.002` files. They are build bookkeeping
//! (the `.bld` lists every compiled object with its folder; the generator writes it through
//! `folder_bldinfo`/`get_folder_bldinfo`), and nothing in the web model or the runtime reads them.
//!
//! The selection of which objects appear in the menu is NOT reproduced here. In KBTeste the menu
//! lists 7 Transactions and 3 WebPanels out of 25 web objects in the build list. The rule is
//! not confirmed, so callers supply the object list and its order.

use std::fs;
use std::io;
use std::path::Path;

/// The menu's XML file, relative to the web model folder.
pub const DEVELOPER_MENU_XML: &str = "DeveloperMenu.xml";
/// The stylesheet named by the XML's `xml-stylesheet` instruction.
pub const DEVELOPER_MENU_XSL: &str = "prgs.xsl";
/// The menu page, copied from the install.
pub const DEVELOPER_MENU_HTML: &str = "developermenu.html";
/// The menu assets folder, copied from the install.
pub const DEVMENU_DIR: &str = "devmenu";

/// `ObjCls` of a Transaction in `DeveloperMenu.xml` (observed in KBTeste).
pub const OBJ_CLS_TRANSACTION: u32 = 0;
/// `ObjCls` of a WebPanel in `DeveloperMenu.xml` (observed in KBTeste).
pub const OBJ_CLS_WEB_PANEL: u32 = 13;

/// One `<Object>` of `DeveloperMenu.xml`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeveloperMenuObject {
    /// The GeneXus object class (`ObjCls`). Only the classes observed in KBTeste are accepted.
    pub obj_cls: u32,
    /// The object name (`ObjName`). Must be an ASCII identifier; `ObjLink` is derived from it.
    pub name: String,
    /// The object description (`ObjDesc`), XML-escaped on output.
    pub description: String,
}

/// Renders `DeveloperMenu.xml` in the SDK's layout: CRLF line endings, three-space indent for the
/// top-level elements and six for an object's fields. `install_uri` is the `<Path>` value, for
/// example `file:///C:/Program Files (x86)/GeneXus/GeneXus18/`, and is written as given.
///
/// `ObjLink` is the lowercase object name plus `.aspx`. Objects keep the order of `objects`.
pub fn developer_menu_xml(
    install_uri: &str,
    objects: &[DeveloperMenuObject],
) -> Result<Vec<u8>, String> {
    let mut out = String::new();
    out.push_str("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n");
    out.push_str("<?xml-stylesheet type=\"text/xsl\" href=\"prgs.xsl\"?>\r\n");
    out.push_str("<Objects>\r\n");
    out.push_str("   <Charset>UTF-8</Charset>\r\n");
    out.push_str(&format!("   <Path>{}</Path>\r\n", xml_escape(install_uri)));
    for object in objects {
        check_object(object)?;
        let link = format!("{}.aspx", object.name.to_ascii_lowercase());
        out.push_str("   <Object>\r\n");
        out.push_str(&format!("      <ObjCls>{}</ObjCls>\r\n", object.obj_cls));
        out.push_str(&format!(
            "      <ObjName>{}</ObjName>\r\n",
            xml_escape(&object.name)
        ));
        out.push_str(&format!(
            "      <ObjDesc>{}</ObjDesc>\r\n",
            xml_escape(&object.description)
        ));
        out.push_str(&format!(
            "      <ObjLink>{}</ObjLink>\r\n",
            xml_escape(&link)
        ));
        out.push_str("   </Object>\r\n");
    }
    out.push_str("</Objects>\r\n");
    Ok(out.into_bytes())
}

/// The static files the install provides for the menu, as `(relative path, bytes)` pairs in a
/// fixed order: `developermenu.html`, `prgs.xsl`, then every file of `devmenu/` sorted by name.
///
/// `gx_netcore` is the install's `gxnetcore` folder. The bytes are copied as they are; nothing is
/// rewritten.
pub fn developer_menu_static_copies(gx_netcore: &Path) -> io::Result<Vec<(String, Vec<u8>)>> {
    let mut copies = vec![
        (
            DEVELOPER_MENU_HTML.to_string(),
            fs::read(gx_netcore.join(DEVELOPER_MENU_HTML))?,
        ),
        (
            DEVELOPER_MENU_XSL.to_string(),
            fs::read(gx_netcore.join(DEVELOPER_MENU_XSL))?,
        ),
    ];
    let dir = gx_netcore.join(DEVMENU_DIR);
    let mut names: Vec<String> = fs::read_dir(&dir)?
        .map(|entry| entry.map(|e| e.file_name().to_string_lossy().into_owned()))
        .collect::<io::Result<_>>()?;
    names.sort();
    for name in names {
        let path = dir.join(&name);
        if path.is_file() {
            copies.push((format!("{DEVMENU_DIR}/{name}"), fs::read(&path)?));
        }
    }
    Ok(copies)
}

fn check_object(object: &DeveloperMenuObject) -> Result<(), String> {
    if object.obj_cls != OBJ_CLS_TRANSACTION && object.obj_cls != OBJ_CLS_WEB_PANEL {
        return Err(format!(
            "developer menu: unsupported ObjCls {} for '{}' (only 0 and 13 are observed)",
            object.obj_cls, object.name
        ));
    }
    let name = &object.name;
    let valid = !name.is_empty()
        && name.chars().next().is_some_and(|c| c.is_ascii_alphabetic())
        && name.chars().all(|c| c.is_ascii_alphanumeric() || c == '_');
    if !valid {
        return Err(format!(
            "developer menu: '{name}' is not an ASCII object identifier"
        ));
    }
    Ok(())
}

/// Escapes the four XML characters the SDK's generator replaces (`&` first, then `"`, `<`, `>`).
/// The literals `&amp;`, `&quot;`, `&lt;` and `&gt;` are in `Artech.Generator.DotNetCore.dll`.
fn xml_escape(text: &str) -> String {
    text.replace('&', "&amp;")
        .replace('"', "&quot;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}
