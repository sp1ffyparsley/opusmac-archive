using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpusMac.Core.Search;

/// <summary>
/// A companion client for the existing Listary agent. This class never starts an
/// agent, opens its database, changes indexed roots, or falls back to another index.
/// Each operation owns its connection, so cancellation cannot wait behind a search.
/// </summary>
public sealed class ListaryClient : IDisposable
{
    private const int MaximumDescriptorBytes = 64 * 1024;
    private const int MaximumRequestBytes = 8 * 1024 * 1024;
    private static readonly SemaphoreSlim MetadataSlots = new(2, 2);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly string _endpointPath;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maximumResponseBytes;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public ListaryClient(string? stateDirectory = null, TimeSpan? requestTimeout = null,
        int maximumResponseBytes = 8 * 1024 * 1024)
    {
        StateDirectory = Path.GetFullPath(stateDirectory ?? DefaultStateDirectory());
        _endpointPath = Path.Combine(StateDirectory, "integration-endpoint-v1.json");
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        if (_requestTimeout < TimeSpan.FromMilliseconds(100) || _requestTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (maximumResponseBytes is < 256 or > 8 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        _maximumResponseBytes = maximumResponseBytes;
    }

    public string StateDirectory { get; }

    public static string DefaultStateDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("SPIFFY_LISTARY_STATE_DIR");
        if (string.IsNullOrWhiteSpace(configured))
            configured = Environment.GetEnvironmentVariable("LISTARY_MAC_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (configured.StartsWith("~/", StringComparison.Ordinal))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), configured[2..]);
            return configured;
        }
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ListaryMac", "state");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "ListaryMac", "state")
            : Path.Combine(home, ".local", "share", "spiffy-listary", "state");
    }

    public async Task<ListaryServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            var endpoint = await ReadEndpointAsync(lifetime.Token).ConfigureAwait(false);
            var data = await SendAsync(endpoint, "status", null, null, lifetime.Token).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object || !TryInt64(data, "protocol_version", out var protocol) || protocol != 1)
                throw InvalidResponse("Indexed search returned an incompatible status.");
            return new(true, "Listary agent connected; indexed folder readiness has not been checked.");
        }
        catch (ListaryException error)
        {
            // Agent errors can include indexed root paths or other private
            // machine details. Status is UI-visible: keep the diagnostic code,
            // but never display an untrusted remote message verbatim.
            var message = error.Code switch
            {
                "not_running" => "Indexed search is offline. Start the Listary agent, then try again.",
                "endpoint_version" => "Indexed search needs a compatible Listary agent.",
                "endpoint_invalid" or "endpoint_unreadable" => "The Listary connection descriptor could not be verified.",
                "timeout" => "Indexed search did not respond in time. Try again.",
                _ => "The Listary connection could not be verified. Refresh and try again."
            };
            return new(false, message, error.Code);
        }
    }

    /// <summary>
    /// Distinguish a running companion from genuinely usable indexed roots.
    /// This is an authenticated, read-only status query; only Listary settings
    /// may grant or change indexed-root permissions.
    /// </summary>
    public async Task<ListaryServiceStatus> GetIndexReadinessAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            var endpoint = await ReadEndpointAsync(lifetime.Token).ConfigureAwait(false);
            var data = await SendAsync(endpoint, "list_roots", null, null, lifetime.Token,
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 4096)
                return new(false, "Indexed folder configuration could not be verified.", "roots_unverified");
            var enabled = 0;
            var available = 0;
            foreach (var root in data.EnumerateArray())
            {
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("enabled", out var enabledValue) ||
                    enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !root.TryGetProperty("availability", out var availability) ||
                    availability.ValueKind != JsonValueKind.String)
                    return new(false, "Indexed folder configuration could not be verified.", "roots_unverified");
                var state = availability.GetString();
                if (state is not ("available" or "unavailable" or "stale"))
                    return new(false, "Indexed folder configuration could not be verified.", "roots_unverified");
                if (!enabledValue.GetBoolean()) continue;
                enabled++;
                if (state == "available") available++;
            }
            if (data.GetArrayLength() == 0)
                return new(false, "No indexed folders selected. Choose folders in Listary settings before searching files.", "roots_not_configured");
            if (enabled == 0)
                return new(false, "All indexed folders are disabled in Listary settings.", "roots_disabled");
            if (available == 0)
                return new(false, "Indexed folders are unavailable or stale. Check their locations in Listary settings.", "roots_unavailable");
            return new(true, "Indexed search has configured, available folders.");
        }
        catch (ListaryException error)
        {
            // The remote failure may carry a private root path. Show only a
            // fixed explanation; retain the machine-readable code for UI/tests.
            return new(false, "Indexed folder readiness could not be verified. Check the Listary connection.", error.Code);
        }
    }

    public Task<ListarySearchPage> SearchAsync(string query, int limit = 100,
        CancellationToken cancellationToken = default) =>
        SearchPageAsync(new(query, Limit: limit), cancellationToken);

    public async Task<ListarySearchPage> SearchPageAsync(ListarySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = lifetime.Token;
        var normalized = request with
        {
            NowUnixMilliseconds = request.NowUnixMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TimezoneOffsetMinutes = request.TimezoneOffsetMinutes ?? (int)DateTimeOffset.Now.Offset.TotalMinutes,
            Categories = request.Categories?.ToArray() ?? []
        };
        var endpoint = await ReadEndpointAsync(token).ConfigureAwait(false);
        RequireEndpointIdentity(endpoint, request.ExpectedEndpointIdentity);
        var requestId = RandomRequestId();
        var parameters = new
        {
            query = normalized.Query,
            include_unavailable = false,
            root_ids = Array.Empty<string>(),
            categories = normalized.Categories,
            modified_time = normalized.ModifiedTime,
            now_unix_ms = normalized.NowUnixMilliseconds,
            timezone_offset_minutes = normalized.TimezoneOffsetMinutes,
            offset = normalized.Offset,
            limit = normalized.Limit,
            sort_field = normalized.SortField,
            sort_direction = normalized.SortDirection,
            expected_catalog_generation = normalized.ExpectedCatalogGeneration
        };
        JsonElement data;
        try
        {
            data = await SendAsync(endpoint, "file_search", requestId, parameters, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryCancelAsync(endpoint, requestId).ConfigureAwait(false);
            throw;
        }
        catch (ListaryException error) when (error.Code is "timeout" or "unavailable")
        {
            await TryCancelAsync(endpoint, requestId).ConfigureAwait(false);
            throw;
        }
        token.ThrowIfCancellationRequested();
        if (data.ValueKind != JsonValueKind.Object ||
            !TryInt64(data, "total_matches", out var total) || total < 0 ||
            !TryInt64(data, "offset", out var offset) || offset != normalized.Offset ||
            !TryInt64(data, "limit", out var limit) || limit != normalized.Limit ||
            !data.TryGetProperty("catalog_generation", out var generationValue) || generationValue.ValueKind != JsonValueKind.Number || !generationValue.TryGetUInt64(out var generation) ||
            !data.TryGetProperty("has_more", out var moreValue) || moreValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !data.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw InvalidResponse("Indexed search returned an invalid page.");
        if (normalized.ExpectedCatalogGeneration is { } expected && generation != expected)
            throw new ListaryException("stale_catalog", "The indexed catalog changed. Refresh the search.");
        var rowCount = rows.GetArrayLength();
        if (rowCount > normalized.Limit || total < rowCount || offset > long.MaxValue - rowCount ||
            (rowCount > 0 && offset + rowCount > total) ||
            moreValue.GetBoolean() != (offset + rowCount < total) ||
            (moreValue.GetBoolean() && rowCount != normalized.Limit))
            throw InvalidResponse("Indexed search returned inconsistent paging information.");
        var results = new List<ListarySearchResult>(rowCount);
        var rejected = 0;
        var seen = new HashSet<uint>();
        foreach (var row in rows.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("item", out var item) ||
                !TryParseItem(item, endpoint.Identity, out var parsed) || !seen.Add(parsed.ItemId))
            {
                rejected++;
                continue;
            }
            results.Add(parsed);
        }
        return new(results.AsReadOnly(), total, offset, (int)limit, moreValue.GetBoolean(), generation,
            normalized, endpoint.Identity, rejected);
    }

    public async Task<FileEntry> RevalidateAsync(ListarySearchResult selected,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(selected);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = lifetime.Token;
        var endpoint = await ReadEndpointAsync(token).ConfigureAwait(false);
        RequireEndpointIdentity(endpoint, selected.EndpointIdentity);
        var data = await SendAsync(endpoint, "get_item", null, new { item_id = selected.ItemId }, token).ConfigureAwait(false);
        if (!TryParseItem(data, endpoint.Identity, out var current)) throw StaleItem();
        var currentPathChanged = !string.Equals(current.Entry.FullPath, selected.Entry.FullPath, PathComparison);
        var strongStableIdentity = !string.IsNullOrWhiteSpace(selected.StableFileId) &&
            string.Equals(current.StableFileId, selected.StableFileId, StringComparison.Ordinal);
        if (
            current.ItemId != selected.ItemId || current.RootId != selected.RootId ||
            (currentPathChanged && !strongStableIdentity) ||
            current.IndexedKind != selected.IndexedKind ||
            !string.Equals(current.StableFileId, selected.StableFileId, StringComparison.Ordinal) ||
            current.IndexedSize != selected.IndexedSize ||
            current.IndexedModifiedUnixMilliseconds != selected.IndexedModifiedUnixMilliseconds)
            throw StaleItem();
        if (!await MetadataSlots.WaitAsync(_requestTimeout, token).ConfigureAwait(false))
            throw new ListaryException("timeout", "File verification is busy. Wait briefly, then try again.");
        var nativeRead = Task.Run(() =>
        {
            try { return ReadNativeEntry(current); }
            finally { MetadataSlots.Release(); }
        });
        _ = nativeRead.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var actual = await nativeRead.WaitAsync(_requestTimeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return actual;
        }
        catch (TimeoutException error)
        {
            throw new ListaryException("timeout", "The selected file did not respond in time. Refresh and try again.", error);
        }
    }

    public async Task RecordActivationAsync(ListarySearchResult selected,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(selected);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var endpoint = await ReadEndpointAsync(lifetime.Token).ConfigureAwait(false);
        RequireEndpointIdentity(endpoint, selected.EndpointIdentity);
        var data = await SendAsync(endpoint, "record_activation", null, new
        {
            item_id = selected.ItemId,
            now_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            source = "file_manager"
        }, lifetime.Token).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("recorded", out var recorded) ||
            recorded.ValueKind != JsonValueKind.True)
            throw InvalidResponse("Indexed search did not confirm the activation history update.");
    }

    private async Task TryCancelAsync(Endpoint endpoint, ulong requestId)
    {
        try
        {
            await SendAsync(endpoint, "cancel", requestId, null, CancellationToken.None,
                TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
        }
        catch (ListaryException) { }
        catch (OperationCanceledException) { }
    }

    private async Task<Endpoint> ReadEndpointAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(_endpointPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumDescriptorBytes)
                throw new ListaryException("endpoint_invalid", "The indexed search descriptor is too large.");
            using var bytes = new MemoryStream();
            var chunk = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > MaximumDescriptorBytes)
                    throw new ListaryException("endpoint_invalid", "The indexed search descriptor is too large.");
                bytes.Write(chunk, 0, count);
            }
            using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 16 });
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object ||
                !TryInt64(data, "schema_version", out var schema) || schema != 1 ||
                !TryInt64(data, "protocol_version", out var protocol) || protocol != 1)
                throw new ListaryException("endpoint_version", "Indexed search needs a compatible Listary agent.");
            if (!TryInt64(data, "process_id", out var process) || process <= 0 ||
                !TryString(data, "address", out var address) || address.Length > 128 ||
                !IPEndPoint.TryParse(address, out var socket) ||
                !IPAddress.IsLoopback(socket.Address) || socket.Port is < 1 or > 65535 ||
                !TryString(data, "token", out var token) || token.Length is < 16 or > 1024 ||
                token.Any(char.IsControl))
                throw new ListaryException("endpoint_invalid", "The indexed search descriptor failed local authentication checks.");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(socket + "\n" + token)));
            return new(socket, token, identity);
        }
        catch (FileNotFoundException error)
        {
            throw new ListaryException("not_running", "Indexed search is offline. Start the Listary agent, then try again.", error);
        }
        catch (DirectoryNotFoundException error)
        {
            throw new ListaryException("not_running", "Indexed search is offline. Start the Listary agent, then try again.", error);
        }
        catch (JsonException error)
        {
            throw new ListaryException("endpoint_invalid", "The indexed search descriptor contains invalid data.", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ListaryException("endpoint_unreadable", "The indexed search descriptor could not be read.", error);
        }
    }

    private async Task<JsonElement> SendAsync(Endpoint endpoint, string method, ulong? requestId,
        object? parameters, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var correlation = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            token = endpoint.Token, id = correlation, method, request_id = requestId, @params = parameters
        });
        if (request.Length >= MaximumRequestBytes)
            throw new ListaryException("request_too_large", "The indexed search request is too large.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? _requestTimeout);
        try
        {
            using var client = new TcpClient(endpoint.Socket.AddressFamily) { NoDelay = true };
            await client.ConnectAsync(endpoint.Socket.Address, endpoint.Socket.Port, deadline.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token).ConfigureAwait(false);
            var frame = await ReadFrameAsync(stream, deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(frame, new() { MaxDepth = 64 });
            var response = document.RootElement;
            if (response.ValueKind != JsonValueKind.Object ||
                !TryString(response, "id", out var actualId) || actualId != correlation ||
                !response.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw InvalidResponse("Indexed search returned a mismatched response.");
            if (!ok.GetBoolean())
            {
                var code = "agent_error";
                if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    if (TryString(error, "code", out var reportedCode) && reportedCode.Length is > 0 and <= 64 &&
                        reportedCode.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                        code = reportedCode;
                }
                // Authenticated agent errors are still untrusted UI text: a
                // remote message may include an indexed root or private file
                // path. Preserve validated codes, not remote prose, for every
                // search, revalidation and activation caller.
                var safeMessage = code switch
                {
                    "stale_catalog" => "The indexed catalog changed. Refresh the search.",
                    "stale_endpoint" => "Indexed search restarted. Refresh the search before continuing.",
                    "stale_item" or "not_found" => "The selected indexed file changed or is unavailable. Refresh the search.",
                    "cancelled" => "Indexed search was cancelled.",
                    "timeout" => "Indexed search did not respond in time. Try again.",
                    "unavailable" => "Indexed search is unavailable. Refresh after the agent is running.",
                    "authentication_failed" or "permission_denied" => "The Listary agent rejected this request. Check the connection and permissions.",
                    "invalid_request" or "invalid_argument" => "The Listary agent rejected the request. Check the search options.",
                    _ => "Indexed search could not complete the request."
                };
                throw new ListaryException(code, safeMessage);
            }
            if (!response.TryGetProperty("data", out var data))
                throw InvalidResponse("Indexed search returned no result data.");
            return data.Clone();
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ListaryException("timeout", "Indexed search did not respond in time. Try again.", error);
        }
        catch (JsonException error)
        {
            throw new ListaryException("invalid_response", "Indexed search returned invalid JSON.", error);
        }
        catch (Exception error) when (error is IOException or SocketException)
        {
            throw new ListaryException("unavailable", "Indexed search is unavailable. Refresh after the agent is running.", error);
        }
    }

    private async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(Math.Min(16384, _maximumResponseBytes + 1));
        using var output = new MemoryStream();
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length,
                    _maximumResponseBytes + 1 - (int)output.Length)), cancellationToken).ConfigureAwait(false);
                if (count == 0) throw InvalidResponse("Indexed search returned an incomplete response frame.");
                var newline = Array.IndexOf(chunk, (byte)'\n', 0, count);
                var take = newline < 0 ? count : newline;
                if (output.Length + take > _maximumResponseBytes)
                    throw new ListaryException("response_too_large", "Indexed search returned an oversized response.");
                output.Write(chunk, 0, take);
                if (newline >= 0) return output.ToArray();
                if (output.Length >= _maximumResponseBytes)
                    throw new ListaryException("response_too_large", "Indexed search returned an oversized response.");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
    }

    private static bool TryParseItem(JsonElement item, string endpointIdentity, out ListarySearchResult result)
    {
        result = null!;
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetUInt32(out var id) || id == 0 ||
            !TryString(item, "root_id", out var rootText) || !Guid.TryParse(rootText, out var rootId) || rootId == Guid.Empty ||
            !TryString(item, "path", out var path) || string.IsNullOrWhiteSpace(path) || path.Length > 32768 ||
            !TryString(item, "name", out var name) || string.IsNullOrEmpty(name) ||
            !TryString(item, "kind", out var kind) || kind is not ("file" or "folder" or "app" or "package") ||
            !TryInt64(item, "size", out var size) || size < 0 ||
            !TryInt64(item, "modified_unix_ms", out var modified) ||
            !item.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True)
            return false;
        if (TryString(item, "preview_hint", out var hint) &&
            hint.StartsWith("workspace_application_v1:", StringComparison.Ordinal))
            return false;
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.Contains('\0') ||
                path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\\?\pipe\", StringComparison.OrdinalIgnoreCase))
                return false;
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                name, PathComparison) || Path.GetExtension(fullPath).Equals(".workspaceapp", StringComparison.OrdinalIgnoreCase))
                return false;
            if (TryString(item, "parent_path", out var parent) &&
                (!Path.IsPathFullyQualified(parent) ||
                !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                    Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(fullPath)) ?? ""),
                    PathComparison)))
                return false;
            string? stableId = null;
            if (item.TryGetProperty("stable_file_id", out var stable) && stable.ValueKind != JsonValueKind.Null)
            {
                if (stable.ValueKind != JsonValueKind.String) return false;
                stableId = stable.GetString();
                if (stableId?.Length > 4096) return false;
            }
            var modifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(modified);
            var directory = kind is "folder" or "package" ||
                kind == "app" && fullPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
            var package = Browsing.DirectoryBrowser.IsPackagePath(fullPath, directory);
            var entry = new FileEntry(fullPath, name, directory, false, size, modifiedUtc,
                DateTimeOffset.UnixEpoch, directory ? FileAttributes.Directory : FileAttributes.Normal, package);
            result = new(entry, id, rootId, stableId, kind, endpointIdentity, size, modified);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static FileEntry ReadNativeEntry(ListarySearchResult current)
    {
        try
        {
            var entry = Browsing.DirectoryBrowser.ReadEntry(current.Entry.FullPath);
            // A nested junction/symlink is indexed as its own file occurrence;
            // native browsing may classify a directory link as a directory.
            if ((!entry.IsSymbolicLink && entry.IsDirectory != current.Entry.IsDirectory) ||
                (!entry.IsDirectory && !entry.IsSymbolicLink && entry.Length != current.IndexedSize) ||
                entry.ModifiedUtc.ToUnixTimeMilliseconds() != current.IndexedModifiedUnixMilliseconds)
                throw StaleItem();
            // Rust's indexer stores the underlying filesystem object's identity.
            // A path can be replaced with equal-size/equal-mtime bytes before the
            // watcher publishes its next generation. Native actions must refuse it.
            if (string.IsNullOrWhiteSpace(current.StableFileId) ||
                !string.Equals(ReadStableFileIdentity(entry.FullPath), current.StableFileId, StringComparison.Ordinal))
                throw StaleItem();
            return entry;
        }
        catch (ListaryException) { throw; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw StaleItem(error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ListaryException("item_unavailable", "The selected file could not be verified. Refresh and try again.", error);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            throw new ListaryException("item_unavailable", "Native file identity verification is unavailable. Refresh and try again.", error);
        }
    }

    // Mirror only the platform identity *format* produced by
    // rust/listary-core/src/indexer.rs::stable_file_id. The Rust agent remains
    // the sole catalog, ranker, generation, and filesystem watcher owner.
    // Use this existing native identity implementation at other file-action
    // admission sites, without exposing catalog/permission capabilities or
    // changing its private indexed-result reflection/test contract.
    public static string? ReadNativeItemIdentity(string path) => ReadStableFileIdentity(path);

    private static string? ReadStableFileIdentity(string path) => ReadNativeItemIdentity(path, out _);

    internal static string? ReadNativeItemIdentity(string path, out bool isOffline)
    {
        isOffline = false;
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenWindowsFileForIdentity(Platform.WindowsFileOperations.NativePath(Path.GetFullPath(path)), 0x80, 0x7, 0, 3,
                0x02000000 | 0x00200000, 0);
            if (handle.IsInvalid || !GetWindowsFileInformation(handle, out var info)) return null;
            isOffline = (info.Attributes & (uint)FileAttributes.Offline) != 0;
            var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return $"windows:{info.VolumeSerialNumber:x8}:{fileIndex:x16}";
        }
        if (OperatingSystem.IsMacOS())
        {
            // Darwin arm64/x64 struct stat: dev_t at offset 0, ino_t at offset 8.
            // Read lstat so a link can never be silently replaced by its target.
            var buffer = Marshal.AllocHGlobal(256);
            try
            {
                if (DarwinLStat(path, buffer) != 0) return null;
                // Darwin stat.st_flags at offset 116, SF_DATALESS.
                isOffline = (Marshal.ReadInt32(buffer, 116) & 0x40000000) != 0;
                var mode = (ushort)Marshal.ReadInt16(buffer, 4);
                if ((mode & 0xf000) == 0xa000) return null; // symbolic link
                var device = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
                return $"unix:{device:x}:{inode:x}";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null; // Unsupported platforms must not grant indexed action authority.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
        public System.Runtime.InteropServices.ComTypes.FILETIME Access;
        public System.Runtime.InteropServices.ComTypes.FILETIME Write;
        public uint VolumeSerialNumber;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Links;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenWindowsFileForIdentity(
        string path, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flags, nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowsFileInformation(SafeFileHandle handle, out WindowsFileInformation info);

    [DllImport("libSystem.B.dylib", EntryPoint = "lstat", SetLastError = true)]
    private static extern int DarwinLStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint statBuffer);

    private static void ValidateRequest(ListarySearchRequest request)
    {
        if (request.Query is null || request.Query.Length > 16384) throw new ArgumentException("The search query is too long.", nameof(request));
        if (request.Limit is < 1 or > 1000 || request.Offset < 0 || request.Offset > long.MaxValue - request.Limit)
            throw new ArgumentOutOfRangeException(nameof(request), "Search pages must have 1 to 1000 rows and a nonnegative offset.");
        if (request.SortField is not ("relevance" or "name" or "path" or "kind" or "modified" or "size") ||
            request.SortDirection is not ("ascending" or "descending"))
            throw new ArgumentException("Unsupported indexed search sort.", nameof(request));
        if (request.Categories?.Any(category => category is not ("folder" or "document" or "picture" or "video" or "audio" or "other")) == true ||
            request.ModifiedTime is not (null or "last_1_day" or "last_7_days" or "last_30_days" or "last_365_days" or "older"))
            throw new ArgumentException("Unsupported indexed search facet.", nameof(request));
        if (request.TimezoneOffsetMinutes is < -1440 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid timezone offset.");
        if (request.NowUnixMilliseconds is { } now) _ = DateTimeOffset.FromUnixTimeMilliseconds(now);
        if (request.Offset > 0 && (request.ExpectedCatalogGeneration is null ||
            string.IsNullOrEmpty(request.ExpectedEndpointIdentity) || request.NowUnixMilliseconds is null || request.TimezoneOffsetMinutes is null))
            throw new ArgumentException("Use the previous page's NextRequest to continue a search snapshot.", nameof(request));
    }

    private static void RequireEndpointIdentity(Endpoint endpoint, string? expected)
    {
        if (expected is not null && expected != endpoint.Identity)
            throw new ListaryException("stale_endpoint", "Indexed search restarted. Refresh the search before continuing.");
    }

    private static ulong RandomRequestId()
    {
        var bytes = RandomNumberGenerator.GetBytes(sizeof(ulong));
        var value = BitConverter.ToUInt64(bytes) & (ulong)long.MaxValue;
        return value == 0 ? 1UL : value;
    }

    private static bool TryInt64(JsonElement element, string property, out long value)
    {
        value = 0;
        return element.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out value);
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = "";
        if (!element.TryGetProperty(property, out var field) || field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString()!;
        return true;
    }

    private static ListaryException InvalidResponse(string message) => new("invalid_response", message);
    private static ListaryException StaleItem(Exception? error = null) =>
        new("stale_item", "The selected indexed file changed or is unavailable. Refresh the search before acting.", error);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private sealed record Endpoint(IPEndPoint Socket, string Token, string Identity);
}
