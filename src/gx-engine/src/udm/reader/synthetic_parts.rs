//! Synthetic parts the reader derives from stored properties rather than from a stored part.

use crate::udm::stored_variables;

/// The synthetic part holding the stored `Id` of each variable of a Procedure, by name, as JSON.
pub const VARIABLE_IDS_PART: &str = "VARIABLE_IDS";

/// The synthetic part holding a Transaction web form's `IsDefault` flag (`true` or `false`).
pub const WEBFORM_DEFAULT_PART: &str = "WEBFORM_DEFAULT";

/// The synthetic part holding the stored row (`<Properties>`) of an enumerated Domain of the model.
pub const DOMAIN_ENUM_PART: &str = "DOMAIN_ENUM";

/// Whether a stored Domain row is an enumeration of the model: it stores `IDEnumDefinedValues` and is not
/// a library interface (`ObjectIsInterface` True), whose values class the GeneXus runtime ships.
pub(super) fn enum_domain_row(properties: &str) -> Result<bool, String> {
    if stored_variables::named_property(properties, "IDEnumDefinedValues")?.is_none() {
        return Ok(false);
    }
    Ok(
        stored_variables::named_property(properties, "ObjectIsInterface")?.as_deref()
            != Some("True"),
    )
}

/// The `IsDefault` property of stored web-form properties, when it is stored as `True` or `False`.
pub(super) fn webform_is_default(properties: &str) -> Option<bool> {
    let document = roxmltree::Document::parse(properties).ok()?;
    let property = document.descendants().find(|node| {
        node.has_tag_name("Property")
            && node
                .children()
                .find(|c| c.has_tag_name("Name"))
                .and_then(|c| c.text())
                == Some("IsDefault")
    })?;
    match property
        .children()
        .find(|c| c.has_tag_name("Value"))
        .and_then(|c| c.text())
    {
        Some("True") => Some(true),
        Some("False") => Some(false),
        _ => None,
    }
}

#[cfg(test)]
mod webform_default_tests {
    use super::webform_is_default;

    #[test]
    fn the_stored_is_default_flag_is_read_and_anything_else_is_not() {
        let p = |v: &str| {
            format!(
                "<Properties><Property><Name>IsDefault</Name><Value>{v}</Value></Property></Properties>"
            )
        };
        assert_eq!(webform_is_default(&p("True")), Some(true));
        assert_eq!(webform_is_default(&p("False")), Some(false));
        assert_eq!(webform_is_default(&p("maybe")), None);
        assert_eq!(webform_is_default("<Properties/>"), None);
        assert_eq!(webform_is_default("not xml"), None);
    }
}

#[cfg(test)]
mod enum_domain_row_tests {
    use super::enum_domain_row;

    const VALUES: &str = "<Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;Rows&lt;/Name&gt;&lt;Description&gt;Rows&lt;/Description&gt;&lt;Value&gt;10&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property>";

    fn row(extra: &str) -> String {
        format!(
            "<Properties><Property><Name>Name</Name><Value>Page</Value></Property>{extra}<Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"
        )
    }

    #[test]
    fn only_an_enumeration_of_the_model_has_a_values_class() {
        // KBTeste's General.Page: an enumeration with no interface flag.
        assert_eq!(enum_domain_row(&row(VALUES)), Ok(true));
        // A library enumeration (`ObjectIsInterface` True): the GeneXus runtime ships its class.
        let interface = format!(
            "{VALUES}<Property><Name>ObjectIsInterface</Name><Value>True</Value></Property>"
        );
        assert_eq!(enum_domain_row(&row(&interface)), Ok(false));
        // A Domain that is not an enumeration.
        assert_eq!(enum_domain_row(&row("")), Ok(false));
        // A repeated property is an error, never a guess.
        assert!(enum_domain_row(&row(&format!("{VALUES}{VALUES}"))).is_err());
    }
}
