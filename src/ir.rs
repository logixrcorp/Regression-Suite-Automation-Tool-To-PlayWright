//! The normalized action IR.
//!
//! This is the contract between "whatever Task Recorder gave us" and "what we
//! emit". Everything schema-specific dies in `lower.rs`; everything
//! Playwright-specific starts in `codegen.rs`.

use serde::Serialize;
use std::collections::BTreeMap;

/// Where a value comes from. Recordings that were parameterized in Task
/// Recorder reference a variable, which is exactly what RSAT surfaces as an
/// Excel column - so it becomes a test-data field rather than a literal.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum Value {
    Literal { text: String },
    Variable { name: String },
}

impl Value {
    /// Render as a TypeScript expression.
    pub fn to_ts(&self) -> String {
        match self {
            Value::Literal { text } => format!("'{}'", escape_ts(text)),
            Value::Variable { name } => format!("params.{}", name),
        }
    }
}

/// Escape for a single-quoted TypeScript string literal. `\r` matters as much
/// as `\n`: TypeScript treats a bare carriage return as a line terminator, so a
/// CRLF that survived XML parsing would otherwise emit an unterminated string.
pub fn escape_ts(s: &str) -> String {
    s.replace('\\', "\\\\")
        .replace('\'', "\\'")
        .replace('\r', "\\r")
        .replace('\n', "\\n")
}

/// Escape for a backtick template literal, where `${` starts an interpolation
/// and a backtick ends the string. Recording and step names are author-supplied
/// text, so neither can be trusted to be inert here.
pub fn escape_template_literal(s: &str) -> String {
    s.replace('\\', "\\\\")
        .replace('`', "\\`")
        .replace("${", "\\${")
        .replace('\r', "\\r")
        .replace('\n', "\\n")
}

/// Flatten to a single line so it cannot break out of a `//` comment.
pub fn comment_safe(s: &str) -> String {
    s.replace(['\r', '\n'], " ")
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum MenuItemKind {
    Display,
    Action,
    Output,
}

impl MenuItemKind {
    pub fn as_str(&self) -> &'static str {
        match self {
            MenuItemKind::Display => "Display",
            MenuItemKind::Action => "Action",
            MenuItemKind::Output => "Output",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "op", rename_all = "camelCase")]
pub enum Action {
    /// Deep-link to a menu item - the reliable way into a form, far better
    /// than replaying the navigation-pane clicks the recorder captured.
    Navigate {
        menu_item: String,
        kind: MenuItemKind,
    },
    /// A form became active. Used to scope subsequent control lookups.
    EnterForm { form: String },
    LeaveForm { form: String },
    SetValue {
        control: String,
        value: Value,
    },
    /// Set a cell in a (virtualized) grid, addressed by column name.
    SetGridValue {
        grid: String,
        column: String,
        row: usize,
        value: Value,
    },
    Click {
        control: String,
    },
    /// A system command such as Save / New / Delete, which D365 renders with
    /// well-known control names rather than user-defined ones.
    Command {
        name: String,
    },
    /// Pick a value through a lookup drop-down rather than typing it.
    Lookup {
        control: String,
        value: Value,
    },
    /// Everything inside runs scoped to a dialog/slider overlay.
    Dialog {
        name: String,
        children: Vec<Action>,
    },
    Validate {
        control: String,
        expected: Value,
    },
    /// A recorder annotation - becomes a `test.step`, which makes the
    /// Playwright trace read like the original recording.
    Step {
        label: String,
        children: Vec<Action>,
    },
    /// Deliberate escape hatch. We never guess: unmapped actions are emitted
    /// as a failing-loud TODO so a human sees the gap.
    Unsupported {
        raw_kind: String,
        /// Truncated one-liner, sized for the emitted `TODO(rsat2pw)` comment.
        detail: String,
        /// Every property the recorder gave us, untruncated. `detail` is for
        /// the generated code; this is for the conversion report, where it is
        /// the raw material for writing a new rule in `lower.rs`.
        props: BTreeMap<String, String>,
    },
}

impl Action {
    /// The name this action goes out as. For everything that reaches the
    /// runtime this is the method the generated spec calls, so a conversion
    /// report and the emitted code use one vocabulary.
    pub fn op_name(&self) -> &'static str {
        match self {
            Action::Navigate { .. } => "navigate",
            Action::EnterForm { .. } => "enterForm",
            Action::LeaveForm { .. } => "leaveForm",
            Action::SetValue { .. } => "setField",
            Action::SetGridValue { .. } => "setGridCell",
            Action::Click { .. } => "click",
            Action::Command { .. } => "command",
            Action::Lookup { .. } => "lookup",
            Action::Validate { .. } => "expectValue",
            Action::Dialog { .. } => "withDialog",
            Action::Step { .. } => "test.step",
            Action::Unsupported { .. } => "unsupported",
        }
    }

    /// A short human-readable rendering of what this action does, using the
    /// same `params.X` / `'literal'` forms the generated spec uses.
    pub fn summary(&self) -> String {
        match self {
            Action::Navigate { menu_item, kind } => format!("{menu_item} ({})", kind.as_str()),
            Action::EnterForm { form } | Action::LeaveForm { form } => form.clone(),
            Action::SetValue { control, value } => format!("{control} = {}", value.to_ts()),
            Action::SetGridValue {
                grid,
                column,
                row,
                value,
            } => format!("{grid}[{row}].{column} = {}", value.to_ts()),
            Action::Click { control } => control.clone(),
            Action::Command { name } => name.clone(),
            Action::Lookup { control, value } => format!("{control} <- {}", value.to_ts()),
            Action::Validate { control, expected } => {
                format!("{control} == {}", expected.to_ts())
            }
            Action::Dialog { name, .. } => name.clone(),
            Action::Step { label, .. } => label.clone(),
            Action::Unsupported { raw_kind, .. } => raw_kind.clone(),
        }
    }

    pub fn children(&self) -> &[Action] {
        match self {
            Action::Step { children, .. } | Action::Dialog { children, .. } => children,
            _ => &[],
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Variable {
    pub name: String,
    pub default: String,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct TestCase {
    pub name: String,
    pub variables: Vec<Variable>,
    pub actions: Vec<Action>,
}

impl TestCase {
    pub fn unsupported_count(&self) -> usize {
        fn walk(actions: &[Action]) -> usize {
            actions
                .iter()
                .map(|a| match a {
                    Action::Unsupported { .. } => 1,
                    Action::Step { children, .. } | Action::Dialog { children, .. } => walk(children),
                    _ => 0,
                })
                .sum()
        }
        walk(&self.actions)
    }

    pub fn action_count(&self) -> usize {
        fn walk(actions: &[Action]) -> usize {
            actions
                .iter()
                .map(|a| match a {
                    Action::Step { children, .. } | Action::Dialog { children, .. } => {
                        1 + walk(children)
                    }
                    _ => 1,
                })
                .sum()
        }
        walk(&self.actions)
    }
}
