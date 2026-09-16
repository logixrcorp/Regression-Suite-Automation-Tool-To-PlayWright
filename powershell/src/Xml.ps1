# A tiny, schema-tolerant XML tree. Mirrors src/xml.rs and Xml.cs.
#
# The three implementations are held to byte-identical output, so this one
# reads the document through the same System.Xml.XmlReader the C# port uses,
# with the same settings. That is deliberate: XmlReader already does the
# CRLF normalization XML 1.0 s2.11 requires and already exposes LocalName with
# the namespace prefix stripped, so matching it is a matter of calling it the
# same way rather than reimplementing its decisions and hoping they agree.

Set-StrictMode -Version Latest

class XmlElement_ {
    [string] $Name
    [System.Collections.Generic.List[object]] $Attrs
    [System.Collections.Generic.List[object]] $Children
    [System.Text.StringBuilder] $TextBuilder

    XmlElement_([string] $name) {
        $this.Name = $name
        $this.Attrs = [System.Collections.Generic.List[object]]::new()
        $this.Children = [System.Collections.Generic.List[object]]::new()
        $this.TextBuilder = [System.Text.StringBuilder]::new()
    }

    [string] Text() { return $this.TextBuilder.ToString() }

    # Case-insensitive, like every lookup in the other two implementations.
    [string] Attr([string] $name) {
        foreach ($pair in $this.Attrs) {
            if ([string]::Equals($pair.Key, $name, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $pair.Value
            }
        }
        return $null
    }

    [object] Child([string] $name) {
        foreach ($child in $this.Children) {
            if ([string]::Equals($child.Name, $name, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $child
            }
        }
        return $null
    }

    [object[]] ChildrenNamed([string] $name) {
        $found = [System.Collections.Generic.List[object]]::new()
        foreach ($child in $this.Children) {
            if ([string]::Equals($child.Name, $name, [System.StringComparison]::OrdinalIgnoreCase)) {
                $found.Add($child)
            }
        }
        return $found.ToArray()
    }

    # Trimmed text of a direct child element, if non-empty.
    [string] TextOf([string] $name) {
        $child = $this.Child($name)
        if ($null -eq $child) { return $null }

        $text = $child.Text().Trim()
        if ($text.Length -eq 0) { return $null }
        return $text
    }

    [bool] IsScalar() { return $this.Children.Count -eq 0 }
}

function ConvertFrom-RecordingXml {
    <#
        .SYNOPSIS
        Parse XML into the loose element tree the rest of the converter reads.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Xml
    )

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
    $settings.XmlResolver = $null
    $settings.IgnoreComments = $true
    $settings.IgnoreProcessingInstructions = $true
    $settings.CheckCharacters = $false

    $stringReader = [System.IO.StringReader]::new($Xml)
    $reader = [System.Xml.XmlReader]::Create($stringReader, $settings)

    $stack = [System.Collections.Generic.List[object]]::new()
    $root = $null

    try {
        while ($reader.Read()) {
            switch ($reader.NodeType) {
                ([System.Xml.XmlNodeType]::Element) {
                    $isEmpty = $reader.IsEmptyElement
                    $element = New-XmlElementFromReader -Reader $reader

                    if ($isEmpty) {
                        if ($stack.Count -gt 0) {
                            $stack[$stack.Count - 1].Children.Add($element)
                        }
                        else { $root = $element }
                    }
                    else { $stack.Add($element) }
                }

                ([System.Xml.XmlNodeType]::Text)                   { Add-XmlText $stack $reader.Value }
                ([System.Xml.XmlNodeType]::CDATA)                  { Add-XmlText $stack $reader.Value }
                ([System.Xml.XmlNodeType]::Whitespace)             { Add-XmlText $stack $reader.Value }
                ([System.Xml.XmlNodeType]::SignificantWhitespace)  { Add-XmlText $stack $reader.Value }

                ([System.Xml.XmlNodeType]::EndElement) {
                    if ($stack.Count -gt 0) {
                        $element = $stack[$stack.Count - 1]
                        $stack.RemoveAt($stack.Count - 1)

                        if ($stack.Count -gt 0) {
                            $stack[$stack.Count - 1].Children.Add($element)
                        }
                        else { $root = $element }
                    }
                }
            }
        }
    }
    finally {
        $reader.Dispose()
        $stringReader.Dispose()
    }

    if ($null -eq $root) {
        throw 'document contained no root element'
    }

    return $root
}

function Add-XmlText {
    param($Stack, [string] $Value)

    if ($Stack.Count -gt 0) {
        [void] $Stack[$Stack.Count - 1].TextBuilder.Append($Value)
    }
}

function New-XmlElementFromReader {
    param([Parameter(Mandatory)] $Reader)

    # LocalName, so `i:type` arrives as `type` exactly as the other two ports
    # see it.
    $element = [XmlElement_]::new($Reader.LocalName)

    if ($Reader.HasAttributes) {
        while ($Reader.MoveToNextAttribute()) {
            # Namespace declarations are not properties of the node.
            if ($Reader.LocalName -eq 'xmlns' -or $Reader.Prefix -eq 'xmlns') { continue }

            $element.Attrs.Add([pscustomobject]@{
                Key   = $Reader.LocalName
                Value = $Reader.Value
            })
        }

        [void] $Reader.MoveToElement()
    }

    return $element
}
