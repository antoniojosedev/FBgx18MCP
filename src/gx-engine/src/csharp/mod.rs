//! Native C# (.NET Core / Framework) code generator for GeneXus.
//!
//! Track D of Plan 130:
//! - Generates `GXProcedure` C# class equivalent to GeneXus IDE output
//! - Maps GeneXus typed variables and expressions to valid C# constructs
//! - Emits .NET Core `net8.0`/`net9.0` `.csproj` project files

#![forbid(unsafe_code)]

pub mod emitter;
pub mod procedure;
pub mod project;
pub mod sdt;
pub mod solution;
pub mod transaction;
pub mod types;
pub mod webpanel;

pub use emitter::CSharpEmitter;
pub use procedure::{CsharpParam, ProcedureClass, STANDARD_IMPORTS};
pub use project::{CsprojConfig, CsprojEmitter, OutputType};
pub use sdt::SdtClass;
pub use solution::{SolutionConfig, SolutionEmitter, SolutionProject, deterministic_guid};
pub use transaction::{TRANSACTION_IMPORTS, TransactionClass};
pub use types::{
    clean_ident, to_csharp_attr_name, to_csharp_default_value, to_csharp_param_name,
    to_csharp_param_signature, to_csharp_type, to_csharp_var_name,
};
pub use webpanel::{WEBPANEL_IMPORTS, WebPanelClass};
