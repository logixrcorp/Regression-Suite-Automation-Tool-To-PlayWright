# Test data: recorded values and RSAT parameter workbooks -> Playwright
# fixtures. Mirrors src/params.rs and Params.cs.
#
# The whole point of RSAT is that one recording runs against many rows of data,
# so variables must become a data-driven fixture rather than literals baked
# into the spec.

Set-StrictMode -Version Latest

# Sheets that mark a workbook as one RSAT generated for itself.
$script:RsatSheets = @('TestCaseSteps', 'MessageValidation')

function New-CaseRow {
    param([Parameter(Mandatory)] [string] $Label)

    return [pscustomobject]@{
        Label  = $Label
        Values = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)
    }
}

function New-CaseSet {
    return [pscustomobject]@{
        Fields = [System.Collections.Generic.List[string]]::new()
        Rows   = [System.Collections.Generic.List[object]]::new()
        Source = ''
    }
}

function Get-CasesFromRecording {
    <#
        .SYNOPSIS
        Fall back to the defaults captured in the recording itself.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $TestCase)

    $cases = New-CaseSet
    $cases.Source = 'recording defaults'

    foreach ($variable in $TestCase.Variables) { $cases.Fields.Add($variable.Name) }

    if ($cases.Fields.Count -gt 0) {
        $row = New-CaseRow -Label 'recorded defaults'
        foreach ($variable in $TestCase.Variables) { $row.Values[$variable.Name] = $variable.Default }
        $cases.Rows.Add($row)
    }

    return $cases
}

function Test-RsatWorkbook {
    <#
        .SYNOPSIS
        Is this one of RSAT's own parameter workbooks?

        .DESCRIPTION
        It matters because the layout is nothing like the plain sheet this
        reader understands: a title block, a "Saved variables" table, one row
        per recorded step, and `{{Form_Control_42}}` variable names. Read as a
        plain sheet it does not fail - it quietly yields cases built out of the
        title block, which is the worst of the available outcomes.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $SheetNames)

    foreach ($marker in $script:RsatSheets) {
        foreach ($name in $SheetNames) {
            if ([string]::Equals($name, $marker, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
    }

    return $false
}

function Get-CasesFromWorkbook {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [AllowNull()] [string] $Sheet,
        [Parameter(Mandatory)] $TestCase
    )

    $sheetNames = Get-XlsxSheetNames -Path $Path

    if ([string]::IsNullOrEmpty($Sheet) -and (Test-RsatWorkbook -SheetNames $sheetNames)) {
        throw ("$Path looks like an RSAT parameter workbook (sheets: $($sheetNames -join ', ')).`n`n" +
            "That layout is not supported yet - reading it as a plain sheet would `n" +
            "silently invent test cases out of its title block. Either:`n" +
            "  * point --sheet at a plain sheet of your own (a header row of `n" +
            "    variable names, one case per row), or`n" +
            "  * drop --params, and the generated data module is seeded with the `n" +
            "    values the recording itself captured.")
    }

    $sheetName = $Sheet
    if ([string]::IsNullOrEmpty($sheetName)) {
        if ($sheetNames.Count -eq 0) { throw 'workbook has no sheets' }
        $sheetName = $sheetNames[0]
    }

    $raw = Read-XlsxSheet -Path $Path -Sheet $Sheet

    $table = [System.Collections.Generic.List[object]]::new()
    foreach ($row in $raw) {
        $trimmed = [System.Collections.Generic.List[string]]::new()
        $hasContent = $false
        foreach ($cell in $row) {
            $value = $cell.Trim()
            $trimmed.Add($value)
            if ($value.Length -gt 0) { $hasContent = $true }
        }
        if ($hasContent) { $table.Add($trimmed) }
    }

    if (Test-TallLayout -Table $table) { $cases = ConvertFrom-TallTable -Table $table }
    else { $cases = ConvertFrom-WideTable -Table $table }

    $source = "$Path [$sheetName]"
    $cases.Source = $source

    # A workbook with a header row but no data rows would otherwise produce a
    # single case of blanks - a spec that types empty strings into every field
    # while looking perfectly healthy. The values the recorder captured are the
    # better answer, and the source line says so rather than pretending the
    # workbook supplied them.
    if ($cases.Rows.Count -eq 0) {
        $cases.Rows.Add((New-CaseRow -Label 'recorded defaults'))
        $cases.Source = "$source (no data rows; using recorded values)"
    }

    # Any variable the recording expects but the workbook omits still needs to
    # exist on the params object, or the generated spec will not compile.
    foreach ($variable in $TestCase.Variables) {
        if (-not ($cases.Fields -ccontains $variable.Name)) {
            $cases.Fields.Add($variable.Name)
            foreach ($row in $cases.Rows) {
                if (-not $row.Values.ContainsKey($variable.Name)) {
                    $row.Values[$variable.Name] = $variable.Default
                }
            }
        }
    }

    return $cases
}

function Test-TallLayout {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] $Table)

    if ($Table.Count -eq 0) { return $false }

    $header = $Table[0]
    if ($header.Count -lt 2) { return $false }

    return [string]::Equals($header[0], 'name', [System.StringComparison]::OrdinalIgnoreCase) -and
           [string]::Equals($header[1], 'value', [System.StringComparison]::OrdinalIgnoreCase)
}

function ConvertFrom-TallTable {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] $Table)

    $cases = New-CaseSet
    $row = New-CaseRow -Label 'workbook'
    $mapped = @{}

    for ($n = 1; $n -lt $Table.Count; $n++) {
        $line = $Table[$n]
        if ($line.Count -eq 0 -or $line[0].Length -eq 0) { continue }

        $baseName = ConvertTo-SafeIdent $line[0]
        if ($mapped.ContainsKey($baseName)) { $field = $mapped[$baseName] }
        else {
            $field = Get-UniqueIdent -Candidate $baseName -Taken $cases.Fields
            $cases.Fields.Add($field)
            $mapped[$baseName] = $field
        }

        $value = ''
        if ($line.Count -gt 1) { $value = $line[1] }
        $row.Values[$field] = $value
    }

    $cases.Rows.Add($row)
    return $cases
}

function ConvertFrom-WideTable {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] $Table)

    $cases = New-CaseSet
    if ($Table.Count -eq 0) { return $cases }

    $header = $Table[0]

    $labelCol = -1
    for ($i = 0; $i -lt $header.Count; $i++) {
        $h = $header[$i].ToLowerInvariant()
        if ($h -eq 'case' -or $h -eq 'testcase' -or $h -eq 'test case' -or $h -eq 'scenario') {
            $labelCol = $i
            break
        }
    }

    $columns = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $header.Count; $i++) {
        if ($i -eq $labelCol -or $header[$i].Length -eq 0) { continue }

        $name = Get-UniqueIdent -Candidate (ConvertTo-SafeIdent $header[$i]) -Taken $cases.Fields
        $cases.Fields.Add($name)
        $columns.Add([pscustomobject]@{ Index = $i; Name = $name })
    }

    for ($n = 0; $n -lt $Table.Count - 1; $n++) {
        $line = $Table[$n + 1]

        $label = "row $($n + 1)"
        if ($labelCol -ge 0 -and $labelCol -lt $line.Count -and $line[$labelCol].Length -gt 0) {
            $label = $line[$labelCol]
        }

        $row = New-CaseRow -Label $label
        foreach ($column in $columns) {
            $value = ''
            if ($column.Index -lt $line.Count) { $value = $line[$column.Index] }
            $row.Values[$column.Name] = $value
        }

        $cases.Rows.Add($row)
    }

    return $cases
}
