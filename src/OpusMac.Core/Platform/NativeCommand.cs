using System.Diagnostics;
using System.Text;

namespace OpusMac.Core.Platform;

/// <summary>Argument-only subprocess invocation with bounded lifetime, output, and owned cancellation.</summary>
internal static class NativeCommand
{
    private static readonly SemaphoreSlim PreviewGate = new(1, 1);
    private static readonly object ProcessGate = new();
    private static readonly HashSet<Process> OwnedProcesses = [];
    private static CancellationTokenSource? _previewCancellation;
    private static Task? _previewTask;
    private static Process? _previewProcess;

    static NativeCommand()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (ProcessGate)
                foreach (var process in OwnedProcesses)
                    TryKill(process);
        };
    }

    public static Task RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken, TaskCompletionSource? started = null) =>
        RunTrackedAsync(executable, arguments, timeout, cancellationToken, started, null);

    private static async Task RunTrackedAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken, TaskCompletionSource? started, Action<Process>? observeProcess)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        var processStarted = false;
        var exitObserved = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Task<string>? output = null;
        Task<string>? error = null;
        try
        {
            if (!process.Start()) throw new IOException("The system helper could not be started: " + executable);
            processStarted = true;
            lock (ProcessGate) OwnedProcesses.Add(process);
            observeProcess?.Invoke(process);
            started?.TrySetResult();
            output = ReadBoundedAsync(process.StandardOutput, 128 * 1024, deadline.Token);
            error = ReadBoundedAsync(process.StandardError, 128 * 1024, deadline.Token);
            // Observe each reader as it completes. Waiting for WhenAll first would let a full,
            // abandoned pipe keep the helper alive until the long process deadline.
            var pending = new List<Task> { process.WaitForExitAsync(deadline.Token), output, error };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            exitObserved = true;
            if (process.ExitCode != 0)
                throw new IOException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}: {(await error.ConfigureAwait(false)).Trim()}");
        }
        catch (Exception exception)
        {
            started?.TrySetException(exception);
            deadline.Cancel();
            TryKill(process);
            Exception? terminationError = null;
            if (processStarted)
            {
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    exitObserved = true;
                }
                catch (TimeoutException errorWhileStopping) { terminationError = errorWhileStopping; }
            }
            if (output != null && error != null)
            {
                try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { }
            }
            if (terminationError != null)
                throw new TimeoutException($"{Path.GetFileName(executable)} did not terminate after cancellation.", terminationError);
            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"{Path.GetFileName(executable)} exceeded its allowed run time.", exception);
            throw;
        }
        finally
        {
            if (!processStarted || exitObserved) ReleaseProcess(process);
            else _ = ReapAndReleaseAsync(process);
        }
    }

    private static void ReleaseProcess(Process process)
    {
        lock (ProcessGate) OwnedProcesses.Remove(process);
        process.Dispose();
    }

    private static async Task ReapAndReleaseAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            ReleaseProcess(process);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Keep the still-owned handle available to the shutdown fallback if observation failed.
            Trace.TraceWarning("System helper exit could not be observed: " + exception.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public static Task OpenQuickLookAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        OpenPreviewAsync("/usr/bin/qlmanage", ["-p", .. paths], cancellationToken);

    private static async Task OpenPreviewAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await PreviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Keep one helper owned by this application. A second preview replaces only our previous one.
            await StopPreviewUnderGateAsync().ConfigureAwait(false);
            _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _previewTask = RunTrackedAsync(executable, arguments, TimeSpan.FromMinutes(30), _previewCancellation.Token, started,
                process => _previewProcess = process);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var early = await Task.WhenAny(_previewTask, Task.Delay(250, cancellationToken)).ConfigureAwait(false);
            if (early == _previewTask) await _previewTask.ConfigureAwait(false);
            else _ = _previewTask.ContinueWith(t => Trace.TraceWarning("Quick Look helper: " + t.Exception?.GetBaseException().Message),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        finally { PreviewGate.Release(); }
    }

    public static async Task StopQuickLookAsync()
    {
        if (!await PreviewGate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
            throw new TimeoutException("Quick Look is still starting; its shutdown has not completed.");
        try { await StopPreviewUnderGateAsync().ConfigureAwait(false); }
        finally { PreviewGate.Release(); }
    }

    private static async Task StopPreviewUnderGateAsync()
    {
        if (_previewCancellation == null) return;
        _previewCancellation.Cancel();
        if (_previewTask != null)
        {
            try { await _previewTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException exception) { Trace.TraceWarning("Quick Look closed after helper failure: " + exception.Message); }
            catch (TimeoutException)
            {
                // A timed-out runner retains its owned process handle until actual exit.
                if (_previewProcess == null) throw;
                TryKill(_previewProcess);
                try { await _previewProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (InvalidOperationException) { /* A retained handle is disposed only after observed exit. */ }
            }
        }
        _previewCancellation.Dispose();
        _previewCancellation = null;
        _previewTask = null;
        _previewProcess = null;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumBytes, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        var bytes = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            bytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (bytes > maximumBytes) throw new IOException("The system helper exceeded its output limit.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}
