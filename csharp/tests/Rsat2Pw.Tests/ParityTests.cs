using Xunit;

namespace Rsat2Pw.Tests;

public class ParityTests
{
    private static (TestCase Case, Cases Data) Convert()
    {
        var testCase = Lower.Run(RecordingReader.Load(Fixtures.FixturePath("ConfirmPurchaseOrder.axtr")));

        var data = Params.FromWorkbook(Fixtures.FixturePath("ConfirmPurchaseOrder-params.xlsx"), null, testCase);
        data.Source = "fixtures/ConfirmPurchaseOrder-params.xlsx [Parameters]";

        return (testCase, data);
    }

    [Fact]
    public void SpecMatchesTheRustOutputByteForByte()
    {
        var (testCase, data) = Convert();
        var output = Codegen.Generate(testCase, data, OnUnsupported.Annotate);

        Assert.Equal(Fixtures.GoldenText("ConfirmPurchaseOrder.spec.ts"), Fixtures.Normalize(output.Spec));
    }

    [Fact]
    public void DataModuleMatchesTheRustOutputByteForByte()
    {
        var (testCase, data) = Convert();
        var output = Codegen.Generate(testCase, data, OnUnsupported.Annotate);

        Assert.Equal(Fixtures.GoldenText("ConfirmPurchaseOrder.data.ts"), Fixtures.Normalize(output.Data));
    }

    [Fact]
    public void ReportMatchesTheRustOutputByteForByte()
    {
        var (testCase, data) = Convert();
        var report = Reporter.Build(testCase, data);

        Assert.Equal(Fixtures.GoldenText("ConfirmPurchaseOrder.report.md"), Fixtures.Normalize(report.ToMarkdown()));
    }

    [Fact]
    public void GeneratedFileNameMatchesTheRustOutput()
    {
        var (testCase, data) = Convert();
        Assert.Equal("ConfirmPurchaseOrder", Codegen.Generate(testCase, data, OnUnsupported.Annotate).Stem);
    }

    [Fact]
    public void EmbeddedRuntimeIsTheRealHelper()
    {
        var runtime = RuntimeAsset.D365Ts;

        Assert.Contains("export class D365", runtime, StringComparison.Ordinal);
        Assert.Contains("data-dyn-controlname", runtime, StringComparison.Ordinal);
        Assert.Contains("CONTROL_FAMILY", runtime, StringComparison.Ordinal);
        Assert.Contains("BLOCKING_SELECTORS", runtime, StringComparison.Ordinal);
    }
}
