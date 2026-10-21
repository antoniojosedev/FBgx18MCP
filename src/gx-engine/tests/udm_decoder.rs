//! Integration and unit tests for GeneXus UDM physical storage decoder,
//! 11-byte envelope verification, GZip decompression, and TokenDataList source reconstruction.

#![forbid(unsafe_code)]

use gx_engine::udm::{
    AuthMode, FLAG_GZIP, FLAG_UNCOMPRESSED_UTF8, HEADER_LEN, KbConnectionInfo, MAGIC,
    UNCOMPRESSED_PREFIX_LEN, UdmPartKind, VERSION_V1, decode_blob, encode_blob,
    extract_token_data_list, unescape_xml,
};
use std::path::Path;

#[test]
fn test_envelope_11_byte_header_structure() {
    let payload = "GeneXus UDM Envelope Header Test";
    let encoded = encode_blob(payload, false).expect("encode payload");

    // A stored uncompressed blob has a 7-byte prefix (magic, version, flag) and no length field.
    assert_eq!(encoded.len(), UNCOMPRESSED_PREFIX_LEN + payload.len());
    assert_eq!(&encoded[0..4], &MAGIC);
    assert_eq!(&encoded[4..6], &VERSION_V1);
    assert_eq!(encoded[6], FLAG_UNCOMPRESSED_UTF8);

    assert_eq!(&encoded[UNCOMPRESSED_PREFIX_LEN..], payload.as_bytes());

    let decoded = decode_blob(&encoded).expect("decode payload");
    assert_eq!(decoded, payload);
}

#[test]
fn test_envelope_fails_closed_on_truncated_data() {
    assert!(decode_blob(&[]).is_err());
    assert!(decode_blob(&[0x01, 0x02, 0x03]).is_err());
    // A gzip blob (flag 01) cut inside its 11-byte header is refused.
    assert!(decode_blob(&[0x01, 0x02, 0x03, 0x04, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00]).is_err());
}

#[test]
fn test_envelope_fails_closed_on_invalid_magic() {
    let mut data = encode_blob("test", false).unwrap();
    data[0] = 0x00;
    let err = decode_blob(&data).unwrap_err();
    assert!(err.contains("Invalid UDM envelope magic"));
}

#[test]
fn test_envelope_fails_closed_on_unsupported_version() {
    let mut data = encode_blob("test", false).unwrap();
    data[5] = 0x02; // version 2 instead of 1
    let err = decode_blob(&data).unwrap_err();
    assert!(err.contains("Unsupported UDM envelope version"));
}

#[test]
fn test_envelope_fails_closed_on_unknown_flag() {
    let mut data = encode_blob("test", false).unwrap();
    data[6] = 0x05; // unknown flag
    let err = decode_blob(&data).unwrap_err();
    assert!(err.contains("Unknown compression flag"));
}

#[test]
fn test_uncompressed_utf8_payload_roundtrip() {
    let samples = [
        "",
        "Short",
        "Ação de Cobrança — Teste com acentuação e caracteres especiais: Ç, ã, é, ü, ñ, €, §.",
        "Line 1\r\nLine 2\r\nLine 3\nLine 4",
    ];

    for sample in samples {
        let encoded = encode_blob(sample, false).expect("encode uncompressed");
        assert_eq!(encoded[6], FLAG_UNCOMPRESSED_UTF8);
        let decoded = decode_blob(&encoded).expect("decode uncompressed");
        assert_eq!(decoded, sample);
    }
}

#[test]
fn test_gzip_rfc1952_payload_roundtrip() {
    let source_code = r#"
// Procedure: CustomerMonthlyBilling
For Each Customer
    Where CustomerStatus = 'Active'
    Where CustomerBalance > 0
    Defined By CustomerName

    &InvoiceTotal = 0
    For Each Order
        Where OrderDate >= &StartDate
        Where OrderDate <= &EndDate
        &InvoiceTotal += OrderAmount
    EndFor

    If &InvoiceTotal > 1000
        Do 'ApplyHighVolumeDiscount'
    EndIf

    Call(PGenerateInvoice, CustomerId, &InvoiceTotal)
EndFor

Sub 'ApplyHighVolumeDiscount'
    &Discount = &InvoiceTotal * 0.05
    &InvoiceTotal -= &Discount
    Msg(Format("High-volume discount %1 applied to %2", &Discount, CustomerName))
EndSub
"#;

    let encoded = encode_blob(source_code, true).expect("encode gzip");
    assert_eq!(encoded[6], FLAG_GZIP);
    assert!(!encoded.is_empty());

    let decoded = decode_blob(&encoded).expect("decode gzip");
    assert_eq!(decoded, source_code);
}

#[test]
fn test_large_payload_gzip_decompression_parity() {
    let mut large_text = String::with_capacity(512 * 1024);
    for i in 0..5000 {
        large_text.push_str(&format!(
            "    &Index = {};\n    If &Index > 100\n        Call(PProcessItem, &Index, \"Customer_{}\");\n    EndIf\n",
            i, i
        ));
    }

    let encoded = encode_blob(&large_text, true).expect("encode large gzip");
    assert!(encoded.len() < large_text.len()); // GZip should achieve substantial compression

    let decoded = decode_blob(&encoded).expect("decode large gzip");
    assert_eq!(decoded.len(), large_text.len());
    assert_eq!(decoded, large_text);
}

#[test]
fn test_corrupted_gzip_stream_fails_gracefully() {
    let mut encoded = encode_blob("Some valid text", true).unwrap();
    // Corrupt the gzip payload part
    for b in &mut encoded[HEADER_LEN..] {
        *b = 0xFF;
    }

    let result = decode_blob(&encoded);
    assert!(result.is_err());
    let err = result.unwrap_err();
    assert!(err.contains("GZip decompression failed"));
}

#[test]
fn test_decompressed_size_mismatch_fails() {
    let encoded = encode_blob("1234567890", true).unwrap();
    let mut tampered = encoded.clone();
    // Set uncompressed size in header to 999 instead of 10
    let fake_len: u32 = 999;
    tampered[7..11].copy_from_slice(&fake_len.to_le_bytes());

    let result = decode_blob(&tampered);
    assert!(result.is_err());
    assert!(result.unwrap_err().contains("size mismatch"));
}

#[test]
fn test_xml_entity_unescaping() {
    let input = "&lt;Rules&gt; &#x0A; &quot;Name&quot; &amp; &apos;Single&apos; &#10; Price &gt; 50 &amp;&amp; Valid";
    let unescaped = unescape_xml(input);
    assert_eq!(
        unescaped,
        "<Rules> \n \"Name\" & 'Single' \n Price > 50 && Valid"
    );

    // Verify unescaped unchanged when no & present
    assert_eq!(unescape_xml("Plain text here"), "Plain text here");

    // Verify broken or non-terminated entities are preserved safely
    assert_eq!(unescape_xml("foo & bar"), "foo & bar");
    assert_eq!(unescape_xml("a &invalid; b"), "a &invalid; b");
}

#[test]
fn test_token_data_list_source_extraction() {
    let xml = r#"
    <TokenDataList>
        <Token id="1"><Word>For</Word></Token>
        <Token id="2"><Word> </Word></Token>
        <Token id="3"><Word>Each</Word></Token>
        <Token id="4"><Word>&#x0A;</Word></Token>
        <Token id="5"><Word>    </Word></Token>
        <Token id="6"><Word>Where</Word></Token>
        <Token id="7"><Word> </Word></Token>
        <Token id="8"><Word>CustomerId</Word></Token>
        <Token id="9"><Word> </Word></Token>
        <Token id="10"><Word>&gt;=</Word></Token>
        <Token id="11"><Word> </Word></Token>
        <Token id="12"><Word>&amp;MinId</Word></Token>
        <Token id="13"><Word>&#x0A;</Word></Token>
        <Token id="14"><Word>    </Word></Token>
        <Token id="15"><Word>&amp;CustomerName</Word></Token>
        <Token id="16"><Word> </Word></Token>
        <Token id="17"><Word>=</Word></Token>
        <Token id="18"><Word> </Word></Token>
        <Token id="19"><Word>&quot;Acme &amp; Co&quot;</Word></Token>
        <Token id="20"><Word>&#x0A;</Word></Token>
        <Token id="21"><Word>EndFor</Word></Token>
    </TokenDataList>
    "#;

    let code = extract_token_data_list(xml).expect("extract tokens");
    let expected =
        "For Each\n    Where CustomerId >= &MinId\n    &CustomerName = \"Acme & Co\"\nEndFor";
    assert_eq!(code, expected);
}

#[test]
fn test_token_data_list_inside_part_container() {
    let xml = r#"
    <Part type="528d1c06-a9c2-420d-bd35-21dca83f12ff">
        <SourcePart>
            <TokenDataList>
                <Token><Word>Msg</Word></Token>
                <Token><Word>(</Word></Token>
                <Token><Word>&quot;Hello World&quot;</Word></Token>
                <Token><Word>)</Word></Token>
            </TokenDataList>
        </SourcePart>
    </Part>
    "#;

    let code = extract_token_data_list(xml).expect("extract tokens from container");
    assert_eq!(code, "Msg(\"Hello World\")");
}

#[test]
fn test_token_data_list_rules_part_extraction() {
    let xml = r#"
    <TokenDataList>
        <Token><Word>Prompt</Word></Token>
        <Token><Word>(</Word></Token>
        <Token><Word>CustomerName</Word></Token>
        <Token><Word>,</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>CustomerId</Word></Token>
        <Token><Word>)</Word></Token>
        <Token><Word>;</Word></Token>
        <Token><Word>&#x0A;</Word></Token>
        <Token><Word>Default</Word></Token>
        <Token><Word>(</Word></Token>
        <Token><Word>CustomerActive</Word></Token>
        <Token><Word>,</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>True</Word></Token>
        <Token><Word>)</Word></Token>
        <Token><Word>;</Word></Token>
        <Token><Word>&#x0A;</Word></Token>
        <Token><Word>Error</Word></Token>
        <Token><Word>(</Word></Token>
        <Token><Word>&apos;Invalid ID&apos;</Word></Token>
        <Token><Word>)</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>If</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>CustomerId</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>&lt;=</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>0</Word></Token>
        <Token><Word>;</Word></Token>
    </TokenDataList>
    "#;

    let rules = extract_token_data_list(xml).expect("extract rules");
    let expected = "Prompt(CustomerName, CustomerId);\nDefault(CustomerActive, True);\nError('Invalid ID') If CustomerId <= 0;";
    assert_eq!(rules, expected);
}

#[test]
fn test_token_data_list_conditions_part_extraction() {
    let xml = r#"
    <TokenDataList>
        <Token><Word>CustomerId</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>&gt;=</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>&amp;FromId</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>and</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>CustomerId</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>&lt;=</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>&amp;ToId</Word></Token>
        <Token><Word>;</Word></Token>
    </TokenDataList>
    "#;

    let conditions = extract_token_data_list(xml).expect("extract conditions");
    assert_eq!(conditions, "CustomerId >= &FromId and CustomerId <= &ToId;");
}

#[test]
fn test_token_data_list_missing_fails_closed() {
    let xml = r#"<Root><SomeOtherElement>Hello</SomeOtherElement></Root>"#;
    assert!(extract_token_data_list(xml).is_err());

    let malformed = r#"<TokenDataList><Word>unclosed"#;
    assert!(extract_token_data_list(malformed).is_err());
}

#[test]
fn test_end_to_end_pipeline_blob_to_source() {
    let original_xml = r#"
    <TokenDataList>
        <Token><Word>Event</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>&apos;GetSummary&apos;</Word></Token>
        <Token><Word>&#x0A;</Word></Token>
        <Token><Word>    </Word></Token>
        <Token><Word>&amp;Status</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>=</Word></Token>
        <Token><Word> </Word></Token>
        <Token><Word>True</Word></Token>
        <Token><Word>&#x0A;</Word></Token>
        <Token><Word>EndEvent</Word></Token>
    </TokenDataList>
    "#;

    // 1. Encode into a GZip-compressed 11-byte envelope blob
    let blob = encode_blob(original_xml, true).expect("encode blob");

    // 2. Decode the blob using the 11-byte envelope header and GZip decompressor
    let decompressed_xml = decode_blob(&blob).expect("decode blob");
    assert_eq!(decompressed_xml, original_xml);

    // 3. Extract the reconstructed source code from the <TokenDataList>
    let reconstructed_source =
        extract_token_data_list(&decompressed_xml).expect("extract source code");
    let expected_source = "Event 'GetSummary'\n    &Status = True\nEndEvent";
    assert_eq!(reconstructed_source, expected_source);
}

/// Part classification is by canonical name. A numeric id is not a part kind:
/// GeneXus 16, 17 and 18 number the same part differently, so ids are resolved per
/// Knowledge Base by `EntityTypeResolver` (see `udm_entity_types.rs`).
#[test]
fn test_udm_part_kinds_classification() {
    let test_cases = [
        ("SOURCE", UdmPartKind::Source, true, false),
        ("Rules", UdmPartKind::Rules, true, false),
        ("Events", UdmPartKind::Events, true, false),
        ("Conditions", UdmPartKind::Conditions, true, false),
        ("Variables", UdmPartKind::Variables, false, true),
        ("Structure", UdmPartKind::Structure, false, true),
        ("WebForm", UdmPartKind::WebForm, false, true),
        ("PatternInstance", UdmPartKind::PatternInstance, false, true),
    ];

    for (name, expected_kind, is_token, is_xml) in test_cases {
        let kind = UdmPartKind::from_name(name);
        assert_eq!(kind, expected_kind, "part name {name}");
        assert_eq!(kind.is_tokenized_source(), is_token, "part name {name}");
        assert_eq!(kind.is_xml_part(), is_xml, "part name {name}");
    }
    assert!(matches!(
        UdmPartKind::from_name("Help"),
        UdmPartKind::Unknown(_)
    ));
}

#[test]
fn test_real_knowledgebase_connection_if_exists() {
    let kb_paths = ["C:\\KBs\\KBTeste", "C:\\KBs\\KBTeste17"];

    for kb_path in kb_paths {
        let conn_file = Path::new(kb_path).join("knowledgebase.connection");
        if conn_file.exists() {
            let info = KbConnectionInfo::from_file(&conn_file).unwrap_or_else(|e| {
                panic!(
                    "Failed to parse real connection file {}: {e}",
                    conn_file.display()
                )
            });

            assert!(!info.server_instance.is_empty());
            assert!(!info.db_name.is_empty());
            assert_eq!(info.auth_mode, AuthMode::Integrated);
            assert!(info.is_local_db());
            assert!(info.connection_string().contains(&info.db_name));
        }
    }
}
