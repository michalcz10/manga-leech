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
            string url = HttpUrl(o.Chapter ?? o.Source!);
            await using var site = SiteRegistry.For(url, o);
            List<Chapter> chapters;
            if (o.Chapter != null)
                chapters = [await site.GetChapter(client, url, cancel.Token)];
            else
            {
                chapters = await site.GetChapters(client, url, cancel.Token);
                var comparer = Comparer<ChapterSortKey>.Create((a, b) => o.Newest ? CompareSortKeys(b, a) : CompareSortKeys(a, b));
                chapters = chapters.Select(ch => (Chapter: ch, Key: SortKey(ch.Title)))
                    .OrderBy(item => item.Key, comparer).Select(item => item.Chapter).ToList();
            }

            if (o.Limit.HasValue)
                chapters = chapters.Take(o.Limit.Value).ToList();
            if (o.Chapter == null)
                Log($"Selected {chapters.Count} chapters.");
            if (o.List)
            {
                foreach (var ch in chapters)
                    Console.WriteLine($"{ch.Title}\t{ch.Url}");
                return 0;
            }

            Directory.CreateDirectory(o.Output);
            return await DownloadPipeline.Run(client, chapters, o, cancel.Token, site);
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
