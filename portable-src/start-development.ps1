[CmdletBinding()]
param(
    [string]$ExePath,
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
# Resolve script-relative defaults after parameter binding for PowerShell -File.
if (-not $PSBoundParameters.ContainsKey('ExePath')) {
    $ExePath = Join-Path $workspace '油管下载器便携版\油管下载器.exe'
}
. (Join-Path $PSScriptRoot 'dev-environment.ps1')
$executable = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath))
if (-not $executable.StartsWith($workspace.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The development executable must be inside the project directory.'
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Build the application first, or supply -ExePath: $executable"
}
$runtimeTemp = Get-ProjectTemporaryDirectory -ProjectRoot $workspace -DirectoryName 'runtime-tmp'
$startInfo = New-Object Diagnostics.ProcessStartInfo
$startInfo.FileName = $executable
$startInfo.WorkingDirectory = Split-Path $executable -Parent
$startInfo.UseShellExecute = $false
foreach ($name in @('TEMP', 'TMP', 'TMPDIR', 'SystemTemp')) {
    $startInfo.EnvironmentVariables[$name] = $runtimeTemp
}
$process = [Diagnostics.Process]::Start($startInfo)
try {
    Write-Output "Development process $($process.Id); temporary directory: $runtimeTemp"
    if ($Wait) {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Development process failed with exit code $($process.ExitCode)." }
    }
}
finally { $process.Dispose() }
