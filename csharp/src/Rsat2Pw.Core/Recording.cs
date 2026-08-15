using System.IO.Compression;
using System.Text;

namespace Rsat2Pw;

public sealed class RecNode
{
    public required string Kind { get; init; }

    public SortedDictionary<string, string> Props { get; } = new(StringComparer.Ordinal);

    public List<RecNode> Children { get; } = [];

    public string? Prop(params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var (key, value) in Props)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        return null;
    }

    public bool HasProp(params string[] names) => Prop(names) is not null;

    public string Describe()
    {
        var parts = Props
            .Where(p => p.Value.Length > 0)
            .Select(p => $"{p.Key}={p.Value}")
            .Take(4);

        return string.Join(", ", parts);
    }
}

public sealed class Recording
{
    public required string Name { get; init; }

    public List<KeyValuePair<string, string>> Variables { get; init; } = [];

    public List<RecNode> Nodes { get; init; } = [];
}

public static class RecordingReader
{
    private static readonly string[] ChildWrappers =
        ["Childs", "Children", "Nodes", "ChildNodes", "Steps"];

    private static readonly string[] NodeElements =
    [
        "AxTaskRecordingNode",
        "Node",
        "UserAction",
        "TaskUserActionNode",
        "AxTaskRecordingUserActionNode",
    ];

    public static Recording Load(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"reading {path}: {ex.Message}", ex);
        }

        var xmlText = bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K'
            ? ExtractFromArchive(bytes)
            : DecodeUtf8(bytes);

        return Parse(xmlText);
    }

    private static string DecodeUtf8(byte[] bytes) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(bytes)
            .TrimStart('﻿');

    private static string ExtractFromArchive(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        ZipArchiveEntry? best = null;
        var bestScore = int.MinValue;

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.ToLowerInvariant();
            if (!name.EndsWith(".xml", StringComparison.Ordinal))
            {
                continue;
            }

            var score = name.Contains("recording", StringComparison.Ordinal) ? 2 : 1;

            if (best is null || score > bestScore)
            {
                best = entry;
                bestScore = score;
            }
        }

        if (best is null)
        {
            throw new InvalidDataException("no .xml entry found inside the .axtr archive");
        }

        using var stream = best.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return DecodeUtf8(memory.ToArray());
    }

    public static Recording Parse(string xmlText)
    {
        var doc = Xml.Parse(xmlText);

        var name = doc.TextOf("Name") ?? doc.TextOf("RecordingName") ?? "Recording";

        var variables = CollectVariables(doc);

        var rootContainer = ChildWrappers
            .Select(doc.Child)
            .FirstOrDefault(c => c is not null) ?? doc;

        var nodes = rootContainer.Children
            .Where(IsNodeElement)
            .Select(NodeFrom)
            .ToList();

        return new Recording
        {
            Name = name,
            Variables = variables,
            Nodes = nodes,
        };
    }

    private static bool IsNodeElement(Element element)
    {
        if (NodeElements.Any(n => string.Equals(element.Name, n, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var lower = element.Name.ToLowerInvariant();
        return lower.Contains("node", StringComparison.Ordinal)
            || lower.Contains("useraction", StringComparison.Ordinal);
    }

    private static RecNode NodeFrom(Element element)
    {
        var kind = element.Attr("type")
            ?? element.TextOf("ActionType")
            ?? element.TextOf("Type")
            ?? element.Name;

        var node = new RecNode { Kind = kind };
        Flatten(element, node);
        return node;
    }

    private static void Flatten(Element element, RecNode target)
    {
        foreach (var child in element.Children)
        {
            if (ChildWrappers.Any(w => string.Equals(child.Name, w, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var grand in child.Children)
                {
                    if (IsNodeElement(grand))
                    {
                        target.Children.Add(NodeFrom(grand));
                    }
                    else
                    {
                        Flatten(grand, target);
                    }
                }
            }
            else if (IsNodeElement(child) && !child.IsScalar)
            {
                target.Children.Add(NodeFrom(child));
            }
            else if (child.IsScalar)
            {
                var text = child.Text.Trim();
                if (text.Length > 0)
                {
                    target.Props.TryAdd(child.Name, text);
                }
            }
            else
            {
                Flatten(child, target);
            }
        }
    }

    private static List<KeyValuePair<string, string>> CollectVariables(Element doc)
    {
        var found = new List<KeyValuePair<string, string>>();

        void Walk(Element element)
        {
            var lower = element.Name.ToLowerInvariant();
            if (lower.Contains("variable", StringComparison.Ordinal)
                && !lower.EndsWith("variables", StringComparison.Ordinal))
            {
                var name = element.TextOf("Name")
                    ?? element.TextOf("VariableName")
                    ?? element.Attr("Name");

                if (name is not null)
                {
                    var value = element.TextOf("Value") ?? element.TextOf("DefaultValue") ?? "";
                    found.Add(new KeyValuePair<string, string>(name, value));
                }

                return;
            }

            foreach (var child in element.Children)
            {
                Walk(child);
            }
        }

        Walk(doc);

        found.Sort(static (a, b) =>
        {
            var byName = string.CompareOrdinal(a.Key, b.Key);
            return byName != 0 ? byName : string.CompareOrdinal(a.Value, b.Value);
        });

        var deduped = new List<KeyValuePair<string, string>>();
        foreach (var entry in found)
        {
            if (deduped.Count == 0 || deduped[^1].Key != entry.Key)
            {
                deduped.Add(entry);
            }
        }

        return deduped;
    }
}
