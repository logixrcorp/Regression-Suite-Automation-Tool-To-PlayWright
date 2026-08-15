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

    private static Report FixtureReport() => ReportFor(Fixtures.FixtureText("CreateCustomer.xml"));

    [Fact]
    public void CountsTranslatedAndUnmappedActions()
    {
        var r = FixtureReport();

        Assert.Equal(18, r.Coverage.Actions);
        Assert.Equal(1, r.Coverage.NotTranslated);
        Assert.Equal(17, r.Coverage.Translated);
        Assert.Equal(94.4, r.Coverage.Percent(), 1);

        Assert.Equal(3, r.TranslatedByOp["setField"]);
        Assert.Equal(2, r.TranslatedByOp["setGridCell"]);
        Assert.Equal(4, r.TranslatedByOp["test.step"]);
    }

    [Fact]
    public void UnmappedKindsCarryTheirFullPropertyBag()
    {
        var kind = Assert.Single(FixtureReport().NotTranslated);

        Assert.Equal("ExportToExcelUserAction", kind.RawKind);
        Assert.Equal(1, kind.Count);
        Assert.Equal("ExportToExcelButton", kind.Props["ControlName"]);
        Assert.Equal("CustomerV3", kind.Props["OfficeTemplate"]);
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

    [Fact]
    public void FlagsVariablesThatNoActionUses()
    {
        var r = ReportFor(
            """
            <AxTaskRecording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>T</Name>
              <Variables>
                <AxTaskRecordingVariable><Name>Used</Name><Value>a</Value></AxTaskRecordingVariable>
                <AxTaskRecordingVariable><Name>Orphan</Name><Value>b</Value></AxTaskRecordingVariable>
              </Variables>
              <Nodes>
                <Node i:type="InputUserAction"><ControlName>C</ControlName>
                  <VariableName>Used</VariableName><Value>a</Value></Node>
              </Nodes></AxTaskRecording>
            """);

        Assert.True(r.Variables.Single(v => v.Name == "Used").Referenced);
        Assert.False(r.Variables.Single(v => v.Name == "Orphan").Referenced);
    }

    [Fact]
    public void OutlineNestsChildrenAndMarksGaps()
    {
        var r = FixtureReport();

        Assert.Contains(r.Outline, e => e.Op == "test.step" && e.Depth == 0);
        Assert.Contains(r.Outline, e => e.Op == "setField" && e.Depth == 2);
        Assert.Single(r.Outline, e => !e.Translated);
    }

    [Fact]
    public void MarkdownAndJsonBothRender()
    {
        var r = FixtureReport();

        var md = r.ToMarkdown();
        Assert.Contains("# Conversion report: Create customer", md, StringComparison.Ordinal);
        Assert.Contains("ExportToExcelUserAction", md, StringComparison.Ordinal);
        Assert.Contains("| Not translated | 1 |", md, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(r.ToJson());
        Assert.Equal(1, json.RootElement.GetProperty("coverage").GetProperty("not_translated").GetInt32());
    }

    [Fact]
    public void EmptyRecordingIsFullyCoveredNotZeroPercent()
    {
        var r = ReportFor("<AxTaskRecording><Name>T</Name><Nodes/></AxTaskRecording>");

        Assert.Equal(0, r.Coverage.Actions);
        Assert.Equal(100.0, r.Coverage.Percent());
    }
}
