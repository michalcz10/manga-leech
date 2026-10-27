# Manga Leech

C# / .NET 10 CLI for downloading a WeebCentral or MangaFire series, RSS/Atom feed, or a single chapter into one PDF per chapter. Site adapters select images in reader order. Each image becomes a page with its original aspect ratio, without cropping or splitting long webtoons. Python is no longer required.

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

## Debian 13 package

Build on Windows (`py`/`python`) or Linux (`python3`) with Python 3.10+ and the .NET 10 SDK:

```sh
python scripts/build-deb.py --runtime linux-x64
# ARM64: python scripts/build-deb.py --runtime linux-arm64
```

Output: `build/deb/manga-leech_0.4.0-1~deb13_amd64.deb` (`arm64` for ARM).
Use `--no-build` to reuse a current complete Linux publish directory, or
`--version VERSION` to set the package version.

```sh
sudo apt install ./manga-leech_0.4.0-1~deb13_amd64.deb
/usr/bin/manga_leech --help
```

The .NET runtime and application libraries remain included. APT installs only
system libraries; **Chromium, Playwright, Node.js and Microsoft APT repositories
are not needed**. The app lives under `/usr/lib/manga-leech` with a launcher at
`/usr/bin/manga_leech`. Use this explicit path if an old manually installed copy
in `/usr/local/bin` takes precedence (`type -a manga_leech`). Upgrading removes
package-owned browser files from old versions; user downloads stay untouched.
Previously installed system browsers are not removed automatically.

## Usage

Run directly with the SDK, or substitute the published executable for `dotnet run --project csharp --`:

```sh
dotnet run --project csharp -- "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/The-Greatest-Estate-Developer" --limit 3
dotnet run --project csharp -- "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/rss" --list
dotnet run --project csharp -- --chapter "https://weebcentral.com/chapters/01J76XZ3W1SDBG0SAGFG8RYXS4"
dotnet run --project csharp -- "https://mangafire.to/title/52l90-domination" --language en --limit 3
```

| Parameter | Meaning / default |
| --- | --- |
| `URL` | Series page or RSS/Atom feed; feeds contain only their listed releases |
| `--chapter URL` | Single chapter instead of a source URL |
| `-h`, `--help` | Show help |
| `-o`, `--output DIR` | Output directory: `downloads` |
| `--list` | List selected chapters without downloading images |
| `--limit N` | First N chapters after sorting; positive integer |
| `--language CODE` | MangaFire language, language code, e.g. `en` or `pt-br`; default `en` |
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

MangaFire uses the same HTTP client as WeebCentral. API signatures are calculated
in native C# using three substitution/feedback rounds over UTF-8 bytes, followed
by URL-safe Base64. No JavaScript is downloaded or interpreted, and no browser,
Node.js, Jint or background process is needed. The protocol constants are documented
in `csharp/Sites/MangaFireSigner.cs` and tested against 1,000 reference signatures
from the original site module. If MangaFire changes its signing algorithm or
requires a browser challenge, the request fails with an error and the native
signer must be updated; there is no interpreter or browser fallback. The old
`--browser-*` options have been removed.

For low-powered machines, reduce download/PDF concurrency:

```sh
manga_leech "https://mangafire.to/title/1no2v-i-became-the-chef-of-the-dragon-king" --chapter-workers 1 --pdf-workers 1 --workers 2 --delete-image-cache
```

MangaFire visits every chapter-list page, filters the chosen language, and keeps one release per chapter number, preferring an official release when available. This avoids duplicate PDF filenames. `--chapter` accepts current `/title/SLUG/chapter/ID` links. Older `/manga/` and `/read/` URLs are not supported. The common sorting and `--limit` are applied after discovery.

Three chapter downloads share eight image slots. Completed chapters enter a bounded queue of at most twice `--pdf-workers`; four PDF workers drain it while downloading continues. Sorting determines selection and scheduling, while chapters may finish in a different order. The image limit stays global as the chapter count increases. This allows requests to overlap across chapters; actual throughput depends on the server. HTTP 429/503 pauses new requests to the same host using `Retry-After` (maximum 120 seconds); each transfer has at most three attempts. The application does not handle CAPTCHA or login.

Images live under `downloads/.images/` with SHA-256 verification receipts. Downloads and PDFs use temporary `.part` files and are published only after success. Exit codes: `0` success, `1` failure, `130` Ctrl+C. Failure of one chapter does not stop others. Ctrl+C cancels queued/network work and waits for active PDF operations to observe cancellation; native image decoding/compression can delay that briefly. Running two instances into the same output directory is unsupported.

PDFs always contain title and XMP metadata. For recognized chapter titles, Kavita receives `calibre:series` and `calibreSI:series_index` (mapped by Kavita to Volume). Names follow `Series - Ch 025.pdf`, including decimal indices. Conflicting output names are rejected before downloading. The optional manifest checks generation inputs, not the bytes of an existing PDF.

JPEG streams and compatible opaque, non-interlaced PNG streams are embedded unchanged, preserving palette and bit depth. Other images, including WebP and transparent PNG, decode to 8-bit RGB with an alpha mask when needed; this can increase file size, and 16-bit transparent PNG loses bit depth. `--jpeg-quality` leaves transparency and one-bit/paletted PNG unchanged and never alters cached originals.

## Development

`csharp/` separates CLI/options, parsing, HTTP retries, image cache, bounded download pipeline and PDF writing. `csharp/Sites/IMangaSite.cs` defines chapter discovery, single-chapter metadata, image discovery and disposal. Each site has its own adapter (`WeebCentralSite`, `MangaFireSite`); register additional adapters in `SiteRegistry`. `Application` handles common sorting and limits, and `DownloadPipeline` handles cache, downloads and PDF creation. `Client` only handles HTTP transfers. `tests/MangaLeech.Tests/` contains a regression runner with a local HTTP server and independent native-signature reference vectors; tests need loopback networking and do not contact live manga sites.

```sh
dotnet build csharp/MangaLeech.csproj -c Release
dotnet run --project tests/MangaLeech.Tests -c Release
```

After publishing, also test native decoding through the Windows executable:

```powershell
dotnet run --project tests/MangaLeech.Tests -c Release -- --published build/csharp/win-x64/manga_leech.exe
```

Dependencies: HtmlAgilityPack and SkiaSharp (MIT); no paid license is needed for Release builds. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). `docs/PROJECT_REVIEW.md` describes the historical Python implementation.

Packaging checks: `python -m unittest discover -s tests/Packaging.Tests`.
