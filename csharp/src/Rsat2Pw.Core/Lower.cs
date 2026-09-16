using System.Globalization;

namespace Rsat2Pw;

/// <summary>
/// Lowering: <see cref="RecNode"/> -> <see cref="Action"/>.
///
/// A direct mirror of <c>src/lower.rs</c>. The vocabulary is the one real
/// exports use: five node kinds, and within <c>CommandUserAction</c> a small
/// set of command names. The recorder puts the *verb* in <c>CommandName</c>
/// and the *target* in <c>ControlName</c>, with <c>ControlType</c> saying what
/// kind of control it is.
/// </summary>
public static class Lower
{
    private static readonly string[] ControlKeys = ["ControlName", "Control", "TargetControl", "ControlId"];
    private static readonly string[] ValueKeys = ["Value", "NewValue", "Text", "InputValue"];
    private static readonly string[] VariableKeys = ["VariableName", "Variable", "ParameterName"];
    private static readonly string[] LabelKeys = ["Description", "CustomDescription", "Annotation", "Name"];

    /// <summary>
    /// Forms that belong to the recorder, not to the business process. Task
    /// Recorder runs inside the client it is recording, so its own pane shows
    /// up as a form scope wrapped around perfectly ordinary actions.
    /// </summary>
    private static readonly string[] RecorderInternalForms =
    [
        "SysBPMPane",
        "SysTaskRecorderPane",
        "SysTaskRecorderForm",
        "SysTaskRecorderStartForm",
        "SysTaskRecorderStopForm",
    ];

    public static readonly string[] ReservedIdents = ["__case"];

    /// <summary>
    /// Collects the test-data fields as lowering walks the recording. RSAT
    /// parameterizes every recorded input - that is what fills the columns of
    /// its parameter workbook - so each recorded value becomes a variable,
    /// defaulting to what the recorder captured.
    /// </summary>
    private sealed class Ctx
    {
        public List<Variable> Variables { get; } = [];

        private List<string> Taken { get; } = [];

        public Ctx(IEnumerable<KeyValuePair<string, string>> seed)
        {
            foreach (var (name, defaultValue) in seed)
            {
                Declare(SanitizeIdent(name), defaultValue);
            }
        }

        public string Declare(string candidate, string defaultValue)
        {
            var name = UniqueIdent(candidate, Taken);
            Taken.Add(name);
            Variables.Add(new Variable(name, defaultValue));
            return name;
        }

        /// <summary>A variable the recording named explicitly; repeated references reuse it.</summary>
        public Value Named(string raw, string defaultValue)
        {
            var candidate = SanitizeIdent(raw);
            var existing = Variables.FirstOrDefault(v => string.Equals(v.Name, candidate, StringComparison.Ordinal));
            return existing is not null
                ? new Value.Variable(existing.Name)
                : new Value.Variable(Declare(candidate, defaultValue));
        }

        /// <summary>A variable derived from the control that was edited.</summary>
        public Value Derived(string baseName, string defaultValue)
        {
            var candidate = SanitizeIdent(baseName.Length == 0 ? "value" : baseName);
            return new Value.Variable(Declare(candidate, defaultValue));
        }
    }

    public static TestCase Run(Recording recording)
    {
        var ctx = new Ctx(recording.Variables);
        var actions = LowerNodes(recording.Nodes, ctx);

        var referenced = new List<string>();
        CollectReferenced(actions, referenced);
        foreach (var name in referenced)
        {
            if (!ctx.Variables.Any(v => string.Equals(v.Name, name, StringComparison.Ordinal)))
            {
                ctx.Variables.Add(new Variable(name, ""));
            }
        }

        return new TestCase
        {
            Name = recording.Name,
            Variables = ctx.Variables,
            Actions = actions,
        };
    }

    private static void CollectReferenced(IReadOnlyList<Action> actions, List<string> outNames)
    {
        static void Note(Value value, List<string> outNames)
        {
            if (value is Value.Variable v && !outNames.Contains(v.Name, StringComparer.Ordinal))
            {
                outNames.Add(v.Name);
            }
        }

        foreach (var action in actions)
        {
            switch (action)
            {
                case Action.SetValue a:
                    Note(a.Value, outNames);
                    break;
                case Action.SetGridValue a:
                    Note(a.Value, outNames);
                    break;
                case Action.Filter a:
                    Note(a.Value, outNames);
                    break;
                case Action.Validate a:
                    Note(a.Expected, outNames);
                    break;
            }

            CollectReferenced(action.ChildActions(), outNames);
        }
    }

    private static List<Action> LowerNodes(IReadOnlyList<RecNode> nodes, Ctx ctx) =>
        nodes.SelectMany(n => LowerNode(n, ctx)).ToList();

    private static List<Action> LowerNode(RecNode node, Ctx ctx)
    {
        switch (node.Kind.ToLowerInvariant())
        {
            case "scope":
                return LowerScope(node, ctx);
            case "menuitemuseraction":
                return [LowerMenuItem(node)];
            case "commanduseraction":
                return [LowerCommand(node, ctx)];
            case "propertyuseraction":
                return [LowerProperty(node, ctx)];
            case "taskuseraction":
                return [LowerTaskMarker(node)];
            // A note the recorder was asked to keep, and a bare annotation.
            // Both are commentary on the recording rather than something to
            // replay.
            case "infouseraction":
            case "annotationuseraction":
                return [LowerNote(node)];
        }

        if (node.HasProp("IsForm") || node.HasProp("IsStepGroup"))
        {
            return LowerScope(node, ctx);
        }

        if (node.HasProp("MenuItemName"))
        {
            return [LowerMenuItem(node)];
        }

        return [LowerLegacy(node, ctx)];
    }

    private static List<Action> LowerScope(RecNode node, Ctx ctx)
    {
        var children = LowerNodes(node.Children, ctx);

        // An empty scope has nothing to wrap. Real recordings are full of them:
        // the client re-enters a form scope every time focus returns to it.
        if (children.Count == 0)
        {
            return children;
        }

        if (node.Flag("IsForm"))
        {
            var form = node.Prop("Name", "FormName", "RecordingName") ?? "";
            if (form.Length == 0 || IsRecorderInternal(form))
            {
                return children;
            }

            return [new Action.Form(form, children)];
        }

        // A step group the user made while recording is public. The client
        // marks its own groupings the same way - every lookup it opens becomes
        // a private `<control>_RequestPopup` group - and those are plumbing,
        // not intent.
        if (node.Flag("IsStepGroup"))
        {
            var scopeType = node.Prop("ScopeType");
            if (scopeType is not null && string.Equals(scopeType, "Private", StringComparison.OrdinalIgnoreCase))
            {
                return children;
            }

            return [new Action.Step(ScopeLabel(node), children)];
        }

        // A private scope with neither flag is client plumbing.
        if (node.HasProp("IsForm") || node.HasProp("IsStepGroup"))
        {
            return children;
        }

        // Older exports had no flags at all. Group only when the recorder gave
        // the scope a human label.
        var label = node.Prop(LabelKeys);
        return label is { Length: > 0 }
            ? [new Action.Step(label, children)]
            : children;
    }

    private static string ScopeLabel(RecNode node)
    {
        var label = node.Prop(LabelKeys);
        return label is { Length: > 0 } ? label : "Recorded step";
    }

    private static bool IsRecorderInternal(string form) =>
        RecorderInternalForms.Any(f => string.Equals(f, form, StringComparison.OrdinalIgnoreCase));

    private static Action LowerMenuItem(RecNode node)
    {
        var menuItem = node.Prop("MenuItemName", "MenuItem", "Name") ?? "";
        var kind = (node.Prop("MenuItemType", "MenuItemKind") ?? "Display").ToLowerInvariant() switch
        {
            "action" => MenuItemKind.Action,
            "output" => MenuItemKind.Output,
            _ => MenuItemKind.Display,
        };

        return new Action.Navigate(menuItem, kind);
    }

    private static Action LowerCommand(RecNode node, Ctx ctx)
    {
        var command = node.Prop("CommandName", "Command") ?? "";
        var control = node.Prop(ControlKeys) ?? "";
        var controlType = node.Prop("ControlType") ?? "";

        // A grid command names the list in `ListContext`; `ControlName` repeats it.
        var listContext = node.Prop("ListContext");
        var grid = listContext is { Length: > 0 } ? listContext : control;

        switch (command.ToLowerInvariant())
        {
            case "click" when control.Length > 0:
                return new Action.Click(control, controlType);

            case "tabshown" when control.Length > 0:
                return new Action.Tab(control);

            case "requestpopup" when control.Length > 0:
                return new Action.OpenLookup(control);

            case "resolvechanges" when control.Length > 0:
                return new Action.CommitLookup(control);

            case "navigationaction" when grid.Length > 0:
                return new Action.OpenRow(grid);

            case "markactiverow" when grid.Length > 0:
                return new Action.MarkRow(grid);

            // `ChangeSelectedIndex` is the same move without the cache suffix.
            case "changeselectedindexincache" when grid.Length > 0:
            case "changeselectedindex" when grid.Length > 0:
            {
                // The new cursor position is the first command argument.
                var row = int.TryParse(
                    node.Arg(0)?.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : 0;

                return new Action.SelectRow(grid, row);
            }

            // `ApplyFilters` is the same command under an older name, and
            // carries the same JSON payload. If it ever does not, LowerFilter
            // finds no field and says so rather than emitting a filter of
            // nothing.
            case "applyfiltersfortaskrecorder":
            case "applyfilters":
                return LowerFilter(node, ctx, control);

            case "resetfilters":
                return new Action.ResetFilters(control);

            // Preparing the filter pane so a field can be filtered on.
            // filter() drives the column header directly and never needs the
            // pane set up, so replaying this would only open UI nothing else
            // touches.
            case "addafilterfield":
                return Skipped(node, "prepares the filter pane; filter() does not use it");

            // Following a link rendered inside a field. The target is the
            // control, exactly as for an ordinary click.
            case "executehyperlink" when control.Length > 0:
                return new Action.Click(control, controlType);

            case "expandingpath" when control.Length > 0:
                return new Action.ExpandTreeItem(control, ctx.Derived(control, node.Arg(0) ?? ""));

            case "selectionpathchanged" when control.Length > 0:
                // The tree path is the value the user picked, so it is test
                // data like any other recorded input.
                return new Action.SelectTreeItem(control, ctx.Derived(control, node.Arg(0) ?? ""));

            // The shortcut name is the whole instruction; without it there is
            // nothing to replay.
            case "executeshortcuts":
            {
                var shortcut = node.Arg(0);
                return shortcut is { Length: > 0 }
                    ? new Action.Shortcut(shortcut)
                    : Unsupported(node);
            }

            // Opening the filter flyout. `filter()` does that itself as part of
            // applying one, so replaying this would just toggle the pane shut.
            case "getfilters":
                return Skipped(node, "opens the filter pane; filter() does that itself");

            case "requestclose":
                return new Action.CloseForm();

            default:
                return Unsupported(node);
        }
    }

    /// <summary>
    /// Unpack <c>ApplyFiltersForTaskRecorder</c>, whose first command argument
    /// is a JSON array describing the filter the user typed.
    /// </summary>
    private static Action LowerFilter(RecNode node, Ctx ctx, string control)
    {
        var json = node.Arg(0) ?? "";

        // `FieldName` appears twice: once inside the (often null) `Capability`
        // object, where it is blank, and once at the top level where it is real.
        var field = JsonString(json, "FieldName") ?? JsonString(json, "FieldLabel") ?? "";
        var label = JsonString(json, "FieldLabel") ?? "";
        var op = JsonString(json, "Operator") ?? "";

        if (field.Length == 0)
        {
            return Unsupported(node);
        }

        var recorded = JsonFirstArrayString(json, "Values") ?? "";
        var value = ctx.Derived(field, recorded);

        return new Action.Filter(control, field, label, op, value);
    }

    private static Action LowerProperty(RecNode node, Ctx ctx)
    {
        var property = node.Prop("PropertyName") ?? "Value";
        if (!string.Equals(property, "Value", StringComparison.OrdinalIgnoreCase))
        {
            return Unsupported(node);
        }

        var control = node.Prop(ControlKeys) ?? "";
        if (control.Length == 0)
        {
            return Unsupported(node);
        }

        var controlType = node.Prop("ControlType") ?? "";
        var value = ValueOf(node, ctx, control);

        // A cell edit names its grid in `ListContext` and its row in `RowIndex`;
        // a plain field edit leaves both nil.
        var grid = node.Prop("ListContext", "GridName", "Grid") ?? "";
        var hasRow = int.TryParse(
            node.Prop("RowIndex", "Row")?.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var row);

        if (grid.Length > 0 && hasRow)
        {
            return new Action.SetGridValue(grid, control, row, controlType, value);
        }

        return new Action.SetValue(control, controlType, value);
    }

    /// <summary>
    /// A recorded note or annotation. It carries no behaviour, so it is
    /// emitted as a comment - the recording said it for a reason, and dropping
    /// it loses the only thing the recorder was told in prose.
    /// </summary>
    private static Action LowerNote(RecNode node) =>
        new Action.Marker(node.Prop("Notes", "Text", "Description", "Comment") ?? "Note");

    private static Action LowerTaskMarker(RecNode node)
    {
        var label = node.Prop("Description", "Name", "Comment") ?? "Sub-task";
        var phase = node.Prop("UserActionType");

        return phase is { Length: > 0 }
            ? new Action.Marker($"{label} ({phase})")
            : new Action.Marker(label);
    }

    /// <summary>
    /// Shapes from older exports, kept because the parser is deliberately
    /// tolerant and a recording that predates the current schema should still
    /// convert.
    /// </summary>
    private static Action LowerLegacy(RecNode node, Ctx ctx)
    {
        var kind = node.Kind.ToLowerInvariant();
        var control = node.Prop(ControlKeys) ?? "";

        if (kind.Contains("validat", StringComparison.Ordinal)
            || kind.Contains("verif", StringComparison.Ordinal)
            || kind.Contains("assert", StringComparison.Ordinal))
        {
            return new Action.Validate(control, ValueOf(node, ctx, control));
        }

        if (control.Length > 0
            && (kind.Contains("input", StringComparison.Ordinal) || node.HasProp(ValueKeys)))
        {
            return new Action.SetValue(control, node.Prop("ControlType") ?? "", ValueOf(node, ctx, control));
        }

        return Unsupported(node);
    }

    private static Value ValueOf(RecNode node, Ctx ctx, string baseName)
    {
        var recorded = node.Prop(ValueKeys) ?? "";
        var variable = node.Prop(VariableKeys);

        return variable is { Length: > 0 }
            ? ctx.Named(variable, recorded)
            : ctx.Derived(baseName, recorded);
    }

    private static Action Unsupported(RecNode node) =>
        new Action.Unsupported(
            RawKind(node),
            node.Describe(),
            new SortedDictionary<string, string>(node.Props, StringComparer.Ordinal));

    private static Action Skipped(RecNode node, string why) =>
        new Action.Skipped(
            RawKind(node),
            why,
            new SortedDictionary<string, string>(node.Props, StringComparer.Ordinal));

    /// <summary>
    /// How an unmapped node is named in the report. A bare
    /// <c>CommandUserAction</c> is useless as a worklist entry - every command
    /// is one - so commands are reported by the verb that has no rule yet.
    /// </summary>
    private static string RawKind(RecNode node)
    {
        var command = node.Prop("CommandName", "Command");
        return command is { Length: > 0 } ? $"{node.Kind}:{command}" : node.Kind;
    }

    // -- the JSON the recorder embeds in command arguments --------------------
    //
    // Deliberately a scanner rather than a parser, and deliberately the same
    // dumb one as in Rust: the two implementations are held to byte-identical
    // output, and that is cheapest to keep true when neither is clever.

    /// <summary>
    /// First non-empty <c>"key": "value"</c> in the blob. The filter payload
    /// carries <c>FieldName</c> twice - blank inside <c>Capability</c>, real at
    /// the top level - so "first non-empty" picks the one that matters.
    /// </summary>
    public static string? JsonString(string json, string key)
    {
        var needle = $"\"{key}\"";
        var from = 0;

        while (true)
        {
            var at = json.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            var after = at + needle.Length;
            from = after;

            var rest = json[after..].TrimStart();
            if (!rest.StartsWith(':'))
            {
                continue;
            }

            rest = rest[1..].TrimStart();
            if (!rest.StartsWith('"'))
            {
                continue;
            }

            var value = ReadJsonString(rest[1..]);
            if (value is { Length: > 0 })
            {
                return value;
            }
        }
    }

    /// <summary>First string element of <c>"key": [ ... ]</c>.</summary>
    public static string? JsonFirstArrayString(string json, string key)
    {
        var needle = $"\"{key}\"";
        var at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = json[(at + needle.Length)..].TrimStart();
        if (!rest.StartsWith(':'))
        {
            return null;
        }

        rest = rest[1..].TrimStart();
        if (!rest.StartsWith('['))
        {
            return null;
        }

        rest = rest[1..].TrimStart();
        if (!rest.StartsWith('"'))
        {
            return null;
        }

        return ReadJsonString(rest[1..]);
    }

    /// <summary>Read up to the closing quote, honouring backslash escapes.</summary>
    private static string? ReadJsonString(string rest)
    {
        var builder = new System.Text.StringBuilder();

        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];

            if (c == '"')
            {
                return builder.ToString();
            }

            if (c == '\\')
            {
                i += 1;
                if (i >= rest.Length)
                {
                    return null;
                }

                builder.Append(rest[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    var escaped => escaped,
                });
                continue;
            }

            builder.Append(c);
        }

        return null;
    }

    public static string SanitizeIdent(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        var result = new string(chars);

        if (result.Length > 0 && char.IsAsciiDigit(result[0]))
        {
            result = "_" + result;
        }

        return result.Length == 0 ? "_" : result;
    }

    public static string UniqueIdent(string candidate, IReadOnlyList<string> taken)
    {
        var name = candidate;
        var n = 2;

        while (ReservedIdents.Contains(name, StringComparer.Ordinal) || taken.Contains(name, StringComparer.Ordinal))
        {
            name = $"{candidate}_{n}";
            n += 1;
        }

        return name;
    }
}
