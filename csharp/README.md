# rsat2pw (C#)

A C# port of [rsat2pw](https://github.com/logixrcorp/Rsat2playwright): convert
Dynamics 365 Finance & Operations **Task Recorder / RSAT** recordings into
runnable **Playwright** tests. Converter in C#, output in TypeScript.

> ## ⚠️ Read this before you run it
>
> **This has never been tested against a real production system.** It was built
> against *synthetic* schemas modelled on production shapes. Treat the mapping
> table in `Lower.cs`, the selectors in `Assets/d365.ts`, and every generated
> spec as a **starting point to verify**, not as working code. Run it against a
> sandbox or test environment first — never straight at production.
>
> **This code is free for everyone.** **Logixr is not responsible if it breaks
> your systems. Run at your own risk.**

```
.axtr (zip) ──► tolerant XML tree ──► RecNode ──► action IR ──► TypeScript
                  Xml.cs             Recording.cs   Lower.cs     Codegen.cs

RSAT .xlsx parameters ─────────────────────────────► data-driven fixtures
                            Xlsx.cs / Params.cs
```

## Byte-identical to the Rust build

This is a true port, not a reimplementation with its own opinions. For the same
recording it emits **exactly** what the Rust converter emits — the spec, the
data module, the report, and the generated file name. The `goldens/` folder in
the test project holds the Rust build's committed output, and
[`ParityTests`](tests/Rsat2Pw.Tests/ParityTests.cs) asserts equality against it.
If those fail, the two implementations have diverged and one of them is wrong.

That contract has already earned its keep: it caught a real bug in the Rust
build, which did not normalize CRLF to LF the way XML 1.0 §2.11 requires, and
so emitted a stray `\r` in every string literal from a Windows-authored
recording. Both ports now normalize.

## Quick start

```bash
dotnet build

dotnet run --project src/Rsat2Pw.Cli -- path/to/Recording.xml \
    --out-dir tests \
    --params path/to/Parameters.xlsx \
    --emit-runtime
```

Input can be an `.axtr` archive or a raw recording `.xml` — the reader sniffs
the zip magic bytes, so RSAT's extracted `Recording.xml` works directly.

| Flag | Purpose |
| --- | --- |
| `-o`, `--out-dir` | where the `.spec.ts` and `.data.ts` land (default `tests`) |
| `-p`, `--params` | RSAT parameter workbook supplying test data |
| `--sheet` | worksheet within that workbook |
| `--on-unsupported` | `annotate` (default), `fail`, or `comment` |
| `--emit-runtime` | also write `runtime/d365.ts` |
| `--report` | conversion report path; a `.json` path emits JSON |
| `--no-report` | skip the conversion report |
| `--dry-run` | print the spec instead of writing files |

## Zero dependencies

The converter library references **no NuGet packages at all**. An `.xlsx` is a
zip of XML and an `.axtr` is a zip of XML, so `System.IO.Compression` plus
`System.Xml` covers both, and `System.Text.Json` covers the JSON report. Where
the Rust build pulls in `quick-xml`, `calamine`, `zip`, `minijinja`, `serde`,
`clap` and `heck`, this port has a supply chain of exactly nothing.

That means [`Xlsx.cs`](src/Rsat2Pw.Core/Xlsx.cs) is a small read-only OpenXML
reader. It handles shared strings, inline strings, cached formula results,
booleans and bare numerics, and honours cell references so gaps in a row do not
shift later columns. It reads values only — formatting and date conversion are
deliberately out of scope.

`heck`'s PascalCase had to be reproduced too, in
[`Casing.cs`](src/Rsat2Pw.Core/Casing.cs), because it decides the generated file
names. Every expectation in its tests was read off the Rust binary rather than
assumed — including that an all-caps run lowercases after its first letter
(`PO` → `Po`) and that a digit never starts a new word (`foo2bar` stays one
word).

## Verification

```bash
dotnet test
```

55 tests: the parser, lowering, the workbook reader, codegen escaping, the
report, and the parity suite that pins output to the Rust build byte for byte.

## Layout

| Path | Role |
| --- | --- |
| `src/Rsat2Pw.Core/Xml.cs` | schema-tolerant XML tree |
| `src/Rsat2Pw.Core/Recording.cs` | `.axtr` / `.xml` reader → `RecNode` |
| `src/Rsat2Pw.Core/Ir.cs` | normalized action IR and TypeScript escaping |
| `src/Rsat2Pw.Core/Lower.cs` | **the mapping table** — where you will spend your first hour |
| `src/Rsat2Pw.Core/Xlsx.cs` | dependency-free `.xlsx` reader |
| `src/Rsat2Pw.Core/Params.cs` | workbook → data-driven fixtures |
| `src/Rsat2Pw.Core/Codegen.cs` | IR → Playwright TypeScript |
| `src/Rsat2Pw.Core/Report.cs` | coverage report (Markdown and JSON) |
| `src/Rsat2Pw.Core/Assets/d365.ts` | the hand-written Playwright runtime, embedded |
| `src/Rsat2Pw.Cli/Program.cs` | the CLI |

## License

**Public domain.** Released under The Unlicense, same as the Rust build.
Released by Logixr Corp, authored by Ehren Schlueter.

**Logixr is not responsible if it breaks your systems. Run at your own risk.**
