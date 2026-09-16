//! Emission: `ir::TestCase` -> Playwright TypeScript.
//!
//! Generated tests never talk to raw Playwright locators. They call into the
//! hand-written `D365` runtime helper, so all the ugly async/blocking-state
//! handling stays in one maintainable place instead of being smeared across
//! thousands of generated lines.

use crate::ir::{comment_safe, escape_template_literal, escape_ts, Action, TestCase};
use crate::params::Cases;
use anyhow::Result;
use heck::ToPascalCase;
use minijinja::{context, Environment};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OnUnsupported {
    /// Record a Playwright annotation and keep going (default).
    Annotate,
    /// Throw, so an unconverted step can never pass silently.
    Fail,
    /// Leave a bare comment.
    Comment,
}

impl OnUnsupported {
    pub fn parse(s: &str) -> Option<Self> {
        match s.to_ascii_lowercase().as_str() {
            "annotate" => Some(Self::Annotate),
            "fail" => Some(Self::Fail),
            "comment" => Some(Self::Comment),
            _ => None,
        }
    }
}

pub struct Output {
    pub spec: String,
    pub data: String,
    pub stem: String,
}

pub fn generate(case: &TestCase, cases: &Cases, on_unsupported: OnUnsupported) -> Result<Output> {
    let stem = case.name.to_pascal_case();
    let mut body = String::new();
    emit_all(&case.actions, 2, on_unsupported, &mut body);

    let mut env = Environment::new();
    env.add_template("spec", include_str!("templates/spec.ts.jinja"))?;
    env.add_template("data", include_str!("templates/data.ts.jinja"))?;

    let fields: Vec<_> = cases
        .fields
        .iter()
        .map(|f| context! { name => f.clone() })
        .collect();

    let rows: Vec<_> = cases
        .rows
        .iter()
        .map(|row| {
            let values: Vec<_> = cases
                .fields
                .iter()
                .map(|f| {
                    context! {
                        name => f.clone(),
                        literal => format!("'{}'", escape_ts(row.values.get(f).map(String::as_str).unwrap_or(""))),
                    }
                })
                .collect();
            context! { label => escape_ts(&row.label), values => values }
        })
        .collect();

    let spec = env.get_template("spec")?.render(context! {
        // Two renderings of the same name: one safe inside a `//` comment, one
        // safe inside the backtick-quoted test title.
        recording_name => comment_safe(&case.name),
        test_title => escape_template_literal(&case.name),
        stem => stem.clone(),
        data_module => format!("./{stem}.data"),
        body => body.trim_end().to_string(),
        action_count => case.action_count(),
        todo_count => case.unsupported_count(),
    })?;

    let data = env.get_template("data")?.render(context! {
        stem => stem.clone(),
        fields => fields,
        rows => rows,
        source => cases.source.clone(),
    })?;

    Ok(Output { spec, data, stem })
}

fn emit_all(actions: &[Action], indent: usize, on_unsupported: OnUnsupported, out: &mut String) {
    for action in actions {
        emit(action, indent, on_unsupported, out);
    }
}

fn emit(action: &Action, indent: usize, on_unsupported: OnUnsupported, out: &mut String) {
    let pad = "  ".repeat(indent);

    match action {
        Action::Navigate { menu_item, kind } => {
            call(out, &pad, &format!(
                "navigate('{}', '{}')",
                escape_ts(menu_item),
                kind.as_str()
            ));
        }
        Action::Form { form, children } => {
            line(out, &pad, &format!(
                "await d365.withForm('{}', async () => {{",
                escape_ts(form)
            ));
            emit_all(children, indent + 1, on_unsupported, out);
            line(out, &pad, "});");
        }
        Action::Step { label, children } => {
            line(out, &pad, &format!(
                "await test.step('{}', async () => {{",
                escape_ts(label)
            ));
            emit_all(children, indent + 1, on_unsupported, out);
            line(out, &pad, "});");
            out.push('\n');
        }
        Action::Click {
            control,
            control_type,
        } => {
            call(out, &pad, &format!(
                "click('{}', '{}')",
                escape_ts(control),
                escape_ts(control_type)
            ));
        }
        Action::Tab { control } => {
            call(out, &pad, &format!("tab('{}')", escape_ts(control)));
        }
        Action::SetValue {
            control,
            control_type,
            value,
        } => {
            call(out, &pad, &format!(
                "setField('{}', {}, '{}')",
                escape_ts(control),
                value.to_ts(),
                escape_ts(control_type)
            ));
        }
        Action::SetGridValue {
            grid,
            column,
            row,
            control_type,
            value,
        } => {
            call(out, &pad, &format!(
                "setGridCell('{}', '{}', {}, {}, '{}')",
                escape_ts(grid),
                escape_ts(column),
                row,
                value.to_ts(),
                escape_ts(control_type)
            ));
        }
        Action::OpenLookup { control } => {
            call(out, &pad, &format!("openLookup('{}')", escape_ts(control)));
        }
        Action::CommitLookup { control } => {
            call(out, &pad, &format!("commitLookup('{}')", escape_ts(control)));
        }
        Action::SelectRow { grid, row } => {
            call(out, &pad, &format!("selectRow('{}', {})", escape_ts(grid), row));
        }
        Action::MarkRow { grid } => {
            call(out, &pad, &format!("markRow('{}')", escape_ts(grid)));
        }
        Action::OpenRow { grid } => {
            call(out, &pad, &format!("openRow('{}')", escape_ts(grid)));
        }
        Action::Filter {
            control,
            field,
            label,
            operator,
            value,
        } => {
            call(out, &pad, &format!(
                "filter('{}', '{}', '{}', '{}', {})",
                escape_ts(control),
                escape_ts(field),
                escape_ts(label),
                escape_ts(operator),
                value.to_ts()
            ));
        }
        Action::SelectTreeItem { control, path } => {
            call(out, &pad, &format!(
                "selectTreeItem('{}', {})",
                escape_ts(control),
                path.to_ts()
            ));
        }
        Action::Shortcut { name } => {
            call(out, &pad, &format!("shortcut('{}')", escape_ts(name)));
        }
        Action::CloseForm => call(out, &pad, "closeForm()"),
        Action::Validate { control, expected } => {
            call(out, &pad, &format!(
                "expectValue('{}', {})",
                escape_ts(control),
                expected.to_ts()
            ));
        }
        Action::Marker { text } => {
            line(out, &pad, &format!("// {}", comment_safe(text)));
        }
        // Understood and deliberately not replayed. It still leaves a trace in
        // the generated file: a step that vanishes without a word is
        // indistinguishable from one the converter never saw.
        Action::Skipped {
            raw_kind, detail, ..
        } => {
            line(out, &pad, &format!(
                "// rsat2pw: skipped '{}' - {}",
                comment_safe(raw_kind),
                comment_safe(detail)
            ));
        }
        // `props` carries the untruncated bag for the conversion report; the
        // emitted TODO deliberately uses the short `detail` instead.
        Action::Unsupported {
            raw_kind, detail, ..
        } => {
            let msg = format!("could not map Task Recorder action '{raw_kind}' ({detail})");
            // Two renderings again: recorded property values run to multiple
            // lines often enough that a raw `msg` in a `//` comment would spill
            // past the comment and emit code that does not parse.
            let literal = escape_ts(&msg);
            line(out, &pad, &format!("// TODO(rsat2pw): {}", comment_safe(&msg)));
            match on_unsupported {
                OnUnsupported::Comment => {}
                OnUnsupported::Annotate => {
                    line(out, &pad, &format!(
                        "test.info().annotations.push({{ type: 'rsat2pw-todo', description: '{literal}' }});"
                    ));
                }
                OnUnsupported::Fail => {
                    line(out, &pad, &format!("throw new Error('rsat2pw: {literal}');"));
                }
            }
        }
    }
}

fn call(out: &mut String, pad: &str, expr: &str) {
    line(out, pad, &format!("await d365.{expr};"));
}

fn line(out: &mut String, pad: &str, text: &str) {
    out.push_str(pad);
    out.push_str(text);
    out.push('\n');
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::params::Case;
    use crate::{lower, recording};

    fn generate_from(xml: &str, on_unsupported: OnUnsupported) -> Output {
        let case = lower::lower(&recording::parse(xml).unwrap());
        let mut cases = crate::params::from_recording(&case);
        if cases.rows.is_empty() {
            cases.rows.push(Case {
                label: "default".to_string(),
                values: Default::default(),
            });
        }
        generate(&case, &cases, on_unsupported).unwrap()
    }

    fn wrap(name: &str, nodes: &str) -> String {
        format!(
            r#"<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                 <Name>{name}</Name><RootScope><Children>{nodes}</Children></RootScope></Recording>"#
        )
    }

    const CLICK: &str = r#"<Node i:type="CommandUserAction"><CommandName>Click</CommandName>
        <ControlName>SystemDefinedSaveButton</ControlName><ControlType>CommandButton</ControlType></Node>"#;

    const UNKNOWN: &str = r#"<Node i:type="CommandUserAction"><CommandName>Mystery</CommandName>
        <ControlName>Grid</ControlName><Note>x</Note></Node>"#;

    #[test]
    fn a_click_carries_its_control_type_through_to_the_runtime() {
        let spec = generate_from(&wrap("T", CLICK), OnUnsupported::Annotate).spec;
        assert!(
            spec.contains("await d365.click('SystemDefinedSaveButton', 'CommandButton');"),
            "{spec}"
        );
    }

    /// The recording name lands inside a backtick template literal, so a
    /// backtick or a `${` in it would otherwise emit code that does not parse.
    #[test]
    fn recording_name_cannot_break_out_of_the_test_title() {
        let spec = generate_from(&wrap("Order `x` ${evil}", CLICK), OnUnsupported::Annotate).spec;

        assert!(spec.contains(r"test(`Order \`x\` \${evil} [${params.__case}]`"), "{spec}");
    }

    /// Recorded property values run to multiple lines often enough that a raw
    /// message in a `//` comment would spill past the comment.
    #[test]
    fn multiline_detail_stays_on_one_comment_line() {
        let spec = generate_from(
            &wrap(
                "T",
                r#"<Node i:type="CommandUserAction"><CommandName>Mystery</CommandName>
                   <Note>one
two</Note></Node>"#,
            ),
            OnUnsupported::Annotate,
        )
        .spec;

        let comment = spec
            .lines()
            .find(|l| l.trim_start().starts_with("// TODO(rsat2pw):"))
            .expect("expected a TODO comment");
        assert!(comment.contains("Note=one two"), "{comment}");
        // The escaped string form keeps the newline; only the comment flattens.
        assert!(spec.contains(r"Note=one\ntwo"), "{spec}");
    }

    #[test]
    fn fail_mode_throws_instead_of_annotating() {
        let spec = generate_from(&wrap("T", UNKNOWN), OnUnsupported::Fail).spec;

        assert!(spec.contains("throw new Error('rsat2pw:"), "{spec}");
        assert!(!spec.contains("annotations.push"), "{spec}");
    }

    #[test]
    fn comment_mode_leaves_only_a_comment() {
        let spec = generate_from(&wrap("T", UNKNOWN), OnUnsupported::Comment).spec;

        assert!(spec.contains("// TODO(rsat2pw)"), "{spec}");
        assert!(!spec.contains("annotations.push"), "{spec}");
        assert!(!spec.contains("throw new Error"), "{spec}");
    }

    /// A skipped action still says so in the generated file. Silently dropping
    /// it would be indistinguishable from never having seen it.
    #[test]
    fn a_skipped_action_leaves_a_comment_behind() {
        let spec = generate_from(
            &wrap(
                "T",
                r#"<Node i:type="CommandUserAction"><CommandName>GetFilters</CommandName>
                   <ControlName>SystemDefinedFilterManager</ControlName></Node>"#,
            ),
            OnUnsupported::Annotate,
        )
        .spec;

        assert!(
            spec.contains("// rsat2pw: skipped 'CommandUserAction:GetFilters'"),
            "{spec}"
        );
    }

    /// The checked-in example under `tests/` is the converter's own advert. If
    /// it drifts from what the converter actually emits, the README is lying.
    #[test]
    fn checked_in_example_matches_current_output() {
        let spec = generate_from(
            include_str!("../fixtures/ConfirmPurchaseOrder.xml"),
            OnUnsupported::Annotate,
        )
        .spec;

        let committed = include_str!("../tests/ConfirmPurchaseOrder.spec.ts");
        assert_eq!(
            spec.replace("\r\n", "\n"),
            committed.replace("\r\n", "\n"),
            "regenerate with: cargo run -- fixtures/ConfirmPurchaseOrder.axtr --out-dir tests \
             --params fixtures/ConfirmPurchaseOrder-params.xlsx"
        );
    }
}
