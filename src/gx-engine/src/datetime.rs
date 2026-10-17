// Timezone-free Gregorian DateTime, integral seconds only. No SDK or clock access.
use crate::date::Date;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct DateTime {
    date: Date,
    seconds: u32,
}

impl DateTime {
    pub fn empty() -> Self {
        Self {
            date: Date::empty(),
            seconds: 0,
        }
    }
    pub fn from_parts(
        [year, month, day, hour, minute, second]: [i64; 6],
        max: Option<u16>,
    ) -> Result<Self, String> {
        let date = Date::ymd_with_window(year, month, day, max)?;
        // Observed native construction preserves the date but resets the entire
        // time when any time component is invalid. Invalid dates retain valid time.
        let seconds =
            if (0..24).contains(&hour) && (0..60).contains(&minute) && (0..60).contains(&second) {
                (hour * 3600 + minute * 60 + second) as u32
            } else {
                0
            };
        Ok(Self { date, seconds })
    }
    pub fn is_empty(self) -> bool {
        self.date.is_empty() && self.seconds == 0
    }
    /// Render as the measured `TToC` exchange picture: `yyyy-MM-ddTHH:mm:ss`.
    /// The empty sentinel renders `"0000-00-00T00:00:00"`, and a time-only value
    /// keeps the empty date (`"0000-00-00T14:30:45"`); the context-less native
    /// helper substitutes the current date there, which this timezone-free
    /// engine deliberately does not model.
    pub fn to_iso_text(self) -> String {
        let (year, month, day) = self.date.components();
        format!(
            "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}",
            self.hour(),
            self.minute(),
            self.second()
        )
    }
    #[inline(always)]
    pub fn components(self) -> (i64, i64, i64) {
        if self.date.is_empty() && !self.is_empty() {
            (1, 1, 1)
        } else {
            self.date.components()
        }
    }
    #[inline(always)]
    pub fn year(self) -> i64 {
        if self.date.is_empty() && !self.is_empty() {
            1
        } else {
            self.date.year()
        }
    }
    #[inline(always)]
    pub fn month(self) -> i64 {
        if self.date.is_empty() && !self.is_empty() {
            1
        } else {
            self.date.month()
        }
    }
    #[inline(always)]
    pub fn day(self) -> i64 {
        if self.date.is_empty() && !self.is_empty() {
            1
        } else {
            self.date.day()
        }
    }
    #[inline(always)]
    pub fn hour(self) -> i64 {
        i64::from(self.seconds / 3600)
    }
    #[inline(always)]
    pub fn minute(self) -> i64 {
        i64::from(self.seconds % 3600 / 60)
    }
    #[inline(always)]
    pub fn second(self) -> i64 {
        i64::from(self.seconds % 60)
    }
    #[inline(always)]
    pub fn day_of_week(self) -> i64 {
        if self.is_empty() {
            0
        } else {
            self.date.day_of_week()
        }
    }
    #[inline(always)]
    pub fn end_of_month(self) -> Self {
        Self {
            date: self.date.end_of_month(),
            seconds: self.seconds,
        }
    }
    pub fn reset_time(self) -> Self {
        Self {
            date: self.date,
            seconds: 0,
        }
    }
    #[inline(always)]
    pub fn age(self, current: Self) -> i64 {
        if self.is_empty() || current.is_empty() {
            return 0;
        }
        self.date.age(current.date)
    }
    pub fn add_days(self, days: i64) -> Result<Self, String> {
        Ok(Self {
            date: self.date.add_days(days)?,
            seconds: self.seconds,
        })
    }
    pub fn add_months(self, months: i64) -> Result<Self, String> {
        Ok(Self {
            date: self.date.add_months(months)?,
            seconds: self.seconds,
        })
    }
    pub fn add_years(self, years: i64) -> Result<Self, String> {
        Ok(Self {
            date: self.date.add_years(years)?,
            seconds: self.seconds,
        })
    }
    pub fn add_minutes(self, minutes: i64) -> Result<Self, String> {
        self.add_seconds(minutes.checked_mul(60).ok_or("DateTime minutes overflow")?)
    }
    pub fn add_hours(self, hours: i64) -> Result<Self, String> {
        self.add_seconds(hours.checked_mul(3600).ok_or("DateTime hours overflow")?)
    }
    pub fn add_seconds(self, seconds: i64) -> Result<Self, String> {
        let time = i64::from(self.seconds)
            .checked_add(seconds)
            .ok_or("DateTime seconds overflow")?;
        Ok(Self {
            date: self.date.add_days(time.div_euclid(86400))?,
            seconds: time.rem_euclid(86400) as u32,
        })
    }
    pub fn difference(self, other: Self) -> i64 {
        self.date.difference(other.date) * 86400 + i64::from(self.seconds)
            - i64::from(other.seconds)
    }
    /// Render exactly as the measured native `&DateTime.ToString()` picture:
    /// `MM/dd/yy hh:mm AM/PM`, 12-hour clock, seconds dropped; empty renders
    /// `"  /  /   12:00 AM"`. The date part uses the same component overrides as
    /// `year()`/`month()`/`day()`, so an empty date with a valid time renders
    /// `01/01/01` (measured: `YMDHMSToT(0, 0, 0, 14, 5, 9)` -> `01/01/01 02:05 PM`).
    pub fn to_text(self) -> String {
        if self.is_empty() {
            return "  /  /   12:00 AM".to_string();
        }
        let date = format!(
            "{:02}/{:02}/{:02}",
            self.month(),
            self.day(),
            self.year() % 100
        );
        let hour = self.hour();
        let (hour, suffix) = match hour {
            0 => (12, "AM"),
            1..=11 => (hour, "AM"),
            12 => (12, "PM"),
            _ => (hour - 12, "PM"),
        };
        format!("{date} {hour:02}:{:02} {suffix}", self.minute())
    }
}
