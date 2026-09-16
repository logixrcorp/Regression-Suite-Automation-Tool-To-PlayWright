using System.IO.Compression;
using System.Text;

namespace Rsat2Pw;

/// <summary>
/// An <c>&lt;Annotation i:type="..."&gt;</c> hanging off a node. Kept out of
/// the property bag on purpose: a <c>FormAnnotation</c> carries
/// <c>MenuItemName</c>, and flattening that in would turn an ordinary click
/// into a navigation.
/// </summary>
public sealed class RecAnnotation
{
    public required string Kind { get; init; }

    public SortedDictionary<string, string> Props { get; } = new(StringComparer.Ordinal);
}

public sealed class RecNode
{
    public required string Kind { get; init; }

    public SortedDictionary<string, string> Props { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>&lt;Arguments&gt;&lt;CommandArgument&gt;&lt;Value&gt;</c> in order.
    /// These stay out of the property bag because a command argument is
    /// positional data (often a JSON blob), not a property called "Value" -
    /// and letting one in makes a filter command look exactly like a field edit.
    /// </summary>
    public List<string> Args { get; } = [];

    public List<RecAnnotation> Annotations { get; } = [];

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

    /// <summary>A property that reads as a boolean <c>true</c>.</summary>
    public bool Flag(string name) =>
        Prop(name) is { } value && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public string? Arg(int index) => index >= 0 && index < Args.Count ? Args[index] : null;

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

/// <summary>
/// Reads an <c>.axtr</c> archive (or a bare recording <c>.xml</c>) into a loose
/// node tree. Mirrors <c>src/recording.rs</c>.
///
/// A Task Recorder export is a <c>DataContract</c> serialization of
/// <c>Microsoft.Dynamics.Client.ServerForm.TaskRecording</c>. Two details are
/// load-bearing: the action tree hangs off <c>RootScope</c> rather than a
/// top-level <c>Nodes</c>, and <c>UserActions</c> is a list of <c>z:Ref</c>
/// pointers into that same tree - so treating it as a second action list
/// yields a recording made entirely of empty nodes.
/// </summary>
public static class RecordingReader
{
    private static readonly string[] ChildWrappers =
        ["Children", "Childs", "Nodes", "ChildNodes", "Steps"];

    private static readonly string[] NodeElements =
    [
        "AxTaskRecordingNode",
        "Node",
        "UserAction",
        "TaskUserActionNode",
        "AxTaskRecordingUserActionNode",
    ];

    /// <summary>
    /// Elements that look node-ish by name but are not actions.
    /// <c>UserActions</c> is the dangerous one: it matches every loose
    /// "contains UserAction" test.
    /// </summary>
    private static readonly string[] NotNodeElements =
    [
        "UserActions",
        "Annotations",
        "Annotation",
        "Arguments",
        "CommandArgument",
        "FormContexts",
        "NavigationPath",
        "Variables",
        "CanonicalUserAction",
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

        var name = doc.TextOf("Name")
            ?? doc.TextOf("Description")
            ?? doc.TextOf("RecordingName")
            ?? "Recording";

        var variables = CollectVariables(doc);

        var nodes = ActionContainer(doc).Children
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

    /// <summary>
    /// Find the element whose children are the recording's top-level actions.
    /// </summary>
    private static Element ActionContainer(Element doc)
    {
        // The real export: `<RootScope><Children>`. RootScope is itself a scope
        // node, so its own `Children` is the action list.
        var rootScope = doc.Child("RootScope");
        if (rootScope is not null)
        {
            return ChildWrappers.Select(rootScope.Child).FirstOrDefault(c => c is not null) ?? rootScope;
        }

        return ChildWrappers.Select(doc.Child).FirstOrDefault(c => c is not null) ?? doc;
    }

    private static bool IsNodeElement(Element element)
    {
        if (NotNodeElements.Any(n => string.Equals(element.Name, n, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (NodeElements.Any(n => string.Equals(element.Name, n, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var lower = element.Name.ToLowerInvariant();
        return lower.Contains("node", StringComparison.Ordinal)
            || lower.EndsWith("useraction", StringComparison.Ordinal);
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
            else if (string.Equals(child.Name, "Arguments", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var arg in child.ChildrenNamed("CommandArgument"))
                {
                    target.Args.Add(arg.TextOf("Value") ?? "");
                }
            }
            else if (string.Equals(child.Name, "Annotations", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var annotation in child.ChildrenNamed("Annotation"))
                {
                    target.Annotations.Add(AnnotationFrom(annotation));
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
            else if (!NotNodeElements.Any(n => string.Equals(child.Name, n, StringComparison.OrdinalIgnoreCase)))
            {
                // An unrecognized grouping element: keep descending so we do
                // not silently lose the actions underneath it.
                Flatten(child, target);
            }
        }
    }

    private static RecAnnotation AnnotationFrom(Element element)
    {
        var annotation = new RecAnnotation { Kind = element.Attr("type") ?? element.Name };

        foreach (var child in element.Children)
        {
            if (!child.IsScalar)
            {
                continue;
            }

            var text = child.Text.Trim();
            if (text.Length > 0)
            {
                annotation.Props[child.Name] = text;
            }
        }

        return annotation;
    }

    /// <summary>
    /// Variables in document order - the same order the Rust implementation
    /// produces, since the two are held to byte-identical output.
    /// </summary>
    private static List<KeyValuePair<string, string>> CollectVariables(Element doc)
    {
        var found = new List<KeyValuePair<string, string>>();

        void Visit(Element element)
        {
            var name = element.TextOf("Name")
                ?? element.TextOf("VariableName")
                ?? element.Attr("Name");

            if (name is null)
            {
                return;
            }

            var value = element.TextOf("Value") ?? element.TextOf("DefaultValue") ?? "";
            found.Add(new KeyValuePair<string, string>(name, value));
        }

        void Walk(Element element)
        {
            if (string.Equals(element.Name, "Variables", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in element.Children)
                {
                    Visit(child);
                }

                return;
            }

            foreach (var child in element.Children)
            {
                Walk(child);
            }
        }

        Walk(doc);
        return found;
    }
}
