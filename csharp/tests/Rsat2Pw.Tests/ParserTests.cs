using Xunit;

namespace Rsat2Pw.Tests;

public class XmlTests
{
    [Fact]
    public void ParsesNestedElementsAndStripsPrefixes()
    {
        var doc = Xml.Parse(
            """
            <AxTaskRecording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>Create customer</Name>
              <Nodes>
                <Node i:type="InputUserAction"><ControlName>CustAccount</ControlName></Node>
              </Nodes>
            </AxTaskRecording>
            """);

        Assert.Equal("AxTaskRecording", doc.Name);
        Assert.Equal("Create customer", doc.TextOf("Name"));

        var node = doc.Child("Nodes")!.Children[0];
        Assert.Equal("InputUserAction", node.Attr("type"));
        Assert.Equal("CustAccount", node.TextOf("ControlName"));
    }

    [Fact]
    public void UnescapesEntities()
    {
        var doc = Xml.Parse("<r><V>Contoso &amp; Sons</V></r>");
        Assert.Equal("Contoso & Sons", doc.TextOf("V"));
    }

    [Fact]
    public void EmptyElementsDoNotSwallowTheirSiblings()
    {
        var doc = Xml.Parse("<r><A/><B>kept</B></r>");
        Assert.Equal(2, doc.Children.Count);
        Assert.Equal("kept", doc.TextOf("B"));
    }
}

public class RecordingTests
{
    private const string Sample = """
        <AxTaskRecording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
          <Name>Create customer</Name>
          <Variables>
            <AxTaskRecordingVariable><Name>CustomerName</Name><Value>Contoso</Value></AxTaskRecordingVariable>
          </Variables>
          <Nodes>
            <AxTaskRecordingNode i:type="TaskUserActionGroup">
              <Annotation>Open the customers list</Annotation>
              <Childs>
                <AxTaskRecordingNode i:type="MenuItemUserAction">
                  <MenuItemName>CustTableListPage</MenuItemName>
                  <MenuItemType>Display</MenuItemType>
                </AxTaskRecordingNode>
              </Childs>
            </AxTaskRecordingNode>
          </Nodes>
        </AxTaskRecording>
        """;

    [Fact]
    public void ReadsNameVariablesAndNestedNodes()
    {
        var rec = RecordingReader.Parse(Sample);

        Assert.Equal("Create customer", rec.Name);
        Assert.Equal([new KeyValuePair<string, string>("CustomerName", "Contoso")], rec.Variables);
        Assert.Single(rec.Nodes);

        var group = rec.Nodes[0];
        Assert.Equal("TaskUserActionGroup", group.Kind);
        Assert.Equal("Open the customers list", group.Prop("Annotation"));
        Assert.Single(group.Children);
        Assert.Equal("CustTableListPage", group.Children[0].Prop("MenuItemName"));
    }

    [Fact]
    public void ToleratesAlternateWrapperSpellings()
    {
        var alt = Sample.Replace("Childs", "Children", StringComparison.Ordinal)
                        .Replace("Nodes", "Steps", StringComparison.Ordinal);

        var rec = RecordingReader.Parse(alt);

        Assert.Single(rec.Nodes);
        Assert.Single(rec.Nodes[0].Children);
    }

    [Fact]
    public void ReadsBothArchiveAndRawXmlToTheSameRecording()
    {
        var fromArchive = RecordingReader.Load(Fixtures.FixturePath("CreateCustomer.axtr"));
        var fromXml = RecordingReader.Load(Fixtures.FixturePath("CreateCustomer.xml"));

        Assert.Equal(fromArchive.Name, fromXml.Name);
        Assert.Equal(fromArchive.Nodes.Count, fromXml.Nodes.Count);
        Assert.Equal(fromArchive.Variables, fromXml.Variables);
    }
}
