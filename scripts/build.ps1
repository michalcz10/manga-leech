param(
    [ValidateSet('csharp')]
    [string]$Target = 'csharp',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'build/csharp'))
$publishDir = [IO.Path]::GetFullPath((Join-Path $buildRoot $Runtime))
$stagingDir = [IO.Path]::GetFullPath((Join-Path $buildRoot ($Runtime + '-staging-' + [guid]::NewGuid().ToString('N'))))
$prefix = $buildRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $publishDir.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $stagingDir.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish paths must stay inside build/csharp.'
}
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$bundleRuntime = if ($SelfContained) { 'true' } else { 'false' }
Push-Location $projectRoot
try {
    & dotnet publish csharp/MangaLeech.csproj --configuration $Configuration `
        --runtime $Runtime --self-contained $bundleRuntime --output $stagingDir `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'C# build failed.' }
    $nativeSymbols = Join-Path $stagingDir 'libSkiaSharp.pdb'
    if (Test-Path -LiteralPath $nativeSymbols) { Remove-Item -LiteralPath $nativeSymbols -Force }
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    Move-Item -LiteralPath $stagingDir -Destination $publishDir
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
}
