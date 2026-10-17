use gx_engine::{
    catalog::{
        Catalog, Edit, KbObject, ObjectId, ObjectKind, Reference, ReferenceKind, SourcePart,
    },
    design_system::{
        DesignSystemObject, DesignSystemToken, StyleDeclaration, StyleRule, TokenSet,
        strip_comments,
    },
};

#[test]
fn test_parse_tokens_and_groups() {
    let source = r#"
tokens AppTokens {
    #colors {
        primary: #3498db;
        secondary: #2ecc71;
        dark: #1e272e;
    }
    #spacing {
        small: 8px;
        medium: 16px;
        large: 24px;
    }
}
"#;
    let dso = DesignSystemObject::parse("AppTheme", source).expect("parse should succeed");
    assert_eq!(dso.name, "AppTheme");
    assert_eq!(dso.token_sets.len(), 2);

    let colors = dso
        .get_token_set("#colors")
        .expect("#colors set should exist");
    assert_eq!(colors.tokens.get("primary").unwrap(), "#3498db");
    assert_eq!(colors.tokens.get("secondary").unwrap(), "#2ecc71");
    assert_eq!(colors.tokens.get("dark").unwrap(), "#1e272e");

    let spacing = dso
        .get_token_set("#spacing")
        .expect("#spacing set should exist");
    assert_eq!(spacing.tokens.get("small").unwrap(), "8px");
    assert_eq!(spacing.tokens.get("medium").unwrap(), "16px");
    assert_eq!(spacing.tokens.get("large").unwrap(), "24px");

    let all = dso.tokens();
    assert_eq!(all.len(), 6);
    assert!(all.iter().any(|t| t.name == "primary"
        && t.value == "#3498db"
        && t.token_set.as_deref() == Some("#colors")));
}

#[test]
fn test_parse_style_rules_and_declarations() {
    let source = r#"
styles AppStyles {
    .Button {
        background-color: $colors.primary;
        padding: $spacing.small;
        border-radius: 4px;
    }
    .Card {
        margin: $spacing.medium;
        box-shadow: 0 2px 4px rgba(0,0,0,0.1);
    }
}
"#;
    let dso = DesignSystemObject::parse("AppStyles", source).expect("parse should succeed");
    assert_eq!(dso.rules.len(), 2);

    let button_rule = dso
        .rules
        .iter()
        .find(|r| r.selector == ".Button")
        .expect(".Button rule");
    assert_eq!(button_rule.declarations.len(), 3);
    assert_eq!(button_rule.declarations[0].property, "background-color");
    assert_eq!(button_rule.declarations[0].value, "$colors.primary");
    assert_eq!(button_rule.declarations[1].property, "padding");
    assert_eq!(button_rule.declarations[1].value, "$spacing.small");
    assert_eq!(button_rule.declarations[2].property, "border-radius");
    assert_eq!(button_rule.declarations[2].value, "4px");

    let card_rule = dso
        .rules
        .iter()
        .find(|r| r.selector == ".Card")
        .expect(".Card rule");
    assert_eq!(card_rule.declarations.len(), 2);
    assert_eq!(card_rule.declarations[0].property, "margin");
    assert_eq!(card_rule.declarations[0].value, "$spacing.medium");
    assert_eq!(card_rule.declarations[1].property, "box-shadow");
    assert_eq!(card_rule.declarations[1].value, "0 2px 4px rgba(0,0,0,0.1)");
}

#[test]
fn test_comment_stripping_in_dso() {
    // 1. Direct test of strip_comments helper
    let commented_code = r#"
// Single line comment
/* Multi-line
   block comment */
.Button {
    /* comment before property */
    color: red; // inline comment
    content: "/* not a comment */";
    quoted: '// also not a comment';
}
"#;
    let stripped = strip_comments(commented_code);
    assert!(!stripped.contains("Single line comment"));
    assert!(!stripped.contains("Multi-line"));
    assert!(!stripped.contains("inline comment"));
    assert!(stripped.contains("/* not a comment */"));
    assert!(stripped.contains("// also not a comment"));

    // 2. Full DSO parsing with comments
    let source = r#"
// Top-level line comment
/* Multi-line
   header block comment */
tokens ThemeTokens { // inline comment
    #colors {
        /* Color token definition */
        primary: #3498db; // brand color
        text: '/* not a comment */';
        url: "https://example.com/api//not-comment";
    }
}
/* Styles section comment */
styles ThemeStyles {
    // Style rule comment
    .Button {
        background-color: $colors.primary; /* comment inside rule */
    }
}
"#;
    let dso = DesignSystemObject::parse("CommentTheme", source).expect("parse should succeed");
    let colors = dso.get_token_set("#colors").expect("#colors");
    assert_eq!(colors.tokens.get("primary").unwrap(), "#3498db");
    assert_eq!(colors.tokens.get("text").unwrap(), "'/* not a comment */'");
    assert_eq!(
        colors.tokens.get("url").unwrap(),
        "\"https://example.com/api//not-comment\""
    );

    let rule = dso
        .rules
        .iter()
        .find(|r| r.selector == ".Button")
        .expect(".Button");
    assert_eq!(rule.declarations[0].property, "background-color");
    assert_eq!(rule.declarations[0].value, "$colors.primary");
}

#[test]
fn test_token_resolution_and_css_rendering() {
    let source = r#"
tokens AppTokens {
    #colors {
        primary: #3498db;
        border: #cccccc;
    }
    #spacing {
        small: 8px;
    }
}
styles AppStyles {
    .Button {
        background-color: $colors.primary;
        padding: $spacing.small;
        border: 1px solid $colors.border;
    }
}
"#;
    let dso = DesignSystemObject::parse("AppTheme", source).expect("parse should succeed");

    // Test token resolution directly
    assert_eq!(
        dso.resolve_token("$colors.primary", None),
        Some("#3498db".to_string())
    );
    assert_eq!(
        dso.resolve_token("colors.primary", None),
        Some("#3498db".to_string())
    );
    assert_eq!(
        dso.resolve_token("$spacing.small", None),
        Some("8px".to_string())
    );
    assert_eq!(
        dso.resolve_token("$primary", None),
        Some("#3498db".to_string())
    );

    // Test CSS rendering
    let css = dso.render_css(None).expect("rendering CSS should succeed");
    assert!(css.contains(".Button {"));
    assert!(css.contains("background-color: #3498db;"));
    assert!(css.contains("padding: 8px;"));
    assert!(css.contains("border: 1px solid #cccccc;"));
}

#[test]
fn test_unresolved_token_fails_closed() {
    let source = r#"
tokens AppTokens {
    #colors {
        primary: #3498db;
    }
}
styles AppStyles {
    .Button {
        background-color: $colors.primary;
        color: $colors.nonexistent;
    }
}
"#;
    let dso = DesignSystemObject::parse("AppTheme", source).expect("parse should succeed");
    assert_eq!(dso.resolve_token("$colors.nonexistent", None), None);

    let result = dso.render_css(None);
    assert!(
        result.is_err(),
        "render_css must fail closed when a token is unresolved"
    );
    let err = result.unwrap_err();
    assert!(
        err.contains("$colors.nonexistent") || err.contains("unresolved"),
        "Error message should mention unresolved token: {err}"
    );
}

#[test]
fn test_dso_import_and_token_inheritance() {
    let base_source = r#"
tokens BaseTokens {
    #colors {
        primary: #3498db;
        secondary: #2ecc71;
        background: #ffffff;
    }
    #spacing {
        base: 16px;
    }
}
"#;
    let base_dso = DesignSystemObject::parse("BaseTheme", base_source).expect("parse base");

    let child_source = r#"
styles ChildStyles (BaseTheme) {
    .Button {
        background-color: $colors.primary;
        padding: $spacing.base;
        color: $colors.background;
    }
    .SpecialButton {
        background-color: $colors.custom;
    }
}
tokens ChildTokens {
    #colors {
        custom: #e74c3c;
        background: #f8f9fa;
    }
}
"#;
    let child_dso = DesignSystemObject::parse("ChildTheme", child_source).expect("parse child");
    assert!(child_dso.imports.contains(&"BaseTheme".to_string()));

    // Verify inheritance in resolve_token
    assert_eq!(
        child_dso.resolve_token("$colors.primary", Some(&base_dso)),
        Some("#3498db".to_string())
    );
    assert_eq!(
        child_dso.resolve_token("$spacing.base", Some(&base_dso)),
        Some("16px".to_string())
    );
    assert_eq!(
        child_dso.resolve_token("$colors.custom", Some(&base_dso)),
        Some("#e74c3c".to_string())
    );
    // Verify override: child's #colors.background (#f8f9fa) overrides parent's (#ffffff)
    assert_eq!(
        child_dso.resolve_token("$colors.background", Some(&base_dso)),
        Some("#f8f9fa".to_string())
    );

    // Verify CSS rendering with inheritance
    let css = child_dso
        .render_css(Some(&base_dso))
        .expect("render child css");
    assert!(css.contains("background-color: #3498db;"));
    assert!(css.contains("padding: 16px;"));
    assert!(css.contains("color: #f8f9fa;"));
    assert!(css.contains("background-color: #e74c3c;"));
}

#[test]
fn test_pseudo_classes_and_complex_selectors() {
    let source = r#"
tokens Tokens {
    #colors {
        hover: #2980b9;
        title: #2c3e50;
    }
}
styles ComplexStyles {
    .Button:hover {
        background-color: $colors.hover;
        cursor: pointer;
    }
    .Button:focus:not(:disabled) {
        outline: 2px solid $colors.hover;
    }
    .Card > .Title {
        color: $colors.title;
        font-weight: bold;
    }
    .List .Item + .Item {
        margin-top: 8px;
    }
}
"#;
    let dso = DesignSystemObject::parse("ComplexTheme", source).expect("parse complex");
    assert_eq!(dso.rules.len(), 4);

    let selectors: Vec<&str> = dso.rules.iter().map(|r| r.selector.as_str()).collect();
    assert!(selectors.contains(&".Button:hover"));
    assert!(selectors.contains(&".Button:focus:not(:disabled)"));
    assert!(selectors.contains(&".Card > .Title"));
    assert!(selectors.contains(&".List .Item + .Item"));

    let css = dso.render_css(None).expect("render complex CSS");
    assert!(css.contains(".Button:hover {"));
    assert!(css.contains("background-color: #2980b9;"));
    assert!(css.contains(".Card > .Title {"));
    assert!(css.contains("color: #2c3e50;"));
}

#[test]
fn test_dso_ast_json_serialization() {
    let mut dso = DesignSystemObject::new("SerializationTheme");
    dso.imports = vec!["BaseDSO".to_string()];

    let mut colors = TokenSet::new("#colors");
    colors.insert("primary", "#3498db");
    colors.insert("accent", "#e74c3c");
    dso.token_sets.push(colors);

    dso.rules.push(StyleRule::new(
        ".Button",
        vec![
            StyleDeclaration::new("background-color", "$colors.primary"),
            StyleDeclaration::new("color", "#ffffff"),
        ],
    ));

    let serialized = serde_json::to_string(&dso).expect("serialize DSO");
    let deserialized: DesignSystemObject =
        serde_json::from_str(&serialized).expect("deserialize DSO");

    assert_eq!(dso, deserialized);
    assert_eq!(deserialized.name, "SerializationTheme");
    assert_eq!(deserialized.imports, vec!["BaseDSO"]);
    assert_eq!(deserialized.token_sets.len(), 1);
    assert_eq!(deserialized.rules.len(), 1);

    // Also verify DesignSystemToken struct serialization
    let token = DesignSystemToken::new("primary", "#3498db", Some("#colors"));
    let tok_json = serde_json::to_string(&token).unwrap();
    let tok_deser: DesignSystemToken = serde_json::from_str(&tok_json).unwrap();
    assert_eq!(token, tok_deser);
}

#[test]
fn test_font_and_media_query_tokens() {
    let source = r#"
tokens AppTokens {
    #fonts {
        headline: "Roboto", sans-serif;
        headline-size: 28px;
        body: "Open Sans", sans-serif;
        body-size: 14px;
    }
    #media-queries {
        mobile: screen and (max-width: 768px);
        tablet: screen and (min-width: 769px) and (max-width: 1024px);
    }
}
styles AppStyles {
    .Headline {
        font-family: $fonts.headline;
        font-size: $fonts.headline-size;
    }
    .BodyText {
        font-family: $fonts.body;
        font-size: $fonts.body-size;
    }
    .ResponsiveContainer {
        --mobile-query: $media-queries.mobile;
        --tablet-query: $media-queries.tablet;
    }
}
"#;
    let dso = DesignSystemObject::parse("FontMediaTheme", source).expect("parse font/media");
    assert_eq!(
        dso.resolve_token("$fonts.headline", None),
        Some("\"Roboto\", sans-serif".to_string())
    );
    assert_eq!(
        dso.resolve_token("$fonts.headline-size", None),
        Some("28px".to_string())
    );
    assert_eq!(
        dso.resolve_token("$media-queries.mobile", None),
        Some("screen and (max-width: 768px)".to_string())
    );

    let css = dso.render_css(None).expect("render font/media CSS");
    assert!(css.contains("font-family: \"Roboto\", sans-serif;"));
    assert!(css.contains("font-size: 28px;"));
    assert!(css.contains("--mobile-query: screen and (max-width: 768px);"));
}

#[test]
fn test_catalog_integration_with_dso_object() {
    let mut catalog = Catalog::empty();
    let base_id = ObjectId::new("base-dso").unwrap();
    let app_id = ObjectId::new("app-dso").unwrap();

    let base_tokens_source = r#"
tokens BaseTokens {
    #colors {
        primary: #2980b9;
        accent: #e67e22;
    }
    #spacing {
        medium: 16px;
    }
}
"#;
    let mut base_obj = KbObject::new(base_id.clone(), "BaseDSO", ObjectKind::DesignSystem);
    base_obj.source_parts.push(SourcePart {
        name: "TOKENS".into(),
        source: base_tokens_source.into(),
    });

    let app_styles_source = r#"
styles AppDSO (BaseDSO) {
    .ActionButton {
        background-color: $colors.primary;
        color: $colors.accent;
        padding: $spacing.medium;
    }
}
"#;
    let mut app_obj = KbObject::new(app_id.clone(), "AppDSO", ObjectKind::DesignSystem);
    app_obj.source_parts.push(SourcePart {
        name: "STYLES".into(),
        source: app_styles_source.into(),
    });
    app_obj.references.push(Reference {
        kind: ReferenceKind::Uses,
        target: base_id.clone(),
    });

    catalog
        .apply(0, &[Edit::Upsert(base_obj), Edit::Upsert(app_obj)])
        .expect("catalog apply should succeed");

    // Retrieve from catalog and verify kind
    let retrieved_app = catalog.get(&app_id).expect("AppDSO exists");
    assert_eq!(retrieved_app.kind, ObjectKind::DesignSystem);
    assert_eq!(retrieved_app.name, "APPDSO");

    let retrieved_base = catalog.get(&base_id).expect("BaseDSO exists");
    assert_eq!(retrieved_base.kind, ObjectKind::DesignSystem);
    assert_eq!(retrieved_base.name, "BASEDSO");

    // Parse and render
    let base_part = retrieved_base
        .source_parts
        .iter()
        .find(|p| p.name == "TOKENS")
        .unwrap();
    let base_dso = DesignSystemObject::parse(&retrieved_base.name, &base_part.source).unwrap();

    let app_part = retrieved_app
        .source_parts
        .iter()
        .find(|p| p.name == "STYLES")
        .unwrap();
    let app_dso = DesignSystemObject::parse(&retrieved_app.name, &app_part.source).unwrap();

    assert!(app_dso.imports.contains(&"BaseDSO".to_string()));
    let css = app_dso
        .render_css(Some(&base_dso))
        .expect("render CSS with catalog objects");
    assert!(css.contains("background-color: #2980b9;"));
    assert!(css.contains("color: #e67e22;"));
    assert!(css.contains("padding: 16px;"));

    // Verify catalog serialization round-trip
    let cat_json = catalog.to_json().expect("catalog to_json");
    let loaded = Catalog::from_json(&cat_json).expect("catalog from_json");
    assert_eq!(catalog.revision(), loaded.revision());
    let loaded_app = loaded.get(&app_id).unwrap();
    assert_eq!(loaded_app.kind, ObjectKind::DesignSystem);
}
