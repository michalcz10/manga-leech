# Third-party notices

Manga Leech uses the following open-source packages. Their license texts are
included under `licenses/` and copied beside published binaries. No paid license
is required to build or distribute this application, including Release builds.

| Package | Version | License | Source |
| --- | --- | --- | --- |
| HtmlAgilityPack | 1.13.1 | MIT | https://github.com/zzzprojects/html-agility-pack |
| SkiaSharp and its Windows/Linux native assets | 3.119.4 | MIT, plus bundled native notices | https://github.com/mono/SkiaSharp |
| Jint | 4.17.0 | BSD-2-Clause | https://github.com/sebastienros/jint |
| Acornima (Jint dependency) | 1.7.0 | BSD-3-Clause | https://github.com/adams85/acornima |

SkiaSharp includes Skia and other native components. Preserve the supplied native
third-party notices as well as the package licenses when distributing binaries.
Self-contained publications also include the Microsoft .NET runtime. The publish
target copies its license and third-party notices from the resolved runtime pack
into `licenses/dotnet/`.

Jint and Acornima license texts are included under `licenses/`. MangaFire's signing
module is fetched from the website at runtime; it is not bundled with Manga Leech.
