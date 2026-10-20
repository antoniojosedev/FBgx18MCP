//! Guards for enumerated domains (Domain rows that carry `IDEnumDefinedValues`).
//!
//! An enumeration is refused by `stored_variables::domain_data_type` unless its stored row declares
//! a type (`ATTCUSTOMTYPE`) with the length that type needs. The SDK's own resolved type for an
//! enumeration with no stored type is not proven here (see `docs/udm_enumerated_domains.md`), so
//! such a domain must stay refused. These tests pin that for each stored shape that decides the
//! refusal, with the rows copied verbatim from `GX_KB_KBTeste` (GX18, ModelId 1, active version) and
//! `GX_KB_KBTeste16` (GX16). The rows were read with SELECT only. The census and the mutation checks
//! that show these guards fail when a type is mapped are in `docs/udm_enumerated_domains.md`.

use super::stored_variables::domain_data_type;
use crate::model::DataType;
/// ProgressIndicatorType: typed NUMERIC, Length 1 (enumeration with a stored type). Active-version row, verbatim.
const GX18_PROGRESS_INDICATOR_TYPE: &str = r##"<Properties><Property><Name>Name</Name><Value>ProgressIndicatorType</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>1</Value></Property><Property><Name>AttMaxLen</Name><Value>1</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;Indeterminate&lt;/Name&gt;&lt;Description&gt;Indeterminate&lt;/Description&gt;&lt;Value&gt;0&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Determinate&lt;/Name&gt;&lt;Description&gt;Determinate&lt;/Description&gt;&lt;Value&gt;1&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
/// AlertType: typed VARCHAR, Length 100 (text enumeration with a stored type). Active-version row, verbatim.
const GX18_ALERT_TYPE: &str = r##"<Properties><Property><Name>Name</Name><Value>AlertType</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;13&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>100</Value></Property><Property><Name>AttMaxLen</Name><Value>100</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;info&lt;/Name&gt;&lt;Description&gt;Info&lt;/Description&gt;&lt;Value&gt;info&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;warning&lt;/Name&gt;&lt;Description&gt;Warning&lt;/Description&gt;&lt;Value&gt;warning&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;success&lt;/Name&gt;&lt;Description&gt;Success&lt;/Description&gt;&lt;Value&gt;success&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;error&lt;/Name&gt;&lt;Description&gt;Error&lt;/Description&gt;&lt;Value&gt;error&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>20</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
/// NotificationBadgeType: no ATTCUSTOMTYPE, no Length. Active-version row, verbatim.
const GX18_NOTIFICATION_BADGE_TYPE: &str = r##"<Properties><Property><Name>Name</Name><Value>NotificationBadgeType</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;None&lt;/Name&gt;&lt;Description&gt;None&lt;/Description&gt;&lt;Value&gt;1&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;SetTo&lt;/Name&gt;&lt;Description&gt;Set To&lt;/Description&gt;&lt;Value&gt;2&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Increase&lt;/Name&gt;&lt;Description&gt;Increase&lt;/Description&gt;&lt;Value&gt;3&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>17</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
/// MessageTypes: no ATTCUSTOMTYPE, Length 2, Decimals 0. Active-version row, verbatim.
const GX18_MESSAGE_TYPES: &str = r##"<Properties><Property><Name>Name</Name><Value>MessageTypes</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>Length</Name><Value>2</Value></Property><Property><Name>Decimals</Name><Value>0</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;Warning&lt;/Name&gt;&lt;Description&gt;Warning&lt;/Description&gt;&lt;Value&gt;0&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Error&lt;/Name&gt;&lt;Description&gt;Error&lt;/Description&gt;&lt;Value&gt;1&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Info&lt;/Name&gt;&lt;Description&gt;Info&lt;/Description&gt;&lt;Value&gt;2&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Debug&lt;/Name&gt;&lt;Description&gt;Debug&lt;/Description&gt;&lt;Value&gt;3&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>ControlType</Name><Value>Combo Box</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
/// MapType: no ATTCUSTOMTYPE, no Length. Active-version row, verbatim.
const GX18_MAP_TYPE: &str = r##"<Properties><Property><Name>Name</Name><Value>MapType</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;Standard&lt;/Name&gt;&lt;Description&gt;Standard&lt;/Description&gt;&lt;Value&gt;1&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Satellite&lt;/Name&gt;&lt;Description&gt;Satellite&lt;/Description&gt;&lt;Value&gt;2&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Hybrid&lt;/Name&gt;&lt;Description&gt;Hybrid&lt;/Description&gt;&lt;Value&gt;3&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>4</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
/// TargetType: typed NUMERIC (code 4) with no Length. Active-version row, verbatim.
const GX18_TARGET_TYPE: &str = r##"<Properties><Property><Name>Name</Name><Value>TargetType</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;Everybody&lt;/Name&gt;&lt;Description&gt;Everybody&lt;/Description&gt;&lt;Value&gt;1&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Devices&lt;/Name&gt;&lt;Description&gt;Devices&lt;/Description&gt;&lt;Value&gt;2&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Groups&lt;/Name&gt;&lt;Description&gt;Groups&lt;/Description&gt;&lt;Value&gt;3&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Targets&lt;/Name&gt;&lt;Description&gt;Targets&lt;/Description&gt;&lt;Value&gt;4&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>17</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
/// PushNotificationPriority: typed CHARACTER (code 5) with no Length. Active-version row, verbatim.
const GX18_PUSH_NOTIFICATION_PRIORITY: &str = r##"<Properties><Property><Name>Name</Name><Value>PushNotificationPriority</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;5&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;High&lt;/Name&gt;&lt;Description&gt;High&lt;/Description&gt;&lt;Value&gt;High&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Normal&lt;/Name&gt;&lt;Description&gt;Normal&lt;/Description&gt;&lt;Value&gt;Normal&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
/// CameraAPIQuality: typed NUMERIC with Length 1, but a Description the Domain allowlist does not name.
const GX18_CAMERA_API_QUALITY: &str = r##"<Properties><Property><Name>Name</Name><Value>CameraAPIQuality</Value></Property><Property><Name>Description</Name><Value>Camera API Quality</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>ATTCUSTOMTYPE</Name><Value>&lt;AttCustomType&gt;&lt;DataType&gt;4&lt;/DataType&gt;&lt;/AttCustomType&gt;</Value></Property><Property><Name>Length</Name><Value>1</Value></Property><Property><Name>AttMaxLen</Name><Value>1</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;Low&lt;/Name&gt;&lt;Description&gt;Low&lt;/Description&gt;&lt;Value&gt;3&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Medium&lt;/Name&gt;&lt;Description&gt;Medium&lt;/Description&gt;&lt;Value&gt;5&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;High&lt;/Name&gt;&lt;Description&gt;High&lt;/Description&gt;&lt;Value&gt;7&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>3</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
/// SmartDeviceType: no ATTCUSTOMTYPE, Length 1, AttMaxLen 1, no Decimals. Active-version row, verbatim.
const GX16_SMART_DEVICE_TYPE: &str = r##"<Properties><Property><Name>Name</Name><Value>SmartDeviceType</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property><Property><Name>Length</Name><Value>1</Value></Property><Property><Name>AttMaxLen</Name><Value>1</Value></Property><Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;iOS&lt;/Name&gt;&lt;Description&gt;iOS&lt;/Description&gt;&lt;Value&gt;0&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Android&lt;/Name&gt;&lt;Description&gt;Android&lt;/Description&gt;&lt;Value&gt;1&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Blackberry&lt;/Name&gt;&lt;Description&gt;Blackberry&lt;/Description&gt;&lt;Value&gt;2&lt;/Value&gt;&lt;/EnumValue&gt;&lt;EnumValue&gt;&lt;Name&gt;Windows&lt;/Name&gt;&lt;Description&gt;Windows&lt;/Description&gt;&lt;Value&gt;3&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>FolderId</Name><Value>2</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"##;
/// A Domain row with the given stored properties, in the stored `<Properties>` form. The
/// enumeration values are one entry: the type rules never read them.
fn enumeration_row(extra: &str) -> String {
    format!(
        "<Properties><Property><Name>Name</Name><Value>SyntheticEnum</Value></Property><Property><Name>OBJ_TYPE</Name><Value>id_OTYPE_DOM</Value></Property>{extra}<Property><Name>IDEnumDefinedValues</Name><Value>&lt;EnumValues&gt;&lt;Values&gt;&lt;EnumValue&gt;&lt;Name&gt;A&lt;/Name&gt;&lt;Description&gt;A&lt;/Description&gt;&lt;Value&gt;1&lt;/Value&gt;&lt;/EnumValue&gt;&lt;/Values&gt;&lt;/EnumValues&gt;</Value></Property><Property><Name>FolderType</Name><Value>c88fffcd-b6f8-0000-8fec-00b5497e2117</Value></Property><Property><Name>ObjectIsInterface</Name><Value>True</Value></Property><Property><Name>IsDefault</Name><Value>False</Value></Property></Properties>"
    )
}

fn property(name: &str, value: &str) -> String {
    format!("<Property><Name>{name}</Name><Value>{value}</Value></Property>")
}

#[test]
fn an_untyped_enumeration_is_refused_whatever_length_its_row_stores() {
    // No ATTCUSTOMTYPE: the stored row declares no type. A Length on the row is not a declared type,
    // so none of these is mapped, including Length 4, the SDK default for an item with no type.
    for length in ["1", "2", "4", "8", "18"] {
        let raw = enumeration_row(&property("Length", length));
        assert_eq!(
            domain_data_type(&raw),
            None,
            "untyped enumeration with Length {length} must stay refused"
        );
    }
    assert_eq!(domain_data_type(&enumeration_row("")), None);
}

#[test]
fn an_untyped_enumeration_with_decimals_is_refused() {
    // Decimals on an untyped row have no SDK form evidenced; the row is refused, not read as NUMERIC.
    let raw = enumeration_row(&format!(
        "{}{}",
        property("Length", "8"),
        property("Decimals", "2")
    ));
    assert_eq!(domain_data_type(&raw), None);
}

#[test]
fn an_untyped_enumeration_row_from_each_knowledge_base_is_refused() {
    // The rows are verbatim: NotificationBadgeType and MapType store nothing; MessageTypes stores
    // Length 2 (and a ControlType, which the allowlist also refuses) and SmartDeviceType Length 1.
    // None declares an ATTCUSTOMTYPE, so none has a type.
    for (label, raw) in [
        ("GX18 NotificationBadgeType", GX18_NOTIFICATION_BADGE_TYPE),
        ("GX18 MessageTypes", GX18_MESSAGE_TYPES),
        ("GX18 MapType", GX18_MAP_TYPE),
        ("GX16 SmartDeviceType", GX16_SMART_DEVICE_TYPE),
    ] {
        assert_eq!(domain_data_type(raw), None, "{label} must stay refused");
    }
}

#[test]
fn a_typed_enumeration_without_a_stored_length_is_refused() {
    // TargetType is a typed NUMERIC with no Length, and PushNotificationPriority a typed CHARACTER
    // with no Length. decode_type refuses both (no length to map), so neither is defaulted.
    assert_eq!(domain_data_type(GX18_TARGET_TYPE), None);
    assert_eq!(domain_data_type(GX18_PUSH_NOTIFICATION_PRIORITY), None);
}

#[test]
fn a_typed_enumeration_with_an_unlisted_property_is_refused_before_its_type_is_read() {
    // CameraAPIQuality declares NUMERIC with Length 1, but its row also carries a Description, which
    // DOMAIN_ROW_PROPERTIES does not name. The whole row is refused, so its declared type is never
    // read. Naming Description (or any other property) changes what this guard refuses and needs
    // its own evidence: this test is the decision point.
    assert_eq!(domain_data_type(GX18_CAMERA_API_QUALITY), None);
}

#[test]
fn a_typed_enumeration_with_a_stored_length_keeps_its_declared_type() {
    // The positive control: an enumeration that declares its type and length is still mapped, so the
    // refusal above is not a refusal of every enumeration.
    assert_eq!(
        domain_data_type(GX18_PROGRESS_INDICATOR_TYPE),
        Some(DataType::Numeric {
            precision: 1,
            scale: 0
        })
    );
    assert_eq!(
        domain_data_type(GX18_ALERT_TYPE),
        Some(DataType::Varchar { length: 100 })
    );
}
