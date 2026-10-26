namespace MangaLeech.Core;
// JPEG and compatible PNG streams are embedded without re-encoding.
// Other formats use 8-bit RGB and a separate alpha mask.
sealed class PdfWriter : IDisposable
{
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

    static byte[] Compress(byte[] data)
    {
        using var stream = new MemoryStream();
        using (var z = new ZLibStream(stream, CompressionLevel.Optimal, true))
            z.Write(data);
        return stream.ToArray();
    }

    record Embedded(int Id, int Width, int Height);
    Embedded Embed(string path, int? quality)
    {
        byte[] data = File.ReadAllBytes(path);
        using var encodedImage = SKData.CreateCopy(data);
        using var infoCodec = SKCodec.Create(encodedImage) ?? throw new InvalidDataException("Unknown image format.");
        var info = infoCodec.Info;
        int w = info.Width, h = info.Height;
        string common = $"/Type /XObject /Subtype /Image /Width {w} /Height {h}";
        if (data.Length > 2 && data[0] == 0xff && data[1] == 0xd8)
        {
            // Preserve the original JPEG stream and invert Adobe CMYK components.
            int components = 0;
            bool adobe = false;
            for (int p = 2; p + 3 < data.Length;)
            {
                if (data[p++] != 0xff)
                    continue;
                while (p < data.Length && data[p] == 0xff)
                    p++;
                if (p >= data.Length)
                    break;
                byte marker = data[p++];
                if (marker is 0xda or 0xd9)
                    break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd8)
                    continue;
                if (p + 2 > data.Length)
                    break;
                int len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                if (len < 2 || p + len > data.Length)
                    break;
                if (marker == 0xee && len >= 7 && Encoding.ASCII.GetString(data, p + 2, 5) == "Adobe")
                    adobe = true;
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc) && len >= 8)
                    components = data[p + 7];
                p += len;
            }

            string cs = components switch
            {
                1 => "/DeviceGray",
                3 => "/DeviceRGB",
                4 => "/DeviceCMYK",
                _ => throw new InvalidDataException("Unsupported JPEG color space.")
            };
            string decode = components == 4 && adobe ? "/Decode [1 0 1 0 1 0 1 0]" : "";
            return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace {cs} /Filter /DCTDecode {decode}", data), w, h);
        }

        // Use optional JPEG compression only when smaller than the lossless stream.
        byte[]? jpeg = null;
        bool png = data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        int color = -1, bits = 8, interlace = 1;
        bool transparent = false;
        byte[]? palette = null;
        using var idat = new MemoryStream();
        if (png)
        {
            for (int p = 8; p + 12 <= data.Length;)
            {
                uint raw = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                if (raw > int.MaxValue || raw > data.Length - p - 12)
                    throw new InvalidDataException("Incomplete PNG chunk.");
                int len = (int)raw;
                string kind = Encoding.ASCII.GetString(data, p + 4, 4);
                int start = p + 8;
                if (kind == "IHDR" && len == 13)
                {
                    bits = data[start + 8];
                    color = data[start + 9];
                    interlace = data[start + 12];
                }

                if (kind == "PLTE")
                    palette = data.AsSpan(start, len).ToArray();
                if (kind == "tRNS")
                    transparent = true;
                if (kind == "IDAT")
                    idat.Write(data, start, len);
                p += len + 12;
                if (kind == "IEND")
                    break;
            }
        }

        if (quality.HasValue && (!png || (color is 0 or 2 && bits == 8 && !transparent)))
        {
            using var image = DecodeImage(data);
            bool alpha = false;
            foreach (var pixel in image.Pixels)
                if (pixel.Alpha != 255)
                {
                    alpha = true;
                    break;
                }

            if (!alpha)
            {
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality.Value);
                jpeg = encoded.ToArray();
            }
        }

        if (png && !transparent && interlace == 0 && color is 0 or 2 or 3 && idat.Length > 0)
        {
            string cs = color == 2 ? "/DeviceRGB" : "/DeviceGray";
            if (color == 3)
            {
                if (palette == null || palette.Length == 0)
                    throw new InvalidDataException("PNG has no palette.");
                cs = $"[/Indexed /DeviceRGB {palette.Length / 3 - 1} <{Convert.ToHexString(palette)}>]";
            }

            byte[] compressed = idat.ToArray();
            if (jpeg != null && jpeg.Length < compressed.Length)
                return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", jpeg), w, h);
            return new(Stream($"{common} /BitsPerComponent {bits} /ColorSpace {cs} /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors {(color == 2 ? 3 : 1)} /BitsPerComponent {bits} /Columns {w} >>", compressed), w, h);
        }

        using var rgba = DecodeImage(data);
        byte[] rgb = new byte[checked(w * h * 3)];
        byte[] alphaBytes = new byte[checked(w * h)];
        bool hasAlpha = false;
        var pixels = rgba.Pixels;
        for (int p = 0; p < pixels.Length; p++)
        {
            var pixel = pixels[p];
            rgb[p * 3] = pixel.Red;
            rgb[p * 3 + 1] = pixel.Green;
            rgb[p * 3 + 2] = pixel.Blue;
            alphaBytes[p] = pixel.Alpha;
            hasAlpha |= pixel.Alpha != 255;
        }

        byte[] lossless = Compress(rgb);
        if (jpeg != null && !hasAlpha && jpeg.Length < lossless.Length)
            return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", jpeg), w, h);
        string mask = hasAlpha ? $"/SMask {Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode", Compress(alphaBytes))} 0 R" : "";
        return new(Stream($"{common} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /FlateDecode {mask}", lossless), w, h);
    }

    internal static void Make(string[] paths, string path, string title, int? quality, CancellationToken ct)
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
                foreach (string imagePath in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    string digest = FileHash(imagePath);
                    if (!images.TryGetValue(digest, out var image))
                    {
                        image = pdf.Embed(imagePath, quality);
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
