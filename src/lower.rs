//! Lowering: `RecNode` -> `ir::Action`.
//!
//! All the schema guesswork lives here, in one editable table. When a node
//! does not match anything we emit `Unsupported` rather than inventing
//! behaviour - a converter that is 85% automatic plus honest gaps beats one
//! that silently emits wrong code.

use crate::ir::{Action, MenuItemKind, TestCase, Value, Variable};
use crate::recording::{RecNode, Recording};

const CONTROL_KEYS: &[&str] = &["ControlName", "Control", "TargetControl", "ControlId"];
const FORM_KEYS: &[&str] = &["FormName", "Form", "TargetForm"];
const VALUE_KEYS: &[&str] = &["Value", "NewValue", "Text", "InputValue"];
const VARIABLE_KEYS: &[&str] = &["VariableName", "Variable", "ParameterName"];
const ANNOTATION_KEYS: &[&str] = &["Annotation", "Description", "Caption", "Label", "Name"];

pub fn lower(rec: &Recording) -> TestCase {
    let actions: Vec<Action> = rec.nodes.iter().map(lower_node).collect();

    // Two recorder variables can sanitize to the same identifier ("Customer
    // name" and "Customer-name" both become `Customer_name`), which would emit
    // a duplicate key in the generated `Params` type and fail to compile.
    let mut taken: Vec<String> = Vec::new();
    let mut variables: Vec<Variable> = rec
        .variables
        .iter()
        .map(|(name, default)| {
            let name = unique_ident(&sanitize_ident(name), &taken);
            taken.push(name.clone());
            Variable {
                name,
                default: default.clone(),
            }
        })
        .collect();

    // A recording can reference a variable that never made it into the
    // <Variables> block. Declaring it anyway keeps the generated data module
    // and the generated spec in agreement, so the output always compiles.
    let mut referenced = Vec::new();
    collect_referenced(&actions, &mut referenced);
    for name in referenced {
        if !variables.iter().any(|v| v.name == name) {
            variables.push(Variable {
                name,
                default: String::new(),
            });
        }
    }

    TestCase {
        name: rec.name.clone(),
        variables,
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
            | Action::Lookup { value, .. } => note(value, out),
            Action::Validate { expected, .. } => note(expected, out),
            Action::Step { children, .. } | Action::Dialog { children, .. } => {
                collect_referenced(children, out)
            }
            _ => {}
        }
    }
}

fn value_of(node: &RecNode) -> Value {
    if let Some(var) = node.prop(VARIABLE_KEYS)
        && !var.is_empty()
    {
        return Value::Variable {
            name: sanitize_ident(var),
        };
    }
    Value::Literal {
        text: node.prop(VALUE_KEYS).unwrap_or_default().to_string(),
    }
}

fn lower_children(node: &RecNode) -> Vec<Action> {
    node.children.iter().map(lower_node).collect()
}

fn lower_node(node: &RecNode) -> Action {
    let kind = node.kind.to_ascii_lowercase();
    let control = node.prop(CONTROL_KEYS).unwrap_or_default().to_string();

    // --- grouping ------------------------------------------------------
    if !node.children.is_empty()
        && (kind.contains("group") || kind.contains("task") || kind.contains("scope"))
    {
        return Action::Step {
            label: node
                .prop(ANNOTATION_KEYS)
                .unwrap_or("Recorded step")
                .to_string(),
            children: lower_children(node),
        };
    }

    // An empty dialog node carries no actions to scope, so it falls through to
    // the ordinary mapping below rather than emitting a pointless scope.
    if (kind.contains("dialog") || kind.contains("slider")) && !node.children.is_empty() {
        return Action::Dialog {
            name: node.prop(ANNOTATION_KEYS).unwrap_or("Dialog").to_string(),
            children: lower_children(node),
        };
    }

    // --- navigation ----------------------------------------------------
    if kind.contains("menuitem") || node.has_prop(&["MenuItemName"]) {
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
        return Action::Navigate { menu_item, kind };
    }

    if kind.contains("form") {
        let form = node.prop(FORM_KEYS).unwrap_or_default().to_string();
        let action_type = node
            .prop(&["ActionType", "FormAction", "State"])
            .unwrap_or("Open")
            .to_ascii_lowercase();
        return if action_type.contains("close") {
            Action::LeaveForm { form }
        } else {
            Action::EnterForm { form }
        };
    }

    // --- data entry ----------------------------------------------------
    if node.has_prop(&["GridName"]) || node.has_prop(&["ColumnName"]) {
        return Action::SetGridValue {
            grid: node
                .prop(&["GridName", "Grid"])
                .unwrap_or(control.as_str())
                .to_string(),
            column: node
                .prop(&["ColumnName", "Column", "FieldName"])
                .unwrap_or_default()
                .to_string(),
            row: node
                .prop(&["RowIndex", "Row"])
                .and_then(|r| r.parse().ok())
                .unwrap_or(0),
            value: value_of(node),
        };
    }

    if kind.contains("lookup") || kind.contains("segmentedentry") {
        return Action::Lookup {
            control,
            value: value_of(node),
        };
    }

    if kind.contains("validat") || kind.contains("verif") || kind.contains("assert") {
        return Action::Validate {
            control,
            expected: value_of(node),
        };
    }

    if kind.contains("input") || (!control.is_empty() && node.has_prop(VALUE_KEYS)) {
        return Action::SetValue {
            control,
            value: value_of(node),
        };
    }

    // --- commands and clicks -------------------------------------------
    if kind.contains("command") || node.has_prop(&["CommandName"]) {
        let name = node
            .prop(&["CommandName", "Command"])
            .unwrap_or(control.as_str())
            .to_string();
        return Action::Command { name };
    }

    if !control.is_empty()
        && (kind.contains("control") || kind.contains("click") || kind.contains("button"))
    {
        return Action::Click { control };
    }

    // --- give up loudly, never quietly ---------------------------------
    Action::Unsupported {
        raw_kind: node.kind.clone(),
        detail: node.describe(),
        props: node.props.clone(),
    }
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

    fn lower_xml(inner: &str) -> Vec<Action> {
        let doc = format!(
            r#"<AxTaskRecording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                 <Name>T</Name><Nodes>{inner}</Nodes></AxTaskRecording>"#
        );
        lower(&recording::parse(&doc).unwrap()).actions
    }

    #[test]
    fn maps_menu_item_navigation() {
        let a = lower_xml(
            r#"<Node i:type="MenuItemUserAction"><MenuItemName>CustTableListPage</MenuItemName>
               <MenuItemType>Display</MenuItemType></Node>"#,
        );
        assert_eq!(
            a[0],
            Action::Navigate {
                menu_item: "CustTableListPage".into(),
                kind: MenuItemKind::Display
            }
        );
    }

    #[test]
    fn variable_bound_input_becomes_a_parameter_not_a_literal() {
        let a = lower_xml(
            r#"<Node i:type="InputUserAction"><ControlName>CustAccount</ControlName>
               <Value>US-001</Value><VariableName>Customer account</VariableName></Node>"#,
        );
        assert_eq!(
            a[0],
            Action::SetValue {
                control: "CustAccount".into(),
                value: Value::Variable {
                    name: "Customer_account".into()
                }
            }
        );
    }

    #[test]
    fn unknown_node_kinds_degrade_to_a_todo() {
        let a = lower_xml(r#"<Node i:type="SomeFutureUserAction"><Mystery>42</Mystery></Node>"#);
        match &a[0] {
            Action::Unsupported {
                raw_kind,
                detail,
                props,
            } => {
                assert_eq!(raw_kind, "SomeFutureUserAction");
                assert!(detail.contains("Mystery=42"));
                // The full bag is what the conversion report shows, so a rule
                // for this kind can be written without reopening the XML.
                assert_eq!(props.get("Mystery").map(String::as_str), Some("42"));
            }
            other => panic!("expected Unsupported, got {other:?}"),
        }
    }

    #[test]
    fn colliding_variable_names_get_distinct_identifiers() {
        let doc = r#"<AxTaskRecording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
             <Name>T</Name>
             <Variables>
               <AxTaskRecordingVariable><Name>Customer name</Name><Value>a</Value></AxTaskRecordingVariable>
               <AxTaskRecordingVariable><Name>Customer-name</Name><Value>b</Value></AxTaskRecordingVariable>
               <AxTaskRecordingVariable><Name>__case</Name><Value>c</Value></AxTaskRecordingVariable>
             </Variables>
             <Nodes/></AxTaskRecording>"#;
        let names: Vec<String> = lower(&recording::parse(doc).unwrap())
            .variables
            .into_iter()
            .map(|v| v.name)
            .collect();
        assert_eq!(names, vec!["Customer_name", "Customer_name_2", "__case_2"]);
    }

    #[test]
    fn grid_actions_keep_column_and_row() {
        let a = lower_xml(
            r#"<Node i:type="InputUserAction"><GridName>Lines</GridName>
               <ColumnName>ItemId</ColumnName><RowIndex>2</RowIndex><Value>D0001</Value></Node>"#,
        );
        assert_eq!(
            a[0],
            Action::SetGridValue {
                grid: "Lines".into(),
                column: "ItemId".into(),
                row: 2,
                value: Value::Literal {
                    text: "D0001".into()
                }
            }
        );
    }
}
