[CmdletBinding()]
param([switch]$Replace)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$releaseDirectory = [IO.Path]::GetFullPath((Join-Path $workspace '油管下载器便携版'))
$zipPath = [IO.Path]::GetFullPath((Join-Path $workspace '油管下载器便携版.zip'))
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'package-output'))
$stageDirectory = Join-Path $outputDirectory ('stage-' + [Guid]::NewGuid().ToString('N'))
$temporaryZip = Join-Path $outputDirectory ('package-' + [Guid]::NewGuid().ToString('N') + '.zip')
$utf8 = New-Object Text.UTF8Encoding($true)

function Assert-NoReparsePoint([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "拒绝通过链接或重解析点打包：$candidate" }
        }
        $parent = Split-Path $candidate -Parent
        if ($parent -eq $candidate) { break }
        $candidate = $parent
    }
}

Assert-NoReparsePoint $zipPath
Assert-NoReparsePoint ($zipPath + '.sha256')
Assert-NoReparsePoint $outputDirectory
if ((Test-Path -LiteralPath $zipPath) -and -not $Replace) {
    throw 'ZIP 已存在。确认需要替换后，请明确添加 -Replace；脚本不会删除下载目录。'
}
$assetDirectory = Join-Path $PSScriptRoot 'release-assets'
$expected = @('油管下载器.exe', '使用说明.txt', 'THIRD-PARTY-NOTICES.txt', 'VERSIONS-AND-SOURCES.txt', 'tools\yt-dlp.exe', 'tools\ffmpeg.exe', 'tools\ffprobe.exe', 'tools\node.exe')
$licenseFiles = @(Get-ChildItem -LiteralPath (Join-Path $assetDirectory 'licenses') -File -Recurse -Filter '*.txt')
if ($licenseFiles.Count -lt 5) { throw '第三方许可文件不完整。' }
foreach ($file in $licenseFiles) { $expected += $file.FullName.Substring($assetDirectory.Length + 1) }
$checksumPath = Join-Path $releaseDirectory 'CHECKSUMS.sha256'
Assert-NoReparsePoint $checksumPath
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw '请先运行 build.ps1 生成发行目录。' }
$checksums = @{}
foreach ($line in (Get-Content -LiteralPath $checksumPath -Encoding UTF8)) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'CHECKSUMS.sha256 格式错误。' }
    $relative = $Matches[2].Replace('/', '\')
    if ($checksums.ContainsKey($relative)) { throw "重复的校验项目：$relative" }
    $checksums[$relative] = $Matches[1]
}
if ($checksums.Count -ne $expected.Count) { throw '发行目录文件清单与源码资产不同，请重新构建。' }
foreach ($relative in $expected) {
    if (-not $checksums.ContainsKey($relative)) { throw "缺少校验项目：$relative" }
    $source = Join-Path $releaseDirectory $relative
    Assert-NoReparsePoint $source
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "缺少发行文件：$relative" }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $checksums[$relative]) { throw "发行文件校验失败：$relative；请重新构建。" }
}

try {
    New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
    $stageRoot = Join-Path $stageDirectory '油管下载器便携版'
    New-Item -ItemType Directory -Path (Join-Path $stageRoot 'Downloads') -Force | Out-Null
    foreach ($relative in ($expected + @('CHECKSUMS.sha256'))) {
        $destination = Join-Path $stageRoot $relative
        $parent = Split-Path $destination -Parent
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath (Join-Path $releaseDirectory $relative) -Destination $destination
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stageDirectory, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
    try {
        $entries = @($archive.Entries | Where-Object { $_.Name -ne '' })
        if ($entries.Count -ne ($expected.Count + 1)) { throw 'ZIP 文件数量不符合白名单。' }
        foreach ($entry in $entries) {
            $relative = $entry.FullName.Replace('/', '\')
            if (-not $relative.StartsWith('油管下载器便携版\')) { throw "ZIP 路径不正确：$relative" }
            $relative = $relative.Substring('油管下载器便携版\'.Length)
            if (($relative -ne 'CHECKSUMS.sha256') -and ($expected -notcontains $relative)) { throw "ZIP 出现白名单外文件：$relative" }
        }
    }
    finally { $archive.Dispose() }
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force:$Replace
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($zipPath + '.sha256'), ($hash + '  ' + [IO.Path]::GetFileName($zipPath) + [Environment]::NewLine), $utf8)
    Write-Output "已生成：$zipPath"
    Write-Output "大小：$((Get-Item -LiteralPath $zipPath).Length) 字节"
    Write-Output "SHA-256：$hash"
}
finally {
    # Only remove this invocation's newly created staging directory, after boundary checks.
    $resolvedStage = [IO.Path]::GetFullPath($stageDirectory)
    $expectedPrefix = $outputDirectory.TrimEnd('\') + '\stage-'
    if (-not $resolvedStage.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '暂存目录清理边界检查失败。' }
    Assert-NoReparsePoint $resolvedStage
    if (Test-Path -LiteralPath $resolvedStage -PathType Container) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
    Assert-NoReparsePoint $temporaryZip
    if (Test-Path -LiteralPath $temporaryZip -PathType Leaf) { Remove-Item -LiteralPath $temporaryZip -Force }
}
