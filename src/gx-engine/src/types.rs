//! Typed semantic values. Decimal storage and Numeric display width are separate.
//! Checked storage deliberately diagnoses overflow instead of native integer wrapping.
use std::str::FromStr;

const MAX_COEFFICIENT: i128 = (1_i128 << 96) - 1;
const MAX_SCALE: u32 = 28;

/// Small unsigned wide integer used only for exact Decimal intermediates.
/// A 96-bit coefficient aligned by 10^28 or multiplied by another coefficient
/// needs up to 192 bits; four limbs leave room for division scaling as well.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
struct Wide([u64; 4]);

impl Wide {
    const ZERO: Self = Self([0; 4]);
    const ONE: Self = Self([1, 0, 0, 0]);

    fn from_u128(value: u128) -> Self {
        Self([value as u64, (value >> 64) as u64, 0, 0])
    }

    fn to_u128(self) -> Option<u128> {
        (self.0[2] == 0 && self.0[3] == 0)
            .then(|| u128::from(self.0[0]) | (u128::from(self.0[1]) << 64))
    }

    fn is_zero(self) -> bool {
        self == Self::ZERO
    }

    fn is_odd(self) -> bool {
        self.0[0] & 1 != 0
    }

    fn checked_add(self, other: Self) -> Option<Self> {
        let mut result = [0; 4];
        let mut carry = 0_u128;
        for (index, limb) in result.iter_mut().enumerate() {
            let sum = u128::from(self.0[index]) + u128::from(other.0[index]) + carry;
            *limb = sum as u64;
            carry = sum >> 64;
        }
        (carry == 0).then_some(Self(result))
    }

    fn checked_sub(self, other: Self) -> Option<Self> {
        if self < other {
            return None;
        }
        let mut result = [0; 4];
        let mut borrow = 0_u128;
        for (index, limb) in result.iter_mut().enumerate() {
            let minuend = u128::from(self.0[index]);
            let subtrahend = u128::from(other.0[index]) + borrow;
            *limb = minuend.wrapping_sub(subtrahend) as u64;
            borrow = u128::from(minuend < subtrahend);
        }
        Some(Self(result))
    }

    fn checked_mul_small(self, factor: u32) -> Option<Self> {
        let mut result = [0; 4];
        let mut carry = 0_u128;
        for (index, limb) in result.iter_mut().enumerate() {
            let product = u128::from(self.0[index]) * u128::from(factor) + carry;
            *limb = product as u64;
            carry = product >> 64;
        }
        (carry == 0).then_some(Self(result))
    }

    fn checked_mul_pow10(mut self, power: u32) -> Option<Self> {
        for _ in 0..power {
            self = self.checked_mul_small(10)?;
        }
        Some(self)
    }

    fn checked_mul(self, other: Self) -> Option<Self> {
        let mut product = [0_u64; 4];
        for left_index in 0..4 {
            let mut carry = 0_u128;
            for right_index in 0..(4 - left_index) {
                let index = left_index + right_index;
                let value = u128::from(self.0[left_index]) * u128::from(other.0[right_index])
                    + u128::from(product[index])
                    + carry;
                product[index] = value as u64;
                carry = value >> 64;
            }
            if carry != 0
                || (4 - left_index..4)
                    .any(|right_index| self.0[left_index] != 0 && other.0[right_index] != 0)
            {
                return None;
            }
        }
        Some(Self(product))
    }

    fn bit(self, index: usize) -> bool {
        self.0[index / 64] & (1_u64 << (index % 64)) != 0
    }

    fn set_bit(&mut self, index: usize) {
        self.0[index / 64] |= 1_u64 << (index % 64);
    }

    fn div_rem(self, divisor: Self) -> Option<(Self, Self)> {
        if divisor.is_zero() {
            return None;
        }
        // Fast path: every value and `10^k` divisor the picture pipeline
        // actually divides fits in 128 bits, where hardware division replaces
        // the bit-by-bit loop below.
        if let (Some(dividend), Some(divisor)) = (self.to_u128(), divisor.to_u128()) {
            return Some((
                Self::from_u128(dividend / divisor),
                Self::from_u128(dividend % divisor),
            ));
        }
        let mut quotient = Self::ZERO;
        let mut remainder = Self::ZERO;
        for index in (0..256).rev() {
            remainder = remainder.checked_mul_small(2)?;
            if self.bit(index) {
                remainder.0[0] |= 1;
            }
            if remainder >= divisor {
                remainder = remainder.checked_sub(divisor)?;
                quotient.set_bit(index);
            }
        }
        Some((quotient, remainder))
    }

    fn rounded_ratio(numerator: Self, denominator: Self) -> Option<Self> {
        let (mut quotient, remainder) = numerator.div_rem(denominator)?;
        let twice_remainder = remainder.checked_mul_small(2)?;
        if twice_remainder > denominator || (twice_remainder == denominator && quotient.is_odd()) {
            quotient = quotient.checked_add(Self::ONE)?;
        }
        Some(quotient)
    }
}

impl Ord for Wide {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        for index in (0..4).rev() {
            match self.0[index].cmp(&other.0[index]) {
                std::cmp::Ordering::Equal => {}
                order => return order,
            }
        }
        std::cmp::Ordering::Equal
    }
}

impl PartialOrd for Wide {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Decimal {
    coefficient: i128,
    scale: u32,
}

impl Decimal {
    fn new(mut coefficient: i128, mut scale: u32) -> Result<Self, String> {
        if scale > MAX_SCALE {
            return Err("decimal scale exceeds 28".into());
        }
        while scale > 0 && coefficient % 10 == 0 {
            coefficient /= 10;
            scale -= 1;
        }
        if !(-MAX_COEFFICIENT..=MAX_COEFFICIENT).contains(&coefficient) {
            return Err("decimal coefficient exceeds 96-bit storage".into());
        }
        Ok(Self { coefficient, scale })
    }

    fn from_magnitude(magnitude: Wide, scale: u32, negative: bool) -> Result<Self, String> {
        let minimum_drop = scale.saturating_sub(MAX_SCALE);
        let maximum = Wide::from_u128(MAX_COEFFICIENT as u128);
        for drop in minimum_drop..=scale {
            let divisor = Wide::ONE
                .checked_mul_pow10(drop)
                .ok_or("decimal scale reduction overflow")?;
            let (mut coefficient, remainder) = magnitude
                .div_rem(divisor)
                .ok_or("decimal scale reduction failed")?;
            let twice_remainder = remainder
                .checked_mul_small(2)
                .ok_or("decimal scale reduction overflow")?;
            if twice_remainder > divisor || (twice_remainder == divisor && coefficient.is_odd()) {
                coefficient = coefficient
                    .checked_add(Wide::ONE)
                    .ok_or("decimal scale reduction overflow")?;
            }
            if coefficient <= maximum {
                let coefficient = coefficient
                    .to_u128()
                    .ok_or("decimal coefficient conversion overflow")?
                    as i128;
                return Self::new(
                    if negative { -coefficient } else { coefficient },
                    scale - drop,
                );
            }
        }
        Err("decimal arithmetic overflow".into())
    }

    fn magnitude(self) -> Wide {
        Wide::from_u128(self.coefficient.unsigned_abs())
    }

    pub fn to_i64(self) -> Result<i64, String> {
        if self.scale != 0 {
            return Err("expected integral Numeric value".into());
        }
        i64::try_from(self.coefficient).map_err(|_| "Numeric value outside i64 range".into())
    }

    /// Converts to `i64` by truncating fractional digits toward zero, matching
    /// the native collection-index conversion (native KBTeste evidence: 1.9 -> 1,
    /// 2.7 -> 2, 0.9 -> 0).
    pub fn truncate_to_i64(self) -> Result<i64, String> {
        if self.scale == 0 {
            return i64::try_from(self.coefficient)
                .map_err(|_| "Numeric value outside i64 range".into());
        }
        let divisor = 10_u128
            .checked_pow(self.scale)
            .ok_or("decimal scale reduction overflow")?;
        let divisor = i128::try_from(divisor).map_err(|_| "decimal scale reduction overflow")?;
        i64::try_from(self.coefficient / divisor)
            .map_err(|_| "Numeric value outside i64 range".into())
    }

    /// Converts to `i64` by taking the mathematical floor (`Math.Floor`), matching
    /// native `NumberUtil.Int` (e.g. 3.14 -> 3, -3.14 -> -4).
    pub fn floor_to_i64(self) -> Result<i64, String> {
        if self.scale == 0 {
            return i64::try_from(self.coefficient)
                .map_err(|_| "Numeric value outside i64 range".into());
        }
        let divisor = 10_i128
            .checked_pow(self.scale)
            .ok_or("decimal scale reduction overflow")?;
        let int_part = self.coefficient / divisor;
        let rem = self.coefficient % divisor;
        let floored = if rem < 0 { int_part - 1 } else { int_part };
        i64::try_from(floored).map_err(|_| "Numeric value outside i64 range".into())
    }

    /// Rounds to `digits` decimals using `MidpointRounding.AwayFromZero`, matching
    /// native `NumberUtil.Round`. Supports negative `digits` (rounding to tens, hundreds, etc.).
    pub fn round(self, digits: i64) -> Result<Self, String> {
        if digits >= self.scale as i64 {
            return Ok(self);
        }
        if digits >= 0 {
            let drop = self.scale - digits as u32;
            let divisor = 10_i128.checked_pow(drop).ok_or("decimal round overflow")?;
            let half = divisor / 2;
            let new_coeff = if self.coefficient >= 0 {
                (self.coefficient + half) / divisor
            } else {
                -((-self.coefficient + half) / divisor)
            };
            Self::new(new_coeff, digits as u32)
        } else {
            let drop = self.scale + (-digits) as u32;
            let divisor = 10_i128.checked_pow(drop).ok_or("decimal round overflow")?;
            let half = divisor / 2;
            let int_scaled = if self.coefficient >= 0 {
                (self.coefficient + half) / divisor
            } else {
                -((-self.coefficient + half) / divisor)
            };
            let mult = 10_i128
                .checked_pow((-digits) as u32)
                .ok_or("decimal round overflow")?;
            let new_coeff = int_scaled
                .checked_mul(mult)
                .ok_or("decimal round overflow")?;
            Self::new(new_coeff, 0)
        }
    }

    /// Truncates to `digits` decimals toward zero, matching native `NumberUtil.Trunc`.
    /// Supports negative `digits`.
    pub fn trunc(self, digits: i64) -> Result<Self, String> {
        if digits >= self.scale as i64 {
            return Ok(self);
        }
        if digits >= 0 {
            let drop = self.scale - digits as u32;
            let divisor = 10_i128.checked_pow(drop).ok_or("decimal trunc overflow")?;
            let new_coeff = self.coefficient / divisor;
            Self::new(new_coeff, digits as u32)
        } else {
            let drop = self.scale + (-digits) as u32;
            let divisor = 10_i128.checked_pow(drop).ok_or("decimal trunc overflow")?;
            let int_scaled = self.coefficient / divisor;
            let mult = 10_i128
                .checked_pow((-digits) as u32)
                .ok_or("decimal trunc overflow")?;
            let new_coeff = int_scaled
                .checked_mul(mult)
                .ok_or("decimal trunc overflow")?;
            Self::new(new_coeff, 0)
        }
    }

    /// Parses a string to `Decimal` matching native `NumberUtil.Val` / `ValParse`.
    /// Trims input, parses initial number stopping at non-digit / unexpected characters,
    /// returns 0 on empty / invalid input.
    pub fn val(text: &str, dec_sep: Option<&str>) -> Self {
        let text = text.trim();
        if text.is_empty() {
            return Self {
                coefficient: 0,
                scale: 0,
            };
        }
        let sep_char = dec_sep.and_then(|s| s.chars().next()).unwrap_or('.');
        let mut filtered = String::new();
        let mut saw_dot = false;
        let mut can_have_minus = true;
        for c in text.chars() {
            if c.is_ascii_digit() {
                filtered.push(c);
                can_have_minus = false;
            } else if c == sep_char && !saw_dot {
                filtered.push('.');
                saw_dot = true;
                can_have_minus = false;
            } else if c == '-' && can_have_minus {
                filtered.push('-');
                can_have_minus = false;
            } else {
                break;
            }
        }
        if filtered.is_empty() || filtered == "-" || filtered == "." || filtered == "-." {
            return Self {
                coefficient: 0,
                scale: 0,
            };
        }
        Self::from_str(&filtered).unwrap_or(Self {
            coefficient: 0,
            scale: 0,
        })
    }

    pub fn checked_add(self, other: Self) -> Result<Self, String> {
        let scale = self.scale.max(other.scale);
        let left = self
            .magnitude()
            .checked_mul_pow10(scale - self.scale)
            .ok_or("decimal addition overflow")?;
        let right = other
            .magnitude()
            .checked_mul_pow10(scale - other.scale)
            .ok_or("decimal addition overflow")?;
        let (magnitude, negative) = match (self.coefficient < 0, other.coefficient < 0) {
            (left_negative, right_negative) if left_negative == right_negative => (
                left.checked_add(right).ok_or("decimal addition overflow")?,
                left_negative,
            ),
            _ if left >= right => (
                left.checked_sub(right).ok_or("decimal addition overflow")?,
                self.coefficient < 0,
            ),
            _ => (
                right.checked_sub(left).ok_or("decimal addition overflow")?,
                other.coefficient < 0,
            ),
        };
        Self::from_magnitude(magnitude, scale, negative)
    }

    pub fn checked_sub(self, other: Self) -> Result<Self, String> {
        self.checked_add(Self::new(-other.coefficient, other.scale)?)
    }

    pub fn checked_mul(self, other: Self) -> Result<Self, String> {
        let magnitude = self
            .magnitude()
            .checked_mul(other.magnitude())
            .ok_or("decimal multiplication overflow")?;
        Self::from_magnitude(
            magnitude,
            self.scale + other.scale,
            (self.coefficient < 0) != (other.coefficient < 0),
        )
    }

    pub fn checked_div(self, other: Self) -> Result<Self, String> {
        if other.coefficient == 0 {
            return Err("division by zero".into());
        }
        let numerator = self.magnitude();
        let denominator = other.magnitude();
        let negative = (self.coefficient < 0) != (other.coefficient < 0);
        let maximum = Wide::from_u128(MAX_COEFFICIENT as u128);
        let mut best = None;
        for scale in 0..=MAX_SCALE {
            let exponent = other.scale as i32 + scale as i32 - self.scale as i32;
            let (scaled_numerator, scaled_denominator) = if exponent >= 0 {
                let Some(scaled) = numerator.checked_mul_pow10(exponent as u32) else {
                    break;
                };
                (scaled, denominator)
            } else {
                let Some(scaled) = denominator.checked_mul_pow10((-exponent) as u32) else {
                    break;
                };
                (numerator, scaled)
            };
            let coefficient = Wide::rounded_ratio(scaled_numerator, scaled_denominator)
                .ok_or("decimal division overflow")?;
            if coefficient > maximum {
                break;
            }
            best = Some((coefficient, scale));
        }
        let (coefficient, scale) = best.ok_or("decimal division overflow")?;
        Self::from_magnitude(coefficient, scale, negative)
    }

    pub fn checked_cmp(self, other: Self) -> Result<std::cmp::Ordering, String> {
        let scale = self.scale.max(other.scale);
        let left = self
            .magnitude()
            .checked_mul_pow10(scale - self.scale)
            .ok_or("decimal comparison overflow")?;
        let right = other
            .magnitude()
            .checked_mul_pow10(scale - other.scale)
            .ok_or("decimal comparison overflow")?;
        let order = match (self.coefficient < 0, other.coefficient < 0) {
            (true, false) => std::cmp::Ordering::Less,
            (false, true) => std::cmp::Ordering::Greater,
            (true, true) => right.cmp(&left),
            (false, false) => left.cmp(&right),
        };
        Ok(order)
    }

    pub fn checked_neg(self) -> Result<Self, String> {
        Self::new(-self.coefficient, self.scale)
    }

    fn truncate(self) -> i128 {
        self.coefficient / 10_i128.pow(self.scale)
    }
}

impl std::fmt::Display for Decimal {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let magnitude = self.coefficient.abs().to_string();
        if self.coefficient < 0 {
            f.write_str("-")?;
        }
        if self.scale == 0 {
            return f.write_str(&magnitude);
        }
        let padded = format!("{:0>width$}", magnitude, width = self.scale as usize + 1);
        let split = padded.len() - self.scale as usize;
        write!(f, "{}.{}", &padded[..split], &padded[split..])
    }
}

impl FromStr for Decimal {
    type Err = String;

    fn from_str(text: &str) -> Result<Self, Self::Err> {
        if text.len() > 64 {
            return Err("decimal literal exceeds 64 bytes".into());
        }
        let (negative, digits) = match text.strip_prefix('-') {
            Some(digits) => (true, digits),
            None => (false, text),
        };
        let mut coefficient = 0_i128;
        let mut scale = None;
        let mut count = 0;
        for byte in digits.bytes() {
            match byte {
                b'.' if scale.is_none() && count > 0 => scale = Some(0),
                b'0'..=b'9' => {
                    coefficient = coefficient
                        .checked_mul(10)
                        .and_then(|n| n.checked_add(i128::from(byte - b'0')))
                        .ok_or("decimal literal overflow")?;
                    count += 1;
                    if let Some(scale) = &mut scale {
                        *scale += 1;
                    }
                }
                _ => return Err("invalid decimal literal".into()),
            }
        }
        if count == 0 || scale == Some(0) {
            return Err("decimal literal requires digits on both sides of a decimal point".into());
        }
        Self::new(
            if negative { -coefficient } else { coefficient },
            scale.unwrap_or(0),
        )
    }
}

impl From<i64> for Decimal {
    fn from(value: i64) -> Self {
        Self {
            coefficient: i128::from(value),
            scale: 0,
        }
    }
}

/// Powers of ten as `double`, matching the scaling table `System.Decimal` uses
/// when a double is converted to decimal.
const DOUBLE_POWERS10: [f64; 29] = [
    1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16,
    1e17, 1e18, 1e19, 1e20, 1e21, 1e22, 1e23, 1e24, 1e25, 1e26, 1e27, 1e28,
];

/// `(decimal)double` as `System.Decimal` implements it (`DecCalc.VarDecFromR8`):
/// the double is scaled to a 15-significant-digit mantissa, rounded half to
/// even, and converted with the reached scale. Native trims trailing zeros from
/// the coefficient without changing its value, so only the value is reproduced.
fn decimal_from_f64(input: f64) -> Result<(bool, Wide, u32), String> {
    let negative = input < 0.0;
    let mut value = input.abs();
    if value == 0.0 {
        return Ok((negative, Wide::ZERO, 0));
    }
    let exponent = ((value.to_bits() >> 52) as i32 & 0x7FF) - 1022;
    if exponent < -94 {
        // Native converts the double to decimal zero, which sits below every
        // display quantum and therefore always overflows the picture.
        return Ok((false, Wide::ZERO, 0));
    }
    if exponent > 96 {
        return Err("Str picture bound exceeds the decimal range".into());
    }
    let mut scale = 14 - ((exponent * 19728) >> 16);
    if scale >= 0 {
        if scale > 28 {
            scale = 28;
        }
        value *= DOUBLE_POWERS10[scale as usize];
    } else if scale != -1 || value >= 1e15 {
        value /= DOUBLE_POWERS10[(-scale) as usize];
    } else {
        scale = 0;
    }
    if value < 1e14 && scale < 28 {
        value *= 10.0;
        scale += 1;
    }
    let mantissa = value.round_ties_even();
    if mantissa == 0.0 {
        return Ok((negative, Wide::ZERO, 0));
    }
    let coefficient = Wide::from_u128(mantissa as u128);
    if scale < 0 {
        // Values at or above 10^24 divide here; the picture bound never does.
        let power = u32::try_from(-scale)
            .map_err(|_| "Str picture bound exceeds the decimal range".to_string())?;
        let coefficient = coefficient
            .checked_mul_pow10(power)
            .ok_or("Str picture bound exceeds the decimal range")?;
        Ok((negative, coefficient, 0))
    } else {
        Ok((negative, coefficient, scale as u32))
    }
}

/// Render `Str(value, width, scale)` with the native picture semantics.
///
/// Native evidence (`StringUtil.Str` scans): rounding is half away from zero at
/// the display scale, and a field of asterisks is produced when `|v|` exceeds
/// the bound `10^places - sign - 5*10^-scale`, where
/// `places = width - scale - separator` and the sign reserves one picture place
/// for negative values. Native builds that bound as a `double` and narrows it
/// through `(decimal)`, which keeps 15 significant digits; the engine
/// reproduces that pipeline because past 15 significant digits the narrowed
/// bound no longer equals the exact rational one. The integral zero is rendered
/// only when a place exists for it, and a negative value that rounds to zero
/// loses its sign.
pub fn format_str(value: Decimal, width: u8, scale: u8) -> Result<String, String> {
    if !(1..=18).contains(&width) || scale > width {
        return Err("supported Str requires width 1..18 and scale <= width".into());
    }
    let field = usize::from(width);
    let decimals = u32::from(scale);
    let separator = u32::from(scale > 0);
    let places = i64::from(width) - i64::from(scale) - i64::from(separator);
    let magnitude = value.magnitude();
    // Overflow follows the native pipeline: the bound
    // `10^places - sign - 5*10^-(decimals+1)` is built in `double` and narrowed
    // through `(decimal)`, and `|v|` wins only when it exceeds that narrowed
    // bound (the sign reserves one place in the picture).
    let overflow = if value.coefficient == 0 {
        false
    } else {
        let bound = 10_f64.powi(places as i32) - if value.coefficient < 0 { 1.0 } else { 0.0 };
        let quantum = 10_f64.powi(decimals as i32 + 1);
        let (negative, coefficient, bound_scale) = decimal_from_f64(bound - 5.0 / quantum)?;
        if negative {
            true
        } else {
            let aligned_bound = coefficient
                .checked_mul_pow10(value.scale)
                .ok_or("Str bound alignment overflow")?;
            let aligned_magnitude = magnitude
                .checked_mul_pow10(bound_scale)
                .ok_or("Str bound alignment overflow")?;
            aligned_bound < aligned_magnitude
        }
    };
    if overflow {
        return Ok("*".repeat(field));
    }
    let mut rounded = magnitude;
    if value.scale <= decimals {
        rounded = rounded
            .checked_mul_pow10(decimals - value.scale)
            .ok_or("Str magnitude alignment overflow")?;
    } else {
        let divisor = Wide::ONE
            .checked_mul_pow10(value.scale - decimals)
            .ok_or("Str magnitude alignment overflow")?;
        let (quotient, remainder) = rounded.div_rem(divisor).ok_or("Str rounding failed")?;
        let twice_remainder = remainder
            .checked_mul_small(2)
            .ok_or("Str rounding overflow")?;
        rounded = if twice_remainder >= divisor {
            quotient
                .checked_add(Wide::ONE)
                .ok_or("Str rounding overflow")?
        } else {
            quotient
        };
    }
    let (whole, fraction) = if decimals > 0 {
        let divisor = Wide::ONE
            .checked_mul_pow10(decimals)
            .ok_or("Str picture overflow")?;
        rounded.div_rem(divisor).ok_or("Str rendering failed")?
    } else {
        (rounded, Wide::ZERO)
    };
    // The overflow guard bounds the places, so both parts fit.
    let whole = whole.to_u128().ok_or("Str integer rendering overflow")?;
    let fraction = fraction
        .to_u128()
        .ok_or("Str fraction rendering overflow")?;
    // Assemble the unpadded content backwards into a stack buffer (sign,
    // fraction, separator, whole digits) so rendering performs a single
    // allocation and no `fmt` machinery. The picture admits at most 20
    // characters; the buffer keeps generous headroom.
    let mut buffer = [0_u8; 48];
    let mut start = buffer.len();
    if decimals > 0 {
        let mut fraction = fraction;
        for _ in 0..decimals {
            start -= 1;
            buffer[start] = b'0' + (fraction % 10) as u8;
            fraction /= 10;
        }
        start -= 1;
        buffer[start] = b'.';
    }
    if whole != 0 || places > 0 {
        let mut whole = whole;
        loop {
            start -= 1;
            buffer[start] = b'0' + (whole % 10) as u8;
            whole /= 10;
            if whole == 0 {
                break;
            }
        }
    }
    if value.coefficient < 0 && !rounded.is_zero() {
        start -= 1;
        buffer[start] = b'-';
    }
    let content = std::str::from_utf8(&buffer[start..]).expect("ascii digits");
    let mut output = String::with_capacity(field.max(content.len()));
    for _ in content.len()..field {
        output.push(' ');
    }
    output.push_str(content);
    Ok(output)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct NumericType {
    precision: u8,
    scale: u8,
}

impl NumericType {
    pub fn new(precision: u8, scale: u8) -> Result<Self, String> {
        if !(1..=18).contains(&precision) || scale > precision {
            return Err("supported Numeric requires precision 1..18 and scale <= precision".into());
        }
        Ok(Self { precision, scale })
    }

    pub fn assign(self, value: Decimal) -> Result<Decimal, String> {
        if self.scale != 0 {
            return Ok(value);
        }
        let whole = value.truncate();
        // GeneXus 18 .NET boundary probes observed Int16/Int32/Int64 storage.
        // Preserve those ranges, but diagnose overflow rather than reproduce wrapping.
        let (minimum, maximum) = match self.precision {
            1..=4 => (i128::from(i16::MIN), i128::from(i16::MAX)),
            5..=9 => (i128::from(i32::MIN), i128::from(i32::MAX)),
            _ => (i128::from(i64::MIN), i128::from(i64::MAX)),
        };
        if !(minimum..=maximum).contains(&whole) {
            return Err(
                "numeric integer storage overflow (native wrapping is not reproduced)".into(),
            );
        }
        Decimal::new(whole, 0)
    }

    /// Render the stored value exactly as native `&var.ToString()` does.
    ///
    /// Native evidence (`numeric-render-native` scans over precision 1..18):
    /// the implicit conversion is `StringUtil.Str(value, precision, scale)`
    /// itself - padding included - so the picture pipeline (bound narrowing
    /// through `(decimal)`, separator place, star count, rounding past the
    /// integer bound) is shared with [`format_str`] instead of re-derived.
    pub fn render(self, value: Decimal) -> Result<String, String> {
        format_str(value, self.precision, self.scale)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Value {
    Boolean(bool),
    Numeric(Decimal),
}

impl Value {
    pub fn as_boolean(self) -> Result<bool, String> {
        match self {
            Self::Boolean(value) => Ok(value),
            Self::Numeric(_) => Err("expected Boolean, got Numeric".into()),
        }
    }

    pub fn as_numeric(self) -> Result<Decimal, String> {
        match self {
            Self::Numeric(value) => Ok(value),
            Self::Boolean(_) => Err("expected Numeric, got Boolean".into()),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::decimal_from_f64;

    // Native `StringUtil.NToC` bound per (width, scale, sign): the double bits
    // and the narrowed `(decimal)` components recorded from the GeneXus 18
    // runtime (`scratchpad/str-util-probe`, mode `thresholds`). The Rust
    // picture formatter must reproduce that pipeline bit for bit.
    const NATIVE_BOUNDS: &str = include_str!("../fixtures/str-bounds-native.tsv");

    // Native trims trailing zeros from the narrowed coefficient without
    // changing its value, so the value, not the representation, is compared.
    fn normalized(mut coefficient: u128, mut scale: u32) -> (u128, u32) {
        while scale > 0 && coefficient.is_multiple_of(10) {
            coefficient /= 10;
            scale -= 1;
        }
        (coefficient, scale)
    }

    #[test]
    fn str_overflow_bound_reproduces_native_double_and_narrowing() {
        let mut total = 0_usize;
        for line in NATIVE_BOUNDS.lines() {
            let line = line.trim();
            if line.is_empty() {
                continue;
            }
            let mut fields = line.split('|');
            let width: u8 = fields.next().unwrap().parse().unwrap();
            let scale: u8 = fields.next().unwrap().parse().unwrap();
            let negative = fields.next().unwrap() == "1";
            let places: i64 = fields.next().unwrap().parse().unwrap();
            let double_bits = u64::from_str_radix(fields.next().unwrap(), 16).unwrap();
            let flags: i32 = fields.next().unwrap().parse().unwrap();
            let low: u128 = fields.next().unwrap().parse().unwrap();
            let mid: u128 = fields.next().unwrap().parse().unwrap();
            let high: u128 = fields.next().unwrap().parse().unwrap();

            let expected_places =
                i64::from(width) - i64::from(scale) - i64::from(u8::from(scale > 0));
            assert_eq!(places, expected_places, "places {line}");
            let bound = 10_f64.powi(places as i32) - if negative { 1.0 } else { 0.0 };
            let quantum = 10_f64.powi(i32::from(scale) + 1);
            let threshold = bound - 5.0 / quantum;
            assert_eq!(threshold.to_bits(), double_bits, "double {line}");

            let (sign, coefficient, bound_scale) = decimal_from_f64(threshold).expect(line);
            assert_eq!(sign, flags < 0, "sign {line}");
            let native_scale = ((flags & 0x00FF_0000) >> 16) as u32;
            let native_coefficient = low | (mid << 32) | (high << 64);
            assert_eq!(
                normalized(coefficient.to_u128().unwrap(), bound_scale),
                normalized(native_coefficient, native_scale),
                "value {line}"
            );
            total += 1;
        }
        assert_eq!(total, 378, "bound fixture unexpectedly small");
    }
}
