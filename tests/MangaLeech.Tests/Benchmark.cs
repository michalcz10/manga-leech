using System.Diagnostics;
using MangaLeech.Core;
using SkiaSharp;

internal static class Benchmark
{
    internal static int Run(string[] inputs)
    {
        using var scratch = new Scratch();
        string[] paths = inputs.Length == 1 && inputs[0] == "--synthetic"
            ? SyntheticImages(scratch.Path) : inputs.Select(Path.GetFullPath).ToArray();
        if (paths.Any(path => !File.Exists(path)))
            throw new FileNotFoundException("All benchmark images must exist.");
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var stopwatch = Stopwatch.StartNew();
        string[] digests = new string[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            ImageCache.ValidateImage(paths[i], new MemoryBudget(512));
            digests[i] = FileUtilities.FileHash(paths[i]);
        }
        string output = Path.Combine(scratch.Path, "benchmark.pdf");
        PdfWriter.Make(paths, output, "Benchmark Chapter 1", null, CancellationToken.None, digests, new MemoryBudget(512));
        stopwatch.Stop();
        process.Refresh();
        Console.WriteLine($"Images: {paths.Length}; input: {paths.Sum(path => new FileInfo(path).Length):N0} B; PDF: {new FileInfo(output).Length:N0} B");
        Console.WriteLine($"Elapsed: {stopwatch.Elapsed.TotalSeconds:F3} s; CPU: {(process.TotalProcessorTime - cpuBefore).TotalSeconds:F3} s; peak working set: {process.PeakWorkingSet64 / 1048576.0:F1} MiB; managed allocations: {(GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0:F1} MiB");
        return 0;
    }

    private static string[] SyntheticImages(string directory)
    {
        using var bitmap = new SKBitmap(2048, 3072);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint();
        canvas.Clear(SKColors.White);
        for (int y = 0; y < bitmap.Height; y += 48)
            for (int x = 0; x < bitmap.Width; x += 128)
            {
                paint.Color = new SKColor((byte)(x / 8), (byte)(y / 12), (byte)((x + y) / 16));
                canvas.DrawRect(x, y, 96, 36, paint);
            }
        var formats = new[] { SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png, SKEncodedImageFormat.Webp };
        var paths = new string[formats.Length];
        for (int i = 0; i < formats.Length; i++)
        {
            paths[i] = Path.Combine(directory, $"synthetic-{i}.img");
            using var data = bitmap.Encode(formats[i], 85);
            File.WriteAllBytes(paths[i], data.ToArray());
        }
        return paths;
    }
}
