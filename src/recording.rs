//! Reads an `.axtr` archive (or a bare recording `.xml`) into a loose node
//! tree: a kind discriminator, a property bag, command arguments, annotations
//! and children.
//!
//! The shape this targets is the real one. A Task Recorder export is a
//! `DataContract` serialization of `Microsoft.Dynamics.Client.ServerForm.TaskRecording`:
//!
//! ```xml
//! <Recording>
//!   <Name>Confirm purchase order</Name>
//!   <RootScope>
//!     <Children>
//!       <Node i:type="Scope">...<Children>...</Children></Node>
//!     </Children>
//!   </RootScope>
//!   <UserActions>            <!-- object-graph back-references, not actions -->
//!     <anyType z:Ref="i3" />
//!   </UserActions>
//! </Recording>
//! ```
//!
//! Two details there are load-bearing. The action tree hangs off `RootScope`,
//! not off a top-level `Nodes`; and `UserActions` is a list of `z:Ref`
//! pointers into that same tree, so treating it as a second action list yields
//! a recording made entirely of empty nodes.

use crate::xml::{self, Element};
use anyhow::{anyhow, Context, Result};
use std::collections::BTreeMap;
use std::io::Read;
use std::path::Path;

/// Wrapper elements that hold child nodes. Task Recorder has used several
/// spellings over the years - including the famously non-English `Childs`.
const CHILD_WRAPPERS: &[&str] = &["Children", "Childs", "Nodes", "ChildNodes", "Steps"];

/// Elements that are containers, not actions in their own right.
const NODE_ELEMENTS: &[&str] = &[
    "AxTaskRecordingNode",
    "Node",
    "UserAction",
    "TaskUserActionNode",
    "AxTaskRecordingUserActionNode",
];

/// Elements that look node-ish by name but are not actions. `UserActions` is
/// the dangerous one: it is a flat list of `z:Ref` back-references to nodes
/// that already appear under `RootScope`, and its name matches every loose
/// "contains UserAction" test.
const NOT_NODE_ELEMENTS: &[&str] = &[
    "UserActions",
    "Annotations",
    "Annotation",
    "Arguments",
    "CommandArgument",
    "FormContexts",
    "NavigationPath",
    "Variables",
    "CanonicalUserAction",
];

/// An `<Annotation i:type="...">` hanging off a node. Kept out of the property
/// bag on purpose: a `FormAnnotation` carries `MenuItemName`, and flattening
/// that in would turn an ordinary click into a navigation.
#[derive(Debug, Clone, PartialEq)]
pub struct RecAnnotation {
    pub kind: String,
    pub props: BTreeMap<String, String>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct RecNode {
    /// The `i:type` discriminator where present, else the element name.
    pub kind: String,
    pub props: BTreeMap<String, String>,
    /// `<Arguments><CommandArgument><Value>` in order. These stay out of the
    /// property bag because a command argument is positional data (often a
    /// JSON blob), not a property called "Value" - and letting one in makes a
    /// filter command look exactly like a field edit.
    pub args: Vec<String>,
    pub annotations: Vec<RecAnnotation>,
    pub children: Vec<RecNode>,
}

impl RecNode {
    /// Case-insensitive property lookup across several candidate names, since
    /// the same concept is spelled differently by different action types.
    pub fn prop(&self, names: &[&str]) -> Option<&str> {
        names.iter().find_map(|n| {
            self.props
                .iter()
                .find(|(k, _)| k.eq_ignore_ascii_case(n))
                .map(|(_, v)| v.as_str())
        })
    }

    pub fn has_prop(&self, names: &[&str]) -> bool {
        self.prop(names).is_some()
    }

    /// A property that reads as a boolean `true`.
    pub fn flag(&self, name: &str) -> bool {
        self.prop(&[name]).is_some_and(|v| v.eq_ignore_ascii_case("true"))
    }

    pub fn arg(&self, index: usize) -> Option<&str> {
        self.args.get(index).map(String::as_str)
    }

    pub fn describe(&self) -> String {
        let mut parts: Vec<String> = self
            .props
            .iter()
            .filter(|(_, v)| !v.is_empty())
            .map(|(k, v)| format!("{k}={v}"))
            .collect();
        parts.truncate(4);
        parts.join(", ")
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Recording {
    pub name: String,
    pub variables: Vec<(String, String)>,
    pub nodes: Vec<RecNode>,
}

/// Load from an `.axtr` (a zip archive) or a raw recording `.xml`.
pub fn load(path: &Path) -> Result<Recording> {
    let bytes = std::fs::read(path).with_context(|| format!("reading {}", path.display()))?;
    let xml_text = if bytes.starts_with(b"PK") {
        extract_from_archive(&bytes)?
    } else {
        String::from_utf8_lossy(&bytes).into_owned()
    };
    parse(&xml_text)
}

fn extract_from_archive(bytes: &[u8]) -> Result<String> {
    let mut archive = zip::ZipArchive::new(std::io::Cursor::new(bytes))
        .context("opening .axtr archive (it should be a zip)")?;

    // Prefer a file that actually looks like the recording; .axtr archives also
    // carry screenshots and a manifest.
    let mut best: Option<(usize, i32)> = None;
    for i in 0..archive.len() {
        let file = archive.by_index(i)?;
        let name = file.name().to_ascii_lowercase();
        if !name.ends_with(".xml") {
            continue;
        }
        let score = if name.contains("recording") { 2 } else { 1 };
        if best.is_none_or(|(_, s)| score > s) {
            best = Some((i, score));
        }
    }

    let (index, _) = best.ok_or_else(|| anyhow!("no .xml entry found inside the .axtr archive"))?;
    let mut file = archive.by_index(index)?;
    let mut text = String::new();
    file.read_to_string(&mut text)?;
    Ok(text)
}

pub fn parse(xml_text: &str) -> Result<Recording> {
    let doc = xml::parse(xml_text)?;

    let name = doc
        .text_of("Name")
        .or_else(|| doc.text_of("Description"))
        .or_else(|| doc.text_of("RecordingName"))
        .unwrap_or_else(|| "Recording".to_string());

    let variables = collect_variables(&doc);

    let nodes = action_container(&doc)
        .children
        .iter()
        .filter(|c| is_node_element(c))
        .map(node_from)
        .collect();

    Ok(Recording {
        name,
        variables,
        nodes,
    })
}

/// Find the element whose children are the recording's top-level actions.
fn action_container(doc: &Element) -> &Element {
    // The real export: `<RootScope><Children>`. RootScope is itself a scope
    // node, so its own `Children` is the action list.
    if let Some(root_scope) = doc.child("RootScope") {
        return CHILD_WRAPPERS
            .iter()
            .find_map(|w| root_scope.child(w))
            .unwrap_or(root_scope);
    }

    CHILD_WRAPPERS
        .iter()
        .find_map(|w| doc.child(w))
        .unwrap_or(doc)
}

fn is_node_element(el: &Element) -> bool {
    if NOT_NODE_ELEMENTS
        .iter()
        .any(|n| el.name.eq_ignore_ascii_case(n))
    {
        return false;
    }

    let lower = el.name.to_ascii_lowercase();
    NODE_ELEMENTS.iter().any(|n| el.name.eq_ignore_ascii_case(n))
        || lower.contains("node")
        || lower.ends_with("useraction")
}

fn node_from(el: &Element) -> RecNode {
    let kind = el
        .attr("type")
        .map(str::to_string)
        .or_else(|| el.text_of("ActionType"))
        .or_else(|| el.text_of("Type"))
        .unwrap_or_else(|| el.name.clone());

    let mut node = RecNode {
        kind,
        props: BTreeMap::new(),
        args: Vec::new(),
        annotations: Vec::new(),
        children: Vec::new(),
    };

    flatten(el, &mut node);
    node
}

/// Pull scalar descendants into the property bag and node-ish descendants into
/// `children`, transparently stepping through wrapper elements. `Arguments`
/// and `Annotations` are lifted into their own fields instead.
fn flatten(el: &Element, node: &mut RecNode) {
    for child in &el.children {
        if CHILD_WRAPPERS.iter().any(|w| child.name.eq_ignore_ascii_case(w)) {
            for grand in &child.children {
                if is_node_element(grand) {
                    node.children.push(node_from(grand));
                } else {
                    flatten(grand, node);
                }
            }
        } else if child.name.eq_ignore_ascii_case("Arguments") {
            for arg in child.children_named("CommandArgument") {
                node.args
                    .push(arg.text_of("Value").unwrap_or_default());
            }
        } else if child.name.eq_ignore_ascii_case("Annotations") {
            for annotation in child.children_named("Annotation") {
                node.annotations.push(annotation_from(annotation));
            }
        } else if is_node_element(child) && !child.is_scalar() {
            node.children.push(node_from(child));
        } else if child.is_scalar() {
            let text = child.text.trim();
            if !text.is_empty() {
                node.props
                    .entry(child.name.clone())
                    .or_insert_with(|| text.to_string());
            }
        } else if !NOT_NODE_ELEMENTS
            .iter()
            .any(|n| child.name.eq_ignore_ascii_case(n))
        {
            // An unrecognized grouping element: keep descending so we do not
            // silently lose the actions underneath it.
            flatten(child, node);
        }
    }
}

fn annotation_from(el: &Element) -> RecAnnotation {
    let mut props = BTreeMap::new();
    for child in &el.children {
        if child.is_scalar() {
            let text = child.text.trim();
            if !text.is_empty() {
                props.insert(child.name.clone(), text.to_string());
            }
        }
    }

    RecAnnotation {
        kind: el
            .attr("type")
            .map(str::to_string)
            .unwrap_or_else(|| el.name.clone()),
        props,
    }
}

fn collect_variables(doc: &Element) -> Vec<(String, String)> {
    let mut out = Vec::new();
    let mut visit = |el: &Element| {
        let name = el
            .text_of("Name")
            .or_else(|| el.text_of("VariableName"))
            .or_else(|| el.attr("Name").map(str::to_string));
        if let Some(name) = name {
            let value = el
                .text_of("Value")
                .or_else(|| el.text_of("DefaultValue"))
                .unwrap_or_default();
            out.push((name, value));
        }
    };

    fn walk(el: &Element, f: &mut impl FnMut(&Element)) {
        if el.name.eq_ignore_ascii_case("Variables") {
            for child in &el.children {
                f(child);
            }
            return;
        }
        for child in &el.children {
            walk(child, f);
        }
    }

    walk(doc, &mut visit);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    const REAL_SHAPE: &str = r#"<?xml version="1.0" encoding="utf-8"?>
<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
           xmlns="http://schemas.datacontract.org/2004/07/Microsoft.Dynamics.Client.ServerForm.TaskRecording">
  <Name>Confirm purchase order</Name>
  <RootScope z:Id="i1" xmlns:z="http://schemas.microsoft.com/2003/10/Serialization/">
    <Parent i:nil="true" />
    <Children>
      <Node z:Id="i2" i:type="Scope">
        <Children>
          <Node z:Id="i3" i:type="CommandUserAction">
            <Description>Click From journal.</Description>
            <Annotations>
              <Annotation i:type="FormAnnotation">
                <MenuItemName>SysBPMPane</MenuItemName>
              </Annotation>
            </Annotations>
            <Arguments>
              <CommandArgument><IsReference>false</IsReference><Value>[{"FieldName":"PurchId"}]</Value></CommandArgument>
              <CommandArgument><IsReference>false</IsReference><Value>1</Value></CommandArgument>
            </Arguments>
            <CommandName>Click</CommandName>
            <ControlName>PurchCopyJournalHeader</ControlName>
            <ControlType>MenuItemButton</ControlType>
          </Node>
        </Children>
        <IsForm>false</IsForm>
        <IsStepGroup>true</IsStepGroup>
        <Name>Copy from journal</Name>
      </Node>
    </Children>
  </RootScope>
  <UserActions xmlns:d2p1="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
    <d2p1:anyType z:Ref="i3" xmlns:z="http://schemas.microsoft.com/2003/10/Serialization/" />
  </UserActions>
  <Version>1</Version>
</Recording>"#;

    /// The action tree hangs off `RootScope`, and `UserActions` is a list of
    /// back-references to nodes already in it. Reading the latter as the
    /// action list is how a real recording converts to nothing at all.
    #[test]
    fn reads_the_action_tree_from_root_scope_and_ignores_user_actions() {
        let rec = parse(REAL_SHAPE).unwrap();

        assert_eq!(rec.name, "Confirm purchase order");
        assert_eq!(rec.nodes.len(), 1, "UserActions must not become a node");

        let scope = &rec.nodes[0];
        assert_eq!(scope.kind, "Scope");
        assert!(scope.flag("IsStepGroup"));
        assert_eq!(scope.children.len(), 1);
    }

    /// A command argument is positional data, frequently a JSON blob. Letting
    /// it into the property bag as "Value" makes a filter command
    /// indistinguishable from a field edit.
    #[test]
    fn command_arguments_stay_out_of_the_property_bag() {
        let node = &parse(REAL_SHAPE).unwrap().nodes[0].children[0];

        assert_eq!(node.prop(&["Value"]), None);
        assert_eq!(node.arg(0), Some(r#"[{"FieldName":"PurchId"}]"#));
        assert_eq!(node.arg(1), Some("1"));
    }

    /// A `FormAnnotation` carries `MenuItemName`. Flattened into the property
    /// bag it would turn this click into a navigation.
    #[test]
    fn annotations_stay_out_of_the_property_bag() {
        let node = &parse(REAL_SHAPE).unwrap().nodes[0].children[0];

        assert_eq!(node.prop(&["MenuItemName"]), None);
        assert_eq!(node.annotations.len(), 1);
        assert_eq!(node.annotations[0].kind, "FormAnnotation");
        assert_eq!(
            node.annotations[0].props.get("MenuItemName").map(String::as_str),
            Some("SysBPMPane")
        );
    }
}
