[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture = Join-Path $project ('.local\wrapper-tests-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $project 'portable-src'
$localSource = Join-Path $fixture 'portable-src'
$data = Join-Path $fixture '.local\stage-bridge'
$failures = 0
function Check([string]$Name, [scriptblock]$Body) {
    try { & $Body; Write-Host "PASS $Name" }
    catch { $script:failures++; Write-Host "FAIL ${Name}: $_" }
}
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Invoke-Wrapper([string[]]$Arguments) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $localSource 'invoke-stage-bridge.ps1') + '" ' + ($Arguments -join ' ')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Wrapper timed out' }
        $output = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        Assert ($process.ExitCode -eq 0) "Wrapper exited $($process.ExitCode): $errorText"
        $receipt = $output | ConvertFrom-Json
        Assert ($receipt.schemaVersion -eq 1 -and $receipt.ok) 'Missing successful JSON receipt'
        return $receipt
    } finally { $process.Dispose() }
}
try {
    New-Item -ItemType Directory -Force $localSource,(Join-Path $data 'bin'),(Join-Path $fixture 'output'),(Join-Path $fixture 'bundle') | Out-Null
    foreach ($name in 'invoke-stage-bridge.ps1','stage-report.ps1','dev-environment.ps1') { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $localSource }
    $settings = @{ toolsRoot=(Join-Path $fixture 'bundle'); outputRoot=(Join-Path $fixture 'output'); height=1080; proxy='' }
    $settings | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $data 'settings.json')
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $sources = @('Core.cs','ProcessRunner.cs','DownloadSession.cs') | ForEach-Object { Join-Path $source $_ }
    $sources += @(Get-ChildItem (Join-Path $source 'bridge') -Filter *.cs | ForEach-Object FullName)
    & $compiler /nologo /target:exe /langversion:5 /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll ('/out:' + (Join-Path $data 'bin\StageBridge.exe')) @sources
    if ($LASTEXITCODE -ne 0) { throw 'Fixture bridge compilation failed' }
    foreach ($id in 'CON','all','Batch','batch') {
        Check "Batch $id retains JSON and has its own report" {
            $manifest = @{ schemaVersion=1; batchId=$id; artist='Test'; release='Test'; items=@(@{videoId='stage000001';url='https://www.youtube.com/watch?v=stage000001'; title='Test'; kind='official-stage'}) }
            $manifestPath = Join-Path $fixture 'manifest.json'
            $manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $manifestPath
            $before = @(Get-ChildItem (Join-Path $data 'reports') -Filter *.html -ErrorAction SilentlyContinue).Count
            $receipt = Invoke-Wrapper @('-Action','submit','-ManifestPath',('"'+$manifestPath+'"'))
            Assert ($receipt.batchId -ceq $id) 'Wrong batch identity'
            $after = @(Get-ChildItem (Join-Path $data 'reports') -Filter *.html).Count
            Assert ($after -eq $before + 1) 'Batch report missing or overwrote another batch'
        }
    }
    Check 'Aggregate report does not overwrite batch all' {
        $before = @(Get-ChildItem (Join-Path $data 'reports') -Filter *.html).Count
        $null = Invoke-Wrapper @('-Action','status')
        Assert (@(Get-ChildItem (Join-Path $data 'reports') -Filter *.html).Count -eq $before + 1) 'Aggregate overwrote batch report'
    }
    Check 'Report failure cannot suppress successful JSON receipt' {
        [IO.File]::WriteAllText((Join-Path $localSource 'stage-report.ps1'), "throw 'fixture report failure'")
        $null = Invoke-Wrapper @('-Action','status')
    }
} finally {
    $full = [IO.Path]::GetFullPath($fixture)
    $boundary = [IO.Path]::GetFullPath((Join-Path $project '.local')) + '\'
    if (-not $full.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $full -Leaf) -notlike 'wrapper-tests-*') { throw 'Unsafe fixture cleanup path' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
if ($failures) { throw "$failures wrapper regressions failed" }