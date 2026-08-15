using System.Text;
using System.Xml;

namespace Rsat2Pw;

public sealed class Element
{
    public required string Name { get; init; }

    public List<KeyValuePair<string, string>> Attrs { get; } = [];

    public List<Element> Children { get; } = [];

    internal StringBuilder TextBuilder { get; } = new();

    public string Text => TextBuilder.ToString();

    public string? Attr(string name)
    {
        foreach (var (key, value) in Attrs)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    public Element? Child(string name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Element> ChildrenNamed(string name) =>
        Children.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public string? TextOf(string name)
    {
        var child = Child(name);
        if (child is null)
        {
            return null;
        }

        var text = child.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    public Element? FindDescendant(string name)
    {
        if (string.Equals(Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        foreach (var child in Children)
        {
            var found = child.FindDescendant(name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public bool IsScalar => Children.Count == 0;
}

public static class Xml
{
    public static Element Parse(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CheckCharacters = false,
        };

        using var reader = XmlReader.Create(new StringReader(xml), settings);

        var stack = new List<Element>();
        Element? root = null;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var isEmpty = reader.IsEmptyElement;
                    var element = ElementFrom(reader);

                    if (isEmpty)
                    {
                        Push(stack, ref root, element);
                    }
                    else
                    {
                        stack.Add(element);
                    }

                    break;
                }

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                {
                    if (stack.Count > 0)
                    {
                        stack[^1].TextBuilder.Append(reader.Value);
                    }

                    break;
                }

                case XmlNodeType.EndElement:
                {
                    if (stack.Count > 0)
                    {
                        var element = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        Push(stack, ref root, element);
                    }

                    break;
                }
            }
        }

        return root ?? throw new InvalidDataException("document contained no root element");
    }

    private static void Push(List<Element> stack, ref Element? root, Element element)
    {
        if (stack.Count > 0)
        {
            stack[^1].Children.Add(element);
        }
        else
        {
            root = element;
        }
    }

    private static Element ElementFrom(XmlReader reader)
    {
        var element = new Element { Name = reader.LocalName };

        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                if (reader.LocalName == "xmlns" || reader.Prefix == "xmlns")
                {
                    continue;
                }

                element.Attrs.Add(new KeyValuePair<string, string>(reader.LocalName, reader.Value));
            }

            reader.MoveToElement();
        }

        return element;
    }
}
