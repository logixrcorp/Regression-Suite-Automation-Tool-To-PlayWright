//! Test data: RSAT parameter workbooks -> Playwright fixtures.
//!
//! The whole point of RSAT is that one recording runs against many rows of
//! data, so variables must become a data-driven fixture rather than literals
//! baked into the spec.
//!
//! Two workbook layouts are recognised:
//!   * **wide**  - header row of variable names, one case per row (preferred).
//!   * **tall**  - a `Name` / `Value` column pair, yielding a single case.
//!     This is the shape RSAT's own per-form parameter sheets tend to take.

use crate::ir::TestCase;
use crate::lower::{sanitize_ident, unique_ident};
use anyhow::{Context, Result};
use calamine::{open_workbook_auto, Reader};
use std::collections::BTreeMap;
use std::path::Path;

#[derive(Debug, Clone, Default)]
pub struct Case {
    pub label: String,
    pub values: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Default)]
pub struct Cases {
    pub fields: Vec<String>,
    pub rows: Vec<Case>,
    pub source: String,
}

/// Fall back to the defaults captured in the recording itself.
pub fn from_recording(case: &TestCase) -> Cases {
    let fields: Vec<String> = case.variables.iter().map(|v| v.name.clone()).collect();
    let values = case
        .variables
        .iter()
        .map(|v| (v.name.clone(), v.default.clone()))
        .collect();

    Cases {
        rows: if fields.is_empty() {
            vec![]
        } else {
            vec![Case {
                label: "recorded defaults".to_string(),
                values,
            }]
        },
        fields,
        source: "recording defaults".to_string(),
    }
}

pub fn from_workbook(path: &Path, sheet: Option<&str>, case: &TestCase) -> Result<Cases> {
    let mut workbook =
        open_workbook_auto(path).with_context(|| format!("opening {}", path.display()))?;

    let sheet_name = match sheet {
        Some(s) => s.to_string(),
        None => workbook
            .sheet_names()
            .first()
            .cloned()
            .context("workbook has no sheets")?,
    };

    let range = workbook
        .worksheet_range(&sheet_name)
        .with_context(|| format!("reading sheet '{sheet_name}'"))?;

    let table: Vec<Vec<String>> = range
        .rows()
        .map(|r| r.iter().map(|c| c.to_string().trim().to_string()).collect())
        .filter(|r: &Vec<String>| r.iter().any(|c| !c.is_empty()))
        .collect();

    let source = format!("{} [{}]", path.display(), sheet_name);
    let mut cases = if is_tall(&table) {
        parse_tall(&table)
    } else {
        parse_wide(&table)
    };
    cases.source = source;

    // Any variable the recording expects but the workbook omits still needs to
    // exist on the params object, or the generated spec will not compile.
    for var in &case.variables {
        if !cases.fields.contains(&var.name) {
            cases.fields.push(var.name.clone());
            for row in &mut cases.rows {
                row.values
                    .entry(var.name.clone())
                    .or_insert_with(|| var.default.clone());
            }
        }
    }

    Ok(cases)
}

fn is_tall(table: &[Vec<String>]) -> bool {
    let Some(header) = table.first() else {
        return false;
    };
    header.len() >= 2
        && header[0].eq_ignore_ascii_case("name")
        && header[1].eq_ignore_ascii_case("value")
}

fn parse_tall(table: &[Vec<String>]) -> Cases {
    let mut fields: Vec<String> = Vec::new();
    let mut values = BTreeMap::new();
    // sanitized name -> emitted field, so a repeated `Name` row updates the
    // field it first created instead of minting a second one.
    let mut mapped: BTreeMap<String, String> = BTreeMap::new();

    for row in table.iter().skip(1) {
        let Some(name) = row.first() else { continue };
        if name.is_empty() {
            continue;
        }
        let base = sanitize_ident(name);
        let field = match mapped.get(&base) {
            Some(f) => f.clone(),
            None => {
                let f = unique_ident(&base, &fields);
                fields.push(f.clone());
                mapped.insert(base, f.clone());
                f
            }
        };
        values.insert(field, row.get(1).cloned().unwrap_or_default());
    }

    Cases {
        fields,
        rows: vec![Case {
            label: "workbook".to_string(),
            values,
        }],
        source: String::new(),
    }
}

fn parse_wide(table: &[Vec<String>]) -> Cases {
    let Some(header) = table.first() else {
        return Cases::default();
    };

    // A leading "Case" / "TestCase" column names the row instead of feeding it.
    let label_col = header
        .iter()
        .position(|h| {
            let h = h.to_ascii_lowercase();
            h == "case" || h == "testcase" || h == "test case" || h == "scenario"
        });

    // Resolve column index -> field name once, so every row uses the same
    // mapping even where two headers sanitize to the same identifier.
    let mut fields: Vec<String> = Vec::new();
    let mut columns: Vec<(usize, String)> = Vec::new();
    for (i, header_cell) in header.iter().enumerate() {
        if Some(i) == label_col || header_cell.is_empty() {
            continue;
        }
        let name = unique_ident(&sanitize_ident(header_cell), &fields);
        fields.push(name.clone());
        columns.push((i, name));
    }

    let rows = table
        .iter()
        .skip(1)
        .enumerate()
        .map(|(n, row)| {
            let label = label_col
                .and_then(|i| row.get(i).cloned())
                .filter(|s| !s.is_empty())
                .unwrap_or_else(|| format!("row {}", n + 1));

            let values = columns
                .iter()
                .map(|(i, name)| (name.clone(), row.get(*i).cloned().unwrap_or_default()))
                .collect();

            Case { label, values }
        })
        .collect();

    Cases {
        fields,
        rows,
        source: String::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wide_layout_yields_one_case_per_row() {
        let table = vec![
            vec!["Case".into(), "Customer name".into(), "Group".into()],
            vec!["domestic".into(), "Contoso".into(), "10".into()],
            vec!["export".into(), "Fabrikam".into(), "20".into()],
        ];
        let cases = parse_wide(&table);
        assert_eq!(cases.fields, vec!["Customer_name", "Group"]);
        assert_eq!(cases.rows.len(), 2);
        assert_eq!(cases.rows[1].label, "export");
        assert_eq!(cases.rows[1].values["Customer_name"], "Fabrikam");
    }

    /// Two headers that sanitize to the same identifier would emit a duplicate
    /// key in the generated `Params` type, which does not compile.
    #[test]
    fn colliding_headers_are_suffixed_not_dropped() {
        let table = vec![
            vec!["Customer name".into(), "Customer-name".into(), "__case".into()],
            vec!["Contoso".into(), "Fabrikam".into(), "collide".into()],
        ];
        let cases = parse_wide(&table);
        assert_eq!(cases.fields, vec!["Customer_name", "Customer_name_2", "__case_2"]);
        assert_eq!(cases.rows[0].values["Customer_name"], "Contoso");
        assert_eq!(cases.rows[0].values["Customer_name_2"], "Fabrikam");
        assert_eq!(cases.rows[0].values["__case_2"], "collide");
    }

    #[test]
    fn tall_layout_yields_a_single_case() {
        let table = vec![
            vec!["Name".into(), "Value".into()],
            vec!["Customer name".into(), "Contoso".into()],
        ];
        assert!(is_tall(&table));
        let cases = parse_tall(&table);
        assert_eq!(cases.rows.len(), 1);
        assert_eq!(cases.rows[0].values["Customer_name"], "Contoso");
    }
}
