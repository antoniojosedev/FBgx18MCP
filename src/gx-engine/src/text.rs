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
