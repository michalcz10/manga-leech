param(
    [ValidateSet('csharp')]
    [string]$Target = 'csharp',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    & dotnet publish csharp/MangaLeech.csproj --configuration $Configuration `
        --runtime $Runtime --self-contained true --output "build/csharp/$Runtime" `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'C# build failed.' }
}
finally {
    Pop-Location
}
