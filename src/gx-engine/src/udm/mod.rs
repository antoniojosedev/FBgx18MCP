//! Unified Data Model (UDM) physical storage, envelope decoding, and token extraction.
//! Implements direct reading of GeneXus physical knowledge base metadata and source parts
//! without relying on the COM STA SDK worker.

#![forbid(unsafe_code)]

pub mod client;
pub mod connection;
pub mod decoder;
pub mod live;
pub mod mock;
pub mod reader;
pub mod row;
pub mod writer;

pub use client::{
    EntityTypeRecord, EntityVersionCompositionRecord, EntityVersionRecord, InMemoryDatabase,
    ModelCrossReferenceRecord, ModelEntityVersionRecord, StateLockRecord, UdmClient, UdmRow,
    UdmValue,
};
pub use connection::{AuthMode, KbConnectionInfo};
pub use decoder::{
    FLAG_GZIP, FLAG_UNCOMPRESSED_UTF8, HEADER_LEN, MAGIC, UdmPartKind, VERSION_V1,
    build_token_data_list, decode_blob, encode_blob, escape_xml, extract_token_data_list,
    unescape_xml,
};
pub use reader::{
    DEFAULT_DESIGN_MODEL_ID, UdmReader, map_entity_type_id_to_kind, map_kind_to_entity_type_id,
};
pub use writer::{UdmObjectIdentity, UdmWriter};
