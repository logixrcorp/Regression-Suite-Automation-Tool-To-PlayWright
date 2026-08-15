using System.Globalization;
using Rsat2Pw;

namespace Rsat2Pw.Cli;

internal static class Program
{
    private const string Version = "0.1.0";

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");

            var inner = ex.InnerException;
            var depth = 0;
            while (inner is not null)
            {
                Console.Error.WriteLine($"  {depth}: {inner.Message}");
                inner = inner.InnerException;
                depth += 1;
            }

            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var parsed = Args.Parse(args);

        if (parsed is null)
        {
            return 2;
        }

        if (parsed.ShowHelp)
        {
            Console.WriteLine(Args.Usage);
            return 0;
        }

        if (parsed.ShowVersion)
        {
            Console.WriteLine($"rsat2pw {Version}");
            return 0;
        }

        if (parsed.Input is null)
        {
            Console.Error.WriteLine("error: an input recording is required\n");
            Console.Error.WriteLine(Args.Usage);
            return 2;
        }

        var recording = RecordingReader.Load(parsed.Input);
        var testCase = Lower.Run(recording);

        var cases = parsed.ParamsPath is not null
            ? Params.FromWorkbook(parsed.ParamsPath, parsed.Sheet, testCase)
            : Params.FromRecording(testCase);

        if (cases.Rows.Count == 0)
        {
            cases.Rows.Add(new Case { Label = "default" });
        }

        var onUnsupported = Codegen.ParseOnUnsupported(parsed.OnUnsupported) ?? OnUnsupported.Annotate;
        var output = Codegen.Generate(testCase, cases, onUnsupported);

        var report = Reporter.Build(testCase, cases);
        PrintSummary(report);

        if (parsed.DryRun)
        {
            Console.WriteLine(output.Spec);
            return 0;
        }

        Directory.CreateDirectory(parsed.OutDir);

        var specPath = Path.Combine(parsed.OutDir, $"{output.Stem}.spec.ts");
        var dataPath = Path.Combine(parsed.OutDir, $"{output.Stem}.data.ts");

        File.WriteAllText(specPath, output.Spec);
        File.WriteAllText(dataPath, output.Data);
        Console.Error.WriteLine($"wrote     : {specPath}");
        Console.Error.WriteLine($"wrote     : {dataPath}");

        if (parsed.EmitRuntime)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(parsed.OutDir));
            var runtimeDir = Path.Combine(parent ?? ".", "runtime");
            Directory.CreateDirectory(runtimeDir);

            var runtimePath = Path.Combine(runtimeDir, "d365.ts");
            File.WriteAllText(runtimePath, RuntimeAsset.D365Ts);
            Console.Error.WriteLine($"wrote     : {runtimePath}");
        }

        if (!parsed.NoReport)
        {
            var reportPath = parsed.ReportPath
                ?? Path.Combine(parsed.OutDir, $"{output.Stem}.report.md");

            var body = Path.GetExtension(reportPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? report.ToJson()
                : report.ToMarkdown();

            var reportDir = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(reportDir))
            {
                Directory.CreateDirectory(reportDir);
            }

            File.WriteAllText(reportPath, body);
            Console.Error.WriteLine($"wrote     : {reportPath}");
        }

        return 0;
    }

    private static void PrintSummary(Report report)
    {
        var c = report.Coverage;

        Console.Error.WriteLine($"recording : {report.Recording}");
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"actions   : {c.Actions} ({c.Translated} translated, {c.NotTranslated} not - {c.Percent():F1}%)"));
        Console.Error.WriteLine($"test data : {report.TestCases} case(s) from {report.TestDataSource}");

        if (report.TranslatedByOp.Count > 0)
        {
            Console.Error.WriteLine("\ntranslated:");
            foreach (var (op, n) in report.TranslatedByOp)
            {
                Console.Error.WriteLine($"  {op,-14} {n,3}");
            }
        }

        if (report.NotTranslated.Count == 0)
        {
            Console.Error.WriteLine("\nnot translated: none - full coverage");
        }
        else
        {
            Console.Error.WriteLine("\nnot translated:");
            foreach (var kind in report.NotTranslated)
            {
                Console.Error.WriteLine($"  {kind.RawKind,-30} {kind.Count,3}");
            }

            Console.Error.WriteLine(
                "  <-- add rules for these in src/Lower.cs; the report lists their properties");
        }
    }
}

internal sealed class Args
{
    public string? Input { get; set; }

    public string OutDir { get; set; } = "tests";

    public string? ParamsPath { get; set; }

    public string? Sheet { get; set; }

    public string OnUnsupported { get; set; } = "annotate";

    public bool EmitRuntime { get; set; }

    public string? ReportPath { get; set; }

    public bool NoReport { get; set; }

    public bool DryRun { get; set; }

    public bool ShowHelp { get; set; }

    public bool ShowVersion { get; set; }

    public const string Usage = """
        Convert a D365 F&O Task Recorder recording into a Playwright test.

        Usage: rsat2pw <INPUT> [OPTIONS]

        Arguments:
          <INPUT>  Task Recorder recording: an .axtr archive or a raw recording .xml

        Options:
          -o, --out-dir <DIR>       Where to write the spec and test data [default: tests]
          -p, --params <FILE>       RSAT parameter workbook (.xlsx) supplying the test data
              --sheet <NAME>        Worksheet within that workbook [default: the first]
              --on-unsupported <M>  annotate (default), fail, or comment
              --emit-runtime        Also write the D365 Playwright runtime helper
              --report <FILE>       Conversion report path; a .json path emits JSON
                                    [default: <out-dir>/<Name>.report.md]
              --no-report           Skip the conversion report
              --dry-run             Print what would be generated without writing anything
          -h, --help                Print help
          -V, --version             Print version
        """;

    public static Args? Parse(string[] argv)
    {
        var args = new Args();

        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];

            string? Next(string flag)
            {
                if (i + 1 >= argv.Length)
                {
                    Console.Error.WriteLine($"error: {flag} requires a value");
                    return null;
                }

                i += 1;
                return argv[i];
            }

            switch (arg)
            {
                case "-h" or "--help":
                    args.ShowHelp = true;
                    return args;

                case "-V" or "--version":
                    args.ShowVersion = true;
                    return args;

                case "-o" or "--out-dir":
                {
                    var value = Next(arg);
                    if (value is null) { return null; }
                    args.OutDir = value;
                    break;
                }

                case "-p" or "--params":
                {
                    var value = Next(arg);
                    if (value is null) { return null; }
                    args.ParamsPath = value;
                    break;
                }

                case "--sheet":
                {
                    var value = Next(arg);
                    if (value is null) { return null; }
                    args.Sheet = value;
                    break;
                }

                case "--on-unsupported":
                {
                    var value = Next(arg);
                    if (value is null) { return null; }
                    if (Codegen.ParseOnUnsupported(value) is null)
                    {
                        Console.Error.WriteLine(
                            $"error: invalid value '{value}' for --on-unsupported "
                            + "[possible values: annotate, fail, comment]");
                        return null;
                    }

                    args.OnUnsupported = value;
                    break;
                }

                case "--emit-runtime":
                    args.EmitRuntime = true;
                    break;

                case "--report":
                {
                    var value = Next(arg);
                    if (value is null) { return null; }
                    args.ReportPath = value;
                    break;
                }

                case "--no-report":
                    args.NoReport = true;
                    break;

                case "--dry-run":
                    args.DryRun = true;
                    break;

                default:
                {
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"error: unexpected argument '{arg}'");
                        return null;
                    }

                    if (args.Input is not null)
                    {
                        Console.Error.WriteLine($"error: unexpected argument '{arg}'");
                        return null;
                    }

                    args.Input = arg;
                    break;
                }
            }
        }

        if (args.ReportPath is not null && args.NoReport)
        {
            Console.Error.WriteLine("error: --report cannot be used with --no-report");
            return null;
        }

        return args;
    }
}
