# rsat2pw

Convert Dynamics 365 Finance & Operations **Task Recorder / RSAT** recordings
into runnable **Playwright** tests. Converter in Rust, output in TypeScript.

> ## ⚠️ Read this before you run it
>
> **This has never been tested against a real production system.** It was built
> against *synthetic* schemas modelled on production shapes — the bundled
> recording fixture is synthetic, and the end-to-end suite runs against a mock
> that reproduces D365's DOM contract, not a live environment. No part of this
> has been validated against an actual D365 F&O instance.
>
> Treat the mapping table in `lower.rs`, the selectors in `runtime/d365.ts`, and
> every generated spec as a **starting point to verify**, not as working code.
> Run it against a sandbox or test environment first — never straight at
> production.
>
> **This code is free for everyone.** Use it, change it, ship it, sell it — no
> conditions attached.
>
> **Logixr is not responsible if it breaks your systems. Run at your own risk.**
> No warranty of any kind, express or implied.

```
.axtr (zip) ──► tolerant XML tree ──► RecNode ──► action IR ──► TypeScript
                     xml.rs           recording.rs   lower.rs     codegen.rs

RSAT .xlsx parameters ─────────────────────────────► data-driven fixtures
                                                          params.rs
```

## Why this maps cleanly

Task Recorder records **AOT control names**. The D365 web client renders those
same names into the DOM as `data-dyn-controlname`. So recorded control identity
becomes a stable Playwright locator with no heuristics and no brittle XPath —
which is normally the hardest part of any record-to-Playwright conversion.

## Quick start

```bash
cargo build --release

# convert a recording, with RSAT test data, and drop the runtime helper alongside
./target/release/rsat2pw fixtures/CreateCustomer.axtr \
    --out-dir tests \
    --params fixtures/CreateCustomer-params.xlsx \
    --emit-runtime

npm install       # also fetches the Chromium build Playwright drives
cp .env.sample .env   # fill in your environment URL and sign-in details
npx playwright test
```

Generated specs land in `tests/` and run as-is: they navigate with relative
URLs against the `baseURL` in `playwright.config.ts`, and inherit their signed-in
session from the `setup` project. Nothing about a converted recording needs
editing to fit the harness.

| Flag | Purpose |
| --- | --- |
| `--out-dir` | where the `.spec.ts` and `.data.ts` land (default `tests`) |
| `--params` | RSAT parameter workbook supplying test data |
| `--sheet` | worksheet within that workbook |
| `--on-unsupported` | `annotate` (default), `fail`, or `comment` |
| `--emit-runtime` | also write `runtime/d365.ts` |
| `--report` | conversion report path; a `.json` path emits JSON (default `<out-dir>/<Name>.report.md`) |
| `--no-report` | skip the conversion report |
| `--dry-run` | print the spec instead of writing files |

## Design decisions worth knowing

**Tolerant parsing, not a rigid schema.** Task Recorder's XML has drifted
across platform updates — element names, wrapper spellings (`Childs` vs
`Children`), and the `i:type` discriminator have all moved. Rather than bind a
`serde` model to one snapshot, `xml.rs` parses to a generic tree and `lower.rs`
interprets it. The entire mapping table lives in one function you can edit.

**Honest gaps over silent guesses.** Any action we cannot map becomes
`Action::Unsupported` and is emitted as a `TODO(rsat2pw)` plus a Playwright
annotation (or a hard failure with `--on-unsupported fail`). A converter that
is 85% automatic with visible gaps beats one that quietly emits wrong code.

**Generated code never touches raw locators.** Specs call only into
`runtime/d365.ts`. D365 is aggressively asynchronous — controls render before
they are interactive, and the blocking overlay is what actually gates input —
so every wait, retry and quirk is concentrated in one hand-maintained file. When
the client changes you fix one file instead of regenerating everything.

**Variables become fixtures, not literals.** That is the whole point of RSAT:
one recording, many rows of data. Recorded variables become a `Params` type and
a `cases` array; the workbook reader accepts both the wide layout (header row of
variable names, one case per row) and the tall `Name`/`Value` layout.

**Navigation is deep-linked.** Recorded navigation-pane clicks are replaced with
`?mi=<MenuItem>` deep links — faster and immune to menu restructuring.

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

## Knowing what converted

Honest gaps are only useful if they are legible, so every run writes a
conversion report next to the spec ([example](tests/CreateCustomer.report.md))
and prints a summary:

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

The report's **Not translated** section is the worklist for the mapping table.
Each unmapped action type arrives with *every* property the recorder supplied —
not the truncated version that goes in the emitted `TODO` — because those
property names are exactly what a new rule in `lower.rs` keys off:

| Property | Example value |
| --- | --- |
| `ControlName` | `ExportToExcelButton` |
| `OfficeTemplate` | `CustomerV3` |

It also carries a **translation outline** (the whole recording in order, with
`!!` against anything that did not convert) and a **test data** table flagging
variables no action uses, or that the workbook does not supply.

Use `--report out.json` for the machine-readable form if you want to gate a
build on coverage. `--dry-run` writes nothing but still prints the summary
above, which is the fastest way to see how a new recording will fare.

## Signing in

Sign-in follows the pattern from Elio Struyf's
[testing-microsoft365-playwright-template](https://github.com/estruyf/testing-microsoft365-playwright-template):
a `setup` project authenticates **once per run** and saves the session to
`playwright/.auth/user.json`; every generated spec inherits it through
`storageState`. D365 F&O signs in through the same Entra ID flow as the rest of
Microsoft 365, so the same [`playwright-m365-helpers`](https://www.npmjs.com/package/playwright-m365-helpers)
`login()` drives it.

Three routes in, and **which one you get is decided by your tenant, not by
preference**:

| Your account | What to set | Which setup runs |
| --- | --- | --- |
| No MFA (CA exclusion) | `D365_USERNAME`, `D365_PASSWORD` | `tests/login.setup.ts` |
| MFA via authenticator code | the above **+ `D365_OTP_SECRET`** | `tests/mfa.setup.ts` |
| MFA via number match / push | nothing — capture by hand | setup skips |

Setting `D365_OTP_SECRET` is what selects the MFA flow; there is no separate
switch to keep in sync. The secret is the seed shown as "Can't scan the image?"
when enrolling the authenticator, which lets the code be computed instead of
read off a phone. Check a seed with `npm run generate:otp -- <secret>`.

**Number matching and push approval cannot be automated** — that is the point of
them. On those tenants capture a session once, by hand, and the setup project
will step aside and reuse it:

```bash
npx playwright open --save-storage=playwright/.auth/user.json https://your-env.operations.dynamics.com
```

Sessions expire on your Conditional Access sign-in-frequency policy. Refresh
one with `npm run auth`. If a run starts against a stale session, `D365.open()`
detects the redirect to the identity provider and **fails immediately** with
what to run next, rather than timing out a minute later complaining that a D365
form is missing.

`.env` and `playwright/.auth/` are both git-ignored.

## Verification

```bash
cargo test              # parser, lowering, codegen + a golden test on tests/
npx tsc --noEmit        # generated TS typechecks
npm run test:mock       # generated specs actually run, against the mock
```

`cargo test` includes a golden test asserting that the committed
`tests/CreateCustomer.spec.ts` is byte-identical to what the converter emits
today, so the worked example in this README can never drift from the code.
Regenerate it with the `rsat2pw` invocation under **Quick start** if you change
codegen deliberately.

`mock/` is a self-test harness: a stand-in page reproducing D365's DOM contract
(`data-dyn-controlname`, a toggled `.blockUI` overlay, `[role="dialog"]`
scoping, indexed grid cells) so the converter and the runtime helper can be
proven end to end without a live environment. It caught a real bug — the idle
wait originally checked whether the blocking overlay *existed* rather than
whether it was *visible*, which would have hung forever against real D365,
since the client keeps that element in the DOM permanently.

The mock run uses whatever Chromium `npm install` fetched. On an offline build
agent, point `CHROMIUM_PATH` at a browser you already have instead.

## Before you trust it on real recordings

- **Verify the XML element names against your own `.axtr` export.** The fixture
  is synthetic and modelled on the documented shape; the parser is deliberately
  tolerant, but the mapping table in `lower.rs` is where you will spend your
  first hour.
- **Tune `BLOCKING_SELECTORS`** in `runtime/d365.ts` to your platform version.
- **Check output menu items.** `navigate()` deep-links display menu items by
  bare name and action menu items with the documented `action:` prefix. Output
  menu items are sent unprefixed, which is *not* verified against a live
  environment — confirm it before converting a recording that opens a report.
- **Check which MFA your tenant enforces.** TOTP can be automated; number
  matching and push approval cannot. See **Signing in** above.
- **Grids are virtualized.** `setGridCell` scrolls until the row materialises,
  but heavily filtered or sorted grids may need a business-key lookup instead of
  a row index.

## License

**Public domain.** Released under [The Unlicense](LICENSE) — copy, modify,
publish, use, compile, sell, or distribute it, commercially or not, by any
means. No conditions, no attribution required.

Released by Logixr Corp, authored by Ehren Schlueter. See [NOTICE](NOTICE) for
origin and the operating caveats.

**Logixr is not responsible if it breaks your systems. Run at your own risk.**
