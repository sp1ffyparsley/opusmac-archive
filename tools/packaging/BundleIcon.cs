using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace OpusMac.Packaging;

/// <summary>A neutral, original dual-pane folder icon drawn from simple geometry; no external artwork.</summary>
internal static class BundleIcon
{
    internal static void WriteIcns(string path)
    {
        var chunks = new[] { (Type: "ic08", Size: 256), (Type: "ic09", Size: 512), (Type: "ic10", Size: 1024) }
            .Select(item => (item.Type, Data: Png(item.Size))).ToArray();
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        output.Write(Encoding.ASCII.GetBytes("icns"));
        WriteBig(output, checked(8u + (uint)chunks.Sum(c => c.Data.Length + 8)));
        foreach (var chunk in chunks)
        {
            output.Write(Encoding.ASCII.GetBytes(chunk.Type));
            WriteBig(output, (uint)chunk.Data.Length + 8);
            output.Write(chunk.Data);
        }
    }
    private static byte[] Png(int size)
    {
        using var pixels = new MemoryStream();
        for (var y = 0; y < size; y++)
        {
            pixels.WriteByte(0);
            for (var x = 0; x < size; x++)
            {
                var px = (x + .5) / size;
                var py = (y + .5) / size;
                byte r = 0, g = 0, b = 0, a = 0;
                if (Rounded(px, py, .04, .04, .96, .96, .205)) { r = 25; g = 49; b = 83; a = 255; }
                if (Rounded(px, py, .16, .24, .49, .43, .035)) { r = 97; g = 188; b = 248; }
                if (Rounded(px, py, .16, .33, .84, .79, .04)) { r = 58; g = 153; b = 229; }
                if (Rounded(px, py, .20, .40, .485, .745, .016) || Rounded(px, py, .515, .40, .80, .745, .016)) { r = 237; g = 246; b = 255; }
                if (py > .443 && py < .465 && (px > .23 && px < .455 || px > .545 && px < .77)) { r = 57; g = 112; b = 163; }
                if ((py > .52 && py < .535 || py > .582 && py < .597 || py > .644 && py < .659) &&
                    (px > .23 && px < .43 || px > .545 && px < .745)) { r = 159; g = 186; b = 208; }
                pixels.Write([r, g, b, a]);
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) pixels.WriteTo(zlib);
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)size);
        header[8] = 8; header[9] = 6;
        Chunk(png, "IHDR", header); Chunk(png, "IDAT", compressed.ToArray()); Chunk(png, "IEND", []);
        return png.ToArray();
    }
    private static bool Rounded(double x, double y, double left, double top, double right, double bottom, double radius)
    {
        if (x < left || x > right || y < top || y > bottom) return false;
        var dx = x - Math.Clamp(x, left + radius, right - radius);
        var dy = y - Math.Clamp(y, top + radius, bottom - radius);
        return dx * dx + dy * dy <= radius * radius;
    }
    private static void Chunk(Stream stream, string type, byte[] payload)
    {
        WriteBig(stream, (uint)payload.Length);
        var name = Encoding.ASCII.GetBytes(type);
        stream.Write(name); stream.Write(payload);
        var crc = 0xffffffffu;
        foreach (var value in name.Concat(payload))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        WriteBig(stream, ~crc);
    }
    private static void WriteBig(Stream stream, uint value)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data, value);
        stream.Write(data);
    }
}
