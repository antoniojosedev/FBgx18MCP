//! Bounded text storage for the measured GeneXus .NET Character/Varchar subset.
use std::fmt;

pub const MAX_TEXT_BYTES: usize = 1024 * 1024;

#[derive(Debug, Clone, Eq)]
pub struct Text(String);

impl Text {
    pub fn new(value: &str) -> Result<Self, String> {
        if value.len() > MAX_TEXT_BYTES {
            return Err("text value exceeds 1 MiB resource limit".into());
        }
        // Declared field width is metadata, not assignment truncation/padding,
        // in the measured standalone GeneXus .NET procedure behavior.
        Ok(Self(value.to_owned()))
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }

    pub fn trim_spaces(self) -> Self {
        // GeneXus Trim preserves tabs and NBSP in the measured .NET path.
        Self(self.0.trim_matches(' ').to_owned())
    }

    pub fn utf16_length(&self) -> usize {
        // Native Length/Len omit trailing ASCII spaces without trimming storage.
        self.0.trim_end_matches(' ').encode_utf16().count()
    }

    pub fn checked_concat(self, other: Self) -> Result<Self, String> {
        let length = self
            .0
            .len()
            .checked_add(other.0.len())
            .ok_or("text length overflow")?;
        if length > MAX_TEXT_BYTES {
            return Err("text value exceeds 1 MiB resource limit".into());
        }
        let mut value = self.0;
        value.push_str(&other.0);
        Ok(Self(value))
    }

    /// Native `Substr`/`Substring`: positions are 1-based over the raw value
    /// (trailing spaces included, since assignments store no padding). A start
    /// below 1 or past the last character returns the empty value, and a
    /// negative length takes everything from the start. Positions count
    /// characters; the admitted corpus is CP1252, where one character is one
    /// native unit, so astral text stays outside the measured set.
    pub fn substring(&self, start: i64, length: i64) -> Self {
        let chars: Vec<char> = self.0.chars().collect();
        if start < 1 || start as usize > chars.len() {
            return Self(String::new());
        }
        let from = (start - 1) as usize;
        let take = if length < 0 {
            chars.len() - from
        } else {
            (length as usize).min(chars.len() - from)
        };
        Self(chars[from..from + take].iter().collect())
    }

    /// Native `Upper`: per-character mapping that keeps the original character
    /// when its uppercase mapping expands to several characters (`ß` stays
    /// `ß`); `µ` still maps to the single-character Greek capital mu.
    pub fn to_upper(&self) -> Self {
        Self(
            self.0
                .chars()
                .map(|c| {
                    let mut mapped = c.to_uppercase();
                    match (mapped.next(), mapped.next()) {
                        (Some(upper), None) => upper,
                        _ => c,
                    }
                })
                .collect(),
        )
    }

    /// Native `Lower`, with the same single-character keep rule as `to_upper`.
    pub fn to_lower(&self) -> Self {
        Self(
            self.0
                .chars()
                .map(|c| {
                    let mut mapped = c.to_lowercase();
                    match (mapped.next(), mapped.next()) {
                        (Some(lower), None) => lower,
                        _ => c,
                    }
                })
                .collect(),
        )
    }

    /// Native `StrReplace`: case-sensitive, non-overlapping left-to-right
    /// occurrences; an empty search text returns the value unchanged.
    pub fn str_replace(&self, find: &Self, with: &Self) -> Result<Self, String> {
        if find.0.is_empty() {
            return Ok(self.clone());
        }
        let value = self.0.replace(&find.0, &with.0);
        if value.len() > MAX_TEXT_BYTES {
            return Err("text value exceeds 1 MiB resource limit".into());
        }
        Ok(Self(value))
    }

    /// Native `LTrim`: trims leading spaces (' ') only.
    pub fn trim_start_spaces(&self) -> Self {
        Self(self.0.trim_start_matches(' ').to_owned())
    }

    /// Native `RTrim`: trims trailing spaces (' ') only.
    pub fn trim_end_spaces(&self) -> Self {
        Self(self.0.trim_end_matches(' ').to_owned())
    }

    /// Native `PadL`: trims text, then pads on the left with repeated fill until size.
    /// If size is smaller than trimmed text length, it is truncated.
    pub fn pad_left(&self, size: i64, fill: &Self) -> Result<Self, String> {
        if size <= 0 {
            return Ok(Self(String::new()));
        }
        let size = size as usize;
        if size > MAX_TEXT_BYTES {
            return Err("pad_left size exceeds 1 MiB resource limit".into());
        }
        let trimmed = self.0.trim_matches(' ');
        let len = trimmed.len();
        if size <= len {
            return Ok(Self(trimmed[..size].to_owned()));
        }
        let needed = size - len;
        let fill_str = if fill.0.is_empty() { " " } else { &fill.0 };
        let count = needed / fill_str.len() + 1;
        let mut repeated = String::with_capacity(count * fill_str.len());
        for _ in 0..count {
            repeated.push_str(fill_str);
        }
        let mut result = repeated[..needed].to_owned();
        result.push_str(trimmed);
        Ok(Self(result[..size].to_owned()))
    }

    /// Native `PadR`: trims trailing spaces, then pads on the right with repeated fill until size.
    /// If size is smaller than rtrimmed text length, it is truncated.
    pub fn pad_right(&self, size: i64, fill: &Self) -> Result<Self, String> {
        if size <= 0 {
            return Ok(Self(String::new()));
        }
        let size = size as usize;
        if size > MAX_TEXT_BYTES {
            return Err("pad_right size exceeds 1 MiB resource limit".into());
        }
        let rtrimmed = self.0.trim_end_matches(' ');
        if size <= rtrimmed.len() {
            return Ok(Self(rtrimmed[..size].to_owned()));
        }
        let needed = size - rtrimmed.len();
        let fill_str = if fill.0.is_empty() { " " } else { &fill.0 };
        let count = needed / fill_str.len() + 1;
        let mut repeated = String::with_capacity(count * fill_str.len());
        for _ in 0..count {
            repeated.push_str(fill_str);
        }
        let mut result = rtrimmed.to_owned();
        result.push_str(&repeated[..needed]);
        Ok(Self(result[..size].to_owned()))
    }

    /// Native `Space`: returns a string consisting of N spaces (' ').
    pub fn space(count: i64) -> Result<Self, String> {
        if count <= 0 {
            return Ok(Self(String::new()));
        }
        let count = count as usize;
        if count > MAX_TEXT_BYTES {
            return Err("space count exceeds 1 MiB resource limit".into());
        }
        Ok(Self(" ".repeat(count)))
    }

    /// Native `Concat`: trims trailing spaces of left operand, appends optional separator, and right operand.
    pub fn concat_with_sep(&self, other: &Self, sep: Option<&Self>) -> Result<Self, String> {
        let left = self.0.trim_end_matches(' ');
        let sep_str = sep.map(|s| s.0.as_str()).unwrap_or("");
        let right = &other.0;
        let total_len = left.len() + sep_str.len() + right.len();
        if total_len > MAX_TEXT_BYTES {
            return Err("concat text exceeds 1 MiB resource limit".into());
        }
        let mut result = String::with_capacity(total_len);
        result.push_str(left);
        result.push_str(sep_str);
        result.push_str(right);
        Ok(Self(result))
    }

    /// Native `StartsWith`: ordinal comparison.
    pub fn starts_with(&self, prefix: &Self) -> bool {
        self.0.starts_with(&prefix.0)
    }

    /// Native `EndsWith`: ordinal comparison.
    pub fn ends_with(&self, suffix: &Self) -> bool {
        self.0.ends_with(&suffix.0)
    }

    /// Native `Contains`: ordinal comparison.
    pub fn contains(&self, needle: &Self) -> bool {
        self.0.contains(&needle.0)
    }

    /// Native `StringSearch` / `StrSearch` / `IndexOf`: 1-based index (0 if not found).
    pub fn str_search(&self, find: &Self, start: i64) -> i64 {
        let s1 = &self.0;
        let s2 = &find.0;
        let start = if start <= 0 { 1 } else { start };
        if start as usize > s1.len() {
            return 0;
        }
        let start_idx = (start - 1) as usize;
        if let Some(pos) = s1[start_idx..].find(s2) {
            (start_idx + pos + 1) as i64
        } else {
            0
        }
    }

    /// Native `Left`: returns initial `size` characters. Negative size yields empty.
    pub fn left(&self, size: i64) -> Self {
        if size <= 0 {
            return Self(String::new());
        }
        let size = size as usize;
        let len = self.0.len();
        if size >= len {
            return self.clone();
        }
        Self(self.0[..size].to_owned())
    }

    /// Native `Right`: returns trailing `size` characters. Negative size throws ArgumentOutOfRangeException.
    pub fn right(&self, size: i64) -> Result<Self, String> {
        if size < 0 {
            return Err(
                "ArgumentOutOfRangeException: startIndex cannot be larger than length of string"
                    .into(),
            );
        }
        let size = size as usize;
        let len = self.0.len();
        if size >= len {
            return Ok(self.clone());
        }
        let start = len - size;
        Ok(Self(self.0[start..].to_owned()))
    }

    /// Native `Asc`: returns ASCII/Unicode code point of first character (32 for empty string).
    pub fn asc(&self) -> i64 {
        if self.0.is_empty() {
            return 32;
        }
        self.0.chars().next().map(|c| c as i64).unwrap_or(32)
    }

    /// Native `Chr`: returns single-character string from ASCII/Unicode code point.
    pub fn chr(ascii: i64) -> Result<Self, String> {
        if !(0..=0x10FFFF).contains(&ascii) {
            return Err("Invalid character code".into());
        }
        if let Some(c) = char::from_u32(ascii as u32) {
            Ok(Self(c.to_string()))
        } else {
            Err("Invalid character code".into())
        }
    }

    /// Native `NotNumeric`: returns true if any character in space-trimmed text is not a digit. Empty returns false.
    pub fn not_numeric(&self) -> bool {
        let trimmed = self.0.trim_matches(' ');
        if trimmed.is_empty() {
            return false;
        }
        for c in trimmed.chars() {
            if !c.is_numeric() {
                return true;
            }
        }
        false
    }
}

impl PartialEq for Text {
    fn eq(&self, other: &Self) -> bool {
        self.0.trim_end_matches(' ') == other.0.trim_end_matches(' ')
    }
}

impl fmt::Display for Text {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}
