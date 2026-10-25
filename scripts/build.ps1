param(
    [ValidateSet('all', 'python', 'csharp')]
    [string]$Target = 'all',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if ($Target -in @('all', 'python') -and $Runtime -ne 'win-x64') {
        throw 'Python must be built on the target OS/architecture. For Linux C# use -Target csharp -Runtime linux-x64; for Linux Python use scripts/build-linux.sh on Linux.'
    }
    if ($Target -in @('all', 'python')) {
        $python = Join-Path $projectRoot '.venv/Scripts/python.exe'
        if (-not (Test-Path -LiteralPath $python)) {
            throw 'Create .venv and install python/requirements-build.txt first (see README).'
        }
        & $python -m PyInstaller --noconfirm --clean --onefile --name manga_leech `
            --distpath build/python --workpath build/.work/python `
            --specpath build/.work python/manga_leech.py
        if ($LASTEXITCODE -ne 0) { throw 'Python build failed.' }
    }
    if ($Target -in @('all', 'csharp')) {
        $csharpOutput = 'build/csharp'
        if ($Runtime -ne 'win-x64') { $csharpOutput = "build/csharp/$Runtime" }
        # ImageSharp 4 requires a valid license for Release; keep Debug as default.
        & dotnet publish csharp/MangaLeech.csproj --configuration $Configuration `
            --runtime $Runtime --self-contained true --output $csharpOutput `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw 'C# build failed.' }
    }
}
finally {
    Pop-Location
}
