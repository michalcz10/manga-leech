namespace MangaLeech.Core;

internal static class FileUtilities
{
    internal static void Log(string value) => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {value}");
    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    internal static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    internal static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string HttpUrl(string value, string? basis = null)
    {
        var uri = basis == null ? new Uri(value, UriKind.Absolute) : new Uri(new Uri(basis), value);
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException($"Invalid HTTP(S) URL: {value}");
        return uri.AbsoluteUri;
    }

    internal static string SameHost(string value, string basis)
    {
        string url = HttpUrl(value, basis);
        if (!string.Equals(new Uri(url).Authority, new Uri(basis).Authority, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The list links to a different host.");
        return url;
    }

    internal static HtmlDocument Html(string text)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(text);
        return doc;
    }

    internal static string Text(HtmlNode? node) => WebUtility.HtmlDecode(node?.InnerText ?? "").Trim();
    internal static void AtomicText(string path, string text)
    {
        string part = path + ".part";
        try
        {
            File.WriteAllText(part, text, new UTF8Encoding(false));
            File.Move(part, path, true);
        }
        finally
        {
            File.Delete(part);
        }
    }
}
