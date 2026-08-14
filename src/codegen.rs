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
            line(out, &pad, &format!(
                "await d365.navigate('{}', '{}');",
                escape_ts(menu_item),
                kind.as_str()
            ));
        }
        Action::EnterForm { form } => {
            line(out, &pad, &format!("await d365.enterForm('{}');", escape_ts(form)));
        }
        Action::LeaveForm { form } => {
            line(out, &pad, &format!("await d365.leaveForm('{}');", escape_ts(form)));
        }
        Action::SetValue { control, value } => {
            line(out, &pad, &format!(
                "await d365.setField('{}', {});",
                escape_ts(control),
                value.to_ts()
            ));
        }
        Action::SetGridValue {
            grid,
            column,
            row,
            value,
        } => {
            line(out, &pad, &format!(
                "await d365.setGridCell('{}', '{}', {}, {});",
                escape_ts(grid),
                escape_ts(column),
                row,
                value.to_ts()
            ));
        }
        Action::Click { control } => {
            line(out, &pad, &format!("await d365.click('{}');", escape_ts(control)));
        }
        Action::Command { name } => {
            line(out, &pad, &format!("await d365.command('{}');", escape_ts(name)));
        }
        Action::Lookup { control, value } => {
            line(out, &pad, &format!(
                "await d365.lookup('{}', {});",
                escape_ts(control),
                value.to_ts()
            ));
        }
        Action::Validate { control, expected } => {
            line(out, &pad, &format!(
                "await d365.expectValue('{}', {});",
                escape_ts(control),
                expected.to_ts()
            ));
        }
        Action::Dialog { name, children } => {
            line(out, &pad, &format!(
                "await d365.withDialog('{}', async () => {{",
                escape_ts(name)
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
            r#"<AxTaskRecording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                 <Name>{name}</Name><Nodes>{nodes}</Nodes></AxTaskRecording>"#
        )
    }

    /// The recording name lands inside a backtick template literal, so a
    /// backtick or a `${` in it would otherwise emit code that does not parse.
    #[test]
    fn recording_name_cannot_break_out_of_the_test_title() {
        let spec = generate_from(
            &wrap("Order `x` ${evil}", "<Node i:type=\"CommandUserAction\"><CommandName>Save</CommandName></Node>"),
            OnUnsupported::Annotate,
        )
        .spec;

        assert!(spec.contains(r"test(`Order \`x\` \${evil} [${params.__case}]`"), "{spec}");
    }

    /// Recorded property values run to multiple lines often enough that a raw
    /// message in a `//` comment would spill past the comment.
    #[test]
    fn multiline_detail_stays_on_one_comment_line() {
        let spec = generate_from(
            &wrap("T", "<Node i:type=\"FutureAction\"><Note>one\ntwo</Note></Node>"),
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
        let spec = generate_from(
            &wrap("T", "<Node i:type=\"FutureAction\"><Note>x</Note></Node>"),
            OnUnsupported::Fail,
        )
        .spec;

        assert!(spec.contains("throw new Error('rsat2pw:"), "{spec}");
        assert!(!spec.contains("annotations.push"), "{spec}");
    }

    #[test]
    fn comment_mode_leaves_only_a_comment() {
        let spec = generate_from(
            &wrap("T", "<Node i:type=\"FutureAction\"><Note>x</Note></Node>"),
            OnUnsupported::Comment,
        )
        .spec;

        assert!(spec.contains("// TODO(rsat2pw)"), "{spec}");
        assert!(!spec.contains("annotations.push"), "{spec}");
        assert!(!spec.contains("throw new Error"), "{spec}");
    }

    /// The checked-in example under `tests/` is the converter's own advert. If
    /// it drifts from what the converter actually emits, the README is lying.
    #[test]
    fn checked_in_example_matches_current_output() {
        let spec = generate_from(
            include_str!("../fixtures/CreateCustomer.xml"),
            OnUnsupported::Annotate,
        )
        .spec;

        let committed = include_str!("../tests/CreateCustomer.spec.ts");
        assert_eq!(
            spec.replace("\r\n", "\n"),
            committed.replace("\r\n", "\n"),
            "regenerate with: cargo run -- fixtures/CreateCustomer.axtr --out-dir tests \
             --params fixtures/CreateCustomer-params.xlsx"
        );
    }
}
