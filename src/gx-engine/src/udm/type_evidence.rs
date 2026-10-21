//! SDK evidence for the stored type codes the decoders map or refuse.
//!
//! Two kinds of fact live here, each with the evidence it rests on (see
//! `docs/udm_type_codes.md` for the full table):
//!
//! - The GeneXus type name the SDK prints for a stored code this engine does not map. Every name is
//!   confirmed by the SDK's own text for the same declarations (SDT `Structure` text, and the
//!   structured `variables` of Procedures) on `KBTeste` (GX18) and `KBTeste16` (GX16). No C# type is
//!   evidenced for the code 255 (a user-defined type), so it stays refused by name. Codes 7, 8, 45,
//!   102 to 105 are mapped in `stored_variables::decode_type` from the SDK's generator mapping.
//! - The length the SDK gives a CHARACTER variable whose stored declaration carries no length. On
//!   both KBs the SDK reports `CHARACTER(20)` for each such declaration (`FilterName` and
//!   `FilterValue` of `AddDeviceTargetFilter` and `RemoveDeviceTargetFilter`, four declarations).

use std::collections::BTreeMap;

/// The GeneXus type name the SDK prints for a stored type code this engine refuses, or `None` for a
/// code this module knows nothing about. The name is evidence of what the SDK declares; it is not a
/// C# type, so it never makes a code mapped.
pub(crate) fn sdk_type_name(code: &str) -> Option<&'static str> {
    match code {
        // Variable `FilterTags` of `SetDeviceTargetMultipleFilters`: the SDK type is a user-defined
        // type (`GX_USRDEFTYP`, stored as `<DataType>255</DataType><Guid>70</Guid>`).
        "255" => Some("GX_USRDEFTYP"),
        _ => None,
    }
}

/// The refusal for a stored type code this engine does not map. It keeps the text the census and the
/// tests match (`type code N is not mapped by this engine`) and names the SDK type when one is known.
pub(crate) fn unmapped_code_message(code: &str) -> String {
    match sdk_type_name(code) {
        Some(name) => format!(
            "type code {code} is not mapped by this engine (SDK type {name}: its C# type is not evidenced)"
        ),
        None => format!("type code {code} is not mapped by this engine"),
    }
}

/// The stored properties of a VARIABLES declaration, with the SDK's length for a CHARACTER that
/// stores none. The default applies only when the row carries no `Length`, `AttMaxLen` or `Decimals`:
/// a row with any of them keeps its own declaration, and any other code is returned unchanged.
pub(crate) fn variable_props(
    code: &str,
    props: &BTreeMap<String, String>,
) -> BTreeMap<String, String> {
    let mut row = props.clone();
    let carries_length = ["Length", "AttMaxLen", "Decimals"]
        .iter()
        .any(|key| props.contains_key(*key));
    if code == "5" && !carries_length {
        row.insert("Length".to_string(), "20".to_string());
    }
    row
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::DataType;
    use crate::udm::stored_variables::decode;

    /// SYNTHETIC, reconstructed from the stored shape the probe read (`gxe-types-probe/var_compare.json`:
    /// `FilterName` of `AddDeviceTargetFilter`, code 5, no `Length`, `AttMaxLen` or `Decimals`). It is not
    /// a byte copy of the stored row; the stored XML was not read in full for this fixture.
    const FILTER_NAME: &str = r#"<Variables><Variable Id="8" Name="FilterName"><Properties><Property><Name>Name</Name><Value>FilterName</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;5&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property></Properties></Variable></Variables>"#;

    fn no_sdt(_: i32) -> Option<String> {
        None
    }

    fn declaration(code: &str, extra: &[(&str, &str)]) -> String {
        let mut props = format!(
            "<Property><Name>Name</Name><Value>V</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_VAR</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;{code}&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property>"
        );
        for (k, v) in extra {
            props.push_str(&format!(
                "<Property><Name>{k}</Name><Value>{v}</Value></Property>"
            ));
        }
        format!(
            "<Variables><Variable Id=\"1\" Name=\"V\"><Properties>{props}</Properties></Variable></Variables>"
        )
    }

    #[test]
    fn a_character_variable_with_no_stored_length_takes_the_sdk_length_of_twenty() {
        let d = decode(FILTER_NAME, &no_sdt, &|_| None, &|_| Ok(None)).unwrap();
        assert_eq!(
            d.variables["FilterName"],
            DataType::Character { length: 20 }
        );
    }

    #[test]
    fn a_character_with_a_stored_length_keeps_it() {
        let xml = declaration("5", &[("Length", "8")]);
        let d = decode(&xml, &no_sdt, &|_| None, &|_| Ok(None)).unwrap();
        assert_eq!(d.variables["V"], DataType::Character { length: 8 });
    }

    #[test]
    fn a_character_with_only_a_max_length_is_still_refused() {
        let xml = declaration("5", &[("AttMaxLen", "30")]);
        let err = decode(&xml, &no_sdt, &|_| None, &|_| Ok(None)).unwrap_err();
        assert!(err.contains("CHARACTER has no stored length"), "{err}");
    }

    #[test]
    fn a_character_that_declares_decimals_is_still_refused() {
        let xml = declaration("5", &[("Decimals", "2")]);
        let err = decode(&xml, &no_sdt, &|_| None, &|_| Ok(None)).unwrap_err();
        assert!(err.contains("CHARACTER declares decimals"), "{err}");
    }

    #[test]
    fn the_default_applies_to_character_only() {
        let props: BTreeMap<String, String> = BTreeMap::new();
        assert_eq!(variable_props("5", &props)["Length"], "20");
        assert!(!variable_props("13", &props).contains_key("Length"));
        assert!(!variable_props("6", &props).contains_key("Length"));
        assert!(!variable_props("4", &props).contains_key("Length"));
    }

    #[test]
    fn every_code_this_engine_refuses_is_refused_by_name_with_its_sdk_type() {
        let expected = [("255", "GX_USRDEFTYP")];
        for (code, name) in expected {
            let xml = declaration(code, &[("Length", "4")]);
            let err = decode(&xml, &no_sdt, &|_| None, &|_| Ok(None)).unwrap_err();
            assert!(
                err.contains(&format!("type code {code} is not mapped by this engine")),
                "{err}"
            );
            assert!(err.contains(name), "{err}");
            assert!(err.contains("C# type is not evidenced"), "{err}");
        }
    }

    #[test]
    fn an_unknown_code_is_refused_without_an_invented_sdk_name() {
        assert_eq!(sdk_type_name("9"), None);
        assert_eq!(
            unmapped_code_message("9"),
            "type code 9 is not mapped by this engine"
        );
    }
}
