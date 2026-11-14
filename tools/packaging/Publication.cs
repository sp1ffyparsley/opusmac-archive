using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OpusMac.Packaging;

internal static class Publication
{
    internal static async Task PublishAsync(string root, string runtime, string? outputRoot)
    {
        root = Path.GetFullPath(root);
        var app = Path.Combine(root, "src", "OpusMac.App", "OpusMac.App.csproj");
        var checks = Path.Combine(root, "tests", "OpusMac.Checks", "OpusMac.Checks.csproj");
        if (!File.Exists(app) || !File.Exists(checks)) throw new DirectoryNotFoundException("Choose the Directory Opus Mac project root.");
        string[] runtimes = runtime switch { "all" => ["win-x64", "osx-arm64"], "win-x64" or "osx-arm64" => [runtime], _ => throw new ArgumentException("Runtime must be all, win-x64, or osx-arm64.") };
        outputRoot = Path.GetFullPath(outputRoot ?? Path.Combine(root, "artifacts", "releases"));
        Directory.CreateDirectory(outputRoot);
        var releaseId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8];
        var stage = Path.Combine(outputRoot, ".stage-" + releaseId);
        var final = Path.Combine(outputRoot, releaseId);
        if (Directory.Exists(stage) || Directory.Exists(final)) throw new IOException("Release path already exists.");
        Directory.CreateDirectory(stage);
        var logs = Path.Combine(stage, "verification");
        Directory.CreateDirectory(logs);
        try
        {
            // Keep the ordinary development graph independent from publication RIDs.
            // The first publication attempt populated these default files through dotnet restore -r.
            foreach (var project in new[] { app, checks })
                await RunAsync("dotnet", ["restore", project, "--ignore-failed-sources",
                    "-p:RuntimeIdentifier=", "-p:RuntimeIdentifiers="], root,
                    Path.Combine(logs, Path.GetFileNameWithoutExtension(project) + "-portable-restore.txt"), TimeSpan.FromMinutes(8));
            // Each RID gets its own checked-in lock graph. Once present, a changed graph fails locked restore.
            foreach (var rid in runtimes)
                foreach (var project in new[] { app, checks })
                {
                    // dotnet restore -r sets RuntimeIdentifiers; it does not reliably set the
                    // singular property Directory.Build.props uses to select its lock filename.
                    var arguments = new List<string> { "restore", project, "-p:RuntimeIdentifier=" + rid, "-p:RuntimeIdentifiers=" + rid,
                        "--ignore-failed-sources", "-p:RuntimeFrameworkVersion=8.0.29", "-p:SelfContained=true" };
                    if (File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages." + rid + ".lock.json"))) arguments.Add("--locked-mode");
                    await RunAsync("dotnet", arguments, root, Path.Combine(logs, Path.GetFileNameWithoutExtension(project) + "-" + rid + "-restore.txt"), TimeSpan.FromMinutes(8));
                }
            var fingerprint = BundleBuilder.SourceFingerprint(root);
            foreach (var rid in runtimes)
            {
                var appPublish = Path.Combine(stage, ".publish-" + rid);
                var checksPublish = Path.Combine(stage, ".checks-" + rid);
                foreach (var (project, output) in new[] { (app, appPublish), (checks, checksPublish) })
                {
                    // project.assets.json is shared across RIDs. Restore its already-locked graph
                    // immediately before publishing so an earlier RID cannot use the last RID's assets.
                    await RunAsync("dotnet", ["restore", project, "-p:RuntimeIdentifier=" + rid, "-p:RuntimeIdentifiers=" + rid, "--locked-mode", "--ignore-failed-sources",
                        "-p:RuntimeFrameworkVersion=8.0.29", "-p:SelfContained=true"], root,
                        Path.Combine(logs, Path.GetFileNameWithoutExtension(project) + "-" + rid + "-selected-assets.txt"), TimeSpan.FromMinutes(8));
                    await RunAsync("dotnet", ["publish", project, "-c", "Release", "-r", rid, "--self-contained", "true", "--no-restore", "-o", output,
                        "-p:RuntimeIdentifiers=" + rid,
                        "-p:RuntimeFrameworkVersion=8.0.29", "-p:Version=0.1.0", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-p:PublishReadyToRun=false",
                        "-p:DebugType=embedded", "-p:ContinuousIntegrationBuild=true"], root,
                        Path.Combine(logs, Path.GetFileNameWithoutExtension(project) + "-" + rid + "-publish.txt"), TimeSpan.FromMinutes(10));
                }
                var canExecute = rid == "win-x64" && OperatingSystem.IsWindows() || rid == "osx-arm64" && OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
                if (canExecute)
                {
                    var suffix = rid == "win-x64" ? ".exe" : string.Empty;
                    var proof = Path.Combine(logs, rid);
                    Directory.CreateDirectory(proof);
                    await RunAsync(Path.Combine(checksPublish, "OpusMac.Checks" + suffix), [Path.Combine(proof, "checks")], root,
                        Path.Combine(proof, "checks-output.txt"), TimeSpan.FromMinutes(5));
                    await RunAsync(Path.Combine(appPublish, "OpusMac" + suffix), ["--self-test", "--state-dir", Path.Combine(proof, "app-state"), "--render-check", Path.Combine(proof, "ui-render")], root,
                        Path.Combine(proof, "app-self-test.txt"), TimeSpan.FromMinutes(2));
                }
                File.WriteAllText(Path.Combine(logs, rid + "-execution.json"), JsonSerializer.Serialize(new
                {
                    runtime = rid, packaged_checks_executed = canExecute, packaged_app_self_test_executed = canExecute,
                    macos_desktop_integration_verified = false, complete_directory_opus_parity_claimed = false
                }, BundleBuilder.Json) + "\n");
                if (BundleBuilder.SourceFingerprint(root) != fingerprint)
                    throw new IOException("Source changed during publication. The partial release was retained for review; no release was promoted.");
                var bundle = Path.Combine(stage, rid);
                BundleBuilder.Create(root, rid, appPublish, checksPublish, bundle, fingerprint);
                PackageArchive.Create(bundle, Path.Combine(stage, "Directory-Opus-Mac-" + rid + ".tar.gz"));
                // These exact paths were created by this publication; payload is now archived and verified.
                Directory.Delete(appPublish, true);
                Directory.Delete(checksPublish, true);
            }
            if (BundleBuilder.SourceFingerprint(root) != fingerprint)
                throw new IOException("Source changed before release promotion; the staged result remains unpromoted.");
            Directory.Move(stage, final);
            var current = Path.Combine(outputRoot, "current.json");
            var temporary = current + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { schema = "opusmac_release_pointer_v1", release = final, runtimes, source_fingerprint = fingerprint, utc = DateTimeOffset.UtcNow }, BundleBuilder.Json));
            File.Move(temporary, current, true);
            Console.WriteLine(JsonSerializer.Serialize(new { published = true, release = final, runtimes, macos_desktop_integration_verified = false }, BundleBuilder.Json));
        }
        catch
        {
            Console.Error.WriteLine("Publication stopped. Existing releases are unchanged. Diagnostic stage: " + stage);
            throw;
        }
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, string workdir, string logPath, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workdir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(timeout);
        if (!process.Start()) throw new IOException("Build helper could not start: " + executable);
        var output = ReadAsync(process.StandardOutput, deadline.Token);
        var error = ReadAsync(process.StandardError, deadline.Token);
        try
        {
            var pending = new List<Task> { process.WaitForExitAsync(deadline.Token), output, error };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
            var text = (await output) + "\n" + (await error);
            await File.WriteAllTextAsync(logPath, text, new UTF8Encoding(false));
            Console.WriteLine(Path.GetFileName(executable) + " " + arguments[0] + ": exit " + process.ExitCode + "; log " + logPath);
            if (process.ExitCode != 0) throw new IOException("Publication check failed: " + logPath);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            if (output.IsCompletedSuccessfully || error.IsCompletedSuccessfully)
                await File.WriteAllTextAsync(logPath, (output.IsCompletedSuccessfully ? output.Result : "") + "\n" + (error.IsCompletedSuccessfully ? error.Result : ""));
            throw;
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (text.Length + read > 2 * 1024 * 1024) throw new IOException("Build helper exceeded its diagnostic output limit.");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
}
