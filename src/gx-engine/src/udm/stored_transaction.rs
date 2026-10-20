//! Decodes the stored structure of a native Knowledge Base Transaction into the portable
//! [`TransactionStructure`] that the physical-table derivation reads.
//!
//! The served `STRUCTURE` part is GeneXus XML, once `udm::decoder::decode_blob` has removed the 11-byte
//! envelope and the gzip stream. It is one root `<Level Id="1">` whose children are a `<Properties />`
//! element and one `<Attribute Id="N" IsKey="True|False">` per attribute of the Transaction, in the order
//! the Knowledge Base stores them. Each `Id` is the entity id of an Attribute object (EntityType 27 in the
//! stored KBs). The reader supplies two things per `Id`, through [`Lookup`]: the attribute's model name,
//! and its stored type, resolved from the Attribute's own row (`stored_variables::resolve_attribute_type`). The
//! root level takes the Transaction's name, as the portable form does, so the root table is named after
//! the Transaction.
//!
//! Anything this module does not prove is refused by name, with the Transaction and the element: a nested
//! `<Level>`, stored properties under `<Properties>`, an unknown element, an `Id` with no stored name or
//! type, or an `IsKey` other than `True` or `False`. Nothing is guessed and no attribute is dropped.
//!
//! Evidence for the shape: the SDK's own output for LiveMoveAttribute (`NetModel/web/livemoveattribute.cs`)
//! names the physical table `[LiveMoveAttribute]`, the key `[LiveMoveAttributeId]` (`Int16`, length 4) and
//! the columns `[LiveMoveAttributeCode]` and `[LiveMoveAttributeDescription]` (`NChar(20)`). The stored
//! blobs of all eight KBTeste Transactions have this one shape.

use roxmltree::{Document, Node, ParsingOptions};

use crate::model::DataType;
use crate::transaction::{
    KeyReference, TransactionAttributeRef, TransactionLevel, TransactionStructure,
};

/// The most attributes one Transaction may carry. The stored Transactions carry a handful.
pub(crate) const MAX_ATTRIBUTES: usize = 4096;

/// The largest structure part this module reads.
const MAX_XML_BYTES: usize = 8 * 1024 * 1024;

/// What the decoder needs to know about the Knowledge Base for one Transaction's attributes.
pub struct Lookup<'a> {
    /// The model name of the Attribute object with the given entity id.
    pub attribute_name: &'a dyn Fn(i32) -> Option<String>,
    /// The stored type of the Attribute object with the given entity id, or why it has none. A primary key whose
    /// row stores a `SuperTypeKey` and no type gets the type it inherits from the key it names.
    pub attribute_type: &'a dyn Fn(i32) -> Result<DataType, String>,
    /// The name of the attribute that the Attribute object's `SuperTypeKey` names, or `None` when its row names
    /// none. An error says why the name cannot be given.
    pub attribute_super_key: &'a dyn Fn(i32) -> Result<Option<String>, String>,
}

/// Whether a stored `STRUCTURE` text is the native GeneXus `Level` document that [`decode`] reads. A
/// well-formed document with another root is not, and is held unchanged. Text that is not well formed is
/// treated as native, so [`decode`] refuses it by name rather than letting it pass.
pub fn is_native_level(xml: &str) -> bool {
    match Document::parse(xml) {
        Ok(document) => document.root_element().tag_name().name() == "Level",
        Err(_) => true,
    }
}

/// Decodes one Transaction's served `STRUCTURE` part. `transaction` is the Transaction's name: the root
/// level takes it, and every error names it.
pub fn decode(
    xml: &str,
    transaction: &str,
    lookup: &Lookup<'_>,
) -> Result<TransactionStructure, String> {
    if xml.len() > MAX_XML_BYTES {
        return Err(format!(
            "STRUCTURE of '{transaction}' is {} bytes, above the {MAX_XML_BYTES}-byte limit",
            xml.len()
        ));
    }
    // The declaration says `encoding="utf-16"`, but the stored text is ASCII and roxmltree reads a
    // `&str` whatever the declared encoding is, so the text is parsed as it is stored.
    let document = Document::parse_with_options(
        xml,
        ParsingOptions {
            allow_dtd: false,
            ..ParsingOptions::default()
        },
    )
    .map_err(|e| format!("STRUCTURE of '{transaction}' is not well formed XML: {e}"))?;
    let root = document.root_element();
    if root.tag_name().name() != "Level" {
        return Err(format!(
            "STRUCTURE of '{transaction}' root is <{}>, expected <Level>",
            root.tag_name().name()
        ));
    }
    if root.attribute("Id") != Some("1") {
        return Err(format!(
            "STRUCTURE of '{transaction}' root level Id is {:?}, expected \"1\"",
            root.attribute("Id")
        ));
    }
    let mut attributes = Vec::new();
    let mut key_references = Vec::new();
    for child in root.children().filter(|n| n.is_element()) {
        match child.tag_name().name() {
            "Properties" => no_stored_content(child, transaction, "the root level")?,
            "Attribute" => {
                if attributes.len() == MAX_ATTRIBUTES {
                    return Err(format!(
                        "STRUCTURE of '{transaction}' exceeds {MAX_ATTRIBUTES} attributes"
                    ));
                }
                let (attribute, references) = attribute(child, transaction, lookup)?;
                if let Some(references) = references {
                    key_references.push(KeyReference {
                        attribute: attribute.name.clone(),
                        references,
                    });
                }
                attributes.push(attribute);
            }
            "Level" => {
                return Err(format!(
                    "STRUCTURE of '{transaction}' has a nested <Level>: its nested levels are not decoded, so the Transaction is refused"
                ));
            }
            other => {
                return Err(format!(
                    "STRUCTURE of '{transaction}' has an unknown element <{other}>"
                ));
            }
        }
    }
    if attributes.is_empty() {
        return Err(format!("STRUCTURE of '{transaction}' has no attributes"));
    }
    let mut structure = TransactionStructure::new(TransactionLevel {
        name: transaction.to_string(),
        description: None,
        attributes,
        levels: Vec::new(),
    });
    structure.key_references = key_references;
    structure
        .validate()
        .map_err(|e| format!("STRUCTURE of '{transaction}': {e}"))?;
    Ok(structure)
}

/// One `<Attribute Id IsKey>` element, as a portable attribute reference with its stored type, and the name of
/// the key it references through `SuperTypeKey`, if its row names one.
fn attribute(
    node: Node<'_, '_>,
    transaction: &str,
    lookup: &Lookup<'_>,
) -> Result<(TransactionAttributeRef, Option<String>), String> {
    let id_text = node
        .attribute("Id")
        .ok_or_else(|| format!("an attribute of '{transaction}' has no Id"))?;
    let id: i32 = id_text
        .parse()
        .map_err(|_| format!("an attribute of '{transaction}' has a non-numeric Id '{id_text}'"))?;
    let is_key = match node.attribute("IsKey") {
        Some("True") => true,
        Some("False") => false,
        other => {
            return Err(format!(
                "attribute {id} of '{transaction}' has IsKey {other:?}, expected True or False"
            ));
        }
    };
    for child in node.children().filter(|n| n.is_element()) {
        if child.tag_name().name() != "Properties" {
            return Err(format!(
                "attribute {id} of '{transaction}' has an unknown element <{}>",
                child.tag_name().name()
            ));
        }
        no_stored_content(child, transaction, &format!("attribute {id}"))?;
    }
    let name = (lookup.attribute_name)(id)
        .ok_or_else(|| format!("attribute {id} of '{transaction}' has no stored name"))?;
    let data_type = (lookup.attribute_type)(id)
        .map_err(|e| format!("attribute {id} ('{name}') of '{transaction}': {e}"))?;
    let references = (lookup.attribute_super_key)(id)
        .map_err(|e| format!("attribute {id} ('{name}') of '{transaction}': {e}"))?;
    let attribute = TransactionAttributeRef {
        name,
        is_key,
        data_type: Some(data_type),
        formula: None,
        is_nullable: false,
        is_foreign_key: false,
    };
    Ok((attribute, references))
}

/// A `<Properties>` element must carry nothing. The stored properties of a level or an attribute are not
/// decoded here, so content there is refused rather than ignored.
fn no_stored_content(node: Node<'_, '_>, transaction: &str, owner: &str) -> Result<(), String> {
    let has_content = node
        .children()
        .any(|c| c.is_element() || c.text().is_some_and(|t| !t.trim().is_empty()));
    if has_content {
        return Err(format!(
            "{owner} of '{transaction}' has stored properties, which are not decoded"
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    const LIVE_MOVE_ATTRIBUTE: &str = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Properties /><Attribute Id="27" IsKey="True"><Properties /></Attribute><Attribute Id="28" IsKey="False"><Properties /></Attribute><Attribute Id="29" IsKey="False"><Properties /></Attribute></Level>"#;

    fn names(id: i32) -> Option<String> {
        match id {
            27 => Some("LiveMoveAttributeId".into()),
            28 => Some("LiveMoveAttributeCode".into()),
            29 => Some("LiveMoveAttributeDescription".into()),
            _ => None,
        }
    }

    fn types(id: i32) -> Result<DataType, String> {
        match id {
            27 => Ok(DataType::Numeric {
                precision: 4,
                scale: 0,
            }),
            28 | 29 => Ok(DataType::Character { length: 20 }),
            _ => Err("no stored type".into()),
        }
    }

    fn no_super_keys(_id: i32) -> Result<Option<String>, String> {
        Ok(None)
    }

    fn decode_with(xml: &str) -> Result<TransactionStructure, String> {
        let lookup = Lookup {
            attribute_name: &names,
            attribute_type: &types,
            attribute_super_key: &no_super_keys,
        };
        decode(xml, "LiveMoveAttribute", &lookup)
    }

    /// The shape of Plan070Trn: one key, attribute 27 (`Plan070TrnId`), whose row names attribute 28 through
    /// `SuperTypeKey` (`Plan071TrnId`). Its type is the one the reader inherits, here NUMERIC(4,0).
    const KEY_WITH_LINK: &str = r#"<?xml version="1.0" encoding="utf-16"?><Level Id="1"><Properties /><Attribute Id="27" IsKey="True"><Properties /></Attribute></Level>"#;

    fn plan_names(id: i32) -> Option<String> {
        match id {
            27 => Some("Plan070TrnId".into()),
            28 => Some("Plan071TrnId".into()),
            _ => None,
        }
    }

    fn plan_types(id: i32) -> Result<DataType, String> {
        match id {
            27 | 28 => Ok(DataType::Numeric {
                precision: 4,
                scale: 0,
            }),
            other => Err(format!("no stored type for attribute {other}")),
        }
    }

    fn plan_link(id: i32) -> Result<Option<String>, String> {
        match id {
            27 => Ok(Some("Plan071TrnId".into())),
            _ => Ok(None),
        }
    }

    #[test]
    fn a_key_whose_row_names_another_key_records_the_link_on_the_structure() {
        let lookup = Lookup {
            attribute_name: &plan_names,
            attribute_type: &plan_types,
            attribute_super_key: &plan_link,
        };
        let structure = decode(KEY_WITH_LINK, "Plan070Trn", &lookup).expect("decodes");
        assert_eq!(
            structure.key_references,
            vec![KeyReference {
                attribute: "Plan070TrnId".into(),
                references: "Plan071TrnId".into(),
            }]
        );
        assert!(structure.root.attributes[0].is_key);
    }

    #[test]
    fn a_key_with_no_link_records_no_key_reference() {
        let structure = decode_with(LIVE_MOVE_ATTRIBUTE).expect("decodes");
        assert!(structure.key_references.is_empty());
    }

    #[test]
    fn a_link_on_a_non_key_attribute_refuses_the_transaction() {
        // Attribute 28 is the only non-key, and it names a key. The Transaction keeps its own key (27), so the
        // refusal is the link's, not the missing-key check that runs first on a level with no key at all.
        let names = |id: i32| -> Option<String> {
            match id {
                27 => Some("Plan070TrnId".into()),
                28 => Some("Plan070Other".into()),
                _ => None,
            }
        };
        let links = |id: i32| -> Result<Option<String>, String> {
            Ok(match id {
                28 => Some("Plan071TrnId".into()),
                _ => None,
            })
        };
        let lookup = Lookup {
            attribute_name: &names,
            attribute_type: &plan_types,
            attribute_super_key: &links,
        };
        let xml = KEY_WITH_LINK.replace(
            "</Level>",
            "<Attribute Id=\"28\" IsKey=\"False\"><Properties /></Attribute></Level>",
        );
        let error = decode(&xml, "Plan070Trn", &lookup).unwrap_err();
        assert!(
            error.contains("key reference on 'Plan070Other', which is not a key attribute"),
            "{error}"
        );
    }

    #[test]
    fn a_link_to_the_attributes_own_name_refuses_the_transaction() {
        let lookup = Lookup {
            attribute_name: &plan_names,
            attribute_type: &plan_types,
            attribute_super_key: &|_id: i32| Ok(Some("Plan070TrnId".to_string())),
        };
        let error = decode(KEY_WITH_LINK, "Plan070Trn", &lookup).unwrap_err();
        assert!(error.contains("references its own attribute"), "{error}");
    }

    #[test]
    fn a_key_whose_type_cannot_be_inherited_refuses_the_transaction_naming_it() {
        let lookup = Lookup {
            attribute_name: &plan_names,
            attribute_type: &|id: i32| Err(format!("its SuperTypeKey chain is cyclic at {id}")),
            attribute_super_key: &plan_link,
        };
        let error = decode(KEY_WITH_LINK, "Plan070Trn", &lookup).unwrap_err();
        assert!(
            error.contains(
                "attribute 27 ('Plan070TrnId') of 'Plan070Trn': its SuperTypeKey chain is cyclic"
            ),
            "{error}"
        );
    }

    #[test]
    fn the_root_level_takes_the_transaction_name_and_the_attributes_keep_their_order() {
        let structure = decode_with(LIVE_MOVE_ATTRIBUTE).expect("the stored shape decodes");
        assert_eq!(structure.root.name, "LiveMoveAttribute");
        let attributes: Vec<(&str, bool, Option<&DataType>)> = structure
            .root
            .attributes
            .iter()
            .map(|a| (a.name.as_str(), a.is_key, a.data_type.as_ref()))
            .collect();
        assert_eq!(
            attributes,
            vec![
                (
                    "LiveMoveAttributeId",
                    true,
                    Some(&DataType::Numeric {
                        precision: 4,
                        scale: 0
                    })
                ),
                (
                    "LiveMoveAttributeCode",
                    false,
                    Some(&DataType::Character { length: 20 })
                ),
                (
                    "LiveMoveAttributeDescription",
                    false,
                    Some(&DataType::Character { length: 20 })
                ),
            ]
        );
        assert!(structure.root.levels.is_empty());
    }

    #[test]
    fn an_attribute_without_a_stored_name_refuses_the_transaction_by_its_id() {
        let xml = LIVE_MOVE_ATTRIBUTE.replace("Id=\"29\"", "Id=\"99\"");
        let error = decode_with(&xml).unwrap_err();
        assert!(
            error.contains("attribute 99 of 'LiveMoveAttribute' has no stored name"),
            "{error}"
        );
    }

    #[test]
    fn an_attribute_without_a_stored_type_refuses_the_transaction_naming_it() {
        let lookup = Lookup {
            attribute_name: &names,
            attribute_type: &|id: i32| {
                if id == 28 {
                    Err("its row stores no ATTCUSTOMTYPE".into())
                } else {
                    types(id)
                }
            },
            attribute_super_key: &no_super_keys,
        };
        let error = decode(LIVE_MOVE_ATTRIBUTE, "LiveMoveAttribute", &lookup).unwrap_err();
        assert!(
            error.contains(
                "attribute 28 ('LiveMoveAttributeCode') of 'LiveMoveAttribute': its row stores no ATTCUSTOMTYPE"
            ),
            "{error}"
        );
    }

    #[test]
    fn a_nested_level_is_refused_not_flattened() {
        let xml = LIVE_MOVE_ATTRIBUTE.replace(
            "</Level>",
            "<Level Id=\"2\"><Attribute Id=\"28\" IsKey=\"False\"/></Level></Level>",
        );
        let error = decode_with(&xml).unwrap_err();
        assert!(error.contains("has a nested <Level>"), "{error}");
    }

    #[test]
    fn an_is_key_value_other_than_true_or_false_is_refused() {
        let xml = LIVE_MOVE_ATTRIBUTE.replace("IsKey=\"False\"", "IsKey=\"Maybe\"");
        let error = decode_with(&xml).unwrap_err();
        assert!(
            error.contains("attribute 28 of 'LiveMoveAttribute' has IsKey"),
            "{error}"
        );
    }

    #[test]
    fn stored_properties_are_refused_not_ignored() {
        let xml = LIVE_MOVE_ATTRIBUTE.replace(
            "<Attribute Id=\"28\" IsKey=\"False\"><Properties />",
            "<Attribute Id=\"28\" IsKey=\"False\"><Properties><Property>x</Property></Properties>",
        );
        let error = decode_with(&xml).unwrap_err();
        assert!(
            error.contains("attribute 28 of 'LiveMoveAttribute' has stored properties"),
            "{error}"
        );
    }

    #[test]
    fn a_root_that_is_not_a_level_or_a_non_numeric_id_is_refused() {
        let error = decode_with("<Trn Id=\"1\"/>").unwrap_err();
        assert!(error.contains("root is <Trn>, expected <Level>"), "{error}");
        let error = decode_with(&LIVE_MOVE_ATTRIBUTE.replace("Id=\"27\"", "Id=\"x\"")).unwrap_err();
        assert!(error.contains("non-numeric Id 'x'"), "{error}");
    }

    #[test]
    fn only_a_native_level_document_is_decoded_and_other_documents_are_held_unchanged() {
        assert!(is_native_level(LIVE_MOVE_ATTRIBUTE));
        // The in-memory form the UDM round trip stores: a well-formed document with another root.
        assert!(!is_native_level(
            r#"<Structure><Level Name="Customer"><Attribute Name="CustomerId" Key="True"/></Level></Structure>"#
        ));
        // Not well formed: treated as native, so it is refused rather than passed through.
        assert!(is_native_level("<Level Id=\"1\">"));
    }

    #[test]
    fn a_structure_with_no_key_is_refused_by_the_portable_validation() {
        let xml = LIVE_MOVE_ATTRIBUTE.replace("IsKey=\"True\"", "IsKey=\"False\"");
        let error = decode_with(&xml).unwrap_err();
        assert!(
            error.contains("STRUCTURE of 'LiveMoveAttribute'")
                && error.contains("must have at least one key attribute"),
            "{error}"
        );
    }
}
