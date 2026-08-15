using Xunit;

namespace Rsat2Pw.Tests;

public class LowerTests
{
    [Fact]
    public void MapsMenuItemNavigation()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="MenuItemUserAction"><MenuItemName>CustTableListPage</MenuItemName>
            <MenuItemType>Display</MenuItemType></Node>
            """);

        Assert.Equal(new Action.Navigate("CustTableListPage", MenuItemKind.Display), actions[0]);
    }

    [Fact]
    public void VariableBoundInputBecomesAParameterNotALiteral()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="InputUserAction"><ControlName>CustAccount</ControlName>
            <Value>US-001</Value><VariableName>Customer account</VariableName></Node>
            """);

        Assert.Equal(
            new Action.SetValue("CustAccount", new Value.Variable("Customer_account")),
            actions[0]);
    }

    [Fact]
    public void UnknownNodeKindsDegradeToATodo()
    {
        var actions = Fixtures.LowerXml(
            """<Node i:type="SomeFutureUserAction"><Mystery>42</Mystery></Node>""");

        var unsupported = Assert.IsType<Action.Unsupported>(actions[0]);
        Assert.Equal("SomeFutureUserAction", unsupported.RawKind);
        Assert.Contains("Mystery=42", unsupported.Detail, StringComparison.Ordinal);

        Assert.Equal("42", unsupported.Props["Mystery"]);
    }

    [Fact]
    public void GridActionsKeepColumnAndRow()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="InputUserAction"><GridName>Lines</GridName>
            <ColumnName>ItemId</ColumnName><RowIndex>2</RowIndex><Value>D0001</Value></Node>
            """);

        Assert.Equal(
            new Action.SetGridValue("Lines", "ItemId", 2, new Value.Literal("D0001")),
            actions[0]);
    }

    [Fact]
    public void CollidingVariableNamesGetDistinctIdentifiers()
    {
        const string doc = """
            <AxTaskRecording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>T</Name>
              <Variables>
                <AxTaskRecordingVariable><Name>Customer name</Name><Value>a</Value></AxTaskRecordingVariable>
                <AxTaskRecordingVariable><Name>Customer-name</Name><Value>b</Value></AxTaskRecordingVariable>
                <AxTaskRecordingVariable><Name>__case</Name><Value>c</Value></AxTaskRecordingVariable>
              </Variables>
              <Nodes/></AxTaskRecording>
            """;

        var names = Lower.Run(RecordingReader.Parse(doc)).Variables.Select(v => v.Name).ToList();

        Assert.Equal(["Customer_name", "Customer_name_2", "__case_2"], names);
    }

    [Theory]
    [InlineData("Customer account", "Customer_account")]
    [InlineData("9lives", "_9lives")]
    [InlineData("", "_")]
    [InlineData("a-b.c", "a_b_c")]
    public void SanitizeIdentProducesValidIdentifiers(string input, string expected) =>
        Assert.Equal(expected, Lower.SanitizeIdent(input));
}

public class CasingTests
{
    [Theory]
    [InlineData("Create customer", "CreateCustomer")]
    [InlineData("Create `customer` ${evil}", "CreateCustomerEvil")]
    [InlineData("CustTableListPage", "CustTableListPage")]
    [InlineData("D365_to_Innova_Add_Line_to_PO_9304_Base", "D365ToInnovaAddLineToPo9304Base")]
    [InlineData("XMLHttpRequest", "XmlHttpRequest")]
    [InlineData("Add Line to PO", "AddLineToPo")]
    [InlineData("foo2bar", "Foo2bar")]
    [InlineData("PO_9304", "Po9304")]
    public void ToPascalCaseMatchesTheRustBuild(string input, string expected) =>
        Assert.Equal(expected, Casing.ToPascalCase(input));
}
