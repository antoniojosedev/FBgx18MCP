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

    pub fn trim_spaces(&self) -> Self {
        // GeneXus Trim preserves tabs and NBSP in the measured .NET path.
        Self(self.trim_str().to_owned())
    }

    #[inline(always)]
    pub fn trim_str(&self) -> &str {
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        if len == 0 {
            return "";
        }
        if bytes[0] != b' ' && bytes[len - 1] != b' ' {
            return &self.0;
        }
        let mut start = 0;
        while start < len && bytes[start] == b' ' {
            start += 1;
        }
        let mut end = len;
        while end > start && bytes[end - 1] == b' ' {
            end -= 1;
        }
        &self.0[start..end]
    }

    #[inline(always)]
    pub fn trim_slice(&self) -> &[u8] {
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        let mut start = 0;
        while start < len && bytes[start] == b' ' {
            start += 1;
        }
        let mut end = len;
        while end > start && bytes[end - 1] == b' ' {
            end -= 1;
        }
        &bytes[start..end]
    }

    #[inline(always)]
    pub fn trim_start_str(&self) -> &str {
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        if len == 0 || bytes[0] != b' ' {
            return &self.0;
        }
        let mut start = 1;
        while start < len && bytes[start] == b' ' {
            start += 1;
        }
        &self.0[start..]
    }

    #[inline(always)]
    pub fn trim_start_slice(&self) -> &[u8] {
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        if len == 0 || bytes[0] != b' ' {
            return bytes;
        }
        let mut start = 1;
        while start < len && bytes[start] == b' ' {
            start += 1;
        }
        &bytes[start..]
    }

    #[inline(always)]
    pub fn trim_end_str(&self) -> &str {
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        if len == 0 || bytes[len - 1] != b' ' {
            return &self.0;
        }
        let mut end = len - 1;
        while end > 0 && bytes[end - 1] == b' ' {
            end -= 1;
        }
        &self.0[..end]
    }

    #[inline(always)]
    pub fn trim_end_slice(&self) -> &[u8] {
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        if len == 0 || bytes[len - 1] != b' ' {
            return bytes;
        }
        let mut end = len - 1;
        while end > 0 && bytes[end - 1] == b' ' {
            end -= 1;
        }
        &bytes[..end]
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
    #[inline(always)]
    pub fn substring_str(&self, start: i64, length: i64) -> &str {
        if start < 1 {
            return "";
        }
        let bytes = self.0.as_bytes();
        let from = (start - 1) as usize;
        if from >= bytes.len() {
            return "";
        }
        let take = if length < 0 {
            bytes.len() - from
        } else {
            (length as usize).min(bytes.len() - from)
        };
        let to = from + take;
        if bytes[..to].is_ascii() {
            return &self.0[from..to];
        }
        let from_idx = (start - 1) as usize;
        let mut char_iter = self.0.char_indices().skip(from_idx);
        let Some((byte_start, _)) = char_iter.next() else {
            return "";
        };
        let byte_end = if length < 0 {
            self.0.len()
        } else if length == 0 {
            byte_start
        } else {
            match char_iter.nth((length - 1) as usize) {
                Some((end, _)) => end,
                None => self.0.len(),
            }
        };
        &self.0[byte_start..byte_end]
    }

    #[inline(always)]
    pub fn substring_slice(&self, start: i64, length: i64) -> &[u8] {
        if start < 1 {
            return &[];
        }
        let bytes = self.0.as_bytes();
        let from = (start - 1) as usize;
        if from >= bytes.len() {
            return &[];
        }
        let take = if length < 0 {
            bytes.len() - from
        } else {
            (length as usize).min(bytes.len() - from)
        };
        let to = from + take;
        if bytes[..to].is_ascii() {
            return &bytes[from..to];
        }
        self.substring_str(start, length).as_bytes()
    }

    #[inline(always)]
    pub fn substring_fast(&self, start: usize, length: usize) -> &[u8] {
        let bytes = self.0.as_bytes();
        if start == 0 || start > bytes.len() {
            return &[];
        }
        let from = start - 1;
        let take = length.min(bytes.len() - from);
        &bytes[from..from + take]
    }

    #[inline]
    pub fn substring(&self, start: i64, length: i64) -> Self {
        Self(self.substring_str(start, length).to_owned())
    }

    /// Native `Upper`: per-character mapping that keeps the original character
    /// when its uppercase mapping expands to several characters (`ß` stays
    /// `ß`); `µ` still maps to the single-character Greek capital mu.
    pub fn to_upper(&self) -> Self {
        if self.0.is_ascii() {
            return Self(self.0.to_ascii_uppercase());
        }
        let mut out = String::with_capacity(self.0.len());
        for c in self.0.chars() {
            let mut mapped = c.to_uppercase();
            match (mapped.next(), mapped.next()) {
                (Some(upper), None) => out.push(upper),
                _ => out.push(c),
            }
        }
        Self(out)
    }

    /// Native `Lower`, with the same single-character keep rule as `to_upper`.
    pub fn to_lower(&self) -> Self {
        if self.0.is_ascii() {
            return Self(self.0.to_ascii_lowercase());
        }
        let mut out = String::with_capacity(self.0.len());
        for c in self.0.chars() {
            let mut mapped = c.to_lowercase();
            match (mapped.next(), mapped.next()) {
                (Some(lower), None) => out.push(lower),
                _ => out.push(c),
            }
        }
        Self(out)
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
        Self(self.trim_start_str().to_owned())
    }

    /// Native `RTrim`: trims trailing spaces (' ') only.
    pub fn trim_end_spaces(&self) -> Self {
        Self(self.trim_end_str().to_owned())
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
        let trimmed = self.trim_str();
        let len = trimmed.len();
        if size <= len {
            return Ok(Self(trimmed[..size].to_owned()));
        }
        let needed = size - len;
        let fill_str = if fill.0.is_empty() { " " } else { &fill.0 };
        let mut result = String::with_capacity(size);
        let mut rem = needed;
        while rem >= fill_str.len() {
            result.push_str(fill_str);
            rem -= fill_str.len();
        }
        if rem > 0 {
            result.push_str(&fill_str[..rem]);
        }
        result.push_str(trimmed);
        Ok(Self(result))
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
        let rtrimmed = self.trim_end_str();
        if size <= rtrimmed.len() {
            return Ok(Self(rtrimmed[..size].to_owned()));
        }
        let needed = size - rtrimmed.len();
        let fill_str = if fill.0.is_empty() { " " } else { &fill.0 };
        let mut result = String::with_capacity(size);
        result.push_str(rtrimmed);
        let mut rem = needed;
        while rem >= fill_str.len() {
            result.push_str(fill_str);
            rem -= fill_str.len();
        }
        if rem > 0 {
            result.push_str(&fill_str[..rem]);
        }
        Ok(Self(result))
    }

    #[inline(always)]
    pub fn pad_left_slice<'a>(
        &self,
        size: i64,
        fill: &Self,
        buf: &'a mut [u8; 64],
    ) -> Result<&'a [u8], String> {
        if size <= 0 {
            return Ok(&[]);
        }
        let size = size as usize;
        if size > 64 {
            return Err("pad_left buffer overflow".into());
        }
        let trimmed = self.trim_slice();
        let len = trimmed.len();
        if size <= len {
            let dest = &mut buf[..size];
            dest.copy_from_slice(&trimmed[..size]);
            return Ok(dest);
        }
        let needed = size - len;
        let fill_bytes = if fill.0.is_empty() {
            b" " as &[u8]
        } else {
            fill.0.as_bytes()
        };
        if fill_bytes.len() == 1 {
            buf[..needed].fill(fill_bytes[0]);
            buf[needed..size].copy_from_slice(trimmed);
            return Ok(&buf[..size]);
        }
        let mut idx = 0;
        while idx + fill_bytes.len() <= needed {
            buf[idx..idx + fill_bytes.len()].copy_from_slice(fill_bytes);
            idx += fill_bytes.len();
        }
        if idx < needed {
            let rem = needed - idx;
            buf[idx..idx + rem].copy_from_slice(&fill_bytes[..rem]);
            idx += rem;
        }
        buf[idx..idx + len].copy_from_slice(trimmed);
        Ok(&buf[..size])
    }

    #[inline(always)]
    pub fn pad_left_fast<'a>(&self, size: usize, fill: &Self, buf: &'a mut [u8; 64]) -> &'a [u8] {
        let trimmed = self.trim_slice();
        let len = trimmed.len();
        if size <= len {
            let dest = &mut buf[..size];
            dest.copy_from_slice(&trimmed[..size]);
            return dest;
        }
        let needed = size - len;
        let fill_bytes = if fill.0.is_empty() {
            b" " as &[u8]
        } else {
            fill.0.as_bytes()
        };
        if fill_bytes.len() == 1 {
            buf[..needed].fill(fill_bytes[0]);
            buf[needed..size].copy_from_slice(trimmed);
            return &buf[..size];
        }
        &buf[..size]
    }

    #[inline(always)]
    pub fn pad_left_bytes<'a>(
        &self,
        size: i64,
        fill: &Self,
        buf: &'a mut [u8; 64],
    ) -> Result<&'a str, String> {
        let slice = self.pad_left_slice(size, fill, buf)?;
        Ok(std::str::from_utf8(slice).unwrap_or(""))
    }

    #[inline(always)]
    pub fn pad_right_slice<'a>(
        &self,
        size: i64,
        fill: &Self,
        buf: &'a mut [u8; 64],
    ) -> Result<&'a [u8], String> {
        if size <= 0 {
            return Ok(&[]);
        }
        let size = size as usize;
        if size > 64 {
            return Err("pad_right buffer overflow".into());
        }
        let rtrimmed = self.trim_end_slice();
        let len = rtrimmed.len();
        if size <= len {
            let dest = &mut buf[..size];
            dest.copy_from_slice(&rtrimmed[..size]);
            return Ok(dest);
        }
        buf[..len].copy_from_slice(rtrimmed);
        let fill_bytes = if fill.0.is_empty() {
            b" " as &[u8]
        } else {
            fill.0.as_bytes()
        };
        if fill_bytes.len() == 1 {
            buf[len..size].fill(fill_bytes[0]);
            return Ok(&buf[..size]);
        }
        let mut idx = len;
        while idx + fill_bytes.len() <= size {
            buf[idx..idx + fill_bytes.len()].copy_from_slice(fill_bytes);
            idx += fill_bytes.len();
        }
        if idx < size {
            let rem = size - idx;
            buf[idx..idx + rem].copy_from_slice(&fill_bytes[..rem]);
        }
        Ok(&buf[..size])
    }

    #[inline(always)]
    pub fn pad_right_fast<'a>(&self, size: usize, fill: &Self, buf: &'a mut [u8; 64]) -> &'a [u8] {
        let rtrimmed = self.trim_end_slice();
        let len = rtrimmed.len();
        if size <= len {
            let dest = &mut buf[..size];
            dest.copy_from_slice(&rtrimmed[..size]);
            return dest;
        }
        buf[..len].copy_from_slice(rtrimmed);
        let fill_bytes = if fill.0.is_empty() {
            b" " as &[u8]
        } else {
            fill.0.as_bytes()
        };
        if fill_bytes.len() == 1 {
            buf[len..size].fill(fill_bytes[0]);
            return &buf[..size];
        }
        &buf[..size]
    }

    #[inline(always)]
    pub fn pad_right_bytes<'a>(
        &self,
        size: i64,
        fill: &Self,
        buf: &'a mut [u8; 64],
    ) -> Result<&'a str, String> {
        let slice = self.pad_right_slice(size, fill, buf)?;
        Ok(std::str::from_utf8(slice).unwrap_or(""))
    }

    #[inline(always)]
    pub fn concat_with_sep_slice<'a>(
        &self,
        other: &Self,
        sep: Option<&Self>,
        buf: &'a mut [u8; 64],
    ) -> Result<&'a [u8], String> {
        let left = self.trim_end_slice();
        let sep_bytes = sep.map(|s| s.0.as_bytes()).unwrap_or(b"");
        let right = other.0.as_bytes();
        let l_len = left.len();
        let s_len = sep_bytes.len();
        let r_len = right.len();
        let total_len = l_len + s_len + r_len;
        if total_len > 64 {
            return Err("concat buffer overflow".into());
        }
        buf[..l_len].copy_from_slice(left);
        buf[l_len..l_len + s_len].copy_from_slice(sep_bytes);
        buf[l_len + s_len..total_len].copy_from_slice(right);
        Ok(&buf[..total_len])
    }

    #[inline(always)]
    pub fn concat_with_sep_fast<'a>(
        &self,
        other: &Self,
        sep: Option<&Self>,
        buf: &'a mut [u8; 64],
    ) -> &'a [u8] {
        let left = self.trim_end_slice();
        let sep_bytes = sep.map(|s| s.0.as_bytes()).unwrap_or(b"");
        let right = other.0.as_bytes();
        let l_len = left.len();
        let s_len = sep_bytes.len();
        let r_len = right.len();
        let total_len = l_len + s_len + r_len;
        buf[..l_len].copy_from_slice(left);
        buf[l_len..l_len + s_len].copy_from_slice(sep_bytes);
        buf[l_len + s_len..total_len].copy_from_slice(right);
        &buf[..total_len]
    }

    #[inline(always)]
    pub fn concat_with_sep_bytes<'a>(
        &self,
        other: &Self,
        sep: Option<&Self>,
        buf: &'a mut [u8; 64],
    ) -> Result<&'a str, String> {
        let slice = self.concat_with_sep_slice(other, sep, buf)?;
        Ok(std::str::from_utf8(slice).unwrap_or(""))
    }

    #[inline(always)]
    pub fn concat_slice<'a>(
        &self,
        other: &Self,
        buf: &'a mut [u8; 64],
    ) -> Result<&'a [u8], String> {
        let l_len = self.0.len();
        let r_len = other.0.len();
        let total_len = l_len + r_len;
        if total_len > 64 {
            return Err("concat buffer overflow".into());
        }
        buf[..l_len].copy_from_slice(self.0.as_bytes());
        buf[l_len..total_len].copy_from_slice(other.0.as_bytes());
        Ok(&buf[..total_len])
    }

    #[inline(always)]
    pub fn concat_fast<'a>(&self, other: &Self, buf: &'a mut [u8; 64]) -> &'a [u8] {
        let l_len = self.0.len();
        let r_len = other.0.len();
        let total_len = l_len + r_len;
        buf[..l_len].copy_from_slice(self.0.as_bytes());
        buf[l_len..total_len].copy_from_slice(other.0.as_bytes());
        &buf[..total_len]
    }

    #[inline(always)]
    pub fn concat_bytes<'a>(&self, other: &Self, buf: &'a mut [u8; 64]) -> Result<&'a str, String> {
        let slice = self.concat_slice(other, buf)?;
        Ok(std::str::from_utf8(slice).unwrap_or(""))
    }

    #[inline(always)]
    pub fn to_upper_slice<'a>(&'a self, buf: &'a mut [u8; 64]) -> &'a [u8] {
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        if len <= 64 {
            let dst = &mut buf[..len];
            dst.copy_from_slice(bytes);
            dst.make_ascii_uppercase();
            return dst;
        }
        bytes
    }

    #[inline(always)]
    pub fn to_upper_bytes<'a>(&'a self, buf: &'a mut [u8; 64]) -> &'a str {
        let slice = self.to_upper_slice(buf);
        std::str::from_utf8(slice).unwrap_or(self.0.as_str())
    }

    #[inline(always)]
    pub fn to_lower_slice<'a>(&'a self, buf: &'a mut [u8; 64]) -> &'a [u8] {
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        if len <= 64 {
            let dst = &mut buf[..len];
            dst.copy_from_slice(bytes);
            dst.make_ascii_lowercase();
            return dst;
        }
        bytes
    }

    #[inline(always)]
    pub fn to_lower_bytes<'a>(&'a self, buf: &'a mut [u8; 64]) -> &'a str {
        let slice = self.to_lower_slice(buf);
        std::str::from_utf8(slice).unwrap_or(self.0.as_str())
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
    #[inline(always)]
    pub fn starts_with(&self, prefix: &Self) -> bool {
        self.0.as_bytes().starts_with(prefix.0.as_bytes())
    }

    /// Native `EndsWith`: ordinal comparison.
    #[inline(always)]
    pub fn ends_with(&self, suffix: &Self) -> bool {
        self.0.as_bytes().ends_with(suffix.0.as_bytes())
    }

    /// Native `Contains`: ordinal comparison.
    #[inline(always)]
    pub fn contains(&self, needle: &Self) -> bool {
        let n = needle.0.as_bytes();
        if n.is_empty() {
            return true;
        }
        let h = self.0.as_bytes();
        if n.len() > h.len() {
            return false;
        }
        let first = n[0];
        let limit = h.len() - n.len();
        let mut offset = 0;
        while offset <= limit {
            if h[offset] == first && &h[offset..offset + n.len()] == n {
                return true;
            }
            offset += 1;
        }
        false
    }

    /// Native `StringSearch` / `StrSearch` / `IndexOf`: 1-based index (0 if not found).
    #[inline(always)]
    pub fn str_search(&self, find: &Self, start: i64) -> i64 {
        let s1 = self.0.as_bytes();
        let s2 = find.0.as_bytes();
        let start = if start <= 0 { 1 } else { start };
        let from = (start - 1) as usize;
        if from >= s1.len() {
            return 0;
        }
        if s2.is_empty() {
            return start;
        }
        if s2.len() > s1.len() - from {
            return 0;
        }
        let first = s2[0];
        let limit = s1.len() - s2.len();
        let mut offset = from;
        while offset <= limit {
            if s1[offset] == first && &s1[offset..offset + s2.len()] == s2 {
                return (offset + 1) as i64;
            }
            offset += 1;
        }
        0
    }

    /// Native `Left`: returns initial `size` characters. Negative size yields empty.
    #[inline(always)]
    pub fn left_str(&self, size: i64) -> &str {
        if size <= 0 {
            return "";
        }
        let size = size as usize;
        let len = self.0.len();
        if size >= len {
            return &self.0;
        }
        &self.0[..size]
    }

    #[inline(always)]
    pub fn left_slice(&self, size: i64) -> &[u8] {
        if size <= 0 {
            return &[];
        }
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        let size = (size as usize).min(len);
        &bytes[..size]
    }

    #[inline(always)]
    pub fn left_slice_fast(&self, size: usize) -> &[u8] {
        let bytes = self.0.as_bytes();
        let size = size.min(bytes.len());
        &bytes[..size]
    }

    #[inline]
    pub fn left(&self, size: i64) -> Self {
        Self(self.left_str(size).to_owned())
    }

    /// Native `Right`: returns trailing `size` characters. Negative size throws ArgumentOutOfRangeException.
    #[inline(always)]
    pub fn right_str(&self, size: i64) -> Result<&str, String> {
        if size < 0 {
            return Err(
                "ArgumentOutOfRangeException: startIndex cannot be larger than length of string"
                    .into(),
            );
        }
        let size = size as usize;
        let len = self.0.len();
        if size >= len {
            return Ok(&self.0);
        }
        let start = len - size;
        Ok(&self.0[start..])
    }

    #[inline(always)]
    pub fn right_slice(&self, size: i64) -> Result<&[u8], String> {
        if size < 0 {
            return Err(
                "ArgumentOutOfRangeException: startIndex cannot be larger than length of string"
                    .into(),
            );
        }
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        let size = size as usize;
        if size >= len {
            return Ok(bytes);
        }
        let start = len - size;
        Ok(&bytes[start..])
    }

    #[inline(always)]
    pub fn right_slice_fast(&self, size: usize) -> &[u8] {
        let bytes = self.0.as_bytes();
        let len = bytes.len();
        if size >= len {
            return bytes;
        }
        let start = len - size;
        &bytes[start..]
    }

    #[inline]
    pub fn right(&self, size: i64) -> Result<Self, String> {
        self.right_str(size).map(|s| Self(s.to_owned()))
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

    /// Native `StringUtil.Format(value, v1..v9)`: replaces `%1`..`%9` (with `\%` escape) or `{0}`..`{8}`.
    pub fn format_template(pattern: &Self, args: &[Self]) -> Result<Self, String> {
        let value = &pattern.0;
        if value.is_empty() {
            return Ok(Self(String::new()));
        }
        let get_arg = |idx: usize| -> &str { args.get(idx).map(|a| a.0.as_str()).unwrap_or("") };
        let chars: Vec<char> = value.chars().collect();
        let mut out = String::with_capacity(value.len());
        let mut flag = false;
        let mut i = 0;
        while i < chars.len() {
            let c = chars[i];
            if c == '\\' {
                if i + 1 < chars.len() && chars[i + 1] != '%' {
                    out.push(c);
                }
                flag = true;
                i += 1;
            } else if c == '%' && i + 1 < chars.len() && chars[i + 1].is_ascii_digit() && !flag {
                let digit = (chars[i + 1] as u8 - b'0') as usize;
                if digit == 0 {
                    return Err("Format placeholder %0 is out of range (expected %1..%9)".into());
                }
                out.push_str(get_arg(digit - 1));
                i += 2;
                flag = false;
            } else {
                out.push(c);
                flag = false;
                i += 1;
            }
        }
        if out == *value && out.contains('{') {
            for idx in 0..9 {
                let token = format!("{{{idx}}}");
                if out.contains(&token) {
                    out = out.replace(&token, get_arg(idx));
                }
            }
        }
        Self::new(&out)
    }

    /// Native `StringUtil.Replicate(str, size)`: repeats string `size` times (<= 0 yields empty).
    pub fn replicate(&self, size: i64) -> Result<Self, String> {
        if size <= 0 || self.0.is_empty() {
            return Ok(Self(String::new()));
        }
        let count = usize::try_from(size).map_err(|_| "Replicate size overflow")?;
        let total_bytes = self
            .0
            .len()
            .checked_mul(count)
            .ok_or("text value exceeds maximum length")?;
        if total_bytes > MAX_TEXT_BYTES {
            return Err(format!("text value exceeds {MAX_TEXT_BYTES}-byte limit"));
        }
        Ok(Self(self.0.repeat(count)))
    }

    /// Native `StringUtil.CharAt(s, idx)`: 1-based character extraction (out-of-range yields empty).
    pub fn char_at(&self, idx: i64) -> Self {
        if idx <= 0 {
            return Self(String::new());
        }
        self.substring(idx, 1)
    }

    /// Native `StringUtil.StringSearchRev(s1, s2, start)`: 1-based reverse search within `s1[..start]`.
    pub fn str_search_rev(&self, find: &Self, start: i64) -> i64 {
        if start == 0 || self.0.is_empty() {
            return 0;
        }
        let s1 = &self.0;
        let s2 = &find.0;
        let end = if start < 0 || (start as usize) > s1.len() {
            s1.len()
        } else {
            start as usize
        };
        if let Some(pos) = s1[..end].rfind(s2) {
            (pos + 1) as i64
        } else {
            0
        }
    }

    /// Native `StringUtil.BoolToStr(bool)`: `"true"` or `"false"`.
    pub fn bool_to_str(value: bool) -> Self {
        Self(if value {
            "true".to_owned()
        } else {
            "false".to_owned()
        })
    }

    /// Native `StringUtil.StrToBool(string)`: true iff `"true"` (case-insensitive) or `"1"`.
    pub fn to_boolean(&self) -> bool {
        self.0.eq_ignore_ascii_case("true") || self.0 == "1"
    }

    /// Native `StringUtil.NewLine()`: `"\r\n"`.
    pub fn newline() -> Self {
        Self("\r\n".to_owned())
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
