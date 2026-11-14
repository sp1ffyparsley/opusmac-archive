using System.Diagnostics;

namespace OpusMac.Core.Platform;

/// <summary>Native filesystem and desktop actions. Mutating callers still use the operation owner.</summary>
public sealed class DesktopFilePlatform : IFilePlatform, IAsyncDisposable
{
    public Task CreateHardLinkAsync(string source, string destination, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        source = RequireExisting(source);
        destination = RequireNewDestination(destination);
        if ((File.GetAttributes(source) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Hard links require a regular file.");
        if (OperatingSystem.IsWindows()) WindowsFileOperations.CreateHardLink(source, destination);
        else if (OperatingSystem.IsMacOS()) MacFileOperations.CreateHardLink(source, destination);
        else throw Unsupported();
    }, cancellationToken);
    /// <summary>Waits for this application's owned Quick Look helper before final window shutdown.</summary>
    public ValueTask DisposeAsync() => new(NativeCommand.StopQuickLookAsync());

    public Task<bool> TryMoveEntryAsync(string source, string destination, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        source = RequireExisting(source);
        destination = RequireNewDestination(destination);
        if (OperatingSystem.IsMacOS())
            return MacFileOperations.TryMoveEntry(source, destination, cancellationToken);
        if (OperatingSystem.IsWindows())
            return WindowsFileOperations.TryMoveEntry(source, destination, cancellationToken);
        throw Unsupported();
    }, cancellationToken);

    public Task CopyLeafAsync(string source, string destination, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        source = RequireExisting(source);
        destination = RequireNewDestination(destination);
        var attributes = File.GetAttributes(source);
        if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("CopyLeafAsync accepts a file or symbolic link, not a directory.");
        if (OperatingSystem.IsMacOS())
            MacFileOperations.CopyLeaf(source, destination, cancellationToken);
        else if (OperatingSystem.IsWindows())
            WindowsFileOperations.CopyLeaf(source, destination, cancellationToken);
        else throw Unsupported();
    }, cancellationToken);

    public Task CopyDirectoryMetadataAsync(string source, string destination, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        source = RequireDirectory(source);
        destination = RequireDirectory(destination);
        if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint) ||
            File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Directory metadata cannot be applied through a symbolic link.");
        if (OperatingSystem.IsMacOS())
            MacFileOperations.CopyDirectoryMetadata(source, destination, cancellationToken);
        else if (OperatingSystem.IsWindows())
            WindowsFileOperations.CopyDirectoryMetadata(source, destination, cancellationToken);
        else throw Unsupported();
    }, cancellationToken);

    public Task<TrashResult> TrashAsync(string path, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = RequireExisting(path);
        if (Path.GetPathRoot(path) == Path.TrimEndingDirectorySeparator(path))
            throw new IOException("A volume root cannot be moved to Trash.");
        if (OperatingSystem.IsMacOS())
            return MacFileOperations.Trash(path, cancellationToken);
        if (OperatingSystem.IsWindows())
            return await WindowsFileOperations.TrashAsync(path, cancellationToken).ConfigureAwait(false);
        throw Unsupported();
    }, cancellationToken);

    public Task RestoreTrashAsync(string trashedPath, string destination, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        trashedPath = RequireExisting(trashedPath);
        destination = RequireNewDestination(destination);
        if (OperatingSystem.IsMacOS())
            MacFileOperations.RestoreTrash(trashedPath, destination, cancellationToken);
        else if (OperatingSystem.IsWindows())
            await WindowsFileOperations.RestoreTrashAsync(trashedPath, destination, cancellationToken).ConfigureAwait(false);
        else throw Unsupported();
    }, cancellationToken);

    public Task OpenAsync(string path, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = RequireExisting(path);
        if (OperatingSystem.IsMacOS())
            await NativeCommand.RunAsync("/usr/bin/open", ["--", path], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        else if (OperatingSystem.IsWindows())
        {
            // This proves ShellExecute accepted the launch, not that the associated application finished opening.
            using var launched = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        else throw Unsupported();
    }, cancellationToken);

    public Task RevealAsync(string path, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = RequireExisting(path);
        if (OperatingSystem.IsMacOS())
            await NativeCommand.RunAsync("/usr/bin/open", ["-R", "--", path], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        else if (OperatingSystem.IsWindows())
            await WindowsFileOperations.RevealAsync(path, cancellationToken).ConfigureAwait(false);
        else throw Unsupported();
    }, cancellationToken);

    public Task QuickLookAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is < 1 or > 128) throw new ArgumentException("Choose between 1 and 128 files for preview.", nameof(paths));
        var resolved = paths.Select(RequireExisting).ToArray();
        if (OperatingSystem.IsMacOS())
            await NativeCommand.OpenQuickLookAsync(resolved, cancellationToken).ConfigureAwait(false);
        else if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Quick Look is available on macOS. Use the viewer or Open on Windows.");
        else throw Unsupported();
    }, cancellationToken);

    public Task OpenTerminalAsync(string directory, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        directory = RequireDirectory(directory);
        if (OperatingSystem.IsMacOS())
            await NativeCommand.RunAsync("/usr/bin/open", ["-a", "Terminal", "--", directory], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        else if (OperatingSystem.IsWindows())
        {
            // WorkingDirectory is data. It is never interpolated into a /K command.
            using var launched = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                WorkingDirectory = directory,
                UseShellExecute = true
            });
        }
        else throw Unsupported();
    }, cancellationToken);

    internal static string RequireExisting(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0')) throw new ArgumentException("Paths cannot contain a null character.", nameof(path));
        var full = Path.GetFullPath(path);
        _ = File.GetAttributes(full); // Includes broken links; File.Exists alone does not.
        return full;
    }

    internal static string RequireDirectory(string path)
    {
        var full = RequireExisting(path);
        if (!File.GetAttributes(full).HasFlag(FileAttributes.Directory))
            throw new DirectoryNotFoundException("The selected path is not a directory: " + full);
        return full;
    }

    internal static string RequireNewDestination(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0')) throw new ArgumentException("Paths cannot contain a null character.", nameof(path));
        var full = Path.GetFullPath(path);
        try { _ = File.GetAttributes(full); }
        catch (FileNotFoundException) { RequireDirectory(Path.GetDirectoryName(full)!); return full; }
        catch (DirectoryNotFoundException) { RequireDirectory(Path.GetDirectoryName(full)!); return full; }
        throw new IOException("The destination already exists: " + full);
    }

    private static PlatformNotSupportedException Unsupported() =>
        new("This build supports Windows development and macOS.");
}
