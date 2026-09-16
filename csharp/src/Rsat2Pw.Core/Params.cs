namespace Rsat2Pw;

public sealed class Case
{
    public required string Label { get; init; }

    public SortedDictionary<string, string> Values { get; init; } = new(StringComparer.Ordinal);
}

public sealed class Cases
{
    public List<string> Fields { get; init; } = [];

    public List<Case> Rows { get; init; } = [];

    public string Source { get; set; } = "";
}

public static class Params
{
    public static Cases FromRecording(TestCase testCase)
    {
        var fields = testCase.Variables.Select(v => v.Name).ToList();

        var rows = new List<Case>();
        if (fields.Count > 0)
        {
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var variable in testCase.Variables)
            {
                values[variable.Name] = variable.Default;
            }

            rows.Add(new Case { Label = "recorded defaults", Values = values });
        }

        return new Cases
        {
            Fields = fields,
            Rows = rows,
            Source = "recording defaults",
        };
    }

    /// <summary>Sheets that mark a workbook as one RSAT generated for itself.</summary>
    private static readonly string[] RsatSheets = ["TestCaseSteps", "MessageValidation"];

    /// <summary>
    /// Is this one of RSAT's own parameter workbooks? It matters because the
    /// layout is nothing like the plain sheet this reader understands, and read
    /// as a plain sheet it does not fail - it quietly yields cases built out of
    /// the title block, which is the worst of the available outcomes.
    /// </summary>
    private static bool LooksLikeAnRsatWorkbook(IReadOnlyList<string> sheetNames) =>
        RsatSheets.Any(marker =>
            sheetNames.Any(name => string.Equals(name, marker, StringComparison.OrdinalIgnoreCase)));

    public static Cases FromWorkbook(string path, string? sheet, TestCase testCase)
    {
        var sheetNames = Xlsx.SheetNames(path);

        if (sheet is null && LooksLikeAnRsatWorkbook(sheetNames))
        {
            throw new InvalidDataException(
                $"{path} looks like an RSAT parameter workbook (sheets: {string.Join(", ", sheetNames)}).\n\n"
                + "That layout is not supported yet - reading it as a plain sheet would \n"
                + "silently invent test cases out of its title block. Either:\n"
                + "  * point --sheet at a plain sheet of your own (a header row of \n"
                + "    variable names, one case per row), or\n"
                + "  * drop --params, and the generated data module is seeded with the \n"
                + "    values the recording itself captured.");
        }

        var sheetName = sheet ?? sheetNames.FirstOrDefault()
            ?? throw new InvalidDataException("workbook has no sheets");

        var raw = Xlsx.ReadSheet(path, sheet);

        var table = raw
            .Select(r => r.Select(c => c.Trim()).ToList())
            .Where(r => r.Any(c => c.Length > 0))
            .ToList();

        var cases = IsTall(table) ? ParseTall(table) : ParseWide(table);
        var source = $"{path} [{sheetName}]";
        cases.Source = source;

        // A workbook with a header row but no data rows would otherwise produce
        // a single case of blanks - a spec that types empty strings into every
        // field while looking perfectly healthy. The values the recorder
        // captured are the better answer, and the source line says so rather
        // than pretending the workbook supplied them.
        if (cases.Rows.Count == 0)
        {
            cases.Rows.Add(new Case { Label = "recorded defaults" });
            cases.Source = $"{source} (no data rows; using recorded values)";
        }

        foreach (var variable in testCase.Variables)
        {
            if (!cases.Fields.Contains(variable.Name, StringComparer.Ordinal))
            {
                cases.Fields.Add(variable.Name);
                foreach (var row in cases.Rows)
                {
                    row.Values.TryAdd(variable.Name, variable.Default);
                }
            }
        }

        return cases;
    }

    internal static bool IsTall(List<List<string>> table)
    {
        if (table.Count == 0)
        {
            return false;
        }

        var header = table[0];
        return header.Count >= 2
            && string.Equals(header[0], "name", StringComparison.OrdinalIgnoreCase)
            && string.Equals(header[1], "value", StringComparison.OrdinalIgnoreCase);
    }

    internal static Cases ParseTall(List<List<string>> table)
    {
        var fields = new List<string>();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);

        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in table.Skip(1))
        {
            if (row.Count == 0 || row[0].Length == 0)
            {
                continue;
            }

            var baseName = Lower.SanitizeIdent(row[0]);
            if (!mapped.TryGetValue(baseName, out var field))
            {
                field = Lower.UniqueIdent(baseName, fields);
                fields.Add(field);
                mapped[baseName] = field;
            }

            values[field] = row.Count > 1 ? row[1] : "";
        }

        return new Cases
        {
            Fields = fields,
            Rows = [new Case { Label = "workbook", Values = values }],
        };
    }

    internal static Cases ParseWide(List<List<string>> table)
    {
        if (table.Count == 0)
        {
            return new Cases();
        }

        var header = table[0];

        int? labelCol = null;
        for (var i = 0; i < header.Count; i++)
        {
            var h = header[i].ToLowerInvariant();
            if (h is "case" or "testcase" or "test case" or "scenario")
            {
                labelCol = i;
                break;
            }
        }

        var fields = new List<string>();
        var columns = new List<(int Index, string Name)>();
        for (var i = 0; i < header.Count; i++)
        {
            if (labelCol == i || header[i].Length == 0)
            {
                continue;
            }

            var name = Lower.UniqueIdent(Lower.SanitizeIdent(header[i]), fields);
            fields.Add(name);
            columns.Add((i, name));
        }

        var rows = new List<Case>();
        for (var n = 0; n < table.Count - 1; n++)
        {
            var row = table[n + 1];

            var label = labelCol is int lc && lc < row.Count && row[lc].Length > 0
                ? row[lc]
                : $"row {n + 1}";

            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (index, name) in columns)
            {
                values[name] = index < row.Count ? row[index] : "";
            }

            rows.Add(new Case { Label = label, Values = values });
        }

        return new Cases { Fields = fields, Rows = rows };
    }
}
