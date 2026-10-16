// Gregorian Date subset, independent of the SDK. Day zero represents .NET's
// empty Date sentinel (0001-01-01); component access on empty returns zero.
// Two-digit years require a measured environment policy and are not guessed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct Date(i32);

const MAX_DAY: i64 = 3_652_058;

fn leap(year: i64) -> bool {
    year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)
}
fn before_year(year: i64) -> i64 {
    let y = year - 1;
    y * 365 + y / 4 - y / 100 + y / 400
}
fn month_days(year: i64, month: i64) -> i64 {
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

impl Date {
    pub fn empty() -> Self {
        Self(0)
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
    fn full_year_date(year: i64, month: i64, day: i64) -> Result<Self, String> {
        if !(1..=9999).contains(&year) {
            return Err("Date year outside 0001..9999".into());
        }
        if !(1..=12).contains(&month) || day < 1 || day > month_days(year, month) {
            return Ok(Self::empty());
        }
        let mut ordinal = before_year(year) + day - 1;
        for m in 1..month {
            ordinal += month_days(year, m);
        }
        Ok(Self(ordinal as i32))
    }
    pub fn is_empty(self) -> bool {
        self.0 == 0
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
        let ordinal = i64::from(self.0)
            .checked_add(days)
            .ok_or("Date arithmetic overflow")?;
        if !(0..=MAX_DAY).contains(&ordinal) {
            return Err("Date arithmetic outside 0001..9999".into());
        }
        Ok(Self(ordinal as i32))
    }
    pub fn add_months(self, months: i64) -> Result<Self, String> {
        let (year, month, day) = if self.is_empty() {
            (1, 1, 1)
        } else {
            self.components()
        };
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
        self.add_months(years.checked_mul(12).ok_or("Date year offset overflow")?)
    }
    pub fn difference(self, other: Self) -> i64 {
        i64::from(self.0) - i64::from(other.0)
    }
    fn components(self) -> (i64, i64, i64) {
        if self.is_empty() {
            return (0, 0, 0);
        }
        let ordinal = i64::from(self.0);
        let (mut low, mut high) = (1, 10000);
        while low + 1 < high {
            let middle = (low + high) / 2;
            if before_year(middle) <= ordinal {
                low = middle;
            } else {
                high = middle;
            }
        }
        let mut day = ordinal - before_year(low);
        let mut month = 1;
        while day >= month_days(low, month) {
            day -= month_days(low, month);
            month += 1;
        }
        (low, month, day + 1)
    }
    pub fn year(self) -> i64 {
        self.components().0
    }
    pub fn month(self) -> i64 {
        self.components().1
    }
    pub fn day(self) -> i64 {
        self.components().2
    }
}
