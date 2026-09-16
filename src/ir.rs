//! The normalized action IR.
//!
//! This is the contract between "whatever Task Recorder gave us" and "what we
//! emit". Everything schema-specific dies in `lower.rs`; everything
//! Playwright-specific starts in `codegen.rs`.

use serde::Serialize;
use std::collections::BTreeMap;

/// Where a value comes from. Task Recorder captures a literal for every user
/// input, and RSAT turns each of those into a spreadsheet column - so they
/// become test-data fields here rather than literals baked into the spec.
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
    /// A form scope (`IsForm`). Control lookups inside it resolve against that
    /// form's subtree first, which is what keeps a dialog's OK button apart
    /// from the one on the form behind it.
    Form {
        form: String,
        children: Vec<Action>,
    },
    /// A recorder step group (`IsStepGroup`) - becomes a `test.step`, which
    /// makes the Playwright trace read like the original recording.
    Step {
        label: String,
        children: Vec<Action>,
    },
    /// `CommandName=Click`. The recorder names the *verb* in `CommandName` and
    /// the *target* in `ControlName`; `ControlType` says how to reach it.
    Click {
        control: String,
        control_type: String,
    },
    /// `CommandName=TabShown` - an action-pane tab, section page or pivot.
    Tab {
        control: String,
    },
    /// `PropertyUserAction` with `PropertyName=Value`.
    SetValue {
        control: String,
        control_type: String,
        value: Value,
    },
    /// The same, addressed into a (virtualized) grid row.
    SetGridValue {
        grid: String,
        column: String,
        row: usize,
        control_type: String,
        value: Value,
    },
    /// `CommandName=RequestPopup` - open a lookup on an input.
    OpenLookup {
        control: String,
    },
    /// `CommandName=ResolveChanges` - commit what the lookup selected.
    CommitLookup {
        control: String,
    },
    /// `CommandName=ChangeSelectedIndexInCache` - move the grid cursor.
    SelectRow {
        grid: String,
        row: usize,
    },
    /// `CommandName=MarkActiveRow` - tick the current row's selection box.
    MarkRow {
        grid: String,
    },
    /// `CommandName=NavigationAction` - follow the link in the selected row.
    OpenRow {
        grid: String,
    },
    /// `CommandName=ApplyFiltersForTaskRecorder`, unpacked from the JSON the
    /// recorder passes as a command argument.
    Filter {
        control: String,
        field: String,
        /// The column header the user actually clicked. The runtime finds the
        /// column by this; `field` is only a fallback.
        label: String,
        operator: String,
        value: Value,
    },
    /// `CommandName=SelectionPathChanged` - pick a node in a tree. The path
    /// arrives backslash-separated, as the tree renders it.
    SelectTreeItem {
        control: String,
        path: Value,
    },
    /// `CommandName=ExecuteShortcuts` - a named client shortcut, such as the
    /// one that flips a page between View and Edit mode.
    Shortcut {
        name: String,
    },
    /// `CommandName=RequestClose`.
    CloseForm,
    Validate {
        control: String,
        expected: Value,
    },
    /// A `TaskUserAction` sub-task boundary. Carries no behaviour, so it is
    /// emitted as a comment rather than a call.
    Marker {
        text: String,
    },
    /// Recorded, understood, and deliberately not replayed: client-internal
    /// bookkeeping with no user-visible effect. Distinct from `Unsupported`,
    /// because "this one does nothing" and "we have no rule for this" are
    /// different admissions and want different follow-up.
    Skipped {
        raw_kind: String,
        detail: String,
        props: BTreeMap<String, String>,
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
            Action::Form { .. } => "withForm",
            Action::Step { .. } => "test.step",
            Action::Click { .. } => "click",
            Action::Tab { .. } => "tab",
            Action::SetValue { .. } => "setField",
            Action::SetGridValue { .. } => "setGridCell",
            Action::OpenLookup { .. } => "openLookup",
            Action::CommitLookup { .. } => "commitLookup",
            Action::SelectRow { .. } => "selectRow",
            Action::MarkRow { .. } => "markRow",
            Action::OpenRow { .. } => "openRow",
            Action::Filter { .. } => "filter",
            Action::SelectTreeItem { .. } => "selectTreeItem",
            Action::Shortcut { .. } => "shortcut",
            Action::CloseForm => "closeForm",
            Action::Validate { .. } => "expectValue",
            Action::Marker { .. } => "marker",
            Action::Skipped { .. } => "skipped",
            Action::Unsupported { .. } => "unsupported",
        }
    }

    /// A short human-readable rendering of what this action does, using the
    /// same `params.X` / `'literal'` forms the generated spec uses.
    pub fn summary(&self) -> String {
        match self {
            Action::Navigate { menu_item, kind } => format!("{menu_item} ({})", kind.as_str()),
            Action::Form { form, .. } => form.clone(),
            Action::Step { label, .. } => label.clone(),
            Action::Click {
                control,
                control_type,
            } => format!("{control} ({control_type})"),
            Action::Tab { control } => control.clone(),
            Action::SetValue { control, value, .. } => format!("{control} = {}", value.to_ts()),
            Action::SetGridValue {
                grid,
                column,
                row,
                value,
                ..
            } => format!("{grid}[{row}].{column} = {}", value.to_ts()),
            Action::OpenLookup { control } | Action::CommitLookup { control } => control.clone(),
            Action::SelectRow { grid, row } => format!("{grid}[{row}]"),
            Action::MarkRow { grid } | Action::OpenRow { grid } => grid.clone(),
            Action::Filter {
                field,
                operator,
                value,
                ..
            } => format!("{field} {operator} {}", value.to_ts()),
            Action::SelectTreeItem { control, path } => {
                format!("{control} <- {}", path.to_ts())
            }
            Action::Shortcut { name } => name.clone(),
            Action::CloseForm => String::new(),
            Action::Validate { control, expected } => {
                format!("{control} == {}", expected.to_ts())
            }
            Action::Marker { text } => text.clone(),
            Action::Skipped {
                raw_kind, detail, ..
            } => {
                if detail.is_empty() {
                    raw_kind.clone()
                } else {
                    format!("{raw_kind} ({detail})")
                }
            }
            Action::Unsupported { raw_kind, .. } => raw_kind.clone(),
        }
    }

    pub fn children(&self) -> &[Action] {
        match self {
            Action::Step { children, .. } | Action::Form { children, .. } => children,
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
        count_matching(&self.actions, &|a| matches!(a, Action::Unsupported { .. }))
    }

    pub fn skipped_count(&self) -> usize {
        count_matching(&self.actions, &|a| matches!(a, Action::Skipped { .. }))
    }

    pub fn action_count(&self) -> usize {
        fn walk(actions: &[Action]) -> usize {
            actions.iter().map(|a| 1 + walk(a.children())).sum()
        }
        walk(&self.actions)
    }
}

fn count_matching(actions: &[Action], pred: &dyn Fn(&Action) -> bool) -> usize {
    actions
        .iter()
        .map(|a| usize::from(pred(a)) + count_matching(a.children(), pred))
        .sum()
}
