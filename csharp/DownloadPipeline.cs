using System.Threading.Channels;

namespace MangaLeech.Core;

internal static class DownloadPipeline
{
    internal const string PdfVersion = "csharp-skia-lossless-v2";
    private sealed record PdfJob(Chapter Chapter, string[] Paths, string[] Digests, string Output, string Manifest, string Signature);
    private sealed class ChapterImages(Chapter chapter, List<string> urls, string output, string manifest, string signature)
    {
        internal readonly Chapter Chapter = chapter;
        internal readonly List<string> Urls = urls;
        internal readonly string[] Paths = new string[urls.Count];
        internal readonly string[] Digests = new string[urls.Count];
        internal readonly string Output = output;
        internal readonly string Manifest = manifest;
        internal readonly string Signature = signature;
        internal readonly TaskCompletionSource<bool> ImagesDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Remaining = urls.Count;
        internal int Completed;
        internal int Failed;
    }
    private sealed record ImageJob(ChapterImages State, int Index);
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
        var memory = new MemoryBudget(options.MemoryLimitMiB);
        // Bound both pending images and completed chapters while allowing a slow
        // image to finish in its own worker as other chapters enter the queue.
        var imageQueue = Channel.CreateBounded<ImageJob>(new BoundedChannelOptions(options.Workers * 2) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = options.Workers == 1 });
        var pdfQueue = Channel.CreateBounded<PdfJob>(new BoundedChannelOptions(options.PdfWorkers * 2) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = options.PdfWorkers == 1 });
        var pdfTasks = Enumerable.Range(0, options.PdfWorkers).Select(_ => Task.Run(async () =>
        {
            await foreach (var job in pdfQueue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    PdfWriter.Make(job.Paths, job.Output, job.Chapter.Title, options.JpegQuality, cancellationToken, job.Digests, memory);
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
        var imageTasks = Enumerable.Range(0, options.Workers).Select(_ => Task.Run(async () =>
        {
            await foreach (var work in imageQueue.Reader.ReadAllAsync(cancellationToken))
            {
                var state = work.State;
                if (Volatile.Read(ref state.Failed) == 0)
                {
                    try
                    {
                        int index = work.Index;
                        var (path, digest) = await DownloadImage(client, state.Chapter, state.Urls[index], index, options, memory, cancellationToken);
                        state.Paths[index] = path;
                        state.Digests[index] = digest;
                        Log($"{state.Chapter.Title}: completed {Interlocked.Increment(ref state.Completed)}/{state.Urls.Count} (image {index + 1})");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        if (Interlocked.Exchange(ref state.Failed, 1) == 0)
                        {
                            Interlocked.Increment(ref failures);
                            Log($"Chapter failed for {state.Chapter.Title}: {exception.Message}");
                        }
                    }
                }

                if (Interlocked.Decrement(ref state.Remaining) == 0)
                {
                    state.ImagesDone.TrySetResult(true);
                    if (Volatile.Read(ref state.Failed) == 0)
                        await pdfQueue.Writer.WriteAsync(new PdfJob(state.Chapter, state.Paths, state.Digests, state.Output, state.Manifest, state.Signature), cancellationToken);
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
                    if (urls.Count == 0)
                        throw new InvalidDataException("The chapter contains no images.");
                    string manifest = Path.ChangeExtension(output, ".pages.sha256");
                    string signature = Hash(PdfVersion + $"\njpeg_quality={options.JpegQuality}\n" + chapter.Title + "\n" + string.Join("\n", urls));
                    if (File.Exists(output) && options.Manifest && File.Exists(manifest) && File.ReadAllText(manifest) == signature && !options.Force && !options.Rebuild)
                    {
                        Log($"Skipping: {output}");
                        return;
                    }

                    var state = new ChapterImages(chapter, urls, output, manifest, signature);
                    for (int i = 0; i < urls.Count; i++)
                        await imageQueue.Writer.WriteAsync(new ImageJob(state, i), token);
                    if (options.ChapterWorkers == 1)
                        await state.ImagesDone.Task.WaitAsync(token);
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
            imageQueue.Writer.TryComplete();
            try
            {
                await Task.WhenAll(imageTasks);
            }
            finally
            {
                pdfQueue.Writer.TryComplete();
                // Keep shared resources alive until all PDF workers stop, including on Ctrl+C.
                await Task.WhenAll(pdfTasks);
            }
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
