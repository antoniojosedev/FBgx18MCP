//! Output channels and streaming buffer engine (Row G11 of Plan 129).
//!
//! Provides structured routing, buffering, and streaming partitioning for
//! GeneXus runtime output channels (`Print`, `msg(..., status)`, `Log`, and web output buffers).

use serde::{Deserialize, Serialize};
use std::fmt;
use std::time::{SystemTime, UNIX_EPOCH};

/// Distinct output channels supported by the runtime engine.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum OutputChannelKind {
    /// Console output (standard output / `Print` statement in command-line context).
    Console,
    /// Buffered web response output (HTTP body stream / web panels).
    WebBuffer,
    /// Diagnostic and runtime execution logs.
    Log,
    /// Status line / progress channel (`msg(&text, status)`).
    StatusLine,
}

impl fmt::Display for OutputChannelKind {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Console => write!(f, "console"),
            Self::WebBuffer => write!(f, "web_buffer"),
            Self::Log => write!(f, "log"),
            Self::StatusLine => write!(f, "status_line"),
        }
    }
}

/// An individual message recorded in an output channel.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct OutputMessage {
    /// Target channel destination.
    pub channel: OutputChannelKind,
    /// Text payload of the message.
    pub text: String,
    /// Optional status code (e.g., from `msg(&text, status)`).
    pub status_code: Option<i32>,
    /// Milliseconds since Unix epoch when the message was recorded.
    pub timestamp_millis: u64,
}

impl OutputMessage {
    /// Create a new output message with explicit fields.
    pub fn new(
        channel: OutputChannelKind,
        text: impl Into<String>,
        status_code: Option<i32>,
        timestamp_millis: u64,
    ) -> Self {
        Self {
            channel,
            text: text.into(),
            status_code,
            timestamp_millis,
        }
    }
}

/// Current system time in milliseconds since UNIX epoch.
fn current_timestamp_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// In-memory buffer collecting and managing multi-channel procedure/runtime outputs.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct OutputBuffer {
    /// Ordered sequence of emitted messages across all channels.
    pub messages: Vec<OutputMessage>,
}

impl OutputBuffer {
    /// Create a new, empty output buffer.
    pub fn new() -> Self {
        Self {
            messages: Vec::new(),
        }
    }

    /// Record a message to the console channel.
    pub fn write_console(&mut self, text: impl Into<String>) {
        self.messages.push(OutputMessage {
            channel: OutputChannelKind::Console,
            text: text.into(),
            status_code: None,
            timestamp_millis: current_timestamp_millis(),
        });
    }

    /// Record a message to the status line channel with an associated status code.
    pub fn write_status(&mut self, text: impl Into<String>, status_code: i32) {
        self.messages.push(OutputMessage {
            channel: OutputChannelKind::StatusLine,
            text: text.into(),
            status_code: Some(status_code),
            timestamp_millis: current_timestamp_millis(),
        });
    }

    /// Record a message to the web buffer channel.
    pub fn write_web(&mut self, text: impl Into<String>) {
        self.messages.push(OutputMessage {
            channel: OutputChannelKind::WebBuffer,
            text: text.into(),
            status_code: None,
            timestamp_millis: current_timestamp_millis(),
        });
    }

    /// Record a message to the log channel.
    pub fn write_log(&mut self, text: impl Into<String>) {
        self.messages.push(OutputMessage {
            channel: OutputChannelKind::Log,
            text: text.into(),
            status_code: None,
            timestamp_millis: current_timestamp_millis(),
        });
    }

    /// Append a pre-constructed message directly to the buffer.
    pub fn push_message(&mut self, msg: OutputMessage) {
        self.messages.push(msg);
    }

    /// Append a message with an explicit timestamp.
    pub fn write_with_timestamp(
        &mut self,
        channel: OutputChannelKind,
        text: impl Into<String>,
        status_code: Option<i32>,
        timestamp_millis: u64,
    ) {
        self.messages.push(OutputMessage {
            channel,
            text: text.into(),
            status_code,
            timestamp_millis,
        });
    }

    /// Return the total number of recorded messages across all channels.
    pub fn message_count(&self) -> usize {
        self.messages.len()
    }

    /// Check if the buffer contains no messages.
    pub fn is_empty(&self) -> bool {
        self.messages.is_empty()
    }

    /// Return references to all messages recorded for a specific channel, preserving order.
    pub fn messages_for_channel(&self, channel: OutputChannelKind) -> Vec<&OutputMessage> {
        self.messages
            .iter()
            .filter(|m| m.channel == channel)
            .collect()
    }

    /// Concatenate all console text messages separated by newlines.
    pub fn flush_console_to_string(&self) -> String {
        self.messages
            .iter()
            .filter(|m| m.channel == OutputChannelKind::Console)
            .map(|m| m.text.as_str())
            .collect::<Vec<_>>()
            .join("\n")
    }

    /// Concatenate all web buffer text messages directly without extra separators.
    pub fn flush_web_to_string(&self) -> String {
        self.messages
            .iter()
            .filter(|m| m.channel == OutputChannelKind::WebBuffer)
            .map(|m| m.text.as_str())
            .collect::<String>()
    }

    /// Return the concatenated text of all messages in the given channel.
    pub fn channel_text(&self, channel: OutputChannelKind) -> String {
        match channel {
            OutputChannelKind::Console => self.flush_console_to_string(),
            OutputChannelKind::WebBuffer => self.flush_web_to_string(),
            OutputChannelKind::Log | OutputChannelKind::StatusLine => self
                .messages_for_channel(channel)
                .into_iter()
                .map(|m| m.text.as_str())
                .collect::<Vec<_>>()
                .join("\n"),
        }
    }

    /// Return a reference to the most recent status line message, if any.
    pub fn latest_status(&self) -> Option<&OutputMessage> {
        self.messages
            .iter()
            .rev()
            .find(|m| m.channel == OutputChannelKind::StatusLine)
    }

    /// Clear all recorded messages from the buffer.
    pub fn clear(&mut self) {
        self.messages.clear();
    }

    /// Serialize the buffer into a `serde_json::Value`.
    pub fn to_json(&self) -> serde_json::Value {
        serde_json::to_value(self).unwrap_or(serde_json::Value::Null)
    }

    /// Deserialize an `OutputBuffer` from a `serde_json::Value`.
    pub fn from_json(val: &serde_json::Value) -> Result<Self, String> {
        serde_json::from_value(val.clone()).map_err(|e| e.to_string())
    }

    /// Split the channel output into bounded streaming chunks for MCP/streaming interfaces.
    ///
    /// Chunks are bounded by `max_chunk_chars` unicode characters and never split
    /// mid-codepoint. If `max_chunk_chars == 0` or the channel output is empty, an empty vector
    /// is returned.
    pub fn render_streaming_chunks(
        &self,
        channel: OutputChannelKind,
        max_chunk_chars: usize,
    ) -> Vec<String> {
        if max_chunk_chars == 0 {
            return Vec::new();
        }
        let text = self.channel_text(channel);
        if text.is_empty() {
            return Vec::new();
        }

        let mut chunks = Vec::new();
        let mut current = String::with_capacity(max_chunk_chars.min(text.len()));
        let mut char_count = 0;

        for ch in text.chars() {
            current.push(ch);
            char_count += 1;
            if char_count == max_chunk_chars {
                chunks.push(current);
                current = String::with_capacity(max_chunk_chars);
                char_count = 0;
            }
        }

        if !current.is_empty() {
            chunks.push(current);
        }

        chunks
    }
}
