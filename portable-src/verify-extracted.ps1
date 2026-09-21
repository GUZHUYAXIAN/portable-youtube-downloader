param(
    [string]$Destination
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
if (-not $PSBoundParameters.ContainsKey('Destination')) {
    $Destination = Join-Path $workspace ('迁移验收 中文 空格 ' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path (Join-Path $workspace 'acceptance') -Force | Out-Null
$zip = Join-Path $workspace '油管下载器便携版.zip'
if (Test-Path -LiteralPath $Destination) { throw '验收目录已经存在；请指定新的目录，避免覆盖已有文件。' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $Destination)
$appRoot = Join-Path $Destination '油管下载器便携版'
foreach ($line in Get-Content -LiteralPath (Join-Path $appRoot 'CHECKSUMS.sha256') -Encoding UTF8) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw '文件校验清单格式错误' }
    $expected = $Matches[1]
    $file = Join-Path $appRoot $Matches[2]
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) { throw "解压校验不通过：$file" }
}
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = Join-Path $appRoot '油管下载器.exe'
$info.WorkingDirectory = $env:WINDIR
$info.UseShellExecute = $false
# Only this new process receives the constrained environment. Windows settings are untouched.
$info.EnvironmentVariables['PATH'] = (Join-Path $env:WINDIR 'System32') + ';' + $env:WINDIR
foreach ($name in @('NODE_OPTIONS','NODE_PATH','PYTHONPATH','PYTHONHOME')) { $info.EnvironmentVariables.Remove($name) }
$process = [Diagnostics.Process]::Start($info)
$record = [ordered]@{
    LaunchedAt = (Get-Date).ToString('o')
    Zip = $zip
    ZipSHA256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    AppRoot = $appRoot
    Exe = $info.FileName
    ProcessId = $process.Id
    WorkingDirectory = $info.WorkingDirectory
    ProcessPATH = $info.EnvironmentVariables['PATH']
    ExtractedChecksumsVerified = $true
}
$record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace 'acceptance\extracted-launch.json') -Encoding UTF8
$record | ConvertTo-Json
