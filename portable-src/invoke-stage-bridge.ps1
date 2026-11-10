[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('submit', 'run', 'status')][string]$Action,
    [string]$ManifestPath,
    [string]$BatchId,
    [switch]$RetryFailed
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$stateDirectory = Join-Path $workspace '.local\stage-bridge'
$exe = Join-Path $stateDirectory 'bin\StageBridge.exe'
$bridgeAction = $Action

function Assert-LocalPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($workspace.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "路径必须在本项目内：$full"
    }
    $candidate = $full
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            if (((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "拒绝链接或重解析路径：$candidate"
            }
        }
        $candidate = [IO.Path]::GetDirectoryName($candidate)
    }
    return $full
}

function Quote-Argument([string]$Value) {
    # Windows argv quoting, matching CommandLineToArgvW; no shell is used.
    $quoted = [Text.StringBuilder]::new('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$quoted.Append('\', (2 * $slashes + 1))
        } else {
            [void]$quoted.Append('\', $slashes)
        }
        [void]$quoted.Append($character)
        $slashes = 0
    }
    [void]$quoted.Append('\', (2 * $slashes))
    [void]$quoted.Append('"')
    return $quoted.ToString()
}

Assert-LocalPath $exe | Out-Null
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw '本地接口尚未构建。请先运行 portable-src\build.ps1 -BridgeOnly。'
}
if ($BatchId -and $BatchId -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$') { throw '批次 ID 仅允许 1-80 位字母、数字、下划线和连字符。' }
if ($Action -eq 'submit' -and ([string]::IsNullOrWhiteSpace($ManifestPath) -or $BatchId -or $RetryFailed)) { throw 'submit 只接收 -ManifestPath。' }
if ($Action -eq 'run' -and ([string]::IsNullOrWhiteSpace($BatchId) -or $ManifestPath)) { throw 'run 需要 -BatchId，不接收 -ManifestPath。' }
if ($Action -eq 'status' -and ($ManifestPath -or $RetryFailed)) { throw 'status 只接收可选的 -BatchId。' }
$arguments = @('--workspace', $workspace, $Action)
if ($Action -eq 'submit') {
    $manifest = Assert-LocalPath $ManifestPath
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "清单不存在：$manifest" }
    $arguments += $manifest
} elseif ($BatchId) { $arguments += $BatchId }
if ($RetryFailed) { $arguments += '--retry-failed' }

. (Join-Path $PSScriptRoot 'dev-environment.ps1')
$script:bridgeExitCode = 1
Invoke-ProjectTempEnvironment -ProjectRoot $workspace -Action {
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $exe
    $startInfo.Arguments = (($arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $startInfo.WorkingDirectory = $workspace
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $startInfo.EnvironmentVariables['STAGE_BRIDGE_PARENT_PID'] = [string]$PID
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    $hasStarted = $false
    try {
        $hasStarted = $process.Start()
        if (-not $hasStarted) { throw '本地接口启动失败。' }
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        # Yield to the PowerShell engine so Ctrl+C can run the cleanup below.
        while (-not $process.WaitForExit(200)) { }
        $json = $outputTask.GetAwaiter().GetResult()
        $script:bridgeExitCode = $process.ExitCode
    } finally {
        try {
            if ($hasStarted -and -not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
        } finally { $process.Dispose() }
    }
    if ([string]::IsNullOrWhiteSpace($json)) { throw "本地接口未返回回执（退出码 $script:bridgeExitCode）。" }
    $receipt = $json | ConvertFrom-Json
    if ($receipt.schemaVersion -ne 1) { throw '无法识别本地回执版本。' }
    $receiptDirectory = Assert-LocalPath (Join-Path $stateDirectory 'receipts')
    [IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
    $receiptPath = Assert-LocalPath (Join-Path $receiptDirectory ((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ') + '-' + $bridgeAction + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8) + '.json'))
    [IO.File]::WriteAllText($receiptPath, $json, (New-Object Text.UTF8Encoding($false)))
    try {
        $reportDirectory = Assert-LocalPath (Join-Path $stateDirectory 'reports')
        [IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
        # Batch identity is case-sensitive; Windows filenames are not. A prefixed
        # identity hash also avoids device names and the aggregate report name.
        $reportName = 'all'
        if ($receipt.batchId) {
            $batchIdentity = [string]$receipt.batchId
            if ($batchIdentity -notmatch '\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z') { throw '回执中的批次 ID 无效。' }
            $hash = [Security.Cryptography.SHA256]::Create()
            try { $reportName = 'batch-' + ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($batchIdentity)))).Replace('-', '').ToLowerInvariant() }
            finally { $hash.Dispose() }
        }
        $reportPath = Assert-LocalPath (Join-Path $reportDirectory ($reportName + '.html'))
        & (Join-Path $PSScriptRoot 'stage-report.ps1') -ReceiptPath $receiptPath -OutputPath $reportPath | Out-Null
    } catch {
        # Reporting is optional: preserve the bridge's durable outcome, exit code,
        # and stdout JSON even when the report cannot be written.
        [Console]::Error.WriteLine('报告生成失败；JSON 回执已保留：' + $_.Exception.Message)
    }
    Write-Output $json.Trim()
} 6>$null
exit $script:bridgeExitCode
