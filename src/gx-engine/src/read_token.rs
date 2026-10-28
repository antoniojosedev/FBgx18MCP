//! Pure primitives that reproduce the Worker's object version token.
//!
//! The Worker (`WriteService.ComputeVersionToken` / `ComputeContentVersionToken` /
//! `ComputeContentFingerprint`) builds
//! `"<utc ticks>:<parent identity>:<sha256 hex of UTF-8 content>"`, where the
//! parent identity is the parent GUID in .NET `"N"` format (32 lowercase hex
//! digits), the parent name when the GUID is unavailable, or `"root"`.
//!
//! This module only composes that string from inputs the caller has already
//! established. It reads no KB and performs no timestamp or time-zone
//! conversion. `kb_ops::read_object` supplies those inputs for a verified
//! single-part read; MCP routing and admission decisions remain unwired.
//! Inputs that cannot be represented exactly are refused with
//! [`ReadTokenRefusal`]; nothing is approximated or defaulted.
//!
//! The tests compare against vectors produced independently with Python
//! `hashlib`. Those vectors prove the string format and hash only. The live
//! `KBTeste` differential that established the timestamp, parent and source
//! inputs is recorded in `kb_ops` tests and plan 132; it is not re-proved here.
//!
//! SHA-256 comes from the maintained RustCrypto `sha2` crate; no hashing code is
//! implemented here. The tests check it against independently generated
//! `hashlib` vectors, including every padding boundary.
//!
//! Limitation: Rust `&str` is valid UTF-8, while a .NET `string` may hold lone
//! surrogates that `Encoding.UTF8` encodes as U+FFFD. Callers must not feed text
//! that was lossily repaired from invalid UTF-16 and expect SDK parity.

use std::fmt;

use sha2::{Digest, Sha256};

/// `DateTime.MaxValue.Ticks`: the largest tick count a .NET `DateTime` can hold.
/// The smallest is 0 (`DateTime.MinValue`).
pub const MAX_DOTNET_TICKS: i64 = 3_155_378_975_999_999_999;

/// Why a token input was refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ReadTokenRefusal {
    /// Ticks are outside the valid .NET `DateTime` range `0..=MAX_DOTNET_TICKS`.
    TicksOutOfRange(i64),
    /// Parent GUID is neither 32 hex digits nor canonical 8-4-4-4-12 form.
    InvalidParentGuid,
    /// Parent name is empty or contains `:`, which would make the token ambiguous.
    InvalidParentName,
}

impl fmt::Display for ReadTokenRefusal {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::TicksOutOfRange(ticks) => write!(
                f,
                "ticks {ticks} are outside the .NET DateTime range 0..={MAX_DOTNET_TICKS}"
            ),
            Self::InvalidParentGuid => {
                f.write_str("parent GUID is not a 32-digit or 8-4-4-4-12 hex GUID")
            }
            Self::InvalidParentName => f.write_str("parent name is empty or contains ':'"),
        }
    }
}

impl std::error::Error for ReadTokenRefusal {}

/// Authoritative UTC .NET ticks (100 ns units since 0001-01-01T00:00:00Z),
/// equal to `LastUpdate.ToUniversalTime().Ticks`. The caller is responsible for
/// any conversion; this type only guarantees the value is a valid `DateTime` tick.
/// Observed on GX18 (Plan 132, disposable copy, UTC-3 host): the SDK value equals
/// the raw stored `ModelEntityOutput` `ObjectSaved` datetime ticks of the active
/// version's Design model, with no time-zone shift. One profile only; wired into
/// the single-part `kb-read` token, not into routing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct UtcTicks(i64);

impl UtcTicks {
    pub fn new(ticks: i64) -> Result<Self, ReadTokenRefusal> {
        if (0..=MAX_DOTNET_TICKS).contains(&ticks) {
            Ok(Self(ticks))
        } else {
            Err(ReadTokenRefusal::TicksOutOfRange(ticks))
        }
    }

    pub fn get(self) -> i64 {
        self.0
    }
}

/// Parent identity exactly as the Worker renders it. The representation is
/// private so a value can only come from [`ParentIdentity::root`],
/// [`ParentIdentity::guid`] or [`ParentIdentity::name`], which validate it.
///
/// ```compile_fail,E0599
/// use gx_engine::read_token::ParentIdentity;
/// let _ = ParentIdentity::Guid("not-a-guid".to_owned());
/// ```
///
/// ```compile_fail,E0599
/// use gx_engine::read_token::ParentIdentity;
/// let _ = ParentIdentity::Name("a:b".to_owned());
/// ```
///
/// ```compile_fail,E0603
/// use gx_engine::read_token::ParentIdentity;
/// let _ = ParentIdentity(gx_engine::read_token::ParentRepr::Root);
/// ```
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParentIdentity(ParentRepr);

#[derive(Debug, Clone, PartialEq, Eq)]
enum ParentRepr {
    /// No parent: rendered as `root`.
    Root,
    /// Parent GUID, stored as 32 lowercase hex digits (.NET `Guid.ToString("N")`).
    Guid(String),
    /// Parent name, used by the Worker only when the parent GUID is unavailable.
    Name(String),
}

impl ParentIdentity {
    /// No parent: rendered as `root`.
    pub fn root() -> Self {
        Self(ParentRepr::Root)
    }

    /// Accepts 32 hex digits or the canonical dashed form, any case; stores `"N"` form.
    pub fn guid(text: &str) -> Result<Self, ReadTokenRefusal> {
        let digits: String = match text.len() {
            32 => text.to_owned(),
            36 => {
                let bytes = text.as_bytes();
                if [8, 13, 18, 23].iter().any(|&i| bytes[i] != b'-') {
                    return Err(ReadTokenRefusal::InvalidParentGuid);
                }
                text.replace('-', "")
            }
            _ => return Err(ReadTokenRefusal::InvalidParentGuid),
        };
        if digits.len() != 32 || !digits.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(ReadTokenRefusal::InvalidParentGuid);
        }
        Ok(Self(ParentRepr::Guid(digits.to_ascii_lowercase())))
    }

    pub fn name(text: &str) -> Result<Self, ReadTokenRefusal> {
        if text.is_empty() || text.contains(':') {
            return Err(ReadTokenRefusal::InvalidParentName);
        }
        Ok(Self(ParentRepr::Name(text.to_owned())))
    }

    fn render(&self) -> &str {
        match &self.0 {
            ParentRepr::Root => "root",
            ParentRepr::Guid(digits) => digits,
            ParentRepr::Name(name) => name,
        }
    }
}

/// `"<ticks>:<parent>"`: the Worker's `ComputeVersionToken` (no content hash).
pub fn object_version_stamp(ticks: UtcTicks, parent: &ParentIdentity) -> String {
    format!("{}:{}", ticks.get(), parent.render())
}

/// `"<ticks>:<parent>:<sha256 hex>"`: the Worker's `ComputeContentVersionToken`.
/// `content` must be the full, un-paginated part text.
pub fn content_version_token(ticks: UtcTicks, parent: &ParentIdentity, content: &str) -> String {
    format!(
        "{}:{}",
        object_version_stamp(ticks, parent),
        content_fingerprint(content)
    )
}

/// Lowercase hex SHA-256 of the UTF-8 bytes (no BOM): the Worker's `ComputeContentFingerprint`.
pub fn content_fingerprint(content: &str) -> String {
    let digest = Sha256::digest(content.as_bytes());
    let mut hex = String::with_capacity(64);
    for byte in digest {
        hex.push(char::from(b"0123456789abcdef"[usize::from(byte >> 4)]));
        hex.push(char::from(b"0123456789abcdef"[usize::from(byte & 0x0f)]));
    }
    hex
}
