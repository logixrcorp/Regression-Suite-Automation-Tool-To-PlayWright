//! Lowering: `RecNode` -> `ir::Action`.
//!
//! All the schema knowledge lives here, in one editable table. When a node
//! does not match anything we emit `Unsupported` rather than inventing
//! behaviour - a converter that is 85% automatic plus honest gaps beats one
//! that silently emits wrong code.
//!
//! The vocabulary this maps is the one real exports actually use. There are
//! five node kinds and, within `CommandUserAction`, a small set of command
//! names:
//!
//! | Node | Meaning |
//! | --- | --- |
//! | `Scope` | grouping; `IsForm` / `IsStepGroup` say which kind |
//! | `MenuItemUserAction` | navigation |
//! | `PropertyUserAction` | field entry (`PropertyName=Value`) |
//! | `CommandUserAction` | a verb in `CommandName` against `ControlName` |
//! | `TaskUserAction` | a sub-task boundary marker |
//!
//! The important shape is that last one: the recorder puts the *verb* in
//! `CommandName` (`Click`, `TabShown`, `RequestPopup`, ...) and the *target*
//! in `ControlName`, with `ControlType` saying what kind of control it is.
//! Reading `CommandName` as the thing to click is the single easiest way to
//! generate a suite that hunts for a button labelled "Click".

use crate::ir::{Action, MenuItemKind, TestCase, Value, Variable};
use crate::recording::{RecNode, Recording};

const CONTROL_KEYS: &[&str] = &["ControlName", "Control", "TargetControl", "ControlId"];
const VALUE_KEYS: &[&str] = &["Value", "NewValue", "Text", "InputValue"];
const VARIABLE_KEYS: &[&str] = &["VariableName", "Variable", "ParameterName"];
const LABEL_KEYS: &[&str] = &["Description", "CustomDescription", "Annotation", "Name"];

/// Forms that belong to the recorder, not to the business process. Task
/// Recorder runs inside the client it is recording, so its own pane shows up
/// as a form scope wrapped around perfectly ordinary actions; keeping the
/// scope would nest the whole recording inside a form that is not there on
/// playback. The children are kept, only the wrapper goes.
const RECORDER_INTERNAL_FORMS: &[&str] = &[
    "SysBPMPane",
    "SysTaskRecorderPane",
    "SysTaskRecorderForm",
    "SysTaskRecorderStartForm",
    "SysTaskRecorderStopForm",
];

/// Collects the test-data fields as lowering walks the recording.
///
/// RSAT parameterizes *every* recorded input - that is what fills the columns
/// of its parameter workbook - so each recorded value becomes a variable here
/// too, defaulting to what the recorder captured.
struct Ctx {
    variables: Vec<Variable>,
    taken: Vec<String>,
}

impl Ctx {
    fn new(seed: &[(String, String)]) -> Self {
        let mut ctx = Ctx {
            variables: Vec::new(),
            taken: Vec::new(),
        };
        // Two recorder variables can sanitize to the same identifier
        // ("Customer name" and "Customer-name" both become `Customer_name`),
        // which would emit a duplicate key in the generated `Params` type and
        // fail to compile.
        for (name, default) in seed {
            ctx.declare(&sanitize_ident(name), default);
        }
        ctx
    }

    fn declare(&mut self, candidate: &str, default: &str) -> String {
        let name = unique_ident(candidate, &self.taken);
        self.taken.push(name.clone());
        self.variables.push(Variable {
            name: name.clone(),
            default: default.to_string(),
        });
        name
    }

    /// A variable the recording named explicitly. Repeated references to the
    /// same recorder variable must land on the same field, so this reuses.
    fn named(&mut self, raw: &str, default: &str) -> Value {
        let candidate = sanitize_ident(raw);
        if let Some(existing) = self.variables.iter().find(|v| v.name == candidate) {
            return Value::Variable {
                name: existing.name.clone(),
            };
        }
        Value::Variable {
            name: self.declare(&candidate, default),
        }
    }

    /// A variable derived from the control that was edited. Each recorded
    /// input gets its own field even when the same control is touched twice,
    /// because those are two steps with two values - which is exactly how
    /// RSAT numbers its own columns.
    fn derived(&mut self, base: &str, default: &str) -> Value {
        let candidate = sanitize_ident(if base.is_empty() { "value" } else { base });
        Value::Variable {
            name: self.declare(&candidate, default),
        }
    }
}

pub fn lower(rec: &Recording) -> TestCase {
    let mut ctx = Ctx::new(&rec.variables);
    let actions = lower_nodes(&rec.nodes, &mut ctx);

    // A recording can reference a variable that never made it into a
    // declaration. Declaring it anyway keeps the generated data module and the
    // generated spec in agreement, so the output always compiles.
    let mut referenced = Vec::new();
    collect_referenced(&actions, &mut referenced);
    for name in referenced {
        if !ctx.variables.iter().any(|v| v.name == name) {
            ctx.variables.push(Variable {
                name,
                default: String::new(),
            });
        }
    }

    TestCase {
        name: rec.name.clone(),
        variables: ctx.variables,
        actions,
    }
}

fn collect_referenced(actions: &[Action], out: &mut Vec<String>) {
    let note = |v: &Value, out: &mut Vec<String>| {
        if let Value::Variable { name } = v
            && !out.contains(name)
        {
            out.push(name.clone());
        }
    };

    for action in actions {
        match action {
            Action::SetValue { value, .. }
            | Action::SetGridValue { value, .. }
            | Action::Filter { value, .. } => note(value, out),
            Action::Validate { expected, .. } => note(expected, out),
            _ => {}
        }
        collect_referenced(action.children(), out);
    }
}

fn lower_nodes(nodes: &[RecNode], ctx: &mut Ctx) -> Vec<Action> {
    nodes.iter().flat_map(|n| lower_node(n, ctx)).collect()
}

fn lower_node(node: &RecNode, ctx: &mut Ctx) -> Vec<Action> {
    let kind = node.kind.to_ascii_lowercase();

    match kind.as_str() {
        "scope" => return lower_scope(node, ctx),
        "menuitemuseraction" => return vec![lower_menu_item(node)],
        "commanduseraction" => return vec![lower_command(node, ctx)],
        "propertyuseraction" => return vec![lower_property(node, ctx)],
        "taskuseraction" => return vec![lower_task_marker(node)],
        // A note the recorder was asked to keep, and a bare annotation. Both
        // are commentary on the recording rather than something to replay.
        "infouseraction" | "annotationuseraction" => return vec![lower_note(node)],
        _ => {}
    }

    // Scopes carry their kind in the property bag as well as in `i:type`, and
    // older exports spelled the grouping node differently.
    if node.has_prop(&["IsForm"]) || node.has_prop(&["IsStepGroup"]) {
        return lower_scope(node, ctx);
    }
    if node.has_prop(&["MenuItemName"]) {
        return vec![lower_menu_item(node)];
    }

    vec![lower_legacy(node, ctx)]
}

fn lower_scope(node: &RecNode, ctx: &mut Ctx) -> Vec<Action> {
    let children = lower_nodes(&node.children, ctx);

    // An empty scope has nothing to wrap. Real recordings are full of them:
    // the client re-enters a form scope every time focus returns to it.
    if children.is_empty() {
        return children;
    }

    if node.flag("IsForm") {
        let form = node
            .prop(&["Name", "FormName", "RecordingName"])
            .unwrap_or_default()
            .to_string();
        if form.is_empty() || is_recorder_internal(&form) {
            return children;
        }
        return vec![Action::Form { form, children }];
    }

    // A step group the user made while recording is public. The client marks
    // its own groupings the same way - every lookup it opens becomes a private
    // `<control>_RequestPopup` group - and those are plumbing, not intent, so
    // only the public ones survive as `test.step`.
    if node.flag("IsStepGroup") {
        if node
            .prop(&["ScopeType"])
            .is_some_and(|s| s.eq_ignore_ascii_case("Private"))
        {
            return children;
        }
        return vec![Action::Step {
            label: scope_label(node),
            children,
        }];
    }

    // A private scope with neither flag is client plumbing - the wrapper the
    // recorder puts around "the filter manager handled a call". Keeping them
    // buries three real actions under fifteen meaningless `test.step` blocks,
    // so the children are lifted into the parent.
    if node.has_prop(&["IsForm"]) || node.has_prop(&["IsStepGroup"]) {
        return children;
    }

    // Older exports had no flags at all. Group only when the recorder gave the
    // scope a human label.
    match node.prop(LABEL_KEYS) {
        Some(label) if !label.is_empty() => vec![Action::Step {
            label: label.to_string(),
            children,
        }],
        _ => children,
    }
}

fn scope_label(node: &RecNode) -> String {
    node.prop(LABEL_KEYS)
        .filter(|l| !l.is_empty())
        .unwrap_or("Recorded step")
        .to_string()
}

fn is_recorder_internal(form: &str) -> bool {
    RECORDER_INTERNAL_FORMS
        .iter()
        .any(|f| f.eq_ignore_ascii_case(form))
}

fn lower_menu_item(node: &RecNode) -> Action {
    let menu_item = node
        .prop(&["MenuItemName", "MenuItem", "Name"])
        .unwrap_or_default()
        .to_string();

    let kind = match node
        .prop(&["MenuItemType", "MenuItemKind"])
        .unwrap_or("Display")
        .to_ascii_lowercase()
        .as_str()
    {
        "action" => MenuItemKind::Action,
        "output" => MenuItemKind::Output,
        _ => MenuItemKind::Display,
    };

    Action::Navigate { menu_item, kind }
}

fn lower_command(node: &RecNode, ctx: &mut Ctx) -> Action {
    let command = node.prop(&["CommandName", "Command"]).unwrap_or_default();
    let control = node.prop(CONTROL_KEYS).unwrap_or_default().to_string();
    let control_type = node.prop(&["ControlType"]).unwrap_or_default().to_string();
    // A grid command names the list in `ListContext`; `ControlName` repeats it.
    let grid = node
        .prop(&["ListContext"])
        .filter(|l| !l.is_empty())
        .unwrap_or(control.as_str())
        .to_string();

    match command.to_ascii_lowercase().as_str() {
        "click" if !control.is_empty() => Action::Click {
            control,
            control_type,
        },
        "tabshown" if !control.is_empty() => Action::Tab { control },
        "requestpopup" if !control.is_empty() => Action::OpenLookup { control },
        "resolvechanges" if !control.is_empty() => Action::CommitLookup { control },
        "navigationaction" if !grid.is_empty() => Action::OpenRow { grid },
        "markactiverow" if !grid.is_empty() => Action::MarkRow { grid },
        "changeselectedindexincache" if !grid.is_empty() => Action::SelectRow {
            grid,
            // The new cursor position is the first command argument.
            row: node.arg(0).and_then(|a| a.trim().parse().ok()).unwrap_or(0),
        },
        "applyfiltersfortaskrecorder" => lower_filter(node, ctx, control),
        "selectionpathchanged" if !control.is_empty() => Action::SelectTreeItem {
            // The tree path is the value the user picked, so it is test data
            // like any other recorded input.
            path: ctx.derived(&control, node.arg(0).unwrap_or_default()),
            control,
        },
        // The shortcut name is the whole instruction; without it there is
        // nothing to replay.
        "executeshortcuts" => match node.arg(0).filter(|a| !a.is_empty()) {
            Some(name) => Action::Shortcut {
                name: name.to_string(),
            },
            None => unsupported(node),
        },
        // Opening the filter flyout. `filter()` does that itself as part of
        // applying one, so replaying this would just toggle the pane shut.
        "getfilters" => skipped(node, "opens the filter pane; filter() does that itself"),
        "requestclose" => Action::CloseForm,
        _ => unsupported(node),
    }
}

/// Unpack `ApplyFiltersForTaskRecorder`, whose first command argument is a
/// JSON array describing the filter the user typed.
fn lower_filter(node: &RecNode, ctx: &mut Ctx, control: String) -> Action {
    let json = node.arg(0).unwrap_or_default();

    // `FieldName` appears twice: once inside the (often null) `Capability`
    // object, where it is blank, and once at the top level where it is real.
    let field = json_string(json, "FieldName")
        .or_else(|| json_string(json, "FieldLabel"))
        .unwrap_or_default();
    let label = json_string(json, "FieldLabel").unwrap_or_default();
    let operator = json_string(json, "Operator").unwrap_or_default();

    if field.is_empty() {
        return unsupported(node);
    }

    let recorded = json_first_array_string(json, "Values").unwrap_or_default();
    let value = ctx.derived(&field, &recorded);

    Action::Filter {
        control,
        field,
        label,
        operator,
        value,
    }
}

fn lower_property(node: &RecNode, ctx: &mut Ctx) -> Action {
    let property = node.prop(&["PropertyName"]).unwrap_or("Value");
    if !property.eq_ignore_ascii_case("Value") {
        return unsupported(node);
    }

    let control = node.prop(CONTROL_KEYS).unwrap_or_default().to_string();
    if control.is_empty() {
        return unsupported(node);
    }
    let control_type = node.prop(&["ControlType"]).unwrap_or_default().to_string();
    let value = value_of(node, ctx, &control);

    // A cell edit names its grid in `ListContext` and its row in `RowIndex`;
    // a plain field edit leaves both nil.
    let grid = node.prop(&["ListContext", "GridName", "Grid"]).unwrap_or_default();
    let row = node
        .prop(&["RowIndex", "Row"])
        .and_then(|r| r.trim().parse::<usize>().ok());

    match (grid.is_empty(), row) {
        (false, Some(row)) => Action::SetGridValue {
            grid: grid.to_string(),
            column: control,
            row,
            control_type,
            value,
        },
        _ => Action::SetValue {
            control,
            control_type,
            value,
        },
    }
}

/// A recorded note or annotation. It carries no behaviour, so it is emitted
/// as a comment - the recording said it for a reason, and dropping it loses
/// the only thing the recorder was told in prose.
fn lower_note(node: &RecNode) -> Action {
    Action::Marker {
        text: node
            .prop(&["Notes", "Text", "Description", "Comment"])
            .unwrap_or("Note")
            .to_string(),
    }
}

fn lower_task_marker(node: &RecNode) -> Action {
    let label = node
        .prop(&["Description", "Name", "Comment"])
        .unwrap_or("Sub-task")
        .to_string();

    match node.prop(&["UserActionType"]) {
        Some(phase) if !phase.is_empty() => Action::Marker {
            text: format!("{label} ({phase})"),
        },
        _ => Action::Marker { text: label },
    }
}

/// Shapes from older exports, kept because the parser is deliberately tolerant
/// and a recording that predates the current schema should still convert.
fn lower_legacy(node: &RecNode, ctx: &mut Ctx) -> Action {
    let kind = node.kind.to_ascii_lowercase();
    let control = node.prop(CONTROL_KEYS).unwrap_or_default().to_string();

    if kind.contains("validat") || kind.contains("verif") || kind.contains("assert") {
        return Action::Validate {
            expected: value_of(node, ctx, &control),
            control,
        };
    }

    if !control.is_empty() && (kind.contains("input") || node.has_prop(VALUE_KEYS)) {
        return Action::SetValue {
            control_type: node.prop(&["ControlType"]).unwrap_or_default().to_string(),
            value: value_of(node, ctx, &control),
            control,
        };
    }

    unsupported(node)
}

fn value_of(node: &RecNode, ctx: &mut Ctx, base: &str) -> Value {
    let recorded = node.prop(VALUE_KEYS).unwrap_or_default().to_string();

    match node.prop(VARIABLE_KEYS).filter(|v| !v.is_empty()) {
        Some(var) => ctx.named(var, &recorded),
        None => ctx.derived(base, &recorded),
    }
}

fn unsupported(node: &RecNode) -> Action {
    Action::Unsupported {
        raw_kind: raw_kind(node),
        detail: node.describe(),
        props: node.props.clone(),
    }
}

fn skipped(node: &RecNode, why: &str) -> Action {
    Action::Skipped {
        raw_kind: raw_kind(node),
        detail: why.to_string(),
        props: node.props.clone(),
    }
}

/// How an unmapped node is named in the report. A bare `CommandUserAction` is
/// useless as a worklist entry - every command is one - so commands are
/// reported by the verb that has no rule yet.
fn raw_kind(node: &RecNode) -> String {
    match node.prop(&["CommandName", "Command"]) {
        Some(command) if !command.is_empty() => format!("{}:{}", node.kind, command),
        _ => node.kind.clone(),
    }
}

// -- the JSON the recorder embeds in command arguments -----------------------
//
// Deliberately a scanner rather than a parser, and deliberately duplicated
// verbatim in the C# port: the two implementations are held to byte-identical
// output, and the cheapest way to keep that true is for both to be this dumb.

/// First non-empty `"key": "value"` in the blob. The filter payload carries
/// `FieldName` twice - blank inside `Capability`, real at the top level - so
/// "first non-empty" is what picks the one that matters.
fn json_string(json: &str, key: &str) -> Option<String> {
    let needle = format!("\"{key}\"");
    let mut from = 0;

    while let Some(at) = json[from..].find(&needle) {
        let after = from + at + needle.len();
        from = after;

        let rest = json[after..].trim_start();
        if !rest.starts_with(':') {
            continue;
        }
        let rest = rest[1..].trim_start();
        if !rest.starts_with('"') {
            continue;
        }

        if let Some(value) = read_json_string(&rest[1..])
            && !value.is_empty()
        {
            return Some(value);
        }
    }

    None
}

/// First string element of `"key": [ ... ]`.
fn json_first_array_string(json: &str, key: &str) -> Option<String> {
    let needle = format!("\"{key}\"");
    let at = json.find(&needle)?;

    let rest = json[at + needle.len()..].trim_start();
    let rest = rest.strip_prefix(':')?.trim_start();
    let rest = rest.strip_prefix('[')?.trim_start();

    // An empty array, or one whose first element is not a string.
    let rest = rest.strip_prefix('"')?;
    read_json_string(rest)
}

/// Read up to the closing quote, honouring backslash escapes.
fn read_json_string(rest: &str) -> Option<String> {
    let mut out = String::new();
    let mut chars = rest.chars();

    while let Some(c) = chars.next() {
        match c {
            '"' => return Some(out),
            '\\' => match chars.next() {
                Some('n') => out.push('\n'),
                Some('r') => out.push('\r'),
                Some('t') => out.push('\t'),
                Some(escaped) => out.push(escaped),
                None => return None,
            },
            _ => out.push(c),
        }
    }

    None
}

/// Identifiers the generated data module uses for its own bookkeeping, so a
/// recorded variable must never be allowed to claim one.
pub const RESERVED_IDENTS: &[&str] = &["__case"];

/// Make `candidate` unique against `taken` (and the reserved names) by
/// suffixing, rather than dropping the colliding field. Losing a workbook
/// column silently is worse than emitting one nobody references.
pub fn unique_ident(candidate: &str, taken: &[String]) -> String {
    let mut name = candidate.to_string();
    let mut n = 2;
    while RESERVED_IDENTS.contains(&name.as_str()) || taken.iter().any(|t| t == &name) {
        name = format!("{candidate}_{n}");
        n += 1;
    }
    name
}

/// Turn a recorder variable name into a safe TypeScript identifier.
pub fn sanitize_ident(name: &str) -> String {
    let mut out: String = name
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() { c } else { '_' })
        .collect();
    if out.chars().next().is_some_and(|c| c.is_ascii_digit()) {
        out.insert(0, '_');
    }
    if out.is_empty() {
        out.push('_');
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::recording;

    fn lower_nodes_xml(inner: &str) -> TestCase {
        let doc = format!(
            r#"<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                 <Name>T</Name><RootScope><Children>{inner}</Children></RootScope></Recording>"#
        );
        lower(&recording::parse(&doc).unwrap())
    }

    fn actions(inner: &str) -> Vec<Action> {
        lower_nodes_xml(inner).actions
    }

    #[test]
    fn maps_menu_item_navigation() {
        let a = actions(
            r#"<Node i:type="MenuItemUserAction"><MenuItemName>purchtablelistpage</MenuItemName>
               <MenuItemType>Display</MenuItemType></Node>"#,
        );
        assert_eq!(
            a[0],
            Action::Navigate {
                menu_item: "purchtablelistpage".into(),
                kind: MenuItemKind::Display
            }
        );
    }

    /// The recorder puts the verb in `CommandName` and the target in
    /// `ControlName`. Reading it the other way round emits a suite that hunts
    /// for a button labelled "Click".
    #[test]
    fn a_click_command_targets_the_control_not_the_verb() {
        let a = actions(
            r#"<Node i:type="CommandUserAction"><CommandName>Click</CommandName>
               <ControlName>PurchCopyJournalHeader</ControlName>
               <ControlType>MenuItemButton</ControlType></Node>"#,
        );
        assert_eq!(
            a[0],
            Action::Click {
                control: "PurchCopyJournalHeader".into(),
                control_type: "MenuItemButton".into()
            }
        );
    }

    #[test]
    fn field_entry_becomes_a_parameter_defaulting_to_the_recorded_value() {
        let case = lower_nodes_xml(
            r#"<Node i:type="PropertyUserAction"><PropertyName>Value</PropertyName>
               <ControlName>PurchParmTable_Num</ControlName><ControlType>Input</ControlType>
               <UserActionType>Input</UserActionType><Value>123</Value></Node>"#,
        );

        assert_eq!(
            case.actions[0],
            Action::SetValue {
                control: "PurchParmTable_Num".into(),
                control_type: "Input".into(),
                value: Value::Variable {
                    name: "PurchParmTable_Num".into()
                }
            }
        );
        assert_eq!(
            case.variables,
            vec![Variable {
                name: "PurchParmTable_Num".into(),
                default: "123".into()
            }]
        );
    }

    /// The filter a user typed lives in a JSON command argument, not in a
    /// property. Left in the property bag it reads as a field edit and emits
    /// `setField(control, '[{"Capability":null,...}]')`.
    #[test]
    fn a_filter_command_unpacks_its_json_argument() {
        let case = lower_nodes_xml(
            r#"<Node i:type="CommandUserAction">
                 <Arguments><CommandArgument><Value>[{"Capability":{"FieldLabel":"Purchase order","FieldName":""},"FieldName":"PurchId","Operator":"Is","Values":["003643"]}]</Value></CommandArgument></Arguments>
                 <CommandName>ApplyFiltersForTaskRecorder</CommandName>
                 <ControlName>SystemDefinedFilterManager</ControlName>
                 <ControlType>FilterManager</ControlType></Node>"#,
        );

        assert_eq!(
            case.actions[0],
            Action::Filter {
                control: "SystemDefinedFilterManager".into(),
                field: "PurchId".into(),
                label: "Purchase order".into(),
                operator: "Is".into(),
                value: Value::Variable {
                    name: "PurchId".into()
                }
            }
        );
        assert_eq!(case.variables[0].default, "003643");
    }

    #[test]
    fn grid_commands_carry_the_list_and_the_row() {
        let a = actions(
            r#"<Node i:type="CommandUserAction">
                 <Arguments><CommandArgument><Value>3</Value></CommandArgument></Arguments>
                 <CommandName>ChangeSelectedIndexInCache</CommandName>
                 <ListContext>Grid</ListContext><ControlName>Grid</ControlName>
                 <ControlType>Grid</ControlType></Node>"#,
        );
        assert_eq!(
            a[0],
            Action::SelectRow {
                grid: "Grid".into(),
                row: 3
            }
        );
    }

    /// A step group is the user's own annotation and becomes a `test.step`;
    /// the private scopes the client wraps around its internals are lifted
    /// away, or a handful of real actions end up buried several `test.step`
    /// blocks deep.
    #[test]
    fn step_groups_are_kept_and_private_plumbing_scopes_are_flattened() {
        let a = actions(
            r#"<Node i:type="Scope">
                 <Description>Create the order</Description>
                 <IsForm>false</IsForm><IsStepGroup>true</IsStepGroup>
                 <Children>
                   <Node i:type="Scope">
                     <IsForm>false</IsForm><IsStepGroup>false</IsStepGroup>
                     <Name>SystemDefinedFilterManager_GetFilters</Name><ScopeType>Private</ScopeType>
                     <Children>
                       <Node i:type="CommandUserAction"><CommandName>Click</CommandName>
                         <ControlName>OkButton</ControlName><ControlType>CommandButton</ControlType></Node>
                     </Children>
                   </Node>
                 </Children>
               </Node>"#,
        );

        match &a[0] {
            Action::Step { label, children } => {
                assert_eq!(label, "Create the order");
                assert_eq!(children.len(), 1, "the private scope should be lifted away");
                assert!(matches!(children[0], Action::Click { .. }));
            }
            other => panic!("expected a step, got {other:?}"),
        }
    }

    /// The client groups its own work the same way the user does. Every
    /// lookup it opens becomes a private `<control>_RequestPopup` step group,
    /// and in real recordings those outnumber the user's own groups entirely.
    #[test]
    fn private_step_groups_are_the_clients_own_and_get_flattened() {
        let a = actions(
            r#"<Node i:type="Scope">
                 <IsForm>false</IsForm><IsStepGroup>true</IsStepGroup>
                 <Name>CompanyLookup_RequestPopup</Name><ScopeType>Private</ScopeType>
                 <Children>
                   <Node i:type="CommandUserAction"><CommandName>RequestPopup</CommandName>
                     <ControlName>CompanyLookup</ControlName><ControlType>Input</ControlType></Node>
                 </Children>
               </Node>"#,
        );

        assert_eq!(
            a,
            vec![Action::OpenLookup {
                control: "CompanyLookup".into()
            }]
        );
    }

    /// Task Recorder records inside the client it is recording, so its own
    /// pane appears as a form scope around ordinary actions.
    #[test]
    fn the_recorders_own_pane_is_not_treated_as_a_form() {
        let a = actions(
            r#"<Node i:type="Scope">
                 <IsForm>true</IsForm><IsStepGroup>false</IsStepGroup>
                 <Name>SysBPMPane</Name><ScopeType>Public</ScopeType>
                 <Children>
                   <Node i:type="CommandUserAction"><CommandName>TabShown</CommandName>
                     <ControlName>PurchOrder</ControlName><ControlType>AppBarTab</ControlType></Node>
                 </Children>
               </Node>"#,
        );

        assert_eq!(
            a,
            vec![Action::Tab {
                control: "PurchOrder".into()
            }]
        );
    }

    #[test]
    fn unknown_commands_are_reported_by_their_verb() {
        let a = actions(
            r#"<Node i:type="CommandUserAction"><CommandName>SelectForAdd</CommandName>
               <ControlName>Grid</ControlName><ControlType>Grid</ControlType></Node>"#,
        );
        match &a[0] {
            Action::Unsupported {
                raw_kind, props, ..
            } => {
                assert_eq!(raw_kind, "CommandUserAction:SelectForAdd");
                // The full bag is what the conversion report shows, so a rule
                // for this verb can be written without reopening the XML.
                assert_eq!(props.get("ControlType").map(String::as_str), Some("Grid"));
            }
            other => panic!("expected Unsupported, got {other:?}"),
        }
    }

    /// A tree selection is recorded as a path in a command argument, the way
    /// the tree renders it, and the value the user picked is test data like
    /// any other recorded input.
    #[test]
    fn a_tree_selection_carries_its_path() {
        let case = lower_nodes_xml(
            r#"<Node i:type="CommandUserAction">
                 <Arguments>
                   <CommandArgument><Value>ALL (ALL)\Adventure Works (Adventure Works)</Value></CommandArgument>
                   <CommandArgument><Value>1</Value></CommandArgument>
                 </Arguments>
                 <CommandName>SelectionPathChanged</CommandName>
                 <ControlName>ctrlFormTree</ControlName><ControlType>Tree</ControlType></Node>"#,
        );

        assert_eq!(
            case.actions[0],
            Action::SelectTreeItem {
                control: "ctrlFormTree".into(),
                path: Value::Variable {
                    name: "ctrlFormTree".into()
                }
            }
        );
        assert_eq!(
            case.variables[0].default,
            r"ALL (ALL)\Adventure Works (Adventure Works)"
        );
    }

    /// The shortcut name is the whole instruction. Without it there is nothing
    /// to replay, so it degrades rather than emitting a nameless call.
    #[test]
    fn a_named_shortcut_is_mapped_and_a_nameless_one_is_not() {
        let mapped = actions(
            r#"<Node i:type="CommandUserAction">
                 <Arguments><CommandArgument><Value>ViewEdit</Value></CommandArgument></Arguments>
                 <CommandName>ExecuteShortcuts</CommandName><ControlName></ControlName></Node>"#,
        );
        assert_eq!(
            mapped[0],
            Action::Shortcut {
                name: "ViewEdit".into()
            }
        );

        let bare = actions(
            r#"<Node i:type="CommandUserAction"><CommandName>ExecuteShortcuts</CommandName></Node>"#,
        );
        assert!(matches!(bare[0], Action::Unsupported { .. }));
    }

    /// A note is the one thing in a recording that was written in prose, on
    /// purpose. Dropping it loses the only instruction a human left behind.
    #[test]
    fn a_recorded_note_survives_as_a_comment() {
        let a = actions(
            r#"<Node i:type="InfoUserAction"><Description>Note.</Description>
                 <Notes>Check the open period.</Notes><Text>Check it</Text></Node>
               <Node i:type="AnnotationUserAction"><Description>Annotated step.</Description></Node>"#,
        );

        assert_eq!(
            a,
            vec![
                Action::Marker {
                    text: "Check the open period.".into()
                },
                Action::Marker {
                    text: "Annotated step.".into()
                }
            ]
        );
    }

    /// Microsoft's CDM schema for the task recorder tables
    /// (`SysTaskRecorderNode*`) lists node types that none of the real
    /// recordings available to test against contain: a recorded note, a
    /// validation, a form open, and a bare annotation. They have to degrade
    /// safely - mapped, or named in the report - rather than vanish, because
    /// the first recording someone converts may well be the one that has them.
    #[test]
    fn node_types_no_sample_recording_contains_still_degrade_honestly() {
        let a = actions(
            r#"<Node i:type="InfoUserAction"><Description>Note.</Description>
                 <Notes>Check the posting profile.</Notes><Text>Check it</Text></Node>
               <Node i:type="ValidationUserAction"><Name>ValidateCustAccount</Name>
                 <ControlName>SalesTable_CustAccount</ControlName><ControlType>Input</ControlType></Node>
               <Node i:type="FormUserAction"><ControlLabel>All sales orders</ControlLabel>
                 <FormId>123_SalesTableListPage_abc</FormId></Node>
               <Node i:type="AnnotationUserAction"><Description>Annotated step.</Description></Node>"#,
        );

        assert_eq!(a.len(), 4, "no node may be silently dropped");

        // A note becomes a comment, and a validation maps: its expected value
        // is test data, which is where RSAT keeps it too.
        assert!(matches!(&a[0], Action::Marker { .. }));
        assert!(matches!(
            &a[1],
            Action::Validate { control, .. } if control == "SalesTable_CustAccount"
        ));
        assert!(matches!(&a[3], Action::Marker { .. }));

        // A form open is the one left: the schema gives it no open/close
        // discriminator, and no recording to hand contains one, so there is
        // nothing to derive a rule from. It degrades, carrying its properties.
        match &a[2] {
            Action::Unsupported { raw_kind, props, .. } => {
                assert_eq!(raw_kind, "FormUserAction");
                assert!(!props.is_empty(), "FormUserAction lost its properties");
            }
            other => panic!("expected FormUserAction to degrade, got {other:?}"),
        }
    }

    #[test]
    fn colliding_variable_names_get_distinct_identifiers() {
        let doc = r#"<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
             <Name>T</Name>
             <Variables>
               <AxTaskRecordingVariable><Name>Customer name</Name><Value>a</Value></AxTaskRecordingVariable>
               <AxTaskRecordingVariable><Name>Customer-name</Name><Value>b</Value></AxTaskRecordingVariable>
               <AxTaskRecordingVariable><Name>__case</Name><Value>c</Value></AxTaskRecordingVariable>
             </Variables>
             <RootScope><Children/></RootScope></Recording>"#;
        let names: Vec<String> = lower(&recording::parse(doc).unwrap())
            .variables
            .into_iter()
            .map(|v| v.name)
            .collect();
        assert_eq!(names, vec!["Customer_name", "Customer_name_2", "__case_2"]);
    }

    #[test]
    fn json_scanner_prefers_the_first_non_empty_match() {
        let json = r#"[{"Capability":{"FieldName":""},"FieldName":"PurchId","Values":["003643"]}]"#;
        assert_eq!(json_string(json, "FieldName").as_deref(), Some("PurchId"));
        assert_eq!(
            json_first_array_string(json, "Values").as_deref(),
            Some("003643")
        );
        assert_eq!(json_string(json, "Missing"), None);
    }
}
