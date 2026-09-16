<div align="center">

# Regression Suite Automation Tool → Playwright

**Convert Dynamics 365 Finance & Operations Task Recorder / RSAT recordings into runnable Playwright tests.**

[![CI](https://github.com/logixrcorp/Regression-Suite-Automation-Tool-To-PlayWright/actions/workflows/ci.yml/badge.svg)](https://github.com/logixrcorp/Regression-Suite-Automation-Tool-To-PlayWright/actions/workflows/ci.yml)
[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](LICENSE)
[![Rust](https://img.shields.io/badge/Rust-1.85%2B-000000?logo=rust&logoColor=white)](Cargo.toml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](csharp/)
[![Output](https://img.shields.io/badge/output-TypeScript-3178C6?logo=typescript&logoColor=white)](tests/)

</div>

---

Point it at a Task Recorder recording and it emits a Playwright spec you can
commit, review and run in CI. Recorded variables become data-driven fixtures, so
one recording still covers many rows of RSAT test data.

The converter ships as **two independent implementations that emit byte-identical
output** — Rust at the repository root, C# under [`csharp/`](csharp/). Use
whichever fits your build pipeline.

Conversion is **deterministic**: a fixed mapping table, no model involved. The
same recording produces the same TypeScript every time, which is what allows two
separate implementations to be held to byte-for-byte agreement in CI.

## ⚠️ Project status

> **The mapping table is derived from real recordings. The runtime is not.**
>
> The parser and the mapping table were built against real exported `.axtr`
> recordings, and the conversion is checked against several of them: 89–100% of
> actions translate, with the rest listed by name in the report. The bundled
> fixture is synthetic, but it is written in the real schema.
>
> What has **not** happened is a run against a live D365 environment. The
> end-to-end suite drives a mock that reproduces the client's DOM contract, not
> an instance. So the *conversion* is evidence-backed; the *selectors* in
> `runtime/d365.ts` are the informed-guess half, and they are where your first
> hour will go. Every failure there names the control, its type and each
> selector tried, so a fix is a one-line addition to one table.
>
> Run against a sandbox or tier-2 environment first — never straight at
> production. See [Before you trust it](#before-you-trust-it-on-real-recordings).
>
> **Logixr is not responsible if it breaks your systems. Run at your own risk.**
> No warranty of any kind, express or implied.

## Contents

- [Why this maps cleanly](#why-this-maps-cleanly)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [CLI reference](#cli-reference)
- [Example output](#example-output)
- [Conversion reports](#conversion-reports)
- [Authentication](#authentication)
- [Repository layout](#repository-layout)
- [Design decisions](#design-decisions)
- [Verification](#verification)
- [Before you trust it on real recordings](#before-you-trust-it-on-real-recordings)
- [License](#license)

## Why this maps cleanly

Task Recorder captures **AOT control names**, and the D365 web client renders
those same names into the DOM — as `name`, as `data-dyn-controlname`, or on a
wrapper around the real element, depending on the kind of control. Recorded
control identity therefore maps to a stable locator with no XPath, no
heuristics and no scraping of generated ids.

Two things in the recording make that work, and both are easy to get backwards:

**The recorder names the verb, not the target.** A recorded click is
`CommandName=Click` against `ControlName=PurchCopyJournalHeader`. Read
`CommandName` as the thing to click and you generate a suite that hunts the
screen for a button labelled "Click".

**`ControlType` says how to reach it.** `MenuItemButton`, `Input`, `Grid`,
`AppBarTab`, `CheckBox` and the rest each live at a different place in the
DOM, so the type rides along with every generated call and the runtime keeps
one table of selector candidates per type.

```
.axtr (zip) ──► tolerant XML tree ──► RecNode ──► action IR ──► TypeScript
  Recording.xml      xml.rs          recording.rs   lower.rs     codegen.rs
                                          │
                       <RootScope><Children> is the action tree;
                       <UserActions> is back-references, not actions

recorded input values ─────────────────────────────► data-driven fixtures
RSAT .xlsx parameters                                     params.rs
```

## Requirements

| Component | Needed for | Version |
| --- | --- | --- |
| Rust | the Rust converter | 1.85+ (edition 2024) |
| .NET SDK | the C# converter | 10.0 |
| Node.js | running the generated tests | 22+ |

You need **either** Rust **or** .NET — not both. Node.js is required in all
cases, since the output is Playwright TypeScript.

## Quick start

### 1. Convert a recording

Input is an `.axtr` archive **or** a raw `Recording.xml`. RSAT extracts the
latter into its working folder, so either works directly.

<details open>
<summary><b>Rust</b></summary>

```bash
cargo build --release

./target/release/rsat2pw fixtures/ConfirmPurchaseOrder.axtr \
    --out-dir tests \
    --params fixtures/ConfirmPurchaseOrder-params.xlsx \
    --emit-runtime
```

</details>

<details>
<summary><b>C#</b></summary>

```bash
cd csharp
dotnet build --configuration Release

dotnet run --project src/Rsat2Pw.Cli -- ../fixtures/ConfirmPurchaseOrder.axtr \
    --out-dir ../tests \
    --params ../fixtures/ConfirmPurchaseOrder-params.xlsx \
    --emit-runtime
```

</details>

### 2. Run the generated tests

```bash
npm install           # also fetches the Chromium build Playwright drives
cp .env.sample .env   # fill in your environment URL and sign-in details
npx playwright test
```

Generated specs land in `tests/` and run **as-is**. They navigate with relative
URLs against the `baseURL` in `playwright.config.ts` and inherit their signed-in
session from the `setup` project, so a converted recording needs no editing to
fit the harness.

## CLI reference

Both implementations accept the same flags.

```
rsat2pw <INPUT> [OPTIONS]
```

| Flag | Purpose |
| --- | --- |
| `-o`, `--out-dir <DIR>` | where the `.spec.ts` and `.data.ts` land (default `tests`) |
| `-p`, `--params <FILE>` | RSAT parameter workbook supplying test data |
| `--sheet <NAME>` | worksheet within that workbook (default: the first) |
| `--on-unsupported <MODE>` | `annotate` (default), `fail`, or `comment` |
| `--emit-runtime` | also write the `runtime/d365.ts` helper |
| `--report <FILE>` | conversion report path; a `.json` path emits JSON (default `<out-dir>/<Name>.report.md`) |
| `--no-report` | skip the conversion report |
| `--dry-run` | print what would be generated without writing anything |
| `-h`, `--help` / `-V`, `--version` | usage and version |

## Example output

```ts
for (const params of cases) {
  test(`Confirm purchase order [${params.__case}]`, async ({ page }) => {
    const d365 = await D365.open(page);

    await test.step('Find the purchase order', async () => {
      await d365.navigate('PurchTableListPage', 'Display');
      await d365.withForm('PurchTable', async () => {
        // rsat2pw: skipped 'CommandUserAction:GetFilters' - opens the filter pane; filter() does that itself
        await d365.filter('SystemDefinedFilterManager', 'PurchId', 'Purchase order', 'Is', params.PurchId);
        await d365.selectRow('Grid', 2);
        await d365.openRow('Grid');
      });
    });

    await test.step('Check the delivery details', async () => {
      // Sub-task: Delivery details. (Begin)
      await d365.withForm('PurchTable', async () => {
        await d365.tab('PurchaseTab');
        await d365.setField('PurchTable_DeliveryDate', params.PurchTable_DeliveryDate, 'Date');
        await d365.openLookup('PurchTable_DlvMode');
        await d365.withForm('PurchTable_DlvMode_Lookup', async () => {
          await d365.selectRow('LookupGrid', 1);
        });
        await d365.commitLookup('PurchTable_DlvMode');
      });
    });
  });
}
```

Step groups the user made while recording become `test.step` calls and form
scopes become `withForm`, so the Playwright trace reads like the original
recording — while the private scopes the client wraps around its own internals
are flattened away, since they are plumbing rather than intent.

### What the mapping table covers

| Recorded | Emitted |
| --- | --- |
| `MenuItemUserAction` | `navigate()` — a `?mi=` deep link |
| `Scope` with `IsForm=true` | `withForm()` |
| `Scope` with a public `IsStepGroup=true` | `test.step()` |
| `PropertyUserAction` | `setField()` / `setGridCell()` |
| `Click`, `TabShown` | `click()`, `tab()` |
| `RequestPopup`, `ResolveChanges` | `openLookup()`, `commitLookup()` |
| `ChangeSelectedIndexInCache` / `ChangeSelectedIndex`, `MarkActiveRow`, `NavigationAction` | `selectRow()`, `markRow()`, `openRow()` |
| `ApplyFiltersForTaskRecorder` / `ApplyFilters` | `filter()`, unpacked from its JSON argument |
| `ResetFilters` | `resetFilters()` |
| `SelectionPathChanged`, `ExpandingPath` | `selectTreeItem()` / `expandTreeItem()`, walking the recorded tree path |
| `ExecuteHyperlink` | `click()` — following a link rendered inside a field |
| `ExecuteShortcuts` | `shortcut()` — e.g. the View/Edit toggle |
| `RequestClose` | `closeForm()` |
| `ValidationUserAction` | `expectValue()`, with the expected value as test data |
| `TaskUserAction`, `InfoUserAction`, `AnnotationUserAction` | a comment — a sub-task boundary, or a note written while recording |

Anything else becomes a `TODO(rsat2pw)` naming the verb that has no rule yet.

Some of those rules exist for verbs that appear in *none* of the recordings
above. There is no published list of the command names the recorder emits — the
node types are in Microsoft's CDM schema, the verbs are not — so the only way to
extend the table is to pool what different corpora turn up. Several rules here
were learned from another converter's dispatch table, written against recordings
we do not have.

**What is deliberately left unmapped**, and why — these are judgement calls, not
oversights:

| Recorded | Why there is no rule |
| --- | --- |
| `OpenGridView` | recorded with no control name at all, so there is nothing to target |
| `OpenFormPart` | cannot be told apart from a part simply *rendering*, and replaying a click that never happened is worse than a TODO |
| `SelectForAdd` | personalization. Skipping it may drop a column a later step needs; replaying it edits the test account's saved layout |
| `FormUserAction` | the schema gives it no open/close discriminator, and no recording to hand contains one |

## Conversion reports

Honest gaps are only useful if they are legible. Every run writes a conversion
report beside the spec ([worked example](tests/ConfirmPurchaseOrder.report.md))
and prints a summary:

```
actions   : 24 (22 translated, 1 skipped, 1 not - 91.7%)

translated:
  click            2
  filter           1
  selectRow        2
  setField         2
  ...

skipped (client-internal, deliberately not replayed):
  CommandUserAction:GetFilters     1

not translated:
  CommandUserAction:SelectForAdd   1
  <-- add rules for these in src/lower.rs; the report lists their properties
```

There are three buckets, not two. **Skipped** is for actions the converter
understands and deliberately does not replay — client-internal bookkeeping with
no user-visible effect. They are counted apart from translated ones on purpose:
otherwise the headline percentage could be improved by deciding that more and
more of the recording does not matter. Each one still leaves a comment in the
generated spec, because a step that vanishes without a word is indistinguishable
from one the converter never saw.

The **Not translated** section is the worklist for the mapping table. Commands
are listed by their verb — `CommandUserAction:SelectForAdd`, not the useless
`CommandUserAction` that every command shares — and each arrives with *every*
property the recorder supplied, not the truncated version that goes into the
emitted `TODO`, because those property names are exactly what a new mapping rule
keys off:

| Property | Example value |
| --- | --- |
| `ControlName` | `Grid` |
| `ControlType` | `Grid` |
| `Description` | `Select Grid to add a field to it.` |

The report also carries a **translation outline** (the whole recording in order,
with `!!` against anything that did not convert) and a **test data** table
flagging variables no action uses, or that the workbook does not supply.

Use `--report out.json` for the machine-readable form if you want to gate a build
on coverage. `--dry-run` writes nothing but still prints the summary above — the
fastest way to see how a new recording will fare.

## Authentication

Sign-in follows the pattern from Elio Struyf's
[testing-microsoft365-playwright-template](https://github.com/estruyf/testing-microsoft365-playwright-template):
a `setup` project authenticates **once per run** and saves the session to
`playwright/.auth/user.json`; every generated spec inherits it through
`storageState`. D365 F&O signs in through the same Entra ID flow as the rest of
Microsoft 365, so
[`playwright-m365-helpers`](https://www.npmjs.com/package/playwright-m365-helpers)
drives it.

There are three routes in, and **which one applies is decided by your tenant, not
by preference**:

| Your account | What to set | Which setup runs |
| --- | --- | --- |
| No MFA (Conditional Access exclusion) | `D365_USERNAME`, `D365_PASSWORD` | `tests/login.setup.ts` |
| MFA via authenticator code (TOTP) | the above **+ `D365_OTP_SECRET`** | `tests/mfa.setup.ts` |
| MFA via number matching / push | nothing — capture by hand | setup steps aside |

Setting `D365_OTP_SECRET` is what selects the MFA flow; there is no separate
switch to keep in sync. The secret is the seed shown as *"Can't scan the image?"*
when enrolling the authenticator, which lets the code be computed rather than
read off a phone. Check a seed with `npm run generate:otp -- <secret>`.

> **Number matching and push approval cannot be automated** — that is the point
> of them. On those tenants, capture a session once by hand and the setup project
> will step aside and reuse it:
>
> ```bash
> npx playwright open --save-storage=playwright/.auth/user.json https://your-env.operations.dynamics.com
> ```

Sessions expire according to your Conditional Access sign-in-frequency policy;
refresh one with `npm run auth`. If a run starts against a stale session,
`D365.open()` detects the redirect to the identity provider and **fails
immediately** with the command to run next, rather than timing out a minute later
reporting a missing D365 form.

`.env` and `playwright/.auth/` are both git-ignored.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/` | the Rust converter |
| `csharp/` | the C# converter, with its own tests and README |
| `runtime/d365.ts` | hand-written Playwright runtime the generated specs call into |
| `tests/` | generated specs, plus the sign-in setup projects |
| `constants/` | auth file path and credential resolution |
| `fixtures/` | the synthetic recording and its parameter workbook |
| `mock/` | stand-in D365 page, and the runtime helper's own tests |
| `scripts/` | TOTP code generator |

### Where the two implementations correspond

| Concern | Rust | C# |
| --- | --- | --- |
| Tolerant XML tree | `src/xml.rs` | `Xml.cs` |
| Recording reader | `src/recording.rs` | `Recording.cs` |
| **Mapping table** | `src/lower.rs` | `Lower.cs` |
| Action IR | `src/ir.rs` | `Ir.cs` |
| Workbook reader | `src/params.rs` (calamine) | `Params.cs` + `Xlsx.cs` (no dependencies) |
| Code generation | `src/codegen.rs` | `Codegen.cs` |
| Conversion report | `src/report.rs` | `Report.cs` |

## Design decisions

**Tolerant parsing, not a rigid schema.** Task Recorder's XML has drifted across
platform updates — element names, wrapper spellings (`Childs` vs `Children`), and
the `i:type` discriminator have all moved. Rather than bind a deserializer to one
snapshot, the parser produces a generic tree and lowering interprets it. The
entire mapping table lives in one editable function.

**Honest gaps over silent guesses.** Any action that cannot be mapped becomes
`Unsupported` and is emitted as a `TODO(rsat2pw)` plus a Playwright annotation —
or a hard failure with `--on-unsupported fail`. A converter that is 85% automatic
with visible gaps beats one that quietly emits wrong code.

**"Does nothing" and "no rule for this" are different admissions.** Hence the
separate `Skipped` bucket. Folding the two together would let a gap be closed by
declaring it unimportant, which is the failure mode this whole design is
guarding against.

**Command arguments are not properties.** The recorder passes positional
arguments — frequently a JSON blob — alongside a command. Flattening those into
the property bag makes a filter command indistinguishable from a field edit, and
emits `setField(control, \'[{"Capability":null,...}]\')`. They are parsed where
they mean something (the filter payload) and otherwise left alone.

**Generated code never touches raw locators.** Specs call only into
`runtime/d365.ts`. D365 is aggressively asynchronous: controls render before they
are interactive, and the blocking overlay is what actually gates input. Every
wait, retry and quirk is concentrated in one hand-maintained file, so when the
client changes you fix one file instead of regenerating everything.

**Every recorded input is test data, not a literal.** That is the whole point of
RSAT: one recording, many rows of data, which is exactly how RSAT fills the
columns of its own parameter workbook. Each recorded value becomes a field on a
`Params` type, named after the control that was edited and defaulting to what the
recorder captured, so a converted recording is data-driven the moment it is
generated. The workbook reader accepts the wide layout (header row of variable
names, one case per row) and the tall `Name`/`Value` layout.

**Navigation is deep-linked.** Recorded navigation-pane clicks are replaced with
`?mi=<MenuItem>` deep links — faster, and immune to menu restructuring.

## Verification

```bash
cargo test              # parser, lowering, codegen + golden tests
npx tsc --noEmit        # generated TypeScript typechecks
npm run test:mock       # generated specs actually run, against the mock
cd csharp && dotnet test    # C# suite, including byte-parity with the Rust output
```

CI runs all of the above on **Linux and Windows**, so a divergence between the
two implementations fails the build.

**How the mapping table was derived.** Not from documentation — from real
exported recordings. Eight of them are public on GitHub, found by searching for
the data-contract namespace and the annotation type names:

```bash
gh api -X GET search/code -f q='"Microsoft.Dynamics.Client.ServerForm.TaskRecording"'
```

They are not redistributed here: their licensing is unclear and they are other
people's business processes. The bundled fixture is synthetic, written in the
schema those files revealed. Against that corpus the converter translates
88–100% of actions, and the handful it does not are listed by name above. Point
it at your own recordings and check — `--dry-run` writes nothing and still
prints the coverage summary.

The node types themselves are cross-checked against Microsoft's own published
schema for the task recorder tables (`SysTaskRecorderNode*` in
[microsoft/CDM](https://github.com/microsoft/CDM)), which is what turns "verbs we
happened to see" into "verbs that exist". Four node types in that schema appear
in none of the eight recordings; a test pins what the converter does with each.

**Golden tests** assert that the committed `tests/ConfirmPurchaseOrder.spec.ts` and
its report are byte-identical to what the converter emits today, so the worked
examples in this README cannot drift from the code. Regenerate them with the
command under [Quick start](#quick-start) if you change code generation
deliberately.

**`mock/`** is a self-test harness: a stand-in page reproducing D365's DOM
contract — controls addressed by `name` first and `data-dyn-controlname` second,
forms by `data-dyn-form-name`, a toggled `.blockUI` overlay, grid rows carrying a
1-based `aria-rowindex` against a recorder that counts from zero, a column-header
filter flyout, and a lookup that renders outside the form that opened it — so
the converter and runtime helper can be proven end to end without a live
environment.

It reproduces the *shape* of that contract rather than a convenient
simplification of it, which is the only way it earns anything. It has now done
so three times: the idle wait originally checked whether the blocking overlay
*existed* rather than whether it was *visible*, which would have hung forever
against real D365; a tree path selected the ancestor that *contained* the node
rather than the node; and adding an overflow menu revealed that `.sysPopup` is
the client's generic popup class, so the filter flyout was matching a menu.

Four parts of it exist purely to be hostile, because each defends a branch of
the runtime that nothing else exercised:

| In the mock | What it defends |
| --- | --- |
| a stale form left in the DOM *after* the live one, same control names | every visibility filter in the runtime |
| a grid that renders three rows at a time, more on `PageDown` | the scroll loop in `gridRow()` |
| two dialogs stacked, sharing a button name | form scoping |
| a value the client rewrites on blur | the assumption that a field keeps what was typed |

[`mock/runtime.spec.ts`](mock/runtime.spec.ts) covers those. It is hand-written and
lives outside `tests/` on purpose, so the real-environment config never picks it
up. Worth knowing what it proves: the visibility filters in `locate()` and
`withForm()` are redundant for the stale-form case - removing either alone still
passes, removing both fails - so it guards the pair rather than each one.

The mock run uses whatever Chromium `npm install` fetched. On an offline build
agent, point `CHROMIUM_PATH` at a browser you already have.

## Before you trust it on real recordings

The conversion side has been checked against real exports. The runtime side has
not been checked against a real environment, and that is where the risk is.

- **Start with the selector table.** `SELECTORS` and `CONTROL_FAMILY` in
  `runtime/d365.ts` are how each `ControlType` is found in the DOM. They are
  grounded in how the client renders today, not in a contract Microsoft
  publishes. A miss throws an error naming the control, its type and every
  selector tried, so the fix is one line in one table.
- **`filter()` is the least verified helper.** The recorder stores a filter as JSON
  on a command rather than as a series of clicks, so there is no click sequence
  to replay and the runtime has to drive the filter flyout itself. Check it
  first.
- **Grid rows assume a convention.** Recorded row indexes are zero-based and are
  matched against the 1-based `aria-rowindex` the client renders. If your platform
  version numbers them differently, adjust `GRID_ROW_SELECTORS`. Grids are also
  virtualized — `setGridCell` scrolls until the row materializes, but heavily
  filtered or sorted grids may need a business-key lookup rather than a row
  index.
- **Tune `BLOCKING_SELECTORS`** to your platform version.
- **Check output menu items.** `navigate()` deep-links display menu items by bare
  name and action menu items with the documented `action:` prefix. Output menu
  items are sent unprefixed, which is **not** verified against a live environment
  — confirm it before converting a recording that opens a report.
- **Check which MFA your tenant enforces.** TOTP can be automated; number
  matching and push approval cannot. See [Authentication](#authentication).

### Known gaps

- **RSAT v2 parameter workbooks are not read yet.** `--params` handles a plain
  sheet — a header row of names with one case per row, or a `Name`/`Value` pair.
  RSAT's own generated workbooks use `General` / `TestCaseSteps` /
  `MessageValidation` sheets instead, and pointing `--params` at one is **refused with
  an error** rather than read: those sheets open with a title block, and reading
  them as a plain sheet quietly invents test cases out of it. Until support
  lands, drop `--params` — the generated data module is seeded from the values the
  recorder captured — or point `--sheet` at a plain sheet of your own.
- **A workbook with headers and no data rows** falls back to the recorded
  values rather than emitting a case of blanks, and the report says so. Found
  by running against a real third-party workbook that had exactly that shape.
- **Validations come from the parameter file, not the recording.** RSAT holds
  expected values on its `MessageValidation` sheet, so a converted recording
  asserts nothing by itself. `expectValue()` exists in the runtime and is emitted
  for recordings that carry validation nodes; add assertions by hand otherwise.
- **Lookup selection is replayed, not resolved.** The recorder captures "row 2
  of the lookup", not "the row whose code is AIR". If the lookup's ordering
  differs on your data, the wrong value gets picked. Tree paths have the
  opposite problem: they replay by the text the recorder captured, so a
  renamed or translated node will not be found.
- **A generated spec asserting nothing can pass while doing the wrong thing.**
  The mock run proves the calls execute, not that they had the intended effect
  — which is the same gap as the missing validations above, seen from the other
  side. The runtime's own probes cover this for each helper; a converted
  recording needs assertions of its own before a green run means much.

## License

**Public domain.** Released under [The Unlicense](LICENSE) — copy, modify,
publish, use, compile, sell or distribute it, commercially or not, by any means.
No conditions, no attribution required.

Released by **Logixr Corp**, authored by **Ehren Schlueter**. See
[NOTICE](NOTICE) for origin and operating caveats.

> **Logixr is not responsible if it breaks your systems. Run at your own risk.**
