//! Conversion reporting: what the converter understood, and what it did not.
//!
//! The honest-gaps principle only pays off if the gaps are legible. This turns
//! a lowered `TestCase` into a coverage report whose "not translated" section
//! is the actual worklist for extending the mapping table in `lower.rs` — it
//! carries every property the recorder supplied for each unmapped node, which
//! is what you need to write the new rule.

use crate::ir::{Action, TestCase};
use crate::params::Cases;
use serde::Serialize;
use std::collections::BTreeMap;

#[derive(Debug, Clone, Serialize)]
pub struct Coverage {
    pub actions: usize,
    pub translated: usize,
    /// Understood, and deliberately not replayed. Counted apart from
    /// `translated` so the headline percentage cannot be inflated by deciding
    /// that more and more of the recording does not matter.
    pub skipped: usize,
    pub not_translated: usize,
}

impl Coverage {
    /// Percentage of actions that reached the runtime. Zero actions counts as
    /// fully covered rather than 0/0 — an empty recording has no gaps.
    pub fn percent(&self) -> f64 {
        if self.actions == 0 {
            100.0
        } else {
            (self.translated as f64 / self.actions as f64) * 100.0
        }
    }
}

/// One recorder action type we could not map, with everything we saw on it.
#[derive(Debug, Clone, Serialize)]
pub struct UnmappedKind {
    pub raw_kind: String,
    pub count: usize,
    /// Union of every property key seen across occurrences of this kind, each
    /// with one example value. This is the mapping-table worklist.
    pub props: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct VariableUse {
    pub name: String,
    pub default: String,
    /// Whether any action actually reads this variable.
    pub referenced: bool,
    /// Whether the test-data source supplies a column for it.
    pub in_test_data: bool,
}

/// One line of the ordered translation outline.
#[derive(Debug, Clone, Serialize)]
pub struct OutlineEntry {
    pub depth: usize,
    pub op: String,
    pub detail: String,
    pub translated: bool,
    pub skipped: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct Report {
    pub recording: String,
    pub coverage: Coverage,
    /// Emitted runtime call -> how many times, for the actions we did map.
    pub translated_by_op: BTreeMap<String, usize>,
    pub skipped_kinds: Vec<UnmappedKind>,
    pub not_translated: Vec<UnmappedKind>,
    pub variables: Vec<VariableUse>,
    pub test_cases: usize,
    pub test_data_source: String,
    pub outline: Vec<OutlineEntry>,
}

pub fn build(case: &TestCase, cases: &Cases) -> Report {
    let mut translated_by_op: BTreeMap<String, usize> = BTreeMap::new();
    let mut unmapped: BTreeMap<String, UnmappedKind> = BTreeMap::new();
    let mut skipped: BTreeMap<String, UnmappedKind> = BTreeMap::new();
    let mut outline = Vec::new();

    walk(
        &case.actions,
        0,
        &mut translated_by_op,
        &mut unmapped,
        &mut skipped,
        &mut outline,
    );

    let not_translated: usize = unmapped.values().map(|u| u.count).sum();
    let skipped_count: usize = skipped.values().map(|u| u.count).sum();
    let actions = case.action_count();

    let referenced = referenced_variables(&case.actions);
    let variables = case
        .variables
        .iter()
        .map(|v| VariableUse {
            name: v.name.clone(),
            default: v.default.clone(),
            referenced: referenced.contains(&v.name),
            in_test_data: cases.fields.contains(&v.name),
        })
        .collect();

    Report {
        recording: case.name.clone(),
        coverage: Coverage {
            actions,
            translated: actions
                .saturating_sub(not_translated)
                .saturating_sub(skipped_count),
            skipped: skipped_count,
            not_translated,
        },
        translated_by_op,
        skipped_kinds: skipped.into_values().collect(),
        not_translated: unmapped.into_values().collect(),
        variables,
        test_cases: cases.rows.len(),
        test_data_source: cases.source.clone(),
        outline,
    }
}

fn walk(
    actions: &[Action],
    depth: usize,
    by_op: &mut BTreeMap<String, usize>,
    unmapped: &mut BTreeMap<String, UnmappedKind>,
    skipped: &mut BTreeMap<String, UnmappedKind>,
    outline: &mut Vec<OutlineEntry>,
) {
    for action in actions {
        let is_skipped = matches!(action, Action::Skipped { .. });
        let translated = !matches!(action, Action::Unsupported { .. }) && !is_skipped;

        outline.push(OutlineEntry {
            depth,
            op: action.op_name().to_string(),
            detail: action.summary(),
            translated,
            skipped: is_skipped,
        });

        match action {
            Action::Unsupported {
                raw_kind, props, ..
            } => record(unmapped, raw_kind, props),
            Action::Skipped {
                raw_kind, props, ..
            } => record(skipped, raw_kind, props),
            _ => {
                *by_op.entry(action.op_name().to_string()).or_insert(0) += 1;
            }
        }

        walk(action.children(), depth + 1, by_op, unmapped, skipped, outline);
    }
}

fn record(
    into: &mut BTreeMap<String, UnmappedKind>,
    raw_kind: &str,
    props: &BTreeMap<String, String>,
) {
    let entry = into
        .entry(raw_kind.to_string())
        .or_insert_with(|| UnmappedKind {
            raw_kind: raw_kind.to_string(),
            count: 0,
            props: BTreeMap::new(),
        });
    entry.count += 1;
    for (k, v) in props {
        // First example value wins; later occurrences only widen the key set.
        entry.props.entry(k.clone()).or_insert_with(|| v.clone());
    }
}

fn referenced_variables(actions: &[Action]) -> Vec<String> {
    use crate::ir::Value;
    let mut out = Vec::new();

    fn note(v: &Value, out: &mut Vec<String>) {
        if let Value::Variable { name } = v
            && !out.contains(name)
        {
            out.push(name.clone());
        }
    }

    fn walk(actions: &[Action], out: &mut Vec<String>) {
        for action in actions {
            match action {
                Action::SetValue { value, .. }
                | Action::SetGridValue { value, .. }
                | Action::Filter { value, .. } => note(value, out),
                Action::Validate { expected, .. } => note(expected, out),
                _ => {}
            }
            walk(action.children(), out);
        }
    }

    walk(actions, &mut out);
    out
}

impl Report {
    pub fn to_json(&self) -> serde_json::Result<String> {
        serde_json::to_string_pretty(self)
    }

    pub fn to_markdown(&self) -> String {
        let mut s = String::new();
        let c = &self.coverage;

        s.push_str(&format!("# Conversion report: {}\n\n", self.recording));
        s.push_str("Generated by `rsat2pw`. Regenerate this alongside the spec whenever\n");
        s.push_str("the recording or the mapping table changes.\n\n");

        // -- coverage ---------------------------------------------------
        s.push_str("## Coverage\n\n");
        s.push_str("| | |\n|---|---|\n");
        s.push_str(&format!("| Actions | {} |\n", c.actions));
        s.push_str(&format!(
            "| Translated | {} ({:.1}%) |\n",
            c.translated,
            c.percent()
        ));
        s.push_str(&format!("| Skipped | {} |\n", c.skipped));
        s.push_str(&format!("| Not translated | {} |\n", c.not_translated));
        s.push_str(&format!(
            "| Test cases | {} (from {}) |\n\n",
            self.test_cases, self.test_data_source
        ));

        if c.not_translated == 0 {
            s.push_str("Every recorded action mapped to a runtime call.\n\n");
        }

        // -- translated -------------------------------------------------
        s.push_str("## Translated\n\n");
        if self.translated_by_op.is_empty() {
            s.push_str("_Nothing._\n\n");
        } else {
            s.push_str("| Emitted call | Count |\n|---|---:|\n");
            for (op, n) in &self.translated_by_op {
                s.push_str(&format!("| {} | {n} |\n", render_op(op)));
            }
            s.push('\n');
        }

        // -- skipped ----------------------------------------------------
        if !self.skipped_kinds.is_empty() {
            s.push_str("## Skipped\n\n");
            s.push_str(
                "Recorded, understood, and deliberately not replayed: client-internal \
                 bookkeeping\nwith no user-visible effect. Each one still leaves a comment \
                 in the generated spec.\n\n",
            );
            s.push_str("| Action | Count |\n|---|---:|\n");
            for kind in &self.skipped_kinds {
                s.push_str(&format!("| `{}` | {} |\n", kind.raw_kind, kind.count));
            }
            s.push('\n');
        }

        // -- not translated ---------------------------------------------
        s.push_str("## Not translated\n\n");
        if self.not_translated.is_empty() {
            s.push_str("_Nothing — full coverage._\n\n");
        } else {
            s.push_str(
                "Each heading is a Task Recorder action with no rule in \
                 `src/lower.rs`.\nThe properties are everything the recorder \
                 supplied, which is what a new\nmapping rule keys off.\n\n",
            );
            for kind in &self.not_translated {
                s.push_str(&format!(
                    "### `{}` — {} occurrence{}\n\n",
                    kind.raw_kind,
                    kind.count,
                    if kind.count == 1 { "" } else { "s" }
                ));
                if kind.props.is_empty() {
                    s.push_str("_No properties recorded._\n\n");
                } else {
                    s.push_str("| Property | Example value |\n|---|---|\n");
                    for (k, v) in &kind.props {
                        s.push_str(&format!("| `{}` | {} |\n", k, md_cell(v)));
                    }
                    s.push('\n');
                }
            }
        }

        // -- test data --------------------------------------------------
        s.push_str("## Test data\n\n");
        if self.variables.is_empty() {
            s.push_str("_The recording captured no input values._\n\n");
        } else {
            s.push_str("| Variable | Used by an action | In test data | Recorded default |\n");
            s.push_str("|---|---|---|---|\n");
            for v in &self.variables {
                s.push_str(&format!(
                    "| `{}` | {} | {} | {} |\n",
                    v.name,
                    yes_no(v.referenced),
                    yes_no(v.in_test_data),
                    md_cell(&v.default),
                ));
            }
            s.push('\n');
            if self.variables.iter().any(|v| v.referenced && !v.in_test_data) {
                s.push_str(
                    "> Variables used by an action but absent from the test data fall back to\n\
                     > the recorded default, so the spec still compiles and runs.\n\n",
                );
            }
        }

        // -- outline ----------------------------------------------------
        s.push_str("## Translation outline\n\n");
        s.push_str(
            "The recording in order. `!!` marks an action that was not translated, \
             `~~` one\nthat was deliberately skipped.\n\n",
        );
        s.push_str("```\n");
        for e in &self.outline {
            let marker = if e.skipped {
                "~~ "
            } else if e.translated {
                "   "
            } else {
                "!! "
            };
            let line = format!("{}{}{} {}", marker, "  ".repeat(e.depth), e.op, e.detail);
            s.push_str(line.trim_end());
            s.push('\n');
        }
        s.push_str("```\n");

        s
    }
}

/// How an emitted op is named in the report. Everything reaching the runtime
/// is a `d365.` method; `test.step` comes from Playwright, and a marker is a
/// comment rather than a call at all.
fn render_op(op: &str) -> String {
    match op {
        "marker" => "a comment".to_string(),
        _ if op.contains('.') => format!("`{op}()`"),
        _ => format!("`d365.{op}()`"),
    }
}

fn yes_no(b: bool) -> &'static str {
    if b { "yes" } else { "no" }
}

/// Keep a value from breaking out of a Markdown table cell.
fn md_cell(s: &str) -> String {
    let flat = s.replace(['\r', '\n'], " ").replace('|', "\\|");
    if flat.trim().is_empty() {
        "_(empty)_".to_string()
    } else {
        format!("`{flat}`")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{lower, params, recording};

    fn report_for(xml: &str) -> Report {
        let case = lower::lower(&recording::parse(xml).unwrap());
        let cases = params::from_recording(&case);
        build(&case, &cases)
    }

    fn wrap(nodes: &str) -> String {
        format!(
            r#"<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                 <Name>T</Name><RootScope><Children>{nodes}</Children></RootScope></Recording>"#
        )
    }

    #[test]
    fn counts_translated_skipped_and_unmapped_actions() {
        let r = report_for(include_str!("../fixtures/ConfirmPurchaseOrder.xml"));

        assert_eq!(r.coverage.actions, r.outline.len());
        assert_eq!(
            r.coverage.translated + r.coverage.skipped + r.coverage.not_translated,
            r.coverage.actions
        );
        assert!(r.translated_by_op.contains_key("click"));
        assert!(r.translated_by_op.contains_key("test.step"));
    }

    /// The whole point of the report: an unmapped kind must arrive with the
    /// properties needed to write its mapping rule.
    #[test]
    fn unmapped_kinds_carry_their_full_property_bag() {
        let r = report_for(&wrap(
            r#"<Node i:type="CommandUserAction"><CommandName>SelectForAdd</CommandName>
               <ControlName>Grid</ControlName><ControlType>Grid</ControlType></Node>"#,
        ));

        assert_eq!(r.not_translated.len(), 1);
        let kind = &r.not_translated[0];
        assert_eq!(kind.raw_kind, "CommandUserAction:SelectForAdd");
        assert_eq!(kind.count, 1);
        assert_eq!(
            kind.props.get("ControlName").map(String::as_str),
            Some("Grid")
        );
        assert_eq!(
            kind.props.get("ControlType").map(String::as_str),
            Some("Grid")
        );
    }

    #[test]
    fn repeated_unmapped_kinds_are_grouped_with_a_count() {
        let r = report_for(&wrap(
            r#"<Node i:type="Mystery"><A>1</A></Node>
               <Node i:type="Mystery"><B>2</B></Node>"#,
        ));

        assert_eq!(r.not_translated.len(), 1);
        assert_eq!(r.not_translated[0].count, 2);
        // Keys union across occurrences, so no property is lost to grouping.
        assert!(r.not_translated[0].props.contains_key("A"));
        assert!(r.not_translated[0].props.contains_key("B"));
        assert_eq!(r.coverage.translated, 0);
    }

    /// A skipped action is not a translated one. Counting it as translated
    /// would let the headline number be improved by skipping more.
    #[test]
    fn skipped_actions_get_their_own_bucket() {
        let r = report_for(&wrap(
            r#"<Node i:type="CommandUserAction"><CommandName>GetFilters</CommandName>
               <ControlName>SystemDefinedFilterManager</ControlName></Node>"#,
        ));

        assert_eq!(r.coverage.skipped, 1);
        assert_eq!(r.coverage.translated, 0);
        assert_eq!(r.coverage.not_translated, 0);
        assert_eq!(r.skipped_kinds[0].raw_kind, "CommandUserAction:GetFilters");
        assert!(r.to_markdown().contains("## Skipped"));
    }

    #[test]
    fn flags_variables_that_no_action_uses() {
        let r = report_for(
            r#"<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                 <Name>T</Name>
                 <Variables>
                   <AxTaskRecordingVariable><Name>Used</Name><Value>a</Value></AxTaskRecordingVariable>
                   <AxTaskRecordingVariable><Name>Orphan</Name><Value>b</Value></AxTaskRecordingVariable>
                 </Variables>
                 <RootScope><Children>
                   <Node i:type="PropertyUserAction"><ControlName>C</ControlName>
                     <VariableName>Used</VariableName><Value>a</Value></Node>
                 </Children></RootScope></Recording>"#,
        );

        let used = r.variables.iter().find(|v| v.name == "Used").unwrap();
        let orphan = r.variables.iter().find(|v| v.name == "Orphan").unwrap();
        assert!(used.referenced);
        assert!(!orphan.referenced);
    }

    #[test]
    fn outline_nests_children_and_marks_gaps() {
        let r = report_for(include_str!("../fixtures/ConfirmPurchaseOrder.xml"));

        // Steps sit at depth 0, their contents deeper.
        assert!(r.outline.iter().any(|e| e.op == "test.step" && e.depth == 0));
        assert!(r.outline.iter().any(|e| e.depth >= 2));
        assert_eq!(
            r.outline.iter().filter(|e| !e.translated && !e.skipped).count(),
            r.coverage.not_translated
        );
    }

    #[test]
    fn markdown_and_json_both_render() {
        let r = report_for(include_str!("../fixtures/ConfirmPurchaseOrder.xml"));

        let md = r.to_markdown();
        assert!(md.contains("# Conversion report: Confirm purchase order"));
        assert!(md.contains("## Coverage"));

        let json: serde_json::Value = serde_json::from_str(&r.to_json().unwrap()).unwrap();
        assert_eq!(json["coverage"]["actions"], r.coverage.actions);
    }

    /// Same contract as the spec's golden test: the committed example report
    /// is what the README points readers at, so it must not drift from what
    /// the code produces.
    #[test]
    fn checked_in_example_report_matches_current_output() {
        let case = lower::lower(
            &recording::parse(include_str!("../fixtures/ConfirmPurchaseOrder.xml")).unwrap(),
        );
        // The committed report was generated with the workbook, so its test-data
        // provenance line has to come from there too.
        let cases = params::from_workbook(
            std::path::Path::new("fixtures/ConfirmPurchaseOrder-params.xlsx"),
            None,
            &case,
        )
        .unwrap();

        let generated = build(&case, &cases).to_markdown();
        let committed = include_str!("../tests/ConfirmPurchaseOrder.report.md");

        assert_eq!(
            generated.replace("\r\n", "\n"),
            committed.replace("\r\n", "\n"),
            "regenerate with: cargo run -- fixtures/ConfirmPurchaseOrder.axtr --out-dir tests \
             --params fixtures/ConfirmPurchaseOrder-params.xlsx"
        );
    }

    #[test]
    fn empty_recording_is_fully_covered_not_zero_percent() {
        let r = report_for(r#"<Recording><Name>T</Name><RootScope><Children/></RootScope></Recording>"#);
        assert_eq!(r.coverage.actions, 0);
        assert_eq!(r.coverage.percent(), 100.0);
    }
}
