# Lowering: RecNode -> IR action. Mirrors src/lower.rs and Lower.cs.
#
# All the schema knowledge lives here, in one editable table. When a node does
# not match anything we emit an `unsupported` action rather than inventing
# behaviour - a converter that is 85% automatic plus honest gaps beats one that
# silently emits wrong code.
#
# The vocabulary is the one real exports use. There are five node kinds and,
# within CommandUserAction, a small set of command names. The important shape
# is that last one: the recorder puts the *verb* in CommandName and the
# *target* in ControlName, with ControlType saying what kind of control it is.
# Reading CommandName as the thing to click is the single easiest way to
# generate a suite that hunts for a button labelled "Click".

Set-StrictMode -Version Latest

$script:ControlKeys  = @('ControlName', 'Control', 'TargetControl', 'ControlId')
$script:ValueKeys    = @('Value', 'NewValue', 'Text', 'InputValue')
$script:VariableKeys = @('VariableName', 'Variable', 'ParameterName')
$script:LabelKeys    = @('Description', 'CustomDescription', 'Annotation', 'Name')

# Forms that belong to the recorder, not to the business process. Task Recorder
# runs inside the client it is recording, so its own pane shows up as a form
# scope wrapped around perfectly ordinary actions; keeping the scope would nest
# the whole recording inside a form that is not there on playback.
$script:RecorderInternalForms = @(
    'SysBPMPane',
    'SysTaskRecorderPane',
    'SysTaskRecorderForm',
    'SysTaskRecorderStartForm',
    'SysTaskRecorderStopForm'
)

# Identifiers the generated data module uses for its own bookkeeping, so a
# recorded variable must never be allowed to claim one.
$script:ReservedIdents = @('__case')

function ConvertTo-SafeIdent {
    <#
        .SYNOPSIS
        Turn a recorder variable name into a safe TypeScript identifier.
    #>
    param([AllowEmptyString()] [string] $Name)

    if ($null -eq $Name) { $Name = '' }

    $chars = $Name.ToCharArray()
    for ($i = 0; $i -lt $chars.Length; $i++) {
        $c = $chars[$i]
        $isAlnum = ($c -ge 'a' -and $c -le 'z') -or ($c -ge 'A' -and $c -le 'Z') -or ($c -ge '0' -and $c -le '9')
        if (-not $isAlnum) { $chars[$i] = '_' }
    }

    $result = [string]::new($chars)
    if ($result.Length -gt 0 -and $result[0] -ge '0' -and $result[0] -le '9') { $result = '_' + $result }
    if ($result.Length -eq 0) { $result = '_' }

    return $result
}

function Get-UniqueIdent {
    <#
        .SYNOPSIS
        Make `Candidate` unique against `Taken` (and the reserved names) by
        suffixing, rather than dropping the colliding field. Losing a workbook
        column silently is worse than emitting one nobody references.
    #>
    param(
        [Parameter(Mandatory)] [string] $Candidate,
        [Parameter(Mandatory)] [AllowEmptyCollection()] $Taken
    )

    $name = $Candidate
    $n = 2

    while ($script:ReservedIdents -ccontains $name -or $Taken -ccontains $name) {
        $name = "${Candidate}_$n"
        $n += 1
    }

    return $name
}

function New-LowerContext {
    <#
        .SYNOPSIS
        Collects the test-data fields as lowering walks the recording.

        .DESCRIPTION
        RSAT parameterizes *every* recorded input - that is what fills the
        columns of its parameter workbook - so each recorded value becomes a
        variable here too, defaulting to what the recorder captured.
    #>
    param([AllowEmptyCollection()] $Seed)

    $ctx = [pscustomobject]@{
        Variables = [System.Collections.Generic.List[object]]::new()
        Taken     = [System.Collections.Generic.List[string]]::new()
    }

    foreach ($entry in $Seed) {
        [void] (Add-CtxVariable -Ctx $ctx -Candidate (ConvertTo-SafeIdent $entry.Name) -Default $entry.Value)
    }

    return $ctx
}

function Add-CtxVariable {
    param(
        [Parameter(Mandatory)] $Ctx,
        [Parameter(Mandatory)] [string] $Candidate,
        [AllowEmptyString()] [string] $Default
    )

    $name = Get-UniqueIdent -Candidate $Candidate -Taken $Ctx.Taken
    $Ctx.Taken.Add($name)
    $Ctx.Variables.Add((New-IrVariable -Name $name -Default $Default))
    return $name
}

function Get-CtxNamedValue {
    <#
        .SYNOPSIS
        A variable the recording named explicitly. Repeated references to the
        same recorder variable must land on the same field, so this reuses.
    #>
    param(
        [Parameter(Mandatory)] $Ctx,
        [Parameter(Mandatory)] [string] $Raw,
        [AllowEmptyString()] [string] $Default
    )

    $candidate = ConvertTo-SafeIdent $Raw

    foreach ($variable in $Ctx.Variables) {
        if ($variable.Name -ceq $candidate) {
            return (New-IrValue -Kind 'variable' -Text $variable.Name)
        }
    }

    return (New-IrValue -Kind 'variable' -Text (Add-CtxVariable -Ctx $Ctx -Candidate $candidate -Default $Default))
}

function Get-CtxDerivedValue {
    <#
        .SYNOPSIS
        A variable derived from the control that was edited. Each recorded
        input gets its own field even when the same control is touched twice,
        because those are two steps with two values - which is how RSAT numbers
        its own columns.
    #>
    param(
        [Parameter(Mandatory)] $Ctx,
        [AllowEmptyString()] [string] $Base,
        [AllowEmptyString()] [string] $Default
    )

    if ([string]::IsNullOrEmpty($Base)) { $Base = 'value' }
    $candidate = ConvertTo-SafeIdent $Base

    return (New-IrValue -Kind 'variable' -Text (Add-CtxVariable -Ctx $Ctx -Candidate $candidate -Default $Default))
}

function ConvertTo-IrTestCase {
    <#
        .SYNOPSIS
        Lower a parsed recording into the action IR.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Recording)

    $ctx = New-LowerContext -Seed $Recording.Variables
    $actions = ConvertTo-IrActions -Nodes $Recording.Nodes -Ctx $ctx

    # A recording can reference a variable that never made it into a
    # declaration. Declaring it anyway keeps the generated data module and the
    # generated spec in agreement, so the output always compiles.
    $referenced = [System.Collections.Generic.List[string]]::new()
    Get-ReferencedVariables -Actions $actions -Result $referenced

    foreach ($name in $referenced) {
        $known = $false
        foreach ($variable in $ctx.Variables) {
            if ($variable.Name -ceq $name) { $known = $true; break }
        }
        if (-not $known) { $ctx.Variables.Add((New-IrVariable -Name $name -Default '')) }
    }

    return (New-IrTestCase -Name $Recording.Name -Variables $ctx.Variables -Actions $actions)
}

function Get-ReferencedVariables {
    param(
        [Parameter(Mandatory)] [AllowNull()] [AllowEmptyCollection()] $Actions,
        [Parameter(Mandatory)] $Result
    )

    foreach ($action in $Actions) {
        $value = $null
        switch ($action.Op) {
            'setField'    { $value = $action.Value }
            'setGridCell' { $value = $action.Value }
            'filter'      { $value = $action.Value }
            'expectValue' { $value = $action.Expected }
        }

        if ($null -ne $value -and $value.Kind -eq 'variable' -and -not ($Result -ccontains $value.Text)) {
            $Result.Add($value.Text)
        }

        Get-ReferencedVariables -Actions (Get-IrChildren $action) -Result $Result
    }
}

function ConvertTo-IrActions {
    param([Parameter(Mandatory)] [AllowNull()] [AllowEmptyCollection()] $Nodes, [Parameter(Mandatory)] $Ctx)

    $actions = [System.Collections.Generic.List[object]]::new()
    foreach ($node in $Nodes) {
        foreach ($action in (ConvertTo-IrActionList -Node $node -Ctx $Ctx)) { $actions.Add($action) }
    }

    return ,$actions
}

function ConvertTo-IrActionList {
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] $Ctx)

    switch ($Node.Kind.ToLowerInvariant()) {
        'scope'               { return (ConvertTo-IrScope -Node $Node -Ctx $Ctx) }
        'menuitemuseraction'  { return @((ConvertTo-IrMenuItem -Node $Node)) }
        'commanduseraction'   { return @((ConvertTo-IrCommand -Node $Node -Ctx $Ctx)) }
        'propertyuseraction'  { return @((ConvertTo-IrProperty -Node $Node -Ctx $Ctx)) }
        'taskuseraction'      { return @((ConvertTo-IrTaskMarker -Node $Node)) }
        # A note the recorder was asked to keep, and a bare annotation. Both
        # are commentary on the recording rather than something to replay.
        'infouseraction'       { return @((ConvertTo-IrNote -Node $Node)) }
        'annotationuseraction' { return @((ConvertTo-IrNote -Node $Node)) }
    }

    # Scopes carry their kind in the property bag as well as in `i:type`, and
    # older exports spelled the grouping node differently.
    if ((Test-RecProp -Node $Node -Names @('IsForm')) -or (Test-RecProp -Node $Node -Names @('IsStepGroup'))) {
        return (ConvertTo-IrScope -Node $Node -Ctx $Ctx)
    }
    if (Test-RecProp -Node $Node -Names @('MenuItemName')) {
        return @((ConvertTo-IrMenuItem -Node $Node))
    }

    return @((ConvertTo-IrLegacy -Node $Node -Ctx $Ctx))
}

function ConvertTo-IrScope {
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] $Ctx)

    $children = ConvertTo-IrActions -Nodes $Node.Children -Ctx $Ctx

    # An empty scope has nothing to wrap. Real recordings are full of them: the
    # client re-enters a form scope every time focus returns to it.
    if ($children.Count -eq 0) { return @() }

    if (Test-RecFlag -Node $Node -Name 'IsForm') {
        $form = Get-RecProp -Node $Node -Names @('Name', 'FormName', 'RecordingName')
        if ($null -eq $form) { $form = '' }

        if ($form.Length -eq 0 -or (Test-RecorderInternalForm -Form $form)) { return ,$children }

        return @((New-IrAction -Op 'withForm' -Fields @{ FormName = $form; Children = $children }))
    }

    # A step group the user made while recording is public. The client marks
    # its own groupings the same way - every lookup it opens becomes a private
    # `<control>_RequestPopup` group - and those are plumbing, not intent.
    if (Test-RecFlag -Node $Node -Name 'IsStepGroup') {
        $scopeType = Get-RecProp -Node $Node -Names @('ScopeType')
        if ($null -ne $scopeType -and [string]::Equals($scopeType, 'Private', [System.StringComparison]::OrdinalIgnoreCase)) {
            return ,$children
        }

        return @((New-IrAction -Op 'test.step' -Fields @{ Label = (Get-ScopeLabel -Node $Node); Children = $children }))
    }

    # A private scope with neither flag is client plumbing.
    if ((Test-RecProp -Node $Node -Names @('IsForm')) -or (Test-RecProp -Node $Node -Names @('IsStepGroup'))) {
        return ,$children
    }

    # Older exports had no flags at all. Group only when the recorder gave the
    # scope a human label.
    $label = Get-RecProp -Node $Node -Names $script:LabelKeys
    if ($null -ne $label -and $label.Length -gt 0) {
        return @((New-IrAction -Op 'test.step' -Fields @{ Label = $label; Children = $children }))
    }

    return ,$children
}

function Get-ScopeLabel {
    param([Parameter(Mandatory)] $Node)

    $label = Get-RecProp -Node $Node -Names $script:LabelKeys
    if ($null -ne $label -and $label.Length -gt 0) { return $label }
    return 'Recorded step'
}

function Test-RecorderInternalForm {
    param([Parameter(Mandatory)] [string] $Form)

    foreach ($name in $script:RecorderInternalForms) {
        if ([string]::Equals($name, $Form, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function ConvertTo-IrMenuItem {
    param([Parameter(Mandatory)] $Node)

    $menuItem = Get-RecProp -Node $Node -Names @('MenuItemName', 'MenuItem', 'Name')
    if ($null -eq $menuItem) { $menuItem = '' }

    $rawKind = Get-RecProp -Node $Node -Names @('MenuItemType', 'MenuItemKind')
    if ($null -eq $rawKind) { $rawKind = 'Display' }

    switch ($rawKind.ToLowerInvariant()) {
        'action' { $kind = 'Action' }
        'output' { $kind = 'Output' }
        default  { $kind = 'Display' }
    }

    return (New-IrAction -Op 'navigate' -Fields @{ MenuItem = $menuItem; Kind = $kind })
}

function ConvertTo-IrCommand {
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] $Ctx)

    $command = Get-RecProp -Node $Node -Names @('CommandName', 'Command')
    if ($null -eq $command) { $command = '' }

    $control = Get-RecProp -Node $Node -Names $script:ControlKeys
    if ($null -eq $control) { $control = '' }

    $controlType = Get-RecProp -Node $Node -Names @('ControlType')
    if ($null -eq $controlType) { $controlType = '' }

    # A grid command names the list in `ListContext`; `ControlName` repeats it.
    $listContext = Get-RecProp -Node $Node -Names @('ListContext')
    $grid = $control
    if ($null -ne $listContext -and $listContext.Length -gt 0) { $grid = $listContext }

    switch ($command.ToLowerInvariant()) {
        'click' {
            if ($control.Length -gt 0) {
                return (New-IrAction -Op 'click' -Fields @{ Control = $control; ControlType = $controlType })
            }
        }
        'tabshown' {
            if ($control.Length -gt 0) { return (New-IrAction -Op 'tab' -Fields @{ Control = $control }) }
        }
        'requestpopup' {
            if ($control.Length -gt 0) { return (New-IrAction -Op 'openLookup' -Fields @{ Control = $control }) }
        }
        'resolvechanges' {
            if ($control.Length -gt 0) { return (New-IrAction -Op 'commitLookup' -Fields @{ Control = $control }) }
        }
        # Following a link rendered inside a field. The target is the control,
        # exactly as for an ordinary click.
        'executehyperlink' {
            if ($control.Length -gt 0) {
                return (New-IrAction -Op 'click' -Fields @{ Control = $control; ControlType = $controlType })
            }
        }
        'expandingpath' {
            if ($control.Length -gt 0) {
                $path = Get-CtxDerivedValue -Ctx $Ctx -Base $control -Default (Get-ArgOrEmpty -Node $Node -Index 0)
                return (New-IrAction -Op 'expandTreeItem' -Fields @{ Control = $control; Path = $path })
            }
        }
        'selectionpathchanged' {
            if ($control.Length -gt 0) {
                # The tree path is the value the user picked, so it is test
                # data like any other recorded input.
                $path = Get-CtxDerivedValue -Ctx $Ctx -Base $control -Default (Get-ArgOrEmpty -Node $Node -Index 0)
                return (New-IrAction -Op 'selectTreeItem' -Fields @{ Control = $control; Path = $path })
            }
        }
        'navigationaction' {
            if ($grid.Length -gt 0) { return (New-IrAction -Op 'openRow' -Fields @{ Grid = $grid }) }
        }
        'markactiverow' {
            if ($grid.Length -gt 0) { return (New-IrAction -Op 'markRow' -Fields @{ Grid = $grid }) }
        }
        # `ChangeSelectedIndex` is the same move without the cache suffix.
        'changeselectedindexincache' { if ($grid.Length -gt 0) { return (New-IrSelectRow -Node $Node -Grid $grid) } }
        'changeselectedindex'        { if ($grid.Length -gt 0) { return (New-IrSelectRow -Node $Node -Grid $grid) } }
        # `ApplyFilters` is the same command under an older name, and carries
        # the same JSON payload. If it ever does not, the filter rule finds no
        # field and says so rather than emitting a filter of nothing.
        'applyfiltersfortaskrecorder' { return (ConvertTo-IrFilter -Node $Node -Ctx $Ctx -Control $control) }
        'applyfilters'                { return (ConvertTo-IrFilter -Node $Node -Ctx $Ctx -Control $control) }
        'resetfilters'                { return (New-IrAction -Op 'resetFilters' -Fields @{ Control = $control }) }
        # Preparing the filter pane so a field can be filtered on. filter()
        # drives the column header directly and never needs the pane set up.
        'addafilterfield' {
            return (New-IrSkipped -Node $Node -Why 'prepares the filter pane; filter() does not use it')
        }
        # Opening the filter flyout. filter() does that itself as part of
        # applying one, so replaying this would just toggle the pane shut.
        'getfilters' {
            return (New-IrSkipped -Node $Node -Why 'opens the filter pane; filter() does that itself')
        }
        # The shortcut name is the whole instruction; without it there is
        # nothing to replay.
        'executeshortcuts' {
            $shortcut = Get-RecArg -Node $Node -Index 0
            if ($null -ne $shortcut -and $shortcut.Length -gt 0) {
                return (New-IrAction -Op 'shortcut' -Fields @{ Name = $shortcut })
            }
        }
        'requestclose' { return (New-IrAction -Op 'closeForm' -Fields @{}) }
    }

    return (New-IrUnsupported -Node $Node)
}

function New-IrSelectRow {
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] [string] $Grid)

    # The new cursor position is the first command argument.
    $row = 0
    $arg = Get-RecArg -Node $Node -Index 0
    if ($null -ne $arg) {
        $parsed = 0
        if ([int]::TryParse($arg.Trim(), [ref] $parsed)) { $row = $parsed }
    }

    return (New-IrAction -Op 'selectRow' -Fields @{ Grid = $Grid; Row = $row })
}

function Get-ArgOrEmpty {
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] [int] $Index)

    $value = Get-RecArg -Node $Node -Index $Index
    if ($null -eq $value) { return '' }
    return $value
}

function ConvertTo-IrFilter {
    <#
        .SYNOPSIS
        Unpack ApplyFiltersForTaskRecorder, whose first command argument is a
        JSON array describing the filter the user typed.
    #>
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] $Ctx, [AllowEmptyString()] [string] $Control)

    $json = Get-ArgOrEmpty -Node $Node -Index 0

    # `FieldName` appears twice: once inside the (often null) `Capability`
    # object, where it is blank, and once at the top level where it is real.
    $field = Get-JsonString -Json $json -Key 'FieldName'
    if ($null -eq $field) { $field = Get-JsonString -Json $json -Key 'FieldLabel' }
    if ($null -eq $field) { $field = '' }

    $label = Get-JsonString -Json $json -Key 'FieldLabel'
    if ($null -eq $label) { $label = '' }

    $operator = Get-JsonString -Json $json -Key 'Operator'
    if ($null -eq $operator) { $operator = '' }

    if ($field.Length -eq 0) { return (New-IrUnsupported -Node $Node) }

    $recorded = Get-JsonFirstArrayString -Json $json -Key 'Values'
    if ($null -eq $recorded) { $recorded = '' }

    $value = Get-CtxDerivedValue -Ctx $Ctx -Base $field -Default $recorded

    return (New-IrAction -Op 'filter' -Fields @{
        Control  = $Control
        Field    = $field
        Label    = $label
        Operator = $operator
        Value    = $value
    })
}

function ConvertTo-IrProperty {
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] $Ctx)

    $property = Get-RecProp -Node $Node -Names @('PropertyName')
    if ($null -eq $property) { $property = 'Value' }

    if (-not [string]::Equals($property, 'Value', [System.StringComparison]::OrdinalIgnoreCase)) {
        return (New-IrUnsupported -Node $Node)
    }

    $control = Get-RecProp -Node $Node -Names $script:ControlKeys
    if ($null -eq $control) { $control = '' }
    if ($control.Length -eq 0) { return (New-IrUnsupported -Node $Node) }

    $controlType = Get-RecProp -Node $Node -Names @('ControlType')
    if ($null -eq $controlType) { $controlType = '' }

    $value = Get-IrValueForNode -Node $Node -Ctx $Ctx -Base $control

    # A cell edit names its grid in `ListContext` and its row in `RowIndex`; a
    # plain field edit leaves both nil.
    $grid = Get-RecProp -Node $Node -Names @('ListContext', 'GridName', 'Grid')
    if ($null -eq $grid) { $grid = '' }

    $rowText = Get-RecProp -Node $Node -Names @('RowIndex', 'Row')
    $row = 0
    $hasRow = $false
    if ($null -ne $rowText) {
        $parsed = 0
        if ([int]::TryParse($rowText.Trim(), [ref] $parsed)) { $row = $parsed; $hasRow = $true }
    }

    if ($grid.Length -gt 0 -and $hasRow) {
        return (New-IrAction -Op 'setGridCell' -Fields @{
            Grid        = $grid
            Column      = $control
            Row         = $row
            ControlType = $controlType
            Value       = $value
        })
    }

    return (New-IrAction -Op 'setField' -Fields @{
        Control     = $control
        ControlType = $controlType
        Value       = $value
    })
}

function ConvertTo-IrTaskMarker {
    param([Parameter(Mandatory)] $Node)

    $label = Get-RecProp -Node $Node -Names @('Description', 'Name', 'Comment')
    if ($null -eq $label) { $label = 'Sub-task' }

    $phase = Get-RecProp -Node $Node -Names @('UserActionType')
    if ($null -ne $phase -and $phase.Length -gt 0) {
        return (New-IrAction -Op 'marker' -Fields @{ Text = "$label ($phase)" })
    }

    return (New-IrAction -Op 'marker' -Fields @{ Text = $label })
}

function ConvertTo-IrNote {
    <#
        .SYNOPSIS
        A recorded note or annotation. It carries no behaviour, so it is
        emitted as a comment - the recording said it for a reason, and dropping
        it loses the only thing the recorder was told in prose.
    #>
    param([Parameter(Mandatory)] $Node)

    $text = Get-RecProp -Node $Node -Names @('Notes', 'Text', 'Description', 'Comment')
    if ($null -eq $text) { $text = 'Note' }

    return (New-IrAction -Op 'marker' -Fields @{ Text = $text })
}

function ConvertTo-IrLegacy {
    <#
        .SYNOPSIS
        Shapes from older exports, kept because the parser is deliberately
        tolerant and a recording that predates the current schema should still
        convert.
    #>
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] $Ctx)

    $kind = $Node.Kind.ToLowerInvariant()

    $control = Get-RecProp -Node $Node -Names $script:ControlKeys
    if ($null -eq $control) { $control = '' }

    if ($kind.Contains('validat') -or $kind.Contains('verif') -or $kind.Contains('assert')) {
        $expected = Get-IrValueForNode -Node $Node -Ctx $Ctx -Base $control
        return (New-IrAction -Op 'expectValue' -Fields @{ Control = $control; Expected = $expected })
    }

    if ($control.Length -gt 0 -and ($kind.Contains('input') -or (Test-RecProp -Node $Node -Names $script:ValueKeys))) {
        $controlType = Get-RecProp -Node $Node -Names @('ControlType')
        if ($null -eq $controlType) { $controlType = '' }

        return (New-IrAction -Op 'setField' -Fields @{
            Control     = $control
            ControlType = $controlType
            Value       = (Get-IrValueForNode -Node $Node -Ctx $Ctx -Base $control)
        })
    }

    return (New-IrUnsupported -Node $Node)
}

function Get-IrValueForNode {
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] $Ctx, [AllowEmptyString()] [string] $Base)

    $recorded = Get-RecProp -Node $Node -Names $script:ValueKeys
    if ($null -eq $recorded) { $recorded = '' }

    $variable = Get-RecProp -Node $Node -Names $script:VariableKeys
    if ($null -ne $variable -and $variable.Length -gt 0) {
        return (Get-CtxNamedValue -Ctx $Ctx -Raw $variable -Default $recorded)
    }

    return (Get-CtxDerivedValue -Ctx $Ctx -Base $Base -Default $recorded)
}

function Get-IrRawKind {
    <#
        .SYNOPSIS
        How an unmapped node is named in the report. A bare CommandUserAction
        is useless as a worklist entry - every command is one - so commands are
        reported by the verb that has no rule yet.
    #>
    param([Parameter(Mandatory)] $Node)

    $command = Get-RecProp -Node $Node -Names @('CommandName', 'Command')
    if ($null -ne $command -and $command.Length -gt 0) { return "$($Node.Kind):$command" }
    return $Node.Kind
}

function New-IrUnsupported {
    param([Parameter(Mandatory)] $Node)

    return (New-IrAction -Op 'unsupported' -Fields @{
        RawKind = (Get-IrRawKind -Node $Node)
        Detail  = (Get-RecDescription -Node $Node)
        Props   = $Node.Props
    })
}

function New-IrSkipped {
    param([Parameter(Mandatory)] $Node, [Parameter(Mandatory)] [string] $Why)

    return (New-IrAction -Op 'skipped' -Fields @{
        RawKind = (Get-IrRawKind -Node $Node)
        Detail  = $Why
        Props   = $Node.Props
    })
}

# -- the JSON the recorder embeds in command arguments ------------------------
#
# Deliberately a scanner rather than a parser, and deliberately the same dumb
# one as in Rust and C#: the three implementations are held to byte-identical
# output, and that is cheapest to keep true when none of them is clever.

function Get-JsonString {
    <#
        .SYNOPSIS
        First non-empty `"key": "value"` in the blob. The filter payload
        carries `FieldName` twice - blank inside `Capability`, real at the top
        level - so "first non-empty" picks the one that matters.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Json, [Parameter(Mandatory)] [string] $Key)

    $needle = '"' + $Key + '"'
    $from = 0

    while ($true) {
        $at = $Json.IndexOf($needle, $from, [System.StringComparison]::Ordinal)
        if ($at -lt 0) { return $null }

        $after = $at + $needle.Length
        $from = $after

        $rest = $Json.Substring($after).TrimStart()
        if (-not $rest.StartsWith(':')) { continue }

        $rest = $rest.Substring(1).TrimStart()
        if (-not $rest.StartsWith('"')) { continue }

        $value = Read-JsonString -Rest $rest.Substring(1)
        if ($null -ne $value -and $value.Length -gt 0) { return $value }
    }
}

function Get-JsonFirstArrayString {
    <#
        .SYNOPSIS
        First string element of `"key": [ ... ]`.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Json, [Parameter(Mandatory)] [string] $Key)

    $needle = '"' + $Key + '"'
    $at = $Json.IndexOf($needle, [System.StringComparison]::Ordinal)
    if ($at -lt 0) { return $null }

    $rest = $Json.Substring($at + $needle.Length).TrimStart()
    if (-not $rest.StartsWith(':')) { return $null }

    $rest = $rest.Substring(1).TrimStart()
    if (-not $rest.StartsWith('[')) { return $null }

    $rest = $rest.Substring(1).TrimStart()
    if (-not $rest.StartsWith('"')) { return $null }

    return (Read-JsonString -Rest $rest.Substring(1))
}

function Read-JsonString {
    <#
        .SYNOPSIS
        Read up to the closing quote, honouring backslash escapes.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Rest)

    $builder = [System.Text.StringBuilder]::new()

    for ($i = 0; $i -lt $Rest.Length; $i++) {
        $c = $Rest[$i]

        if ($c -eq '"') { return $builder.ToString() }

        if ($c -eq '\') {
            $i += 1
            if ($i -ge $Rest.Length) { return $null }

            switch ($Rest[$i]) {
                'n'     { [void] $builder.Append("`n") }
                'r'     { [void] $builder.Append("`r") }
                't'     { [void] $builder.Append("`t") }
                default { [void] $builder.Append($Rest[$i]) }
            }
            continue
        }

        [void] $builder.Append($c)
    }

    return $null
}
