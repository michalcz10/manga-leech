# Third-party notices

Manga Leech uses the following open-source packages. Their license texts are
included under `licenses/` and copied beside published binaries. No paid license
is required to build or distribute this application, including Release builds.

| Package | Version | License | Source |
| --- | --- | --- | --- |
| HtmlAgilityPack | 1.13.1 | MIT | https://github.com/zzzprojects/html-agility-pack |
| SkiaSharp and its Windows/Linux native assets | 3.119.4 | MIT, plus bundled native notices | https://github.com/mono/SkiaSharp |
| Microsoft.Playwright and its bundled driver | 1.56.0 | Apache-2.0, plus bundled third-party notices | https://github.com/microsoft/playwright-dotnet |

SkiaSharp includes Skia and other native components. Preserve the supplied native
third-party notices as well as the package licenses when distributing binaries.
Self-contained publications also include the Microsoft .NET runtime. The publish
target copies its license and third-party notices from the resolved runtime pack
into `licenses/dotnet/`.

Playwright's license, notice and driver dependency notices are included under
`licenses/Playwright-*`. Browser binaries are installed separately and carry
their own licenses. Keep the `.playwright` driver directory beside the application.
