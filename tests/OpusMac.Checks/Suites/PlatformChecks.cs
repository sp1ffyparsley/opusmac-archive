using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using OpusMac.Core.Platform;

namespace OpusMac.Checks.Suites;

public sealed class PlatformChecks : ICheckSuite
{
    public async Task RunAsync(CheckRunner checks, string sandbox)
    {
        var platform = new DesktopFilePlatform();
        await checks.CheckAsync("owned preview replacement and async disposal await actual helper exit", async () =>
        {
            var fixture = Directory.CreateDirectory(Path.Combine(sandbox, "preview-lifetime")).FullName;
            var script = Path.Combine(fixture, OperatingSystem.IsWindows() ? "preview.ps1" : "preview.zsh");
            await File.WriteAllTextAsync(script, OperatingSystem.IsWindows()
                ? "[IO.File]::WriteAllText($args[0], $PID.ToString()); Start-Sleep -Seconds 30"
                : "printf '%s\\n' \"$$\" > \"$1\"\nsleep 30\n", new UTF8Encoding(false));
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/zsh";
            var open = typeof(DesktopFilePlatform).Assembly.GetType("OpusMac.Core.Platform.NativeCommand")!
                .GetMethod("OpenPreviewAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            async Task<Process> StartPreviewAsync(string name)
            {
                var pidPath = Path.Combine(fixture, name + ".pid");
                string[] arguments = OperatingSystem.IsWindows()
                    ? ["-NoProfile", "-NonInteractive", "-File", script, pidPath] : ["-f", script, pidPath];
                await (Task)open.Invoke(null, [executable, arguments, CancellationToken.None])!;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!File.Exists(pidPath)) await Task.Delay(20, deadline.Token);
                var processId = int.Parse(await File.ReadAllTextAsync(pidPath), System.Globalization.CultureInfo.InvariantCulture);
                return Process.GetProcessById(processId);
            }
            try
            {
                using var first = await StartPreviewAsync("first");
                CheckRunner.True(!first.HasExited, "Preview fixture did not remain alive.");
                using var second = await StartPreviewAsync("second");
                CheckRunner.True(first.HasExited, "Replacement returned before the previous preview exited.");
                CheckRunner.True(!second.HasExited, "Replacement preview fixture did not remain alive.");
                await platform.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                CheckRunner.True(second.HasExited, "Async platform disposal returned before the preview exited.");
                await platform.DisposeAsync();
            }
            finally { await platform.DisposeAsync(); }
        });
        await checks.CheckAsync("native helper output overflow fails promptly and terminates its process", async () =>
        {
            var fixture = Directory.CreateDirectory(Path.Combine(sandbox, "helper-limit")).FullName;
            var pidPath = Path.Combine(fixture, "owned-pid.txt");
            var script = Path.Combine(fixture, OperatingSystem.IsWindows() ? "helper.ps1" : "helper.zsh");
            var content = OperatingSystem.IsWindows()
                ? "[IO.File]::WriteAllText($args[0], $PID.ToString()); [Console]::Out.Write(('x' * 270000)); Start-Sleep -Seconds 30"
                : "printf '%s\\n' \"$$\" > \"$1\"\ni=0; while (( i++ < 4096 )); do printf '%064d' 0; done\nsleep 30\n";
            await File.WriteAllTextAsync(script, content, new UTF8Encoding(false));
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                : "/bin/zsh";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-File", script, pidPath] : ["-f", script, pidPath];
            var method = typeof(DesktopFilePlatform).Assembly.GetType("OpusMac.Core.Platform.NativeCommand")!
                .GetMethod("RunAsync", BindingFlags.Static | BindingFlags.Public)!;
            using var cancellation = new CancellationTokenSource();
            var watch = Stopwatch.StartNew();
            var task = (Task)method.Invoke(null, [executable, arguments, TimeSpan.FromSeconds(30), cancellation.Token, null])!;
            try
            {
                await CheckRunner.ThrowsAsync<IOException>(() => task.WaitAsync(TimeSpan.FromSeconds(12)));
                CheckRunner.True(watch.Elapsed < TimeSpan.FromSeconds(8), "The output limit waited for the process deadline.");
                var processId = int.Parse(await File.ReadAllTextAsync(pidPath), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    using var helper = Process.GetProcessById(processId);
                    CheckRunner.True(helper.HasExited, "The overflowing helper remains alive.");
                }
                catch (ArgumentException) { /* The owned process has already been reaped. */ }
            }
            finally
            {
                cancellation.Cancel();
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { }
            }
        });
        await checks.CheckAsync("platform copies bytes, timestamps and unusual filenames", async () =>
        {
            var source = Path.Combine(sandbox, "copy source ñ & 'quoted'.txt");
            var destination = Path.Combine(sandbox, "copy destination ñ & 'quoted'.txt");
            var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("native copy \0 ñ \r\n", 8192)));
            await File.WriteAllBytesAsync(source, data);
            var modified = new DateTime(2023, 4, 5, 6, 7, 8, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(source, modified);
            await platform.CopyLeafAsync(source, destination, default);
            CheckRunner.True((await File.ReadAllBytesAsync(destination)).SequenceEqual(data), "Copied data differs.");
            CheckRunner.Equal(modified, File.GetLastWriteTimeUtc(destination), "Last-write time was not retained");
            CheckRunner.True(File.Exists(source), "Copy removed its source.");
        });
        await checks.CheckAsync("platform rejects overwrite and pre-canceled copy", async () =>
        {
            var source = Path.Combine(sandbox, "collision-source.txt");
            var destination = Path.Combine(sandbox, "collision-destination.txt");
            await File.WriteAllTextAsync(source, "source survives");
            await File.WriteAllTextAsync(destination, "destination survives");
            await CheckRunner.ThrowsAsync<IOException>(() => platform.CopyLeafAsync(source, destination, default));
            CheckRunner.Equal("destination survives", await File.ReadAllTextAsync(destination));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var canceledDestination = Path.Combine(sandbox, "canceled-copy.txt");
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => platform.CopyLeafAsync(source, canceledDestination, cancellation.Token));
            CheckRunner.True(!File.Exists(canceledDestination), "Canceled copy created a destination.");
            CheckRunner.Equal("source survives", await File.ReadAllTextAsync(source));
        });
        await checks.CheckAsync("platform exclusively renames files and folders", async () =>
        {
            var source = Path.Combine(sandbox, "move-source.txt");
            var destination = Path.Combine(sandbox, "move-destination.txt");
            await File.WriteAllTextAsync(source, "atomic rename");
            CheckRunner.True(await platform.TryMoveEntryAsync(source, destination, default), "Same-volume rename did not run.");
            CheckRunner.True(!File.Exists(source) && File.Exists(destination), "Rename did not commit both paths.");
            await File.WriteAllTextAsync(source, "must survive collision");
            await CheckRunner.ThrowsAsync<IOException>(async () => { _ = await platform.TryMoveEntryAsync(source, destination, default); });
            CheckRunner.Equal("must survive collision", await File.ReadAllTextAsync(source));
            CheckRunner.Equal("atomic rename", await File.ReadAllTextAsync(destination));
            var directory = Directory.CreateDirectory(Path.Combine(sandbox, "move-folder")).FullName;
            await File.WriteAllTextAsync(Path.Combine(directory, "child.txt"), "child");
            var movedDirectory = Path.Combine(sandbox, "moved-folder");
            CheckRunner.True(await platform.TryMoveEntryAsync(directory, movedDirectory, default), "Directory rename failed.");
            CheckRunner.Equal("child", await File.ReadAllTextAsync(Path.Combine(movedDirectory, "child.txt")));
        });
        await checks.CheckAsync("platform retains directory metadata without copying children", async () =>
        {
            var source = Directory.CreateDirectory(Path.Combine(sandbox, "metadata-source")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(sandbox, "metadata-destination")).FullName;
            await File.WriteAllTextAsync(Path.Combine(source, "source-only.txt"), "not a recursive operation");
            var modified = new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            Directory.SetLastWriteTimeUtc(source, modified);
            await platform.CopyDirectoryMetadataAsync(source, destination, default);
            CheckRunner.Equal(modified, Directory.GetLastWriteTimeUtc(destination));
            CheckRunner.Equal(0, Directory.EnumerateFileSystemEntries(destination).Count());
        });
        await checks.CheckAsync("platform preserves file, directory and broken symbolic links", async () =>
        {
            var fixture = Directory.CreateDirectory(Path.Combine(sandbox, "links")).FullName;
            var target = Path.Combine(fixture, "target.txt");
            await File.WriteAllTextAsync(target, "target unchanged");
            var fileLink = Path.Combine(fixture, "source-link");
            CreateFixtureLink(() => File.CreateSymbolicLink(fileLink, "target.txt"));
            var fileCopy = Path.Combine(fixture, "copied-link");
            await platform.CopyLeafAsync(fileLink, fileCopy, default);
            CheckRunner.Equal("target.txt", new FileInfo(fileCopy).LinkTarget);
            var directory = Directory.CreateDirectory(Path.Combine(fixture, "target-folder")).FullName;
            await File.WriteAllTextAsync(Path.Combine(directory, "sentinel.txt"), "not traversed");
            var directoryLink = Path.Combine(fixture, "directory-link");
            CreateFixtureLink(() => Directory.CreateSymbolicLink(directoryLink, "target-folder"));
            var directoryCopy = Path.Combine(fixture, "copied-directory-link");
            await platform.CopyLeafAsync(directoryLink, directoryCopy, default);
            CheckRunner.Equal("target-folder", new DirectoryInfo(directoryCopy).LinkTarget);
            var broken = Path.Combine(fixture, "broken-link");
            CreateFixtureLink(() => File.CreateSymbolicLink(broken, "missing-target"));
            var brokenCopy = Path.Combine(fixture, "copied-broken-link");
            await platform.CopyLeafAsync(broken, brokenCopy, default);
            CheckRunner.Equal("missing-target", new FileInfo(brokenCopy).LinkTarget);
            await CheckRunner.ThrowsAsync<IOException>(() => platform.CopyLeafAsync(target, brokenCopy, default));
            CheckRunner.Equal("target unchanged", await File.ReadAllTextAsync(target));
        });
        await checks.CheckAsync("platform returns real Trash location and restores without overwriting", async () =>
        {
            var source = Path.Combine(sandbox, "trash-" + Guid.NewGuid().ToString("N") + ".txt");
            await File.WriteAllTextAsync(source, "recoverable fixture only");
            string? trashed = null;
            try
            {
                var result = await platform.TrashAsync(source, default);
                trashed = result.TrashedPath;
                CheckRunner.True(!File.Exists(source), "Trash did not remove the original path.");
                CheckRunner.True(!string.IsNullOrWhiteSpace(trashed) && File.Exists(trashed), "The OS did not return a usable Trash path.");
                CheckRunner.Equal("recoverable fixture only", await File.ReadAllTextAsync(trashed!));
                await File.WriteAllTextAsync(source, "collision is retained");
                await CheckRunner.ThrowsAsync<IOException>(() => platform.RestoreTrashAsync(trashed!, source, default));
                CheckRunner.Equal("collision is retained", await File.ReadAllTextAsync(source));
                File.Delete(source);
                await platform.RestoreTrashAsync(trashed!, source, default);
                CheckRunner.Equal("recoverable fixture only", await File.ReadAllTextAsync(source));
                CheckRunner.True(!File.Exists(trashed), "Restore left the recycled payload behind.");
                if (OperatingSystem.IsWindows() && Path.GetFileName(trashed!).StartsWith("$R", StringComparison.Ordinal))
                {
                    var metadata = Path.Combine(Path.GetDirectoryName(trashed!)!, "$I" + Path.GetFileName(trashed!)[2..]);
                    CheckRunner.True(!File.Exists(metadata), "Restore left stale Windows Recycle Bin metadata.");
                }
            }
            finally
            {
                // Never delete a recycled fixture directly; restore through the platform even after a failed assertion.
                if (trashed != null && File.Exists(trashed))
                    await platform.RestoreTrashAsync(trashed, Path.Combine(sandbox, "recovered-" + Guid.NewGuid().ToString("N") + ".txt"), default);
            }
        });
        await checks.CheckAsync("platform recycles and restores a populated directory", async () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(sandbox, "plain-trash-folder-" + Guid.NewGuid().ToString("N"))).FullName;
            await File.WriteAllTextAsync(Path.Combine(directory, "inside.txt"), "directory fixture retained");
            string? trashed = null;
            try
            {
                trashed = (await platform.TrashAsync(directory, default)).TrashedPath;
                CheckRunner.True(!Directory.Exists(directory) && trashed != null && Directory.Exists(trashed), "Directory Trash location is unavailable.");
                await platform.RestoreTrashAsync(trashed!, directory, default);
                CheckRunner.Equal("directory fixture retained", await File.ReadAllTextAsync(Path.Combine(directory, "inside.txt")));
                CheckRunner.True(!Directory.Exists(trashed), "Directory restore left its payload in Trash.");
                if (OperatingSystem.IsWindows())
                {
                    var metadata = Path.Combine(Path.GetDirectoryName(trashed!)!, "$I" + Path.GetFileName(trashed!)[2..]);
                    CheckRunner.True(!File.Exists(metadata), "Directory restore left stale Recycle Bin metadata.");
                }
            }
            finally
            {
                if (trashed != null && Directory.Exists(trashed))
                    await platform.RestoreTrashAsync(trashed, Path.Combine(sandbox, "recovered-plain-folder-" + Guid.NewGuid().ToString("N")), default);
            }
        });
        await checks.CheckAsync("platform recycles a directory without following its symbolic links", async () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(sandbox, "trash-folder-" + Guid.NewGuid().ToString("N"))).FullName;
            var sentinel = Path.Combine(sandbox, "trash-link-target.txt");
            await File.WriteAllTextAsync(sentinel, "external target stays");
            await File.WriteAllTextAsync(Path.Combine(directory, "inside.txt"), "folder content");
            CreateFixtureLink(() => File.CreateSymbolicLink(Path.Combine(directory, "outside-link"), sentinel));
            string? trashed = null;
            try
            {
                trashed = (await platform.TrashAsync(directory, default)).TrashedPath;
                CheckRunner.True(trashed != null && Directory.Exists(trashed), "Directory Trash location is unavailable.");
                CheckRunner.Equal("external target stays", await File.ReadAllTextAsync(sentinel));
                await platform.RestoreTrashAsync(trashed!, directory, default);
                CheckRunner.Equal("folder content", await File.ReadAllTextAsync(Path.Combine(directory, "inside.txt")));
                CheckRunner.True(new FileInfo(Path.Combine(directory, "outside-link")).LinkTarget != null, "Directory restore lost its link.");
            }
            finally
            {
                if (trashed != null && Directory.Exists(trashed))
                    await platform.RestoreTrashAsync(trashed, Path.Combine(sandbox, "recovered-folder-" + Guid.NewGuid().ToString("N")), default);
            }
        });
        if (OperatingSystem.IsMacOS())
        {
            await checks.CheckAsync("macOS copy preserves resource fork, extended attributes and mode", async () =>
            {
                if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
                var source = Path.Combine(sandbox, "mac-metadata-source");
                var destination = Path.Combine(sandbox, "mac-metadata-copy");
                await File.WriteAllTextAsync(source, "data fork");
                var extended = Encoding.UTF8.GetBytes("extended attribute proof");
                var resource = Encoding.UTF8.GetBytes("resource fork proof");
                SetAttribute(source, "com.opusmac.fixture", extended);
                SetAttribute(source, "com.apple.ResourceFork", resource);
                File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                await platform.CopyLeafAsync(source, destination, default);
                CheckRunner.True(ReadAttribute(destination, "com.opusmac.fixture").SequenceEqual(extended), "Extended attribute changed.");
                CheckRunner.True(ReadAttribute(destination, "com.apple.ResourceFork").SequenceEqual(resource), "Resource fork changed.");
                CheckRunner.Equal(File.GetUnixFileMode(source), File.GetUnixFileMode(destination));
            });
        }
    }

    private static void SetAttribute(string path, string name, byte[] value)
    {
        if (SetXattr(path, name, value, (nuint)value.Length, 0, 0) != 0)
            throw new IOException("setxattr fixture failed: errno " + Marshal.GetLastPInvokeError());
    }
    private static void CreateFixtureLink(Action create)
    {
        try { create(); }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        {
            CheckRunner.NotVerified("Windows does not grant this process symbolic-link creation privilege; the fixture could not be created and the link behavior was not executed.");
        }
    }
    private static byte[] ReadAttribute(string path, string name)
    {
        var buffer = new byte[4096];
        var length = GetXattr(path, name, buffer, (nuint)buffer.Length, 0, 0);
        if (length < 0) throw new IOException("getxattr fixture failed: errno " + Marshal.GetLastPInvokeError());
        return buffer[..checked((int)length)];
    }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nuint size, uint position, int options);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nuint size, uint position, int options);
}
