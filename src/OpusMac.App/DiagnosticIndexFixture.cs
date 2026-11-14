using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OpusMac.App;

/// <summary>
/// Disposable headless-UI fixture only. Mimics the existing authenticated
/// Listary wire protocol over loopback; never touches the installed agent or
/// its profile, roots, index, settings or foreground window.
/// </summary>
internal sealed class DiagnosticIndexFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accept;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private string[] _indexedDirectories;
    private object[] _roots = [];
    private TaskCompletionSource<bool>? _pausedSearch;
    private TaskCompletionSource<bool>? _activePausedSearch;
    private TaskCompletionSource<bool>? _pausedSearchObserved;

    public string StateDirectory { get; }
    public void SetRoots(params object[] roots) => Volatile.Write(ref _roots, roots);
    public void SetIndexedDirectories(string[] paths) => Volatile.Write(ref _indexedDirectories, paths);

    public Task PauseNextSearch()
    {
        _pausedSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pausedSearchObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _pausedSearchObserved.Task;
    }

    public void ReleasePausedSearch()
    {
        _pausedSearch?.TrySetResult(true);
        _activePausedSearch?.TrySetResult(true);
    }

    public DiagnosticIndexFixture(string stateDirectory, string indexedDirectory)
    {
        StateDirectory = stateDirectory;
        _indexedDirectories = [indexedDirectory];
        Directory.CreateDirectory(stateDirectory);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        File.WriteAllText(Path.Combine(stateDirectory, "integration-endpoint-v1.json"),
            JsonSerializer.Serialize(new
            {
                schema_version = 1, protocol_version = 1, process_id = Environment.ProcessId,
                address = endpoint.ToString(), token = _token
            }));
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(HandleAsync(client));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
                var line = await reader.ReadLineAsync(_stop.Token);
                if (line is null || line.Length > 8192) return;
                using var document = JsonDocument.Parse(line);
                var request = document.RootElement;
                if (request.GetProperty("token").GetString() != _token) return;
                var method = request.GetProperty("method").GetString();
                if (method == "file_search" && Interlocked.Exchange(ref _pausedSearch, null) is { } gate)
                {
                    _activePausedSearch = gate;
                    _pausedSearchObserved?.TrySetResult(true);
                    await gate.Task.WaitAsync(_stop.Token);
                    if (ReferenceEquals(_activePausedSearch, gate)) _activePausedSearch = null;
                }
                object data = method switch
                {
                    "list_roots" => Volatile.Read(ref _roots),
                    "file_search" => SearchPage(request.GetProperty("params")),
                    "get_item" => GetItem(request.GetProperty("params").GetProperty("item_id").GetInt32()),
                    "record_activation" => new { recorded = true },
                    "cancel" => new { cancelled = true },
                    _ => throw new InvalidOperationException("Unexpected diagnostic indexed method")
                };
                var response = JsonSerializer.Serialize(new
                {
                    id = request.GetProperty("id").GetString(), ok = true, data
                }) + "\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), _stop.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
    }

    private object SearchPage(JsonElement parameters)
    {
        var query = parameters.GetProperty("query").GetString();
        var offset = parameters.GetProperty("offset").GetInt64();
        var limit = parameters.GetProperty("limit").GetInt32();
        var hasItem = query == "Design" && Volatile.Read(ref _roots).Any(root =>
        {
            var record = JsonSerializer.SerializeToElement(root);
            return record.ValueKind == JsonValueKind.Object &&
                record.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True &&
                record.TryGetProperty("availability", out var availability) &&
                availability.ValueKind == JsonValueKind.String &&
                availability.GetString() == "available";
        });
        var paths = hasItem ? Volatile.Read(ref _indexedDirectories) : [];
        var page = paths.Skip(checked((int)offset)).Take(limit).Select((path, index) =>
            new { item = DirectoryItem(path, checked(23 + (int)offset + index)) }).ToArray();
        return new
        {
            total_matches = paths.Length,
            offset, limit, catalog_generation = 1,
            has_more = offset + page.Length < paths.Length,
            results = page
        };
    }

    private object GetItem(int id)
    {
        var paths = Volatile.Read(ref _indexedDirectories);
        var index = id - 23;
        return index >= 0 && index < paths.Length ? DirectoryItem(paths[index], id) : new { };
    }

    private static object DirectoryItem(string path, int id)
    {
        var info = new DirectoryInfo(path);
        var method = typeof(OpusMac.Core.Search.ListaryClient).GetMethod("ReadStableFileIdentity",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing canonical native identity reader.");
        return new
        {
            id, root_id = Guid.Parse("6f6d60b3-1aa7-4a9a-8e69-829bac18762e"),
            path, parent_path = Path.GetDirectoryName(path),
            name = info.Name, kind = "folder", size = 0L,
            modified_unix_ms = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            available = true, stable_file_id = method.Invoke(null, [path])
        };
    }

    public async ValueTask DisposeAsync()
    {
        ReleasePausedSearch();
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(3));
        _stop.Dispose();
    }
}
