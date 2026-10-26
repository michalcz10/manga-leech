# Manga Leech

C# / .NET 10 CLI for downloading a WeebCentral series, RSS/Atom feed, or a single chapter into one PDF per chapter. Images come exclusively from `#chapter-images` in reader order. Each image becomes a page with its original aspect ratio, without cropping or splitting long webtoons. Python is no longer required.

## Build

Install the .NET 10 SDK. Run commands from the repository root; the first build needs internet access for NuGet. Published binaries include the .NET runtime and native image decoder.

Windows / PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
.\build\csharp\win-x64\manga_leech.exe --help
```

Linux / Bash (glibc, x64 or ARM64):

```bash
bash scripts/build-linux.sh
./build/csharp/linux-$(uname -m | sed -e 's/x86_64/x64/' -e 's/aarch64/arm64/')/manga_leech --help
```

Release is the default. Windows cross-publishing: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Runtime linux-x64` (also `linux-arm64`, `win-arm64`). Outputs are always under `build/csharp/<runtime>/`. Set `-Configuration Debug` on Windows or `CONFIGURATION=Debug` on Linux for a debug build. Linux still requires system glibc libraries and an executable extraction directory for native Skia assets; set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` if needed. Retain the license and notice files beside distributed binaries.

## Usage

Run directly with the SDK, or substitute the published executable for `dotnet run --project csharp --`:

```sh
dotnet run --project csharp -- "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/The-Greatest-Estate-Developer" --limit 3
dotnet run --project csharp -- "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/rss" --list
dotnet run --project csharp -- --chapter "https://weebcentral.com/chapters/01J76XZ3W1SDBG0SAGFG8RYXS4"
```

| Parameter | Meaning / default |
| --- | --- |
| `URL` | Series page or RSS/Atom feed; feeds contain only their listed releases |
| `--chapter URL` | Single chapter instead of a source URL |
| `-h`, `--help` | Show help |
| `-o`, `--output DIR` | Output directory: `downloads` |
| `--list` | List selected chapters without downloading images |
| `--limit N` | First N chapters after sorting; positive integer |
| `--oldest-first` | Ascending numeric chapter order (default) |
| `--newest-first` | Descending numeric chapter order |
| `--workers N` | **8** simultaneous image transfers in total across all chapters |
| `--chapter-workers N` | **3** chapters downloading concurrently; `1` restores sequential chapters |
| `--pdf-workers N` | **4** PDF workers using threads |
| `--delay SECONDS` | Delay before each request per worker: `0.5`; not a global request rate |
| `--timeout SECONDS` | Total time per transfer attempt: `60` |
| `--user-agent VALUE` | `MangaLeech.CSharp/0.2` |
| `--force` | Download images again and replace PDFs |
| `--rebuild` | Rebuild PDFs, downloading only missing or invalid cached images |
| `--write-manifest` | Optional `.pages.sha256` signature of URLs, title, quality and generator version |
| `--delete-image-cache` | Remove used cached images and verification receipts after successful PDF creation |
| `--jpeg-quality 1-95` | Optional lossy compression when smaller; original JPEGs stay unchanged |

Worker counts accept 1â€“1024. `--pdf-tasks-per-child` from Python is unsupported because C# uses threads. `--force` overrides `--rebuild`; existing PDFs are skipped unless forced, rebuilt, or their enabled manifest differs. Cache cleanup applies only to newly generated PDFs, not skipped PDFs. Failed chapters retain verified images. A rebuild still reads the chapter page for its current image list.

## Concurrency, cache and PDFs

Three chapter downloads share eight image slots. Completed chapters enter a bounded queue of at most twice `--pdf-workers`; four PDF workers drain it while downloading continues. Sorting determines selection and scheduling, while chapters may finish in a different order. The image limit stays global as the chapter count increases. This allows requests to overlap across chapters; actual throughput depends on the server. HTTP 429/503 pauses new requests to the same host using `Retry-After` (maximum 120 seconds); each transfer has at most three attempts. The application does not handle CAPTCHA or login.

Images live under `downloads/.images/` with SHA-256 verification receipts. Downloads and PDFs use temporary `.part` files and are published only after success. Exit codes: `0` success, `1` failure, `130` Ctrl+C. Failure of one chapter does not stop others. Ctrl+C cancels queued/network work and waits for active PDF operations to observe cancellation; native image decoding/compression can delay that briefly. Running two instances into the same output directory is unsupported.

PDFs always contain title and XMP metadata. For recognized chapter titles, Kavita receives `calibre:series` and `calibreSI:series_index` (mapped by Kavita to Volume). Names follow `Series - Ch 025.pdf`, including decimal indices. Conflicting output names are rejected before downloading. The optional manifest checks generation inputs, not the bytes of an existing PDF.

JPEG streams and compatible opaque, non-interlaced PNG streams are embedded unchanged, preserving palette and bit depth. Other images, including WebP and transparent PNG, decode to 8-bit RGB with an alpha mask when needed; this can increase file size, and 16-bit transparent PNG loses bit depth. `--jpeg-quality` leaves transparency and one-bit/paletted PNG unchanged and never alters cached originals.

## Development

`csharp/` separates CLI/options, parsing, HTTP retries, image cache, bounded download pipeline and PDF writing. `tests/MangaLeech.Tests/` contains a dependency-free regression runner with a local HTTP server; tests need loopback networking and do not contact WeebCentral.

```sh
dotnet build csharp/MangaLeech.csproj -c Release
dotnet run --project tests/MangaLeech.Tests -c Release
```

After publishing, also test native decoding through the Windows executable:

```powershell
dotnet run --project tests/MangaLeech.Tests -c Release -- --published build/csharp/win-x64/manga_leech.exe
```

Dependencies: HtmlAgilityPack and SkiaSharp (MIT); no paid license is needed for Release builds. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). `docs/PROJECT_REVIEW.md` describes the historical Python implementation.
