//! Binary blob decoder and XML token reconstruction for GeneXus UDM storage.
//! Handles 11-byte envelope parsing, RFC 1952 GZip decompression, and `<TokenDataList>`
//! source code extraction.

#![forbid(unsafe_code)]

use roxmltree::Document;

/// Magic 4-byte header identifying a GeneXus UDM serialized blob envelope.
pub const MAGIC: [u8; 4] = [0x01, 0x02, 0x03, 0x04];

/// Envelope version 1 (2 bytes).
pub const VERSION_V1: [u8; 2] = [0x00, 0x01];

/// Compression flag indicating RFC 1952 GZip compression.
pub const FLAG_GZIP: u8 = 0x01;

/// Compression flag indicating uncompressed UTF-8 text payload.
pub const FLAG_UNCOMPRESSED_UTF8: u8 = 0x02;

/// Standard length of the envelope header (11 bytes).
pub const HEADER_LEN: usize = 11;

/// Common GeneXus Part types identified in UDM storage.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, serde::Serialize, serde::Deserialize)]
pub enum UdmPartKind {
    /// Procedure / Transaction / WebPanel source code (TokenDataList).
    Source,
    /// Transaction / WebPanel rules (TokenDataList).
    Rules,
    /// Object events (TokenDataList).
    Events,
    /// Conditions part (TokenDataList).
    Conditions,
    /// Variables definition (XML).
    Variables,
    /// Structure definition (XML).
    Structure,
    /// Web Form layout (XML).
    WebForm,
    /// Pattern instance metadata (XML).
    PatternInstance,
    /// Unknown or custom part ID.
    Unknown(i32),
}

impl UdmPartKind {
    /// Maps a GeneXus part integer ID to `UdmPartKind`.
    pub fn from_id(id: i32) -> Self {
        match id {
            100 => Self::Source,
            102 => Self::Rules,
            97 => Self::Events,
            89 => Self::Conditions,
            105 => Self::Variables,
            104 => Self::Structure,
            107 => Self::WebForm,
            78 => Self::PatternInstance,
            other => Self::Unknown(other),
        }
    }

    /// Returns the integer ID of this part kind.
    pub fn id(&self) -> i32 {
        match self {
            Self::Source => 100,
            Self::Rules => 102,
            Self::Events => 97,
            Self::Conditions => 89,
            Self::Variables => 105,
            Self::Structure => 104,
            Self::WebForm => 107,
            Self::PatternInstance => 78,
            Self::Unknown(id) => *id,
        }
    }

    /// Returns `true` if this part represents source code encoded as a `<TokenDataList>`.
    pub fn is_tokenized_source(&self) -> bool {
        matches!(
            self,
            Self::Source | Self::Rules | Self::Events | Self::Conditions
        )
    }

    /// Returns `true` if this part represents XML metadata (Variables, Structure, WebForm, etc.).
    pub fn is_xml_part(&self) -> bool {
        matches!(
            self,
            Self::Variables | Self::Structure | Self::WebForm | Self::PatternInstance
        )
    }

    /// Returns the canonical uppercase part name used by the catalog.
    pub fn name(&self) -> &str {
        match self {
            Self::Source => "SOURCE",
            Self::Rules => "RULES",
            Self::Events => "EVENTS",
            Self::Conditions => "CONDITIONS",
            Self::Variables => "VARIABLES",
            Self::Structure => "STRUCTURE",
            Self::WebForm => "WEBFORM",
            Self::PatternInstance => "PATTERNINSTANCE",
            Self::Unknown(_) => "UNKNOWN",
        }
    }

    /// Resolves a part name (case-insensitive) to `UdmPartKind`.
    pub fn from_name(name: &str) -> Self {
        match name.to_ascii_uppercase().as_str() {
            "SOURCE" => Self::Source,
            "RULES" => Self::Rules,
            "EVENTS" => Self::Events,
            "CONDITIONS" => Self::Conditions,
            "VARIABLES" => Self::Variables,
            "STRUCTURE" => Self::Structure,
            "WEBFORM" => Self::WebForm,
            "PATTERNINSTANCE" => Self::PatternInstance,
            _ => Self::Unknown(0),
        }
    }
}

/// Decodes a GeneXus UDM storage binary blob, validating the 11-byte envelope header
/// and decompressing GZip payloads or reading uncompressed UTF-8 strings.
///
/// Header layout (11 bytes):
/// - Offset 0..3: `[0x01, 0x02, 0x03, 0x04]` (Magic)
/// - Offset 4..5: `[0x00, 0x01]` (Version)
/// - Offset 6: `0x01` = GZip compressed, `0x02` = uncompressed UTF-8
/// - Offset 7..10: `u32` little-endian uncompressed byte length
/// - Offset 11..end: GZip stream (RFC 1952) or UTF-8 string
pub fn decode_blob(data: &[u8]) -> Result<String, String> {
    if data.len() < HEADER_LEN {
        return Err(format!(
            "Blob data too short: {} bytes (minimum header length is {} bytes)",
            data.len(),
            HEADER_LEN
        ));
    }

    if data[0..4] != MAGIC {
        return Err(format!(
            "Invalid UDM envelope magic: expected {:02X?}, got {:02X?}",
            MAGIC,
            &data[0..4]
        ));
    }

    if data[4..6] != VERSION_V1 {
        return Err(format!(
            "Unsupported UDM envelope version: expected {:02X?}, got {:02X?}",
            VERSION_V1,
            &data[4..6]
        ));
    }

    let flag = data[6];
    let uncompressed_len = u32::from_le_bytes([data[7], data[8], data[9], data[10]]) as usize;
    let payload = &data[HEADER_LEN..];

    match flag {
        FLAG_UNCOMPRESSED_UTF8 => {
            let text = std::str::from_utf8(payload)
                .map_err(|e| format!("Invalid UTF-8 in uncompressed payload: {e}"))?;
            Ok(text.to_string())
        }
        FLAG_GZIP => {
            use flate2::read::GzDecoder;
            use std::io::Read;

            let mut decoder = GzDecoder::new(payload);
            let mut decompressed = Vec::with_capacity(uncompressed_len);
            decoder
                .read_to_end(&mut decompressed)
                .map_err(|e| format!("GZip decompression failed: {e}"))?;

            if uncompressed_len > 0 && decompressed.len() != uncompressed_len {
                return Err(format!(
                    "Decompressed size mismatch: header specified {} bytes, decompressed {} bytes",
                    uncompressed_len,
                    decompressed.len()
                ));
            }

            String::from_utf8(decompressed)
                .map_err(|e| format!("Decompressed payload is not valid UTF-8: {e}"))
        }
        other => Err(format!(
            "Unknown compression flag: 0x{:02X} (expected 0x01 for GZip or 0x02 for uncompressed UTF-8)",
            other
        )),
    }
}

/// Encodes text into a GeneXus UDM storage binary blob with the standard 11-byte envelope.
pub fn encode_blob(text: &str, compress: bool) -> Result<Vec<u8>, String> {
    let uncompressed_bytes = text.as_bytes();
    let uncompressed_len = uncompressed_bytes.len() as u32;

    let mut result = Vec::with_capacity(HEADER_LEN + uncompressed_bytes.len());
    result.extend_from_slice(&MAGIC);
    result.extend_from_slice(&VERSION_V1);

    if compress {
        use flate2::Compression;
        use flate2::write::GzEncoder;
        use std::io::Write;

        result.push(FLAG_GZIP);
        result.extend_from_slice(&uncompressed_len.to_le_bytes());

        let mut encoder = GzEncoder::new(result, Compression::default());
        encoder
            .write_all(uncompressed_bytes)
            .map_err(|e| format!("GZip compression failed: {e}"))?;
        encoder
            .finish()
            .map_err(|e| format!("GZip compression finish failed: {e}"))
    } else {
        result.push(FLAG_UNCOMPRESSED_UTF8);
        result.extend_from_slice(&uncompressed_len.to_le_bytes());
        result.extend_from_slice(uncompressed_bytes);
        Ok(result)
    }
}

/// Escapes special XML characters in text for safe storage in XML elements.
pub fn escape_xml(input: &str) -> String {
    let mut output = String::with_capacity(input.len());
    for ch in input.chars() {
        match ch {
            '&' => output.push_str("&amp;"),
            '<' => output.push_str("&lt;"),
            '>' => output.push_str("&gt;"),
            '"' => output.push_str("&quot;"),
            '\'' => output.push_str("&apos;"),
            _ => output.push(ch),
        }
    }
    output
}

/// Constructs a valid `<TokenDataList>` XML fragment from source code lines.
pub fn build_token_data_list(source: &str) -> String {
    let mut xml = String::from("<TokenDataList>\n");
    let lines: Vec<&str> = source.split('\n').collect();
    for (i, line) in lines.iter().enumerate() {
        let clean_line = line.strip_suffix('\r').unwrap_or(line);
        xml.push_str("    <Token><Word>");
        xml.push_str(&escape_xml(clean_line));
        xml.push_str("</Word></Token>\n");
        if i + 1 < lines.len() {
            xml.push_str("    <Token><Word>&#x0A;</Word></Token>\n");
        }
    }
    xml.push_str("</TokenDataList>");
    xml
}

/// Unescapes XML entities in a string including standard named entities (`&gt;`, `&lt;`,
/// `&amp;`, `&quot;`, `&apos;`) and numeric character references (`&#x0A;`, `&#10;`, etc.).
pub fn unescape_xml(input: &str) -> String {
    if !input.contains('&') {
        return input.to_string();
    }

    let mut output = String::with_capacity(input.len());
    let mut chars = input.chars().peekable();

    while let Some(ch) = chars.next() {
        if ch == '&' {
            let mut entity = String::with_capacity(8);
            let mut found_semi = false;

            for next_ch in chars.by_ref() {
                if next_ch == ';' {
                    found_semi = true;
                    break;
                }
                entity.push(next_ch);
                if entity.len() > 16 {
                    break;
                }
            }

            if found_semi {
                match entity.as_str() {
                    "lt" => output.push('<'),
                    "gt" => output.push('>'),
                    "amp" => output.push('&'),
                    "quot" => output.push('"'),
                    "apos" => output.push('\''),
                    s if s.starts_with("#x") || s.starts_with("#X") => {
                        if let Ok(code) = u32::from_str_radix(&s[2..], 16) {
                            if let Some(c) = char::from_u32(code) {
                                output.push(c);
                            } else {
                                output.push('&');
                                output.push_str(&entity);
                                output.push(';');
                            }
                        } else {
                            output.push('&');
                            output.push_str(&entity);
                            output.push(';');
                        }
                    }
                    s if s.starts_with('#') => {
                        if let Ok(code) = s[1..].parse::<u32>() {
                            if let Some(c) = char::from_u32(code) {
                                output.push(c);
                            } else {
                                output.push('&');
                                output.push_str(&entity);
                                output.push(';');
                            }
                        } else {
                            output.push('&');
                            output.push_str(&entity);
                            output.push(';');
                        }
                    }
                    _ => {
                        output.push('&');
                        output.push_str(&entity);
                        output.push(';');
                    }
                }
            } else {
                output.push('&');
                output.push_str(&entity);
            }
        } else {
            output.push(ch);
        }
    }

    output
}

/// Parses a `<TokenDataList>` XML fragment and reconstructs the original source code
/// by concatenating `<Word>` element text in document order and unescaping XML entities.
pub fn extract_token_data_list(xml: &str) -> Result<String, String> {
    let clean_xml = xml.strip_prefix('\u{FEFF}').unwrap_or(xml);
    let doc =
        Document::parse(clean_xml).map_err(|e| format!("Malformed XML in token data list: {e}"))?;

    let root = doc.root_element();
    let token_list_node = if root.tag_name().name().eq_ignore_ascii_case("TokenDataList") {
        root
    } else {
        root.descendants()
            .find(|n| n.is_element() && n.tag_name().name().eq_ignore_ascii_case("TokenDataList"))
            .ok_or_else(|| "Missing <TokenDataList> element in XML".to_string())?
    };

    let mut result = String::new();
    for word_node in token_list_node
        .descendants()
        .filter(|n| n.is_element() && n.tag_name().name().eq_ignore_ascii_case("Word"))
    {
        let raw_text: String = word_node
            .children()
            .filter(|n| n.is_text())
            .filter_map(|n| n.text())
            .collect();
        let unescaped = unescape_xml(&raw_text);
        result.push_str(&unescaped);
    }

    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn uncompressed_blob_roundtrip() {
        let original = "Hello GeneXus Direct UDM Reader!";
        let encoded = encode_blob(original, false).expect("encode uncompressed");
        assert_eq!(encoded.len(), HEADER_LEN + original.len());
        assert_eq!(&encoded[0..4], &MAGIC);
        assert_eq!(&encoded[4..6], &VERSION_V1);
        assert_eq!(encoded[6], FLAG_UNCOMPRESSED_UTF8);

        let decoded = decode_blob(&encoded).expect("decode uncompressed");
        assert_eq!(decoded, original);
    }

    #[test]
    fn gzip_blob_roundtrip() {
        let original = "For Each Customer\n    Where CustomerStatus = 'Active'\n    &Name = CustomerName\nEndFor";
        let encoded = encode_blob(original, true).expect("encode gzip");
        assert_eq!(&encoded[0..4], &MAGIC);
        assert_eq!(&encoded[4..6], &VERSION_V1);
        assert_eq!(encoded[6], FLAG_GZIP);

        let decoded = decode_blob(&encoded).expect("decode gzip");
        assert_eq!(decoded, original);
    }

    #[test]
    fn invalid_headers_fail_closed() {
        // Too short
        assert!(decode_blob(&[1, 2, 3]).is_err());

        // Invalid magic
        let mut bad_magic = encode_blob("test", false).unwrap();
        bad_magic[0] = 0xFF;
        assert!(decode_blob(&bad_magic).is_err());

        // Invalid version
        let mut bad_ver = encode_blob("test", false).unwrap();
        bad_ver[4] = 0x02;
        assert!(decode_blob(&bad_ver).is_err());

        // Unknown flag
        let mut bad_flag = encode_blob("test", false).unwrap();
        bad_flag[6] = 0x99;
        assert!(decode_blob(&bad_flag).is_err());
    }

    #[test]
    fn token_data_list_reconstruction() {
        let xml = r#"
        <TokenDataList>
            <Token><Word>For</Word></Token>
            <Token><Word> </Word></Token>
            <Token><Word>Each</Word></Token>
            <Token><Word>&#x0A;</Word></Token>
            <Token><Word>    </Word></Token>
            <Token><Word>&amp;CustomerName</Word></Token>
            <Token><Word> </Word></Token>
            <Token><Word>=</Word></Token>
            <Token><Word> </Word></Token>
            <Token><Word>&quot;Acme &amp; Co&quot;</Word></Token>
            <Token><Word>&#x0A;</Word></Token>
            <Token><Word>EndFor</Word></Token>
        </TokenDataList>
        "#;

        let code = extract_token_data_list(xml).expect("extract token data");
        let expected = "For Each\n    &CustomerName = \"Acme & Co\"\nEndFor";
        assert_eq!(code, expected);
    }

    #[test]
    fn unescape_xml_entities() {
        let input = "Price &lt; 100 &amp;&amp; Name == &quot;Acme&quot; &apos;test&apos; &#x0A; NewLine &#10; Another";
        let unescaped = unescape_xml(input);
        assert_eq!(
            unescaped,
            "Price < 100 && Name == \"Acme\" 'test' \n NewLine \n Another"
        );
    }

    #[test]
    fn udm_part_kinds() {
        assert_eq!(UdmPartKind::from_id(100), UdmPartKind::Source);
        assert_eq!(UdmPartKind::from_id(102), UdmPartKind::Rules);
        assert_eq!(UdmPartKind::from_id(97), UdmPartKind::Events);
        assert_eq!(UdmPartKind::from_id(89), UdmPartKind::Conditions);
        assert_eq!(UdmPartKind::from_id(105), UdmPartKind::Variables);
        assert_eq!(UdmPartKind::from_id(104), UdmPartKind::Structure);
        assert_eq!(UdmPartKind::from_id(107), UdmPartKind::WebForm);
        assert_eq!(UdmPartKind::from_id(78), UdmPartKind::PatternInstance);

        assert!(UdmPartKind::Source.is_tokenized_source());
        assert!(UdmPartKind::Rules.is_tokenized_source());
        assert!(UdmPartKind::Events.is_tokenized_source());
        assert!(UdmPartKind::Conditions.is_tokenized_source());
        assert!(!UdmPartKind::Variables.is_tokenized_source());

        assert!(UdmPartKind::Variables.is_xml_part());
        assert!(UdmPartKind::Structure.is_xml_part());
        assert!(UdmPartKind::WebForm.is_xml_part());
        assert!(UdmPartKind::PatternInstance.is_xml_part());
        assert!(!UdmPartKind::Source.is_xml_part());
        assert_eq!(UdmPartKind::Source.name(), "SOURCE");
        assert_eq!(UdmPartKind::from_name("source"), UdmPartKind::Source);
    }

    #[test]
    fn build_token_data_list_roundtrip() {
        let code =
            "For Each Customer\n    &Name = \"Acme <Co> & Partners\"\n    Msg('Saved')\nEndFor";
        let xml = build_token_data_list(code);
        let reconstructed = extract_token_data_list(&xml).expect("reconstruct token data");
        assert_eq!(reconstructed, code);
    }
}
