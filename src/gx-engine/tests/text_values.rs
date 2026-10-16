use gx_engine::text::{MAX_TEXT_BYTES, Text};

#[test]
fn text_storage_and_concatenation_preserve_spaces_and_unicode() {
    let value = Text::new(" ação ").unwrap();
    assert_eq!(value.to_string(), " ação ");
    assert_eq!(
        value
            .checked_concat(Text::new("x").unwrap())
            .unwrap()
            .to_string(),
        " ação x"
    );
    assert_eq!(Text::new("abc ").unwrap(), Text::new("abc").unwrap());
    assert_ne!(Text::new(" abc").unwrap(), Text::new("abc").unwrap());
    assert_ne!(Text::new("A").unwrap(), Text::new("a").unwrap());
}

#[test]
fn text_resource_limit_rejects_before_concatenation_allocation() {
    let maximum = "x".repeat(MAX_TEXT_BYTES);
    assert!(Text::new(&(maximum.clone() + "x")).is_err());
    let value = Text::new(&maximum).unwrap();
    assert!(
        value
            .clone()
            .checked_concat(Text::new("x").unwrap())
            .is_err()
    );
    assert_eq!(
        value
            .checked_concat(Text::new("").unwrap())
            .unwrap()
            .to_string()
            .len(),
        MAX_TEXT_BYTES
    );
}
