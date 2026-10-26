namespace MangaLeech.Core;

internal static class ImageCache
{
    internal const string CheckVersion = "csharp-skia-v2";
    internal static void ValidateImage(string path)
    {
        // Require an end marker as well as a successful full decode for JPEG.
        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadByte() == 0xff && stream.ReadByte() == 0xd8)
            {
                if (stream.Length < 4)
                    throw new InvalidDataException("Incomplete JPEG.");
                stream.Seek(-2, SeekOrigin.End);
                if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd9)
                    throw new InvalidDataException("JPEG has no EOI end marker.");
            }
        }

        using var image = DecodeImage(File.ReadAllBytes(path));
        if (image.Width < 1 || image.Height < 1)
            throw new InvalidDataException("Empty image.");
    }

    internal static bool ValidImage(string path)
    {
        try
        {
            string digest = FileHash(path), receipt = Path.ChangeExtension(path, ".verified");
            if (File.Exists(receipt) && File.ReadAllText(receipt) == CheckVersion + "\n" + digest)
                return true;
            ValidateImage(path);
            AtomicText(receipt, CheckVersion + "\n" + digest);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    internal static async Task<string[]> DownloadImages(Client client, Chapter chapter, List<string> urls, Options o, SemaphoreSlim imageSlots, CancellationToken ct)
    {
        string cache = Path.Combine(o.Output, ".images", CacheName(chapter));
        Directory.CreateDirectory(cache);
        var paths = new string[urls.Count];
        int completed = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, urls.Count), new ParallelOptions { MaxDegreeOfParallelism = o.Workers, CancellationToken = ct }, async (i, token) =>
        {
            string url = urls[i], path = Path.Combine(cache, $"{i + 1:00000}-{Hash(url)[..12]}.img");
            try
            {
                if (o.Force || !ValidImage(path))
                {
                    string part = Path.ChangeExtension(path, ".part");
                    try
                    {
                        // The image budget is shared by every active chapter, including retries.
                        await imageSlots.WaitAsync(token);
                        try
                        {
                            await using (var stream = File.Create(part))
                            {
                                await client.Transfer(url, chapter.Url, stream, token);
                            }
                            ValidateImage(part);
                            string digest = FileHash(part);
                            File.Move(part, path, true);
                            AtomicText(Path.ChangeExtension(path, ".verified"), CheckVersion + "\n" + digest);
                        }
                        finally
                        {
                            imageSlots.Release();
                        }

                    }
                    finally
                    {
                        File.Delete(part);
                    }
                }

                paths[i] = path;
                Log($"{chapter.Title}: completed {Interlocked.Increment(ref completed)}/{urls.Count} (image {i + 1})");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new IOException($"Image {i + 1}/{urls.Count} ({url}): {e.Message}", e);
            }
        });
        return paths;
    }

    // Reject partial decodes instead of accepting an image header or incomplete pixels.
    internal static SKBitmap DecodeImage(byte[] data)
    {
        using var encoded = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(encoded) ?? throw new InvalidDataException("Unknown image format.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"Incomplete or invalid image: {result}.");
        }

        return bitmap;
    }
}
