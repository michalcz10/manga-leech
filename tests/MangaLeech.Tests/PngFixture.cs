using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

internal static class PngFixture
{
    // Construct tiny PNGs independently of Skia's encoder so low-bit and 16-bit
    // preservation tests do not accidentally exercise only its 8-bit output.
    internal static byte[] Make(byte bits, byte color, byte[] row, byte[]? palette = null)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 2);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = bits;
        header[9] = color;
        Chunk(output, "IHDR", header);
        if (palette != null) Chunk(output, "PLTE", palette);
        using var pixels = new MemoryStream();
        using (var compression = new ZLibStream(pixels, CompressionLevel.Optimal, true))
        {
            compression.WriteByte(0);
            compression.Write(row);
        }
        Chunk(output, "IDAT", pixels.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        byte[] size = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, data.Length);
        output.Write(size);
        byte[] name = Encoding.ASCII.GetBytes(type);
        output.Write(name);
        output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in name.Concat(data))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(size, ~crc);
        output.Write(size);
    }
}
