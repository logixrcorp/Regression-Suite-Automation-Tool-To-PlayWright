//! Reads an `.axtr` archive (or a bare recording `.xml`) into a loose node
//! tree: a kind discriminator, a property bag, and children.

use crate::xml::{self, Element};
use anyhow::{anyhow, Context, Result};
use std::collections::BTreeMap;
use std::io::Read;
use std::path::Path;

/// Wrapper elements that hold child nodes. Task Recorder has used several
/// spellings over the years - including the famously non-English `Childs`.
const CHILD_WRAPPERS: &[&str] = &["Childs", "Children", "Nodes", "ChildNodes", "Steps"];

/// Elements that are containers, not actions in their own right.
const NODE_ELEMENTS: &[&str] = &[
    "AxTaskRecordingNode",
    "Node",
    "UserAction",
    "TaskUserActionNode",
    "AxTaskRecordingUserActionNode",
];

#[derive(Debug, Clone, PartialEq)]
pub struct RecNode {
    /// The `i:type` discriminator where present, else the element name.
    pub kind: String,
    pub props: BTreeMap<String, String>,
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
        .or_else(|| doc.text_of("RecordingName"))
        .unwrap_or_else(|| "Recording".to_string());

    let variables = collect_variables(&doc);

    let root_container = CHILD_WRAPPERS
        .iter()
        .find_map(|w| doc.child(w))
        .unwrap_or(&doc);

    let nodes = root_container
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

fn is_node_element(el: &Element) -> bool {
    NODE_ELEMENTS.iter().any(|n| el.name.eq_ignore_ascii_case(n))
        || el.name.to_ascii_lowercase().contains("node")
        || el.name.to_ascii_lowercase().contains("useraction")
}

fn node_from(el: &Element) -> RecNode {
    let kind = el
        .attr("type")
        .map(str::to_string)
        .or_else(|| el.text_of("ActionType"))
        .or_else(|| el.text_of("Type"))
        .unwrap_or_else(|| el.name.clone());

    let mut props = BTreeMap::new();
    let mut children = Vec::new();
    flatten(el, &mut props, &mut children);

    RecNode {
        kind,
        props,
        children,
    }
}

/// Pull scalar descendants into the property bag and node-ish descendants into
/// `children`, transparently stepping through wrapper elements.
fn flatten(el: &Element, props: &mut BTreeMap<String, String>, children: &mut Vec<RecNode>) {
    for child in &el.children {
        if CHILD_WRAPPERS.iter().any(|w| child.name.eq_ignore_ascii_case(w)) {
            for grand in &child.children {
                if is_node_element(grand) {
                    children.push(node_from(grand));
                } else {
                    flatten(grand, props, children);
                }
            }
        } else if is_node_element(child) && !child.is_scalar() {
            children.push(node_from(child));
        } else if child.is_scalar() {
            let text = child.text.trim();
            if !text.is_empty() {
                props.entry(child.name.clone()).or_insert_with(|| text.to_string());
            }
        } else {
            // An unrecognized grouping element: keep descending so we do not
            // silently lose the actions underneath it.
            flatten(child, props, children);
        }
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
        let lower = el.name.to_ascii_lowercase();
        if lower.contains("variable") && !lower.ends_with("variables") {
            f(el);
            return;
        }
        for c in &el.children {
            walk(c, f);
        }
    }

    walk(doc, &mut visit);
    out.sort();
    out.dedup_by(|a, b| a.0 == b.0);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = r#"
    <AxTaskRecording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
      <Name>Create customer</Name>
      <Variables>
        <AxTaskRecordingVariable><Name>CustomerName</Name><Value>Contoso</Value></AxTaskRecordingVariable>
      </Variables>
      <Nodes>
        <AxTaskRecordingNode i:type="TaskUserActionGroup">
          <Annotation>Open the customers list</Annotation>
          <Childs>
            <AxTaskRecordingNode i:type="MenuItemUserAction">
              <MenuItemName>CustTableListPage</MenuItemName>
              <MenuItemType>Display</MenuItemType>
            </AxTaskRecordingNode>
          </Childs>
        </AxTaskRecordingNode>
      </Nodes>
    </AxTaskRecording>"#;

    #[test]
    fn reads_name_variables_and_nested_nodes() {
        let rec = parse(SAMPLE).unwrap();
        assert_eq!(rec.name, "Create customer");
        assert_eq!(rec.variables, vec![("CustomerName".into(), "Contoso".into())]);
        assert_eq!(rec.nodes.len(), 1);

        let group = &rec.nodes[0];
        assert_eq!(group.kind, "TaskUserActionGroup");
        assert_eq!(group.prop(&["Annotation"]), Some("Open the customers list"));
        assert_eq!(group.children.len(), 1);
        assert_eq!(group.children[0].prop(&["MenuItemName"]), Some("CustTableListPage"));
    }

    #[test]
    fn tolerates_alternate_wrapper_spellings() {
        let alt = SAMPLE.replace("Childs", "Children").replace("Nodes", "Steps");
        let rec = parse(&alt).unwrap();
        assert_eq!(rec.nodes.len(), 1);
        assert_eq!(rec.nodes[0].children.len(), 1);
    }
}
