namespace MangaLeech.Core;

internal static class CommandHelp
{
    internal const string Text = """
        Manga Leech (.NET 10) - one PDF per chapter from a series or RSS/Atom feed.
        manga_leech URL [options]
        manga_leech --chapter URL [options]
          -h, --help             Show help
          -o, --output DIR       Output directory (downloads)
          --list                 List chapters without downloading images
          --limit N              First N chapters after sorting
          --oldest-first         Ascending chapter number (default)
          --newest-first         Descending chapter number
          --workers N            Global concurrent image downloads (8)
          --chapter-workers N    Concurrent chapters downloading images (3)
          --pdf-workers N        Concurrent PDF workers (4)
          --timeout SECONDS      Total timeout per transfer attempt (60)
          --delay SECONDS        Delay before each request, per worker (0.5)
          --user-agent VALUE     HTTP User-Agent (MangaLeech.CSharp/0.2)
          --force                Download images and replace PDFs again
          --rebuild              Rebuild PDFs, reusing verified image cache
          --write-manifest       Write/check optional .pages.sha256 signatures
          --delete-image-cache   Delete used cached images after successful PDF creation
          --jpeg-quality 1-95    Optional lossy compression; original JPEGs stay unchanged
        Worker counts: 1-1024. Use --chapter-workers 1 for sequential chapters.
        --pdf-tasks-per-child is unsupported: PDF workers use threads.
        """;
}
