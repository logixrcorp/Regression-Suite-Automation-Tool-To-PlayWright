using System.Text.Json;
using Xunit;

namespace Rsat2Pw.Tests;

public class ReportTests
{
    private static Report ReportFor(string xml)
    {
        var testCase = Lower.Run(RecordingReader.Parse(xml));
        return Reporter.Build(testCase, Params.FromRecording(testCase));
    }

    private static Report FixtureReport() => ReportFor(Fixtures.FixtureText("ConfirmPurchaseOrder.xml"));

    [Fact]
    public void CountsTranslatedSkippedAndUnmappedActions()
    {
        var r = FixtureReport();

        Assert.Equal(r.Coverage.Actions, r.Outline.Count);
        Assert.Equal(
            r.Coverage.Actions,
            r.Coverage.Translated + r.Coverage.Skipped + r.Coverage.NotTranslated);

        Assert.True(r.TranslatedByOp.ContainsKey("click"));
        Assert.True(r.TranslatedByOp.ContainsKey("test.step"));
    }

    /// <summary>
    /// The whole point of the report: an unmapped kind must arrive with the
    /// properties needed to write its mapping rule.
    /// </summary>
    [Fact]
    public void UnmappedKindsCarryTheirFullPropertyBag()
    {
        var r = ReportFor(Fixtures.Wrap(
            "T",
            """
            <Node i:type="CommandUserAction"><CommandName>SelectForAdd</CommandName>
            <ControlName>Grid</ControlName><ControlType>Grid</ControlType></Node>
            """));

        var kind = Assert.Single(r.NotTranslated);

        Assert.Equal("CommandUserAction:SelectForAdd", kind.RawKind);
        Assert.Equal(1, kind.Count);
        Assert.Equal("Grid", kind.Props["ControlName"]);
        Assert.Equal("Grid", kind.Props["ControlType"]);
    }

    [Fact]
    public void RepeatedUnmappedKindsAreGroupedWithACount()
    {
        var r = ReportFor(Fixtures.Wrap(
            "T",
            """
            <Node i:type="Mystery"><A>1</A></Node>
            <Node i:type="Mystery"><B>2</B></Node>
            """));

        var kind = Assert.Single(r.NotTranslated);
        Assert.Equal(2, kind.Count);

        Assert.True(kind.Props.ContainsKey("A"));
        Assert.True(kind.Props.ContainsKey("B"));
        Assert.Equal(0, r.Coverage.Translated);
    }

    /// <summary>
    /// A skipped action is not a translated one. Counting it as translated
    /// would let the headline number be improved by skipping more.
    /// </summary>
    [Fact]
    public void SkippedActionsGetTheirOwnBucket()
    {
        var r = ReportFor(Fixtures.Wrap(
            "T",
            """
            <Node i:type="CommandUserAction"><CommandName>GetFilters</CommandName>
            <ControlName>SystemDefinedFilterManager</ControlName></Node>
            """));

        Assert.Equal(1, r.Coverage.Skipped);
        Assert.Equal(0, r.Coverage.Translated);
        Assert.Equal(0, r.Coverage.NotTranslated);
        Assert.Equal("CommandUserAction:GetFilters", r.SkippedKinds[0].RawKind);
        Assert.Contains("## Skipped", r.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsVariablesThatNoActionUses()
    {
        var r = ReportFor(
            """
            <Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>T</Name>
              <Variables>
                <AxTaskRecordingVariable><Name>Used</Name><Value>a</Value></AxTaskRecordingVariable>
                <AxTaskRecordingVariable><Name>Orphan</Name><Value>b</Value></AxTaskRecordingVariable>
              </Variables>
              <RootScope><Children>
                <Node i:type="PropertyUserAction"><ControlName>C</ControlName>
                  <VariableName>Used</VariableName><Value>a</Value></Node>
              </Children></RootScope></Recording>
            """);

        Assert.True(r.Variables.Single(v => v.Name == "Used").Referenced);
        Assert.False(r.Variables.Single(v => v.Name == "Orphan").Referenced);
    }

    [Fact]
    public void OutlineNestsChildrenAndMarksGaps()
    {
        var r = FixtureReport();

        Assert.Contains(r.Outline, e => e.Op == "test.step" && e.Depth == 0);
        Assert.Contains(r.Outline, e => e.Depth >= 2);
        Assert.Equal(r.Coverage.NotTranslated, r.Outline.Count(e => !e.Translated && !e.Skipped));
    }

    [Fact]
    public void MarkdownAndJsonBothRender()
    {
        var r = FixtureReport();

        var md = r.ToMarkdown();
        Assert.Contains("# Conversion report: Confirm purchase order", md, StringComparison.Ordinal);
        Assert.Contains("## Coverage", md, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(r.ToJson());
        Assert.Equal(
            r.Coverage.Actions,
            json.RootElement.GetProperty("coverage").GetProperty("actions").GetInt32());
    }

    [Fact]
    public void EmptyRecordingIsFullyCoveredNotZeroPercent()
    {
        var r = ReportFor("<Recording><Name>T</Name><RootScope><Children/></RootScope></Recording>");

        Assert.Equal(0, r.Coverage.Actions);
        Assert.Equal(100.0, r.Coverage.Percent());
    }
}
