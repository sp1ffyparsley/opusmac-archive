using System.Diagnostics;
using OpusMac.Core;
using OpusMac.Core.Operations;
using OpusMac.Core.Platform;

namespace OpusMac.Checks.Suites;

public sealed class ReparseContractChecks : ICheckSuite
{
    public async Task RunAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Windows junction copy fails closed before symbolic-link creation", async () =>
        {
            if (!OperatingSystem.IsWindows()) CheckRunner.NotVerified("Windows-only junction contract.");
            var root = Directory.CreateDirectory(Path.Combine(sandbox, "junction-copy")).FullName;
            var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            await File.WriteAllTextAsync(Path.Combine(target, "sentinel.txt"), "target remains unchanged");
            var junction = Path.Combine(root, "source-junction");
            var destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("cmd.exe did not start");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            CheckRunner.Equal(0, process.ExitCode, $"mklink /J failed: {stdout} {stderr}");

            await using var platform = new DesktopFilePlatform();
            using var service = new FileOperationService(platform, state);
            var result = await service.ExecuteAsync(new(FileOperationKind.Copy, [junction], destination));
            CheckRunner.True(!result.Succeeded, "A junction unexpectedly copied as a supported symbolic link.");
            CheckRunner.True(
                result.Errors.Any(error => error.Contains("not a supported symbolic link", StringComparison.OrdinalIgnoreCase)),
                "A junction must fail as an unsupported reparse type before Windows symbolic-link creation is attempted."
            );
            CheckRunner.True(
                result.Errors.All(error => !error.Contains("privilege", StringComparison.OrdinalIgnoreCase)),
                "The unsupported junction path leaked into symbolic-link creation and produced a privilege failure."
            );
            CheckRunner.True(!Directory.Exists(Path.Combine(destination, Path.GetFileName(junction))), "A failed junction copy left a destination entry.");
            CheckRunner.Equal("target remains unchanged", await File.ReadAllTextAsync(Path.Combine(target, "sentinel.txt")));
        });
    }
}
