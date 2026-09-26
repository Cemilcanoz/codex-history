[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release'),
    [string]$Version = '0.1.1'
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw 'Version must be a semantic version, for example 0.1.0 or 0.1.0-preview.1.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $output 'Codex-History-win-x64'
$archive = Join-Path $output "Codex-History-$Version-win-x64-portable.zip"
$stage = [System.IO.Path]::GetFullPath($stage)
if ([System.IO.Path]::GetRelativePath($output, $stage) -ne 'Codex-History-win-x64') {
    throw 'The staging directory must be directly under the release output directory.'
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }

Push-Location $repoRoot
try {
    & dotnet publish src\CodexHistory.App\CodexHistory.App.csproj `
        --configuration Release --runtime win-x64 --self-contained true `
        --output $stage `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

    $executable = Join-Path $stage 'CodexHistory.App.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published executable missing: $executable"
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $stage 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $stage 'README.md')

    @'
CODEX HISTORY — WINDOWS PORTABLE

1. ZIP dosyasını bir klasöre çıkarın.
2. CodexHistory.App.exe dosyasını çalıştırın.
3. sessions/ ve archived_sessions/ klasörlerini içeren Codex ana klasörünü seçin ve TARA düğmesine basın. İsterseniz önce DEMO ile sentetik veriyi deneyin.

Windows x64 içindir. .NET kurulumu gerektirmez. Uygulama yalnız seçtiğiniz Codex oturum
klasörünü okur; auth.json ve kimlik bilgilerini işlemez. İndeks veritabanı yerel
AppData\Local\CodexHistory\index.db konumunda oluşturulur. Kaldırmak için uygulama
klasörünü silin; indeks verisini de kaldırmak isterseniz bu AppData klasörünü silin.

API maliyeti güncel liste fiyatlarıyla yaklaşık karşılıktır; Codex abonelik faturası değildir.
Su göstergesi ölçüm değil, değiştirilebilir katsayılı örnek senaryodur.
Bu, bağımsız bir topluluk projesidir; OpenAI'nin resmî ürünü değildir.
'@ | Set-Content -LiteralPath (Join-Path $stage 'KULLANIM.txt') -Encoding UTF8

    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($archive))" |
        Set-Content -LiteralPath "$archive.sha256" -Encoding ASCII
    Remove-Item -LiteralPath $stage -Recurse -Force
    Write-Output "Archive: $archive"
    Write-Output "SHA256: $hash"
}
finally {
    Pop-Location
}
