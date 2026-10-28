namespace MangaLeech.Core;
// JPEG and compatible PNG streams are embedded without re-encoding.
// Other formats use 8-bit RGB and a separate alpha mask.
sealed class PdfWriter : IDisposable
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    readonly FileStream output;
    readonly List<long> offsets = [0];
    PdfWriter(string path)
    {
        output = File.Create(path);
        Write("%PDF-1.7\n%binary\n");
    }

    public void Dispose() => output.Dispose();
    int Reserve()
    {
        offsets.Add(0);
        return offsets.Count - 1;
    }

    void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
    void Object(int id, string value)
    {
        offsets[id] = output.Position;
        Write($"{id} 0 obj\n{value}\nendobj\n");
    }

    int Stream(string dictionary, byte[] bytes)
    {
        int id = Reserve();
        offsets[id] = output.Position;
        Write($"{id} 0 obj\n<< {dictionary} /Length {bytes.Length} >>\nstream\n");
        output.Write(bytes);
        Write("\nendstream\nendobj\n");
        return id;
    }
    int Stream(string dictionary, long length, Action<Stream> writeBody)
    {
        int id = Reserve();
        offsets[id] = output.Position;
        Write($"{id} 0 obj\n<< {dictionary} /Length {length} >>\nstream\n");
        long start = output.Position;
        writeBody(output);
        if (output.Position - start != length)
            throw new IOException("Image stream length changed while writing the PDF.");
        Write("\nendstream\nendobj\n");
        return id;
    }

    static byte[] Compress(byte[] data)
    {
        using var stream = new MemoryStream();
        using (var z = new ZLibStream(stream, CompressionLevel.Optimal, true))
            z.Write(data);
        return stream.ToArray();
    }

    record Embedded(int Id, int Width, int Height);
    Embedded EmbedJpeg(string path)
    {
        using var infoStream = File.OpenRead(path);
        using var infoCodec = SKCodec.Create(infoStream) ?? throw new InvalidDataException("Unknown JPEG.");
        int w = infoCodec.Info.Width, h = infoCodec.Info.Height;
        string common = $"/Type /XObject /Subtype /Image /Width {w} /Height {h}";
        using var scan = File.OpenRead(path);
        if (scan.ReadByte() != 0xff || scan.ReadByte() != 0xd8)
            throw new InvalidDataException("Invalid JPEG header.");
        int components = 0;
        bool adobe = false;
        Span<byte> signature = stackalloc byte[5];
        while (scan.Position + 3 < scan.Length)
        {
            if (scan.ReadByte() != 0xff) continue;
            int marker;
            do { marker = scan.ReadByte(); } while (marker == 0xff);
            if (marker is -1 or 0xda or 0xd9) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
            int high = scan.ReadByte(), low = scan.ReadByte();
            if (low < 0) break;
            int len = (high << 8) | low;
            long end = scan.Position + len - 2;
            if (len < 2 || end > scan.Length) break;
            if (marker == 0xee && len >= 7)
            {
                scan.ReadExactly(signature);
                adobe |= signature.SequenceEqual("Adobe"u8);
            }
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc) && len >= 8)
            {
                scan.Position += 5;
                components = scan.ReadByte();
            }
            scan.Position = end;
        }
        string cs = components switch
        {
            1 => "/DeviceGray",
            3 => "/DeviceRGB",
            4 => "/DeviceCMYK",
            _ => throw new InvalidDataException("Unsupported JPEG color space.")
        };
        string decode = components == 4 && adobe ? "/Decode [1 0 1 0 1 0 1 0]" : "";
        int id = Stream($"{common} /BitsPerComponent 8 /ColorSpace {cs} /Filter /DCTDecode {decode}", scan.Length, destination =>
        {
            using var source = File.OpenRead(path);
            source.CopyTo(destination);
        });
        return new(id, w, h);
    }

    Embedded Embed(string path, int? quality, MemoryBudget? budget, CancellationToken ct)
    {
        bool png;
        using (var sniff = File.OpenRead(path))
        {
            Span<byte> signature = stackalloc byte[8];
            int count = sniff.Read(signature);
            if (count >= 2 && signature[0] == 0xff && signature[1] == 0xd8)
                return EmbedJpeg(path);
            png = count == 8 && signature.SequenceEqual(PngSignature);
        }

        using var infoStream = File.OpenRead(path);
        using var infoCodec = SKCodec.Create(infoStream) ?? throw new InvalidDataException("Unknown image format.");
        var info = infoCodec.Info;
        int w = info.Width, h = info.Height;
        string common = $"/Type /XObject /Subtype /Image /Width {w} /Height {h}";

        int color = -1, bits = 8, interlace = 1;
        bool transparent = false;
        byte[]? palette = null;
        long idatLength = 0;
        var idatParts = new List<(long Offset, int Length)>();
        if (png)
        {
            using var scan = File.OpenRead(path);
            scan.Position = 8;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> header = stackalloc byte[13];
            while (scan.Position + 12 <= scan.Length)
            {
                scan.ReadExactly(chunk);
                uint raw = BinaryPrimitives.ReadUInt32BigEndian(chunk[..4]);
                if (raw > int.MaxValue || raw > scan.Length - scan.Position - 4)
                    throw new InvalidDataException("Incomplete PNG chunk.");
                int len = (int)raw;
                long start = scan.Position;
                if (chunk[4..].SequenceEqual("IHDR"u8) && len == 13)
                {
                    scan.ReadExactly(header);
                    bits = header[8];
                    color = header[9];
                    interlace = header[12];
                }
                if (chunk[4..].SequenceEqual("PLTE"u8))
                {
                    if (len > 768) throw new InvalidDataException("Invalid PNG palette.");
                    palette = new byte[len];
                    scan.ReadExactly(palette);
                }
                if (chunk[4..].SequenceEqual("tRNS"u8))
                    transparent = true;
                if (chunk[4..].SequenceEqual("IDAT"u8))
                {
                    idatParts.Add((start, len));
                    idatLength += len;
                }
                scan.Position = start + len + 4;
                if (chunk[4..].SequenceEqual("IEND"u8))
                    break;
            }
        }

        bool directPng = png && !transparent && interlace == 0 && (color is 0 or 2 or 3) && idatLength > 0;
        bool tryJpeg = quality.HasValue && (!png || (color is 0 or 2 && bits == 8 && !transparent));
        using var lease = (tryJpeg || !directPng) ? budget?.Acquire(checked(16L * w * h + new FileInfo(path).Length), ct) : null;
        using SKBitmap? decoded = tryJpeg ? DecodeImage(path) : null;
        byte[]? jpeg = null;
        bool? decodedHasAlpha = null;
        if (decoded != null)
        {
            decodedHasAlpha = HasAlpha(decoded);
            if (!decodedHasAlpha.Value)
            {
                using var encoded = decoded.Encode(SKEncodedImageFormat.Jpeg, quality!.Value);
                jpeg = encoded.ToArray();
            }
        }

        if (directPng)
        {
            string cs = color == 2 ? "/DeviceRGB" : "/DeviceGray";
            if (color == 3)
            {
                if (palette == null || palette.Length == 0)
                    throw new InvalidDataException("PNG has no palette.");
                cs = $"[/Indexed /DeviceRGB {palette.Length / 3 - 1} <{Convert.ToHexString(palette)}>]";
            }

            if (jpeg != null && jpeg.Length < idatLength)
                return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", jpeg), w, h);
            int id = Stream($"{common} /BitsPerComponent {bits} /ColorSpace {cs} /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors {(color == 2 ? 3 : 1)} /BitsPerComponent {bits} /Columns {w} >>", idatLength, destination =>
            {
                using var source = File.OpenRead(path);
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    foreach (var (offset, length) in idatParts)
                    {
                        source.Position = offset;
                        int left = length;
                        while (left > 0)
                        {
                            int size = Math.Min(left, buffer.Length);
                            source.ReadExactly(buffer.AsSpan(0, size));
                            destination.Write(buffer, 0, size);
                            left -= size;
                        }
                    }
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
            });
            return new(id, w, h);
        }

        using SKBitmap? fallback = decoded == null ? DecodeImage(path) : null;
        var rgba = decoded ?? fallback!;
        byte[] rgb = new byte[checked(w * h * 3)];
        bool hasAlpha = decodedHasAlpha ?? HasAlpha(rgba);
        byte[]? alphaBytes = hasAlpha ? new byte[checked(w * h)] : null;
        CopyChannels(rgba, rgb, alphaBytes);

        byte[] lossless = Compress(rgb);
        if (jpeg != null && !hasAlpha && jpeg.Length < lossless.Length)
            return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", jpeg), w, h);
        string mask = hasAlpha ? $"/SMask {Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode", Compress(alphaBytes!))} 0 R" : "";
        return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /FlateDecode {mask}", lossless), w, h);
    }

    private static unsafe bool HasAlpha(SKBitmap bitmap)
    {
        byte* pixels = (byte*)bitmap.GetPixels();
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (pixels[y * bitmap.RowBytes + x * 4 + 3] != 255) return true;
        return false;
    }

    private static unsafe void CopyChannels(SKBitmap bitmap, byte[] rgb, byte[]? alpha)
    {
        byte* pixels = (byte*)bitmap.GetPixels();
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                byte* pixel = pixels + y * bitmap.RowBytes + x * 4;
                int index = y * bitmap.Width + x;
                rgb[index * 3] = pixel[0];
                rgb[index * 3 + 1] = pixel[1];
                rgb[index * 3 + 2] = pixel[2];
                if (alpha != null) alpha[index] = pixel[3];
            }
    }

    internal static void Make(string[] paths, string path, string title, int? quality, CancellationToken ct, string[]? digests = null, MemoryBudget? budget = null)
    {
        string part = path + ".part";
        try
        {
            using (var pdf = new PdfWriter(part))
            {
                int catalog = pdf.Reserve(), pages = pdf.Reserve(), info = pdf.Reserve();
                var (series, number) = Metadata(title);
                XNamespace x = "adobe:ns:meta/", rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#", dc = "http://purl.org/dc/elements/1.1/", calibre = "http://calibre-ebook.com/xmp-namespace", index = "http://calibre-ebook.com/xmp-namespace-series-index";
                var desc = new XElement(rdf + "Description", new XAttribute(rdf + "about", ""), new XElement(dc + "title", new XElement(rdf + "Alt", new XElement(rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), series))));
                if (number != null)
                    desc.Add(new XElement(calibre + "series", series), new XElement(index + "series_index", number));
                var xml = new XElement(x + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", x), new XAttribute(XNamespace.Xmlns + "rdf", rdf), new XAttribute(XNamespace.Xmlns + "dc", dc), new XAttribute(XNamespace.Xmlns + "calibre", calibre), new XAttribute(XNamespace.Xmlns + "calibreSI", index), new XElement(rdf + "RDF", desc));
                int metadata = pdf.Stream("/Type /Metadata /Subtype /XML", Encoding.UTF8.GetBytes(xml.ToString()));
                pdf.Object(info, $"<< /Title <FEFF{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(series))}> /Creator (Manga Leech CSharp) >>");
                var kids = new List<int>();
                var images = new Dictionary<string, Embedded>();
                for (int i = 0; i < paths.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string imagePath = paths[i];
                    string digest = digests == null ? FileHash(imagePath) : digests[i];
                    if (!images.TryGetValue(digest, out var image))
                    {
                        image = pdf.Embed(imagePath, quality, budget, ct);
                        images[digest] = image;
                    }

                    double scale = Math.Min(.75, 14000.0 / Math.Max(image.Width, image.Height));
                    string w = (image.Width * scale).ToString("0.########", CultureInfo.InvariantCulture), h = (image.Height * scale).ToString("0.########", CultureInfo.InvariantCulture);
                    int content = pdf.Stream("", Encoding.ASCII.GetBytes($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n"));
                    int page = pdf.Reserve();
                    kids.Add(page);
                    pdf.Object(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 {image.Id} 0 R >> >> /Contents {content} 0 R >>");
                }

                if (kids.Count == 0)
                    throw new InvalidDataException("Cannot create a PDF without pages.");
                pdf.Object(pages, $"<< /Type /Pages /Count {kids.Count} /Kids [{string.Join(" ", kids.Select(id => $"{id} 0 R"))}] >>");
                pdf.Object(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /Metadata {metadata} 0 R >>");
                long xref = pdf.output.Position;
                pdf.Write($"xref\n0 {pdf.offsets.Count}\n0000000000 65535 f \n");
                foreach (long offset in pdf.offsets.Skip(1))
                    pdf.Write(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
                pdf.Write($"trailer\n<< /Size {pdf.offsets.Count} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            }

            ct.ThrowIfCancellationRequested();
            File.Move(part, path, true);
        }
        finally
        {
            File.Delete(part);
        }
    }
}
