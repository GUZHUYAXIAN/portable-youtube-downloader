function Get-ProjectTemporaryDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [ValidateSet('tmp', 'runtime-tmp')][string]$DirectoryName = 'tmp'
    )

    $projectTempRoot = [IO.Path]::GetFullPath((Join-Path (Join-Path $ProjectRoot '.local') $DirectoryName))
    $projectTempCandidate = $projectTempRoot
    while ($projectTempCandidate) {
        if (Test-Path -LiteralPath $projectTempCandidate) {
            $projectTempItem = Get-Item -LiteralPath $projectTempCandidate -Force
            if (($projectTempItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a linked development temporary directory: $projectTempCandidate"
            }
        }
        $projectTempParent = Split-Path $projectTempCandidate -Parent
        if ($projectTempParent -eq $projectTempCandidate) { break }
        $projectTempCandidate = $projectTempParent
    }
    [IO.Directory]::CreateDirectory($projectTempRoot) | Out-Null
    return $projectTempRoot
}

function Invoke-ProjectTempEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )
    $projectTempRoot = Get-ProjectTemporaryDirectory -ProjectRoot $ProjectRoot

    # GetTempPath/GetTempPath2 and child tools can consult different variables.
    # Process scope is deliberate: never change user or machine settings.
    $projectTempNames = @('TEMP', 'TMP', 'TMPDIR', 'SystemTemp')
    $projectTempPrevious = @{}
    foreach ($projectTempName in $projectTempNames) {
        $projectTempPrevious[$projectTempName] = [Environment]::GetEnvironmentVariable($projectTempName, 'Process')
    }
    try {
        foreach ($projectTempName in $projectTempNames) {
            [Environment]::SetEnvironmentVariable($projectTempName, $projectTempRoot, 'Process')
        }
        $projectTempActual = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        if (-not $projectTempActual.Equals($projectTempRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Temporary path escaped the project: $projectTempActual"
        }
        Write-Host "Development temporary directory: $projectTempActual"
        & $Action
    }
    finally {
        foreach ($projectTempName in $projectTempNames) {
            [Environment]::SetEnvironmentVariable($projectTempName, $projectTempPrevious[$projectTempName], 'Process')
        }
    }
}
