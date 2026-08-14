//! rsat2pw - convert D365 F&O Task Recorder / RSAT recordings into Playwright.
//!
//! Pipeline:
//!   `.axtr` (zip)  ->  tolerant XML tree  ->  RecNode  ->  action IR  ->  TypeScript
//!                              xml.rs      recording.rs   lower.rs      codegen.rs
//!
//! RSAT parameter workbooks are read separately (`params.rs`) and become
//! data-driven Playwright fixtures rather than literals in the spec.

pub mod codegen;
pub mod ir;
pub mod lower;
pub mod params;
pub mod recording;
pub mod report;
pub mod xml;

/// The hand-written Playwright runtime the generated specs call into.
pub const RUNTIME_D365_TS: &str = include_str!("../runtime/d365.ts");
