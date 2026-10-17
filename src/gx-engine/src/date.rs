// Gregorian Date subset, independent of the SDK. Day zero represents .NET's
// empty Date sentinel (0001-01-01); component access on empty returns zero.
// Two-digit years require a measured environment policy and are not guessed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct Date {
    ordinal: i32,
    year: u16,
    month: u8,
    day: u8,
}

const MAX_DAY: i64 = 3_652_058;

fn leap(year: i64) -> bool {
    year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)
}
fn before_year(year: i64) -> i64 {
    let y = year - 1;
    y * 365 + y / 4 - y / 100 + y / 400
}
pub(crate) fn month_days(year: i64, month: i64) -> i64 {
    match month {
        2 => {
            if leap(year) {
                29
            } else {
                28
            }
        }
        4 | 6 | 9 | 11 => 30,
        _ => 31,
    }
}

/// Measured `CToD` trailing-time grammar: `H[:mm[:ss]][.fraction]` with an
/// optional `AM`/`PM` marker (case-insensitive, optional separating space) that
/// switches the hour to the 12-hour clock. Sub-second digits are read and
/// discarded. Compact forms (`143045`) and out-of-range components are invalid.
fn valid_time_of_day(text: &str) -> bool {
    let mut body = text.trim_matches(' ');
    let upper = body.to_ascii_uppercase();
    let twelve_hour = if upper.ends_with("AM") {
        body = body[..body.len() - 2].trim_matches(' ');
        Some(false)
    } else if upper.ends_with("PM") {
        body = body[..body.len() - 2].trim_matches(' ');
        Some(true)
    } else {
        None
    };
    if body.is_empty() {
        return false;
    }
    if let Some((head, fraction)) = body.split_once('.') {
        if fraction.is_empty() || !fraction.bytes().all(|byte| byte.is_ascii_digit()) {
            return false;
        }
        body = head;
    }
    let fields: Vec<&str> = body.split(':').collect();
    if !(2..=3).contains(&fields.len()) {
        return false;
    }
    let number = |field: &str| -> Option<i64> {
        if field.is_empty() || field.len() > 2 || !field.bytes().all(|byte| byte.is_ascii_digit()) {
            return None;
        }
        field.parse::<i64>().ok()
    };
    let (Some(hour), Some(minute)) = (number(fields[0]), number(fields[1])) else {
        return false;
    };
    let second = if fields.len() == 3 {
        match number(fields[2]) {
            Some(value) => value,
            None => return false,
        }
    } else {
        0
    };
    let hour_range = match twelve_hour {
        Some(_) => 1..=12,
        None => 0..=23,
    };
    hour_range.contains(&hour) && (0..=59).contains(&minute) && (0..=59).contains(&second)
}

impl Date {
    pub fn empty() -> Self {
        Self {
            ordinal: 0,
            year: 0,
            month: 0,
            day: 0,
        }
    }
    pub fn ymd(year: i64, month: i64, day: i64) -> Result<Self, String> {
        Self::ymd_with_window(year, month, day, None)
    }
    pub fn ymd_with_window(
        mut year: i64,
        month: i64,
        day: i64,
        max: Option<u16>,
    ) -> Result<Self, String> {
        if let Some(max) = max {
            if !(99..=9999).contains(&max) {
                return Err("invalid Date two-digit-year maximum".into());
            }
            if (0..100).contains(&year) {
                let max = i64::from(max);
                year += (max / 100) * 100;
                if year > max {
                    year -= 100;
                }
            }
        }
        if !(1..=9999).contains(&year) || (max.is_none() && year < 100) {
            return Err(
                "Date year must be 100..9999; two-digit-year window is not configured".into(),
            );
        }
        Self::full_year_date(year, month, day)
    }
    pub(crate) fn full_year_date(year: i64, month: i64, day: i64) -> Result<Self, String> {
        if !(1..=9999).contains(&year) {
            return Err("Date year outside 0001..9999".into());
        }
        if !(1..=12).contains(&month) || day < 1 || day > month_days(year, month) {
            return Ok(Self::empty());
        }
        const CUMULATIVE_DAYS: [i64; 13] =
            [0, 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];
        let leap_extra = if month > 2 && leap(year) { 1 } else { 0 };
        let ordinal = before_year(year) + CUMULATIVE_DAYS[month as usize] + leap_extra + day - 1;
        Ok(Self {
            ordinal: ordinal as i32,
            year: year as u16,
            month: month as u8,
            day: day as u8,
        })
    }
    pub fn is_empty(self) -> bool {
        self.ordinal == 0
    }
    /// Render as the measured `DToC` exchange picture: `yyyy-MM-dd`, with the
    /// empty sentinel rendering `"0000-00-00"` (unlike the `MM/dd/yy` display
    /// picture of `to_text`).
    pub fn to_iso_text(self) -> String {
        if self.is_empty() {
            return "0000-00-00".to_string();
        }
        let (year, month, day) = self.components();
        format!("{year:04}-{month:02}-{day:02}")
    }
    /// Parse the measured `CToD` grammar. Three numeric components separated by
    /// any mix of `-`, `/` or `.`; a four-digit first component is read
    /// `yyyy`-first, otherwise the order is `M/d/[yy]yy` and a two-digit year
    /// resolves through the environment window. A trailing part is accepted only
    /// when it is a valid time after a space or `T`; any other trailing text, an
    /// invalid time, or an out-of-range component yields the empty date, as
    /// measured. Input is trimmed. Timezone-offset forms (`2024-02-29-01`) are
    /// outside the admitted grammar.
    pub fn parse_cto_d(text: &str, two_digit_max: Option<u16>) -> Date {
        let trimmed = text.trim_matches(' ');
        if trimmed.is_empty() {
            return Self::empty();
        }
        let (date_part, time_part) = match trimmed.find([' ', 'T']) {
            Some(index) => (
                &trimmed[..index],
                Some(trimmed[index + 1..].trim_matches(' ')),
            ),
            None => (trimmed, None),
        };
        if let Some(time) = time_part
            && (time.is_empty() || !valid_time_of_day(time))
        {
            return Self::empty();
        }
        let mut components = [0_i64; 3];
        let mut count = 0;
        let mut first_digits = 0_usize;
        for (index, part) in date_part.split(['-', '/', '.']).enumerate() {
            if index >= 3
                || part.is_empty()
                || part.len() > 4
                || !part.bytes().all(|byte| byte.is_ascii_digit())
            {
                return Self::empty();
            }
            if index == 0 {
                first_digits = part.len();
            }
            components[index] = part.parse::<i64>().unwrap_or(-1);
            count += 1;
        }
        if count != 3 {
            return Self::empty();
        }
        let (year, month, day) = if first_digits == 4 {
            (components[0], components[1], components[2])
        } else {
            (components[2], components[0], components[1])
        };
        Self::ymd_with_window(year, month, day, two_digit_max).unwrap_or_else(|_| Self::empty())
    }
    /// Render exactly as the measured native `&Date.ToString()` picture:
    /// `MM/dd/yy` with zero padding; empty renders `"  /  /  "` (two spaces,
    /// slash, two spaces, slash, two spaces). The two-digit year is modulo 100
    /// (measured: year 100 and 1900 render `00`).
    pub fn to_text(self) -> String {
        if self.is_empty() {
            return "  /  /  ".to_string();
        }
        let (year, month, day) = self.components();
        format!("{:02}/{:02}/{:02}", month, day, year % 100)
    }
    pub fn add_days(self, days: i64) -> Result<Self, String> {
        let ordinal = i64::from(self.ordinal)
            .checked_add(days)
            .ok_or("Date arithmetic overflow")?;
        if !(0..=MAX_DAY).contains(&ordinal) {
            return Err("Date arithmetic outside 0001..9999".into());
        }
        if self.ordinal == 0 {
            let (y, m, d) = Self::ordinal_to_components(ordinal as i32);
            return Ok(Self {
                ordinal: ordinal as i32,
                year: y as u16,
                month: m as u8,
                day: d as u8,
            });
        }
        if (-31..=31).contains(&days) {
            let cur_days = month_days(i64::from(self.year), i64::from(self.month));
            let new_day = i64::from(self.day) + days;
            if (1..=cur_days).contains(&new_day) {
                return Ok(Self {
                    ordinal: ordinal as i32,
                    year: self.year,
                    month: self.month,
                    day: new_day as u8,
                });
            }
            if new_day > cur_days && self.month < 12 {
                let next_day = new_day - cur_days;
                let next_m = self.month + 1;
                let next_days = month_days(i64::from(self.year), i64::from(next_m));
                if next_day <= next_days {
                    return Ok(Self {
                        ordinal: ordinal as i32,
                        year: self.year,
                        month: next_m,
                        day: next_day as u8,
                    });
                }
            }
        }
        let (y, m, d) = Self::ordinal_to_components(ordinal as i32);
        Ok(Self {
            ordinal: ordinal as i32,
            year: y as u16,
            month: m as u8,
            day: d as u8,
        })
    }
    pub fn add_months(self, months: i64) -> Result<Self, String> {
        let (year, month, day) = if self.is_empty() {
            (1, 1, 1)
        } else {
            (
                i64::from(self.year),
                i64::from(self.month),
                i64::from(self.day),
            )
        };
        if (-12..=12).contains(&months) {
            let m_sum = month + months;
            if (1..=12).contains(&m_sum) {
                return Self::full_year_date(year, m_sum, day.min(month_days(year, m_sum)));
            }
        }
        let index = ((year - 1) * 12 + month - 1)
            .checked_add(months)
            .ok_or("Date month offset overflow")?;
        if !(0..9999 * 12).contains(&index) {
            return Err("Date month offset outside 0001..9999".into());
        }
        let year = index / 12 + 1;
        let month = index % 12 + 1;
        Self::full_year_date(year, month, day.min(month_days(year, month)))
    }
    pub fn add_years(self, years: i64) -> Result<Self, String> {
        let (year, month, day) = if self.is_empty() {
            (1, 1, 1)
        } else {
            (
                i64::from(self.year),
                i64::from(self.month),
                i64::from(self.day),
            )
        };
        let new_year = year.checked_add(years).ok_or("Date year overflow")?;
        if !(1..=9999).contains(&new_year) {
            return Err("Date year outside 0001..9999".into());
        }
        let new_day = if month == 2 && day == 29 && !leap(new_year) {
            28
        } else {
            day
        };
        Self::full_year_date(new_year, month, new_day)
    }
    pub fn difference(self, other: Self) -> i64 {
        i64::from(self.ordinal) - i64::from(other.ordinal)
    }
    #[inline(always)]
    fn ordinal_to_components(ord: i32) -> (i64, i64, i64) {
        if ord == 0 {
            return (0, 0, 0);
        }
        let z = i64::from(ord) + 306;
        let era = (if z >= 0 { z } else { z - 146096 }) / 146097;
        let doe = z - era * 146097;
        let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        let mut y = yoe + era * 400;
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        let mp = (5 * doy + 2) / 153;
        let d = doy - (153 * mp + 2) / 5 + 1;
        let m = if mp < 10 { mp + 3 } else { mp - 9 };
        if m <= 2 {
            y += 1;
        }
        (y, m, d)
    }
    #[inline(always)]
    pub(crate) fn components(self) -> (i64, i64, i64) {
        (
            i64::from(self.year),
            i64::from(self.month),
            i64::from(self.day),
        )
    }
    #[inline(always)]
    pub fn year(self) -> i64 {
        i64::from(self.year)
    }
    #[inline(always)]
    pub fn month(self) -> i64 {
        i64::from(self.month)
    }
    #[inline(always)]
    pub fn day(self) -> i64 {
        i64::from(self.day)
    }
    pub fn hour(self) -> i64 {
        0
    }
    pub fn minute(self) -> i64 {
        0
    }
    pub fn second(self) -> i64 {
        0
    }
    #[inline(always)]
    pub fn day_of_week(self) -> i64 {
        if self.is_empty() {
            return 0;
        }
        let ordinal = i64::from(self.ordinal);
        (ordinal + 1) % 7 + 1
    }
    #[inline(always)]
    pub fn end_of_month(self) -> Self {
        if self.is_empty() {
            return self;
        }
        let last_day = month_days(i64::from(self.year), i64::from(self.month)) as u8;
        let delta = (last_day - self.day) as i32;
        Self {
            ordinal: self.ordinal + delta,
            year: self.year,
            month: self.month,
            day: last_day,
        }
    }
    pub fn reset_time(self) -> Self {
        self
    }
    pub fn age(self, current: Self) -> i64 {
        if self.is_empty() || current.is_empty() {
            return 0;
        }
        let b_year = i64::from(self.year);
        let b_month = i64::from(self.month);
        let b_day = i64::from(self.day);
        let c_year = i64::from(current.year);
        let c_month = i64::from(current.month);
        let c_day = i64::from(current.day);
        let mut age = c_year - b_year;
        if c_month < b_month || (c_month == b_month && c_day < b_day) {
            age -= 1;
        }
        age
    }
}
