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

public abstract record Action
{
    public sealed record Navigate(string MenuItem, MenuItemKind Kind) : Action;

    public sealed record EnterForm(string Form) : Action;

    public sealed record LeaveForm(string Form) : Action;

    public sealed record SetValue(string Control, Value Value) : Action;

    public sealed record SetGridValue(string Grid, string Column, int Row, Value Value) : Action;

    public sealed record Click(string Control) : Action;

    public sealed record Command(string Name) : Action;

    public sealed record Lookup(string Control, Value Value) : Action;

    public sealed record Dialog(string Name, List<Action> Children) : Action;

    public sealed record Validate(string Control, Value Expected) : Action;

    public sealed record Step(string Label, List<Action> Children) : Action;

    public sealed record Unsupported(
        string RawKind,
        string Detail,
        SortedDictionary<string, string> Props) : Action;

    public string OpName() => this switch
    {
        Navigate => "navigate",
        EnterForm => "enterForm",
        LeaveForm => "leaveForm",
        SetValue => "setField",
        SetGridValue => "setGridCell",
        Click => "click",
        Command => "command",
        Lookup => "lookup",
        Validate => "expectValue",
        Dialog => "withDialog",
        Step => "test.step",
        Unsupported => "unsupported",
        _ => throw new InvalidOperationException("unreachable"),
    };

    public string Summary() => this switch
    {
        Navigate a => $"{a.MenuItem} ({Ir.KindAsString(a.Kind)})",
        EnterForm a => a.Form,
        LeaveForm a => a.Form,
        SetValue a => $"{a.Control} = {a.Value.ToTs()}",
        SetGridValue a => $"{a.Grid}[{a.Row}].{a.Column} = {a.Value.ToTs()}",
        Click a => a.Control,
        Command a => a.Name,
        Lookup a => $"{a.Control} <- {a.Value.ToTs()}",
        Validate a => $"{a.Control} == {a.Expected.ToTs()}",
        Dialog a => a.Name,
        Step a => a.Label,
        Unsupported a => a.RawKind,
        _ => throw new InvalidOperationException("unreachable"),
    };

    public IReadOnlyList<Action> ChildActions() => this switch
    {
        Step s => s.Children,
        Dialog d => d.Children,
        _ => [],
    };
}

public sealed record Variable(string Name, string Default);

public sealed class TestCase
{
    public required string Name { get; init; }

    public List<Variable> Variables { get; init; } = [];

    public List<Action> Actions { get; init; } = [];

    public int UnsupportedCount() => Walk(Actions);

    private static int Walk(IReadOnlyList<Action> actions)
    {
        var total = 0;
        foreach (var action in actions)
        {
            if (action is Action.Unsupported)
            {
                total += 1;
            }
            else
            {
                total += Walk(action.ChildActions());
            }
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
