using Xunit;

namespace Rsat2Pw.Tests;

public class ParamsTests
{
    private static List<List<string>> Table(params string[][] rows) =>
        rows.Select(r => r.ToList()).ToList();

    [Fact]
    public void WideLayoutYieldsOneCasePerRow()
    {
        var cases = Params.ParseWide(Table(
            ["Case", "Customer name", "Group"],
            ["domestic", "Contoso", "10"],
            ["export", "Fabrikam", "20"]));

        Assert.Equal(["Customer_name", "Group"], cases.Fields);
        Assert.Equal(2, cases.Rows.Count);
        Assert.Equal("export", cases.Rows[1].Label);
        Assert.Equal("Fabrikam", cases.Rows[1].Values["Customer_name"]);
    }

    [Fact]
    public void CollidingHeadersAreSuffixedNotDropped()
    {
        var cases = Params.ParseWide(Table(
            ["Customer name", "Customer-name", "__case"],
            ["Contoso", "Fabrikam", "collide"]));

        Assert.Equal(["Customer_name", "Customer_name_2", "__case_2"], cases.Fields);
        Assert.Equal("Contoso", cases.Rows[0].Values["Customer_name"]);
        Assert.Equal("Fabrikam", cases.Rows[0].Values["Customer_name_2"]);
        Assert.Equal("collide", cases.Rows[0].Values["__case_2"]);
    }

    [Fact]
    public void TallLayoutYieldsASingleCase()
    {
        var table = Table(
            ["Name", "Value"],
            ["Customer name", "Contoso"]);

        Assert.True(Params.IsTall(table));

        var cases = Params.ParseTall(table);
        Assert.Single(cases.Rows);
        Assert.Equal("Contoso", cases.Rows[0].Values["Customer_name"]);
    }

    [Fact]
    public void UnlabelledWideRowsFallBackToAPositionalName()
    {
        var cases = Params.ParseWide(Table(
            ["Customer name"],
            ["Contoso"],
            ["Fabrikam"]));

        Assert.Equal("row 1", cases.Rows[0].Label);
        Assert.Equal("row 2", cases.Rows[1].Label);
    }

    /// <summary>
    /// RSAT's own parameter workbooks have a layout this reader does not
    /// understand - and reading one as a plain sheet does not fail, it quietly
    /// builds cases out of the title block. Refusing is the honest answer.
    /// </summary>
    [Fact]
    public void AnRsatParameterWorkbookIsRefusedRatherThanMisread()
    {
        var testCase = Lower.Run(RecordingReader.Load(Fixtures.FixturePath("ConfirmPurchaseOrder.xml")));
        var book = Fixtures.FixturePath("RsatV2-params.xlsx");

        var ex = Assert.Throws<InvalidDataException>(() => Params.FromWorkbook(book, null, testCase));
        Assert.Contains("RSAT parameter workbook", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TestCaseSteps", ex.Message, StringComparison.Ordinal);

        // --sheet is the escape hatch, and it still works.
        Assert.NotNull(Params.FromWorkbook(book, "General", testCase));
    }

    /// <summary>
    /// A workbook can have headers and no data rows - a template someone has
    /// not filled in yet. Emitting a case of blanks from it produces a spec
    /// that runs, types nothing into every field, and looks healthy doing it.
    /// Found against a real third-party workbook, which had exactly that shape.
    /// </summary>
    [Fact]
    public void AWorkbookWithNoDataRowsFallsBackToRecordedValues()
    {
        var testCase = Lower.Run(RecordingReader.Load(Fixtures.FixturePath("ConfirmPurchaseOrder.xml")));

        var cases = Params.FromWorkbook(
            Fixtures.FixturePath("EmptyTemplate-params.xlsx"),
            null,
            testCase);

        Assert.Single(cases.Rows);

        // The recorder captured this value; the empty template must not erase it.
        var recorded = testCase.Variables.Single(v => v.Name == "PurchTable_DeliveryDate");
        Assert.Equal("9/30/2026", recorded.Default);
        Assert.Equal(recorded.Default, cases.Rows[0].Values["PurchTable_DeliveryDate"]);

        // And the substitution is visible rather than silent.
        Assert.Contains("no data rows", cases.Source, StringComparison.Ordinal);
    }
}

public class XlsxTests
{
    private static string Workbook => Fixtures.FixturePath("ConfirmPurchaseOrder-params.xlsx");

    [Fact]
    public void ReadsSheetNames() =>
        Assert.Equal(["Parameters"], Xlsx.SheetNames(Workbook));

    [Fact]
    public void ReadsTheParameterGridIncludingHeaders()
    {
        var table = Xlsx.ReadSheet(Workbook, null);

        Assert.Equal(3, table.Count);
        Assert.Equal(
            ["Case", "PurchId", "PurchTable_DeliveryDate", "PurchLine_PurchQty", "PurchParmTable_Printout"],
            table[0]);
        Assert.Equal(["confirm with printout", "000123", "9/30/2026", "12", "true"], table[1]);
        Assert.Equal(["confirm quietly", "000124", "10/14/2026", "3", "false"], table[2]);
    }

    [Fact]
    public void NamingAMissingSheetFailsLoudly()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Xlsx.ReadSheet(Workbook, "NoSuchSheet"));
        Assert.Contains("NoSuchSheet", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<c r=\"A1\" t=\"inlineStr\"><is><t>hello</t></is></c>", "hello")]
    [InlineData("<c r=\"A1\"><v>42</v></c>", "42")]
    [InlineData("<c r=\"A1\"><v>42.5</v></c>", "42.5")]
    [InlineData("<c r=\"A1\"><v>10.0</v></c>", "10")]
    [InlineData("<c r=\"A1\" t=\"str\"><v>formula</v></c>", "formula")]
    [InlineData("<c r=\"A1\" t=\"b\"><v>1</v></c>", "true")]
    public void ReadsEveryCellEncoding(string cellXml, string expected)
    {
        var sheet = $"""
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData><row r="1">{cellXml}</row></sheetData>
            </worksheet>
            """;

        var table = Xlsx.ReadCells(sheet, []);
        Assert.Equal(expected, table[0][0]);
    }

    [Fact]
    public void HonoursCellReferencesSoGapsArePreserved()
    {
        const string sheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData><row r="1">
                <c r="A1" t="inlineStr"><is><t>a</t></is></c>
                <c r="C1" t="inlineStr"><is><t>c</t></is></c>
              </row></sheetData>
            </worksheet>
            """;

        var table = Xlsx.ReadCells(sheet, []);
        Assert.Equal(["a", "", "c"], table[0]);
    }
}
