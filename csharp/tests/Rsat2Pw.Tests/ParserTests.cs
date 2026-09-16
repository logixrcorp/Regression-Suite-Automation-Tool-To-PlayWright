using Xunit;

namespace Rsat2Pw.Tests;

public class XmlTests
{
    [Fact]
    public void ParsesNestedElementsAndStripsPrefixes()
    {
        var doc = Xml.Parse(
            """
            <Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>Create customer</Name>
              <Nodes>
                <Node i:type="PropertyUserAction"><ControlName>CustAccount</ControlName></Node>
              </Nodes>
            </Recording>
            """);

        Assert.Equal("Recording", doc.Name);
        Assert.Equal("Create customer", doc.TextOf("Name"));

        var node = doc.Child("Nodes")!.Children[0];
        Assert.Equal("PropertyUserAction", node.Attr("type"));
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
    /// <summary>The shape a real export actually has.</summary>
    private const string RealShape = """
        <?xml version="1.0" encoding="utf-8"?>
        <Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance"
                   xmlns="http://schemas.datacontract.org/2004/07/Microsoft.Dynamics.Client.ServerForm.TaskRecording">
          <Name>Confirm purchase order</Name>
          <RootScope z:Id="i1" xmlns:z="http://schemas.microsoft.com/2003/10/Serialization/">
            <Parent i:nil="true" />
            <Children>
              <Node z:Id="i2" i:type="Scope">
                <Children>
                  <Node z:Id="i3" i:type="CommandUserAction">
                    <Description>Click From journal.</Description>
                    <Annotations>
                      <Annotation i:type="FormAnnotation">
                        <MenuItemName>SysBPMPane</MenuItemName>
                      </Annotation>
                    </Annotations>
                    <Arguments>
                      <CommandArgument><IsReference>false</IsReference><Value>[{"FieldName":"PurchId"}]</Value></CommandArgument>
                      <CommandArgument><IsReference>false</IsReference><Value>1</Value></CommandArgument>
                    </Arguments>
                    <CommandName>Click</CommandName>
                    <ControlName>PurchCopyJournalHeader</ControlName>
                    <ControlType>MenuItemButton</ControlType>
                  </Node>
                </Children>
                <IsForm>false</IsForm>
                <IsStepGroup>true</IsStepGroup>
                <Name>Copy from journal</Name>
              </Node>
            </Children>
          </RootScope>
          <UserActions xmlns:d2p1="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
            <d2p1:anyType z:Ref="i3" xmlns:z="http://schemas.microsoft.com/2003/10/Serialization/" />
          </UserActions>
          <Version>1</Version>
        </Recording>
        """;

    /// <summary>
    /// The action tree hangs off <c>RootScope</c>, and <c>UserActions</c> is a
    /// list of back-references to nodes already in it. Reading the latter as
    /// the action list is how a real recording converts to nothing at all.
    /// </summary>
    [Fact]
    public void ReadsTheActionTreeFromRootScopeAndIgnoresUserActions()
    {
        var rec = RecordingReader.Parse(RealShape);

        Assert.Equal("Confirm purchase order", rec.Name);
        Assert.Single(rec.Nodes);

        var scope = rec.Nodes[0];
        Assert.Equal("Scope", scope.Kind);
        Assert.True(scope.Flag("IsStepGroup"));
        Assert.Single(scope.Children);
    }

    /// <summary>
    /// A command argument is positional data, frequently a JSON blob. Letting
    /// it into the property bag as "Value" makes a filter command
    /// indistinguishable from a field edit.
    /// </summary>
    [Fact]
    public void CommandArgumentsStayOutOfThePropertyBag()
    {
        var node = RecordingReader.Parse(RealShape).Nodes[0].Children[0];

        Assert.Null(node.Prop("Value"));
        Assert.Equal("""[{"FieldName":"PurchId"}]""", node.Arg(0));
        Assert.Equal("1", node.Arg(1));
    }

    /// <summary>
    /// A <c>FormAnnotation</c> carries <c>MenuItemName</c>. Flattened into the
    /// property bag it would turn this click into a navigation.
    /// </summary>
    [Fact]
    public void AnnotationsStayOutOfThePropertyBag()
    {
        var node = RecordingReader.Parse(RealShape).Nodes[0].Children[0];

        Assert.Null(node.Prop("MenuItemName"));
        var annotation = Assert.Single(node.Annotations);
        Assert.Equal("FormAnnotation", annotation.Kind);
        Assert.Equal("SysBPMPane", annotation.Props["MenuItemName"]);
    }

    [Fact]
    public void ToleratesAlternateWrapperSpellings()
    {
        var alt = RealShape.Replace("<Children>", "<Childs>", StringComparison.Ordinal)
                           .Replace("</Children>", "</Childs>", StringComparison.Ordinal);

        var rec = RecordingReader.Parse(alt);

        Assert.Single(rec.Nodes);
        Assert.Single(rec.Nodes[0].Children);
    }

    [Fact]
    public void ReadsBothArchiveAndRawXmlToTheSameRecording()
    {
        var fromArchive = RecordingReader.Load(Fixtures.FixturePath("ConfirmPurchaseOrder.axtr"));
        var fromXml = RecordingReader.Load(Fixtures.FixturePath("ConfirmPurchaseOrder.xml"));

        Assert.Equal(fromArchive.Name, fromXml.Name);
        Assert.Equal(fromArchive.Nodes.Count, fromXml.Nodes.Count);
        Assert.Equal(fromArchive.Variables, fromXml.Variables);
    }
}
