use gx_engine::output_channel::{OutputBuffer, OutputChannelKind, OutputMessage};
use serde_json::json;

#[test]
fn test_output_buffer_initial_empty() {
    let buf = OutputBuffer::new();
    assert_eq!(buf.message_count(), 0);
    assert!(buf.is_empty());
    assert!(buf.messages.is_empty());
    assert_eq!(buf.flush_console_to_string(), "");
    assert_eq!(buf.flush_web_to_string(), "");
    assert_eq!(buf.channel_text(OutputChannelKind::Console), "");
    assert_eq!(buf.channel_text(OutputChannelKind::WebBuffer), "");
    assert_eq!(buf.channel_text(OutputChannelKind::Log), "");
    assert_eq!(buf.channel_text(OutputChannelKind::StatusLine), "");
    assert!(buf.latest_status().is_none());
    assert!(
        buf.messages_for_channel(OutputChannelKind::Console)
            .is_empty()
    );
    assert!(
        buf.messages_for_channel(OutputChannelKind::WebBuffer)
            .is_empty()
    );
    assert!(buf.messages_for_channel(OutputChannelKind::Log).is_empty());
    assert!(
        buf.messages_for_channel(OutputChannelKind::StatusLine)
            .is_empty()
    );
    assert!(
        buf.render_streaming_chunks(OutputChannelKind::Console, 10)
            .is_empty()
    );

    // Default trait creates identical empty buffer
    let def_buf = OutputBuffer::default();
    assert_eq!(buf, def_buf);

    // Test OutputMessage::new and push_message
    let mut custom_buf = OutputBuffer::new();
    let msg = OutputMessage::new(OutputChannelKind::Console, "manual entry", None, 1000);
    assert_eq!(msg.channel, OutputChannelKind::Console);
    assert_eq!(msg.text, "manual entry");
    assert_eq!(msg.status_code, None);
    assert_eq!(msg.timestamp_millis, 1000);
    custom_buf.push_message(msg);
    assert_eq!(custom_buf.message_count(), 1);
    assert_eq!(custom_buf.messages[0].text, "manual entry");
}

#[test]
fn test_write_console_and_flush_string() {
    let mut buf = OutputBuffer::new();
    buf.write_console("Starting procedure execution...");
    buf.write_console("Row 1: Customer A");
    buf.write_console("Row 2: Customer B");

    assert_eq!(buf.message_count(), 3);
    assert!(!buf.is_empty());

    let msgs = buf.messages_for_channel(OutputChannelKind::Console);
    assert_eq!(msgs.len(), 3);
    for m in &msgs {
        assert_eq!(m.channel, OutputChannelKind::Console);
        assert_eq!(m.status_code, None);
        assert!(m.timestamp_millis > 0);
    }
    assert_eq!(msgs[0].text, "Starting procedure execution...");
    assert_eq!(msgs[1].text, "Row 1: Customer A");
    assert_eq!(msgs[2].text, "Row 2: Customer B");

    let flushed = buf.flush_console_to_string();
    assert_eq!(
        flushed,
        "Starting procedure execution...\nRow 1: Customer A\nRow 2: Customer B"
    );

    // Multiple flushes are idempotent and non-destructive
    assert_eq!(buf.flush_console_to_string(), flushed);
    assert_eq!(buf.message_count(), 3);
}

#[test]
fn test_write_status_with_status_code_and_latest_status() {
    let mut buf = OutputBuffer::new();
    assert!(buf.latest_status().is_none());

    buf.write_status("Step 1: Connecting to database", 0);
    assert_eq!(
        buf.latest_status()
            .map(|m| (m.text.as_str(), m.status_code)),
        Some(("Step 1: Connecting to database", Some(0)))
    );

    buf.write_status("Step 2: Table lock acquired", 100);
    buf.write_status("Step 3: Index update warning", 301);
    buf.write_status("Step 4: Migration finished with warning", 302);

    assert_eq!(buf.message_count(), 4);

    let statuses = buf.messages_for_channel(OutputChannelKind::StatusLine);
    assert_eq!(statuses.len(), 4);
    assert_eq!(statuses[0].status_code, Some(0));
    assert_eq!(statuses[1].status_code, Some(100));
    assert_eq!(statuses[2].status_code, Some(301));
    assert_eq!(statuses[3].status_code, Some(302));

    let latest = buf.latest_status().expect("must have latest status");
    assert_eq!(latest.text, "Step 4: Migration finished with warning");
    assert_eq!(latest.status_code, Some(302));
    assert_eq!(latest.channel, OutputChannelKind::StatusLine);
}

#[test]
fn test_write_web_buffer_and_flush() {
    let mut buf = OutputBuffer::new();
    buf.write_web("<div class=\"gx-grid\">");
    buf.write_web("<span>Item 1</span>");
    buf.write_web("<span>Item 2</span>");
    buf.write_web("</div>");

    assert_eq!(buf.message_count(), 4);

    let web_msgs = buf.messages_for_channel(OutputChannelKind::WebBuffer);
    assert_eq!(web_msgs.len(), 4);
    for m in &web_msgs {
        assert_eq!(m.channel, OutputChannelKind::WebBuffer);
        assert_eq!(m.status_code, None);
    }

    let web_html = buf.flush_web_to_string();
    assert_eq!(
        web_html,
        "<div class=\"gx-grid\"><span>Item 1</span><span>Item 2</span></div>"
    );

    // Other channels remain completely empty
    assert_eq!(buf.flush_console_to_string(), "");
    assert!(buf.latest_status().is_none());
}

#[test]
fn test_channel_filtering_distinct_messages() {
    let mut buf = OutputBuffer::new();

    buf.write_console("Console msg 1");
    buf.write_console("Console msg 2");

    buf.write_web("<html/>");
    buf.write_web("<body/>");
    buf.write_web("<footer/>");

    buf.write_log("DEBUG: worker initialized");
    buf.write_log("INFO: starting pipeline");
    buf.write_log("WARN: cache miss");
    buf.write_log("INFO: pipeline complete");

    buf.write_status("Batch active", 10);

    assert_eq!(buf.message_count(), 10);

    let console_msgs = buf.messages_for_channel(OutputChannelKind::Console);
    let web_msgs = buf.messages_for_channel(OutputChannelKind::WebBuffer);
    let log_msgs = buf.messages_for_channel(OutputChannelKind::Log);
    let status_msgs = buf.messages_for_channel(OutputChannelKind::StatusLine);

    assert_eq!(console_msgs.len(), 2);
    assert_eq!(web_msgs.len(), 3);
    assert_eq!(log_msgs.len(), 4);
    assert_eq!(status_msgs.len(), 1);

    // Sum of filtered channel counts matches total message count
    assert_eq!(
        console_msgs.len() + web_msgs.len() + log_msgs.len() + status_msgs.len(),
        buf.message_count()
    );

    assert_eq!(
        console_msgs
            .into_iter()
            .map(|m| m.text.as_str())
            .collect::<Vec<_>>(),
        vec!["Console msg 1", "Console msg 2"]
    );
    assert_eq!(
        log_msgs
            .into_iter()
            .map(|m| m.text.as_str())
            .collect::<Vec<_>>(),
        vec![
            "DEBUG: worker initialized",
            "INFO: starting pipeline",
            "WARN: cache miss",
            "INFO: pipeline complete"
        ]
    );
}

#[test]
fn test_interleaved_channel_ordering_preserved() {
    let mut buf = OutputBuffer::new();

    buf.write_console("Console 1");
    buf.write_log("Log 1");
    buf.write_status("Status 1", 10);
    buf.write_web("Web 1");
    buf.write_console("Console 2");
    buf.write_status("Status 2", 20);
    buf.write_web("Web 2");
    buf.write_log("Log 2");

    assert_eq!(buf.message_count(), 8);

    // Global order across all channels is strictly preserved
    let global_sequence: Vec<(&str, OutputChannelKind)> = buf
        .messages
        .iter()
        .map(|m| (m.text.as_str(), m.channel))
        .collect();

    assert_eq!(
        global_sequence,
        vec![
            ("Console 1", OutputChannelKind::Console),
            ("Log 1", OutputChannelKind::Log),
            ("Status 1", OutputChannelKind::StatusLine),
            ("Web 1", OutputChannelKind::WebBuffer),
            ("Console 2", OutputChannelKind::Console),
            ("Status 2", OutputChannelKind::StatusLine),
            ("Web 2", OutputChannelKind::WebBuffer),
            ("Log 2", OutputChannelKind::Log),
        ]
    );

    // Timestamps are monotonically non-decreasing
    for i in 1..buf.messages.len() {
        assert!(
            buf.messages[i].timestamp_millis >= buf.messages[i - 1].timestamp_millis,
            "timestamp at index {} ({}) should be >= previous ({})",
            i,
            buf.messages[i].timestamp_millis,
            buf.messages[i - 1].timestamp_millis
        );
    }

    // Channel-filtered queries maintain relative channel order
    let console_texts: Vec<&str> = buf
        .messages_for_channel(OutputChannelKind::Console)
        .into_iter()
        .map(|m| m.text.as_str())
        .collect();
    assert_eq!(console_texts, vec!["Console 1", "Console 2"]);

    let status_texts: Vec<(&str, Option<i32>)> = buf
        .messages_for_channel(OutputChannelKind::StatusLine)
        .into_iter()
        .map(|m| (m.text.as_str(), m.status_code))
        .collect();
    assert_eq!(
        status_texts,
        vec![("Status 1", Some(10)), ("Status 2", Some(20))]
    );
}

#[test]
fn test_streaming_chunks_bounded_partitioning() {
    let mut buf = OutputBuffer::new();

    // 1. Empty buffer yields empty chunks
    assert!(
        buf.render_streaming_chunks(OutputChannelKind::Console, 10)
            .is_empty()
    );

    // 2. max_chunk_chars == 0 yields empty chunks
    buf.write_console("Sample text for streaming");
    assert!(
        buf.render_streaming_chunks(OutputChannelKind::Console, 0)
            .is_empty()
    );

    // 3. Exact multiple chunk boundary: 20 chars split by 5 chars = exactly 4 chunks
    buf.clear();
    buf.write_console("0123456789ABCDEFGHIJ");
    let chunks_exact = buf.render_streaming_chunks(OutputChannelKind::Console, 5);
    assert_eq!(chunks_exact.len(), 4);
    assert_eq!(chunks_exact, vec!["01234", "56789", "ABCDE", "FGHIJ"]);
    assert_eq!(chunks_exact.concat(), "0123456789ABCDEFGHIJ");

    // 4. Non-multiple chunk boundary: 20 chars split by 7 chars = 3 chunks (7, 7, 6)
    let chunks_uneven = buf.render_streaming_chunks(OutputChannelKind::Console, 7);
    assert_eq!(chunks_uneven.len(), 3);
    assert_eq!(chunks_uneven, vec!["0123456", "789ABCD", "EFGHIJ"]);
    assert_eq!(chunks_uneven.concat(), "0123456789ABCDEFGHIJ");

    // 5. Oversized chunk: chunk size larger than content yields 1 chunk
    let chunks_large = buf.render_streaming_chunks(OutputChannelKind::Console, 100);
    assert_eq!(chunks_large.len(), 1);
    assert_eq!(chunks_large[0], "0123456789ABCDEFGHIJ");

    // 6. Single-character chunks
    let chunks_single = buf.render_streaming_chunks(OutputChannelKind::Console, 1);
    assert_eq!(chunks_single.len(), 20);
    assert_eq!(chunks_single[0], "0");
    assert_eq!(chunks_single[19], "J");
    assert_eq!(chunks_single.concat(), "0123456789ABCDEFGHIJ");

    // 7. Unicode multi-byte characters and emoji (accents, UTF-8 boundaries)
    buf.clear();
    let unicode_text = "GeneXus 🚀: Operação concluída com sucesso! É super rápido.";
    buf.write_web(unicode_text);

    let unicode_chunks = buf.render_streaming_chunks(OutputChannelKind::WebBuffer, 12);
    for chunk in &unicode_chunks {
        assert!(
            chunk.chars().count() <= 12,
            "Chunk exceeded 12 characters: '{chunk}'"
        );
    }
    assert_eq!(
        unicode_chunks.concat(),
        unicode_text,
        "Reassembled unicode chunks must match original"
    );
}

#[test]
fn test_buffer_clear_and_reuse() {
    let mut buf = OutputBuffer::new();

    buf.write_console("Console before clear");
    buf.write_status("Status before clear", 1);
    buf.write_web("<span>Web before clear</span>");
    buf.write_log("Log before clear");

    assert_eq!(buf.message_count(), 4);
    assert!(!buf.is_empty());
    assert!(buf.latest_status().is_some());

    buf.clear();

    assert_eq!(buf.message_count(), 0);
    assert!(buf.is_empty());
    assert!(buf.messages.is_empty());
    assert!(buf.latest_status().is_none());
    assert_eq!(buf.flush_console_to_string(), "");
    assert_eq!(buf.flush_web_to_string(), "");
    assert!(
        buf.messages_for_channel(OutputChannelKind::Console)
            .is_empty()
    );

    // Buffer is fully functional after clear
    buf.write_console("Fresh run line 1");
    buf.write_console("Fresh run line 2");
    buf.write_status("Fresh run status", 42);

    assert_eq!(buf.message_count(), 3);
    assert_eq!(
        buf.flush_console_to_string(),
        "Fresh run line 1\nFresh run line 2"
    );
    assert_eq!(
        buf.latest_status()
            .map(|m| (m.text.as_str(), m.status_code)),
        Some(("Fresh run status", Some(42)))
    );
}

#[test]
fn test_json_roundtrip_serialization() {
    let mut original = OutputBuffer::new();
    original.write_with_timestamp(
        OutputChannelKind::Console,
        "Report Title",
        None,
        1700000000000,
    );
    original.write_with_timestamp(
        OutputChannelKind::StatusLine,
        "Processing record 10 of 100",
        Some(10),
        1700000001000,
    );
    original.write_with_timestamp(
        OutputChannelKind::WebBuffer,
        "<b>Web Title</b>",
        None,
        1700000002000,
    );
    original.write_with_timestamp(
        OutputChannelKind::Log,
        "Finished step 1",
        None,
        1700000003000,
    );

    let val = original.to_json();
    assert!(val["messages"].is_array());
    assert_eq!(val["messages"].as_array().unwrap().len(), 4);
    assert_eq!(val["messages"][0]["channel"], "console");
    assert_eq!(val["messages"][0]["text"], "Report Title");
    assert!(val["messages"][0]["status_code"].is_null());
    assert_eq!(val["messages"][0]["timestamp_millis"], 1700000000000_u64);

    assert_eq!(val["messages"][1]["channel"], "status_line");
    assert_eq!(val["messages"][1]["status_code"], 10);

    let restored = OutputBuffer::from_json(&val).expect("valid JSON must deserialize");
    assert_eq!(restored, original);

    // Invalid JSON schema returns Err(String)
    let invalid = json!({ "messages": "invalid string instead of array" });
    let err = OutputBuffer::from_json(&invalid);
    assert!(err.is_err());
}

#[test]
fn test_simulated_procedure_runtime_output_collection() {
    // Simulates a GeneXus Procedure `PExportInvoices` that executes a sequence
    // of `msg(..., status)` status reports and `Print` statements, capturing
    // and routing them through OutputBuffer.
    let mut runtime_buffer = OutputBuffer::new();

    // Procedure initialization
    runtime_buffer.write_status("PExportInvoices starting...", 0);

    // Print Header
    runtime_buffer.write_console("==== INVOICE EXPORT REPORT ====");
    runtime_buffer.write_console("InvoiceNo | Customer             | Amount");
    runtime_buffer.write_console("----------+----------------------+---------");

    // Loop through 5 simulated invoice records
    let invoices = [
        (1001, "Acme Corporation", 1500.50),
        (1002, "Globex Industries", 2730.00),
        (1003, "Soylent Logistics", 890.25),
        (1004, "Initech Systems", 4120.80),
        (1005, "Umbrella Pharmaceuticals", 9550.00),
    ];

    for (id, customer, amount) in invoices {
        // Status progress report: msg(..., status)
        runtime_buffer.write_status(format!("Processing invoice #{id} ({customer})"), 0);

        // Print Line
        runtime_buffer.write_console(format!("INV-{id}  | {customer:<20} | ${amount:>7.2}"));

        // Logging special business conditions
        if amount > 5000.0 {
            runtime_buffer.write_log(format!("Audit note: High-value invoice {id} flagged"));
        }
    }

    // Mid-execution warning: msg("Warning: Tax table cache stale", status)
    runtime_buffer.write_status("Warning: Tax table cache stale", 1);

    // Print Footer
    runtime_buffer.write_console("----------+----------------------+---------");
    runtime_buffer.write_console("Total Invoices: 5 | Total Amount: $18791.55");
    runtime_buffer.write_console("==== END OF REPORT ====");

    // Final status notification: msg("PExportInvoices completed", status)
    runtime_buffer.write_status("PExportInvoices completed", 0);

    // Total emitted messages:
    // 1 start status + 5 loop statuses + 1 warning status + 1 final status = 8 status messages
    // 3 header lines + 5 invoice lines + 3 footer lines = 11 console lines
    // 1 audit log message = 1 log message
    // 0 web messages
    // Total = 20 messages
    assert_eq!(runtime_buffer.message_count(), 20);

    let status_msgs = runtime_buffer.messages_for_channel(OutputChannelKind::StatusLine);
    assert_eq!(status_msgs.len(), 8);

    // Verify latest status reflects final completion
    let latest = runtime_buffer
        .latest_status()
        .expect("must have final status");
    assert_eq!(latest.text, "PExportInvoices completed");
    assert_eq!(latest.status_code, Some(0));

    // Verify warning status was captured with status_code == Some(1)
    let warning = status_msgs
        .iter()
        .find(|m| m.text == "Warning: Tax table cache stale")
        .expect("warning status must exist");
    assert_eq!(warning.status_code, Some(1));

    // Verify Console output formatting
    let console_output = runtime_buffer.flush_console_to_string();
    assert!(console_output.starts_with("==== INVOICE EXPORT REPORT ===="));
    assert!(console_output.contains("INV-1001  | Acme Corporation     | $1500.50"));
    assert!(console_output.contains("INV-1005  | Umbrella Pharmaceuticals | $9550.00"));
    assert!(console_output.ends_with("==== END OF REPORT ===="));

    // Verify Log output
    let log_msgs = runtime_buffer.messages_for_channel(OutputChannelKind::Log);
    assert_eq!(log_msgs.len(), 1);
    assert_eq!(
        log_msgs[0].text,
        "Audit note: High-value invoice 1005 flagged"
    );

    // Verify Streaming Chunks of the Console output
    let chunks = runtime_buffer.render_streaming_chunks(OutputChannelKind::Console, 60);
    assert!(!chunks.is_empty());
    for chunk in &chunks {
        assert!(chunk.chars().count() <= 60);
    }
    assert_eq!(chunks.concat(), console_output);
}
