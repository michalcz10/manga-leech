namespace MangaLeech.Core;

internal static class Application
{
    public static async Task<int> MainAsync(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var o = Options.Parse(args);
            if (o.Help)
            {
                Console.WriteLine(CommandHelp.Text);
                return 0;
            }

            using var client = new Client(o);
            List<Chapter> chapters;
            if (o.Chapter != null)
            {
                string url = HttpUrl(o.Chapter), html = await client.GetText(url, null, cancel.Token);
                client.Prefetched[url] = html;
                string title = Text(Html(html).DocumentNode.SelectSingleNode("//title"));
                var parts = title.Split(" | ").Select(p => p.Trim()).ToArray();
                if (parts.Length >= 3 && Label.Match(parts[0]) is { Success: true } m && m.Length == parts[0].Length)
                    title = parts[1] + " " + parts[0];
                else if (title.EndsWith(" | Weeb Central"))
                    title = title[..^15].Trim();
                if (title.Length == 0)
                    throw new InvalidDataException("Cannot determine a chapter title for PDF metadata.");
                chapters = [new Chapter(title, url)];
            }
            else
            {
                string url = HttpUrl(o.Source!);
                string path = new Uri(url).AbsolutePath.TrimEnd('/');
                chapters = path.StartsWith("/series/") && !path.EndsWith("/rss") ? await client.Series(url, cancel.Token) : Feed(await client.Get(url, null, cancel.Token), url);
                var comparer = Comparer<Chapter>.Create((a, b) => o.Newest ? CompareChapters(b, a) : CompareChapters(a, b));
                chapters = chapters.OrderBy(ch => ch, comparer).ToList();
                Log($"Source contains {chapters.Count} chapters.");
            }

            if (o.Limit.HasValue)
                chapters = chapters.Take(o.Limit.Value).ToList();
            if (o.List)
            {
                foreach (var ch in chapters)
                    Console.WriteLine($"{ch.Title}\t{ch.Url}");
                return 0;
            }

            Directory.CreateDirectory(o.Output);
            return await DownloadPipeline.Run(client, chapters, o, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Log("Cancelled. The next run can resume from cache.");
            return 130;
        }
        catch (Exception e)
        {
            Log($"Error: {e.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
