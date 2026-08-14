//! A tiny, schema-tolerant XML tree.
//!
//! Task Recorder's `AxTaskRecording` XML has drifted between platform updates
//! (element names, wrapper nodes and the `i:type` discriminator have all moved
//! around). Rather than bind a rigid `serde` model to one snapshot of the
//! schema, we parse into a generic tree and interpret it in `lower.rs`. Unknown
//! shapes then degrade into a `TODO` in the generated test instead of a hard
//! parse failure.

use anyhow::{anyhow, Result};
use quick_xml::events::Event;
use quick_xml::Reader;

#[derive(Debug, Clone, Default, PartialEq)]
pub struct Element {
    /// Local name, namespace prefix stripped (`i:type` -> `type`).
    pub name: String,
    pub attrs: Vec<(String, String)>,
    pub text: String,
    pub children: Vec<Element>,
}

/// Recordings are UTF-8, so we decode lossily and resolve entities ourselves.
/// This sidesteps quick-xml's decoder API, which shifts between releases and is
/// feature-gated differently once another crate (calamine) unifies features.
fn unescape_utf8(bytes: &[u8]) -> String {
    let raw = String::from_utf8_lossy(bytes);
    match quick_xml::escape::unescape(&raw) {
        Ok(s) => s.into_owned(),
        Err(_) => raw.into_owned(),
    }
}

fn local(name: &str) -> &str {
    match name.rfind(':') {
        Some(i) => &name[i + 1..],
        None => name,
    }
}

impl Element {
    pub fn attr(&self, name: &str) -> Option<&str> {
        self.attrs
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }

    pub fn child(&self, name: &str) -> Option<&Element> {
        self.children.iter().find(|c| c.name.eq_ignore_ascii_case(name))
    }

    pub fn children_named<'a>(&'a self, name: &'a str) -> impl Iterator<Item = &'a Element> + 'a {
        self.children
            .iter()
            .filter(move |c| c.name.eq_ignore_ascii_case(name))
    }

    /// Trimmed text of a direct child element, if non-empty.
    pub fn text_of(&self, name: &str) -> Option<String> {
        self.child(name)
            .map(|c| c.text.trim().to_string())
            .filter(|s| !s.is_empty())
    }

    /// First element anywhere in the subtree with this name.
    pub fn find_descendant(&self, name: &str) -> Option<&Element> {
        if self.name.eq_ignore_ascii_case(name) {
            return Some(self);
        }
        self.children.iter().find_map(|c| c.find_descendant(name))
    }

    /// True when this element carries no element children (i.e. it is a leaf
    /// scalar we can flatten into a property bag).
    pub fn is_scalar(&self) -> bool {
        self.children.is_empty()
    }
}

pub fn parse(xml: &str) -> Result<Element> {
    // Deliberately NOT trimming at the reader level: quick-xml splits text
    // around entity references, and trimming each fragment would silently eat
    // the spaces around them ("Contoso & Sons" -> "Contoso&Sons"). We trim once
    // at the point of use instead.
    let reader = &mut Reader::from_str(xml);

    let mut stack: Vec<Element> = Vec::new();
    let mut root: Option<Element> = None;

    loop {
        match reader.read_event()? {
            Event::Start(e) => stack.push(element_from(&e)?),
            Event::Empty(e) => {
                let el = element_from(&e)?;
                push(&mut stack, &mut root, el);
            }
            Event::Text(e) => {
                let t = unescape_utf8(e.as_ref());
                if let Some(parent) = stack.last_mut() {
                    parent.text.push_str(&t);
                }
            }
            // quick-xml surfaces entity references as their own event, so
            // `&amp;` inside element text never reaches the Text branch.
            Event::GeneralRef(e) => {
                let name = String::from_utf8_lossy(e.as_ref()).into_owned();
                let resolved = quick_xml::escape::resolve_predefined_entity(&name)
                    .map(str::to_string)
                    .unwrap_or_else(|| format!("&{name};"));
                if let Some(parent) = stack.last_mut() {
                    parent.text.push_str(&resolved);
                }
            }
            Event::CData(e) => {
                let t = String::from_utf8_lossy(e.as_ref()).into_owned();
                if let Some(parent) = stack.last_mut() {
                    parent.text.push_str(&t);
                }
            }
            Event::End(_) => {
                if let Some(el) = stack.pop() {
                    push(&mut stack, &mut root, el);
                }
            }
            Event::Eof => break,
            _ => {}
        }
    }

    root.ok_or_else(|| anyhow!("document contained no root element"))
}

fn push(stack: &mut [Element], root: &mut Option<Element>, el: Element) {
    match stack.last_mut() {
        Some(parent) => parent.children.push(el),
        None => *root = Some(el),
    }
}

fn element_from(e: &quick_xml::events::BytesStart<'_>) -> Result<Element> {
    let name = String::from_utf8_lossy(e.name().as_ref()).into_owned();
    let mut attrs = Vec::new();
    for a in e.attributes() {
        let a = a?;
        let key = String::from_utf8_lossy(a.key.as_ref()).into_owned();
        let value = unescape_utf8(a.value.as_ref());
        attrs.push((local(&key).to_string(), value));
    }
    Ok(Element {
        name: local(&name).to_string(),
        attrs,
        text: String::new(),
        children: Vec::new(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_nested_elements_and_strips_prefixes() {
        let doc = parse(
            r#"<AxTaskRecording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                 <Name>Create customer</Name>
                 <Nodes>
                   <Node i:type="InputUserAction"><ControlName>CustAccount</ControlName></Node>
                 </Nodes>
               </AxTaskRecording>"#,
        )
        .unwrap();

        assert_eq!(doc.name, "AxTaskRecording");
        assert_eq!(doc.text_of("Name").as_deref(), Some("Create customer"));

        let node = &doc.child("Nodes").unwrap().children[0];
        assert_eq!(node.attr("type"), Some("InputUserAction"));
        assert_eq!(node.text_of("ControlName").as_deref(), Some("CustAccount"));
    }

    #[test]
    fn unescapes_entities() {
        let doc = parse(r#"<r><V>Contoso &amp; Sons</V></r>"#).unwrap();
        assert_eq!(doc.text_of("V").as_deref(), Some("Contoso & Sons"));
    }
}
