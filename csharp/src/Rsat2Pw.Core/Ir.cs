namespace Rsat2Pw;

public abstract record Value
{
    public sealed record Literal(string Text) : Value;

    public sealed record Variable(string Name) : Value;

    public string ToTs() => this switch
    {
        Literal l => $"'{Ir.EscapeTs(l.Text)}'",
        Variable v => $"params.{v.Name}",
        _ => throw new InvalidOperationException("unreachable"),
    };
}

public enum MenuItemKind
{
    Display,
    Action,
    Output,
}

/// <summary>
/// The normalized action IR. Mirrors <c>src/ir.rs</c>; the two implementations
/// are held to byte-identical output, so any change here needs the same change
/// there.
/// </summary>
public abstract record Action
{
    /// <summary>Deep-link to a menu item.</summary>
    public sealed record Navigate(string MenuItem, MenuItemKind Kind) : Action;

    /// <summary>A form scope (<c>IsForm</c>), which narrows control lookups.</summary>
    public sealed record Form(string FormName, List<Action> Children) : Action;

    /// <summary>A recorder step group (<c>IsStepGroup</c>).</summary>
    public sealed record Step(string Label, List<Action> Children) : Action;

    /// <summary><c>CommandName=Click</c>, against <c>ControlName</c>.</summary>
    public sealed record Click(string Control, string ControlType) : Action;

    /// <summary><c>CommandName=TabShown</c>.</summary>
    public sealed record Tab(string Control) : Action;

    /// <summary><c>PropertyUserAction</c> with <c>PropertyName=Value</c>.</summary>
    public sealed record SetValue(string Control, string ControlType, Value Value) : Action;

    /// <summary>The same, addressed into a grid row.</summary>
    public sealed record SetGridValue(
        string Grid,
        string Column,
        int Row,
        string ControlType,
        Value Value) : Action;

    /// <summary><c>CommandName=RequestPopup</c>.</summary>
    public sealed record OpenLookup(string Control) : Action;

    /// <summary><c>CommandName=ResolveChanges</c>.</summary>
    public sealed record CommitLookup(string Control) : Action;

    /// <summary><c>CommandName=ChangeSelectedIndexInCache</c>.</summary>
    public sealed record SelectRow(string Grid, int Row) : Action;

    /// <summary><c>CommandName=MarkActiveRow</c>.</summary>
    public sealed record MarkRow(string Grid) : Action;

    /// <summary><c>CommandName=NavigationAction</c>.</summary>
    public sealed record OpenRow(string Grid) : Action;

    /// <summary><c>CommandName=ApplyFiltersForTaskRecorder</c>, unpacked from JSON.</summary>
    public sealed record Filter(
        string Control,
        string Field,
        string Label,
        string Operator,
        Value Value) : Action;

    /// <summary>
    /// <c>CommandName=SelectionPathChanged</c> - pick a node in a tree. The
    /// path arrives backslash-separated, as the tree renders it.
    /// </summary>
    public sealed record SelectTreeItem(string Control, Value Path) : Action;

    /// <summary>
    /// <c>CommandName=ExecuteShortcuts</c> - a named client shortcut, such as
    /// the one that flips a page between View and Edit mode.
    /// </summary>
    public sealed record Shortcut(string Name) : Action;

    /// <summary><c>CommandName=RequestClose</c>.</summary>
    public sealed record CloseForm : Action;

    public sealed record Validate(string Control, Value Expected) : Action;

    /// <summary>A <c>TaskUserAction</c> sub-task boundary; emitted as a comment.</summary>
    public sealed record Marker(string Text) : Action;

    /// <summary>
    /// Recorded, understood, and deliberately not replayed. Distinct from
    /// <see cref="Unsupported"/>: "this one does nothing" and "we have no rule
    /// for this" are different admissions.
    /// </summary>
    public sealed record Skipped(
        string RawKind,
        string Detail,
        SortedDictionary<string, string> Props) : Action;

    public sealed record Unsupported(
        string RawKind,
        string Detail,
        SortedDictionary<string, string> Props) : Action;

    public string OpName() => this switch
    {
        Navigate => "navigate",
        Form => "withForm",
        Step => "test.step",
        Click => "click",
        Tab => "tab",
        SetValue => "setField",
        SetGridValue => "setGridCell",
        OpenLookup => "openLookup",
        CommitLookup => "commitLookup",
        SelectRow => "selectRow",
        MarkRow => "markRow",
        OpenRow => "openRow",
        Filter => "filter",
        SelectTreeItem => "selectTreeItem",
        Shortcut => "shortcut",
        CloseForm => "closeForm",
        Validate => "expectValue",
        Marker => "marker",
        Skipped => "skipped",
        Unsupported => "unsupported",
        _ => throw new InvalidOperationException("unreachable"),
    };

    public string Summary() => this switch
    {
        Navigate a => $"{a.MenuItem} ({Ir.KindAsString(a.Kind)})",
        Form a => a.FormName,
        Step a => a.Label,
        Click a => $"{a.Control} ({a.ControlType})",
        Tab a => a.Control,
        SetValue a => $"{a.Control} = {a.Value.ToTs()}",
        SetGridValue a => $"{a.Grid}[{a.Row}].{a.Column} = {a.Value.ToTs()}",
        OpenLookup a => a.Control,
        CommitLookup a => a.Control,
        SelectRow a => $"{a.Grid}[{a.Row}]",
        MarkRow a => a.Grid,
        OpenRow a => a.Grid,
        Filter a => $"{a.Field} {a.Operator} {a.Value.ToTs()}",
        SelectTreeItem a => $"{a.Control} <- {a.Path.ToTs()}",
        Shortcut a => a.Name,
        CloseForm => "",
        Validate a => $"{a.Control} == {a.Expected.ToTs()}",
        Marker a => a.Text,
        Skipped a => a.Detail.Length == 0 ? a.RawKind : $"{a.RawKind} ({a.Detail})",
        Unsupported a => a.RawKind,
        _ => throw new InvalidOperationException("unreachable"),
    };

    public IReadOnlyList<Action> ChildActions() => this switch
    {
        Step s => s.Children,
        Form f => f.Children,
        _ => [],
    };
}

public sealed record Variable(string Name, string Default);

public sealed class TestCase
{
    public required string Name { get; init; }

    public List<Variable> Variables { get; init; } = [];

    public List<Action> Actions { get; init; } = [];

    public int UnsupportedCount() => CountMatching(Actions, static a => a is Action.Unsupported);

    public int SkippedCount() => CountMatching(Actions, static a => a is Action.Skipped);

    private static int CountMatching(IReadOnlyList<Action> actions, Func<Action, bool> predicate)
    {
        var total = 0;
        foreach (var action in actions)
        {
            total += (predicate(action) ? 1 : 0) + CountMatching(action.ChildActions(), predicate);
        }

        return total;
    }

    public int ActionCount() => CountAll(Actions);

    private static int CountAll(IReadOnlyList<Action> actions)
    {
        var total = 0;
        foreach (var action in actions)
        {
            total += 1 + CountAll(action.ChildActions());
        }

        return total;
    }
}

public static class Ir
{
    public static string EscapeTs(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("'", "\\'", StringComparison.Ordinal)
         .Replace("\r", "\\r", StringComparison.Ordinal)
         .Replace("\n", "\\n", StringComparison.Ordinal);

    public static string EscapeTemplateLiteral(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("`", "\\`", StringComparison.Ordinal)
         .Replace("${", "\\${", StringComparison.Ordinal)
         .Replace("\r", "\\r", StringComparison.Ordinal)
         .Replace("\n", "\\n", StringComparison.Ordinal);

    public static string CommentSafe(string s) =>
        s.Replace("\r", " ", StringComparison.Ordinal)
         .Replace("\n", " ", StringComparison.Ordinal);

    public static string KindAsString(MenuItemKind kind) => kind switch
    {
        MenuItemKind.Display => "Display",
        MenuItemKind.Action => "Action",
        MenuItemKind.Output => "Output",
        _ => "Display",
    };
}
