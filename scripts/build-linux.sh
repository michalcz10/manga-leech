#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
if [[ "$(uname -s)" != Linux ]]; then
    echo 'Run this script on Linux.' >&2
    exit 1
fi
target="${1:-all}"
case "$target" in
    all|python|csharp) ;;
    *) echo 'Usage: bash scripts/build-linux.sh [all|python|csharp]' >&2; exit 1 ;;
esac
case "$(uname -m)" in
    x86_64) runtime=linux-x64 ;;
    aarch64|arm64) runtime=linux-arm64 ;;
    *) echo 'Supported architectures: x86_64 and aarch64.' >&2; exit 1 ;;
esac

if [[ "$target" == all || "$target" == python ]]; then
    python="${PYTHON:-.venv-linux/bin/python}"
    if [[ ! -x "$python" ]]; then
        echo 'Create .venv-linux and install python/requirements-build.txt first (see README).' >&2
        exit 1
    fi
    "$python" -m PyInstaller --noconfirm --clean --onefile --name manga_leech \
        --distpath "build/python/$runtime" --workpath "build/.work/python/$runtime" \
        --specpath "build/.work/$runtime" python/manga_leech.py
fi

if [[ "$target" == all || "$target" == csharp ]]; then
    # ImageSharp 4 requires a valid license for Release; keep Debug as default.
    dotnet publish csharp/MangaLeech.csproj --configuration "${CONFIGURATION:-Debug}" \
        --runtime "$runtime" --self-contained true --output "build/csharp/$runtime" \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=None -p:DebugSymbols=false
fi
