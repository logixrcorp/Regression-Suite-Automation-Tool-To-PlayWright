# A small read-only OpenXML reader. Mirrors Xlsx.cs, which in turn replaces
# calamine in the Rust build.
#
# An .xlsx is a zip of XML, so System.IO.Compression plus the element tree in
# Xml.ps1 covers it. It reads values only - formatting and date conversion are
# deliberately out of scope, exactly as in the other two ports.

Set-StrictMode -Version Latest

function Get-XlsxSheetNames {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    $archive = Open-XlsxArchive -Path $Path
    try {
        $names = [System.Collections.Generic.List[string]]::new()
        foreach ($sheet in (Get-XlsxSheetRefs -Archive $archive)) { $names.Add($sheet.Name) }
        return ,$names.ToArray()
    }
    finally { $archive.Dispose() }
}

function Read-XlsxSheet {
    <#
        .SYNOPSIS
        Read a worksheet as a rectangular table of trimmed-to-content rows.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path, [AllowNull()] [string] $Sheet)

    $archive = Open-XlsxArchive -Path $Path
    try {
        $sheets = Get-XlsxSheetRefs -Archive $archive
        if ($sheets.Count -eq 0) { throw 'workbook has no sheets' }

        $target = $null
        if ([string]::IsNullOrEmpty($Sheet)) { $target = $sheets[0] }
        else {
            foreach ($candidate in $sheets) {
                if ([string]::Equals($candidate.Name, $Sheet, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $target = $candidate
                    break
                }
            }
            if ($null -eq $target) {
                throw "worksheet '$Sheet' not found; the workbook has: $(($sheets | ForEach-Object { $_.Name }) -join ', ')"
            }
        }

        $entry = $archive.GetEntry($target.Path)
        if ($null -eq $entry) { throw "worksheet part '$($target.Path)' is missing from the workbook" }

        $sharedStrings = Read-XlsxSharedStrings -Archive $archive
        return ,(Read-XlsxCells -SheetXml (Read-XlsxEntry -Entry $entry) -SharedStrings $sharedStrings)
    }
    finally { $archive.Dispose() }
}

function Open-XlsxArchive {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw "opening ${Path}: file not found" }

    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $stream = [System.IO.MemoryStream]::new($bytes)
    return [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)
}

function Read-XlsxEntry {
    param([Parameter(Mandatory)] $Entry)

    $stream = $Entry.Open()
    $memory = [System.IO.MemoryStream]::new()
    try {
        $stream.CopyTo($memory)
        return (ConvertFrom-Utf8Bytes -Bytes $memory.ToArray())
    }
    finally {
        $stream.Dispose()
        $memory.Dispose()
    }
}

function Get-XlsxSheetRefs {
    param([Parameter(Mandatory)] $Archive)

    $sheets = [System.Collections.Generic.List[object]]::new()

    $entry = $Archive.GetEntry('xl/workbook.xml')
    if ($null -eq $entry) { return ,$sheets }

    $relationships = Read-XlsxRelationships -Archive $Archive
    $doc = ConvertFrom-RecordingXml -Xml (Read-XlsxEntry -Entry $entry)

    $sheetsElement = $doc.Child('sheets')
    if ($null -eq $sheetsElement) { return ,$sheets }

    foreach ($sheet in $sheetsElement.ChildrenNamed('sheet')) {
        $name = $sheet.Attr('name')
        if ($null -eq $name) { continue }

        $id = $sheet.Attr('id')
        $target = $null
        if ($null -ne $id -and $relationships.ContainsKey($id)) { $target = $relationships[$id] }
        if ($null -eq $target) { continue }

        $sheets.Add([pscustomobject]@{ Name = $name; Path = (ConvertTo-XlsxPart -Target $target) })
    }

    return ,$sheets
}

function Read-XlsxRelationships {
    param([Parameter(Mandatory)] $Archive)

    $map = @{}

    $entry = $Archive.GetEntry('xl/_rels/workbook.xml.rels')
    if ($null -eq $entry) { return $map }

    $doc = ConvertFrom-RecordingXml -Xml (Read-XlsxEntry -Entry $entry)
    foreach ($relationship in $doc.ChildrenNamed('Relationship')) {
        $id = $relationship.Attr('Id')
        $target = $relationship.Attr('Target')
        if ($null -ne $id -and $null -ne $target) { $map[$id] = $target }
    }

    return $map
}

function ConvertTo-XlsxPart {
    param([Parameter(Mandatory)] [string] $Target)

    $cleaned = $Target.Replace('\', '/')
    if ($cleaned.StartsWith('/')) { return $cleaned.TrimStart('/') }
    return "xl/$cleaned"
}

function Read-XlsxSharedStrings {
    param([Parameter(Mandatory)] $Archive)

    $result = [System.Collections.Generic.List[string]]::new()

    $entry = $Archive.GetEntry('xl/sharedStrings.xml')
    if ($null -eq $entry) { return ,$result }

    $doc = ConvertFrom-RecordingXml -Xml (Read-XlsxEntry -Entry $entry)
    foreach ($si in $doc.ChildrenNamed('si')) { $result.Add((Get-XlsxConcatText -Element $si)) }

    return ,$result
}

function Get-XlsxConcatText {
    <#
        .SYNOPSIS
        All the `t` runs under an element, joined - a shared string can be
        split across several runs when part of it is formatted differently.
    #>
    param([Parameter(Mandatory)] $Element)

    $builder = [System.Text.StringBuilder]::new()

    $walk = {
        param($element)

        if ([string]::Equals($element.Name, 't', [System.StringComparison]::Ordinal)) {
            [void] $builder.Append($element.Text())
            return
        }

        foreach ($child in $element.Children) { & $walk $child }
    }

    & $walk $Element
    return $builder.ToString()
}

function Read-XlsxCells {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $SheetXml, [Parameter(Mandatory)] $SharedStrings)

    $doc = ConvertFrom-RecordingXml -Xml $SheetXml
    $sheetData = Find-XmlDescendant -Element $doc -Name 'sheetData'
    if ($null -eq $sheetData) { return ,@() }

    $cells = [System.Collections.Generic.List[object]]::new()
    $rowIndex = 0

    foreach ($row in $sheetData.ChildrenNamed('row')) {
        $declared = 0
        $r = $row.Attr('r')
        if ($null -ne $r -and [int]::TryParse($r, [ref] $declared) -and $declared -gt 0) {
            $rowIndex = $declared - 1
        }

        $colIndex = 0
        foreach ($cell in $row.ChildrenNamed('c')) {
            $reference = $cell.Attr('r')
            if ($null -ne $reference) {
                $parsed = Get-XlsxColumnIndex -Reference $reference
                if ($parsed -ge 0) { $colIndex = $parsed }
            }

            $value = Get-XlsxCellValue -Cell $cell -SharedStrings $SharedStrings
            if ($value.Length -gt 0) {
                $cells.Add([pscustomobject]@{ Row = $rowIndex; Col = $colIndex; Value = $value })
            }

            $colIndex += 1
        }

        $rowIndex += 1
    }

    if ($cells.Count -eq 0) { return ,@() }

    $minRow = [int]::MaxValue; $maxRow = [int]::MinValue
    $minCol = [int]::MaxValue; $maxCol = [int]::MinValue
    foreach ($cell in $cells) {
        if ($cell.Row -lt $minRow) { $minRow = $cell.Row }
        if ($cell.Row -gt $maxRow) { $maxRow = $cell.Row }
        if ($cell.Col -lt $minCol) { $minCol = $cell.Col }
        if ($cell.Col -gt $maxCol) { $maxCol = $cell.Col }
    }

    $width = $maxCol - $minCol + 1

    $table = [System.Collections.Generic.List[object]]::new()
    for ($r = $minRow; $r -le $maxRow; $r++) {
        $line = [System.Collections.Generic.List[string]]::new()
        for ($c = 0; $c -lt $width; $c++) { $line.Add('') }
        $table.Add($line)
    }

    foreach ($cell in $cells) {
        $table[$cell.Row - $minRow][$cell.Col - $minCol] = $cell.Value
    }

    return ,$table
}

function Find-XmlDescendant {
    param([Parameter(Mandatory)] $Element, [Parameter(Mandatory)] [string] $Name)

    if ([string]::Equals($Element.Name, $Name, [System.StringComparison]::OrdinalIgnoreCase)) { return $Element }

    foreach ($child in $Element.Children) {
        $found = Find-XmlDescendant -Element $child -Name $Name
        if ($null -ne $found) { return $found }
    }

    return $null
}

function Get-XlsxCellValue {
    param([Parameter(Mandatory)] $Cell, [Parameter(Mandatory)] $SharedStrings)

    $type = $Cell.Attr('t')
    if ($null -eq $type) { $type = 'n' }

    switch ($type) {
        's' {
            $raw = $Cell.TextOf('v')
            $index = 0
            if ($null -ne $raw -and [int]::TryParse($raw, [ref] $index) -and
                $index -ge 0 -and $index -lt $SharedStrings.Count) {
                return $SharedStrings[$index]
            }
            return ''
        }

        'inlineStr' {
            $inline = $Cell.Child('is')
            if ($null -eq $inline) { return '' }
            return (Get-XlsxConcatText -Element $inline)
        }

        'str' {
            $value = $Cell.TextOf('v')
            if ($null -eq $value) { return '' }
            return $value
        }

        'b' {
            if ($Cell.TextOf('v') -eq '1') { return 'true' }
            return 'false'
        }

        'e' {
            $value = $Cell.TextOf('v')
            if ($null -eq $value) { return '' }
            return $value
        }

        default {
            $raw = $Cell.TextOf('v')
            if ($null -eq $raw) { return '' }
            return (Format-XlsxNumber -Raw $raw)
        }
    }
}

function Format-XlsxNumber {
    <#
        .SYNOPSIS
        A whole number loses its decimal tail, so `10.0` reads back as `10`
        rather than as a value nobody typed.
    #>
    param([Parameter(Mandatory)] [string] $Raw)

    $number = 0.0
    $styles = [System.Globalization.NumberStyles]::Float
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture

    if (-not [double]::TryParse($Raw, $styles, $invariant, [ref] $number)) { return $Raw }

    if ([Math]::Abs($number % 1) -lt [double]::Epsilon -and [Math]::Abs($number) -lt 9.007199254740992E15) {
        return ([long] $number).ToString($invariant)
    }

    return $number.ToString($invariant)
}

function Get-XlsxColumnIndex {
    param([Parameter(Mandatory)] [string] $Reference)

    $column = 0
    $sawLetter = $false

    foreach ($c in $Reference.ToCharArray()) {
        $isLetter = ($c -ge 'a' -and $c -le 'z') -or ($c -ge 'A' -and $c -le 'Z')
        if (-not $isLetter) { break }

        $column = ($column * 26) + ([int][char]::ToUpperInvariant($c) - [int][char]'A' + 1)
        $sawLetter = $true
    }

    if ($sawLetter) { return $column - 1 }
    return -1
}
