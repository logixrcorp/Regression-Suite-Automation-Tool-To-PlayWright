<#
.SYNOPSIS
    Convert a Dynamics 365 Task Recorder / RSAT recording into a Playwright
    TypeScript spec.

.DESCRIPTION
    The third implementation of rsat2pw, alongside the Rust and C# builds. All
    three emit byte-identical output for the same recording, so a divergence
    between them is a build failure rather than a surprise months later.

    This one exists for environments where installing a toolchain - or running
    an unsigned binary - is the obstacle. It needs nothing that is not already
    on a Windows machine.

.PARAMETER Path
    The recording: an .axtr archive, or the Recording.xml RSAT extracts.

.PARAMETER OutDir
    Where the .spec.ts and .data.ts land. Defaults to 'tests'.

.PARAMETER ParamsPath
    RSAT parameter workbook supplying test data.

.PARAMETER Sheet
    Worksheet within that workbook. Defaults to the first.

.PARAMETER OnUnsupported
    What an unmapped action becomes: annotate (default), fail, or comment.

.PARAMETER Report
    Conversion report path. A .json path emits JSON.

.PARAMETER NoReport
    Skip the conversion report.

.PARAMETER DryRun
    Print what would be generated without writing anything.

.EXAMPLE
    .\rsat2pw.ps1 -Path .\ConfirmPurchaseOrder.axtr -OutDir ..\tests
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string] $Path,

    [Alias('o')]
    [string] $OutDir = 'tests',

    [Alias('p')]
    [string] $ParamsPath,

    [string] $Sheet,

    [ValidateSet('annotate', 'fail', 'comment')]
    [string] $OnUnsupported = 'annotate',

    [string] $Report,

    [switch] $NoReport,

    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'src/Paths.ps1')
. (Join-Path $here 'src/Xml.ps1')
. (Join-Path $here 'src/Ir.ps1')
. (Join-Path $here 'src/Recording.ps1')
. (Join-Path $here 'src/Lower.ps1')
. (Join-Path $here 'src/Xlsx.ps1')
. (Join-Path $here 'src/Params.ps1')
. (Join-Path $here 'src/Codegen.ps1')
. (Join-Path $here 'src/Report.ps1')

function Write-GeneratedFile {
    <#
        .SYNOPSIS
        Write UTF-8 without a BOM and with LF endings.

        .DESCRIPTION
        Set-Content and Out-File would add CRLF, and on Windows PowerShell a
        BOM as well - either of which makes this implementation's output differ
        from the other two on the very first byte.
    #>
    param([Parameter(Mandatory)] [string] $FilePath, [Parameter(Mandatory)] [AllowEmptyString()] [string] $Text)

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($FilePath, $Text, $encoding)
}

try {
    # PowerShell's location and .NET's working directory are different things,
    # so the paths written to go through Resolve-FullPath first. The paths read
    # from are resolved inside the functions that open them, and deliberately
    # left as typed here: the workbook path is echoed into the generated data
    # module's source line, and the other two implementations print it exactly
    # as it was given.
    $OutDir = Resolve-FullPath -Path $OutDir
    if (-not [string]::IsNullOrEmpty($Report)) { $Report = Resolve-FullPath -Path $Report }

    $recording = Import-Recording -Path $Path
    $testCase = ConvertTo-IrTestCase -Recording $recording

    if ([string]::IsNullOrEmpty($ParamsPath)) {
        $cases = Get-CasesFromRecording -TestCase $testCase
    }
    else {
        $cases = Get-CasesFromWorkbook -Path $ParamsPath -Sheet $Sheet -TestCase $testCase
    }

    # Always emit at least one case, or the generated `for` loop runs zero
    # times and the suite silently passes with no tests.
    if ($cases.Rows.Count -eq 0) { $cases.Rows.Add((New-CaseRow -Label 'default')) }

    $output = ConvertTo-PlaywrightSpec -TestCase $testCase -Cases $cases -OnUnsupported $OnUnsupported
    $reportData = New-ConversionReport -TestCase $testCase -Cases $cases

    Write-ConversionSummary -Report $reportData

    if ($DryRun) {
        Write-Information 'dry run   : nothing written' -InformationAction Continue
        exit 0
    }

    if (-not (Test-Path -LiteralPath $OutDir)) {
        [void] (New-Item -ItemType Directory -Path $OutDir -Force)
    }

    $specPath = Join-Path $OutDir "$($output.Stem).spec.ts"
    $dataPath = Join-Path $OutDir "$($output.Stem).data.ts"

    Write-GeneratedFile -FilePath $specPath -Text $output.Spec
    Write-GeneratedFile -FilePath $dataPath -Text $output.Data

    Write-Information "wrote     : $specPath" -InformationAction Continue
    Write-Information "wrote     : $dataPath" -InformationAction Continue

    if (-not $NoReport) {
        $reportPath = $Report
        if ([string]::IsNullOrEmpty($reportPath)) {
            $reportPath = Join-Path $OutDir "$($output.Stem).report.md"
        }

        if ($reportPath.ToLowerInvariant().EndsWith('.json')) {
            Write-GeneratedFile -FilePath $reportPath -Text (ConvertTo-ReportJson -Report $reportData)
        }
        else {
            Write-GeneratedFile -FilePath $reportPath -Text (ConvertTo-ReportMarkdown -Report $reportData)
        }

        Write-Information "wrote     : $reportPath" -InformationAction Continue
    }

    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
