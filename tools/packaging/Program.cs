using System.Text.Json;
using OpusMac.Packaging;

try
{
    switch (args.FirstOrDefault())
    {
        case "publish" when args.Length is >= 2 and <= 4:
            await Publication.PublishAsync(args[1], args.Length > 2 ? args[2] : "all", args.Length > 3 ? args[3] : null);
            break;
        case "fingerprint" when args.Length == 2:
            Console.WriteLine(BundleBuilder.SourceFingerprint(args[1]));
            break;
        case "verify" when args.Length == 2:
            BundleBuilder.Verify(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(new { verified = true, scope = "package hashes and architecture", macos_execution_verified = false }));
            break;
        case "archive" when args.Length == 3:
            PackageArchive.Create(args[1], args[2]);
            break;
        case "self-test" when args.Length == 2:
            PackagingChecks.Run(args[1]);
            break;
        default:
            Console.WriteLine("Commands: publish PROJECT_ROOT [all|win-x64|osx-arm64] [OUTPUT_ROOT]; fingerprint PROJECT_ROOT; verify RELEASE_DIRECTORY; archive RELEASE_DIRECTORY OUTPUT.tar.gz; self-test DISPOSABLE_DIRECTORY");
            return args.Length == 0 ? 0 : 2;
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
