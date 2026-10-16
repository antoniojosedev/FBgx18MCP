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
    pub fn year(self) -> i64 {
        if self.date.is_empty() && !self.is_empty() {
            1
        } else {
            self.date.year()
        }
    }
    pub fn month(self) -> i64 {
        if self.date.is_empty() && !self.is_empty() {
            1
        } else {
            self.date.month()
        }
    }
    pub fn day(self) -> i64 {
        if self.date.is_empty() && !self.is_empty() {
            1
        } else {
            self.date.day()
        }
    }
    pub fn hour(self) -> i64 {
        i64::from(self.seconds / 3600)
    }
    pub fn minute(self) -> i64 {
        i64::from(self.seconds % 3600 / 60)
    }
    pub fn second(self) -> i64 {
        i64::from(self.seconds % 60)
    }
    pub fn day_of_week(self) -> i64 {
        if self.is_empty() {
            0
        } else {
            self.date.day_of_week()
        }
    }
    pub fn end_of_month(self) -> Self {
        if self.is_empty() {
            return self;
        }
        let year = self.year();
        let month = self.month();
        let last_day = crate::date::month_days(year, month);
        let date = Date::ymd(year, month, last_day).unwrap_or(self.date);
        Self {
            date,
            seconds: self.seconds,
        }
    }
    pub fn reset_time(self) -> Self {
        Self {
            date: self.date,
            seconds: 0,
        }
    }
    pub fn age(self, current: Self) -> i64 {
        if self.is_empty() || current.is_empty() {
            return 0;
        }
        let mut age = current.year() - self.year();
        if current.month() < self.month()
            || (current.month() == self.month() && current.day() < self.day())
        {
            age -= 1;
        }
        age
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
