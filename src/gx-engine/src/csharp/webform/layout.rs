//! Structure checks of the stored WebForm layout XML that the plain panel and master page emitters accept.

use super::*;

/// Checks the stored `WEBFORM` is one `MainTable` whose rows hold one empty cell each, and returns
/// the row count. The table shape is matched as a pair: no class with zero rows, or a class with
/// three rows. Any other element, attribute or content refuses the layout.
pub(super) fn check_layout(object: &KbObject) -> Result<usize, String> {
    let text =
        part_text(object, "WEBFORM").ok_or_else(|| "it has no WEBFORM layout".to_string())?;
    let document =
        roxmltree::Document::parse(text).map_err(|e| format!("its WEBFORM is not XML: {e}"))?;
    let root = document.root_element();
    expect(root, "GxMultiForm", &["rootId", "version"])?;
    let form = single_child(root, "Form")?;
    expect(form, "Form", &["id", "type"])?;
    if form.attribute("type") != Some("layout") {
        return Err("its form is not a layout form".to_string());
    }
    let detail = single_child(form, "detail")?;
    expect(detail, "detail", &["PanelType"])?;
    if detail.attribute("PanelType") != Some("Web") {
        return Err("its layout is not a web panel layout".to_string());
    }
    let layout = single_child(detail, "layout")?;
    expect(layout, "layout", &["id"])?;
    let table = single_child(layout, "table")?;
    expect(table, "table", &["id", "controlName", "tableType", "class"])?;
    if table.attribute("controlName") != Some("MainTable")
        || table.attribute("tableType") != Some("Responsive")
    {
        return Err("its table is not the responsive MainTable".to_string());
    }
    let mut rows = 0;
    for row in element_children(table) {
        expect(row, "row", &["id"])?;
        let cell = single_child(row, "cell")?;
        expect(cell, "cell", &["id"])?;
        if element_children(cell).next().is_some() || has_text(cell) {
            return Err("a table cell holds content, which is not emitted yet".to_string());
        }
        rows += 1;
    }
    if has_text(table) || has_text(layout) || has_text(detail) || has_text(form) {
        return Err("its layout holds text outside the table".to_string());
    }
    let has_class = table.attribute("class").is_some();
    match (rows, has_class) {
        (0, false) | (3, true) => Ok(rows),
        (rows, has_class) => Err(format!(
            "its table has {rows} row(s) and {} class, a shape no SDK output has been matched for",
            if has_class { "a" } else { "no" }
        )),
    }
}

pub(super) fn child_text<'a>(node: roxmltree::Node<'a, '_>, tag: &str) -> Option<&'a str> {
    node.children()
        .find(|child| child.has_tag_name(tag))
        .map(|child| child.text().unwrap_or_default())
}

fn element_children<'a, 'input>(
    node: roxmltree::Node<'a, 'input>,
) -> impl Iterator<Item = roxmltree::Node<'a, 'input>> {
    node.children().filter(|child| child.is_element())
}

fn has_text(node: roxmltree::Node<'_, '_>) -> bool {
    node.children()
        .any(|child| child.is_text() && child.text().is_some_and(|t| !t.trim().is_empty()))
}

pub(super) fn expect(
    node: roxmltree::Node<'_, '_>,
    tag: &str,
    attributes: &[&str],
) -> Result<(), String> {
    if !node.has_tag_name(tag) {
        return Err(format!(
            "unexpected element '{}' where '{tag}' belongs",
            node.tag_name().name()
        ));
    }
    if let Some(attribute) = node
        .attributes()
        .find(|attribute| !attributes.contains(&attribute.name()))
    {
        return Err(format!(
            "'{tag}' has attribute '{}', which is not emitted yet",
            attribute.name()
        ));
    }
    Ok(())
}

fn single_child<'a, 'input>(
    node: roxmltree::Node<'a, 'input>,
    tag: &str,
) -> Result<roxmltree::Node<'a, 'input>, String> {
    let children: Vec<_> = element_children(node).collect();
    match children.as_slice() {
        [only] if only.has_tag_name(tag) => Ok(*only),
        _ => Err(format!(
            "'{}' must hold exactly one '{tag}' element",
            node.tag_name().name()
        )),
    }
}

/// Checks the stored `WEBFORM` of a master page is one classed responsive `MainTable` with one row,
/// one cell, and the `ContentHolder` alone in it. Returns the table's class references.
pub(super) fn check_master_layout(object: &KbObject) -> Result<String, String> {
    let text =
        part_text(object, "WEBFORM").ok_or_else(|| "it has no WEBFORM layout".to_string())?;
    let document =
        roxmltree::Document::parse(text).map_err(|e| format!("its WEBFORM is not XML: {e}"))?;
    let root = document.root_element();
    expect(root, "GxMultiForm", &["rootId", "version"])?;
    let form = single_child(root, "Form")?;
    expect(form, "Form", &["id", "type"])?;
    if form.attribute("type") != Some("layout") {
        return Err("its form is not a layout form".to_string());
    }
    let detail = single_child(form, "detail")?;
    expect(detail, "detail", &["PanelType"])?;
    if detail.attribute("PanelType") != Some("Web") {
        return Err("its layout is not a web panel layout".to_string());
    }
    let layout = single_child(detail, "layout")?;
    expect(layout, "layout", &["id"])?;
    let table = single_child(layout, "table")?;
    expect(table, "table", &["id", "controlName", "tableType", "class"])?;
    if table.attribute("controlName") != Some("MainTable")
        || table.attribute("tableType") != Some("Responsive")
    {
        return Err("its table is not the responsive MainTable".to_string());
    }
    let row = single_child(table, "row")?;
    expect(row, "row", &["id"])?;
    let cell = single_child(row, "cell")?;
    expect(cell, "cell", &["id"])?;
    let holder = single_child(cell, "contentholder")?;
    expect(holder, "contentholder", &["controlName"])?;
    if holder.attribute("controlName") != Some("ContentHolder")
        || element_children(holder).next().is_some()
    {
        return Err("its content holder is not the plain ContentHolder".to_string());
    }
    for node in [holder, cell, row, table, layout, detail, form] {
        if has_text(node) {
            return Err("its layout holds text outside the controls".to_string());
        }
    }
    table
        .attribute("class")
        .map(str::to_string)
        .ok_or_else(|| "its MainTable has no class".to_string())
}
