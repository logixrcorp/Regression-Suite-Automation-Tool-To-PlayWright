<#
.SYNOPSIS
    The PowerShell port's test suite, including the parity check that pins its
    output to the Rust build byte for byte.

.DESCRIPTION
    Deliberately dependency-free. Pester's versions differ enough between
    Windows PowerShell 5.1 and PowerShell 7 that requiring it would be a
    bigger liability than a forty-line assertion helper, and the whole point
    of this implementation is that it runs on a machine with nothing
    installed.

    Exits non-zero if anything fails, so CI treats it like any other suite.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $here)

. (Join-Path $here '../src/Xml.ps1')
. (Join-Path $here '../src/Ir.ps1')
. (Join-Path $here '../src/Recording.ps1')
. (Join-Path $here '../src/Lower.ps1')
. (Join-Path $here '../src/Xlsx.ps1')
. (Join-Path $here '../src/Params.ps1')
. (Join-Path $here '../src/Codegen.ps1')
. (Join-Path $here '../src/Report.ps1')

$script:Passed = 0
$script:Failed = 0

function Test-Case {
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [scriptblock] $Body)

    try {
        & $Body
        $script:Passed += 1
        Write-Host "  PASS  $Name"
    }
    catch {
        $script:Failed += 1
        Write-Host "  FAIL  $Name"
        Write-Host "        $($_.Exception.Message)"
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string] $Because = '')

    if ($Expected -is [string] -or $Actual -is [string]) {
        if (-not [string]::Equals([string] $Expected, [string] $Actual, [System.StringComparison]::Ordinal)) {
            throw "expected '$Expected', got '$Actual'. $Because"
        }
        return
    }

    if ($Expected -ne $Actual) { throw "expected '$Expected', got '$Actual'. $Because" }
}

function Assert-True {
    param([bool] $Condition, [string] $Because = '')
    if (-not $Condition) { throw "expected true. $Because" }
}

function Get-FixtureCase {
    param([string] $Name = 'ConfirmPurchaseOrder.xml')
    return (ConvertTo-IrTestCase -Recording (Import-Recording -Path (Join-Path $root "fixtures/$Name")))
}

function Test-BytesEqual {
    param([string] $Left, [string] $Right)

    $a = [System.IO.File]::ReadAllBytes($Left)
    $b = [System.IO.File]::ReadAllBytes($Right)

    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { return $false } }
    return $true
}

Write-Host 'parity with the Rust build'

# The committed goldens are the Rust build's output. If these fail, the three
# implementations have diverged and one of them is wrong.
Test-Case 'spec, data and report match the goldens byte for byte' {
    $outDir = Join-Path ([System.IO.Path]::GetTempPath()) "rsat2pw-parity-$([System.Guid]::NewGuid().ToString('N'))"
    [void] (New-Item -ItemType Directory -Path $outDir -Force)

    try {
        Push-Location $root
        try {
            & (Join-Path $root 'powershell/rsat2pw.ps1') `
                -Path 'fixtures/ConfirmPurchaseOrder.axtr' `
                -OutDir $outDir `
                -ParamsPath 'fixtures/ConfirmPurchaseOrder-params.xlsx' 2>$null | Out-Null
        }
        finally { Pop-Location }

        foreach ($file in @('ConfirmPurchaseOrder.spec.ts', 'ConfirmPurchaseOrder.data.ts', 'ConfirmPurchaseOrder.report.md')) {
            Assert-True (Test-BytesEqual (Join-Path $root "tests/$file") (Join-Path $outDir $file)) "$file differs from the Rust output"
        }
    }
    finally { Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue }
}

Test-Case 'the generated file name matches the Rust output' {
    $case = Get-FixtureCase
    $output = ConvertTo-PlaywrightSpec -TestCase $case -Cases (Get-CasesFromRecording -TestCase $case)
    Assert-Equal 'ConfirmPurchaseOrder' $output.Stem
}

Write-Host 'the mapping table'

Test-Case 'a click command targets the control, not the verb' {
    $rec = ConvertFrom-RecordingText -XmlText @'
<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><Name>T</Name><RootScope><Children>
  <Node i:type="CommandUserAction"><CommandName>Click</CommandName>
    <ControlName>PurchCopyJournalHeader</ControlName><ControlType>MenuItemButton</ControlType></Node>
</Children></RootScope></Recording>
'@
    $action = (ConvertTo-IrTestCase -Recording $rec).Actions[0]
    Assert-Equal 'click' $action.Op
    Assert-Equal 'PurchCopyJournalHeader' $action.Control
    Assert-Equal 'MenuItemButton' $action.ControlType
}

Test-Case 'a filter command unpacks its JSON argument' {
    $rec = ConvertFrom-RecordingText -XmlText @'
<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><Name>T</Name><RootScope><Children>
  <Node i:type="CommandUserAction">
    <Arguments><CommandArgument><Value>[{"Capability":{"FieldLabel":"Purchase order","FieldName":""},"FieldName":"PurchId","Operator":"Is","Values":["003643"]}]</Value></CommandArgument></Arguments>
    <CommandName>ApplyFiltersForTaskRecorder</CommandName>
    <ControlName>SystemDefinedFilterManager</ControlName><ControlType>FilterManager</ControlType></Node>
</Children></RootScope></Recording>
'@
    $case = ConvertTo-IrTestCase -Recording $rec
    $action = $case.Actions[0]

    Assert-Equal 'filter' $action.Op
    Assert-Equal 'PurchId' $action.Field
    Assert-Equal 'Purchase order' $action.Label
    Assert-Equal 'Is' $action.Operator
    Assert-Equal '003643' $case.Variables[0].Default
}

Test-Case 'the recorder pane is not treated as a form' {
    $rec = ConvertFrom-RecordingText -XmlText @'
<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><Name>T</Name><RootScope><Children>
  <Node i:type="Scope"><IsForm>true</IsForm><IsStepGroup>false</IsStepGroup>
    <Name>SysBPMPane</Name><ScopeType>Public</ScopeType>
    <Children>
      <Node i:type="CommandUserAction"><CommandName>TabShown</CommandName>
        <ControlName>PurchOrder</ControlName><ControlType>AppBarTab</ControlType></Node>
    </Children></Node>
</Children></RootScope></Recording>
'@
    $actions = (ConvertTo-IrTestCase -Recording $rec).Actions
    Assert-Equal 1 $actions.Count
    Assert-Equal 'tab' $actions[0].Op
}

Test-Case 'a private step group is the client''s own and gets flattened' {
    $rec = ConvertFrom-RecordingText -XmlText @'
<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><Name>T</Name><RootScope><Children>
  <Node i:type="Scope"><IsForm>false</IsForm><IsStepGroup>true</IsStepGroup>
    <Name>CompanyLookup_RequestPopup</Name><ScopeType>Private</ScopeType>
    <Children>
      <Node i:type="CommandUserAction"><CommandName>RequestPopup</CommandName>
        <ControlName>CompanyLookup</ControlName><ControlType>Input</ControlType></Node>
    </Children></Node>
</Children></RootScope></Recording>
'@
    $actions = (ConvertTo-IrTestCase -Recording $rec).Actions
    Assert-Equal 1 $actions.Count
    Assert-Equal 'openLookup' $actions[0].Op
}

Test-Case 'unknown commands are reported by their verb' {
    $rec = ConvertFrom-RecordingText -XmlText @'
<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><Name>T</Name><RootScope><Children>
  <Node i:type="CommandUserAction"><CommandName>SelectForAdd</CommandName>
    <ControlName>Grid</ControlName><ControlType>Grid</ControlType></Node>
</Children></RootScope></Recording>
'@
    $action = (ConvertTo-IrTestCase -Recording $rec).Actions[0]
    Assert-Equal 'unsupported' $action.Op
    Assert-Equal 'CommandUserAction:SelectForAdd' $action.RawKind
    Assert-Equal 'Grid' $action.Props['ControlType']
}

Write-Host 'the parser'

Test-Case 'UserActions is not read as a second action list' {
    $rec = ConvertFrom-RecordingText -XmlText @'
<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><Name>T</Name>
  <RootScope><Children>
    <Node z:Id="i2" i:type="CommandUserAction" xmlns:z="http://schemas.microsoft.com/2003/10/Serialization/">
      <CommandName>Click</CommandName><ControlName>Ok</ControlName><ControlType>CommandButton</ControlType></Node>
  </Children></RootScope>
  <UserActions xmlns:d2p1="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
    <d2p1:anyType z:Ref="i2" xmlns:z="http://schemas.microsoft.com/2003/10/Serialization/" />
  </UserActions></Recording>
'@
    Assert-Equal 1 $rec.Nodes.Count 'the z:Ref pointer list must not become a node'
}

Test-Case 'command arguments stay out of the property bag' {
    $rec = ConvertFrom-RecordingText -XmlText @'
<Recording xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><Name>T</Name><RootScope><Children>
  <Node i:type="CommandUserAction">
    <Arguments><CommandArgument><Value>[{"FieldName":"PurchId"}]</Value></CommandArgument></Arguments>
    <CommandName>Click</CommandName><ControlName>Ok</ControlName></Node>
</Children></RootScope></Recording>
'@
    $node = $rec.Nodes[0]
    Assert-True (-not (Test-RecProp -Node $node -Names @('Value'))) 'a command argument is not a property called Value'
    Assert-Equal '[{"FieldName":"PurchId"}]' (Get-RecArg -Node $node -Index 0)
}

Write-Host 'identifiers and casing'

Test-Case 'sanitize produces valid identifiers' {
    Assert-Equal 'Customer_account' (ConvertTo-SafeIdent 'Customer account')
    Assert-Equal '_9lives' (ConvertTo-SafeIdent '9lives')
    Assert-Equal '_' (ConvertTo-SafeIdent '')
    Assert-Equal 'a_b_c' (ConvertTo-SafeIdent 'a-b.c')
}

Test-Case 'PascalCase matches the Rust build' {
    Assert-Equal 'CreateCustomer' (ConvertTo-PascalCase 'Create customer')
    Assert-Equal 'CustTableListPage' (ConvertTo-PascalCase 'CustTableListPage')
    Assert-Equal 'D365ToInnovaAddLineToPo9304Base' (ConvertTo-PascalCase 'D365_to_Innova_Add_Line_to_PO_9304_Base')
    Assert-Equal 'XmlHttpRequest' (ConvertTo-PascalCase 'XMLHttpRequest')
    Assert-Equal 'AddLineToPo' (ConvertTo-PascalCase 'Add Line to PO')
    Assert-Equal 'Foo2bar' (ConvertTo-PascalCase 'foo2bar')
    Assert-Equal 'Po9304' (ConvertTo-PascalCase 'PO_9304')
}

Write-Host 'parameter workbooks'

Test-Case 'an RSAT parameter workbook is refused rather than misread' {
    $case = Get-FixtureCase
    $book = Join-Path $root 'fixtures/RsatV2-params.xlsx'

    $message = ''
    try { [void] (Get-CasesFromWorkbook -Path $book -Sheet $null -TestCase $case) }
    catch { $message = $_.Exception.Message }

    Assert-True ($message -like '*RSAT parameter workbook*') "expected a refusal, got '$message'"
    Assert-True ($message -like '*TestCaseSteps*') 'the refusal should name the sheets it saw'

    # --sheet is the escape hatch, and it still works.
    $cases = Get-CasesFromWorkbook -Path $book -Sheet 'General' -TestCase $case
    Assert-True ($cases.Rows.Count -gt 0) 'the escape hatch should still read the sheet'
}

Test-Case 'a workbook with no data rows falls back to recorded values' {
    $case = Get-FixtureCase
    $cases = Get-CasesFromWorkbook -Path (Join-Path $root 'fixtures/EmptyTemplate-params.xlsx') -Sheet $null -TestCase $case

    Assert-Equal 1 $cases.Rows.Count
    Assert-Equal '9/30/2026' $cases.Rows[0].Values['PurchTable_DeliveryDate']
    Assert-True ($cases.Source -like '*no data rows*') 'the substitution must be visible in the source line'
}

Write-Host ''
Write-Host "powershell: $script:Passed passed, $script:Failed failed"

if ($script:Failed -gt 0) { exit 1 }
exit 0
