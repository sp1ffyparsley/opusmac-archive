using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace OpusMac.Packaging;

internal static class BundleBuilder
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "artifacts", ".git", ".vs", ".idea", "TestResults", ".test_tmp" };
    private const string AppName = "Directory Opus Mac.app";

    internal static void Create(string projectRoot, string rid, string appPublish, string checksPublish, string output, string sourceFingerprint)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Package destination already exists: " + output);
        Directory.CreateDirectory(output);
        var payload = rid == "osx-arm64" ? Path.Combine(output, AppName, "Contents", "MacOS") : Path.Combine(output, "Directory Opus Mac");
        Directory.CreateDirectory(payload);
        CopyTree(appPublish, payload);
        CopyTree(checksPublish, payload);
        if (rid == "osx-arm64")
        {
            var contents = Path.GetDirectoryName(payload)!;
            var resources = Path.Combine(contents, "Resources");
            Directory.CreateDirectory(resources);
            WriteInfoPlist(Path.Combine(contents, "Info.plist"));
            BundleIcon.WriteIcns(Path.Combine(resources, "OpusMac.icns"));
            foreach (var name in new[] { "Verify-Mac.command", "Sign-Mac.command", "MAC-ACCEPTANCE.md", "OpusMac.entitlements" })
            {
                File.Copy(Path.Combine(projectRoot, "tools", "packaging", name), Path.Combine(output, name));
                if (name.EndsWith(".command", StringComparison.Ordinal) || name == "OpusMac.entitlements")
                    ValidateUnixText(Path.Combine(output, name));
            }
        }
        else File.Copy(Path.Combine(projectRoot, "tools", "packaging", "Verify-Windows.ps1"), Path.Combine(output, "Verify-Windows.ps1"));
        File.WriteAllText(Path.Combine(output, "README.txt"),
            rid == "osx-arm64"
                ? "Directory Opus Mac — unofficial personal file manager\r\n\r\nThis is a self-contained Apple Silicon application. No Python or .NET SDK is required.\r\nExtract the tar.gz with Archive Utility and keep the package together for verification and signing.\r\nRead MAC-ACCEPTANCE.md first. Run Verify-Mac.command from Terminal to check this exact package and execute disposable native tests.\r\nAfter signing and acceptance, copy Directory Opus Mac.app to Applications; retain the extracted package and receipts.\r\nThe Windows build verifies packaging; it does not claim Mac execution or complete Directory Opus parity.\r\nThis independent implementation is not affiliated with GPSoftware. See LICENSE.txt for the project's source license.\r\n"
                : "Directory Opus Mac — unofficial Windows qualification build\r\n\r\nRun Directory Opus Mac\\OpusMac.exe. No Python or .NET installation is required.\r\nVerify-Windows.ps1 checks hashes and runs disposable packaged tests. Quick Look is macOS-only.\r\nThis independent application does not alter Explorer, Finder defaults, or the installed Directory Opus.\r\nNot affiliated with GPSoftware. See LICENSE.txt for the project's source license.\r\n",
            new UTF8Encoding(false));
        // Include the project's own license with distributed executables, alongside
        // the existing license notices for third-party package dependencies.
        var projectLicense = Path.Combine(projectRoot, "LICENSE");
        if (File.Exists(projectLicense))
            File.Copy(projectLicense, Path.Combine(output, "LICENSE.txt"));
        CopyLicenses(projectRoot, output);
        ValidatePayload(payload, rid);
        var files = Inventory(output);
        var manifest = new Manifest("opusmac_release_v1", rid, "0.1.0", sourceFingerprint,
            RuntimeInformation.OSDescription, DateTimeOffset.UtcNow, true, false, files);
        File.WriteAllText(Path.Combine(output, "release-manifest.json"), JsonSerializer.Serialize(manifest, Json) + "\n", new UTF8Encoding(false));
        WriteChecksumList(output);
        Verify(output);
    }

    internal static void Verify(string root)
    {
        root = Path.GetFullPath(root);
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(root, "release-manifest.json")), Json)
            ?? throw new InvalidDataException("Package manifest is missing.");
        if (manifest.Schema != "opusmac_release_v1" || manifest.Rid is not ("win-x64" or "osx-arm64") || !manifest.SelfContained)
            throw new InvalidDataException("Package identity is unsupported.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ValidateRelative(file.Path);
            if (!names.Add(file.Path)) throw new InvalidDataException("Duplicate package path: " + file.Path);
            var path = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var information = new FileInfo(path);
            if (!information.Exists || information.Attributes.HasFlag(FileAttributes.ReparsePoint) || information.Length != file.Length || Hash(path) != file.Sha256)
                throw new InvalidDataException("Package integrity mismatch: " + file.Path);
        }
        var observed = Inventory(root).Where(f => f.Path is not ("release-manifest.json" or "SHA256SUMS")).Select(f => f.Path);
        if (!names.SetEquals(observed)) throw new InvalidDataException("Unexpected or missing package files.");
        var payload = manifest.Rid == "osx-arm64" ? Path.Combine(root, AppName, "Contents", "MacOS") : Path.Combine(root, "Directory Opus Mac");
        ValidatePayload(payload, manifest.Rid);
        if (manifest.Rid == "osx-arm64")
        {
            foreach (var script in new[] { "Verify-Mac.command", "Sign-Mac.command", "OpusMac.entitlements" })
                ValidateUnixText(Path.Combine(root, script));
            var document = XDocument.Load(Path.Combine(root, AppName, "Contents", "Info.plist"));
            var keys = document.Descendants("key").ToDictionary(k => k.Value, k => k.NextNode is XElement value ? value.Value : string.Empty);
            if (keys.GetValueOrDefault("CFBundleExecutable") != "OpusMac" || keys.GetValueOrDefault("CFBundleIdentifier") != "io.github.sp1ffyparsley.opusmac")
                throw new InvalidDataException("Mac bundle identity does not match its executable.");
        }
    }

    internal static void ValidateUnixText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) || bytes.Contains((byte)'\r'))
            throw new InvalidDataException("Mac script/config must be UTF-8 without BOM and use LF endings: " + Path.GetFileName(path));
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (path.EndsWith(".command", StringComparison.Ordinal) && !text.StartsWith("#!/bin/zsh\n", StringComparison.Ordinal))
            throw new InvalidDataException("Mac script lacks its zsh interpreter line: " + Path.GetFileName(path));
    }

    internal static List<PayloadFile> Inventory(string root) => EnumerateFiles(root).Select(path =>
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        ValidateRelative(relative);
        return new PayloadFile(relative, new FileInfo(path).Length, Hash(path), IsExecutable(relative));
    }).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

    internal static string SourceFingerprint(string root)
    {
        root = Path.GetFullPath(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateFiles(root, ignoreBuildOutputs: true).OrderBy(p => p, StringComparer.Ordinal))
        {
            var row = Path.GetRelativePath(root, file).Replace('\\', '/') + "\t" + Hash(file) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(row));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static IEnumerable<string> EnumerateFiles(string root, bool ignoreBuildOutputs = false)
    {
        foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            if (ignoreBuildOutputs && item is DirectoryInfo && Ignored.Contains(item.Name)) continue;
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Package input contains a symbolic link or reparse point: " + item.FullName);
            if (item is DirectoryInfo) { foreach (var file in EnumerateFiles(item.FullName, ignoreBuildOutputs)) yield return file; }
            else yield return item.FullName;
        }
    }

    internal static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    internal static bool IsExecutable(string relative) =>
        relative.EndsWith(".command", StringComparison.OrdinalIgnoreCase) ||
        relative.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(relative) is "OpusMac" or "OpusMac.Checks";

    internal static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains('\n') || path.Contains('\r') || path.Contains('\0') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Unsafe package path: " + path);
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                if (Hash(file) != Hash(target)) throw new InvalidDataException("App and checks contain different versions of shared dependency: " + Path.GetFileName(file));
                continue;
            }
            File.Copy(file, target);
        }
    }

    private static void ValidatePayload(string payload, string rid)
    {
        var suffix = rid == "win-x64" ? ".exe" : string.Empty;
        foreach (var name in new[] { "OpusMac" + suffix, "OpusMac.Checks" + suffix, "OpusMac.dll", "OpusMac.Core.dll", "OpusMac.runtimeconfig.json", "OpusMac.Checks.runtimeconfig.json" })
            if (!File.Exists(Path.Combine(payload, name))) throw new InvalidDataException("Required package file missing: " + name);
        var required = rid == "osx-arm64"
            ? new[] { "libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib", "libcoreclr.dylib", "libhostfxr.dylib", "libhostpolicy.dylib" }
            : new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll" };
        foreach (var name in required)
            if (!File.Exists(Path.Combine(payload, name))) throw new InvalidDataException("Native runtime missing: " + name);
        foreach (var file in EnumerateFiles(payload))
        {
            if (Path.GetExtension(file).Equals(".py", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).StartsWith("python", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The independent application cannot contain Python payloads.");
            BinaryInspector.Validate(file, rid);
        }
    }

    private static void WriteChecksumList(string root)
    {
        var lines = Inventory(root).Where(f => f.Path != "SHA256SUMS").Select(f => f.Sha256 + "  " + f.Path);
        File.WriteAllText(Path.Combine(root, "SHA256SUMS"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    private static void CopyLicenses(string projectRoot, string output)
    {
        var assetPath = Path.Combine(projectRoot, "src", "OpusMac.App", "obj", "project.assets.json");
        using var document = JsonDocument.Parse(File.ReadAllText(assetPath));
        var packageRoots = document.RootElement.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToArray();
        var notices = Path.Combine(output, "ThirdPartyLicenses");
        Directory.CreateDirectory(notices);
        foreach (var library in document.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
            var relative = library.Value.GetProperty("path").GetString()!;
            var folder = packageRoots.Select(p => Path.Combine(p, relative)).FirstOrDefault(Directory.Exists)
                ?? throw new DirectoryNotFoundException("A restored package license source is missing: " + library.Name);
            var destination = Path.Combine(notices, library.Name.Replace('/', '-'));
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Contains("license", StringComparison.OrdinalIgnoreCase) || name.Contains("notice", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, Path.Combine(destination, name), false);
            }
        }
    }

    private static void WriteInfoPlist(string path)
    {
        var dictionary = new XElement("dict",
            Pair("CFBundleName", "Directory Opus Mac"), Pair("CFBundleDisplayName", "Directory Opus Mac"),
            Pair("CFBundleExecutable", "OpusMac"), Pair("CFBundleIdentifier", "io.github.sp1ffyparsley.opusmac"),
            Pair("CFBundleInfoDictionaryVersion", "6.0"), Pair("CFBundlePackageType", "APPL"),
            Pair("CFBundleShortVersionString", "0.1.0"), Pair("CFBundleVersion", "1"), Pair("CFBundleIconFile", "OpusMac"),
            Pair("LSMinimumSystemVersion", "13.0"), Pair("LSApplicationCategoryType", "public.app-category.utilities"),
            new XElement("key", "NSHighResolutionCapable"), new XElement("true"),
            Pair("NSDocumentsFolderUsageDescription", "Browse and manage files in folders you choose."),
            Pair("NSDesktopFolderUsageDescription", "Browse and manage files on your Desktop."),
            Pair("NSDownloadsFolderUsageDescription", "Browse and manage your downloaded files."),
            Pair("NSRemovableVolumesUsageDescription", "Browse and manage files on drives you choose."),
            Pair("NSNetworkVolumesUsageDescription", "Browse and manage folders on servers you choose."),
            new XElement("key", "CFBundleDocumentTypes"), new XElement("array", new XElement("dict",
                Pair("CFBundleTypeName", "Folder"), Pair("CFBundleTypeRole", "Editor"), Pair("LSHandlerRank", "Alternate"),
                new XElement("key", "LSItemContentTypes"), new XElement("array", new XElement("string", "public.folder")))));
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));
        document.Save(path);
    }
    private static object[] Pair(string key, string value) => [new XElement("key", key), new XElement("string", value)];

    internal sealed record PayloadFile(string Path, long Length, string Sha256, bool Executable);
    internal sealed record Manifest(string Schema, string Rid, string Version, string SourceFingerprint, string BuildHost,
        DateTimeOffset CreatedUtc, bool SelfContained, bool MacosRuntimeVerified, List<PayloadFile> Files);
}
