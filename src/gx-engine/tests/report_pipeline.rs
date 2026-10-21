use gx_engine::report::{
    PageOrientation, PageSetup, PrintBlock, Report, ReportBandKind, ReportControl,
    ReportControlKind, ReportPipeline,
};
use std::collections::BTreeMap;

#[test]
fn test_report_ast_creation_and_json_roundtrip() {
    let mut header_pb = PrintBlock::new("pbHeader", ReportBandKind::PageHeader, 60);
    header_pb.controls.push(ReportControl::textblock(
        "lblTitle",
        "Monthly Sales Report",
        100,
        15,
    ));
    header_pb
        .controls
        .push(ReportControl::variable("varDate", "&Today", 400, 15));

    let mut detail_pb = PrintBlock::new("pbDetail", ReportBandKind::Detail, 25);
    detail_pb
        .controls
        .push(ReportControl::attribute("attProduct", "ProductName", 20, 5));
    detail_pb
        .controls
        .push(ReportControl::attribute("attQty", "Quantity", 250, 5));
    detail_pb
        .controls
        .push(ReportControl::attribute("attPrice", "Price", 350, 5));

    let mut footer_pb = PrintBlock::new("pbFooter", ReportBandKind::PageFooter, 30);
    footer_pb
        .controls
        .push(ReportControl::textblock("lblPage", "Page:", 450, 10));
    footer_pb
        .controls
        .push(ReportControl::variable("varPage", "&Page", 490, 10));

    let report = Report::new("SalesReport")
        .with_page_setup(PageSetup::a4_portrait())
        .with_print_block(header_pb)
        .with_print_block(detail_pb)
        .with_print_block(footer_pb)
        .with_rule("Output_File(\"sales.pdf\", \"PDF\")");

    assert!(report.validate().is_ok());
    assert_eq!(report.name, "SalesReport");
    assert_eq!(report.print_blocks.len(), 3);
    assert_eq!(report.page_setup.orientation, PageOrientation::Portrait);
    assert!(report.page_header().is_some());
    assert!(report.page_footer().is_some());
    assert_eq!(report.detail_blocks().len(), 1);

    // JSON round-trip
    let json = serde_json::to_string_pretty(&report).expect("Report should serialize to JSON");
    let deserialized: Report =
        serde_json::from_str(&json).expect("Report should deserialize from JSON");
    assert_eq!(report, deserialized);
}

#[test]
fn test_report_dsl_parsing() {
    let dsl = r#"
        report CustomerListing {
            orientation = "landscape";

            printblock Header (band = page_header, height = 50) {
                textblock lblHeader { caption = "Customer Directory"; x = 20; y = 10; }
                variable varPage { binding = "&Page"; x = 500; y = 10; }
            }

            printblock Detail (band = detail, height = 20) {
                attribute CustomerId { binding = "CustomerId"; x = 20; y = 2; }
                attribute CustomerName { binding = "CustomerName"; x = 120; y = 2; }
            }

            printblock Footer (band = page_footer, height = 30) {
                textblock lblConfidential { caption = "Confidential Report"; x = 20; y = 10; }
            }
        }
    "#;

    let report = Report::parse("CustomerListing", dsl).expect("Report DSL should parse");
    assert_eq!(report.name, "CustomerListing");
    assert_eq!(report.page_setup.orientation, PageOrientation::Landscape);
    assert_eq!(report.print_blocks.len(), 3);

    let header = report.find_print_block("Header").expect("Header pb exists");
    assert_eq!(header.band, ReportBandKind::PageHeader);
    assert_eq!(header.height, 50);
    assert_eq!(header.controls.len(), 2);

    let detail = report.find_print_block("Detail").expect("Detail pb exists");
    assert_eq!(detail.band, ReportBandKind::Detail);
    assert_eq!(detail.height, 20);
    assert_eq!(detail.controls.len(), 2);

    let footer = report.find_print_block("Footer").expect("Footer pb exists");
    assert_eq!(footer.band, ReportBandKind::PageFooter);
    assert_eq!(footer.height, 30);
    assert_eq!(footer.controls.len(), 1);
}

#[test]
fn test_report_validation_fail_closed() {
    // 1. Empty name
    let rep = Report::new("");
    assert!(rep.validate().is_err());

    // 2. Duplicate PrintBlock name
    let rep = Report::new("DupPb")
        .with_print_block(PrintBlock::new("pb1", ReportBandKind::Detail, 20))
        .with_print_block(PrintBlock::new("PB1", ReportBandKind::Detail, 30));
    assert!(rep.validate().is_err());

    // 3. PrintBlock with 0 height
    let rep =
        Report::new("ZeroH").with_print_block(PrintBlock::new("pbZero", ReportBandKind::Detail, 0));
    assert!(rep.validate().is_err());

    // 4. PrintBlock exceeding page height
    let rep = Report::new("HugePb").with_print_block(PrintBlock::new(
        "pbHuge",
        ReportBandKind::Detail,
        9000,
    ));
    assert!(rep.validate().is_err());

    // 5. Header + Footer exceeding printable height
    let mut setup = PageSetup::a4_portrait();
    setup.height = 300;
    setup.top_margin = 50;
    setup.bottom_margin = 50;
    // Printable height = 200
    let rep = Report::new("OverflowHF")
        .with_page_setup(setup)
        .with_print_block(PrintBlock::new("H", ReportBandKind::PageHeader, 120))
        .with_print_block(PrintBlock::new("F", ReportBandKind::PageFooter, 90));
    assert!(rep.validate().is_err());

    // 6. TextBlock missing caption
    let mut pb = PrintBlock::new("pbText", ReportBandKind::Detail, 30);
    pb.controls.push(ReportControl {
        name: "lblNoCaption".into(),
        kind: ReportControlKind::TextBlock,
        caption: None,
        binding: None,
        x: None,
        y: None,
        width: None,
        height: None,
        font_family: None,
        font_size: None,
        font_bold: false,
    });
    let rep = Report::new("NoCaption").with_print_block(pb);
    assert!(rep.validate().is_err());

    // 7. Attribute missing binding
    let mut pb = PrintBlock::new("pbAtt", ReportBandKind::Detail, 30);
    pb.controls.push(ReportControl {
        name: "attNoBinding".into(),
        kind: ReportControlKind::Attribute,
        caption: None,
        binding: None,
        x: None,
        y: None,
        width: None,
        height: None,
        font_family: None,
        font_size: None,
        font_bold: false,
    });
    let rep = Report::new("NoBinding").with_print_block(pb);
    assert!(rep.validate().is_err());
}

#[test]
fn test_report_pipeline_single_page() {
    let mut header = PrintBlock::new("Header", ReportBandKind::PageHeader, 40);
    header
        .controls
        .push(ReportControl::textblock("title", "Invoice #101", 10, 10));

    let mut detail = PrintBlock::new("Detail", ReportBandKind::Detail, 20);
    detail
        .controls
        .push(ReportControl::attribute("item", "ItemDescription", 10, 5));
    detail
        .controls
        .push(ReportControl::attribute("amount", "ItemAmount", 200, 5));

    let mut footer = PrintBlock::new("Footer", ReportBandKind::PageFooter, 30);
    footer
        .controls
        .push(ReportControl::textblock("lbl", "Page:", 400, 5));
    footer
        .controls
        .push(ReportControl::variable("page", "&Page", 450, 5));

    let report = Report::new("InvoicePrint")
        .with_print_block(header)
        .with_print_block(detail)
        .with_print_block(footer);

    let mut pipeline = ReportPipeline::new(report).expect("Pipeline should initialize");

    let mut ctx1 = BTreeMap::new();
    ctx1.insert("ItemDescription".to_string(), "Widget A".to_string());
    ctx1.insert("ItemAmount".to_string(), "$10.00".to_string());
    pipeline
        .print_block("Detail", &ctx1)
        .expect("print_block should succeed");

    let mut ctx2 = BTreeMap::new();
    ctx2.insert("ItemDescription".to_string(), "Widget B".to_string());
    ctx2.insert("ItemAmount".to_string(), "$25.00".to_string());
    pipeline
        .print_block("Detail", &ctx2)
        .expect("print_block should succeed");

    let ctx_empty = BTreeMap::new();
    let output = pipeline.finish(&ctx_empty).expect("finish should succeed");

    assert_eq!(output.total_pages, 1);
    assert_eq!(output.pages.len(), 1);

    let page = &output.pages[0];
    assert_eq!(page.page_number, 1);

    let text_items = output.all_rendered_text();
    assert!(text_items.contains(&"Invoice #101".to_string()));
    assert!(text_items.contains(&"Widget A".to_string()));
    assert!(text_items.contains(&"$10.00".to_string()));
    assert!(text_items.contains(&"Widget B".to_string()));
    assert!(text_items.contains(&"$25.00".to_string()));
    assert!(text_items.contains(&"Page:".to_string()));
    assert!(text_items.contains(&"1".to_string())); // &Page resolved to 1
}

#[test]
fn test_report_pipeline_multipage_automatic_pagebreak() {
    // Custom small page setup to easily test automatic page-break
    let mut setup = PageSetup::a4_portrait();
    setup.height = 200; // Small height
    setup.top_margin = 20;
    setup.bottom_margin = 20;
    // Printable height = 160

    let mut header = PrintBlock::new("Header", ReportBandKind::PageHeader, 30);
    header
        .controls
        .push(ReportControl::textblock("h", "HEADER", 10, 5));

    let mut footer = PrintBlock::new("Footer", ReportBandKind::PageFooter, 30);
    footer
        .controls
        .push(ReportControl::variable("p", "&Page", 10, 5));

    // Detail height = 40. Available body height = 160 - 30 (header) - 30 (footer) = 100
    // So page 1 can fit 2 detail blocks (2 * 40 = 80 <= 100). The 3rd detail block (80 + 40 = 120 > 100)
    // MUST trigger an automatic page break to Page 2!
    let mut detail = PrintBlock::new("Detail", ReportBandKind::Detail, 40);
    detail
        .controls
        .push(ReportControl::attribute("d", "RowName", 10, 5));

    let report = Report::new("Paginated")
        .with_page_setup(setup)
        .with_print_block(header)
        .with_print_block(detail)
        .with_print_block(footer);

    let mut pipeline = ReportPipeline::new(report).expect("Pipeline should initialize");

    for i in 1..=4 {
        let mut ctx = BTreeMap::new();
        ctx.insert("RowName".to_string(), format!("Item {i}"));
        pipeline
            .print_block("Detail", &ctx)
            .expect("print detail should succeed");
    }

    let empty = BTreeMap::new();
    let output = pipeline.finish(&empty).expect("finish should succeed");

    assert_eq!(output.total_pages, 2);
    assert_eq!(output.pages.len(), 2);

    let page1 = &output.pages[0];
    assert_eq!(page1.page_number, 1);
    let page2 = &output.pages[1];
    assert_eq!(page2.page_number, 2);

    // Page 1 should contain items 1 & 2 plus footer with Page 1
    let p1_text: Vec<&str> = page1.items.iter().map(|it| it.text.as_str()).collect();
    assert!(p1_text.contains(&"HEADER"));
    assert!(p1_text.contains(&"Item 1"));
    assert!(p1_text.contains(&"Item 2"));
    assert!(!p1_text.contains(&"Item 3"));
    assert!(p1_text.contains(&"1")); // Footer &Page == 1

    // Page 2 should contain items 3 & 4 plus footer with Page 2
    let p2_text: Vec<&str> = page2.items.iter().map(|it| it.text.as_str()).collect();
    assert!(p2_text.contains(&"HEADER"));
    assert!(p2_text.contains(&"Item 3"));
    assert!(p2_text.contains(&"Item 4"));
    assert!(p2_text.contains(&"2")); // Footer &Page == 2
}

#[test]
fn test_report_pipeline_manual_new_page() {
    let mut header = PrintBlock::new("H", ReportBandKind::PageHeader, 20);
    header
        .controls
        .push(ReportControl::textblock("t", "HDR", 0, 0));

    let mut detail = PrintBlock::new("D", ReportBandKind::Detail, 20);
    detail
        .controls
        .push(ReportControl::attribute("x", "Text", 0, 0));

    let report = Report::new("ManualBreak")
        .with_print_block(header)
        .with_print_block(detail);

    let mut pipeline = ReportPipeline::new(report).expect("Pipeline should initialize");

    let mut ctx1 = BTreeMap::new();
    ctx1.insert("Text".to_string(), "FirstPageText".to_string());
    pipeline.print_block("D", &ctx1).expect("print block 1 ok");

    pipeline
        .new_page(&ctx1)
        .expect("manual new_page should succeed");

    let mut ctx2 = BTreeMap::new();
    ctx2.insert("Text".to_string(), "SecondPageText".to_string());
    pipeline.print_block("D", &ctx2).expect("print block 2 ok");

    let output = pipeline.finish(&ctx2).expect("finish ok");
    assert_eq!(output.total_pages, 2);

    let p1 = &output.pages[0];
    let p2 = &output.pages[1];
    assert!(p1.items.iter().any(|i| i.text == "FirstPageText"));
    assert!(p2.items.iter().any(|i| i.text == "SecondPageText"));
}
