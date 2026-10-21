//! Facts read from the Knowledge Base web output: build number, client configuration, support source and stamps.

use super::*;

/// The KB build number, from `NetModel/Web/GXCFG.WEB`. `None` when the file or key is absent or the
/// value is not digits.
pub fn web_build_number_from_kb(kb_dir: &Path) -> Option<String> {
    let path = kb_dir.join("NetModel").join("Web").join("GXCFG.WEB");
    let text = std::fs::read_to_string(path).ok()?;
    let document = roxmltree::Document::parse(text.trim_start_matches('\u{feff}')).ok()?;
    let value = document
        .descendants()
        .find(|node| node.has_tag_name("add") && node.attribute("key") == Some("GX_BUILD_NUMBER"))?
        .attribute("value")?;
    (!value.is_empty() && value.chars().all(|c| c.is_ascii_digit())).then(|| value.to_string())
}

/// The web model's client configuration script (`gxcfg.js`), from the model settings in
/// `NetModel/Web/GXCFG.WEB`. The text is matched, byte for byte, against the SDK's script for KBTeste; a
/// setting it needs that is absent, or a value outside the matched ones, is refused by name.
pub fn web_client_config_from_kb(kb_dir: &Path) -> Result<String, String> {
    let path = kb_dir.join("NetModel").join("Web").join("GXCFG.WEB");
    let text = std::fs::read_to_string(&path)
        .map_err(|e| format!("the model settings {} are not read: {e}", path.display()))?;
    client_config_script(&text)
}

/// [`web_client_config_from_kb`] over the text of `GXCFG.WEB`.
pub fn client_config_script(settings: &str) -> Result<String, String> {
    let document = roxmltree::Document::parse(settings.trim_start_matches('\u{feff}'))
        .map_err(|e| format!("the model settings are not XML: {e}"))?;
    let setting = |key: &str| -> Result<&str, String> {
        document
            .descendants()
            .find(|node| node.has_tag_name("add") && node.attribute("key") == Some(key))
            .and_then(|node| node.attribute("value"))
            .ok_or_else(|| format!("the model settings have no {key}"))
    };
    let matched = |key: &str, allowed: &[&str]| -> Result<String, String> {
        let value = setting(key)?;
        if allowed.contains(&value) {
            Ok(value.to_string())
        } else {
            Err(format!(
                "the model setting {key}='{value}' is not one the script is matched for"
            ))
        }
    };
    let build = setting("GX_BUILD_NUMBER")?;
    if build.is_empty() || !build.chars().all(|c| c.is_ascii_digit()) {
        return Err(format!(
            "the model setting GX_BUILD_NUMBER='{build}' is not a number"
        ));
    }
    let language = matched("LANGUAGE", &["eng"])?;
    let date_format = matched("DateFormat", &["MDY", "DMY", "YMD"])?;
    let time_format = matched("TimeAmPmFormat", &["12", "24"])?;
    let year_limit = setting("YearLimit")?;
    let century = year_limit
        .strip_prefix("19")
        .filter(|y| y.len() == 2 && y.chars().all(|c| c.is_ascii_digit()))
        .ok_or_else(|| format!("the model setting YearLimit='{year_limit}' is not a 19xx year"))?;
    // The thousands separator is not a setting: the SDK writes ',' beside the '.' decimal point.
    let decimal = matched("DECIMAL_POINT", &["."])?;
    let storage_time_zone = matched("StorageTimeZone", &["0", "1"])?;
    matched("DocumentType", &["HTML5"])?;
    let lines = [
        "gx.setStaticDirectory(gx.text.chr(47));".to_string(),
        "gx.setBlankWhenEmptyDate( false );".to_string(),
        String::new(),
        format!("gx.gxBuild =  {build} ;"),
        "gx.gen.net = true;".to_string(),
        format!("gx.setLanguageCode( '{language}' );"),
        format!("gx.setDateFormat( \"{date_format}\" );"),
        format!("gx.setTimeFormat( {time_format} );"),
        format!("gx.setCenturyFirstYear( {century} );"),
        format!("gx.setDecimalPoint( '{decimal}' );"),
        "gx.setThousandSeparator( ',' );".to_string(),
        "gx.TabOnPrompSelect =  false ;".to_string(),
        format!("gx.StorageTimeZone =  {storage_time_zone} ;"),
        "gx.datepickerImage=[];".to_string(),
        "gx.blankImage=\"blank.jpg\";".to_string(),
        "gx.ascImage=\"asc.gif\";".to_string(),
        "gx.descImage=\"desc.gif\";".to_string(),
        "gx.expandImage=\"expand.gif\";".to_string(),
        "gx.collapseImage=\"collapse.gif\";".to_string(),
        "gx.downloadImage=\"download.gif\";".to_string(),
        "gx.indicatorImage=\"indicator.gif\";".to_string(),
        "gx.promptImage=\"prompt.gif\";".to_string(),
        "gx.myLocationImage=\"locateMe.gif\";".to_string(),
        "gx.resizeImage=\"resize.gif\";".to_string(),
        "gx.csv.stopOnError = false;".to_string(),
        "gx.csv.dismissSeconds = 0;".to_string(),
        "gx.csv.messagePosition = 'right';".to_string(),
        "gx.csv.overlap = true;".to_string(),
        "gx.csv.oneAtAtime = true;".to_string(),
        "gx.HTML5 = true;".to_string(),
    ];
    Ok(lines.iter().map(|line| format!("{line}\r\n")).collect())
}

/// The `<meta name="generator">` text the SDK writes into web objects, `GeneXus .NET 18_0_10-184260`, for
/// the GeneXus install that generates: the product version of its `genexus.exe`. `None` when the install
/// or its version is not read.
pub fn web_generator_from_install(install: &Path) -> Option<String> {
    let image = std::fs::read(install.join("genexus.exe")).ok()?;
    generator_banner(&product_version_from_pe(&image)?)
}

/// `GeneXus .NET <major>_<minor>_<upgrade>-<build>` for a four-part product version.
pub fn generator_banner(version: &str) -> Option<String> {
    let parts: Vec<&str> = version.split('.').collect();
    let numeric = |part: &&str| !part.is_empty() && part.bytes().all(|b| b.is_ascii_digit());
    match parts.as_slice() {
        [major, minor, upgrade, build] if parts.iter().all(numeric) => {
            Some(format!("GeneXus .NET {major}_{minor}_{upgrade}-{build}"))
        }
        _ => None,
    }
}

/// The `ProductVersion` string of a PE image's version resource: the UTF-16 key, NUL, padding to a
/// 32-bit boundary, then the NUL-terminated UTF-16 value.
pub fn product_version_from_pe(image: &[u8]) -> Option<String> {
    let key: Vec<u8> = "ProductVersion\0"
        .encode_utf16()
        .flat_map(u16::to_le_bytes)
        .collect();
    let at = image
        .windows(key.len())
        .position(|window| window == key.as_slice())?;
    let mut cursor = (at + key.len()).next_multiple_of(4);
    let mut units = Vec::new();
    loop {
        let unit = u16::from_le_bytes([*image.get(cursor)?, *image.get(cursor + 1)?]);
        if unit == 0 {
            break;
        }
        units.push(unit);
        cursor += 2;
        if units.len() > 64 {
            return None;
        }
    }
    let value = String::from_utf16(&units).ok()?;
    (!value.is_empty()).then_some(value)
}

/// The KB's generated helper class `NetModel/Web/GxWebStd.cs`, verbatim. `None` when the file is
/// absent or unreadable.
pub fn web_support_from_kb(kb_dir: &Path) -> Option<String> {
    let path = kb_dir.join("NetModel").join("Web").join("GxWebStd.cs");
    std::fs::read_to_string(path)
        .ok()
        .filter(|text| !text.is_empty())
}

/// The generation stamp for a Unix time, as `yyyyMMddHHmmss` (UTC).
pub fn stamp_from_unix_seconds(seconds: u64) -> String {
    let days = (seconds / 86_400) as i64;
    let secs_of_day = seconds % 86_400;
    // Civil date from days since 1970-01-01 (proleptic Gregorian).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!(
        "{year:04}{month:02}{day:02}{:02}{:02}{:02}",
        secs_of_day / 3600,
        (secs_of_day % 3600) / 60,
        secs_of_day % 60
    )
}

/// Whether `existing` is the output `new` already, generated at another time: the bytes are equal except
/// where `new` holds `stamp`, and there `existing` holds one earlier stamp of the same width, all digits.
/// The stamp only marks when a panel's scripts were generated, so a file that matches keeps its stamp and
/// a browser's cached scripts stay valid (and an unchanged project is not compiled again).
pub fn differs_only_in_stamp(existing: &[u8], new: &[u8], stamp: &str) -> bool {
    if existing.len() != new.len() {
        return false;
    }
    let stamp = stamp.as_bytes();
    let mut earlier: Option<&[u8]> = None;
    let mut at = 0;
    while at < new.len() {
        let Some(found) = (!stamp.is_empty())
            .then(|| {
                new[at..]
                    .windows(stamp.len())
                    .position(|window| window == stamp)
            })
            .flatten()
        else {
            return existing[at..] == new[at..];
        };
        let start = at + found;
        let end = start + stamp.len();
        if existing[at..start] != new[at..start] {
            return false;
        }
        let old = &existing[start..end];
        if !old.iter().all(u8::is_ascii_digit) || earlier.is_some_and(|value| value != old) {
            return false;
        }
        earlier = Some(old);
        at = end;
    }
    true
}

/// The generation stamp for the current time.
pub fn current_stamp() -> String {
    let seconds = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    stamp_from_unix_seconds(seconds)
}

#[cfg(test)]
mod generator_banner_tests {
    use super::{generator_banner, product_version_from_pe};

    fn utf16(text: &str) -> Vec<u8> {
        text.encode_utf16().flat_map(u16::to_le_bytes).collect()
    }

    /// A `VS_VERSIONINFO` string entry as a PE stores it: length, value length, type, the key, padding, the value.
    fn entry(key: &str, value: &str) -> Vec<u8> {
        let mut bytes = vec![0u8; 6];
        bytes.extend(utf16(key));
        bytes.extend([0, 0]);
        bytes.resize(bytes.len().next_multiple_of(4), 0);
        bytes.extend(utf16(value));
        bytes.extend([0, 0]);
        bytes.resize(bytes.len().next_multiple_of(4), 0);
        bytes
    }

    #[test]
    fn the_product_version_is_read_from_the_version_resource() {
        // Version-resource structures are 32-bit aligned, as in an image.
        let mut image = b"MZ\x90\x00 not a real image   ".to_vec();
        assert!(image.len().is_multiple_of(4));
        image.extend(entry("FileVersion", "18.0.10.53188"));
        image.extend(entry("ProductVersion", "18.0.10.184260"));
        image.extend(entry("Comments", "x"));
        assert_eq!(
            product_version_from_pe(&image).as_deref(),
            Some("18.0.10.184260")
        );
        assert_eq!(product_version_from_pe(b"MZ no resources"), None);
        let mut truncated = b"MZ".to_vec();
        truncated.extend(utf16("ProductVersion"));
        assert_eq!(product_version_from_pe(&truncated), None);
    }

    #[test]
    fn the_banner_is_the_sdk_meta_generator_text() {
        assert_eq!(
            generator_banner("18.0.10.184260").as_deref(),
            Some("GeneXus .NET 18_0_10-184260")
        );
        for bad in ["", "18.0.10", "18.0.10.x", "18.0.10.184260.1", "18..10.1"] {
            assert_eq!(generator_banner(bad), None, "{bad}");
        }
    }
}
