# The normalized action IR, and the escaping every emitted string goes through.
# Mirrors src/ir.rs and Ir.cs.
#
# An action is a PSCustomObject carrying an `Op` discriminator and whatever
# fields that op needs - the closest PowerShell gets to the tagged union the
# other two ports use. `Op` is the name the runtime method is called by, so the
# report and the generated code share one vocabulary.

Set-StrictMode -Version Latest

function New-IrValue {
    <#
        .SYNOPSIS
        A literal, or a reference to a test-data field.
    #>
    param(
        [ValidateSet('literal', 'variable')]
        [string] $Kind,
        [AllowEmptyString()]
        [string] $Text
    )

    return [pscustomobject]@{ Kind = $Kind; Text = $Text }
}

function ConvertTo-TsExpression {
    <#
        .SYNOPSIS
        Render a value as the TypeScript expression the spec will contain.
    #>
    param([Parameter(Mandatory)] $Value)

    if ($Value.Kind -eq 'variable') { return "params.$($Value.Text)" }
    return "'" + (ConvertTo-EscapedTs $Value.Text) + "'"
}

function ConvertTo-EscapedTs {
    <#
        .SYNOPSIS
        Escape for a single-quoted TypeScript string literal.

        .DESCRIPTION
        `\r` matters as much as `\n`: TypeScript treats a bare carriage return
        as a line terminator, so a CRLF that survived XML parsing would emit an
        unterminated string.
    #>
    param([AllowEmptyString()] [string] $Text)

    if ($null -eq $Text) { return '' }

    return $Text.
        Replace('\', '\\').
        Replace("'", "\'").
        Replace("`r", '\r').
        Replace("`n", '\n')
}

function ConvertTo-EscapedTemplateLiteral {
    <#
        .SYNOPSIS
        Escape for a backtick template literal, where `${` starts an
        interpolation and a backtick ends the string. Recording and step names
        are author-supplied, so neither can be trusted to be inert here.
    #>
    param([AllowEmptyString()] [string] $Text)

    if ($null -eq $Text) { return '' }

    return $Text.
        Replace('\', '\\').
        Replace([string][char]0x60, '\' + [string][char]0x60).
        Replace('${', '\${').
        Replace("`r", '\r').
        Replace("`n", '\n')
}

function ConvertTo-CommentSafe {
    <#
        .SYNOPSIS
        Flatten to a single line so it cannot break out of a `//` comment.
    #>
    param([AllowEmptyString()] [string] $Text)

    if ($null -eq $Text) { return '' }
    return $Text.Replace("`r", ' ').Replace("`n", ' ')
}

function New-IrAction {
    <#
        .SYNOPSIS
        Build an action. `Op` is the runtime method it will be emitted as.
    #>
    param(
        [Parameter(Mandatory)] [string] $Op,
        [hashtable] $Fields = @{}
    )

    $action = [ordered]@{ Op = $Op }
    foreach ($key in $Fields.Keys) { $action[$key] = $Fields[$key] }

    # Grouping ops always carry a child list, so callers never have to test.
    if (($Op -eq 'test.step' -or $Op -eq 'withForm') -and -not $action.Contains('Children')) {
        $action['Children'] = [System.Collections.Generic.List[object]]::new()
    }

    return [pscustomobject] $action
}

function Get-IrChildren {
    <#
        .SYNOPSIS
        The child actions of a grouping action, or an empty list.

        .DESCRIPTION
        Both returns use the comma operator, because PowerShell unrolls a
        returned collection and an empty one would arrive at the caller as
        $null - which every recursive walk would then have to guard against.
    #>
    param([Parameter(Mandatory)] $Action)

    if ($Action.PSObject.Properties.Name -contains 'Children') { return ,$Action.Children }
    return ,([System.Collections.Generic.List[object]]::new())
}

function Get-IrSummary {
    <#
        .SYNOPSIS
        A short human-readable rendering, using the same `params.X` /
        `'literal'` forms the generated spec uses.
    #>
    param([Parameter(Mandatory)] $Action)

    switch ($Action.Op) {
        'navigate'       { return "$($Action.MenuItem) ($($Action.Kind))" }
        'withForm'       { return $Action.FormName }
        'test.step'      { return $Action.Label }
        'click'          { return "$($Action.Control) ($($Action.ControlType))" }
        'tab'            { return $Action.Control }
        'setField'       { return "$($Action.Control) = $(ConvertTo-TsExpression $Action.Value)" }
        'setGridCell'    { return "$($Action.Grid)[$($Action.Row)].$($Action.Column) = $(ConvertTo-TsExpression $Action.Value)" }
        'openLookup'     { return $Action.Control }
        'commitLookup'   { return $Action.Control }
        'selectRow'      { return "$($Action.Grid)[$($Action.Row)]" }
        'markRow'        { return $Action.Grid }
        'openRow'        { return $Action.Grid }
        'filter'         { return "$($Action.Field) $($Action.Operator) $(ConvertTo-TsExpression $Action.Value)" }
        'selectTreeItem' { return "$($Action.Control) <- $(ConvertTo-TsExpression $Action.Path)" }
        'expandTreeItem' { return "$($Action.Control) <- $(ConvertTo-TsExpression $Action.Path)" }
        'shortcut'       { return $Action.Name }
        'resetFilters'   { return $Action.Control }
        'closeForm'      { return '' }
        'expectValue'    { return "$($Action.Control) == $(ConvertTo-TsExpression $Action.Expected)" }
        'marker'         { return $Action.Text }
        'skipped'        {
            if ([string]::IsNullOrEmpty($Action.Detail)) { return $Action.RawKind }
            return "$($Action.RawKind) ($($Action.Detail))"
        }
        'unsupported'    { return $Action.RawKind }
        default          { throw "unreachable op '$($Action.Op)'" }
    }

    throw "unreachable op '$($Action.Op)'"
}

function Measure-IrActions {
    <#
        .SYNOPSIS
        Total actions, counting a grouping action and everything inside it.
    #>
    param([Parameter(Mandatory)] [AllowNull()] [AllowEmptyCollection()] $Actions)

    $total = 0
    foreach ($action in $Actions) {
        $total += 1 + (Measure-IrActions (Get-IrChildren $action))
    }
    return $total
}

function Measure-IrMatching {
    param(
        [Parameter(Mandatory)] [AllowNull()] [AllowEmptyCollection()] $Actions,
        [Parameter(Mandatory)] [string] $Op
    )

    $total = 0
    foreach ($action in $Actions) {
        if ($action.Op -eq $Op) { $total += 1 }
        $total += Measure-IrMatching (Get-IrChildren $action) $Op
    }
    return $total
}

function New-IrVariable {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [AllowEmptyString()] [string] $Default
    )

    return [pscustomobject]@{ Name = $Name; Default = $Default }
}

function New-IrTestCase {
    param(
        [Parameter(Mandatory)] [string] $Name,
        $Variables,
        $Actions
    )

    return [pscustomobject]@{
        Name      = $Name
        Variables = $Variables
        Actions   = $Actions
    }
}
