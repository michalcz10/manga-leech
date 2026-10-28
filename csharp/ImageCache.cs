namespace MangaLeech.Core;

internal static class ImageCache
{
    internal const string CheckVersion = "csharp-skia-v2";
    internal static void ValidateImage(string path, MemoryBudget? budget = null, CancellationToken ct = default)
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

        using var input = File.OpenRead(path);
        using var codec = SKCodec.Create(input) ?? throw new InvalidDataException("Unknown image format.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var lease = budget?.Acquire(checked((long)info.Width * info.Height * 5), ct);
        using var image = new SKBitmap(info);
        var result = codec.GetPixels(info, image.GetPixels());
        if (result != SKCodecResult.Success)
            throw new InvalidDataException($"Incomplete or invalid image: {result}.");
        if (image.Width < 1 || image.Height < 1)
            throw new InvalidDataException("Empty image.");
    }

    internal static bool ValidImage(string path) => TryGetDigest(path, out _);

    internal static bool TryGetDigest(string path, out string digest, MemoryBudget? budget = null, CancellationToken ct = default)
    {
        digest = "";
        try
        {
            digest = FileHash(path);
            string receipt = Path.ChangeExtension(path, ".verified");
            if (File.Exists(receipt) && File.ReadAllText(receipt) == CheckVersion + "\n" + digest)
                return true;
            ValidateImage(path, budget, ct);
            AtomicText(receipt, CheckVersion + "\n" + digest);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            digest = "";
            return false;
        }
    }

    internal static async Task<(string Path, string Digest)> DownloadImage(Client client, Chapter chapter, string url, int index, Options o, MemoryBudget budget, CancellationToken ct)
    {
        string cache = Path.Combine(o.Output, ".images", CacheName(chapter));
        Directory.CreateDirectory(cache);
        string path = Path.Combine(cache, $"{index + 1:00000}-{Hash(url)[..12]}.img");
        try
        {
            string digest;
            if (o.Force || !TryGetDigest(path, out digest, budget, ct))
            {
                string part = Path.ChangeExtension(path, ".part");
                try
                {
                    await using (var stream = File.Create(part))
                    {
                        digest = await client.Transfer(url, chapter.Url, stream, ct, calculateHash: true) ?? throw new InvalidDataException("Missing image hash.");
                    }
                    ValidateImage(part, budget, ct);
                    File.Move(part, path, true);
                    AtomicText(Path.ChangeExtension(path, ".verified"), CheckVersion + "\n" + digest);
                }
                finally
                {
                    File.Delete(part);
                }
            }

            return (path, digest);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new IOException($"Image {index + 1} ({url}): {e.Message}", e);
        }
    }

    // Reject partial decodes instead of accepting an image header or incomplete pixels.
    internal static SKBitmap DecodeImage(string path)
    {
        using var input = File.OpenRead(path);
        using var codec = SKCodec.Create(input) ?? throw new InvalidDataException("Unknown image format.");
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
