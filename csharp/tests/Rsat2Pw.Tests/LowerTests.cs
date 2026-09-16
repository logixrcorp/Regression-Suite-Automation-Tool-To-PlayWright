using Xunit;

namespace Rsat2Pw.Tests;

public class LowerTests
{
    [Fact]
    public void MapsMenuItemNavigation()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="MenuItemUserAction"><MenuItemName>purchtablelistpage</MenuItemName>
            <MenuItemType>Display</MenuItemType></Node>
            """);

        Assert.Equal(new Action.Navigate("purchtablelistpage", MenuItemKind.Display), actions[0]);
    }

    /// <summary>
    /// The recorder puts the verb in <c>CommandName</c> and the target in
    /// <c>ControlName</c>. Reading it the other way round emits a suite that
    /// hunts for a button labelled "Click".
    /// </summary>
    [Fact]
    public void AClickCommandTargetsTheControlNotTheVerb()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="CommandUserAction"><CommandName>Click</CommandName>
            <ControlName>PurchCopyJournalHeader</ControlName>
            <ControlType>MenuItemButton</ControlType></Node>
            """);

        Assert.Equal(new Action.Click("PurchCopyJournalHeader", "MenuItemButton"), actions[0]);
    }

    [Fact]
    public void FieldEntryBecomesAParameterDefaultingToTheRecordedValue()
    {
        var testCase = Fixtures.LowerCase(
            """
            <Node i:type="PropertyUserAction"><PropertyName>Value</PropertyName>
            <ControlName>PurchParmTable_Num</ControlName><ControlType>Input</ControlType>
            <UserActionType>Input</UserActionType><Value>123</Value></Node>
            """);

        Assert.Equal(
            new Action.SetValue("PurchParmTable_Num", "Input", new Value.Variable("PurchParmTable_Num")),
            testCase.Actions[0]);

        Assert.Equal([new Variable("PurchParmTable_Num", "123")], testCase.Variables);
    }

    /// <summary>
    /// The filter a user typed lives in a JSON command argument, not in a
    /// property. Left in the property bag it reads as a field edit.
    /// </summary>
    [Fact]
    public void AFilterCommandUnpacksItsJsonArgument()
    {
        var testCase = Fixtures.LowerCase(
            """
            <Node i:type="CommandUserAction">
              <Arguments><CommandArgument><Value>[{"Capability":{"FieldLabel":"Purchase order","FieldName":""},"FieldName":"PurchId","Operator":"Is","Values":["003643"]}]</Value></CommandArgument></Arguments>
              <CommandName>ApplyFiltersForTaskRecorder</CommandName>
              <ControlName>SystemDefinedFilterManager</ControlName>
              <ControlType>FilterManager</ControlType></Node>
            """);

        Assert.Equal(
            new Action.Filter(
                "SystemDefinedFilterManager",
                "PurchId",
                "Purchase order",
                "Is",
                new Value.Variable("PurchId")),
            testCase.Actions[0]);

        Assert.Equal("003643", testCase.Variables[0].Default);
    }

    [Fact]
    public void GridCommandsCarryTheListAndTheRow()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="CommandUserAction">
              <Arguments><CommandArgument><Value>3</Value></CommandArgument></Arguments>
              <CommandName>ChangeSelectedIndexInCache</CommandName>
              <ListContext>Grid</ListContext><ControlName>Grid</ControlName>
              <ControlType>Grid</ControlType></Node>
            """);

        Assert.Equal(new Action.SelectRow("Grid", 3), actions[0]);
    }

    /// <summary>
    /// A step group is the user's own annotation and becomes a
    /// <c>test.step</c>; the private scopes the client wraps around its
    /// internals are lifted away.
    /// </summary>
    [Fact]
    public void StepGroupsAreKeptAndPrivatePlumbingScopesAreFlattened()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="Scope">
              <Description>Create the order</Description>
              <IsForm>false</IsForm><IsStepGroup>true</IsStepGroup>
              <Children>
                <Node i:type="Scope">
                  <IsForm>false</IsForm><IsStepGroup>false</IsStepGroup>
                  <Name>SystemDefinedFilterManager_GetFilters</Name><ScopeType>Private</ScopeType>
                  <Children>
                    <Node i:type="CommandUserAction"><CommandName>Click</CommandName>
                      <ControlName>OkButton</ControlName><ControlType>CommandButton</ControlType></Node>
                  </Children>
                </Node>
              </Children>
            </Node>
            """);

        var step = Assert.IsType<Action.Step>(actions[0]);
        Assert.Equal("Create the order", step.Label);
        Assert.Single(step.Children);
        Assert.IsType<Action.Click>(step.Children[0]);
    }

    /// <summary>
    /// The client groups its own work the same way the user does. Every lookup
    /// it opens becomes a private <c>&lt;control&gt;_RequestPopup</c> step
    /// group, and in real recordings those outnumber the user's own entirely.
    /// </summary>
    [Fact]
    public void PrivateStepGroupsAreTheClientsOwnAndGetFlattened()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="Scope">
              <IsForm>false</IsForm><IsStepGroup>true</IsStepGroup>
              <Name>CompanyLookup_RequestPopup</Name><ScopeType>Private</ScopeType>
              <Children>
                <Node i:type="CommandUserAction"><CommandName>RequestPopup</CommandName>
                  <ControlName>CompanyLookup</ControlName><ControlType>Input</ControlType></Node>
              </Children>
            </Node>
            """);

        Assert.Equal([new Action.OpenLookup("CompanyLookup")], actions);
    }

    /// <summary>
    /// Task Recorder records inside the client it is recording, so its own
    /// pane appears as a form scope around ordinary actions.
    /// </summary>
    [Fact]
    public void TheRecordersOwnPaneIsNotTreatedAsAForm()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="Scope">
              <IsForm>true</IsForm><IsStepGroup>false</IsStepGroup>
              <Name>SysBPMPane</Name><ScopeType>Public</ScopeType>
              <Children>
                <Node i:type="CommandUserAction"><CommandName>TabShown</CommandName>
                  <ControlName>PurchOrder</ControlName><ControlType>AppBarTab</ControlType></Node>
              </Children>
            </Node>
            """);

        Assert.Equal([new Action.Tab("PurchOrder")], actions);
    }

    [Fact]
    public void UnknownCommandsAreReportedByTheirVerb()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="CommandUserAction"><CommandName>SelectForAdd</CommandName>
            <ControlName>Grid</ControlName><ControlType>Grid</ControlType></Node>
            """);

        var unsupported = Assert.IsType<Action.Unsupported>(actions[0]);
        Assert.Equal("CommandUserAction:SelectForAdd", unsupported.RawKind);

        // The full bag is what the conversion report shows, so a rule for this
        // verb can be written without reopening the XML.
        Assert.Equal("Grid", unsupported.Props["ControlType"]);
    }

    /// <summary>
    /// A tree selection is recorded as a path in a command argument, the way
    /// the tree renders it, and the value the user picked is test data like
    /// any other recorded input.
    /// </summary>
    [Fact]
    public void ATreeSelectionCarriesItsPath()
    {
        var testCase = Fixtures.LowerCase(
            """
            <Node i:type="CommandUserAction">
              <Arguments>
                <CommandArgument><Value>ALL (ALL)\Adventure Works (Adventure Works)</Value></CommandArgument>
                <CommandArgument><Value>1</Value></CommandArgument>
              </Arguments>
              <CommandName>SelectionPathChanged</CommandName>
              <ControlName>ctrlFormTree</ControlName><ControlType>Tree</ControlType></Node>
            """);

        Assert.Equal(
            new Action.SelectTreeItem("ctrlFormTree", new Value.Variable("ctrlFormTree")),
            testCase.Actions[0]);

        Assert.Equal(@"ALL (ALL)\Adventure Works (Adventure Works)", testCase.Variables[0].Default);
    }

    /// <summary>
    /// The shortcut name is the whole instruction. Without it there is nothing
    /// to replay, so it degrades rather than emitting a nameless call.
    /// </summary>
    [Fact]
    public void ANamedShortcutIsMappedAndANamelessOneIsNot()
    {
        var mapped = Fixtures.LowerXml(
            """
            <Node i:type="CommandUserAction">
              <Arguments><CommandArgument><Value>ViewEdit</Value></CommandArgument></Arguments>
              <CommandName>ExecuteShortcuts</CommandName><ControlName></ControlName></Node>
            """);

        Assert.Equal(new Action.Shortcut("ViewEdit"), mapped[0]);

        var bare = Fixtures.LowerXml(
            """<Node i:type="CommandUserAction"><CommandName>ExecuteShortcuts</CommandName></Node>""");

        Assert.IsType<Action.Unsupported>(bare[0]);
    }

    /// <summary>
    /// A note is the one thing in a recording that was written in prose, on
    /// purpose. Dropping it loses the only instruction a human left behind.
    /// </summary>
    [Fact]
    public void ARecordedNoteSurvivesAsAComment()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="InfoUserAction"><Description>Note.</Description>
              <Notes>Check the open period.</Notes><Text>Check it</Text></Node>
            <Node i:type="AnnotationUserAction"><Description>Annotated step.</Description></Node>
            """);

        Assert.Equal(
            [new Action.Marker("Check the open period."), new Action.Marker("Annotated step.")],
            actions);
    }

    /// <summary>
    /// From a real customer recording: four of these arrive in a row directly
    /// after <c>navigate</c>, before the first click in the whole file. That is
    /// a page rendering its FactBoxes, not a user opening four parts by hand.
    /// </summary>
    [Fact]
    public void AFormPartRenderingIsSkippedAndItsFilterScopeFlattened()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="CommandUserAction"><CommandName>OpenFormPart</CommandName>
              <ControlName>EcoResProductVariantsPerCompanyPart</ControlName>
              <ControlType>Part</ControlType></Node>
            <Node i:type="Scope">
              <IsForm>true</IsForm><IsStepGroup>false</IsStepGroup>
              <Name>__partlinkfilter_RetailItemChannelFactBox</Name><ScopeType>Public</ScopeType>
              <Children>
                <Node i:type="CommandUserAction"><CommandName>Click</CommandName>
                  <ControlName>InventItemOrderSetupAction</ControlName>
                  <ControlType>MenuItemButton</ControlType></Node>
              </Children>
            </Node>
            """);

        var skipped = Assert.IsType<Action.Skipped>(actions[0]);
        Assert.Equal("CommandUserAction:OpenFormPart", skipped.RawKind);

        // The filter scope is the client's own: its child is lifted out rather
        // than nested inside a form nobody navigated to.
        Assert.Equal(new Action.Click("InventItemOrderSetupAction", "MenuItemButton"), actions[1]);
    }

    /// <summary>
    /// Verbs the recorder emits that our own corpus of recordings happens not
    /// to contain. They were found in another converter's dispatch table -
    /// written against a different set of recordings - which is the only way
    /// to extend this list short of a published enumeration, and there is not
    /// one.
    /// </summary>
    [Fact]
    public void VerbsLearnedFromAnotherCorpus()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="CommandUserAction">
              <Arguments><CommandArgument><Value>2</Value></CommandArgument></Arguments>
              <CommandName>ChangeSelectedIndex</CommandName>
              <ListContext>Grid</ListContext><ControlName>Grid</ControlName></Node>
            <Node i:type="CommandUserAction"><CommandName>ExecuteHyperlink</CommandName>
              <ControlName>PurchTable_PurchId</ControlName><ControlType>Input</ControlType></Node>
            <Node i:type="CommandUserAction">
              <Arguments><CommandArgument><Value>ALL (ALL)</Value></CommandArgument></Arguments>
              <CommandName>ExpandingPath</CommandName><ControlName>ctrlFormTree</ControlName></Node>
            <Node i:type="CommandUserAction"><CommandName>ResetFilters</CommandName>
              <ControlName>SystemDefinedFilterManager</ControlName></Node>
            <Node i:type="CommandUserAction"><CommandName>AddAFilterField</CommandName>
              <ControlName>SystemDefinedFilterManager</ControlName></Node>
            """);

        // The two aliases land on the rules their longer-named twins use.
        Assert.Equal(new Action.SelectRow("Grid", 2), actions[0]);
        Assert.Equal(new Action.Click("PurchTable_PurchId", "Input"), actions[1]);

        var expand = Assert.IsType<Action.ExpandTreeItem>(actions[2]);
        Assert.Equal("ctrlFormTree", expand.Control);

        Assert.Equal(new Action.ResetFilters("SystemDefinedFilterManager"), actions[3]);

        // Preparing the filter pane is understood and deliberately not replayed.
        Assert.IsType<Action.Skipped>(actions[4]);
    }

    /// <summary>
    /// Microsoft's CDM schema for the task recorder tables
    /// (<c>SysTaskRecorderNode*</c>) lists node types that none of the real
    /// recordings available to test against contain: a recorded note, a
    /// validation, a form open, and a bare annotation. They have to degrade
    /// safely - mapped, or named in the report - rather than vanish.
    /// </summary>
    [Fact]
    public void NodeTypesNoSampleRecordingContainsStillDegradeHonestly()
    {
        var actions = Fixtures.LowerXml(
            """
            <Node i:type="InfoUserAction"><Description>Note.</Description>
              <Notes>Check the posting profile.</Notes><Text>Check it</Text></Node>
            <Node i:type="ValidationUserAction"><Name>ValidateCustAccount</Name>
              <ControlName>SalesTable_CustAccount</ControlName><ControlType>Input</ControlType></Node>
            <Node i:type="FormUserAction"><ControlLabel>All sales orders</ControlLabel>
              <FormId>123_SalesTableListPage_abc</FormId></Node>
            <Node i:type="AnnotationUserAction"><Description>Annotated step.</Description></Node>
            """);

        Assert.Equal(4, actions.Count);

        // A note becomes a comment, and a validation maps: its expected value
        // is test data, which is where RSAT keeps it too.
        Assert.IsType<Action.Marker>(actions[0]);
        var validate = Assert.IsType<Action.Validate>(actions[1]);
        Assert.Equal("SalesTable_CustAccount", validate.Control);
        Assert.IsType<Action.Marker>(actions[3]);

        // A form open is the one left: the schema gives it no open/close
        // discriminator, and no recording to hand contains one, so there is
        // nothing to derive a rule from. It degrades, carrying its properties.
        var unsupported = Assert.IsType<Action.Unsupported>(actions[2]);
        Assert.Equal("FormUserAction", unsupported.RawKind);
        Assert.NotEmpty(unsupported.Props);
    }

    [Fact]
    public void CollidingVariableNamesGetDistinctIdentifiers()
    {
        const string doc = """
            <Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>T</Name>
              <Variables>
                <AxTaskRecordingVariable><Name>Customer name</Name><Value>a</Value></AxTaskRecordingVariable>
                <AxTaskRecordingVariable><Name>Customer-name</Name><Value>b</Value></AxTaskRecordingVariable>
                <AxTaskRecordingVariable><Name>__case</Name><Value>c</Value></AxTaskRecordingVariable>
              </Variables>
              <RootScope><Children/></RootScope></Recording>
            """;

        var names = Lower.Run(RecordingReader.Parse(doc)).Variables.Select(v => v.Name).ToList();

        Assert.Equal(["Customer_name", "Customer_name_2", "__case_2"], names);
    }

    [Fact]
    public void JsonScannerPrefersTheFirstNonEmptyMatch()
    {
        const string json = """[{"Capability":{"FieldName":""},"FieldName":"PurchId","Values":["003643"]}]""";

        Assert.Equal("PurchId", Lower.JsonString(json, "FieldName"));
        Assert.Equal("003643", Lower.JsonFirstArrayString(json, "Values"));
        Assert.Null(Lower.JsonString(json, "Missing"));
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
