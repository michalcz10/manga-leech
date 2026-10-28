namespace MangaLeech.Core;

internal static class ChapterParser
{
    internal static readonly Regex Label = new(@"\b(?:Chapter|Page|Volume|Vol|Episode|Kapitel|Act|Battle|Round|Circle|Vision)\.?\s+(\d+(?:\.\d+)?)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static Match? LastLabel(string title) => Label.Matches(title).Cast<Match>().LastOrDefault();
    internal static (string Title, string? Number) Metadata(string title)
    {
        var m = LastLabel(title);
        return m != null && title[..m.Index].Trim() is { Length: > 0 } series ? (series, m.Groups[1].Value) : (title, null);
    }

    internal static string Clean(string title)
    {
        title = Regex.Replace(title.Normalize(NormalizationForm.FormKC), "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim(' ', '.');
        return title.Length > 120 ? title[..120] : title.Length == 0 ? "chapter" : title;
    }

    internal static string CacheName(Chapter ch) => $"{Clean(ch.Title)}-{Hash(ch.Url)[..10]}";
    internal static string PdfName(Chapter ch)
    {
        var (series, number) = Metadata(ch.Title);
        if (number == null)
            return CacheName(ch);
        var parts = number.Split('.');
        string index = BigInteger.Parse(parts[0], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture).PadLeft(3, '0');
        if (parts.Length > 1)
            index += "." + parts[1];
        string kind = LastLabel(ch.Title)!.Value.StartsWith("Vol", StringComparison.OrdinalIgnoreCase) ? "Vol" : "Ch";
        return $"{Clean(series)} - {kind} {index}";
    }

    internal readonly record struct ChapterSortKey(bool HasNumber, BigInteger Whole, string Fraction);
    internal static ChapterSortKey SortKey(string title)
    {
        var match = LastLabel(title);
        if (match == null) return new(false, BigInteger.Zero, "");
        var parts = match.Groups[1].Value.Split('.');
        return new(true, BigInteger.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 ? parts[1] : "");
    }
    internal static int CompareSortKeys(ChapterSortKey x, ChapterSortKey y)
    {
        if (!x.HasNumber || !y.HasNumber)
            return x.HasNumber == y.HasNumber ? 0 : x.HasNumber ? -1 : 1;
        int c = x.Whole.CompareTo(y.Whole);
        if (c != 0)
            return c;
        int n = Math.Max(x.Fraction.Length, y.Fraction.Length);
        for (int i = 0; i < n; i++)
        {
            char a = i < x.Fraction.Length ? x.Fraction[i] : '0';
            char b = i < y.Fraction.Length ? y.Fraction[i] : '0';
            if (a != b) return a.CompareTo(b);
        }
        return 0;
    }
    internal static int CompareChapters(Chapter a, Chapter b) => CompareSortKeys(SortKey(a.Title), SortKey(b.Title));

    internal static List<Chapter> Feed(byte[] data, string basis)
    {
        using var stream = new MemoryStream(data);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader);
        var result = new List<Chapter>();
        var seen = new HashSet<string>();
        foreach (var item in root.Descendants().Where(n => n.Name.LocalName is "item" or "entry"))
        {
            string title = item.Elements().FirstOrDefault(n => n.Name.LocalName == "title")?.Value.Trim() ?? "";
            var link = item.Elements().LastOrDefault(n => n.Name.LocalName == "link" && ((string?)n.Attribute("rel") ?? "alternate") == "alternate");
            string? href = ((string?)link?.Attribute("href") ?? link?.Value)?.Trim();
            if (string.IsNullOrEmpty(href))
                continue;
            string url = HttpUrl(href, basis);
            if (seen.Add(url))
                result.Add(new Chapter(title.Length > 0 ? title : new Uri(url).Segments.Last(), url));
        }

        if (result.Count == 0)
            throw new InvalidDataException("The feed contains no chapter links.");
        return result;
    }

    internal static List<string> ImageUrls(string html, string basis)
    {
        var images = Html(html).GetElementbyId("chapter-images")?.Descendants("img") ?? Enumerable.Empty<HtmlNode>();
        var urls = images.Select(n => n.GetAttributeValue("data-src", "") is { Length: > 0 } src ? src : n.GetAttributeValue("src", "")).Where(s => s.Length > 0).Select(s => HttpUrl(WebUtility.HtmlDecode(s), basis)).Distinct().ToList();
        if (urls.Count == 0)
            throw new InvalidDataException("No images found in #chapter-images.");
        return urls;
    }
}
