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

    public static Cases FromWorkbook(string path, string? sheet, TestCase testCase)
    {
        var sheetName = sheet ?? Xlsx.SheetNames(path).FirstOrDefault()
            ?? throw new InvalidDataException("workbook has no sheets");

        var raw = Xlsx.ReadSheet(path, sheet);

        var table = raw
            .Select(r => r.Select(c => c.Trim()).ToList())
            .Where(r => r.Any(c => c.Length > 0))
            .ToList();

        var cases = IsTall(table) ? ParseTall(table) : ParseWide(table);
        cases.Source = $"{path} [{sheetName}]";

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
