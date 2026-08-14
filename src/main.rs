use anyhow::{Context, Result};
use clap::Parser;
use rsat2pw::codegen::{self, OnUnsupported};
use rsat2pw::params::{self, Case, Cases};
use rsat2pw::{lower, recording, report};
use std::path::PathBuf;

/// Convert a D365 F&O Task Recorder recording into a Playwright test.
#[derive(Parser, Debug)]
#[command(name = "rsat2pw", version, about, long_about = None)]
struct Args {
    /// Task Recorder recording: an .axtr archive or a raw recording .xml.
    input: PathBuf,

    /// Directory to write the generated spec and test data into.
    #[arg(short, long, default_value = "tests")]
    out_dir: PathBuf,

    /// RSAT parameter workbook (.xlsx) supplying the test data.
    #[arg(short, long)]
    params: Option<PathBuf>,

    /// Worksheet within the parameter workbook (defaults to the first).
    #[arg(long)]
    sheet: Option<String>,

    /// What to do with Task Recorder actions we cannot map.
    #[arg(long, default_value = "annotate", value_parser = ["annotate", "fail", "comment"])]
    on_unsupported: String,

    /// Also write the D365 Playwright runtime helper next to the tests.
    #[arg(long)]
    emit_runtime: bool,

    /// Where to write the conversion report. Defaults to
    /// `<out-dir>/<Name>.report.md`; give a `.json` path to emit JSON instead.
    #[arg(long)]
    report: Option<PathBuf>,

    /// Skip the conversion report.
    #[arg(long, conflicts_with = "report")]
    no_report: bool,

    /// Print what would be generated without writing anything.
    #[arg(long)]
    dry_run: bool,
}

fn main() -> Result<()> {
    let args = Args::parse();

    let recording = recording::load(&args.input)?;
    let test_case = lower::lower(&recording);

    let mut cases: Cases = match &args.params {
        Some(path) => params::from_workbook(path, args.sheet.as_deref(), &test_case)?,
        None => params::from_recording(&test_case),
    };

    // Always emit at least one case, or the generated `for` loop runs zero
    // times and the suite silently passes with no tests.
    if cases.rows.is_empty() {
        cases.rows.push(Case {
            label: "default".to_string(),
            values: Default::default(),
        });
    }

    let on_unsupported = OnUnsupported::parse(&args.on_unsupported).unwrap_or(OnUnsupported::Annotate);
    let output = codegen::generate(&test_case, &cases, on_unsupported)?;

    let spec_path = args.out_dir.join(format!("{}.spec.ts", output.stem));
    let data_path = args.out_dir.join(format!("{}.data.ts", output.stem));

    let report = report::build(&test_case, &cases);
    print_summary(&report);

    if args.dry_run {
        println!("{}", output.spec);
        return Ok(());
    }

    std::fs::create_dir_all(&args.out_dir)
        .with_context(|| format!("creating {}", args.out_dir.display()))?;
    std::fs::write(&spec_path, &output.spec)?;
    std::fs::write(&data_path, &output.data)?;
    eprintln!("wrote     : {}", spec_path.display());
    eprintln!("wrote     : {}", data_path.display());

    if args.emit_runtime {
        let runtime_dir = args
            .out_dir
            .parent()
            .unwrap_or_else(|| std::path::Path::new("."))
            .join("runtime");
        std::fs::create_dir_all(&runtime_dir)?;
        let runtime_path = runtime_dir.join("d365.ts");
        std::fs::write(&runtime_path, rsat2pw::RUNTIME_D365_TS)?;
        eprintln!("wrote     : {}", runtime_path.display());
    }

    if !args.no_report {
        let report_path = args
            .report
            .clone()
            .unwrap_or_else(|| args.out_dir.join(format!("{}.report.md", output.stem)));

        // A `.json` target emits the machine-readable form; anything else is
        // the Markdown report meant to be read.
        let body = if report_path.extension().is_some_and(|e| e == "json") {
            report.to_json()?
        } else {
            report.to_markdown()
        };

        if let Some(parent) = report_path.parent().filter(|p| !p.as_os_str().is_empty()) {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(&report_path, body)
            .with_context(|| format!("writing {}", report_path.display()))?;
        eprintln!("wrote     : {}", report_path.display());
    }

    Ok(())
}

/// The console view of the same report: enough to see coverage at a glance
/// without opening a file, which is what `--dry-run` relies on.
fn print_summary(report: &report::Report) {
    let c = &report.coverage;

    eprintln!("recording : {}", report.recording);
    eprintln!(
        "actions   : {} ({} translated, {} not - {:.1}%)",
        c.actions,
        c.translated,
        c.not_translated,
        c.percent()
    );
    eprintln!(
        "test data : {} case(s) from {}",
        report.test_cases, report.test_data_source
    );

    if !report.translated_by_op.is_empty() {
        eprintln!("\ntranslated:");
        for (op, n) in &report.translated_by_op {
            eprintln!("  {op:<14} {n:>3}");
        }
    }

    if report.not_translated.is_empty() {
        eprintln!("\nnot translated: none - full coverage");
    } else {
        eprintln!("\nnot translated:");
        for kind in &report.not_translated {
            eprintln!("  {:<30} {:>3}", kind.raw_kind, kind.count);
        }
        eprintln!("  <-- add rules for these in src/lower.rs; the report lists their properties");
    }
}
