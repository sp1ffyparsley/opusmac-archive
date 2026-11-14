using System.Buffers.Binary;
using System.Reflection.PortableExecutable;

namespace OpusMac.Packaging;

internal static class BinaryInspector
{
    internal static void Validate(string path, string rid)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase) || name is "OpusMac" or "OpusMac.Checks")
        {
            if (rid != "osx-arm64" || !HasArm64MachO(path)) throw new InvalidDataException("Expected an ARM64 Mach-O payload: " + name);
        }
        else if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            if (!reader.HasMetadata && (rid != "win-x64" || reader.PEHeaders.CoffHeader.Machine != Machine.Amd64))
                throw new InvalidDataException("Native PE architecture does not match package: " + name);
        }
        else if (extension.Equals(".so", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A Linux native library cannot ship in this package: " + name);
    }

    internal static bool HasArm64MachO(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != header.Length) return false;
        var little = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (little == 0xfeedfacf) return stream.Length >= 32 && BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == 0x0100000c;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is not (0xcafebabe or 0xcafebabf)) return false;
        var count = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (count is 0 or > 64) return false;
        var entrySize = magic == 0xcafebabf ? 32 : 20;
        var tableEnd = 8UL + count * (ulong)entrySize;
        if (tableEnd > (ulong)stream.Length) return false;
        var found = false;
        Span<byte> entry = stackalloc byte[32];
        for (var index = 0; index < count; index++)
        {
            stream.Position = 8 + index * entrySize;
            stream.ReadExactly(entry[..entrySize]);
            var architecture = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var offset = entrySize == 32 ? BinaryPrimitives.ReadUInt64BigEndian(entry[8..]) : BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var size = entrySize == 32 ? BinaryPrimitives.ReadUInt64BigEndian(entry[16..]) : BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            if (offset < tableEnd || offset > (ulong)stream.Length || size < 32 || size > (ulong)stream.Length - offset) return false;
            if (architecture == 0x0100000c)
            {
                stream.Position = checked((long)offset);
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xfeedfacf ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 0x0100000c) return false;
                found = true;
            }
        }
        return found;
    }
}
