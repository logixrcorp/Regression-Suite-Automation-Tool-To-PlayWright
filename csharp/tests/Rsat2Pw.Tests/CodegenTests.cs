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

    [Fact]
    public void RecordingNameCannotBreakOutOfTheTestTitle()
    {
        var spec = GenerateFrom(
            Fixtures.Wrap(
                "Order `x` ${evil}",
                """<Node i:type="CommandUserAction"><CommandName>Save</CommandName></Node>"""),
            OnUnsupported.Annotate).Spec;

        Assert.Contains(@"test(`Order \`x\` \${evil} [${params.__case}]`", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void MultilineDetailStaysOnOneCommentLine()
    {
        var spec = GenerateFrom(
            Fixtures.Wrap("T", "<Node i:type=\"FutureAction\"><Note>one\ntwo</Note></Node>"),
            OnUnsupported.Annotate).Spec;

        var comment = spec.Split('\n').First(l => l.TrimStart().StartsWith("// TODO(rsat2pw):", StringComparison.Ordinal));
        Assert.Contains("Note=one two", comment, StringComparison.Ordinal);

        Assert.Contains(@"Note=one\ntwo", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void FailModeThrowsInsteadOfAnnotating()
    {
        var spec = GenerateFrom(
            Fixtures.Wrap("T", """<Node i:type="FutureAction"><Note>x</Note></Node>"""),
            OnUnsupported.Fail).Spec;

        Assert.Contains("throw new Error('rsat2pw:", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("annotations.push", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentModeLeavesOnlyAComment()
    {
        var spec = GenerateFrom(
            Fixtures.Wrap("T", """<Node i:type="FutureAction"><Note>x</Note></Node>"""),
            OnUnsupported.Comment).Spec;

        Assert.Contains("// TODO(rsat2pw)", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("annotations.push", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new Error", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsLineEndingsNormalizeBeforeEscaping()
    {
        var spec = GenerateFrom(
            Fixtures.Wrap("T", "<Node i:type=\"InputUserAction\"><ControlName>C</ControlName><Value>a\r\nb</Value></Node>"),
            OnUnsupported.Annotate).Spec;

        Assert.DoesNotContain('\r', spec);
        Assert.Contains(@"'a\nb'", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapedCarriageReturnsSurviveAsEscapes()
    {
        Assert.Equal(@"a\r\nb", Ir.EscapeTs("a\r\nb"));
        Assert.Equal(@"it\'s", Ir.EscapeTs("it's"));
        Assert.Equal(@"back\\slash", Ir.EscapeTs(@"back\slash"));
    }
}
