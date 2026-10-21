//! Vectors for `gx_engine::read_token`.
//!
//! Every expected digest/token below was produced independently with Python
//! `hashlib.sha256(text.encode("utf-8")).hexdigest()` and the format
//! `f"{ticks}:{parent}:{digest}"`, mirroring Worker
//! `WriteService.ComputeContentVersionToken`. They prove format and hash only,
//! not live SDK `LastUpdate`, parent or source-text parity.

use gx_engine::read_token::{
    MAX_DOTNET_TICKS, ParentIdentity, ReadTokenRefusal, UtcTicks, content_fingerprint,
    content_version_token, object_version_stamp,
};

const ROOT_MODULE_N: &str = "afa4737741d54ae897556f53150aa361";
const NON_ASCII: &str = "a\u{e7}\u{e3}o \u{2013} \u{65e5}\u{672c}\u{8a9e} \u{1F600}";
const NON_ASCII_SHA: &str = "7aed77b3295efa4c872242c38871770d5716e82f2f273f5bd7193aec297b8a04";

fn ticks(value: i64) -> UtcTicks {
    UtcTicks::new(value).expect("valid .NET ticks")
}

#[test]
fn fingerprint_matches_hashlib_vectors() {
    let cases: [(&str, String); 10] = [
        (
            "",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855".into(),
        ),
        (
            "abc",
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad".into(),
        ),
        (NON_ASCII, NON_ASCII_SHA.into()),
        (
            "line1\r\nline2",
            "d14a91a6d1c6ee83bf0c774ebecbee6d8b393b395dae29eea839c354d6fba9c0".into(),
        ),
        (
            "line1\nline2",
            "683376e290829b482c2655745caffa7a1dccfa10afaa62dac2b42dd6c68d0f83".into(),
        ),
        (
            "x\u{85}y",
            "1d8c4f61b825f326dfc0302bafa2bf2e468aa52ff5dcd56ddcde96427b91d5bd".into(),
        ),
        // Surrounding whitespace is content: no trimming or newline normalisation.
        (
            "abc\n",
            "edeaaff3f1774ad2888673770c6d64097e391bc362d7d6fb34982ddf0efd18cb".into(),
        ),
        (
            "abc \r\n",
            "fff0148214f5513e57cdeea0c3debb262f0a8fdc481fdcd480449716efb03491".into(),
        ),
        (
            "  abc",
            "1e763219e229d1971b9a556c34a32485e14547524fe754ec233769f77173f7af".into(),
        ),
        // A leading BOM character is content (U+FEFF => EF BB BF), not stripped.
        (
            "\u{feff}abc",
            "1c28dc3f1f804a1ad9c9b4b4cf5e2658d16ad4ed08e3020d04a8d2865018947c".into(),
        ),
    ];
    for (text, expected) in cases {
        assert_eq!(content_fingerprint(text), expected, "text {text:?}");
    }
}

#[test]
fn fingerprint_crlf_and_lf_differ() {
    assert_ne!(
        content_fingerprint("line1\r\nline2"),
        content_fingerprint("line1\nline2")
    );
}

#[test]
fn fingerprint_is_correct_at_every_padding_boundary() {
    let cases = [
        (
            55,
            "9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318",
        ),
        (
            56,
            "b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a",
        ),
        (
            63,
            "7d3e74a05d7db15bce4ad9ec0658ea98e3f06eeecf16b4c6fff2da457ddc2f34",
        ),
        (
            64,
            "ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb",
        ),
        (
            65,
            "635361c48bb9eab14198e76ea8ab7f1a41685d6ad62aa9146d301d4f17eb0ae0",
        ),
        (
            119,
            "31eba51c313a5c08226adf18d4a359cfdfd8d2e816b13f4af952f7ea6584dcfb",
        ),
        (
            120,
            "2f3d335432c70b580af0e8e1b3674a7c020d683aa5f73aaaedfdc55af904c21c",
        ),
        (
            128,
            "6836cf13bac400e9105071cd6af47084dfacad4e5e302c94bfed24e013afb73e",
        ),
        (
            1_000_000,
            "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0",
        ),
    ];
    for (length, expected) in cases {
        assert_eq!(
            content_fingerprint(&"a".repeat(length)),
            expected,
            "length {length}"
        );
    }
}

#[test]
fn content_token_matches_hashlib_vectors_for_guid_parent_and_tick_extremes() {
    let parent = ParentIdentity::guid(ROOT_MODULE_N).unwrap();
    // 2^53 + 1 is not representable as f64, so it catches float round-tripping.
    for value in [
        0,
        637_000_000_000_000_000,
        9_007_199_254_740_993,
        MAX_DOTNET_TICKS,
    ] {
        assert_eq!(
            content_version_token(ticks(value), &parent, NON_ASCII),
            format!("{value}:{ROOT_MODULE_N}:{NON_ASCII_SHA}"),
        );
    }
    assert_eq!(MAX_DOTNET_TICKS, 3_155_378_975_999_999_999);
}

#[test]
fn root_and_name_parents_match_hashlib_vectors() {
    assert_eq!(
        content_version_token(ticks(638_000_000_000_000_001), &ParentIdentity::root(), ""),
        "638000000000000001:root:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    );
    let named = ParentIdentity::name("M\u{f3}dulo Financeiro").unwrap();
    assert_eq!(
        content_version_token(ticks(638_000_000_000_000_001), &named, "line1\r\nline2"),
        "638000000000000001:M\u{f3}dulo Financeiro:d14a91a6d1c6ee83bf0c774ebecbee6d8b393b395dae29eea839c354d6fba9c0",
    );
    assert_eq!(
        content_version_token(ticks(638_000_000_000_000_001), &named, "line1\nline2"),
        "638000000000000001:M\u{f3}dulo Financeiro:683376e290829b482c2655745caffa7a1dccfa10afaa62dac2b42dd6c68d0f83",
    );
}

#[test]
fn object_stamp_has_no_content_component() {
    let parent = ParentIdentity::guid(ROOT_MODULE_N).unwrap();
    assert_eq!(
        object_version_stamp(ticks(637_000_000_000_000_000), &parent),
        format!("637000000000000000:{ROOT_MODULE_N}"),
    );
    assert_eq!(
        object_version_stamp(ticks(0), &ParentIdentity::root()),
        "0:root"
    );
    assert_eq!(
        object_version_stamp(ticks(MAX_DOTNET_TICKS), &ParentIdentity::root()),
        "3155378975999999999:root",
    );
}

#[test]
fn parent_guid_is_rendered_in_n_format_lowercase() {
    let dashed_upper = "AFA47377-41D5-4AE8-9755-6F53150AA361";
    for input in [
        dashed_upper,
        "afa47377-41d5-4ae8-9755-6f53150aa361",
        "AFA4737741D54AE897556F53150AA361",
        ROOT_MODULE_N,
    ] {
        let parent = ParentIdentity::guid(input).unwrap();
        assert_eq!(
            parent,
            ParentIdentity::guid(ROOT_MODULE_N).unwrap(),
            "input {input}"
        );
        let token = content_version_token(ticks(1), &parent, "");
        assert!(token.starts_with(&format!("1:{ROOT_MODULE_N}:")), "{token}");
        assert!(!token.contains('-') && token == token.to_ascii_lowercase());
    }
}

#[test]
fn ticks_must_be_valid_dotnet_ticks() {
    assert!(UtcTicks::new(0).is_ok());
    assert_eq!(
        UtcTicks::new(MAX_DOTNET_TICKS).unwrap().get(),
        MAX_DOTNET_TICKS
    );
    for invalid in [-1, MAX_DOTNET_TICKS + 1, i64::MAX, i64::MIN] {
        assert_eq!(
            UtcTicks::new(invalid),
            Err(ReadTokenRefusal::TicksOutOfRange(invalid))
        );
    }
}

#[test]
fn unknown_or_malformed_parent_inputs_are_refused() {
    for bad in [
        "",
        "afa4737741d54ae897556f53150aa36",        // 31 digits
        "afa4737741d54ae897556f53150aa3611",      // 33 digits
        "zfa4737741d54ae897556f53150aa361",       // non-hex
        "{afa47377-41d5-4ae8-9755-6f53150aa361}", // braces
        "afa47377-41d5-4ae8-9755-6f53150aa36-",   // misplaced dash, right length
        "afa473774-1d5-4ae8-9755-6f53150aa361",   // dash in wrong position
        "afa4737741d54ae897556f53150aa36\u{e9}",  // non-ASCII, 32 chars but 33 bytes
    ] {
        assert_eq!(
            ParentIdentity::guid(bad),
            Err(ReadTokenRefusal::InvalidParentGuid),
            "{bad:?}"
        );
    }
    for bad in ["", "a:b"] {
        assert_eq!(
            ParentIdentity::name(bad),
            Err(ReadTokenRefusal::InvalidParentName),
            "{bad:?}"
        );
    }
}
