# Turning a PowerShell path into one a .NET API will agree with.
#
# PowerShell keeps its own current location, and it is not the process working
# directory that System.IO resolves relative paths against. In a shell started
# from the user profile they differ from the first command, so:
#
#     Set-Location C:\Projects\RSATTests
#     New-Item -ItemType Directory .\Out          # C:\Projects\RSATTests\Out
#     [System.IO.File]::WriteAllText('.\Out\x')   # C:\Users\<you>\Out\x
#
# The directory is created in one place and the file written to another, and
# the error names a path the user never typed. Every path that reaches a .NET
# file API goes through here first.

Set-StrictMode -Version Latest

function Resolve-FullPath {
    <#
        .SYNOPSIS
        Resolve a path the way PowerShell means it, whether or not it exists.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Path)

    if ([string]::IsNullOrEmpty($Path)) { return $Path }

    # GetUnresolvedProviderPathFromPSPath resolves against the *session's*
    # location and does not require the target to exist yet, which is what an
    # output path needs.
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}
