//! Extended primitive types (GUID/UUID and Math functions) for gx-engine.
//! RFC 4122 v4 compliant GUID implementation and GeneXus-compatible mathematical operations.

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::fmt;
use std::hash::Hash;
use std::str::FromStr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

/// 128-bit GUID/UUID implementation (16 bytes array `[u8; 16]`).
/// Follows RFC 4122 format specifications for version 4 UUIDs.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub struct GuidValue(pub [u8; 16]);

fn hex_val(b: u8) -> Result<u8, String> {
    match b {
        b'0'..=b'9' => Ok(b - b'0'),
        b'a'..=b'f' => Ok(b - b'a' + 10),
        b'A'..=b'F' => Ok(b - b'A' + 10),
        _ => Err(format!("invalid hex character in GUID: '{}'", b as char)),
    }
}

impl GuidValue {
    /// The empty GUID sentinel: all zeros (`00000000-0000-0000-0000-000000000000`).
    pub const EMPTY: Self = Self([0u8; 16]);

    /// Construct from raw 16 bytes.
    pub const fn from_bytes(bytes: [u8; 16]) -> Self {
        Self(bytes)
    }

    /// Access reference to the 16 raw bytes.
    pub const fn as_bytes(&self) -> &[u8; 16] {
        &self.0
    }

    /// Access the 16 raw bytes by value.
    pub const fn to_bytes(&self) -> [u8; 16] {
        self.0
    }

    /// Consume into the 16 raw bytes.
    pub const fn into_bytes(self) -> [u8; 16] {
        self.0
    }

    /// Generates a valid RFC 4122 v4 UUID deterministically from a 64-bit seed using SplitMix64.
    /// Sets version to 4 (bits 4-7 of byte 6 = 0b0100) and variant to RFC 4122 (bits 6-7 of byte 8 = 0b10).
    pub fn new_v4(mut seed: u64) -> Self {
        let mut next = || {
            seed = seed.wrapping_add(0x9e3779b97f4a7c15);
            let mut z = seed;
            z = (z ^ (z >> 30)).wrapping_mul(0xbf58476d1ce4e5b9);
            z = (z ^ (z >> 27)).wrapping_mul(0x94d049bb133111eb);
            z ^ (z >> 31)
        };
        let p1 = next().to_be_bytes();
        let p2 = next().to_be_bytes();
        let mut bytes = [0u8; 16];
        bytes[0..8].copy_from_slice(&p1);
        bytes[8..16].copy_from_slice(&p2);

        // Version 4: byte 6 high nibble = 0b0100 (4)
        bytes[6] = (bytes[6] & 0x0f) | 0x40;
        // Variant RFC 4122: byte 8 high 2 bits = 0b10 (0x80..=0xbf)
        bytes[8] = (bytes[8] & 0x3f) | 0x80;

        Self(bytes)
    }

    /// Generates a valid v4 UUID using system timestamp and an atomic sequence counter.
    pub fn new_guid() -> Self {
        static COUNTER: AtomicU64 = AtomicU64::new(0x243f6a8885a308d3);
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_nanos() as u64)
            .unwrap_or(0);
        let count = COUNTER.fetch_add(0x9e3779b97f4a7c15, Ordering::Relaxed);
        let seed = nanos ^ count.rotate_left(17);
        Self::new_v4(seed)
    }

    /// Parses a 36-char hyphenated UUID or 32-char unhyphenated hex string (case-insensitive).
    #[allow(clippy::should_implement_trait)]
    pub fn from_str(s: &str) -> Result<Self, String> {
        Self::parse_str(s)
    }

    fn parse_str(s: &str) -> Result<Self, String> {
        let bytes = s.as_bytes();
        let mut hex_chars = [0u8; 32];

        if bytes.len() == 36 {
            // Check hyphens at exact RFC 4122 positions: 8, 13, 18, 23
            if bytes[8] != b'-' || bytes[13] != b'-' || bytes[18] != b'-' || bytes[23] != b'-' {
                return Err(
                    "invalid UUID hyphen placement: expected hyphens at positions 8, 13, 18, 23"
                        .into(),
                );
            }
            // Copy 32 hex chars skipping hyphens
            hex_chars[0..8].copy_from_slice(&bytes[0..8]);
            hex_chars[8..12].copy_from_slice(&bytes[9..13]);
            hex_chars[12..16].copy_from_slice(&bytes[14..18]);
            hex_chars[16..20].copy_from_slice(&bytes[19..23]);
            hex_chars[20..32].copy_from_slice(&bytes[24..36]);
        } else if bytes.len() == 32 {
            hex_chars.copy_from_slice(bytes);
        } else {
            return Err(format!(
                "invalid GUID string length: expected 32 or 36 characters, got {}",
                bytes.len()
            ));
        }

        let mut out = [0u8; 16];
        for i in 0..16 {
            let hi = hex_val(hex_chars[i * 2])?;
            let lo = hex_val(hex_chars[i * 2 + 1])?;
            out[i] = (hi << 4) | lo;
        }

        Ok(Self(out))
    }

    /// Returns the standard 36-char lowercase hexadecimal string (`xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx`).
    #[allow(clippy::inherent_to_string_shadow_display)]
    pub fn to_string(&self) -> String {
        format!(
            "{:02x}{:02x}{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
            self.0[0],
            self.0[1],
            self.0[2],
            self.0[3],
            self.0[4],
            self.0[5],
            self.0[6],
            self.0[7],
            self.0[8],
            self.0[9],
            self.0[10],
            self.0[11],
            self.0[12],
            self.0[13],
            self.0[14],
            self.0[15]
        )
    }

    /// Returns `true` if all 16 bytes are zero.
    pub fn is_empty(&self) -> bool {
        self.0 == [0u8; 16]
    }

    /// Resets the GUID to all zeros (empty).
    pub fn set_empty(&mut self) {
        self.0 = [0u8; 16];
    }

    /// Sets the GUID value from a string (GeneXus `&guid.FromString(s)`).
    pub fn from_string(&mut self, s: &str) -> Result<(), String> {
        *self = Self::from_str(s)?;
        Ok(())
    }
}

impl FromStr for GuidValue {
    type Err = String;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        Self::parse_str(s)
    }
}

impl fmt::Display for GuidValue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.to_string())
    }
}

impl Default for GuidValue {
    fn default() -> Self {
        Self::EMPTY
    }
}

impl Serialize for GuidValue {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_str(&self.to_string())
    }
}

impl<'de> Deserialize<'de> for GuidValue {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        struct GuidVisitor;

        impl<'de> serde::de::Visitor<'de> for GuidVisitor {
            type Value = GuidValue;

            fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
                formatter.write_str("a valid GUID/UUID string (32 or 36 hex characters)")
            }

            fn visit_str<E>(self, value: &str) -> Result<GuidValue, E>
            where
                E: serde::de::Error,
            {
                GuidValue::from_str(value).map_err(E::custom)
            }
        }

        deserializer.deserialize_str(GuidVisitor)
    }
}

/// Modulo operation matching GeneXus `Mod(n1, n2)` and `&n1.Mod(&n2)`.
/// Returns the remainder of dividing `n1` by `n2`.
/// If `n2` is 0.0 or either operand is NaN, returns 0.0.
pub fn math_mod(n1: f64, n2: f64) -> f64 {
    if n2 == 0.0 || n2.is_nan() || n1.is_nan() {
        0.0
    } else {
        let res = n1 % n2;
        if res == 0.0 {
            0.0 // Normalize -0.0 to 0.0
        } else {
            res
        }
    }
}

/// Power function matching GeneXus `&base.Pow(&exp)`.
pub fn math_pow(base: f64, exp: f64) -> f64 {
    base.powf(exp)
}

/// Square root matching GeneXus `&n.Sqrt()`.
/// Fails closed with an error on negative numbers or NaN.
pub fn math_sqrt(n: f64) -> Result<f64, String> {
    if n.is_nan() {
        return Err("cannot calculate square root of NaN".into());
    }
    if n < 0.0 {
        return Err(format!(
            "cannot calculate square root of negative number: {n}"
        ));
    }
    Ok(n.sqrt())
}

/// Absolute value matching GeneXus `&n.Abs()`.
pub fn math_abs(n: f64) -> f64 {
    if n == 0.0 { 0.0 } else { n.abs() }
}

/// Generates a pseudo-random float in `[0.0, 1.0)` using SplitMix64.
/// Mutates `seed` to advance the PRNG state.
pub fn math_random(seed: &mut u64) -> f64 {
    *seed = seed.wrapping_add(0x9e3779b97f4a7c15);
    let mut z = *seed;
    z = (z ^ (z >> 30)).wrapping_mul(0xbf58476d1ce4e5b9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94d049bb133111eb);
    let val = z ^ (z >> 31);
    // Upper 53 bits (size of f64 mantissa)
    let mantissa = val >> 11;
    (mantissa as f64) / ((1_u64 << 53) as f64)
}

/// Trait providing fluent method calls matching GeneXus number methods (`&n.Mod(&m)`, `&n.Pow(&exp)`, etc.).
pub trait MathOps {
    fn gx_mod(self, other: Self) -> Self;
    fn gx_pow(self, exp: Self) -> Self;
    fn gx_sqrt(self) -> Result<Self, String>
    where
        Self: Sized;
    fn gx_abs(self) -> Self;
}

impl MathOps for f64 {
    fn gx_mod(self, other: Self) -> Self {
        math_mod(self, other)
    }

    fn gx_pow(self, exp: Self) -> Self {
        math_pow(self, exp)
    }

    fn gx_sqrt(self) -> Result<Self, String> {
        math_sqrt(self)
    }

    fn gx_abs(self) -> Self {
        math_abs(self)
    }
}

/// Simulated GeneXus variable for variable method dispatch testing.
#[derive(Debug, Clone, PartialEq)]
pub enum ExtendedVariable {
    Guid(GuidValue),
    Number(f64),
}

impl ExtendedVariable {
    /// Dispatches a method call on the variable by name, matching GeneXus case-insensitivity.
    /// Supports:
    /// - `&guid.IsEmpty()`
    /// - `&guid.SetEmpty()`
    /// - `&guid.FromString(s)`
    /// - `&guid.ToString()`
    /// - `&num.Mod(other)`
    /// - `&num.Pow(exp)`
    /// - `&num.Sqrt()`
    /// - `&num.Abs()`
    pub fn dispatch(&mut self, method: &str, args: &[&str]) -> Result<String, String> {
        let norm = method.trim().to_ascii_uppercase();
        match self {
            ExtendedVariable::Guid(guid) => match norm.as_str() {
                "ISEMPTY" => {
                    if !args.is_empty() {
                        return Err("IsEmpty expects 0 arguments".into());
                    }
                    Ok(guid.is_empty().to_string())
                }
                "SETEMPTY" => {
                    if !args.is_empty() {
                        return Err("SetEmpty expects 0 arguments".into());
                    }
                    guid.set_empty();
                    Ok(String::new())
                }
                "FROMSTRING" => {
                    if args.len() != 1 {
                        return Err("FromString expects 1 argument".into());
                    }
                    *guid = GuidValue::from_str(args[0])?;
                    Ok(String::new())
                }
                "TOSTRING" => {
                    if !args.is_empty() {
                        return Err("ToString expects 0 arguments".into());
                    }
                    Ok(guid.to_string())
                }
                _ => Err(format!("unknown method '{method}' for GUID variable")),
            },
            ExtendedVariable::Number(num) => match norm.as_str() {
                "MOD" => {
                    if args.len() != 1 {
                        return Err("Mod expects 1 argument".into());
                    }
                    let other: f64 = args[0]
                        .parse()
                        .map_err(|e| format!("invalid numeric argument for Mod: {e}"))?;
                    Ok(math_mod(*num, other).to_string())
                }
                "POW" => {
                    if args.len() != 1 {
                        return Err("Pow expects 1 argument".into());
                    }
                    let exp: f64 = args[0]
                        .parse()
                        .map_err(|e| format!("invalid numeric argument for Pow: {e}"))?;
                    Ok(math_pow(*num, exp).to_string())
                }
                "SQRT" => {
                    if !args.is_empty() {
                        return Err("Sqrt expects 0 arguments".into());
                    }
                    let res = math_sqrt(*num)?;
                    Ok(res.to_string())
                }
                "ABS" => {
                    if !args.is_empty() {
                        return Err("Abs expects 0 arguments".into());
                    }
                    Ok(math_abs(*num).to_string())
                }
                _ => Err(format!("unknown method '{method}' for Numeric variable")),
            },
        }
    }
}
