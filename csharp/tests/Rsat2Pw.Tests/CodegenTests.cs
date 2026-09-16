using Xunit;

namespace Rsat2Pw.Tests;

public class CodegenTests
{
    private static Output GenerateFrom(string xml, OnUnsupported onUnsupported)
    {
        var testCase = Lower.Run(RecordingReader.Parse(xml));
        var cases = Params.FromRecording(testCase);

        if (cases.Rows.Count == 0)
        {
            cases.Rows.Add(new Case { Label = "default" });
        }

        return Codegen.Generate(testCase, cases, onUnsupported);
    }

    private const string Click = """
        <Node i:type="CommandUserAction"><CommandName>Click</CommandName>
        <ControlName>SystemDefinedSaveButton</ControlName><ControlType>CommandButton</ControlType></Node>
        """;

    private const string Unknown = """
        <Node i:type="CommandUserAction"><CommandName>Mystery</CommandName>
        <ControlName>Grid</ControlName><Note>x</Note></Node>
        """;

    [Fact]
    public void AClickCarriesItsControlTypeThroughToTheRuntime()
    {
        var spec = GenerateFrom(Fixtures.Wrap("T", Click), OnUnsupported.Annotate).Spec;

        Assert.Contains(
            "await d365.click('SystemDefinedSaveButton', 'CommandButton');",
            spec,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A skipped action still says so in the generated file. Silently dropping
    /// it would be indistinguishable from never having seen it.
    /// </summary>
    [Fact]
    public void ASkippedActionLeavesACommentBehind()
    {
        var spec = GenerateFrom(
            Fixtures.Wrap(
                "T",
                """
                <Node i:type="CommandUserAction"><CommandName>GetFilters</CommandName>
                <ControlName>SystemDefinedFilterManager</ControlName></Node>
                """),
            OnUnsupported.Annotate).Spec;

        Assert.Contains(
            "// rsat2pw: skipped 'CommandUserAction:GetFilters'",
            spec,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingNameCannotBreakOutOfTheTestTitle()
    {
        var spec = GenerateFrom(
            Fixtures.Wrap("Order `x` ${evil}", Click),
            OnUnsupported.Annotate).Spec;

        Assert.Contains(@"test(`Order \`x\` \${evil} [${params.__case}]`", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void MultilineDetailStaysOnOneCommentLine()
    {
        var spec = GenerateFrom(
            Fixtures.Wrap(
                "T",
                "<Node i:type=\"CommandUserAction\"><CommandName>Mystery</CommandName><Note>one\ntwo</Note></Node>"),
            OnUnsupported.Annotate).Spec;

        var comment = spec.Split('\n').First(l => l.TrimStart().StartsWith("// TODO(rsat2pw):", StringComparison.Ordinal));
        Assert.Contains("Note=one two", comment, StringComparison.Ordinal);

        Assert.Contains(@"Note=one\ntwo", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void FailModeThrowsInsteadOfAnnotating()
    {
        var spec = GenerateFrom(Fixtures.Wrap("T", Unknown), OnUnsupported.Fail).Spec;

        Assert.Contains("throw new Error('rsat2pw:", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("annotations.push", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentModeLeavesOnlyAComment()
    {
        var spec = GenerateFrom(Fixtures.Wrap("T", Unknown), OnUnsupported.Comment).Spec;

        Assert.Contains("// TODO(rsat2pw)", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("annotations.push", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new Error", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsLineEndingsNormalizeBeforeEscaping()
    {
        var output = GenerateFrom(
            Fixtures.Wrap(
                "T",
                "<Node i:type=\"PropertyUserAction\"><ControlName>C</ControlName><Value>a\r\nb</Value></Node>"),
            OnUnsupported.Annotate);

        // The recorded value is test data now, so the literal lands in the
        // data module rather than in the spec.
        Assert.DoesNotContain('\r', output.Spec);
        Assert.DoesNotContain('\r', output.Data);
        Assert.Contains(@"'a\nb'", output.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapedCarriageReturnsSurviveAsEscapes()
    {
        Assert.Equal(@"a\r\nb", Ir.EscapeTs("a\r\nb"));
        Assert.Equal(@"it\'s", Ir.EscapeTs("it's"));
        Assert.Equal(@"back\\slash", Ir.EscapeTs(@"back\slash"));
    }
}
