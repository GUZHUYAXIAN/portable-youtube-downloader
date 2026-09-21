param([Parameter(Mandatory=$true)][string]$AppRoot, [Parameter(Mandatory=$true)][string]$Video, [int]$MaxHeight = 480)
$ErrorActionPreference = 'Stop'
$probe = Join-Path $AppRoot 'tools\ffprobe.exe'
$json = & $probe -v error -show_entries 'stream=index,codec_name,codec_type,width,height:format=format_name,duration,size' -of json $Video
if ($LASTEXITCODE -ne 0) { throw 'ffprobe failed' }
$data = $json | ConvertFrom-Json
$videoStreams = @($data.streams | Where-Object codec_type -eq 'video')
$audioStreams = @($data.streams | Where-Object codec_type -eq 'audio')
if ($videoStreams.Count -eq 0 -or $audioStreams.Count -eq 0) { throw '音频或视频流缺失' }
if ($data.format.format_name -notmatch '(^|,)mp4(,|$)' -or [double]$data.format.duration -le 0) { throw '容器或时长校验失败' }
if (@($videoStreams | Where-Object { $_.height -gt $MaxHeight }).Count -gt 0) { throw '输出超出清晰度上限' }
$evidence = Join-Path (Split-Path $PSScriptRoot -Parent) 'acceptance'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$json | Set-Content -LiteralPath (Join-Path $evidence 'ffprobe-final.json') -Encoding utf8
Get-FileHash -LiteralPath $Video -Algorithm SHA256 | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'downloaded-file.json') -Encoding utf8
# Decode actual downloaded streams as an additional check, with no media output files.
& (Join-Path $AppRoot 'tools\ffmpeg.exe') -v error -i $Video -map '0:v:0' -map '0:a:0' -f null - 2> (Join-Path $evidence 'decode-errors.txt')
if ($LASTEXITCODE -ne 0) { throw '实际音视频解码校验失败' }
$json
'PASS MP4 contains both streams, valid duration, selected quality and full-file decode'
