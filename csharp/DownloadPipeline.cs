using System.Threading.Channels;

namespace MangaLeech.Core;

internal static class DownloadPipeline
{
    internal const string PdfVersion = "csharp-skia-lossless-v2";
    private sealed record PdfJob(Chapter Chapter, string[] Paths, string Output, string Manifest, string Signature);
    internal static async Task<int> Run(Client client, List<Chapter> chapters, Options options, CancellationToken cancellationToken, IMangaSite? site = null)
    {
        site ??= new WeebCentralSite();
        // Validate the entire selection before any worker can write a conflicting file.
        chapters = chapters.DistinctBy(chapter => chapter.Url).ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chapter in chapters)
        {
            string name = PdfName(chapter);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"Multiple chapters have the same output name: {name}.pdf");
            }
        }

        int failures = 0;
        using var imageSlots = new SemaphoreSlim(options.Workers);
        // Backpressure bounds completed chapters awaiting PDF creation. Chapter workers
        // also wait here, so a fast connection cannot create an unlimited task backlog.
        var queue = Channel.CreateBounded<PdfJob>(new BoundedChannelOptions(options.PdfWorkers * 2) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = options.PdfWorkers == 1 });
        var pdfTasks = Enumerable.Range(0, options.PdfWorkers).Select(_ => Task.Run(async () =>
        {
            await foreach (var job in queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    PdfWriter.Make(job.Paths, job.Output, job.Chapter.Title, options.JpegQuality, cancellationToken);
                    if (options.Manifest)
                    {
                        AtomicText(job.Manifest, job.Signature);
                    }
                    else
                    {
                        File.Delete(job.Manifest);
                    }

                    // Cache is removed only after both PDF and optional manifest succeed.
                    if (options.DeleteImageCache)
                    {
                        DeleteCache(job.Paths);
                    }

                    Log($"Completed: {job.Output}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref failures);
                    Log($"PDF failed for {job.Chapter.Title}: {exception.Message}");
                }
            }
        })).ToArray();
        try
        {
            await Parallel.ForEachAsync(chapters, new ParallelOptions { MaxDegreeOfParallelism = options.ChapterWorkers, CancellationToken = cancellationToken }, async (chapter, token) =>
            {
                try
                {
                    string output = Path.Combine(options.Output, PdfName(chapter) + ".pdf");
                    if (File.Exists(output) && !options.Force && !options.Rebuild && !options.Manifest)
                    {
                        Log($"Skipping: {output}");
                        return;
                    }

                    var urls = await site.GetImages(client, chapter.Url, token);
                    string manifest = Path.ChangeExtension(output, ".pages.sha256");
                    string signature = Hash(PdfVersion + $"\njpeg_quality={options.JpegQuality}\n" + chapter.Title + "\n" + string.Join("\n", urls));
                    if (File.Exists(output) && options.Manifest && File.Exists(manifest) && File.ReadAllText(manifest) == signature && !options.Force && !options.Rebuild)
                    {
                        Log($"Skipping: {output}");
                        return;
                    }

                    var paths = await DownloadImages(client, chapter, urls, options, imageSlots, token);
                    await queue.Writer.WriteAsync(new PdfJob(chapter, paths, output, manifest, signature), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref failures);
                    Log($"Chapter failed for {chapter.Title}: {exception.Message}");
                }
            });
        }
        finally
        {
            queue.Writer.TryComplete();
            // Keep shared resources alive until all PDF workers stop, including on Ctrl+C.
            await Task.WhenAll(pdfTasks);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return failures == 0 ? 0 : 1;
    }

    private static void DeleteCache(string[] paths)
    {
        foreach (string path in paths)
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, ".verified"));
        }

        string? directory = Path.GetDirectoryName(paths[0]);
        if (directory != null && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }
}
