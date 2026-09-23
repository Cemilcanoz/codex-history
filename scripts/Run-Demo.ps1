[CmdletBinding()]
param(
    [string]$ArtifactPath = (Join-Path (Get-Location) 'artifacts\demo.png')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifact = [System.IO.Path]::GetFullPath($ArtifactPath)
$artifactDirectory = Split-Path -Parent $artifact
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$temporaryArtifact = Join-Path $artifactDirectory ('.demo-' + [Guid]::NewGuid().ToString('N') + '.png')
$temporaryBase = [System.IO.Path]::GetFileNameWithoutExtension($temporaryArtifact)
$companionLabels = @('light', 'dark', 'small')
$temporaryArtifacts = @($temporaryArtifact)
$artifacts = @($artifact)
foreach ($label in $companionLabels) {
    $temporaryArtifacts += Join-Path $artifactDirectory ($temporaryBase + ".$label" + [System.IO.Path]::GetExtension($temporaryArtifact))
    $artifacts += Join-Path $artifactDirectory ([System.IO.Path]::GetFileNameWithoutExtension($artifact) + ".$label" + [System.IO.Path]::GetExtension($artifact))
}

Push-Location $repoRoot
try {
    & dotnet run --project src\CodexHistory.App -- --smoke $temporaryArtifact
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke run failed with exit code $LASTEXITCODE."
    }
    for ($index = 0; $index -lt $temporaryArtifacts.Count; $index++) {
        $temporaryPath = $temporaryArtifacts[$index]
        if (-not (Test-Path -LiteralPath $temporaryPath -PathType Leaf)) {
            throw "Smoke run completed without creating artifact: $temporaryPath"
        }
        if ((Get-Item -LiteralPath $temporaryPath).Length -le 0) {
            throw "Smoke run created an empty artifact: $temporaryPath"
        }
    }
    for ($index = 0; $index -lt $temporaryArtifacts.Count; $index++) {
        Move-Item -LiteralPath $temporaryArtifacts[$index] -Destination $artifacts[$index] -Force
    }
    Write-Output "Demo artifact: $artifact"
}
finally {
    foreach ($temporaryPath in $temporaryArtifacts) {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
    Pop-Location
}
