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

> **This has not been validated against a real D365 environment.**
>
> It was built against *synthetic* schemas modelled on production shapes. The
> bundled recording fixture is synthetic, and the end-to-end suite runs against a
> mock reproducing D365's DOM contract — not a live instance. Everything passing
> proves the tool is self-consistent, nothing more.
>
> Treat the mapping table (`src/lower.rs`, or `csharp/src/Rsat2Pw.Core/Lower.cs`),
> the selectors in `runtime/d365.ts`, and every generated spec as a **starting
> point to verify**. Run against a sandbox or tier-2 environment first — never
> straight at production. See [Before you trust it](#before-you-trust-it-on-real-recordings).
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

Task Recorder captures **AOT control names**. The D365 web client renders those
same names into the DOM as `data-dyn-controlname`. Recorded control identity
therefore maps straight to a stable Playwright locator — no XPath, no
heuristics, no scraping of generated ids.

That is normally the hardest part of any record-to-replay conversion, and here
it falls out of how the platform already works.

```
.axtr (zip) ──► tolerant XML tree ──► RecNode ──► action IR ──► TypeScript
                     xml.rs           recording.rs   lower.rs     codegen.rs

RSAT .xlsx parameters ─────────────────────────────► data-driven fixtures
                                                          params.rs
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

./target/release/rsat2pw fixtures/CreateCustomer.axtr \
    --out-dir tests \
    --params fixtures/CreateCustomer-params.xlsx \
    --emit-runtime
```

</details>

<details>
<summary><b>C#</b></summary>

```bash
cd csharp
dotnet build --configuration Release

dotnet run --project src/Rsat2Pw.Cli -- ../fixtures/CreateCustomer.axtr \
    --out-dir ../tests \
    --params ../fixtures/CreateCustomer-params.xlsx \
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
  test(`Create customer [${params.__case}]`, async ({ page }) => {
    const d365 = await D365.open(page);

    await test.step('Create a new customer', async () => {
      await d365.command('New');
      await d365.withDialog('Create customer', async () => {
        await d365.setField('CustAccount', params.Customer_account);
        await d365.setField('NameRef_Name', params.Customer_name);
        await d365.lookup('CustGroup', params.Customer_group);
        await d365.command('OK');
      });
    });
  });
}
```

Recorder annotations become `test.step` calls, so the Playwright trace reads
like the original recording.

## Conversion reports

Honest gaps are only useful if they are legible. Every run writes a conversion
report beside the spec ([worked example](tests/CreateCustomer.report.md)) and
prints a summary:

```
actions   : 18 (17 translated, 1 not - 94.4%)

translated:
  command          3
  setField         3
  setGridCell      2
  ...

not translated:
  ExportToExcelUserAction          1
  <-- add rules for these in src/lower.rs; the report lists their properties
```

The **Not translated** section is the worklist for the mapping table. Each
unmapped action type arrives with *every* property the recorder supplied — not
the truncated version that goes into the emitted `TODO` — because those property
names are exactly what a new mapping rule keys off:

| Property | Example value |
| --- | --- |
| `ControlName` | `ExportToExcelButton` |
| `OfficeTemplate` | `CustomerV3` |

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
| `mock/` | stand-in D365 page used by the self-test harness |
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

**Generated code never touches raw locators.** Specs call only into
`runtime/d365.ts`. D365 is aggressively asynchronous: controls render before they
are interactive, and the blocking overlay is what actually gates input. Every
wait, retry and quirk is concentrated in one hand-maintained file, so when the
client changes you fix one file instead of regenerating everything.

**Variables become fixtures, not literals.** That is the whole point of RSAT: one
recording, many rows of data. Recorded variables become a `Params` type and a
`cases` array. The workbook reader accepts both the wide layout (header row of
variable names, one case per row) and the tall `Name`/`Value` layout.

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

**Golden tests** assert that the committed `tests/CreateCustomer.spec.ts` and its
report are byte-identical to what the converter emits today, so the worked
examples in this README cannot drift from the code. Regenerate them with the
command under [Quick start](#quick-start) if you change code generation
deliberately.

**`mock/`** is a self-test harness: a stand-in page reproducing D365's DOM
contract (`data-dyn-controlname`, a toggled `.blockUI` overlay, `[role="dialog"]`
scoping, indexed grid cells) so the converter and runtime helper can be proven
end to end without a live environment. It has already earned its keep — the idle
wait originally checked whether the blocking overlay *existed* rather than
whether it was *visible*, which would have hung forever against real D365, since
the client keeps that element in the DOM permanently.

The mock run uses whatever Chromium `npm install` fetched. On an offline build
agent, point `CHROMIUM_PATH` at a browser you already have.

## Before you trust it on real recordings

- **Verify the XML element names against your own `.axtr` export.** The bundled
  fixture is synthetic and modelled on the documented shape. The parser is
  deliberately tolerant, but the mapping table is where you will spend your first
  hour.
- **Tune `BLOCKING_SELECTORS`** in `runtime/d365.ts` to your platform version.
- **Check output menu items.** `navigate()` deep-links display menu items by bare
  name and action menu items with the documented `action:` prefix. Output menu
  items are sent unprefixed, which is **not** verified against a live environment
  — confirm it before converting a recording that opens a report.
- **Check which MFA your tenant enforces.** TOTP can be automated; number
  matching and push approval cannot. See [Authentication](#authentication).
- **Grids are virtualized.** `setGridCell` scrolls until the row materialises, but
  heavily filtered or sorted grids may need a business-key lookup rather than a
  row index.

## License

**Public domain.** Released under [The Unlicense](LICENSE) — copy, modify,
publish, use, compile, sell or distribute it, commercially or not, by any means.
No conditions, no attribution required.

Released by **Logixr Corp**, authored by **Ehren Schlueter**. See
[NOTICE](NOTICE) for origin and operating caveats.

> **Logixr is not responsible if it breaks your systems. Run at your own risk.**
