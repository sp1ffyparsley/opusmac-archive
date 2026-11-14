using System.Buffers.Binary;

namespace OpusMac.Packaging;

internal static class PackagingChecks
{
    internal static void Run(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Packaging checks require a new disposable directory.");
        Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, "fixture ü");
        Directory.CreateDirectory(Path.Combine(fixture, "Example.app", "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(fixture, "spaced ü file.txt"), "archive content\n");
        File.WriteAllText(Path.Combine(fixture, "Verify-Mac.command"), "#!/bin/zsh\nexit 0\n");
        var host = Path.Combine(fixture, "Example.app", "Contents", "MacOS", "OpusMac");
        var data = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x0100000c);
        File.WriteAllBytes(host, data);
        if (!BinaryInspector.HasArm64MachO(host)) throw new InvalidDataException("ARM64 thin-header recognition failed.");
        var invalid = Path.Combine(root, "x64-header");
        data[4] = 7;
        File.WriteAllBytes(invalid, data);
        if (BinaryInspector.HasArm64MachO(invalid)) throw new InvalidDataException("Architecture checker accepted x64 as ARM64.");
        var fat = new byte[60];
        BinaryPrimitives.WriteUInt32BigEndian(fat, 0xcafebabe);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(8), 0x0100000c);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(16), 28);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(20), 32);
        data.CopyTo(fat, 28);
        var misleading = Path.Combine(root, "fat-with-x64-slice");
        File.WriteAllBytes(misleading, fat);
        if (BinaryInspector.HasArm64MachO(misleading)) throw new InvalidDataException("Architecture checker trusted a misleading fat table.");
        BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(32), 0x0100000c);
        File.WriteAllBytes(misleading, fat);
        if (!BinaryInspector.HasArm64MachO(misleading)) throw new InvalidDataException("Architecture checker rejected a genuine ARM64 slice.");
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(16), 8);
        File.WriteAllBytes(misleading, fat);
        if (BinaryInspector.HasArm64MachO(misleading)) throw new InvalidDataException("Architecture checker accepted an overlapping fat table.");
        var archive = Path.Combine(root, "fixture.tar.gz");
        PackageArchive.Create(fixture, archive);
        PackageArchive.Verify(fixture, archive);
        File.AppendAllText(Path.Combine(fixture, "spaced ü file.txt"), "tampered");
        var rejected = false;
        try { PackageArchive.Verify(fixture, archive); } catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidDataException("Archive integrity checker accepted changed bytes.");
        rejected = false;
        try { BundleBuilder.ValidateRelative("../outside"); } catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidDataException("Package traversal guard failed.");
        BundleIcon.WriteIcns(Path.Combine(root, "icon.icns"));
        var icon = File.ReadAllBytes(Path.Combine(root, "icon.icns"));
        if (icon.Length < 1024 || BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(4)) != icon.Length)
            throw new InvalidDataException("Generated multi-resolution icon has an invalid container length.");
        var fingerprint = BundleBuilder.SourceFingerprint(fixture);
        var scratch = Path.Combine(fixture, ".test_tmp");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "fixture.log"), "generated diagnostic output");
        if (BundleBuilder.SourceFingerprint(fixture) != fingerprint)
            throw new InvalidDataException("Generated test fixtures changed the source fingerprint.");
        if (!BundleBuilder.EnumerateFiles(fixture).Contains(Path.Combine(scratch, "fixture.log")))
            throw new InvalidDataException("The source fingerprint exclusion leaked into payload verification.");
        File.WriteAllText(Path.Combine(root, "checks.json"), "{\"passed\":10,\"failed\":0,\"scope\":\"thin and fat architecture headers, misleading and overlapping fat-table rejection, POSIX modes, Unicode archive, integrity rejection, safe paths, icon container, source-only fixture exclusion\",\"macos_execution_verified\":false}\n");
        Console.WriteLine("Packaging checks: 10 passed, 0 failed. " + root);
    }
}
