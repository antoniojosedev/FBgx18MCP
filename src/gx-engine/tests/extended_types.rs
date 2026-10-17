use gx_engine::extended_types::{
    ExtendedVariable, GuidValue, MathOps, math_abs, math_mod, math_pow, math_random, math_sqrt,
};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::hash::{DefaultHasher, Hash, Hasher};

#[test]
fn test_guid_v4_generation_and_rfc4122_compliance() {
    let seeds = [0_u64, 1, 42, 123456789, 0xdeadbeefcafebabe, u64::MAX];
    for seed in seeds {
        let guid = GuidValue::new_v4(seed);
        let bytes = guid.to_bytes();

        // RFC 4122 v4 requires:
        // Byte 6: bits 4-7 must be 0b0100 (4)
        assert_eq!(
            bytes[6] >> 4,
            4,
            "version bits for seed {seed} must be 4, got {:02x}",
            bytes[6]
        );

        // Byte 8: bits 6-7 must be 0b10 (variant RFC 4122, 0x80..=0xbf)
        assert_eq!(
            bytes[8] >> 6,
            2,
            "variant bits for seed {seed} must be 0b10 (RFC 4122), got {:02x}",
            bytes[8]
        );
        assert_eq!(
            bytes[8] & 0xc0,
            0x80,
            "variant mask for seed {seed} must match 0x80, got {:02x}",
            bytes[8]
        );

        let str_rep = guid.to_string();
        assert_eq!(str_rep.len(), 36);
        assert_eq!(&str_rep[8..9], "-");
        assert_eq!(&str_rep[13..14], "-");
        assert_eq!(&str_rep[18..19], "-");
        assert_eq!(&str_rep[23..24], "-");

        // Character at index 14 is the version digit (must be '4')
        assert_eq!(&str_rep[14..15], "4");

        // Character at index 19 is the variant digit (must be '8', '9', 'a', or 'b')
        assert!(
            matches!(&str_rep[19..20], "8" | "9" | "a" | "b"),
            "variant character must be 8, 9, a, or b, got {}",
            &str_rep[19..20]
        );
    }

    // Two distinct seeds must produce distinct GUIDs
    let g1 = GuidValue::new_v4(100);
    let g2 = GuidValue::new_v4(200);
    assert_ne!(g1, g2);

    // new_guid() produces RFC 4122 v4 GUIDs and successive calls are distinct
    let sys_g1 = GuidValue::new_guid();
    let sys_g2 = GuidValue::new_guid();
    assert_ne!(sys_g1, sys_g2);
    assert_eq!(sys_g1.to_bytes()[6] >> 4, 4);
    assert_eq!(sys_g1.to_bytes()[8] >> 6, 2);
    assert_eq!(&sys_g1.to_string()[14..15], "4");
    assert!(matches!(&sys_g1.to_string()[19..20], "8" | "9" | "a" | "b"));
}

#[test]
fn test_guid_from_str_valid_formats() {
    let lower_hyphen = "a1b2c3d4-e5f6-4a7b-8c9d-0123456789ab";
    let upper_hyphen = "A1B2C3D4-E5F6-4A7B-8C9D-0123456789AB";
    let mixed_hyphen = "A1b2C3d4-e5F6-4A7b-8C9d-0123456789aB";
    let lower_plain = "a1b2c3d4e5f64a7b8c9d0123456789ab";
    let upper_plain = "A1B2C3D4E5F64A7B8C9D0123456789AB";
    let mixed_plain = "A1b2C3d4e5F64A7b8C9d0123456789aB";

    let g_lower_h = GuidValue::from_str(lower_hyphen).expect("lower hyphen");
    let g_upper_h = GuidValue::from_str(upper_hyphen).expect("upper hyphen");
    let g_mixed_h = GuidValue::from_str(mixed_hyphen).expect("mixed hyphen");
    let g_lower_p = GuidValue::from_str(lower_plain).expect("lower plain");
    let g_upper_p = GuidValue::from_str(upper_plain).expect("upper plain");
    let g_mixed_p = GuidValue::from_str(mixed_plain).expect("mixed plain");

    // All must produce identical GUID representations
    assert_eq!(g_lower_h, g_upper_h);
    assert_eq!(g_lower_h, g_mixed_h);
    assert_eq!(g_lower_h, g_lower_p);
    assert_eq!(g_lower_h, g_upper_p);
    assert_eq!(g_lower_h, g_mixed_p);

    // Canonical representation must be standard 36-char lowercase
    assert_eq!(g_lower_h.to_string(), lower_hyphen);

    // Empty GUID formats
    let empty_h =
        GuidValue::from_str("00000000-0000-0000-0000-000000000000").expect("empty hyphen");
    let empty_p = GuidValue::from_str("00000000000000000000000000000000").expect("empty plain");
    assert_eq!(empty_h, GuidValue::EMPTY);
    assert_eq!(empty_p, GuidValue::EMPTY);
    assert!(empty_h.is_empty());

    // FromStr trait parse()
    let parsed: GuidValue = lower_hyphen.parse().expect("FromStr trait");
    assert_eq!(parsed, g_lower_h);
}

#[test]
fn test_guid_from_str_invalid_formats_fail_closed() {
    let invalid_cases = [
        // Short strings
        "",
        "123",
        "a1b2c3d4",
        "a1b2c3d4-e5f6-4a7b-8c9d-0123456789a", // 35 chars
        "a1b2c3d4e5f64a7b8c9d0123456789a",     // 31 chars
        // Long strings
        "a1b2c3d4-e5f6-4a7b-8c9d-0123456789abc", // 37 chars
        "a1b2c3d4e5f64a7b8c9d0123456789abc",     // 33 chars
        "a1b2c3d4-e5f6-4a7b-8c9d-0123456789ab-extra",
        // Bad hex characters
        "g1b2c3d4-e5f6-4a7b-8c9d-0123456789ab",
        "a1b2c3d4-e5f6-4a7b-8c9d-0123456789az",
        "a1b2c3d4-e5f6-4a7b-8c9d-0123456789a!",
        "a1b2c3d4-e5f6-4a7b-8c9d-012345 6789ab",
        "a1b2c3d4e5f64a7b8c9d0123456789zg",
        // Wrong hyphen placements
        "a1b2c3d-4e5f6-4a7b-8c9d-0123456789ab", // pos 7 instead of 8
        "a1b2c3d4_e5f6-4a7b-8c9d-0123456789ab", // underscore at 8
        "a1b2c3d4-e5f64-a7b-8c9d-0123456789ab", // pos 14 instead of 13
        "a1b2c3d4-e5f6-4a7b8-c9d-0123456789ab", // pos 19 instead of 18
        "a1b2c3d4-e5f6-4a7b-8c9d0-123456789ab", // pos 24 instead of 23
        "{a1b2c3d4-e5f6-4a7b-8c9d-0123456789ab}",
    ];

    for case in invalid_cases {
        assert!(
            GuidValue::from_str(case).is_err(),
            "expected parse error for invalid GUID '{case}'"
        );
    }
}

#[test]
fn test_guid_empty_state_and_set_empty() {
    let empty = GuidValue::EMPTY;
    assert!(empty.is_empty());
    assert_eq!(empty.to_bytes(), [0u8; 16]);
    assert_eq!(empty.to_string(), "00000000-0000-0000-0000-000000000000");

    let default_guid = GuidValue::default();
    assert_eq!(default_guid, empty);
    assert!(default_guid.is_empty());

    let mut guid = GuidValue::new_v4(42);
    assert!(!guid.is_empty());
    assert_ne!(guid, GuidValue::EMPTY);

    guid.set_empty();
    assert!(guid.is_empty());
    assert_eq!(guid, GuidValue::EMPTY);
    assert_eq!(guid.to_string(), "00000000-0000-0000-0000-000000000000");

    guid.from_string("a1b2c3d4-e5f6-4a7b-8c9d-0123456789ab")
        .expect("from_string");
    assert!(!guid.is_empty());
    guid.set_empty();
    assert!(guid.is_empty());
}

#[test]
fn test_guid_equality_and_hashing() {
    let g1 = GuidValue::from_str("a1b2c3d4-e5f6-4a7b-8c9d-0123456789ab").unwrap();
    let g1_copy = g1;
    let g2 = GuidValue::from_str("A1B2C3D4-E5F6-4A7B-8C9D-0123456789AB").unwrap();
    let g3 = GuidValue::from_str("ffffffff-ffff-4fff-8fff-ffffffffffff").unwrap();

    assert_eq!(g1, g1_copy);
    assert_eq!(g1, g2);
    assert_ne!(g1, g3);

    // Hash check
    let mut hasher1 = DefaultHasher::new();
    let mut hasher2 = DefaultHasher::new();
    g1.hash(&mut hasher1);
    g2.hash(&mut hasher2);
    assert_eq!(hasher1.finish(), hasher2.finish());

    // HashSet deduplication
    let mut set = HashSet::new();
    assert!(set.insert(g1));
    assert!(!set.insert(g2)); // Duplicate, returns false
    assert!(set.insert(g3));
    assert_eq!(set.len(), 2);
    assert!(set.contains(&g1));

    // HashMap lookup
    let mut map = HashMap::new();
    map.insert(g1, "first_entry");
    assert_eq!(map.get(&g2), Some(&"first_entry"));
}

#[test]
fn test_guid_json_serialization() {
    #[derive(Serialize, Deserialize, PartialEq, Debug)]
    struct Record {
        id: GuidValue,
        name: String,
    }

    let empty = GuidValue::EMPTY;
    let json_empty = serde_json::to_string(&empty).unwrap();
    assert_eq!(json_empty, "\"00000000-0000-0000-0000-000000000000\"");

    let deserialized_empty: GuidValue = serde_json::from_str(&json_empty).unwrap();
    assert_eq!(deserialized_empty, empty);

    let guid = GuidValue::from_str("a1b2c3d4-e5f6-4a7b-8c9d-0123456789ab").unwrap();
    let json_guid = serde_json::to_string(&guid).unwrap();
    assert_eq!(json_guid, "\"a1b2c3d4-e5f6-4a7b-8c9d-0123456789ab\"");

    let deserialized_guid: GuidValue = serde_json::from_str(&json_guid).unwrap();
    assert_eq!(deserialized_guid, guid);

    // Deserializing unhyphenated string from JSON
    let deserialized_plain: GuidValue =
        serde_json::from_str("\"a1b2c3d4e5f64a7b8c9d0123456789ab\"").unwrap();
    assert_eq!(deserialized_plain, guid);

    // Deserializing invalid string from JSON
    let err = serde_json::from_str::<GuidValue>("\"not-a-valid-guid\"");
    assert!(err.is_err());

    // Struct serialization round-trip
    let record = Record {
        id: guid,
        name: "TestRecord".into(),
    };
    let record_json = serde_json::to_string(&record).unwrap();
    assert!(record_json.contains("\"id\":\"a1b2c3d4-e5f6-4a7b-8c9d-0123456789ab\""));
    let deserialized_record: Record = serde_json::from_str(&record_json).unwrap();
    assert_eq!(deserialized_record, record);
}

#[test]
fn test_math_mod_positive_and_negative() {
    // Basic positive integers
    assert_eq!(math_mod(10.0, 3.0), 1.0);
    assert_eq!(math_mod(14.0, 5.0), 4.0);
    assert_eq!(math_mod(9.0, 3.0), 0.0);

    // Negative operands matching GeneXus / .NET IEEE remainder behavior
    assert_eq!(math_mod(-10.0, 3.0), -1.0);
    assert_eq!(math_mod(10.0, -3.0), 1.0);
    assert_eq!(math_mod(-10.0, -3.0), -1.0);

    // Fractional numbers
    assert!((math_mod(5.5, 2.0) - 1.5).abs() < 1e-9);
    assert!((math_mod(-5.5, 2.0) - (-1.5)).abs() < 1e-9);

    // Zero dividend and divisor
    assert_eq!(math_mod(0.0, 5.0), 0.0);
    assert_eq!(math_mod(10.0, 0.0), 0.0);
    assert_eq!(math_mod(f64::NAN, 3.0), 0.0);
    assert_eq!(math_mod(10.0, f64::NAN), 0.0);

    // Fluent MathOps trait
    assert_eq!(10.0_f64.gx_mod(3.0), 1.0);
    assert_eq!((-10.0_f64).gx_mod(3.0), -1.0);
}

#[test]
fn test_math_pow_and_sqrt() {
    // Power function
    assert_eq!(math_pow(2.0, 3.0), 8.0);
    assert_eq!(math_pow(5.0, 0.0), 1.0);
    assert_eq!(math_pow(4.0, 0.5), 2.0);
    assert_eq!(math_pow(2.0, -1.0), 0.5);
    assert_eq!(math_pow(10.0, 3.0), 1000.0);
    assert_eq!(math_pow(0.0, 4.0), 0.0);

    // Fluent MathOps pow
    assert_eq!(2.0_f64.gx_pow(3.0), 8.0);

    // Square root
    assert_eq!(math_sqrt(0.0).unwrap(), 0.0);
    assert_eq!(math_sqrt(4.0).unwrap(), 2.0);
    assert_eq!(math_sqrt(9.0).unwrap(), 3.0);
    assert_eq!(math_sqrt(16.0).unwrap(), 4.0);
    assert_eq!(math_sqrt(100.0).unwrap(), 10.0);
    assert!((math_sqrt(2.0).unwrap() - std::f64::consts::SQRT_2).abs() < 1e-12);

    // Fluent MathOps sqrt
    assert_eq!(16.0_f64.gx_sqrt().unwrap(), 4.0);

    // Negative numbers fail closed
    assert!(math_sqrt(-1.0).is_err());
    assert!(math_sqrt(-16.0).is_err());
    assert!(math_sqrt(f64::NAN).is_err());
    assert!((-16.0_f64).gx_sqrt().is_err());
}

#[test]
fn test_math_abs_and_random() {
    // Absolute value
    assert_eq!(math_abs(5.0), 5.0);
    assert_eq!(math_abs(-5.0), 5.0);
    assert_eq!(math_abs(0.0), 0.0);
    assert_eq!(math_abs(-0.0), 0.0);
    assert_eq!(math_abs(-9876.54321), 9876.54321);

    // Fluent MathOps abs
    assert_eq!((-42.0_f64).gx_abs(), 42.0);

    // Pseudo-random generation
    let mut seed = 42_u64;
    let mut prev = math_random(&mut seed);
    assert!((0.0..1.0).contains(&prev));

    let mut distinct_count = 0;
    for _ in 0..100 {
        let val = math_random(&mut seed);
        assert!(
            (0.0..1.0).contains(&val),
            "random value {val} out of [0, 1)"
        );
        if val != prev {
            distinct_count += 1;
        }
        prev = val;
    }
    assert!(distinct_count > 95, "PRNG values should be varied");

    // Zero seed works and advances
    let mut zero_seed = 0_u64;
    let r = math_random(&mut zero_seed);
    assert!((0.0..1.0).contains(&r));
    assert_ne!(zero_seed, 0);

    // Deterministic repeatability
    let mut s1 = 999_u64;
    let mut s2 = 999_u64;
    assert_eq!(math_random(&mut s1), math_random(&mut s2));
}

#[test]
fn test_guid_and_math_method_dispatch() {
    // 1. GUID variable method dispatch: &guid.IsEmpty(), &guid.FromString(), &guid.ToString(), &guid.SetEmpty()
    let mut guid_var = ExtendedVariable::Guid(GuidValue::EMPTY);

    assert_eq!(guid_var.dispatch("IsEmpty", &[]).unwrap(), "true");

    guid_var
        .dispatch("FromString", &["a1b2c3d4-e5f6-4a7b-8c9d-0123456789ab"])
        .unwrap();
    assert_eq!(guid_var.dispatch("IsEmpty", &[]).unwrap(), "false");
    assert_eq!(
        guid_var.dispatch("ToString", &[]).unwrap(),
        "a1b2c3d4-e5f6-4a7b-8c9d-0123456789ab"
    );

    guid_var.dispatch("SetEmpty", &[]).unwrap();
    assert_eq!(guid_var.dispatch("IsEmpty", &[]).unwrap(), "true");
    assert_eq!(
        guid_var.dispatch("ToString", &[]).unwrap(),
        "00000000-0000-0000-0000-000000000000"
    );

    // Case-insensitivity in method dispatch
    assert_eq!(guid_var.dispatch("isempty", &[]).unwrap(), "true");
    assert_eq!(
        guid_var.dispatch("TOSTRING", &[]).unwrap(),
        "00000000-0000-0000-0000-000000000000"
    );

    // Fail-closed on invalid arguments or unknown methods
    assert!(guid_var.dispatch("IsEmpty", &["unexpected_arg"]).is_err());
    assert!(guid_var.dispatch("UnknownMethod", &[]).is_err());

    // 2. Numeric variable method dispatch: &num.Sqrt(), &num.Pow(), &num.Mod(), &num.Abs()
    let mut num_var = ExtendedVariable::Number(16.0);
    assert_eq!(num_var.dispatch("Sqrt", &[]).unwrap(), "4");
    assert_eq!(num_var.dispatch("Mod", &["5"]).unwrap(), "1");
    assert_eq!(num_var.dispatch("Pow", &["2"]).unwrap(), "256");

    let mut neg_num = ExtendedVariable::Number(-42.5);
    assert_eq!(neg_num.dispatch("Abs", &[]).unwrap(), "42.5");
    assert!(neg_num.dispatch("Sqrt", &[]).is_err());

    // Case-insensitivity on numeric methods
    assert_eq!(num_var.dispatch("sqrt", &[]).unwrap(), "4");
    assert_eq!(num_var.dispatch("mod", &["7"]).unwrap(), "2");

    // Fail-closed on invalid arguments or unknown methods
    assert!(num_var.dispatch("Sqrt", &["extra"]).is_err());
    assert!(num_var.dispatch("Mod", &[]).is_err());
    assert!(num_var.dispatch("NonExistent", &[]).is_err());
}
