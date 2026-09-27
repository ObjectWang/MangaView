param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x64')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$outputPath = [System.IO.Path]::GetFullPath($Output)
$project = Join-Path $root 'src\MangaView.App\MangaView.App.csproj'

$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:DOTNET_ROOT = 'D:\software\dotnet'

if (Test-Path -LiteralPath $outputPath) {
    $artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
    if (-not $outputPath.StartsWith($artifactRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an output path outside the artifacts directory: $outputPath"
    }
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

& 'D:\software\dotnet\dotnet.exe' publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $outputPath

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($document in @('README-V1.md', 'README-M4.md', 'THIRD-PARTY-NOTICES.md')) {
    $source = Join-Path $root $document
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $outputPath $document) -Force
    }
}

Write-Host "Published to $outputPath"
Write-Host "Executable: $(Join-Path $outputPath 'MangaView.exe')"
