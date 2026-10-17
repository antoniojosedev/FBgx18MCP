//! Report, Print Blocks, Layout Bands, and Page Pipeline (Plan 130 Track B).
//!
//! Provides data structures, DSL parser, print blocks, bands (PageHeader,
//! PageFooter, Detail, etc.), page layout setup, and an execution pipeline
//! for GeneXus Reports and Procedure report generation.

use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};

use crate::dataview::strip_comments;

/// Standard report band classification matching GeneXus report layouts.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ReportBandKind {
    PageHeader,
    PageFooter,
    ColumnHeader,
    Detail,
    GroupHeader,
    GroupFooter,
    Title,
    Summary,
}

/// Control types placed inside Report PrintBlocks.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ReportControlKind {
    TextBlock,
    Attribute,
    Variable,
    Line,
    Rectangle,
    Image,
}

/// A positioned control in a Report PrintBlock.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReportControl {
    pub name: String,
    pub kind: ReportControlKind,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub caption: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub binding: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub x: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub y: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub width: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub height: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub font_family: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub font_size: Option<u32>,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub font_bold: bool,
}

impl ReportControl {
    pub fn textblock(name: impl Into<String>, caption: impl Into<String>, x: u32, y: u32) -> Self {
        Self {
            name: name.into(),
            kind: ReportControlKind::TextBlock,
            caption: Some(caption.into()),
            binding: None,
            x: Some(x),
            y: Some(y),
            width: None,
            height: None,
            font_family: None,
            font_size: None,
            font_bold: false,
        }
    }

    pub fn attribute(name: impl Into<String>, binding: impl Into<String>, x: u32, y: u32) -> Self {
        Self {
            name: name.into(),
            kind: ReportControlKind::Attribute,
            caption: None,
            binding: Some(binding.into()),
            x: Some(x),
            y: Some(y),
            width: None,
            height: None,
            font_family: None,
            font_size: None,
            font_bold: false,
        }
    }

    pub fn variable(name: impl Into<String>, binding: impl Into<String>, x: u32, y: u32) -> Self {
        Self {
            name: name.into(),
            kind: ReportControlKind::Variable,
            caption: None,
            binding: Some(binding.into()),
            x: Some(x),
            y: Some(y),
            width: None,
            height: None,
            font_family: None,
            font_size: None,
            font_bold: false,
        }
    }

    pub fn line(name: impl Into<String>, x: u32, y: u32, width: u32, height: u32) -> Self {
        Self {
            name: name.into(),
            kind: ReportControlKind::Line,
            caption: None,
            binding: None,
            x: Some(x),
            y: Some(y),
            width: Some(width),
            height: Some(height),
            font_family: None,
            font_size: None,
            font_bold: false,
        }
    }

    pub fn rectangle(name: impl Into<String>, x: u32, y: u32, width: u32, height: u32) -> Self {
        Self {
            name: name.into(),
            kind: ReportControlKind::Rectangle,
            caption: None,
            binding: None,
            x: Some(x),
            y: Some(y),
            width: Some(width),
            height: Some(height),
            font_family: None,
            font_size: None,
            font_bold: false,
        }
    }
}

/// A PrintBlock section containing controls rendered together in a band.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PrintBlock {
    pub name: String,
    pub band: ReportBandKind,
    pub height: u32,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub controls: Vec<ReportControl>,
}

impl PrintBlock {
    pub fn new(name: impl Into<String>, band: ReportBandKind, height: u32) -> Self {
        Self {
            name: name.into(),
            band,
            height,
            controls: Vec::new(),
        }
    }

    pub fn with_control(mut self, control: ReportControl) -> Self {
        self.controls.push(control);
        self
    }

    pub fn find_control(&self, name: &str) -> Option<&ReportControl> {
        let needle = name.to_ascii_lowercase();
        self.controls
            .iter()
            .find(|c| c.name.to_ascii_lowercase() == needle)
    }
}

/// Paper orientation for the report.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum PageOrientation {
    #[default]
    Portrait,
    Landscape,
}

/// Page dimensions and margins setup in points (72 points = 1 inch).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PageSetup {
    pub width: u32,
    pub height: u32,
    pub top_margin: u32,
    pub bottom_margin: u32,
    pub left_margin: u32,
    pub right_margin: u32,
    #[serde(default)]
    pub orientation: PageOrientation,
}

impl Default for PageSetup {
    fn default() -> Self {
        Self::a4_portrait()
    }
}

impl PageSetup {
    /// Standard A4 Portrait (595 x 842 points).
    pub fn a4_portrait() -> Self {
        Self {
            width: 595,
            height: 842,
            top_margin: 36,
            bottom_margin: 36,
            left_margin: 36,
            right_margin: 36,
            orientation: PageOrientation::Portrait,
        }
    }

    /// Standard A4 Landscape (842 x 595 points).
    pub fn a4_landscape() -> Self {
        Self {
            width: 842,
            height: 595,
            top_margin: 36,
            bottom_margin: 36,
            left_margin: 36,
            right_margin: 36,
            orientation: PageOrientation::Landscape,
        }
    }

    /// US Letter Portrait (612 x 792 points).
    pub fn letter_portrait() -> Self {
        Self {
            width: 612,
            height: 792,
            top_margin: 36,
            bottom_margin: 36,
            left_margin: 36,
            right_margin: 36,
            orientation: PageOrientation::Portrait,
        }
    }

    /// US Letter Landscape (792 x 612 points).
    pub fn letter_landscape() -> Self {
        Self {
            width: 792,
            height: 612,
            top_margin: 36,
            bottom_margin: 36,
            left_margin: 36,
            right_margin: 36,
            orientation: PageOrientation::Landscape,
        }
    }

    pub fn printable_height(&self) -> u32 {
        self.height
            .saturating_sub(self.top_margin + self.bottom_margin)
    }

    pub fn printable_width(&self) -> u32 {
        self.width
            .saturating_sub(self.left_margin + self.right_margin)
    }
}

/// Complete Report layout definition containing PrintBlocks, PageSetup, and rules.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct Report {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(default)]
    pub page_setup: PageSetup,
    #[serde(default)]
    pub print_blocks: Vec<PrintBlock>,
    #[serde(default)]
    pub rules: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub variables: Vec<crate::webpanel::ScreenVariable>,
}

impl Report {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            page_setup: PageSetup::default(),
            print_blocks: Vec::new(),
            rules: Vec::new(),
            variables: Vec::new(),
            description: None,
        }
    }

    pub fn with_page_setup(mut self, setup: PageSetup) -> Self {
        self.page_setup = setup;
        self
    }

    pub fn with_print_block(mut self, block: PrintBlock) -> Self {
        self.print_blocks.push(block);
        self
    }

    pub fn with_rule(mut self, rule: impl Into<String>) -> Self {
        self.rules.push(rule.into());
        self
    }

    pub fn with_variable(mut self, var: crate::webpanel::ScreenVariable) -> Self {
        self.variables.push(var);
        self
    }

    /// Validates the Report structure fail-closed:
    /// - Name non-empty
    /// - Printable dimensions positive
    /// - Unique PrintBlock names
    /// - Controls valid within PrintBlocks
    /// - Header and Footer heights fit within printable height
    pub fn validate(&self) -> Result<(), String> {
        let trimmed_name = self.name.trim();
        if trimmed_name.is_empty() {
            return Err("Report name cannot be empty".to_string());
        }

        let printable_h = self.page_setup.printable_height();
        if printable_h == 0 {
            return Err("Report page setup printable height must be positive".to_string());
        }

        let mut seen_blocks = HashSet::new();
        let mut header_height = 0;
        let mut footer_height = 0;

        for pb in &self.print_blocks {
            if pb.name.trim().is_empty() {
                return Err("PrintBlock name cannot be empty".to_string());
            }
            let key = pb.name.to_ascii_lowercase();
            if !seen_blocks.insert(key) {
                return Err(format!("Duplicate PrintBlock name '{}' in report", pb.name));
            }
            if pb.height == 0 {
                return Err(format!("PrintBlock '{}' height must be positive", pb.name));
            }
            if pb.height > printable_h {
                return Err(format!(
                    "PrintBlock '{}' height ({}) exceeds printable page height ({})",
                    pb.name, pb.height, printable_h
                ));
            }

            match pb.band {
                ReportBandKind::PageHeader => header_height += pb.height,
                ReportBandKind::PageFooter => footer_height += pb.height,
                _ => {}
            }

            let mut seen_ctrls = HashSet::new();
            for c in &pb.controls {
                if c.name.trim().is_empty() {
                    return Err(format!(
                        "Control in PrintBlock '{}' has empty name",
                        pb.name
                    ));
                }
                let c_key = c.name.to_ascii_lowercase();
                if !seen_ctrls.insert(c_key) {
                    return Err(format!(
                        "Duplicate control name '{}' in PrintBlock '{}'",
                        c.name, pb.name
                    ));
                }
                if c.kind == ReportControlKind::TextBlock && c.caption.is_none() {
                    return Err(format!(
                        "TextBlock control '{}' in PrintBlock '{}' requires caption",
                        c.name, pb.name
                    ));
                }
                if (c.kind == ReportControlKind::Attribute || c.kind == ReportControlKind::Variable)
                    && c.binding.is_none()
                {
                    return Err(format!(
                        "Control '{}' in PrintBlock '{}' requires binding",
                        c.name, pb.name
                    ));
                }
            }
        }

        if header_height + footer_height >= printable_h {
            return Err(format!(
                "Combined PageHeader ({header_height}) and PageFooter ({footer_height}) heights exceed printable height ({printable_h})"
            ));
        }

        Ok(())
    }

    pub fn find_print_block(&self, name: &str) -> Option<&PrintBlock> {
        let needle = name.to_ascii_lowercase();
        self.print_blocks
            .iter()
            .find(|pb| pb.name.to_ascii_lowercase() == needle)
    }

    pub fn page_header(&self) -> Option<&PrintBlock> {
        self.print_blocks
            .iter()
            .find(|pb| pb.band == ReportBandKind::PageHeader)
    }

    pub fn page_footer(&self) -> Option<&PrintBlock> {
        self.print_blocks
            .iter()
            .find(|pb| pb.band == ReportBandKind::PageFooter)
    }

    pub fn detail_blocks(&self) -> Vec<&PrintBlock> {
        self.print_blocks
            .iter()
            .filter(|pb| pb.band == ReportBandKind::Detail)
            .collect()
    }

    /// Available vertical height on each page for body / detail content.
    pub fn body_height_available(&self) -> u32 {
        let header_h = self.page_header().map(|h| h.height).unwrap_or(0);
        let footer_h = self.page_footer().map(|f| f.height).unwrap_or(0);
        self.page_setup
            .printable_height()
            .saturating_sub(header_h + footer_h)
    }

    /// Parses a Report from JSON or structured DSL format.
    pub fn parse(name: &str, source: &str) -> Result<Self, String> {
        let clean = strip_comments(source);
        let trimmed = clean.trim();

        if trimmed.is_empty() {
            return Err("Report source cannot be empty".to_string());
        }

        if trimmed.starts_with('{') {
            let mut rep: Report = serde_json::from_str(trimmed)
                .map_err(|e| format!("Failed to parse Report JSON: {e}"))?;
            if rep.name.is_empty() && !name.is_empty() {
                rep.name = name.to_string();
            }
            rep.validate()?;
            return Ok(rep);
        }

        // DSL format:
        // report <Name> {
        //   orientation = "portrait";
        //   printblock <Name> (band = <band>, height = <h>) {
        //      textblock <Name> { caption = "text"; x = 10; y = 10; }
        //      attribute <Name> { binding = "att"; x = 20; y = 10; }
        //   }
        // }
        let mut rep = Report::new(name);

        let mut in_pb = false;
        let mut current_pb: Option<PrintBlock> = None;
        let mut pb_body = String::new();
        let mut brace_depth = 0;

        for line in clean.lines() {
            let l = line.trim();
            if l.is_empty() {
                continue;
            }

            if l.starts_with("report ") {
                let parts: Vec<&str> = l.split_whitespace().collect();
                if parts.len() >= 2 {
                    let parsed_name = parts[1].trim_matches('{').trim();
                    if !parsed_name.is_empty() {
                        rep.name = parsed_name.to_string();
                    }
                }
            } else if l.starts_with("orientation") && l.contains('=') {
                if let Some((_, val)) = l.split_once('=') {
                    let v = val.trim().trim_matches(';').trim_matches('"');
                    if v.eq_ignore_ascii_case("landscape") {
                        rep.page_setup = PageSetup::a4_landscape();
                    } else {
                        rep.page_setup = PageSetup::a4_portrait();
                    }
                }
            } else if l.starts_with("printblock ") && l.contains('{') {
                in_pb = true;
                brace_depth = 1;
                let pb_header = l.split('{').next().unwrap().trim();
                current_pb = Some(parse_printblock_header(pb_header)?);
                pb_body.clear();
            } else if in_pb {
                if l.contains('{') {
                    brace_depth += l.matches('{').count();
                }
                if l.contains('}') {
                    let closes = l.matches('}').count();
                    if closes >= brace_depth {
                        in_pb = false;
                        brace_depth = 0;
                        if let Some(mut pb) = current_pb.take() {
                            pb.controls = parse_report_controls(&pb_body)?;
                            rep.print_blocks.push(pb);
                        }
                        pb_body.clear();
                        continue;
                    } else {
                        brace_depth -= closes;
                    }
                }
                pb_body.push_str(line);
                pb_body.push('\n');
            }
        }

        if let Some(mut pb) = current_pb.take() {
            pb.controls = parse_report_controls(&pb_body)?;
            rep.print_blocks.push(pb);
        }

        rep.validate()?;
        Ok(rep)
    }
}

fn parse_printblock_header(header: &str) -> Result<PrintBlock, String> {
    // printblock <Name> (band = <band>, height = <h>)
    let parts: Vec<&str> = header.split_whitespace().collect();
    if parts.len() < 2 {
        return Err(format!("Invalid printblock header '{header}'"));
    }
    let name = parts[1].trim_matches('(').trim();
    let mut band = ReportBandKind::Detail;
    let mut height = 20;

    if let (Some(start), Some(end)) = (header.find('('), header.rfind(')')) {
        let props = &header[start + 1..end];
        for prop in props.split(',') {
            if let Some((k, v)) = prop.split_once('=') {
                let k = k.trim().to_ascii_lowercase();
                let v = v.trim().trim_matches('"');
                match k.as_str() {
                    "band" => match v.to_ascii_lowercase().as_str() {
                        "page_header" | "header" => band = ReportBandKind::PageHeader,
                        "page_footer" | "footer" => band = ReportBandKind::PageFooter,
                        "column_header" => band = ReportBandKind::ColumnHeader,
                        "detail" => band = ReportBandKind::Detail,
                        "group_header" => band = ReportBandKind::GroupHeader,
                        "group_footer" => band = ReportBandKind::GroupFooter,
                        "title" => band = ReportBandKind::Title,
                        "summary" => band = ReportBandKind::Summary,
                        _ => {}
                    },
                    "height" => {
                        if let Ok(h) = v.parse::<u32>() {
                            height = h;
                        }
                    }
                    _ => {}
                }
            }
        }
    }

    Ok(PrintBlock::new(name, band, height))
}

fn parse_report_controls(source: &str) -> Result<Vec<ReportControl>, String> {
    let mut controls = Vec::new();
    for line in source.lines() {
        let l = line.trim();
        if l.is_empty() || l.starts_with("//") {
            continue;
        }

        let tokens: Vec<&str> = l.split_whitespace().collect();
        if tokens.len() < 2 {
            continue;
        }

        let kind = match tokens[0].to_ascii_lowercase().as_str() {
            "textblock" => ReportControlKind::TextBlock,
            "attribute" => ReportControlKind::Attribute,
            "variable" => ReportControlKind::Variable,
            "line" => ReportControlKind::Line,
            "rectangle" | "rect" => ReportControlKind::Rectangle,
            "image" => ReportControlKind::Image,
            _ => continue,
        };

        let name = tokens[1].trim_matches('{').to_string();
        let mut control = ReportControl {
            name,
            kind,
            caption: None,
            binding: None,
            x: None,
            y: None,
            width: None,
            height: None,
            font_family: None,
            font_size: None,
            font_bold: false,
        };

        if let (Some(brace_start), Some(brace_end)) = (l.find('{'), l.rfind('}')) {
            let props_str = &l[brace_start + 1..brace_end];
            for prop in props_str.split(';') {
                let prop = prop.trim();
                if let Some((k, v)) = prop.split_once('=') {
                    let k = k.trim().to_ascii_lowercase();
                    let v = v.trim().trim_matches('"');
                    match k.as_str() {
                        "caption" => control.caption = Some(v.to_string()),
                        "binding" => control.binding = Some(v.to_string()),
                        "x" => control.x = v.parse().ok(),
                        "y" => control.y = v.parse().ok(),
                        "width" | "w" => control.width = v.parse().ok(),
                        "height" | "h" => control.height = v.parse().ok(),
                        "font_size" | "size" => control.font_size = v.parse().ok(),
                        "bold" => control.font_bold = v.eq_ignore_ascii_case("true"),
                        _ => {}
                    }
                }
            }
        }

        if control.binding.is_none()
            && (control.kind == ReportControlKind::Attribute
                || control.kind == ReportControlKind::Variable)
        {
            control.binding = Some(control.name.clone());
        }

        controls.push(control);
    }
    Ok(controls)
}

// ---------------------------------------------------------------------------
// Report Pipeline: Layout rendering, automatic page breaks, and headers/footers
// ---------------------------------------------------------------------------

/// Rendered visual element on an output page.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RenderedItem {
    pub control_name: String,
    pub kind: ReportControlKind,
    pub text: String,
    pub x: u32,
    pub y: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub width: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub height: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub font_size: Option<u32>,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub font_bold: bool,
}

/// A rendered page in the report output.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReportPage {
    pub page_number: u32,
    pub items: Vec<RenderedItem>,
}

/// Final output produced by executing the ReportPipeline.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReportOutput {
    pub report_name: String,
    pub total_pages: u32,
    pub pages: Vec<ReportPage>,
}

impl ReportOutput {
    pub fn find_items_by_text(&self, needle: &str) -> Vec<&RenderedItem> {
        let needle_lower = needle.to_ascii_lowercase();
        let mut result = Vec::new();
        for page in &self.pages {
            for item in &page.items {
                if item.text.to_ascii_lowercase().contains(&needle_lower) {
                    result.push(item);
                }
            }
        }
        result
    }

    pub fn all_rendered_text(&self) -> Vec<String> {
        let mut result = Vec::new();
        for page in &self.pages {
            for item in &page.items {
                if !item.text.is_empty() {
                    result.push(item.text.clone());
                }
            }
        }
        result
    }
}

/// Execution pipeline for GeneXus reports simulating printing printblocks
/// onto paginated sheets with automatic page breaks, headers, and footers.
pub struct ReportPipeline {
    report: Report,
    current_page: u32,
    current_y: u32,
    pages: Vec<ReportPage>,
    current_items: Vec<RenderedItem>,
    started: bool,
}

impl ReportPipeline {
    pub fn new(report: Report) -> Result<Self, String> {
        report.validate()?;
        Ok(Self {
            report,
            current_page: 1,
            current_y: 0,
            pages: Vec::new(),
            current_items: Vec::new(),
            started: false,
        })
    }

    /// Starts the report, initializing Page 1 and emitting PageHeader if defined.
    pub fn start(&mut self, context: &BTreeMap<String, String>) -> Result<(), String> {
        if self.started {
            return Ok(());
        }
        self.started = true;
        self.current_y = self.report.page_setup.top_margin;
        self.emit_page_header(context);
        Ok(())
    }

    /// Prints a named PrintBlock onto the report. Automatically breaks page
    /// if the block exceeds available printable vertical space.
    pub fn print_block(
        &mut self,
        block_name: &str,
        context: &BTreeMap<String, String>,
    ) -> Result<(), String> {
        if !self.started {
            self.start(context)?;
        }

        let pb = self
            .report
            .find_print_block(block_name)
            .ok_or_else(|| format!("PrintBlock '{block_name}' not found in report"))?
            .clone();

        let footer_h = self.report.page_footer().map(|f| f.height).unwrap_or(0);
        let max_y = self.report.page_setup.height - self.report.page_setup.bottom_margin - footer_h;

        // Automatic page break if block doesn't fit
        if self.current_y + pb.height > max_y {
            self.trigger_page_break(context);
        }

        self.render_block_controls(&pb, self.current_y, context);
        self.current_y += pb.height;
        Ok(())
    }

    /// Forces a page break, emitting PageFooter on the current page and PageHeader on the next.
    pub fn new_page(&mut self, context: &BTreeMap<String, String>) -> Result<(), String> {
        if !self.started {
            self.start(context)?;
        }
        self.trigger_page_break(context);
        Ok(())
    }

    /// Completes the report, emitting final PageFooter on the last page.
    pub fn finish(mut self, context: &BTreeMap<String, String>) -> Result<ReportOutput, String> {
        if !self.started {
            self.start(context)?;
        }

        self.emit_page_footer(context);
        self.pages.push(ReportPage {
            page_number: self.current_page,
            items: std::mem::take(&mut self.current_items),
        });

        Ok(ReportOutput {
            report_name: self.report.name.clone(),
            total_pages: self.pages.len() as u32,
            pages: self.pages,
        })
    }

    fn trigger_page_break(&mut self, context: &BTreeMap<String, String>) {
        self.emit_page_footer(context);
        self.pages.push(ReportPage {
            page_number: self.current_page,
            items: std::mem::take(&mut self.current_items),
        });

        self.current_page += 1;
        self.current_y = self.report.page_setup.top_margin;
        self.emit_page_header(context);
    }

    fn emit_page_header(&mut self, context: &BTreeMap<String, String>) {
        if let Some(header) = self.report.page_header().cloned() {
            let y = self.report.page_setup.top_margin;
            self.render_block_controls(&header, y, context);
            self.current_y = y + header.height;
        }
    }

    fn emit_page_footer(&mut self, context: &BTreeMap<String, String>) {
        if let Some(footer) = self.report.page_footer().cloned() {
            let y = self.report.page_setup.height
                - self.report.page_setup.bottom_margin
                - footer.height;
            self.render_block_controls(&footer, y, context);
        }
    }

    fn render_block_controls(
        &mut self,
        pb: &PrintBlock,
        base_y: u32,
        context: &BTreeMap<String, String>,
    ) {
        for c in &pb.controls {
            let item_x = self.report.page_setup.left_margin + c.x.unwrap_or(0);
            let item_y = base_y + c.y.unwrap_or(0);

            let text = match c.kind {
                ReportControlKind::TextBlock => c.caption.clone().unwrap_or_default(),
                ReportControlKind::Attribute | ReportControlKind::Variable => {
                    let binding = c.binding.as_deref().unwrap_or(&c.name);
                    resolve_binding(binding, self.current_page, context)
                }
                ReportControlKind::Line
                | ReportControlKind::Rectangle
                | ReportControlKind::Image => String::new(),
            };

            self.current_items.push(RenderedItem {
                control_name: c.name.clone(),
                kind: c.kind,
                text,
                x: item_x,
                y: item_y,
                width: c.width,
                height: c.height,
                font_size: c.font_size,
                font_bold: c.font_bold,
            });
        }
    }
}

fn resolve_binding(binding: &str, current_page: u32, context: &BTreeMap<String, String>) -> String {
    let norm = binding.trim();
    if norm.eq_ignore_ascii_case("&page") || norm.eq_ignore_ascii_case("&pagenumber") {
        return current_page.to_string();
    }
    if let Some(val) = context.get(norm) {
        return val.clone();
    }
    let without_amp = norm.strip_prefix('&').unwrap_or(norm);
    if let Some(val) = context.get(without_amp) {
        return val.clone();
    }
    // Return empty string if unbound
    String::new()
}
