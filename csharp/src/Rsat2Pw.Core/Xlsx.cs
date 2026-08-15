using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Rsat2Pw;

public static class Xlsx
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<string> SheetNames(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return ReadSheets(archive).Select(s => s.Name).ToList();
    }

    public static List<List<string>> ReadSheet(string path, string? sheetName)
    {
        using var archive = ZipFile.OpenRead(path);

        var sheets = ReadSheets(archive);
        if (sheets.Count == 0)
        {
            throw new InvalidDataException("workbook has no sheets");
        }

        var sheet = sheetName is null
            ? sheets[0]
            : sheets.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.Ordinal))
              ?? throw new InvalidDataException($"Worksheet '{sheetName}' not found");

        var entry = archive.GetEntry(sheet.Path)
            ?? throw new InvalidDataException($"worksheet part '{sheet.Path}' missing from the workbook");

        var sharedStrings = ReadSharedStrings(archive);

        return ReadCells(ReadEntry(archive, entry), sharedStrings);
    }

    private sealed record SheetRef(string Name, string Path);

    private static List<SheetRef> ReadSheets(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("not an .xlsx workbook (xl/workbook.xml missing)");

        var workbook = Xml.Parse(ReadEntry(archive, workbookEntry));
        var rels = ReadRelationships(archive);

        var sheets = new List<SheetRef>();
        var sheetsElement = workbook.Child("sheets");
        if (sheetsElement is null)
        {
            return sheets;
        }

        foreach (var sheet in sheetsElement.ChildrenNamed("sheet"))
        {
            var name = sheet.Attr("name");
            if (name is null)
            {
                continue;
            }

            var id = sheet.Attr("id");
            string? target = null;
            if (id is not null)
            {
                rels.TryGetValue(id, out target);
            }

            target ??= $"worksheets/sheet{sheets.Count + 1}.xml";

            sheets.Add(new SheetRef(name, NormalizePart(target)));
        }

        return sheets;
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive archive)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var entry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (entry is null)
        {
            return map;
        }

        var doc = Xml.Parse(ReadEntry(archive, entry));
        foreach (var rel in doc.ChildrenNamed("Relationship"))
        {
            var id = rel.Attr("Id");
            var target = rel.Attr("Target");
            if (id is not null && target is not null)
            {
                map[id] = target;
            }
        }

        return map;
    }

    private static string NormalizePart(string target)
    {
        var cleaned = target.Replace('\\', '/');
        return cleaned.StartsWith('/') ? cleaned.TrimStart('/') : $"xl/{cleaned}";
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var result = new List<string>();

        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return result;
        }

        var doc = Xml.Parse(ReadEntry(archive, entry));
        foreach (var si in doc.ChildrenNamed("si"))
        {
            result.Add(ConcatText(si));
        }

        return result;
    }

    private static string ConcatText(Element element)
    {
        var builder = new StringBuilder();

        void Walk(Element node)
        {
            if (string.Equals(node.Name, "t", StringComparison.Ordinal))
            {
                builder.Append(node.Text);
                return;
            }

            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(element);
        return builder.ToString();
    }

    internal static List<List<string>> ReadCells(string sheetXml, List<string> sharedStrings)
    {
        var doc = Xml.Parse(sheetXml);
        var sheetData = doc.FindDescendant("sheetData");
        if (sheetData is null)
        {
            return [];
        }

        var cells = new List<(int Row, int Col, string Value)>();
        var rowIndex = 0;

        foreach (var row in sheetData.ChildrenNamed("row"))
        {
            if (int.TryParse(row.Attr("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var declared)
                && declared > 0)
            {
                rowIndex = declared - 1;
            }

            var colIndex = 0;
            foreach (var cell in row.ChildrenNamed("c"))
            {
                var reference = cell.Attr("r");
                if (reference is not null)
                {
                    var parsed = ColumnFromReference(reference);
                    if (parsed >= 0)
                    {
                        colIndex = parsed;
                    }
                }

                var value = CellValue(cell, sharedStrings);
                if (value.Length > 0)
                {
                    cells.Add((rowIndex, colIndex, value));
                }

                colIndex += 1;
            }

            rowIndex += 1;
        }

        if (cells.Count == 0)
        {
            return [];
        }

        var minRow = cells.Min(c => c.Row);
        var maxRow = cells.Max(c => c.Row);
        var minCol = cells.Min(c => c.Col);
        var maxCol = cells.Max(c => c.Col);
        var width = maxCol - minCol + 1;

        var table = new List<List<string>>();
        for (var r = minRow; r <= maxRow; r++)
        {
            table.Add(Enumerable.Repeat("", width).ToList());
        }

        foreach (var (row, col, value) in cells)
        {
            table[row - minRow][col - minCol] = value;
        }

        return table;
    }

    private static string CellValue(Element cell, List<string> sharedStrings)
    {
        var type = cell.Attr("t") ?? "n";

        switch (type)
        {
            case "s":
            {
                var raw = cell.TextOf("v");
                if (raw is not null
                    && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 0
                    && index < sharedStrings.Count)
                {
                    return sharedStrings[index];
                }

                return "";
            }

            case "inlineStr":
            {
                var inline = cell.Child("is");
                return inline is null ? "" : ConcatText(inline);
            }

            case "str":
            {
                return cell.TextOf("v") ?? "";
            }

            case "b":
            {
                return cell.TextOf("v") == "1" ? "true" : "false";
            }

            case "e":
            {
                return cell.TextOf("v") ?? "";
            }

            default:
            {
                var raw = cell.TextOf("v");
                if (raw is null)
                {
                    return "";
                }

                return FormatNumber(raw);
            }
        }
    }

    private static string FormatNumber(string raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return raw;
        }

        if (Math.Abs(number % 1) < double.Epsilon
            && Math.Abs(number) < 9.007199254740992E15)
        {
            return ((long)number).ToString(CultureInfo.InvariantCulture);
        }

        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static int ColumnFromReference(string reference)
    {
        var column = 0;
        var sawLetter = false;

        foreach (var c in reference)
        {
            if (char.IsAsciiLetter(c))
            {
                column = (column * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
                sawLetter = true;
            }
            else
            {
                break;
            }
        }

        return sawLetter ? column - 1 : -1;
    }

    private static string ReadEntry(ZipArchive archive, ZipArchiveEntry entry)
    {
        _ = archive;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
