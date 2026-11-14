using System.Diagnostics;

namespace OpusMac.App;

// Optional local playback backend. No media bytes are copied or rewritten by the viewer.
// FFplay provides its own pause/seek controls; the Avalonia pane owns one child process.
internal sealed class VideoPreviewSession : IDisposable
{
    private readonly Process _process;

    private VideoPreviewSession(Process process) => _process = process;

    public static bool IsVideo(string name) => Path.GetExtension(name).ToLowerInvariant()
        is ".mp4" or ".m4v" or ".mov" or ".mkv" or ".webm" or ".avi";

    internal static string? FindPlayer()
    {
        var executable = OperatingSystem.IsWindows() ? "ffplay.exe" : "ffplay";
        foreach (var item in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = item.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static VideoPreviewSession Start(string executable, string file, bool mirrored, CancellationToken token,
        double startSeconds = 0)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(startSeconds) || startSeconds < 0 || startSeconds > 86400)
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        if (!IsVideo(file) || !File.Exists(file) ||
            (File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
            throw new IOException("The selected video is missing, linked or offline. Select an available regular file.");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(file))!
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-autoexit",
                     "-window_title", "Directory Opus video preview", "-seek_interval", "5" })
            start.ArgumentList.Add(argument);
        if (startSeconds > 0)
        {
            start.ArgumentList.Add("-ss");
            start.ArgumentList.Add(startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        if (mirrored)
        {
            start.ArgumentList.Add("-vf");
            start.ArgumentList.Add("hflip");
        }
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(Path.GetFullPath(file));
        token.ThrowIfCancellationRequested();
        var process = Process.Start(start) ?? throw new IOException("FFplay could not open the selected video.");
        var session = new VideoPreviewSession(process);
        if (token.IsCancellationRequested)
        {
            session.Dispose();
            token.ThrowIfCancellationRequested();
        }
        return session;
    }

    public async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync();
        return _process.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { _process.Dispose(); }
    }
}
