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
}

public class XlsxTests
{
    private static string Workbook => Fixtures.FixturePath("CreateCustomer-params.xlsx");

    [Fact]
    public void ReadsSheetNames() =>
        Assert.Equal(["Parameters"], Xlsx.SheetNames(Workbook));

    [Fact]
    public void ReadsTheParameterGridIncludingHeaders()
    {
        var table = Xlsx.ReadSheet(Workbook, null);

        Assert.Equal(3, table.Count);
        Assert.Equal(["Case", "Customer account", "Customer name", "Customer group", "Street"], table[0]);
        Assert.Equal(["domestic retail", "US-9001", "Contoso Retail", "10", "123 Sample Way"], table[1]);
        Assert.Equal(["export wholesale", "US-9002", "Fabrikam Export", "20", "9 Harbour Rd"], table[2]);
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
