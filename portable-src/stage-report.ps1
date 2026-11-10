[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReceiptPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd('\', '/')

function Resolve-ProjectPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw '路径不能为空。' }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "路径必须位于项目目录内：$full"
    }
    for ($candidate = $full; $candidate; $candidate = Split-Path $candidate -Parent) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "拒绝通过链接或重解析点访问：$candidate"
            }
        }
    }
    return $full
}

function Encode-Html($Value) { return [Net.WebUtility]::HtmlEncode([string]$Value) }
function Read-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Display-Text($Value, [string]$Fallback = '未提供') {
    if ([string]::IsNullOrWhiteSpace([string]$Value)) { return $Fallback }
    return [string]$Value
}
function File-Link([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return '<span class="muted">尚无文件路径</span>' }
    # Receipt metadata cannot turn a link into an arbitrary local or network target.
    try { $full = Resolve-ProjectPath $Path }
    catch { return '<span class="problem">路径不可用：' + (Encode-Html $Path) + '</span>' }
    if (-not (Test-Path -LiteralPath $full)) {
        return '<span class="muted">' + (Encode-Html $Label) + '（尚未创建或文件已移动）</span>'
    }
    $uri = New-Object Uri($full)
    return '<a href="' + (Encode-Html $uri.AbsoluteUri) + '">' + (Encode-Html $Label) + '</a>'
}
function Video-Link($Task) {
    $id = [string](Read-Field $Task 'videoId')
    $url = [string](Read-Field $Task 'url')
    $uri = $null
    if ($id -notmatch '^[A-Za-z0-9_-]{11}$' -or -not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri)) { return '' }
    if ($uri.Scheme -notin @('http', 'https') -or $uri.Host -notin @('youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com', 'youtu.be') -or $uri.UserInfo -or -not $uri.IsDefaultPort) { return '' }
    # Use the validated identity instead of embedding an untrusted raw URL.
    return '<a href="https://www.youtube.com/watch?v=' + $id + '" rel="noreferrer">查看 YouTube 原视频</a>'
}

$receiptFull = Resolve-ProjectPath $ReceiptPath
$outputFull = Resolve-ProjectPath $OutputPath
if ($receiptFull.Equals($outputFull, [StringComparison]::OrdinalIgnoreCase)) { throw '报告不能覆盖原始回执。' }
if ([IO.Path]::GetExtension($receiptFull) -ne '.json') { throw '回执必须是 JSON 文件。' }
if ([IO.Path]::GetExtension($outputFull) -ne '.html') { throw '输出必须使用 .html 扩展名。' }
if (-not (Test-Path -LiteralPath $receiptFull -PathType Leaf)) { throw "找不到回执：$receiptFull" }
if ((Get-Item -LiteralPath $receiptFull).Length -gt 16MB) { throw '回执超过 16 MiB，未生成报告。' }
$receipt = [IO.File]::ReadAllText($receiptFull, [Text.Encoding]::UTF8) | ConvertFrom-Json
if ((Read-Field $receipt 'schemaVersion') -ne 1 -or $null -eq $receipt.PSObject.Properties['tasks']) { throw '不支持的回执格式：需要 schemaVersion=1 和 tasks。' }
$tasks = @((Read-Field $receipt 'tasks') | Where-Object { $null -ne $_ })
$statuses = @{
    pending = '等待下载'; downloading = '下载中'; succeeded = '已完成'; failed = '下载失败';
    interrupted = '等待恢复'; missing = '文件缺失'
}
$kinds = @{
    'official-stage' = '官方舞台'; fullcam = '全景直拍'; 'member-fancam' = '成员直拍'; fancam = '饭拍'
}
$counts = @{}
foreach ($status in $statuses.Keys) { $counts[$status] = @($tasks | Where-Object { (Read-Field $_ 'status') -eq $status }).Count }
$batch = Display-Text (Read-Field $receipt 'batchId') '全部批次'
$generated = Display-Text (Read-Field $receipt 'generatedUtc') '未提供'
$timestamp = [DateTimeOffset]::MinValue
if ([DateTimeOffset]::TryParse($generated, [ref]$timestamp)) {
    $generated = $timestamp.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd HH:mm:ss') + '（北京时间）'
}
$html = New-Object Text.StringBuilder
[void]$html.AppendLine(@'
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
<title>K-pop 舞台下载记录</title>
<style>
:root { color-scheme: light; --bg: oklch(97% .008 260); --surface: oklch(100% 0 none); --text: oklch(27% .025 260); --muted: oklch(47% .025 260); --accent: oklch(45% .16 270); --border: oklch(88% .015 260); --danger: oklch(43% .16 25); --success: oklch(40% .10 155); --space: clamp(1rem, .7rem + 1.2vw, 2rem); --heading: clamp(1.7rem, 1.3rem + 1.5vw, 2.6rem); overflow-wrap: break-word; scrollbar-gutter: stable; }
*, *::before, *::after { box-sizing: border-box; min-inline-size: 0; }
body { margin: 0; min-block-size: 100svh; background: var(--bg); color: var(--text); font: 1rem/1.65 "Segoe UI", "Microsoft YaHei UI", sans-serif; }
main { --gutter: var(--space); --content: 76rem; --breakout: 82rem; display: grid; grid-template-columns: [full-width-start] minmax(var(--gutter), 1fr) [breakout-start] minmax(0, calc((var(--breakout) - var(--content)) / 2)) [content-start] min(100% - var(--gutter) * 2, var(--content)) [content-end] minmax(0, calc((var(--breakout) - var(--content)) / 2)) [breakout-end] minmax(var(--gutter), 1fr) [full-width-end]; gap: var(--space); padding-block: calc(var(--space) * 1.5); }
main > * { grid-column: content; }
h1, h2, h3, p, dl, dd { margin-block: 0; margin-inline: 0; }
h1 { font-size: var(--heading); line-height: 1.25; text-wrap: balance; }
h2 { font-size: 1.3rem; text-wrap: balance; }
h3 { font-size: 1.05rem; line-height: 1.5; overflow-wrap: anywhere; }
a { color: var(--accent); text-underline-offset: .2em; touch-action: manipulation; }
a:focus-visible { outline: 3px solid var(--accent); outline-offset: .2rem; }
a:active { color: var(--danger); background: color-mix(in oklch, var(--accent) 12%, var(--surface)); }
@media (hover: hover) and (pointer: fine) { a:hover { color: color-mix(in oklch, var(--accent), oklch(0% 0 none) 20%); } }
.intro, .event, .video, .empty { display: flex; flex-direction: column; gap: .85rem; }
.eyebrow { color: var(--accent); font-weight: 700; letter-spacing: .08em; }
.muted, dt, .note { color: var(--muted); }
.note { font-size: .9rem; }
.summary { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 8rem), 1fr)); gap: .8rem; }
.metric { padding-block: 1rem; padding-inline: 1.1rem; background: var(--surface); border: 1px solid var(--border); border-radius: .8rem; }
.metric dd { font-size: 1.8rem; font-weight: 700; font-variant-numeric: tabular-nums; }
.event { padding-block: .5rem; }
.event-heading { display: flex; flex-wrap: wrap; align-items: baseline; gap: .4rem 1rem; }
.event-heading p { color: var(--muted); }
.videos { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 25rem), 1fr)); gap: 1rem; }
.video { background: var(--surface); border: 1px solid var(--border); border-radius: 1rem; padding-block: 1.15rem; padding-inline: 1.2rem; }
.video-head, .links { display: flex; flex-wrap: wrap; gap: .5rem 1rem; align-items: baseline; }
.video-head { justify-content: space-between; }
.tag { display: inline-block; padding-block: .15rem; padding-inline: .6rem; border-radius: .4rem; background: color-mix(in oklch, var(--accent) 8%, var(--surface)); color: var(--accent); font-size: .85rem; }
.succeeded { color: var(--success); background: color-mix(in oklch, var(--success) 9%, var(--surface)); }
.failed, .missing, .problem { color: var(--danger); }
.failed, .missing { background: color-mix(in oklch, var(--danger) 8%, var(--surface)); }
.metadata { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: .2rem .8rem; font-size: .9rem; }
.metadata dd, .path, .error { overflow-wrap: anywhere; }
.error { white-space: pre-wrap; background: color-mix(in oklch, var(--danger) 6%, var(--surface)); border-inline-start: 3px solid var(--danger); padding-block: .6rem; padding-inline: .8rem; font-size: .9rem; }
.path { font-size: .85rem; color: var(--muted); }
.files { padding-inline-start: 1.25rem; margin-block: 0; font-size: .9rem; }
.links { margin-block-start: auto; }
.links a { padding-block: .25rem; }
.empty { padding-block: 2rem; padding-inline: var(--space); background: var(--surface); border: 1px dashed var(--border); border-radius: 1rem; }
footer { border-block-start: 1px solid var(--border); padding-block-start: 1rem; }
</style>
</head>
<body><main>
<header class="intro"><p class="eyebrow">K-POP / LOCAL LIBRARY</p><h1>舞台下载记录</h1>
'@)
[void]$html.AppendLine('<p>批次：<strong>' + (Encode-Html $batch) + '</strong></p>')
[void]$html.AppendLine('<p class="muted">回执时间：' + (Encode-Html $generated) + '</p>')
[void]$html.AppendLine('<p class="note">这是生成时的状态快照。重新运行本地 status 命令并生成报告后，再刷新此页。演出日期与上传日期分别记录。</p></header>')
$metrics = @(
    @('总视频', $tasks.Count), @('已完成', $counts.succeeded), @('等待下载', ($counts.pending + $counts.interrupted)),
    @('下载中', $counts.downloading), @('失败 / 文件缺失', ($counts.failed + $counts.missing))
)
[void]$html.AppendLine('<dl class="summary" aria-label="下载状态汇总">')
foreach ($metric in $metrics) { [void]$html.AppendLine('<div class="metric"><dt>' + $metric[0] + '</dt><dd>' + $metric[1] + '</dd></div>') }
[void]$html.AppendLine('</dl>')
$receiptError = [string](Read-Field $receipt 'error')
if ($receiptError) { [void]$html.AppendLine('<p class="error problem">接口报告：' + (Encode-Html $receiptError) + '</p>') }
if (Read-Field $receipt 'cancelled') { [void]$html.AppendLine('<p class="note">此次操作已取消；未完成的视频会保留在任务清单中。</p>') }
if ($tasks.Count -eq 0) { [void]$html.AppendLine('<section class="empty"><h2>还没有视频任务</h2><p class="muted">接收 Dots 的视频清单后，这里会显示活动、视频与下载状态。</p></section>') }
$groups = @($tasks | Group-Object -Property artist, release, performanceDate, eventName | Sort-Object Name)
foreach ($group in $groups) {
    $first = $group.Group[0]
    $artist = Display-Text (Read-Field $first 'artist') '艺人待确认'
    $release = Display-Text (Read-Field $first 'release') '专辑待确认'
    $event = Display-Text (Read-Field $first 'eventName') '活动待确认'
    $date = Display-Text (Read-Field $first 'performanceDate') '演出日期待确认'
    [void]$html.AppendLine('<section class="event"><div class="event-heading"><h2>' + (Encode-Html $date) + ' · ' + (Encode-Html $event) + '</h2><p>' + $group.Count + ' 个视频</p></div>')
    [void]$html.AppendLine('<p class="muted">' + (Encode-Html $artist) + ' / ' + (Encode-Html $release) + '</p><div class="videos">')
    foreach ($task in ($group.Group | Sort-Object kind, member, title)) {
        $status = [string](Read-Field $task 'status')
        $statusLabel = '未知状态'; $statusClass = ''
        if ($statuses.ContainsKey($status)) { $statusLabel = $statuses[$status]; $statusClass = $status }
        $kind = [string](Read-Field $task 'kind')
        $kindLabel = Display-Text $kind '类型待确认'
        if ($kinds.ContainsKey($kind)) { $kindLabel = $kinds[$kind] }
        [void]$html.AppendLine('<article class="video"><div class="video-head"><span class="tag">' + (Encode-Html $kindLabel) + '</span><span class="tag ' + $statusClass + '">' + $statusLabel + '</span></div>')
        [void]$html.AppendLine('<h3>' + (Encode-Html (Display-Text (Read-Field $task 'title') '标题未提供')) + '</h3><dl class="metadata">')
        $fields = @(
            @('频道', (Display-Text (Read-Field $task 'channel'))), @('成员', (Display-Text (Read-Field $task 'member') '全团 / 未指定')),
            @('上传日期', (Display-Text (Read-Field $task 'uploadDate') '待确认')), @('视频 ID', (Display-Text (Read-Field $task 'videoId'))),
            @('尝试次数', (Display-Text (Read-Field $task 'attempts') '0'))
        )
        foreach ($field in $fields) { [void]$html.AppendLine('<dt>' + $field[0] + '</dt><dd>' + (Encode-Html $field[1]) + '</dd>') }
        [void]$html.AppendLine('</dl>')
        $taskError = [string](Read-Field $task 'error')
        if ($taskError) { [void]$html.AppendLine('<p class="error problem">' + (Encode-Html $taskError) + '</p>') }
        $directory = [string](Read-Field $task 'targetDirectory')
        [void]$html.AppendLine('<p class="path">保存目录：' + (Encode-Html (Display-Text $directory '待分配')) + '</p>')
        $files = @((Read-Field $task 'files') | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        if ($files.Count -gt 0) {
            [void]$html.AppendLine('<ul class="files" aria-label="本地视频文件">')
            foreach ($file in $files) { [void]$html.AppendLine('<li>' + (File-Link ([string]$file) ([IO.Path]::GetFileName([string]$file))) + '</li>') }
            [void]$html.AppendLine('</ul>')
        }
        [void]$html.AppendLine('<div class="links">' + (Video-Link $task) + (File-Link $directory '打开保存目录') + '</div></article>')
    }
    [void]$html.AppendLine('</div></section>')
}
[void]$html.AppendLine('<footer class="note">' + (File-Link $receiptFull '查看原始 JSON 回执') + ' · 本页只展示记录，不会自动下载、联网或更改文件。浏览器可能对打开本地文件另作提示。</footer></main></body></html>')
$parent = Split-Path $outputFull -Parent
[IO.Directory]::CreateDirectory($parent) | Out-Null
# Recheck after creating directories and immediately before the only output write.
$null = Resolve-ProjectPath $outputFull
[IO.File]::WriteAllText($outputFull, $html.ToString(), (New-Object Text.UTF8Encoding($true)))
Write-Output $outputFull
