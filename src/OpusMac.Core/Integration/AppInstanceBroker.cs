using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpusMac.Core.Browsing;

namespace OpusMac.Core.Integration;

public sealed record OpenRequest(string[] Paths, bool Activate = true);

public sealed class AppInstanceBroker : IDisposable, IAsyncDisposable
{
    private const int FrameLimit = 64 * 1024;
    private static readonly TimeSpan ClientDeadline = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ResponseWriteDeadline = TimeSpan.FromSeconds(2);
    private readonly string _stateDirectory;
    private readonly CancellationTokenSource _stop = new();
    private FileStream? _lease;
    private NamedPipeServerStream? _server;
    private Task? _loop;
    private bool _disposed;
    public string PipeName { get; }
    public bool IsPrimary => _lease is not null;

    public AppInstanceBroker(string stateDirectory)
    {
        _stateDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory));
        var identity = OperatingSystem.IsWindows() ? _stateDirectory.ToUpperInvariant() : _stateDirectory;
        PipeName = "opusmac-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "\n" + identity)))[..32].ToLowerInvariant();
    }

    public bool TryBecomePrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary) return true;
        Directory.CreateDirectory(_stateDirectory);
        try
        {
            _lease = new FileStream(Path.Combine(_stateDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 11 or 32 or 33 or 35)
        {
            return false;
        }
    }

    public void Start(Func<OpenRequest, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary) throw new InvalidOperationException("This process does not own the application instance.");
        if (_loop is not null) throw new InvalidOperationException("The instance receiver is already running.");
        _loop = Task.Run(() => ListenAsync(handler, _stop.Token));
    }

    public async Task ForwardAsync(OpenRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        request = Normalize(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ClientDeadline);
        var id = Guid.NewGuid().ToString("N");
        var requestWriteStarted = false;
        var responseKnown = false;
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var requestFrame = SerializeFrame(new RequestEnvelope(1, id, request));
            requestWriteStarted = true;
            await WriteFrameAsync(pipe, requestFrame, deadline.Token).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<ResponseEnvelope>(await ReadFrameAsync(pipe, deadline.Token).ConfigureAwait(false))
                ?? throw new IOException("The existing window returned an empty response.");
            if (response.Id != id) throw new IOException("The existing window returned a mismatched response.");
            responseKnown = true;
            if (response.TimedOut)
                throw new TimeoutException(response.Error ?? "The existing window's request handler timed out; its outcome may still be unknown.");
            if (!response.Accepted) throw new IOException(response.Error ?? "The existing window refused the open request.");
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The existing Opus Mac window did not answer before the client deadline. The request outcome may be unknown; reconcile it before retrying.", e);
        }
        catch (OperationCanceledException e) when (cancellationToken.IsCancellationRequested && requestWriteStarted)
        {
            throw new OperationCanceledException(
                "The forwarding request was cancelled after transmission began. The request outcome may be unknown; reconcile it before retrying.",
                e,
                cancellationToken);
        }
        catch (Exception e) when (requestWriteStarted && !responseKnown)
        {
            throw new IOException(
                "The forwarding request was transmitted but no matching response was received. The request outcome may be unknown; reconcile it before retrying.",
                e);
        }
    }

    private async Task ListenAsync(Func<OpenRequest, Task> handler, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, FrameLimit, FrameLimit);
                    _server = pipe;
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(RequestDeadline);
                    string? id = null;
                    ResponseEnvelope reply;
                    var handlerStarted = false;
                    try
                    {
                        var envelope = JsonSerializer.Deserialize<RequestEnvelope>(await ReadFrameAsync(pipe, deadline.Token).ConfigureAwait(false));
                        if (envelope is null || envelope.Version != 1 || !Guid.TryParseExact(envelope.Id, "N", out _))
                            throw new IOException("The open request is invalid.");
                        id = envelope.Id;
                        var request = Normalize(envelope.Request);
                        handlerStarted = true;
                        await handler(request).WaitAsync(deadline.Token).ConfigureAwait(false);
                        reply = new ResponseEnvelope(id, true, null);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested && id is not null)
                    {
                        reply = new ResponseEnvelope(
                            id,
                            false,
                            handlerStarted
                                ? "The existing window's request handler exceeded its deadline; the operation outcome may be unknown because the handler may still complete. Reconcile the operation before retrying."
                                : "The open request expired before its handler started.",
                            handlerStarted);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested && id is null)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        reply = new ResponseEnvelope(id, false, e.Message);
                    }
                    using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    responseDeadline.CancelAfter(ResponseWriteDeadline);
                    await WriteFrameAsync(pipe, reply, responseDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
                catch (Exception e) when (e is IOException or OperationCanceledException)
                {
                    // A disconnected/malformed client cannot terminate the receiver or start a second app.
                    if (!token.IsCancellationRequested) await Task.Delay(25, token).ConfigureAwait(false);
                }
                finally { _server = null; }
            }
        }
        finally
        {
            ReleaseLease();
        }
    }

    private static OpenRequest Normalize(OpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Paths is null || request.Paths.Length > 32) throw new ArgumentException("An open request accepts at most 32 paths.");
        var paths = request.Paths.Select(path =>
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0'))
                throw new ArgumentException("The open request contains an invalid path.");
            return DirectoryBrowser.ResolvePath(path, Environment.CurrentDirectory);
        }).ToArray();
        return request with { Paths = paths };
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[2048];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (count == 0) throw new IOException("The instance connection ended before a response.");
            var newline = Array.IndexOf(chunk, (byte)'\n', 0, count);
            var take = newline >= 0 ? newline : count;
            if (buffer.Length + take > FrameLimit) throw new IOException("The instance message exceeds the size limit.");
            buffer.Write(chunk, 0, take);
            if (newline >= 0)
            {
                if (newline != count - 1) throw new IOException("Only one message is accepted per connection.");
                return buffer.ToArray();
            }
        }
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken token)
    {
        await WriteFrameAsync(stream, SerializeFrame(value), token).ConfigureAwait(false);
    }

    private static byte[] SerializeFrame<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > FrameLimit) throw new IOException("The instance message exceeds the size limit.");
        return bytes;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _server?.Dispose();
        if (_loop is null) ReleaseLease();
    }

    private void ReleaseLease()
    {
        _lease?.Dispose();
        _lease = null;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or ObjectDisposedException) { }
        }
        if (_loop is null || _loop.IsCompleted) _stop.Dispose();
    }

    private sealed record RequestEnvelope(int Version, string Id, OpenRequest Request);
    private sealed record ResponseEnvelope(string? Id, bool Accepted, string? Error, bool TimedOut = false);
}
