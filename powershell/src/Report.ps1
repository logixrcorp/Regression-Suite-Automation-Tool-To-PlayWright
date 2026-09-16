# Conversion reporting: what the converter understood, and what it did not.
# Mirrors src/report.rs and Report.cs.
#
# The honest-gaps principle only pays off if the gaps are legible. The "not
# translated" section is the actual worklist for extending the mapping table -
# it carries every property the recorder supplied for each unmapped node, which
# is what you need to write the new rule.

Set-StrictMode -Version Latest

function New-ConversionReport {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $TestCase, [Parameter(Mandatory)] $Cases)

    $translatedByOp = [System.Collections.Generic.SortedDictionary[string, int]]::new([System.StringComparer]::Ordinal)
    $unmapped = [System.Collections.Generic.SortedDictionary[string, object]]::new([System.StringComparer]::Ordinal)
    $skipped = [System.Collections.Generic.SortedDictionary[string, object]]::new([System.StringComparer]::Ordinal)
    $outline = [System.Collections.Generic.List[object]]::new()

    Trace-IrActions -Actions $TestCase.Actions -Depth 0 -ByOp $translatedByOp -Unmapped $unmapped -Skipped $skipped -Outline $outline

    $notTranslated = 0
    foreach ($entry in $unmapped.Values) { $notTranslated += $entry.Count }

    $skippedCount = 0
    foreach ($entry in $skipped.Values) { $skippedCount += $entry.Count }

    $actions = Measure-IrActions $TestCase.Actions

    $referenced = [System.Collections.Generic.List[string]]::new()
    Get-ReferencedVariables -Actions $TestCase.Actions -Result $referenced

    $variables = [System.Collections.Generic.List[object]]::new()
    foreach ($variable in $TestCase.Variables) {
        $variables.Add([pscustomobject]@{
            Name       = $variable.Name
            Default    = $variable.Default
            Referenced = ($referenced -ccontains $variable.Name)
            InTestData = ($Cases.Fields -ccontains $variable.Name)
        })
    }

    $translated = $actions - $notTranslated - $skippedCount
    if ($translated -lt 0) { $translated = 0 }

    return [pscustomobject]@{
        Recording      = $TestCase.Name
        Actions        = $actions
        Translated     = $translated
        Skipped        = $skippedCount
        NotTranslated  = $notTranslated
        TranslatedByOp = $translatedByOp
        SkippedKinds   = [object[]] $skipped.Values
        NotTranslatedKinds = [object[]] $unmapped.Values
        Variables      = $variables
        TestCases      = $Cases.Rows.Count
        TestDataSource = $Cases.Source
        Outline        = $outline
    }
}

function Get-ReportPercent {
    <#
        .SYNOPSIS
        Percentage of actions that reached the runtime. Zero actions counts as
        fully covered rather than 0/0 - an empty recording has no gaps.
    #>
    param([Parameter(Mandatory)] $Report)

    if ($Report.Actions -eq 0) { return 100.0 }
    return ([double] $Report.Translated / [double] $Report.Actions) * 100.0
}

function Trace-IrActions {
    param(
        [Parameter(Mandatory)] [AllowNull()] [AllowEmptyCollection()] $Actions,
        [Parameter(Mandatory)] [int] $Depth,
        [Parameter(Mandatory)] $ByOp,
        [Parameter(Mandatory)] $Unmapped,
        [Parameter(Mandatory)] $Skipped,
        [Parameter(Mandatory)] $Outline
    )

    foreach ($action in $Actions) {
        $isSkipped = $action.Op -eq 'skipped'
        $translated = ($action.Op -ne 'unsupported') -and (-not $isSkipped)

        $Outline.Add([pscustomobject]@{
            Depth      = $Depth
            Op         = $action.Op
            Detail     = (Get-IrSummary $action)
            Translated = $translated
            Skipped    = $isSkipped
        })

        if ($action.Op -eq 'unsupported') {
            Add-UnmappedKind -Into $Unmapped -RawKind $action.RawKind -Props $action.Props
        }
        elseif ($isSkipped) {
            Add-UnmappedKind -Into $Skipped -RawKind $action.RawKind -Props $action.Props
        }
        else {
            if ($ByOp.ContainsKey($action.Op)) { $ByOp[$action.Op] = $ByOp[$action.Op] + 1 }
            else { $ByOp[$action.Op] = 1 }
        }

        Trace-IrActions -Actions (Get-IrChildren $action) -Depth ($Depth + 1) -ByOp $ByOp -Unmapped $Unmapped -Skipped $Skipped -Outline $Outline
    }
}

function Add-UnmappedKind {
    param([Parameter(Mandatory)] $Into, [Parameter(Mandatory)] [string] $RawKind, $Props)

    if (-not $Into.ContainsKey($RawKind)) {
        $Into[$RawKind] = [pscustomobject]@{
            RawKind = $RawKind
            Count   = 0
            Props   = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)
        }
    }

    $entry = $Into[$RawKind]
    $entry.Count = $entry.Count + 1

    if ($null -ne $Props) {
        foreach ($pair in $Props.GetEnumerator()) {
            # First example value wins; later occurrences only widen the key set.
            if (-not $entry.Props.ContainsKey($pair.Key)) { $entry.Props[$pair.Key] = $pair.Value }
        }
    }
}

function Format-ReportOp {
    <#
        .SYNOPSIS
        How an emitted op is named in the report. Everything reaching the
        runtime is a `d365.` method; `test.step` comes from Playwright, and a
        marker is a comment rather than a call at all.
    #>
    param([Parameter(Mandatory)] [string] $Op)

    $tick = [string][char]0x60

    if ($Op -eq 'marker') { return 'a comment' }
    if ($Op.Contains('.')) { return "$tick$Op()$tick" }
    return "${tick}d365.$Op()$tick"
}

function Format-ReportCell {
    <#
        .SYNOPSIS
        Keep a value from breaking out of a Markdown table cell.
    #>
    param([AllowEmptyString()] [string] $Text)

    if ($null -eq $Text) { $Text = '' }

    $flat = $Text.Replace("`r", ' ').Replace("`n", ' ').Replace('|', '\|')
    if ([string]::IsNullOrWhiteSpace($flat)) { return '_(empty)_' }

    $tick = [string][char]0x60
    return "$tick$flat$tick"
}

function Format-YesNo {
    param([bool] $Value)
    if ($Value) { return 'yes' }
    return 'no'
}

function ConvertTo-ReportMarkdown {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Report)

    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $tick = [string][char]0x60
    $fence = $tick * 3

    # Windows PowerShell 5.1 reads a .ps1 as ANSI unless the file carries a
    # UTF-8 BOM, so a literal em-dash in the source would arrive mangled and
    # the output would differ from the other two implementations. Every source
    # file here is pure ASCII; characters that have to reach the output are
    # built from their code points.
    $emDash = [string][char]0x2014

    $s = [System.Text.StringBuilder]::new()

    [void] $s.Append("# Conversion report: $($Report.Recording)`n`n")
    [void] $s.Append("Generated by ${tick}rsat2pw${tick}. Regenerate this alongside the spec whenever`n")
    [void] $s.Append("the recording or the mapping table changes.`n`n")

    # -- coverage -----------------------------------------------------------
    [void] $s.Append("## Coverage`n`n")
    [void] $s.Append("| | |`n|---|---|`n")
    [void] $s.Append("| Actions | $($Report.Actions.ToString($invariant)) |`n")
    [void] $s.Append("| Translated | $($Report.Translated.ToString($invariant)) ($([string]::Format($invariant, '{0:F1}', (Get-ReportPercent $Report)))%) |`n")
    [void] $s.Append("| Skipped | $($Report.Skipped.ToString($invariant)) |`n")
    [void] $s.Append("| Not translated | $($Report.NotTranslated.ToString($invariant)) |`n")
    [void] $s.Append("| Test cases | $($Report.TestCases.ToString($invariant)) (from $($Report.TestDataSource)) |`n`n")

    if ($Report.NotTranslated -eq 0) {
        [void] $s.Append("Every recorded action mapped to a runtime call.`n`n")
    }

    # -- translated ---------------------------------------------------------
    [void] $s.Append("## Translated`n`n")
    if ($Report.TranslatedByOp.Count -eq 0) {
        [void] $s.Append("_Nothing._`n`n")
    }
    else {
        [void] $s.Append("| Emitted call | Count |`n|---|---:|`n")
        foreach ($pair in $Report.TranslatedByOp.GetEnumerator()) {
            [void] $s.Append("| $(Format-ReportOp $pair.Key) | $($pair.Value.ToString($invariant)) |`n")
        }
        [void] $s.Append("`n")
    }

    # -- skipped ------------------------------------------------------------
    if ($Report.SkippedKinds.Count -gt 0) {
        [void] $s.Append("## Skipped`n`n")
        [void] $s.Append("Recorded, understood, and deliberately not replayed: client-internal bookkeeping`n")
        [void] $s.Append("with no user-visible effect. Each one still leaves a comment in the generated spec.`n`n")
        [void] $s.Append("| Action | Count |`n|---|---:|`n")
        foreach ($kind in $Report.SkippedKinds) {
            [void] $s.Append("| $tick$($kind.RawKind)$tick | $($kind.Count.ToString($invariant)) |`n")
        }
        [void] $s.Append("`n")
    }

    # -- not translated -----------------------------------------------------
    [void] $s.Append("## Not translated`n`n")
    if ($Report.NotTranslatedKinds.Count -eq 0) {
        [void] $s.Append("_Nothing $emDash full coverage._`n`n")
    }
    else {
        [void] $s.Append("Each heading is a Task Recorder action with no rule in ")
        [void] $s.Append("${tick}src/lower.rs${tick}.`nThe properties are everything the recorder ")
        [void] $s.Append("supplied, which is what a new`nmapping rule keys off.`n`n")

        foreach ($kind in $Report.NotTranslatedKinds) {
            $plural = 's'
            if ($kind.Count -eq 1) { $plural = '' }

            [void] $s.Append("### $tick$($kind.RawKind)$tick $emDash $($kind.Count.ToString($invariant)) occurrence$plural`n`n")

            if ($kind.Props.Count -eq 0) {
                [void] $s.Append("_No properties recorded._`n`n")
            }
            else {
                [void] $s.Append("| Property | Example value |`n|---|---|`n")
                foreach ($pair in $kind.Props.GetEnumerator()) {
                    [void] $s.Append("| $tick$($pair.Key)$tick | $(Format-ReportCell $pair.Value) |`n")
                }
                [void] $s.Append("`n")
            }
        }
    }

    # -- test data ----------------------------------------------------------
    [void] $s.Append("## Test data`n`n")
    if ($Report.Variables.Count -eq 0) {
        [void] $s.Append("_The recording captured no input values._`n`n")
    }
    else {
        [void] $s.Append("| Variable | Used by an action | In test data | Recorded default |`n")
        [void] $s.Append("|---|---|---|---|`n")
        foreach ($variable in $Report.Variables) {
            [void] $s.Append("| $tick$($variable.Name)$tick | $(Format-YesNo $variable.Referenced) | $(Format-YesNo $variable.InTestData) | $(Format-ReportCell $variable.Default) |`n")
        }
        [void] $s.Append("`n")

        $orphaned = $false
        foreach ($variable in $Report.Variables) {
            if ($variable.Referenced -and -not $variable.InTestData) { $orphaned = $true; break }
        }

        if ($orphaned) {
            [void] $s.Append("> Variables used by an action but absent from the test data fall back to`n")
            [void] $s.Append("> the recorded default, so the spec still compiles and runs.`n`n")
        }
    }

    # -- outline ------------------------------------------------------------
    [void] $s.Append("## Translation outline`n`n")
    [void] $s.Append("The recording in order. $tick!!$tick marks an action that was not translated, ")
    [void] $s.Append("$tick~~$tick one`nthat was deliberately skipped.`n`n")
    [void] $s.Append("$fence`n")

    foreach ($entry in $Report.Outline) {
        $marker = '   '
        if ($entry.Skipped) { $marker = '~~ ' }
        elseif (-not $entry.Translated) { $marker = '!! ' }

        $indent = '  ' * $entry.Depth
        $line = "$marker$indent$($entry.Op) $($entry.Detail)"
        [void] $s.Append($line.TrimEnd())
        [void] $s.Append("`n")
    }

    [void] $s.Append("$fence`n")

    return $s.ToString()
}

function ConvertTo-ReportJson {
    <#
        .SYNOPSIS
        The machine-readable form, for gating a build on coverage.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Report)

    $byOp = [ordered]@{}
    foreach ($pair in $Report.TranslatedByOp.GetEnumerator()) { $byOp[$pair.Key] = $pair.Value }

    $shape = [ordered]@{
        recording = $Report.Recording
        coverage  = [ordered]@{
            actions        = $Report.Actions
            translated     = $Report.Translated
            skipped        = $Report.Skipped
            not_translated = $Report.NotTranslated
        }
        translated_by_op = $byOp
        skipped_kinds    = @(Format-ReportKindsForJson -Kinds $Report.SkippedKinds)
        not_translated   = @(Format-ReportKindsForJson -Kinds $Report.NotTranslatedKinds)
        variables        = @(
            foreach ($variable in $Report.Variables) {
                [ordered]@{
                    name         = $variable.Name
                    default      = $variable.Default
                    referenced   = $variable.Referenced
                    in_test_data = $variable.InTestData
                }
            }
        )
        test_cases       = $Report.TestCases
        test_data_source = $Report.TestDataSource
        outline          = @(
            foreach ($entry in $Report.Outline) {
                [ordered]@{
                    depth      = $entry.Depth
                    op         = $entry.Op
                    detail     = $entry.Detail
                    translated = $entry.Translated
                    skipped    = $entry.Skipped
                }
            }
        )
    }

    return (ConvertTo-Json -InputObject $shape -Depth 12)
}

function Format-ReportKindsForJson {
    param([AllowNull()] [AllowEmptyCollection()] $Kinds)

    foreach ($kind in $Kinds) {
        $props = [ordered]@{}
        foreach ($pair in $kind.Props.GetEnumerator()) { $props[$pair.Key] = $pair.Value }

        [ordered]@{
            raw_kind = $kind.RawKind
            count    = $kind.Count
            props    = $props
        }
    }
}

function Write-ConversionSummary {
    <#
        .SYNOPSIS
        The same summary the other two CLIs print, on stderr.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Report)

    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $percent = [string]::Format($invariant, '{0:F1}', (Get-ReportPercent $Report))

    [Console]::Error.WriteLine("recording : $($Report.Recording)")
    [Console]::Error.WriteLine(
        "actions   : $($Report.Actions) ($($Report.Translated) translated, $($Report.Skipped) skipped, " +
        "$($Report.NotTranslated) not - $percent%)")
    [Console]::Error.WriteLine("test data : $($Report.TestCases) case(s) from $($Report.TestDataSource)")

    if ($Report.TranslatedByOp.Count -gt 0) {
        [Console]::Error.WriteLine('')
        [Console]::Error.WriteLine('translated:')
        foreach ($pair in $Report.TranslatedByOp.GetEnumerator()) {
            [Console]::Error.WriteLine(('  {0,-14} {1,3}' -f $pair.Key, $pair.Value))
        }
    }

    if ($Report.SkippedKinds.Count -gt 0) {
        [Console]::Error.WriteLine('')
        [Console]::Error.WriteLine('skipped (client-internal, deliberately not replayed):')
        foreach ($kind in $Report.SkippedKinds) {
            [Console]::Error.WriteLine(('  {0,-30} {1,3}' -f $kind.RawKind, $kind.Count))
        }
    }

    if ($Report.NotTranslatedKinds.Count -eq 0) {
        [Console]::Error.WriteLine('')
        [Console]::Error.WriteLine('not translated: none - full coverage')
    }
    else {
        [Console]::Error.WriteLine('')
        [Console]::Error.WriteLine('not translated:')
        foreach ($kind in $Report.NotTranslatedKinds) {
            [Console]::Error.WriteLine(('  {0,-30} {1,3}' -f $kind.RawKind, $kind.Count))
        }
        [Console]::Error.WriteLine('  <-- add rules for these in src/lower.rs; the report lists their properties')
    }
}
