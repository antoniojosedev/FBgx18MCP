//! Independent experimental compiler for an explicitly admitted portable subset.
//! This crate neither opens native Knowledge Bases nor loads the GeneXus SDK.

pub mod backend;
pub mod build;
pub mod catalog;
pub mod date;
pub mod datetime;
mod dependencies;
pub mod diagnostics;
pub mod exchange;
pub mod frontend;
pub mod incremental;
pub mod kb;
pub mod model;
pub mod record_runtime;
mod rules;
pub mod structured;
pub mod text;
pub mod transaction;
mod typed_backend;
pub mod types;

pub fn compile(project: &model::Project) -> Result<String, String> {
    if project.format_version == 2 {
        return typed_backend::emit(project);
    }
    let procedures = project.parse()?;
    backend::emit(&procedures, &project.entry.to_ascii_uppercase())
}
