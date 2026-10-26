#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
if [[ "$(uname -s)" != Linux ]]; then
    echo 'Run this script on Linux.' >&2
    exit 1
fi
# Keep the old explicit C# target accepted for existing callers.
if [[ "${1:-csharp}" != csharp ]]; then
    echo 'Usage: bash scripts/build-linux.sh [csharp]' >&2
    exit 1
fi
case "$(uname -m)" in
    x86_64) runtime=linux-x64 ;;
    aarch64|arm64) runtime=linux-arm64 ;;
    *) echo 'Supported architectures: x86_64 and aarch64.' >&2; exit 1 ;;
esac

dotnet publish csharp/MangaLeech.csproj --configuration "${CONFIGURATION:-Release}" \
    --runtime "$runtime" --self-contained true --output "build/csharp/$runtime" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None -p:DebugSymbols=false
