//! Typed semantic values. Decimal storage and Numeric display width are separate.
//! Checked storage deliberately diagnoses overflow instead of native integer wrapping.
use std::str::FromStr;
use std::sync::OnceLock;

const MAX_COEFFICIENT: i128 = (1_i128 << 96) - 1;
const MAX_SCALE: u32 = 28;

pub const POW10_U128: [u128; 39] = [
    1,
    10,
    100,
    1_000,
    10_000,
    100_000,
    1_000_000,
    10_000_000,
    100_000_000,
    1_000_000_000,
    10_000_000_000,
    100_000_000_000,
    1_000_000_000_000,
    10_000_000_000_000,
    100_000_000_000_000,
    1_000_000_000_000_000,
    10_000_000_000_000_000,
    100_000_000_000_000_000,
    1_000_000_000_000_000_000,
    10_000_000_000_000_000_000,
    100_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000_000_000_000_000_000,
];

pub const POW10_U64: [u64; 20] = [
    1,
    10,
    100,
    1_000,
    10_000,
    100_000,
    1_000_000,
    10_000_000,
    100_000_000,
    1_000_000_000,
    10_000_000_000,
    100_000_000_000,
    1_000_000_000_000,
    10_000_000_000_000,
    100_000_000_000_000,
    1_000_000_000_000_000,
    10_000_000_000_000_000,
    100_000_000_000_000_000,
    1_000_000_000_000_000_000,
    10_000_000_000_000_000_000,
];

pub const POW10_I128: [i128; 39] = [
    1,
    10,
    100,
    1_000,
    10_000,
    100_000,
    1_000_000,
    10_000_000,
    100_000_000,
    1_000_000_000,
    10_000_000_000,
    100_000_000_000,
    1_000_000_000_000,
    10_000_000_000_000,
    100_000_000_000_000,
    1_000_000_000_000_000,
    10_000_000_000_000_000,
    100_000_000_000_000_000,
    1_000_000_000_000_000_000,
    10_000_000_000_000_000_000,
    100_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000_000_000_000_000,
    1_000_000_000_000_000_000_000_000_000_000_000_000,
    10_000_000_000_000_000_000_000_000_000_000_000_000,
    100_000_000_000_000_000_000_000_000_000_000_000_000,
];

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
        if power < 39
            && let Some(val) = self.to_u128()
            && let Some(res) = val.checked_mul(POW10_U128[power as usize])
        {
            return Some(Self::from_u128(res));
        }
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
        if let Ok(mut c64) = i64::try_from(coefficient) {
            while scale > 0 && c64 % 10 == 0 {
                c64 /= 10;
                scale -= 1;
            }
            return Ok(Self {
                coefficient: i128::from(c64),
                scale,
            });
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

    pub fn coefficient(&self) -> i128 {
        self.coefficient
    }

    pub fn scale(&self) -> u32 {
        self.scale
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
        if (self.scale as usize) < POW10_I128.len() {
            let divisor = POW10_I128[self.scale as usize];
            if let Ok(c64) = i64::try_from(self.coefficient) {
                let div64 = divisor as i64;
                let int_part = c64 / div64;
                let rem = c64 % div64;
                let floored = if rem < 0 { int_part - 1 } else { int_part };
                return Ok(floored);
            }
            let int_part = self.coefficient / divisor;
            let rem = self.coefficient % divisor;
            let floored = if rem < 0 { int_part - 1 } else { int_part };
            return i64::try_from(floored).map_err(|_| "Numeric value outside i64 range".into());
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
            let drop = (self.scale - digits as u32) as usize;
            let divisor = POW10_I128
                .get(drop)
                .copied()
                .ok_or("decimal round overflow")?;
            if let Ok(c64) = i64::try_from(self.coefficient) {
                let div64 = divisor as i64;
                let half64 = div64 / 2;
                let new_coeff = if c64 >= 0 {
                    (c64 + half64) / div64
                } else {
                    -((-c64 + half64) / div64)
                };
                return Self::new(i128::from(new_coeff), digits as u32);
            }
            let half = divisor / 2;
            let new_coeff = if self.coefficient >= 0 {
                (self.coefficient + half) / divisor
            } else {
                -((-self.coefficient + half) / divisor)
            };
            Self::new(new_coeff, digits as u32)
        } else {
            let drop = (self.scale + (-digits) as u32) as usize;
            let divisor = POW10_I128
                .get(drop)
                .copied()
                .ok_or("decimal round overflow")?;
            let half = divisor / 2;
            let int_scaled = if self.coefficient >= 0 {
                (self.coefficient + half) / divisor
            } else {
                -((-self.coefficient + half) / divisor)
            };
            let mult = POW10_I128
                .get((-digits) as usize)
                .copied()
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
            let drop = (self.scale - digits as u32) as usize;
            let divisor = POW10_I128
                .get(drop)
                .copied()
                .ok_or("decimal trunc overflow")?;
            if let Ok(c64) = i64::try_from(self.coefficient) {
                let div64 = divisor as i64;
                let new_coeff = c64 / div64;
                return Self::new(i128::from(new_coeff), digits as u32);
            }
            let new_coeff = self.coefficient / divisor;
            Self::new(new_coeff, digits as u32)
        } else {
            let drop = (self.scale + (-digits) as u32) as usize;
            let divisor = POW10_I128
                .get(drop)
                .copied()
                .ok_or("decimal trunc overflow")?;
            let int_scaled = self.coefficient / divisor;
            let mult = POW10_I128
                .get((-digits) as usize)
                .copied()
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
        let bytes = text.as_bytes();
        let len = bytes.len();
        if len == 0 {
            return Self {
                coefficient: 0,
                scale: 0,
            };
        }
        let mut start_idx = 0;
        while start_idx < len && bytes[start_idx] == b' ' {
            start_idx += 1;
        }
        if start_idx == len {
            return Self {
                coefficient: 0,
                scale: 0,
            };
        }
        let sep_byte = match dec_sep {
            Some(s) => match s.as_bytes().first() {
                Some(&b) => b,
                None => b'.',
            },
            None => b'.',
        };
        let (negative, mut cur_idx) = if bytes[start_idx] == b'-' {
            (true, start_idx + 1)
        } else {
            (false, start_idx)
        };
        let mut coeff: i64 = 0;
        let mut scale: u32 = 0;
        let mut in_frac = false;
        let mut has_digits = false;
        let mut overflow_i64 = false;
        let mut coeff_i128: i128 = 0;

        while cur_idx < len {
            let b = bytes[cur_idx];
            cur_idx += 1;
            if b.is_ascii_digit() {
                has_digits = true;
                if in_frac {
                    scale += 1;
                }
                if !overflow_i64 {
                    if let Some(next) = coeff
                        .checked_mul(10)
                        .and_then(|c| c.checked_add(i64::from(b - b'0')))
                    {
                        coeff = next;
                    } else {
                        overflow_i64 = true;
                        coeff_i128 = i128::from(coeff) * 10 + i128::from(b - b'0');
                    }
                } else if let Some(next) = coeff_i128
                    .checked_mul(10)
                    .and_then(|c| c.checked_add(i128::from(b - b'0')))
                {
                    coeff_i128 = next;
                } else {
                    break;
                }
            } else if b == sep_byte && !in_frac {
                in_frac = true;
            } else {
                break;
            }
        }
        if !has_digits {
            return Self {
                coefficient: 0,
                scale: 0,
            };
        }
        if !overflow_i64 {
            let mut c = if negative { -coeff } else { coeff };
            while scale > 0 && c % 10 == 0 {
                c /= 10;
                scale -= 1;
            }
            return Self {
                coefficient: i128::from(c),
                scale,
            };
        }
        let final_coeff = if negative { -coeff_i128 } else { coeff_i128 };
        Self::new(final_coeff, scale).unwrap_or(Self {
            coefficient: 0,
            scale: 0,
        })
    }

    pub fn checked_add(self, other: Self) -> Result<Self, String> {
        let scale = self.scale.max(other.scale);
        if scale <= MAX_SCALE {
            let left_scale_diff = (scale - self.scale) as usize;
            let right_scale_diff = (scale - other.scale) as usize;
            if left_scale_diff < POW10_I128.len()
                && right_scale_diff < POW10_I128.len()
                && let (Ok(l64), Ok(r64)) = (
                    i64::try_from(self.coefficient),
                    i64::try_from(other.coefficient),
                )
                && let (Some(l_scaled), Some(r_scaled)) = (
                    l64.checked_mul(POW10_I128[left_scale_diff] as i64),
                    r64.checked_mul(POW10_I128[right_scale_diff] as i64),
                )
                && let Some(sum64) = l_scaled.checked_add(r_scaled)
            {
                return Self::new(i128::from(sum64), scale);
            }
            if left_scale_diff < POW10_I128.len()
                && right_scale_diff < POW10_I128.len()
                && let (Some(l), Some(r)) = (
                    self.coefficient.checked_mul(POW10_I128[left_scale_diff]),
                    other.coefficient.checked_mul(POW10_I128[right_scale_diff]),
                )
                && let Some(sum) = l.checked_add(r)
                && (-MAX_COEFFICIENT..=MAX_COEFFICIENT).contains(&sum)
            {
                return Self::new(sum, scale);
            }
        }
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

    #[inline]
    pub fn checked_sub(self, other: Self) -> Result<Self, String> {
        self.checked_add(Self {
            coefficient: -other.coefficient,
            scale: other.scale,
        })
    }

    pub fn checked_mul(self, other: Self) -> Result<Self, String> {
        let scale = self.scale + other.scale;
        if scale <= MAX_SCALE {
            if let (Ok(l64), Ok(r64)) = (
                i64::try_from(self.coefficient),
                i64::try_from(other.coefficient),
            ) && let Some(prod64) = l64.checked_mul(r64)
            {
                return Self::new(i128::from(prod64), scale);
            }
            if let Some(coeff) = self.coefficient.checked_mul(other.coefficient)
                && (-MAX_COEFFICIENT..=MAX_COEFFICIENT).contains(&coeff)
            {
                return Self::new(coeff, scale);
            }
        }
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
        let negative = (self.coefficient < 0) != (other.coefficient < 0);
        let num_u128 = self.coefficient.unsigned_abs();
        let den_u128 = other.coefficient.unsigned_abs();

        if let (Ok(num64), Ok(den64)) = (u64::try_from(num_u128), u64::try_from(den_u128)) {
            let mut n = num64;
            let mut cur_scale = self.scale as i32 - other.scale as i32;
            let mut steps = 0;
            while cur_scale < 0 {
                if let Some(next_n) = n.checked_mul(10) {
                    n = next_n;
                    cur_scale += 1;
                } else {
                    break;
                }
            }
            if cur_scale >= 0 {
                while steps < 16 && cur_scale < MAX_SCALE as i32 {
                    if n % den64 == 0 {
                        let q = (n / den64) as i64;
                        let coeff = if negative { -q } else { q };
                        return Self::new(i128::from(coeff), cur_scale as u32);
                    }
                    if let Some(next_n) = n.checked_mul(10) {
                        n = next_n;
                        cur_scale += 1;
                        steps += 1;
                    } else {
                        break;
                    }
                }
            }
        }

        // 128-bit hardware division fast-path
        for scale in (0..=MAX_SCALE).rev() {
            let exponent = other.scale as i32 + scale as i32 - self.scale as i32;
            if exponent >= 0 {
                let exp = exponent as usize;
                if exp < POW10_I128.len() {
                    let pow = POW10_I128[exp] as u128;
                    if let Some(scaled_num) = num_u128.checked_mul(pow) {
                        let mut quotient = scaled_num / den_u128;
                        let remainder = scaled_num % den_u128;
                        if let Some(twice_rem) = remainder.checked_mul(2) {
                            if twice_rem > den_u128
                                || (twice_rem == den_u128 && (quotient & 1 != 0))
                            {
                                if let Some(q_plus) = quotient.checked_add(1) {
                                    quotient = q_plus;
                                } else {
                                    continue;
                                }
                            }
                            if quotient <= MAX_COEFFICIENT as u128 {
                                let coeff = if negative {
                                    -(quotient as i128)
                                } else {
                                    quotient as i128
                                };
                                return Self::new(coeff, scale);
                            }
                        }
                    }
                }
            } else {
                let exp = (-exponent) as usize;
                if exp < POW10_I128.len() {
                    let pow = POW10_I128[exp] as u128;
                    if let Some(scaled_den) = den_u128.checked_mul(pow) {
                        let mut quotient = num_u128 / scaled_den;
                        let remainder = num_u128 % scaled_den;
                        if let Some(twice_rem) = remainder.checked_mul(2) {
                            if twice_rem > scaled_den
                                || (twice_rem == scaled_den && (quotient & 1 != 0))
                            {
                                if let Some(q_plus) = quotient.checked_add(1) {
                                    quotient = q_plus;
                                } else {
                                    continue;
                                }
                            }
                            if quotient <= MAX_COEFFICIENT as u128 {
                                let coeff = if negative {
                                    -(quotient as i128)
                                } else {
                                    quotient as i128
                                };
                                return Self::new(coeff, scale);
                            }
                        }
                    }
                }
            }
        }

        let numerator = self.magnitude();
        let denominator = other.magnitude();
        let maximum = Wide::from_u128(MAX_COEFFICIENT as u128);
        for scale in (0..=MAX_SCALE).rev() {
            let exponent = other.scale as i32 + scale as i32 - self.scale as i32;
            let (scaled_numerator, scaled_denominator) = if exponent >= 0 {
                let Some(scaled) = numerator.checked_mul_pow10(exponent as u32) else {
                    continue;
                };
                (scaled, denominator)
            } else {
                let Some(scaled) = denominator.checked_mul_pow10((-exponent) as u32) else {
                    continue;
                };
                (numerator, scaled)
            };
            if let Some(coefficient) = Wide::rounded_ratio(scaled_numerator, scaled_denominator)
                && coefficient <= maximum
            {
                return Self::from_magnitude(coefficient, scale, negative);
            }
        }
        Err("decimal division overflow".into())
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

#[derive(Clone, Copy)]
struct BoundEntry {
    negative: bool,
    coefficient: Wide,
    coefficient_u128: Option<u128>,
    bound_scale: u32,
    valid: bool,
}

static BOUNDS_TABLE: OnceLock<Box<[BoundEntry; 760]>> = OnceLock::new();

fn get_bound_entry(places: i64, decimals: u32, is_negative: bool) -> Result<BoundEntry, String> {
    if !(-1..=18).contains(&places) || decimals > 18 {
        let bound = 10_f64.powi(places as i32) - if is_negative { 1.0 } else { 0.0 };
        let quantum = 10_f64.powi(decimals as i32 + 1);
        let (negative, coefficient, bound_scale) = decimal_from_f64(bound - 5.0 / quantum)?;
        return Ok(BoundEntry {
            negative,
            coefficient,
            coefficient_u128: coefficient.to_u128(),
            bound_scale,
            valid: true,
        });
    }
    let idx =
        (((places + 1) as usize * 19) + decimals as usize) * 2 + if is_negative { 1 } else { 0 };
    if let Some(table) = BOUNDS_TABLE.get() {
        let entry = table[idx];
        if entry.valid {
            return Ok(entry);
        }
    }
    let table = BOUNDS_TABLE.get_or_init(|| {
        let mut arr = Box::new(
            [BoundEntry {
                negative: false,
                coefficient: Wide::ZERO,
                coefficient_u128: Some(0),
                bound_scale: 0,
                valid: false,
            }; 760],
        );
        for p in -1..=18 {
            for d in 0..=18 {
                for &neg in &[false, true] {
                    let idx = (((p + 1) as usize * 19) + d as usize) * 2 + if neg { 1 } else { 0 };
                    let bound = 10_f64.powi(p) - if neg { 1.0 } else { 0.0 };
                    let quantum = 10_f64.powi(d + 1);
                    if let Ok((negative, coefficient, bound_scale)) =
                        decimal_from_f64(bound - 5.0 / quantum)
                    {
                        arr[idx] = BoundEntry {
                            negative,
                            coefficient,
                            coefficient_u128: coefficient.to_u128(),
                            bound_scale,
                            valid: true,
                        };
                    }
                }
            }
        }
        arr
    });
    let idx =
        (((places + 1) as usize * 19) + decimals as usize) * 2 + if is_negative { 1 } else { 0 };
    let entry = table[idx];
    if entry.valid {
        Ok(entry)
    } else {
        Err("Str picture bound exceeds the decimal range".into())
    }
}

#[inline(always)]
fn round_u64(magnitude: u64, scale: u32, decimals: u32) -> Option<(u64, u64, bool)> {
    if scale == 0 {
        return Some((magnitude, 0, magnitude == 0));
    }
    if scale == decimals {
        if decimals == 0 {
            return Some((magnitude, 0, magnitude == 0));
        }
        if decimals >= 20 {
            return None;
        }
        let div = POW10_U64[decimals as usize];
        return Some((magnitude / div, magnitude % div, magnitude == 0));
    }
    if scale < decimals {
        let diff = (decimals - scale) as usize;
        if diff >= 20 {
            return None;
        }
        let rounded = magnitude.checked_mul(POW10_U64[diff])?;
        if decimals >= 20 {
            return None;
        }
        let div = POW10_U64[decimals as usize];
        Some((rounded / div, rounded % div, rounded == 0))
    } else {
        let diff = (scale - decimals) as usize;
        if diff >= 20 {
            return None;
        }
        let divisor = POW10_U64[diff];
        let quotient = magnitude / divisor;
        let remainder = magnitude % divisor;
        let twice_remainder = remainder.checked_mul(2)?;
        let rounded = if twice_remainder >= divisor {
            quotient.checked_add(1)?
        } else {
            quotient
        };
        let (whole, fraction) = if decimals > 0 {
            if decimals >= 20 {
                return None;
            }
            let div = POW10_U64[decimals as usize];
            (rounded / div, rounded % div)
        } else {
            (rounded, 0)
        };
        Some((whole, fraction, rounded == 0))
    }
}

#[inline(always)]
fn round_u128(magnitude: u128, scale: u32, decimals: u32) -> Option<(u128, u128, bool)> {
    if scale == 0 {
        return Some((magnitude, 0, magnitude == 0));
    }
    if scale == decimals {
        if decimals == 0 {
            return Some((magnitude, 0, magnitude == 0));
        }
        if decimals >= 39 {
            return None;
        }
        let div = POW10_U128[decimals as usize];
        return Some((magnitude / div, magnitude % div, magnitude == 0));
    }
    if scale < decimals {
        let diff = (decimals - scale) as usize;
        if diff >= 39 {
            return None;
        }
        let rounded = magnitude.checked_mul(POW10_U128[diff])?;
        if decimals >= 39 {
            return None;
        }
        let div = POW10_U128[decimals as usize];
        Some((rounded / div, rounded % div, rounded == 0))
    } else {
        let diff = (scale - decimals) as usize;
        if diff >= 39 {
            return None;
        }
        let divisor = POW10_U128[diff];
        let quotient = magnitude / divisor;
        let remainder = magnitude % divisor;
        let twice_remainder = remainder.checked_mul(2)?;
        let rounded = if twice_remainder >= divisor {
            quotient.checked_add(1)?
        } else {
            quotient
        };
        let (whole, fraction) = if decimals > 0 {
            if decimals >= 39 {
                return None;
            }
            let div = POW10_U128[decimals as usize];
            (rounded / div, rounded % div)
        } else {
            (rounded, 0)
        };
        Some((whole, fraction, rounded == 0))
    }
}

#[inline(always)]
fn check_overflow_entry(
    places: i64,
    decimals: u32,
    is_negative: bool,
    scale: u32,
    magnitude_u128: u128,
) -> Result<bool, String> {
    let entry = get_bound_entry(places, decimals, is_negative)?;
    if entry.negative {
        return Ok(true);
    }
    if let (Some(coeff), true, true) = (entry.coefficient_u128, scale < 20, entry.bound_scale < 20)
        && let (Ok(coeff64), Ok(mag64)) = (u64::try_from(coeff), u64::try_from(magnitude_u128))
    {
        let bound_mul = POW10_U64[scale as usize];
        let mag_mul = POW10_U64[entry.bound_scale as usize];
        if let (Some(aligned_bound), Some(aligned_mag)) =
            (coeff64.checked_mul(bound_mul), mag64.checked_mul(mag_mul))
        {
            return Ok(aligned_bound < aligned_mag);
        }
    }
    if let (Some(coeff), true, true) = (entry.coefficient_u128, scale < 39, entry.bound_scale < 39)
    {
        let bound_mul = POW10_U128[scale as usize];
        let mag_mul = POW10_U128[entry.bound_scale as usize];
        match (
            coeff.checked_mul(bound_mul),
            magnitude_u128.checked_mul(mag_mul),
        ) {
            (Some(aligned_bound), Some(aligned_mag)) => Ok(aligned_bound < aligned_mag),
            _ => {
                let aligned_bound = entry
                    .coefficient
                    .checked_mul_pow10(scale)
                    .ok_or("Str bound alignment overflow")?;
                let aligned_magnitude = Wide::from_u128(magnitude_u128)
                    .checked_mul_pow10(entry.bound_scale)
                    .ok_or("Str bound alignment overflow")?;
                Ok(aligned_bound < aligned_magnitude)
            }
        }
    } else {
        let aligned_bound = entry
            .coefficient
            .checked_mul_pow10(scale)
            .ok_or("Str bound alignment overflow")?;
        let aligned_magnitude = Wide::from_u128(magnitude_u128)
            .checked_mul_pow10(entry.bound_scale)
            .ok_or("Str bound alignment overflow")?;
        Ok(aligned_bound < aligned_magnitude)
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
pub fn format_str_bytes(
    value: Decimal,
    width: u8,
    scale: u8,
    buffer: &mut [u8; 64],
) -> Result<&str, String> {
    if !(1..=18).contains(&width) || scale > width {
        return Err("supported Str requires width 1..18 and scale <= width".into());
    }
    let field = usize::from(width);
    let decimals = u32::from(scale);
    let separator = u32::from(scale > 0);
    let places = i64::from(width) - i64::from(scale) - i64::from(separator);
    let magnitude_u128 = value.coefficient.unsigned_abs();

    let is_negative = value.coefficient < 0;
    let overflow = if value.coefficient == 0 {
        false
    } else {
        let safe_power = if is_negative { places - 2 } else { places - 1 };
        if (0..19).contains(&safe_power) && value.scale < 20 {
            if let Ok(mag64) = u64::try_from(magnitude_u128) {
                let int_part = mag64 / POW10_U64[value.scale as usize];
                if int_part < POW10_U64[safe_power as usize] {
                    false
                } else {
                    check_overflow_entry(
                        places,
                        decimals,
                        is_negative,
                        value.scale,
                        magnitude_u128,
                    )?
                }
            } else {
                let int_part = magnitude_u128 / POW10_U128[value.scale as usize];
                if int_part < POW10_U128[safe_power as usize] {
                    false
                } else {
                    check_overflow_entry(
                        places,
                        decimals,
                        is_negative,
                        value.scale,
                        magnitude_u128,
                    )?
                }
            }
        } else if (0..39).contains(&safe_power) && value.scale < 39 {
            let int_part = magnitude_u128 / POW10_U128[value.scale as usize];
            if int_part < POW10_U128[safe_power as usize] {
                false
            } else {
                check_overflow_entry(places, decimals, is_negative, value.scale, magnitude_u128)?
            }
        } else {
            check_overflow_entry(places, decimals, is_negative, value.scale, magnitude_u128)?
        }
    };
    if overflow {
        const ASTERISKS: &[u8] = b"************************************************";
        let dest = &mut buffer[..field];
        dest.copy_from_slice(&ASTERISKS[..field]);
        return Ok(std::str::from_utf8(dest).expect("ascii"));
    }

    let (whole, fraction, is_rounded_zero) = if let (Ok(mag64), true, true) = (
        u64::try_from(magnitude_u128),
        value.scale < 20,
        decimals < 20,
    ) {
        if let Some((w, f, rz)) = round_u64(mag64, value.scale, decimals) {
            (u128::from(w), u128::from(f), rz)
        } else if let Some((w, f, rz)) = round_u128(magnitude_u128, value.scale, decimals) {
            (w, f, rz)
        } else {
            (0, 0, false)
        }
    } else if let Some((w, f, rz)) = round_u128(magnitude_u128, value.scale, decimals) {
        (w, f, rz)
    } else {
        let magnitude = Wide::from_u128(magnitude_u128);
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
        let (w, f) = if decimals > 0 {
            let divisor = Wide::ONE
                .checked_mul_pow10(decimals)
                .ok_or("Str picture overflow")?;
            rounded.div_rem(divisor).ok_or("Str rendering failed")?
        } else {
            (rounded, Wide::ZERO)
        };
        let rz = rounded.is_zero();
        (
            w.to_u128().ok_or("Str integer rendering overflow")?,
            f.to_u128().ok_or("Str fraction rendering overflow")?,
            rz,
        )
    };

    let mut start = buffer.len();
    if decimals > 0 {
        if let Ok(mut frac64) = u64::try_from(fraction) {
            for _ in 0..decimals {
                start -= 1;
                buffer[start] = b'0' + (frac64 % 10) as u8;
                frac64 /= 10;
            }
        } else {
            let mut fraction = fraction;
            for _ in 0..decimals {
                start -= 1;
                buffer[start] = b'0' + (fraction % 10) as u8;
                fraction /= 10;
            }
        }
        start -= 1;
        buffer[start] = b'.';
    }
    if whole != 0 || places > 0 {
        if let Ok(mut whole64) = u64::try_from(whole) {
            const DIGITS: &[u8; 200] = b"00010203040506070809101112131415161718192021222324252627282930313233343536373839404142434445464748495051525354555657585960616263646566676869707172737475767778798081828384858687888990919293949596979899";
            while whole64 >= 100 {
                let rem = (whole64 % 100) as usize * 2;
                whole64 /= 100;
                start -= 2;
                buffer[start] = DIGITS[rem];
                buffer[start + 1] = DIGITS[rem + 1];
            }
            if whole64 < 10 {
                start -= 1;
                buffer[start] = b'0' + whole64 as u8;
            } else {
                let rem = whole64 as usize * 2;
                start -= 2;
                buffer[start] = DIGITS[rem];
                buffer[start + 1] = DIGITS[rem + 1];
            }
        } else {
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
    }
    if value.coefficient < 0 && !is_rounded_zero {
        start -= 1;
        buffer[start] = b'-';
    }
    let content_len = buffer.len() - start;
    if content_len < field {
        let pad = field - content_len;
        let pad_start = start - pad;
        buffer[pad_start..start].fill(b' ');
        start = pad_start;
    }
    let s = std::str::from_utf8(&buffer[start..]).expect("ascii digits");
    Ok(s)
}

pub fn format_str(value: Decimal, width: u8, scale: u8) -> Result<String, String> {
    let mut buffer = [0_u8; 64];
    format_str_bytes(value, width, scale, &mut buffer).map(|s| s.to_string())
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
