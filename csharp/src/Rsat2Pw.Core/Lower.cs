using System.Globalization;

namespace Rsat2Pw;

public static class Lower
{
    private static readonly string[] ControlKeys = ["ControlName", "Control", "TargetControl", "ControlId"];
    private static readonly string[] FormKeys = ["FormName", "Form", "TargetForm"];
    private static readonly string[] ValueKeys = ["Value", "NewValue", "Text", "InputValue"];
    private static readonly string[] VariableKeys = ["VariableName", "Variable", "ParameterName"];
    private static readonly string[] AnnotationKeys = ["Annotation", "Description", "Caption", "Label", "Name"];

    public static readonly string[] ReservedIdents = ["__case"];

    public static TestCase Run(Recording recording)
    {
        var actions = recording.Nodes.Select(LowerNode).ToList();

        var taken = new List<string>();
        var variables = new List<Variable>();
        foreach (var (name, defaultValue) in recording.Variables)
        {
            var ident = UniqueIdent(SanitizeIdent(name), taken);
            taken.Add(ident);
            variables.Add(new Variable(ident, defaultValue));
        }

        var referenced = new List<string>();
        CollectReferenced(actions, referenced);
        foreach (var name in referenced)
        {
            if (!variables.Any(v => v.Name == name))
            {
                variables.Add(new Variable(name, ""));
            }
        }

        return new TestCase
        {
            Name = recording.Name,
            Variables = variables,
            Actions = actions,
        };
    }

    private static void CollectReferenced(IReadOnlyList<Action> actions, List<string> outNames)
    {
        static void Note(Value value, List<string> outNames)
        {
            if (value is Value.Variable v && !outNames.Contains(v.Name))
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
                case Action.Lookup a:
                    Note(a.Value, outNames);
                    break;
                case Action.Validate a:
                    Note(a.Expected, outNames);
                    break;
                default:
                    CollectReferenced(action.ChildActions(), outNames);
                    break;
            }
        }
    }

    private static Value ValueOf(RecNode node)
    {
        var variable = node.Prop(VariableKeys);
        if (!string.IsNullOrEmpty(variable))
        {
            return new Value.Variable(SanitizeIdent(variable));
        }

        return new Value.Literal(node.Prop(ValueKeys) ?? "");
    }

    private static List<Action> LowerChildren(RecNode node) =>
        node.Children.Select(LowerNode).ToList();

    private static Action LowerNode(RecNode node)
    {
        var kind = node.Kind.ToLowerInvariant();
        var control = node.Prop(ControlKeys) ?? "";

        if (node.Children.Count > 0
            && (kind.Contains("group", StringComparison.Ordinal)
                || kind.Contains("task", StringComparison.Ordinal)
                || kind.Contains("scope", StringComparison.Ordinal)))
        {
            return new Action.Step(
                node.Prop(AnnotationKeys) ?? "Recorded step",
                LowerChildren(node));
        }

        if ((kind.Contains("dialog", StringComparison.Ordinal)
             || kind.Contains("slider", StringComparison.Ordinal))
            && node.Children.Count > 0)
        {
            return new Action.Dialog(
                node.Prop(AnnotationKeys) ?? "Dialog",
                LowerChildren(node));
        }

        if (kind.Contains("menuitem", StringComparison.Ordinal) || node.HasProp("MenuItemName"))
        {
            var menuItem = node.Prop("MenuItemName", "MenuItem", "Name") ?? "";
            var menuKind = (node.Prop("MenuItemType", "MenuItemKind") ?? "Display").ToLowerInvariant() switch
            {
                "action" => MenuItemKind.Action,
                "output" => MenuItemKind.Output,
                _ => MenuItemKind.Display,
            };

            return new Action.Navigate(menuItem, menuKind);
        }

        if (kind.Contains("form", StringComparison.Ordinal))
        {
            var form = node.Prop(FormKeys) ?? "";
            var actionType = (node.Prop("ActionType", "FormAction", "State") ?? "Open").ToLowerInvariant();

            return actionType.Contains("close", StringComparison.Ordinal)
                ? new Action.LeaveForm(form)
                : new Action.EnterForm(form);
        }

        if (node.HasProp("GridName") || node.HasProp("ColumnName"))
        {
            var rowText = node.Prop("RowIndex", "Row");
            var row = int.TryParse(rowText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

            return new Action.SetGridValue(
                node.Prop("GridName", "Grid") ?? control,
                node.Prop("ColumnName", "Column", "FieldName") ?? "",
                row,
                ValueOf(node));
        }

        if (kind.Contains("lookup", StringComparison.Ordinal)
            || kind.Contains("segmentedentry", StringComparison.Ordinal))
        {
            return new Action.Lookup(control, ValueOf(node));
        }

        if (kind.Contains("validat", StringComparison.Ordinal)
            || kind.Contains("verif", StringComparison.Ordinal)
            || kind.Contains("assert", StringComparison.Ordinal))
        {
            return new Action.Validate(control, ValueOf(node));
        }

        if (kind.Contains("input", StringComparison.Ordinal)
            || (control.Length > 0 && node.HasProp(ValueKeys)))
        {
            return new Action.SetValue(control, ValueOf(node));
        }

        if (kind.Contains("command", StringComparison.Ordinal) || node.HasProp("CommandName"))
        {
            return new Action.Command(node.Prop("CommandName", "Command") ?? control);
        }

        if (control.Length > 0
            && (kind.Contains("control", StringComparison.Ordinal)
                || kind.Contains("click", StringComparison.Ordinal)
                || kind.Contains("button", StringComparison.Ordinal)))
        {
            return new Action.Click(control);
        }

        return new Action.Unsupported(
            node.Kind,
            node.Describe(),
            new SortedDictionary<string, string>(node.Props, StringComparer.Ordinal));
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
