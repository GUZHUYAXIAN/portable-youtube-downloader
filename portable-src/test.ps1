[CmdletBinding()]
param([string]$NodePath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
# Resolve script-relative defaults after parameter binding for PowerShell -File.
if (-not $PSBoundParameters.ContainsKey('NodePath')) {
    $NodePath = Join-Path $PSScriptRoot 'vendor\node-v24.13.0\node.exe'
}
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'dev-environment.ps1')
Invoke-ProjectTempEnvironment -ProjectRoot $workspace -Action {
    if (-not (Test-Path -LiteralPath $NodePath -PathType Leaf)) { throw "Missing Node executable: $NodePath" }
    $buildDir = Join-Path $PSScriptRoot 'test-output'
    New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $testEnvironment = Join-Path $PSScriptRoot 'tests\TestEnvironment.cs'
    & $compiler /nologo /target:exe /codepage:65001 /out:"$buildDir\CoreTests.exe" "$PSScriptRoot\Core.cs" "$PSScriptRoot\tests\CoreTests.cs" $testEnvironment
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
    & "$buildDir\CoreTests.exe" $NodePath
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    & $compiler /nologo /target:exe /codepage:65001 /out:"$buildDir\RunnerTests.exe" "$PSScriptRoot\Core.cs" "$PSScriptRoot\ProcessRunner.cs" "$PSScriptRoot\tests\RunnerTests.cs" $testEnvironment
    if ($LASTEXITCODE -ne 0) { throw 'Runner test compilation failed' }
    & "$buildDir\RunnerTests.exe" $NodePath
    if ($LASTEXITCODE -ne 0) { throw 'Runner tests failed' }
    & $compiler /nologo /target:exe /codepage:65001 /out:"$buildDir\SessionTests.exe" "$PSScriptRoot\Core.cs" "$PSScriptRoot\ProcessRunner.cs" "$PSScriptRoot\DownloadSession.cs" "$PSScriptRoot\tests\SessionTests.cs" $testEnvironment
    if ($LASTEXITCODE -ne 0) { throw 'Session test compilation failed' }
    & "$buildDir\SessionTests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Session tests failed' }
    & $compiler /nologo /target:exe /codepage:65001 /out:"$buildDir\UiMessagesTests.exe" "$PSScriptRoot\UiMessages.cs" "$PSScriptRoot\tests\UiMessagesTests.cs" $testEnvironment
    if ($LASTEXITCODE -ne 0) { throw 'UI messages test compilation failed' }
    & "$buildDir\UiMessagesTests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'UI messages tests failed' }
}
