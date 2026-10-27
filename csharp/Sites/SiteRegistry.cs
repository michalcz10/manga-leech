namespace MangaLeech.Core;

internal static class SiteRegistry
{
    internal static IMangaSite For(string url, Options options)
    {
        var uri = new Uri(HttpUrl(url));
        if (uri.Host.Equals("mangafire.to", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("www.mangafire.to", StringComparison.OrdinalIgnoreCase))
            return new MangaFireSite(options);
        // Preserve generic RSS/Atom and compatible reader URLs on other hosts.
        return new WeebCentralSite();
    }
}
