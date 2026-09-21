[CmdletBinding()]
param(
    [string]$YtDlpPath,
    [string]$FFmpegDirectory = '',
    [string]$NodePath,
    [switch]$CompileOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
# Resolve script-relative defaults after parameter binding for PowerShell -File.
if (-not $PSBoundParameters.ContainsKey('YtDlpPath')) {
    $YtDlpPath = Join-Path $PSScriptRoot 'vendor\yt-dlp-2026.08.19\yt-dlp.exe'
}
if (-not $PSBoundParameters.ContainsKey('NodePath')) {
    $NodePath = Join-Path $PSScriptRoot 'vendor\node-v24.13.0\node.exe'
}
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'dev-environment.ps1')
Invoke-ProjectTempEnvironment -ProjectRoot $workspace -Action {
if ([string]::IsNullOrWhiteSpace($FFmpegDirectory)) {
    $FFmpegDirectory = Join-Path $PSScriptRoot 'vendor\ffmpeg-8.1.1\bin'
    if (-not (Test-Path -LiteralPath $FFmpegDirectory -PathType Container)) {
        $FFmpegDirectory = Join-Path $workspace '.tools\ffmpeg\ffmpeg-8.1.1-essentials_build\bin'
    }
}
$releaseDirectory = [IO.Path]::GetFullPath((Join-Path $workspace '油管下载器便携版'))
$buildDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build-output'))
$assetDirectory = Join-Path $PSScriptRoot 'release-assets'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$utf8 = New-Object Text.UTF8Encoding($true)

function Assert-NoReparsePoint([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "拒绝通过链接或重解析点写入：$candidate"
            }
        }
        $parent = Split-Path $candidate -Parent
        if ($parent -eq $candidate) { break }
        $candidate = $parent
    }
}

function Ensure-Directory([string]$Path) {
    Assert-NoReparsePoint $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function Copy-ManagedFile([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "缺少文件：$Source" }
    Assert-NoReparsePoint $Destination
    Ensure-Directory (Split-Path $Destination -Parent)
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw '找不到系统 .NET Framework x64 C# 编译器，请在含 .NET Framework 4.8 的 Windows 上构建。'
}

# Exact inputs keep the bundled license records in sync with the executables.
$tools = @(
    @{ Name = 'yt-dlp.exe'; Source = $YtDlpPath; Hash = '66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A' },
    @{ Name = 'ffmpeg.exe'; Source = (Join-Path $FFmpegDirectory 'ffmpeg.exe'); Hash = '228D7A8556258DE907FDB55F36850078EBC7680B84EC30D84EA02E99BEC1D1EB' },
    @{ Name = 'ffprobe.exe'; Source = (Join-Path $FFmpegDirectory 'ffprobe.exe'); Hash = '0FDE260F5ABD35C9CAFD96F594CC76365A780C1B73A90E35B6A3409EA1DB1BF0' },
    @{ Name = 'node.exe'; Source = $NodePath; Hash = 'D14BA95CDCE1EF7DC9AD3AC74949CA5DB38B27378EE30F30A23CF26F9E875A11' }
)
foreach ($tool in $tools) {
    if (-not (Test-Path -LiteralPath $tool.Source -PathType Leaf)) { throw "缺少工具：$($tool.Source)" }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $tool.Source).Hash
    if ($actual -ne $tool.Hash) {
        throw "工具校验不匹配：$($tool.Name)。请核实版本及许可证后再更新构建脚本，不能静默替换。"
    }
}

$requiredSources = @('Core.cs', 'ProcessRunner.cs', 'MainForm.cs', 'Program.cs')
foreach ($name in $requiredSources) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)) { throw "源码尚未完成：$name" }
}
$documentNames = @('使用说明.txt', 'THIRD-PARTY-NOTICES.txt', 'VERSIONS-AND-SOURCES.txt')
$assets = @()
foreach ($name in $documentNames) {
    $path = Join-Path $assetDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少发行文档：$name" }
    $assets += Get-Item -LiteralPath $path
}
$licenseDirectory = Join-Path $assetDirectory 'licenses'
$licenseFiles = @(Get-ChildItem -LiteralPath $licenseDirectory -Recurse -File -Filter '*.txt')
if ($licenseFiles.Count -lt 5) { throw '第三方许可文件不完整。' }
$assets += $licenseFiles

Ensure-Directory $buildDirectory
$temporaryExe = Join-Path $buildDirectory ('download-' + [Guid]::NewGuid().ToString('N') + '.exe')
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
try {
    $compilerArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/langversion:5', '/codepage:65001', '/warn:4', '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')), ('/out:' + $temporaryExe)) + $sources
    & $compiler @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw "正式程序编译失败，退出码 $LASTEXITCODE" }

    if ($CompileOnly) {
        Write-Output '编译验证完成；临时 EXE 将清理，未更新发行目录。'
        return
    }

    Ensure-Directory $releaseDirectory
    Ensure-Directory (Join-Path $releaseDirectory 'tools')
    Ensure-Directory (Join-Path $releaseDirectory 'Downloads')
    Copy-ManagedFile $temporaryExe (Join-Path $releaseDirectory '油管下载器.exe')
    $relativePaths = @('油管下载器.exe')
    foreach ($tool in $tools) {
        $relative = 'tools\' + $tool.Name
        Copy-ManagedFile $tool.Source (Join-Path $releaseDirectory $relative)
        $relativePaths += $relative
    }
    foreach ($asset in $assets) {
        $relative = $asset.FullName.Substring($assetDirectory.Length + 1)
        Copy-ManagedFile $asset.FullName (Join-Path $releaseDirectory $relative)
        $relativePaths += $relative
    }
    $checksumLines = foreach ($relative in ($relativePaths | Sort-Object)) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $releaseDirectory $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
        $hash + '  ' + $relative.Replace('\', '/')
    }
    $checksumPath = Join-Path $releaseDirectory 'CHECKSUMS.sha256'
    Assert-NoReparsePoint $checksumPath
    [IO.File]::WriteAllLines($checksumPath, [string[]]$checksumLines, $utf8)
    Write-Output "构建完成：$(Join-Path $releaseDirectory '油管下载器.exe')"
    Write-Output "发行文件：$($relativePaths.Count + 1) 个；Downloads 中已有内容被保留，不会加入 ZIP。"
}
finally {
    if (Test-Path -LiteralPath $temporaryExe -PathType Leaf) {
        Assert-NoReparsePoint $temporaryExe
        Remove-Item -LiteralPath $temporaryExe -Force
    }
}
}
