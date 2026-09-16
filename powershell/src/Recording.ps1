# Reads an `.axtr` archive (or a bare recording `.xml`) into a loose node tree.
# Mirrors src/recording.rs and Recording.cs.
#
# The shape this targets is the real one: a DataContract serialization of
# Microsoft.Dynamics.Client.ServerForm.TaskRecording, whose action tree hangs
# off <RootScope><Children> rather than a top-level <Nodes>, and whose sibling
# <UserActions> is a list of z:Ref pointers back into that same tree. Reading
# the pointer list as a second action list yields a recording made entirely of
# empty nodes, which is why it is excluded by name below.

Set-StrictMode -Version Latest

# Wrapper elements that hold child nodes. Task Recorder has used several
# spellings over the years - including the famously non-English `Childs`.
$script:ChildWrappers = @('Children', 'Childs', 'Nodes', 'ChildNodes', 'Steps')

$script:NodeElements = @(
    'AxTaskRecordingNode',
    'Node',
    'UserAction',
    'TaskUserActionNode',
    'AxTaskRecordingUserActionNode'
)

# Elements that look node-ish by name but are not actions. `UserActions` is the
# dangerous one: it matches every loose "contains UserAction" test.
$script:NotNodeElements = @(
    'UserActions',
    'Annotations',
    'Annotation',
    'Arguments',
    'CommandArgument',
    'FormContexts',
    'NavigationPath',
    'Variables',
    'CanonicalUserAction'
)

function New-RecNode {
    param([Parameter(Mandatory)] [string] $Kind)

    return [pscustomobject]@{
        Kind        = $Kind
        # Ordinal-sorted, so the property bag enumerates identically in all
        # three implementations - the report prints it in this order.
        Props       = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)
        Args        = [System.Collections.Generic.List[string]]::new()
        Annotations = [System.Collections.Generic.List[object]]::new()
        Children    = [System.Collections.Generic.List[object]]::new()
    }
}

function Get-RecProp {
    <#
        .SYNOPSIS
        Case-insensitive property lookup across several candidate names, since
        the same concept is spelled differently by different action types.
    #>
    param(
        [Parameter(Mandatory)] $Node,
        [Parameter(Mandatory)] [string[]] $Names
    )

    foreach ($name in $Names) {
        foreach ($entry in $Node.Props.GetEnumerator()) {
            if ([string]::Equals($entry.Key, $name, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $entry.Value
            }
        }
    }

    return $null
}

function Test-RecProp {
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] [string[]] $Names)
    return $null -ne (Get-RecProp -Node $Node -Names $Names)
}

function Test-RecFlag {
    <#
        .SYNOPSIS
        A property that reads as a boolean `true`.
    #>
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] [string] $Name)

    $value = Get-RecProp -Node $Node -Names @($Name)
    if ($null -eq $value) { return $false }
    return [string]::Equals($value, 'true', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-RecArg {
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] [int] $Index)

    if ($Index -ge 0 -and $Index -lt $Node.Args.Count) { return $Node.Args[$Index] }
    return $null
}

function Get-RecDescription {
    <#
        .SYNOPSIS
        The first four non-empty properties, which is what a TODO comment shows.
    #>
    param([Parameter(Mandatory)] $Node)

    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $Node.Props.GetEnumerator()) {
        if ($entry.Value.Length -eq 0) { continue }
        if ($parts.Count -ge 4) { break }
        $parts.Add("$($entry.Key)=$($entry.Value)")
    }

    return [string]::Join(', ', $parts)
}

function Import-Recording {
    <#
        .SYNOPSIS
        Load from an `.axtr` (a zip archive) or a raw recording `.xml`.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "reading ${Path}: file not found"
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)

    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0x50 -and $bytes[1] -eq 0x4B) {
        $xmlText = Read-RecordingFromArchive -Bytes $bytes
    }
    else {
        $xmlText = ConvertFrom-Utf8Bytes -Bytes $bytes
    }

    return ConvertFrom-RecordingText -XmlText $xmlText
}

function ConvertFrom-Utf8Bytes {
    param([Parameter(Mandatory)] [byte[]] $Bytes)

    $encoding = [System.Text.UTF8Encoding]::new($false, $false)
    return $encoding.GetString($Bytes).TrimStart([char]0xFEFF)
}

function Read-RecordingFromArchive {
    param([Parameter(Mandatory)] [byte[]] $Bytes)

    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue

    $stream = [System.IO.MemoryStream]::new($Bytes)
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)

    try {
        # .axtr archives also carry screenshots and a manifest, so prefer the
        # entry that actually looks like the recording.
        $best = $null
        $bestScore = [int]::MinValue

        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.ToLowerInvariant()
            if (-not $name.EndsWith('.xml')) { continue }

            $score = 1
            if ($name.Contains('recording')) { $score = 2 }

            if ($null -eq $best -or $score -gt $bestScore) {
                $best = $entry
                $bestScore = $score
            }
        }

        if ($null -eq $best) {
            throw 'no .xml entry found inside the .axtr archive'
        }

        $entryStream = $best.Open()
        $memory = [System.IO.MemoryStream]::new()
        try {
            $entryStream.CopyTo($memory)
            return ConvertFrom-Utf8Bytes -Bytes $memory.ToArray()
        }
        finally {
            $entryStream.Dispose()
            $memory.Dispose()
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

function ConvertFrom-RecordingText {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $XmlText)

    $doc = ConvertFrom-RecordingXml -Xml $XmlText

    $name = $doc.TextOf('Name')
    if ($null -eq $name) { $name = $doc.TextOf('Description') }
    if ($null -eq $name) { $name = $doc.TextOf('RecordingName') }
    if ($null -eq $name) { $name = 'Recording' }

    $variables = Get-RecordingVariables -Doc $doc

    $container = Get-ActionContainer -Doc $doc
    $nodes = [System.Collections.Generic.List[object]]::new()
    foreach ($child in $container.Children) {
        if (Test-NodeElement $child) { $nodes.Add((ConvertFrom-XmlNode $child)) }
    }

    return [pscustomobject]@{
        Name      = $name
        Variables = $variables
        Nodes     = $nodes
    }
}

function Get-ActionContainer {
    <#
        .SYNOPSIS
        Find the element whose children are the recording's top-level actions.
    #>
    param([Parameter(Mandatory)] $Doc)

    # The real export: `<RootScope><Children>`. RootScope is itself a scope
    # node, so its own `Children` is the action list.
    $rootScope = $Doc.Child('RootScope')
    if ($null -ne $rootScope) {
        foreach ($wrapper in $script:ChildWrappers) {
            $found = $rootScope.Child($wrapper)
            if ($null -ne $found) { return $found }
        }
        return $rootScope
    }

    foreach ($wrapper in $script:ChildWrappers) {
        $found = $Doc.Child($wrapper)
        if ($null -ne $found) { return $found }
    }

    return $Doc
}

function Test-NodeElement {
    param([Parameter(Mandatory)] $Element)

    foreach ($name in $script:NotNodeElements) {
        if ([string]::Equals($Element.Name, $name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    foreach ($name in $script:NodeElements) {
        if ([string]::Equals($Element.Name, $name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    $lower = $Element.Name.ToLowerInvariant()
    return $lower.Contains('node') -or $lower.EndsWith('useraction')
}

function ConvertFrom-XmlNode {
    param([Parameter(Mandatory)] $Element)

    $kind = $Element.Attr('type')
    if ($null -eq $kind) { $kind = $Element.TextOf('ActionType') }
    if ($null -eq $kind) { $kind = $Element.TextOf('Type') }
    if ($null -eq $kind) { $kind = $Element.Name }

    $node = New-RecNode -Kind $kind
    Expand-XmlNode -Element $Element -Node $node
    return $node
}

function Expand-XmlNode {
    <#
        .SYNOPSIS
        Pull scalar descendants into the property bag and node-ish descendants
        into Children, stepping through wrapper elements. Arguments and
        Annotations are lifted into their own fields instead.
    #>
    param([Parameter(Mandatory)] $Element, [Parameter(Mandatory)] $Node)

    foreach ($child in $Element.Children) {
        $isWrapper = $false
        foreach ($wrapper in $script:ChildWrappers) {
            if ([string]::Equals($child.Name, $wrapper, [System.StringComparison]::OrdinalIgnoreCase)) {
                $isWrapper = $true
                break
            }
        }

        if ($isWrapper) {
            foreach ($grand in $child.Children) {
                if (Test-NodeElement $grand) { $Node.Children.Add((ConvertFrom-XmlNode $grand)) }
                else { Expand-XmlNode -Element $grand -Node $Node }
            }
            continue
        }

        if ([string]::Equals($child.Name, 'Arguments', [System.StringComparison]::OrdinalIgnoreCase)) {
            foreach ($arg in $child.ChildrenNamed('CommandArgument')) {
                $value = $arg.TextOf('Value')
                if ($null -eq $value) { $value = '' }
                $Node.Args.Add($value)
            }
            continue
        }

        if ([string]::Equals($child.Name, 'Annotations', [System.StringComparison]::OrdinalIgnoreCase)) {
            foreach ($annotation in $child.ChildrenNamed('Annotation')) {
                $Node.Annotations.Add((ConvertFrom-XmlAnnotation $annotation))
            }
            continue
        }

        if ((Test-NodeElement $child) -and -not $child.IsScalar()) {
            $Node.Children.Add((ConvertFrom-XmlNode $child))
            continue
        }

        if ($child.IsScalar()) {
            $text = $child.Text().Trim()
            if ($text.Length -gt 0 -and -not $Node.Props.ContainsKey($child.Name)) {
                $Node.Props[$child.Name] = $text
            }
            continue
        }

        $denied = $false
        foreach ($name in $script:NotNodeElements) {
            if ([string]::Equals($child.Name, $name, [System.StringComparison]::OrdinalIgnoreCase)) {
                $denied = $true
                break
            }
        }

        # An unrecognized grouping element: keep descending so we do not
        # silently lose the actions underneath it.
        if (-not $denied) { Expand-XmlNode -Element $child -Node $Node }
    }
}

function ConvertFrom-XmlAnnotation {
    param([Parameter(Mandatory)] $Element)

    $kind = $Element.Attr('type')
    if ($null -eq $kind) { $kind = $Element.Name }

    $props = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)
    foreach ($child in $Element.Children) {
        if (-not $child.IsScalar()) { continue }

        $text = $child.Text().Trim()
        if ($text.Length -gt 0) { $props[$child.Name] = $text }
    }

    return [pscustomobject]@{ Kind = $kind; Props = $props }
}

function Get-RecordingVariables {
    <#
        .SYNOPSIS
        Variables in document order - the order the other two implementations
        produce, since all three are held to byte-identical output.
    #>
    param([Parameter(Mandatory)] $Doc)

    $found = [System.Collections.Generic.List[object]]::new()

    $walk = {
        param($element)

        if ([string]::Equals($element.Name, 'Variables', [System.StringComparison]::OrdinalIgnoreCase)) {
            foreach ($child in $element.Children) {
                $name = $child.TextOf('Name')
                if ($null -eq $name) { $name = $child.TextOf('VariableName') }
                if ($null -eq $name) { $name = $child.Attr('Name') }
                if ($null -eq $name) { continue }

                $value = $child.TextOf('Value')
                if ($null -eq $value) { $value = $child.TextOf('DefaultValue') }
                if ($null -eq $value) { $value = '' }

                $found.Add([pscustomobject]@{ Name = $name; Value = $value })
            }
            return
        }

        foreach ($child in $element.Children) { & $walk $child }
    }

    & $walk $Doc

    # The comma keeps PowerShell from unrolling the list: an empty one would
    # otherwise come back as $null and every caller would have to test for it.
    return ,$found
}
