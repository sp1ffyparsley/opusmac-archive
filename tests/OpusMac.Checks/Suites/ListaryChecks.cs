using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using OpusMac.Core.Search;

namespace OpusMac.Checks.Suites;

/// <summary>Protocol and local metadata fixtures. These checks never contact the installed agent.</summary>
public sealed class ListaryChecks : ICheckSuite
{
    public async Task RunAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Listary transport status never exposes private paths from agent errors", async () =>
        {
            var state = NewDirectory(sandbox, "status-private-error");
            const string privatePath = @"C:\Users\PrivateOwner\SecretCollection\index.db";
            await using var agent = new FixtureAgent(state, (_, _) =>
                Task.FromResult(Reply.Failure("unavailable", $"Agent failed while reading {privatePath}")));
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var status = await client.GetStatusAsync();
            CheckRunner.Equal("unavailable", status.ErrorCode);
            CheckRunner.True(!status.IsAvailable, "An agent error cannot report a healthy transport.");
            CheckRunner.True(!status.Message.Contains(privatePath, StringComparison.Ordinal) &&
                !status.Message.Contains("PrivateOwner", StringComparison.Ordinal),
                "Status messaging must not disclose private agent-side paths.");
        });

        await checks.CheckAsync("Listary search failures hide remote paths and allow a later healthy search", async () =>
        {
            var state = NewDirectory(sandbox, "search-private-error");
            const string privatePath = @"C:\Users\PrivateOwner\PrivateIndex\top-secret.txt";
            await using var agent = new FixtureAgent(state, (_, _) =>
                Task.FromResult(Reply.Failure("stale_catalog", $"Catalog changed at {privatePath}")));
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            try
            {
                await client.SearchAsync("private");
                throw new InvalidOperationException("A failed agent search must not report success.");
            }
            catch (ListaryException error)
            {
                CheckRunner.Equal("stale_catalog", error.Code);
                CheckRunner.True(error.Message.Contains("Refresh", StringComparison.OrdinalIgnoreCase),
                    "A stale catalog must retain useful refresh guidance.");
                CheckRunner.True(!error.Message.Contains(privatePath, StringComparison.Ordinal) &&
                    !error.Message.Contains("PrivateOwner", StringComparison.Ordinal),
                    "Search error messages must not expose agent-side indexed paths.");
            }
            agent.Handler = (request, _) => Task.FromResult(Reply.Success(Page(request, [], 11)));
            var recovered = await client.SearchAsync("next search");
            CheckRunner.Equal(0, recovered.Results.Count);
            CheckRunner.Equal(0L, recovered.TotalMatches);
        });

        await checks.CheckAsync("Listary revalidation and activation failures hide remote file details", async () =>
        {
            var state = NewDirectory(sandbox, "action-private-errors");
            const string privatePath = @"C:\Users\PrivateOwner\PrivateIndex\action.txt";
            var item = await CreateItemAsync(state, "safe.txt", 1);
            await using var agent = new FixtureAgent(state, (request, _) =>
            {
                var method = request.GetProperty("method").GetString();
                return Task.FromResult(method switch
                {
                    "file_search" => Reply.Success(Page(request, [item], 4)),
                    "get_item" => Reply.Failure("stale_item", $"Indexed file was replaced: {privatePath}"),
                    "record_activation" => Reply.Failure("permission_denied", $"History not writable for {privatePath}"),
                    _ => throw new InvalidOperationException("Unexpected fixture method.")
                });
            });
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var selected = (await client.SearchAsync("safe")).Results.Single();
            foreach (var (expectedCode, operation) in new (string, Func<Task>)[]
            {
                ("stale_item", () => client.RevalidateAsync(selected)),
                ("permission_denied", () => client.RecordActivationAsync(selected))
            })
            {
                try
                {
                    await operation();
                    throw new InvalidOperationException("A failed Listary action must not report success.");
                }
                catch (ListaryException error)
                {
                    CheckRunner.Equal(expectedCode, error.Code);
                    CheckRunner.True(!error.Message.Contains(privatePath, StringComparison.Ordinal) &&
                        !error.Message.Contains("PrivateOwner", StringComparison.Ordinal),
                        "An action error must not disclose the agent's private indexed file path.");
                }
            }
            CheckRunner.True(File.Exists((string)item["path"]!),
                "Failed actions must leave the disposable indexed source intact.");
        });

        await checks.CheckAsync("Listary indexed-root readiness distinguishes rootless, disabled, offline and usable catalogs", async () =>
        {
            var state = NewDirectory(sandbox, "root-readiness");
            object[] roots = [];
            await using var agent = new FixtureAgent(state, (request, _) =>
            {
                CheckRunner.Equal("list_roots", request.GetProperty("method").GetString(),
                    "Root readiness must query only the existing authenticated, read-only root list.");
                return Task.FromResult(Reply.Success(roots));
            });
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var rootless = await client.GetIndexReadinessAsync();
            CheckRunner.True(!rootless.IsAvailable && rootless.ErrorCode == "roots_not_configured",
                "An available endpoint with zero selected roots is not usable indexed search.");
            CheckRunner.True(rootless.Message.Contains("Choose", StringComparison.Ordinal),
                "Rootless diagnosis should explain the human-selected root requirement.");
            roots = [new { enabled = true, availability = "available", path = "C:/private" }, new { enabled = false, availability = "unavailable" }];
            CheckRunner.True((await client.GetIndexReadinessAsync()).IsAvailable,
                "A valid enabled root stays usable when unrelated roots are offline or disabled.");
            roots = [new { enabled = false, availability = "available", path = "C:/private/case-name" }];
            var disabled = await client.GetIndexReadinessAsync();
            CheckRunner.Equal("roots_disabled", disabled.ErrorCode);
            CheckRunner.True(!disabled.Message.Contains("private", StringComparison.Ordinal),
                "Root diagnostics must not expose private root paths.");
            roots = [new { enabled = true, availability = "unavailable", path = "C:/private/offline-root" }];
            CheckRunner.Equal("roots_unavailable", (await client.GetIndexReadinessAsync()).ErrorCode);
            roots = [new { enabled = true, availability = "stale" }];
            CheckRunner.Equal("roots_unavailable", (await client.GetIndexReadinessAsync()).ErrorCode);
            roots = [new { enabled = true, availability = "available" }];
            var ready = await client.GetIndexReadinessAsync();
            CheckRunner.True(ready.IsAvailable && ready.ErrorCode is null &&
                ready.Message.Contains("configured", StringComparison.OrdinalIgnoreCase),
                "A genuinely available enabled root must not show rootless messaging.");
            roots = [new { enabled = true, availability = "mystery" }];
            CheckRunner.Equal("roots_unverified", (await client.GetIndexReadinessAsync()).ErrorCode);
            roots = [new { enabled = "true", availability = "available" }];
            CheckRunner.Equal("roots_unverified", (await client.GetIndexReadinessAsync()).ErrorCode);
            roots = [new { enabled = true, availability = "available", path = "C:/private" }];
            agent.Handler = (_, _) => Task.FromResult(Reply.Failure("unavailable", "C:/private/absolute-index-details"));
            var unavailable = await client.GetIndexReadinessAsync();
            CheckRunner.Equal("unavailable", unavailable.ErrorCode);
            CheckRunner.True(!unavailable.Message.Contains("private", StringComparison.Ordinal),
                "Remote errors must not leak paths from index configuration.");
            CheckRunner.True(agent.Requests.All(r => r.GetProperty("method").GetString() == "list_roots"),
                "Readiness must not mutate roots or attempt broad search.");
        });

        await checks.CheckAsync("Listary root-status cancellation leaves the next indexed search usable", async () =>
        {
            var state = NewDirectory(sandbox, "root-status-cancel");
            var started = NewSignal();
            var held = NewSignal();
            await using var agent = new FixtureAgent(state, async (request, token) =>
            {
                if (request.GetProperty("method").GetString() == "list_roots")
                {
                    started.TrySetResult(true);
                    await held.Task.WaitAsync(token);
                    return Reply.Success(Array.Empty<object>());
                }
                return Reply.Success(Page(request, [], 1));
            });
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            using var stop = new CancellationTokenSource();
            var pending = client.GetIndexReadinessAsync(stop.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var independent = await client.SearchAsync("fresh", 20).WaitAsync(TimeSpan.FromSeconds(3));
            CheckRunner.Equal(0, independent.Results.Count);
            stop.Cancel();
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            held.TrySetResult(true);
        });

        await checks.CheckAsync("Listary authenticated protocol, paging, native revalidation and activation", async () =>
        {
            var state = NewDirectory(sandbox, "protocol");
            var first = await CreateItemAsync(state, "Résumé βeta.txt", 1);
            var second = await CreateItemAsync(state, "snow scene.txt", 2);
            var third = await CreateItemAsync(state, "第三个.txt", 3);
            Dictionary<string, object?>[] rows = [first, second, third];
            var activations = 0;
            await using var agent = new FixtureAgent(state, (request, _) =>
            {
                var parameters = request.GetProperty("params");
                return Task.FromResult(request.GetProperty("method").GetString() switch
                {
                    "status" => Reply.Success(new { protocol_version = 1 }),
                    "file_search" => Reply.Success(Page(request, rows, 7)),
                    "get_item" => Reply.Success(rows.Single(row => (uint)row["id"]! == parameters.GetProperty("item_id").GetUInt32())),
                    "record_activation" => Activate(parameters, ref activations),
                    _ => throw new InvalidOperationException("Unexpected method in companion protocol test.")
                });
            });
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            CheckRunner.True((await client.GetStatusAsync()).IsAvailable, "Fixture status should be available.");
            CheckRunner.True(!(await client.GetStatusAsync()).Message.Contains("Indexed search is available", StringComparison.Ordinal),
                "Transport-health status must not claim configured search roots.");
            var page = await client.SearchAsync("scene", 2);
            CheckRunner.Equal(2, page.Results.Count);
            CheckRunner.Equal(3L, page.TotalMatches);
            CheckRunner.Equal(7UL, page.CatalogGeneration);
            CheckRunner.True(page.HasMore && page.NextRequest is not null, "First page must have a continuation.");
            CheckRunner.Equal((string)first["path"]!, page.Results[0].Entry.FullPath);
            CheckRunner.True(!page.EndpointIdentity.Contains(agent.Token, StringComparison.Ordinal), "Tokens must not be exposed in result metadata.");
            var continuation = page.NextRequest!;
            CheckRunner.Equal(page.Request.NowUnixMilliseconds, continuation.NowUnixMilliseconds);
            CheckRunner.Equal(page.Request.TimezoneOffsetMinutes, continuation.TimezoneOffsetMinutes);
            var next = await client.SearchPageAsync(continuation);
            CheckRunner.Equal(1, next.Results.Count);
            CheckRunner.Equal(2L, next.Offset);
            CheckRunner.True(!next.HasMore && next.NextRequest is null, "Last page must terminate paging.");
            var current = await client.RevalidateAsync(page.Results[0]);
            CheckRunner.Equal(new FileInfo(current.FullPath).Length, current.Length);
            CheckRunner.True(current.CreatedUtc != DateTimeOffset.UnixEpoch, "Revalidation must read native metadata.");
            await client.RecordActivationAsync(page.Results[0]);
            CheckRunner.Equal(1, activations);
            var searches = agent.Requests.Where(r => r.GetProperty("method").GetString() == "file_search").ToArray();
            var ids = searches.Select(r => r.GetProperty("request_id").GetUInt64()).ToArray();
            CheckRunner.True(ids.All(id => id != 0) && ids.Distinct().Count() == 2, "Concurrent consumers need nonzero unique request IDs.");
            CheckRunner.Equal(7UL, searches[1].GetProperty("params").GetProperty("expected_catalog_generation").GetUInt64());
            CheckRunner.Equal("relevance", searches[0].GetProperty("params").GetProperty("sort_field").GetString());
            CheckRunner.True(!searches[0].GetProperty("params").GetProperty("include_unavailable").GetBoolean(), "File manager results should omit unavailable indexed rows.");
        });

        await checks.CheckAsync("Listary endpoint rotation reuses client and invalidates old selections", async () =>
        {
            var state = NewDirectory(sandbox, "rotation");
            var rows = new[] { await CreateItemAsync(state, "first.txt", 1), await CreateItemAsync(state, "second.txt", 2) };
            await using var oldAgent = new FixtureAgent(state, (request, _) =>
                Task.FromResult(request.GetProperty("method").GetString() == "status"
                    ? Reply.Success(new { protocol_version = 1 }) : Reply.Success(Page(request, rows, 9))));
            await oldAgent.PublishAsync();
            using var client = new ListaryClient(state);
            var before = await client.SearchAsync("first", 1);
            await using var replacement = new FixtureAgent(state, (request, _) =>
                Task.FromResult(request.GetProperty("method").GetString() == "status"
                    ? Reply.Success(new { protocol_version = 1 }) : Reply.Success(Page(request, rows, 9))));
            await replacement.PublishAsync();
            CheckRunner.True((await client.GetStatusAsync()).IsAvailable, "Same client must discover replacement endpoint.");
            var after = await client.SearchAsync("first", 1);
            CheckRunner.True(before.EndpointIdentity != after.EndpointIdentity, "Per-run identity must rotate.");
            await ExpectCodeAsync("stale_endpoint", () => client.RevalidateAsync(before.Results[0]));
            await ExpectCodeAsync("stale_endpoint", () => client.SearchPageAsync(before.NextRequest!));
            CheckRunner.Equal(1, oldAgent.Requests.Count);
            CheckRunner.Equal(2, replacement.Requests.Count);
        });

        await checks.CheckAsync("Listary missing, malformed, remote and incompatible descriptors fail closed", async () =>
        {
            var state = NewDirectory(sandbox, "descriptors");
            using var client = new ListaryClient(state);
            CheckRunner.Equal("not_running", (await client.GetStatusAsync()).ErrorCode);
            var valid = new Dictionary<string, object?>
            {
                ["schema_version"] = 1, ["protocol_version"] = 1, ["process_id"] = Environment.ProcessId,
                ["address"] = "127.0.0.1:1", ["token"] = new string('a', 32)
            };
            foreach (var (key, value, code) in new (string, object, string)[]
            {
                ("address", "8.8.8.8:9911", "endpoint_invalid"),
                ("address", "localhost:9911", "endpoint_invalid"),
                ("address", "127.0.0.1:0", "endpoint_invalid"),
                ("address", "127.0.0.1:99999", "endpoint_invalid"),
                ("token", "short", "endpoint_invalid"),
                ("token", new string('x', 20) + "\n", "endpoint_invalid"),
                ("process_id", 0, "endpoint_invalid"),
                ("protocol_version", 2, "endpoint_version"),
                ("schema_version", "1", "endpoint_version")
            })
            {
                var invalid = new Dictionary<string, object?>(valid) { [key] = value };
                await File.WriteAllTextAsync(Path.Combine(state, "integration-endpoint-v1.json"), JsonSerializer.Serialize(invalid));
                var status = await client.GetStatusAsync();
                CheckRunner.True(!status.IsAvailable, $"Invalid descriptor field {key} must not connect.");
                CheckRunner.Equal(code, status.ErrorCode, $"Unexpected rejection for {key}");
            }
            await File.WriteAllTextAsync(Path.Combine(state, "integration-endpoint-v1.json"), "{malformed");
            CheckRunner.Equal("endpoint_invalid", (await client.GetStatusAsync()).ErrorCode);
            await File.WriteAllTextAsync(Path.Combine(state, "integration-endpoint-v1.json"), new string('x', 65537));
            CheckRunner.Equal("endpoint_invalid", (await client.GetStatusAsync()).ErrorCode);
        });

        await checks.CheckAsync("Listary cancellation uses a separate connection and original endpoint", async () =>
        {
            var state = NewDirectory(sandbox, "cancel");
            var started = NewSignal();
            var canceled = NewSignal();
            ulong activeId = 0;
            await using var oldAgent = new FixtureAgent(state, async (request, stop) =>
            {
                switch (request.GetProperty("method").GetString())
                {
                    case "file_search":
                        activeId = request.GetProperty("request_id").GetUInt64();
                        started.TrySetResult(true);
                        await canceled.Task.WaitAsync(stop);
                        return Reply.Failure("cancelled", "Cancelled fixture request.");
                    case "cancel":
                        CheckRunner.Equal(activeId, request.GetProperty("request_id").GetUInt64());
                        canceled.TrySetResult(true);
                        return Reply.Success(new { cancelled = true });
                    default: throw new InvalidOperationException("Unexpected cancellation method.");
                }
            });
            await oldAgent.PublishAsync();
            using var client = new ListaryClient(state);
            using var cancellation = new CancellationTokenSource();
            var pending = client.SearchAsync("long query", cancellationToken: cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await using var replacement = new FixtureAgent(state, (_, _) =>
                Task.FromResult(Reply.Success(new { protocol_version = 1 })));
            await replacement.PublishAsync();
            cancellation.Cancel();
            await CheckRunner.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            CheckRunner.True(activeId != 0, "Cancellable searches need an active request ID.");
            CheckRunner.Equal(0, replacement.Requests.Count, "Cancellation must not target a replacement process.");
            CheckRunner.Equal(2, oldAgent.Requests.Count, "Cancel should arrive while the original connection is busy.");
        });

        await checks.CheckAsync("Listary request timeout attempts bounded cancellation", async () =>
        {
            var state = NewDirectory(sandbox, "timeout");
            var canceled = NewSignal();
            await using var agent = new FixtureAgent(state, async (request, stop) =>
            {
                if (request.GetProperty("method").GetString() == "cancel")
                {
                    canceled.TrySetResult(true);
                    return Reply.Success(new { cancelled = true });
                }
                await canceled.Task.WaitAsync(stop);
                return Reply.Failure("cancelled", "Timed out fixture search.");
            });
            await agent.PublishAsync();
            using var client = new ListaryClient(state, TimeSpan.FromMilliseconds(150));
            await ExpectCodeAsync("timeout", () => client.SearchAsync("timeout").WaitAsync(TimeSpan.FromSeconds(3)));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        });

        await checks.CheckAsync("Listary response framing, correlation, numeric shape and byte bounds", async () =>
        {
            var state = NewDirectory(sandbox, "frames");
            await using var agent = new FixtureAgent(state, (_, _) =>
                Task.FromResult(Reply.RawFrame(new string('x', 2048) + "\n")));
            await agent.PublishAsync();
            using var client = new ListaryClient(state, maximumResponseBytes: 1024);
            await ExpectCodeAsync("response_too_large", () => client.SearchAsync("bounds"));
            agent.Handler = (_, _) => Task.FromResult(Reply.RawFrame("{broken\n"));
            await ExpectCodeAsync("invalid_response", () => client.SearchAsync("json"));
            agent.Handler = (_, _) => Task.FromResult(Reply.RawFrame("{\"id\":\"wrong\",\"ok\":true,\"data\":{}}\n"));
            await ExpectCodeAsync("invalid_response", () => client.SearchAsync("correlation"));
            agent.Handler = (_, _) => Task.FromResult(Reply.RawFrame("{\"id\":\"unfinished\"}"));
            await ExpectCodeAsync("invalid_response", () => client.SearchAsync("truncated"));
            agent.Handler = (request, _) => Task.FromResult(Reply.Success(new
            {
                total_matches = 0, offset = 0, limit = request.GetProperty("params").GetProperty("limit").GetInt32(),
                catalog_generation = "7", has_more = false, results = Array.Empty<object>()
            }));
            await ExpectCodeAsync("invalid_response", () => client.SearchAsync("number"));
            agent.Handler = (_, _) => Task.FromResult(Reply.Failure("authentication_failed", "Token " + agent.Token));
            try { await client.SearchAsync("redaction"); throw new InvalidOperationException("Expected agent error."); }
            catch (ListaryException error)
            {
                CheckRunner.Equal("authentication_failed", error.Code);
                CheckRunner.True(!error.Message.Contains(agent.Token, StringComparison.Ordinal), "Authentication tokens must not appear in errors.");
            }
            agent.Handler = (_, _) => Task.FromResult(Reply.Success(new { protocol_version = 1 }) with { FragmentSize = 1 });
            CheckRunner.True((await client.GetStatusAsync()).IsAvailable, "Fragmented TCP frames must be reassembled.");
        });

        await checks.CheckAsync("Listary stale catalogs preserve generation and search time across pages", async () =>
        {
            var state = NewDirectory(sandbox, "stale-page");
            var rows = new[] { await CreateItemAsync(state, "one.txt", 1), await CreateItemAsync(state, "two.txt", 2) };
            JsonElement firstParameters = default;
            await using var agent = new FixtureAgent(state, (request, _) =>
            {
                var parameters = request.GetProperty("params");
                if (parameters.GetProperty("offset").GetInt64() == 0)
                {
                    firstParameters = parameters.Clone();
                    return Task.FromResult(Reply.Success(Page(request, rows, 6)));
                }
                CheckRunner.Equal(6UL, parameters.GetProperty("expected_catalog_generation").GetUInt64());
                CheckRunner.Equal(firstParameters.GetProperty("now_unix_ms").GetInt64(), parameters.GetProperty("now_unix_ms").GetInt64());
                CheckRunner.Equal(firstParameters.GetProperty("timezone_offset_minutes").GetInt32(), parameters.GetProperty("timezone_offset_minutes").GetInt32());
                return Task.FromResult(Reply.Failure("stale_catalog", "The fixture catalog changed."));
            });
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var page = await client.SearchPageAsync(new("today", Limit: 1, ModifiedTime: "last_1_day"));
            await ExpectCodeAsync("stale_catalog", () => client.SearchPageAsync(page.NextRequest!));
            agent.Handler = (request, _) => Task.FromResult(Reply.Success(Page(request, rows, 99)));
            await ExpectCodeAsync("stale_catalog", () => client.SearchPageAsync(page.NextRequest!));
            await CheckRunner.ThrowsAsync<ArgumentException>(() =>
                client.SearchPageAsync(new("today", Offset: 1, ExpectedCatalogGeneration: 6)));
        });

        await checks.CheckAsync("Listary indexed identity and revision changes fail before actions", async () =>
        {
            var state = NewDirectory(sandbox, "identity");
            var original = await CreateItemAsync(state, "identity.txt", 1);
            Dictionary<string, object?>? current = original;
            await using var agent = new FixtureAgent(state, (request, _) =>
                Task.FromResult(request.GetProperty("method").GetString() == "file_search"
                    ? Reply.Success(Page(request, [original], 1)) : Reply.Success(current)));
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var selected = (await client.SearchAsync("identity")).Results[0];
            foreach (var (key, value) in new (string, object?)[]
            {
                ("id", 2), ("root_id", Guid.NewGuid()), ("stable_file_id", "replacement"),
                ("kind", "folder"), ("size", (long)original["size"]! + 1),
                ("modified_unix_ms", (long)original["modified_unix_ms"]! + 1), ("available", false)
            })
            {
                current = new(original) { [key] = value };
                await ExpectCodeAsync("stale_item", () => client.RevalidateAsync(selected));
            }
            var originalPath = (string)original["path"]!;
            var movedPath = Path.Combine(state, "moved identity.txt");
            File.Move(originalPath, movedPath);
            current = new(original)
            {
                ["path"] = movedPath, ["name"] = Path.GetFileName(movedPath)
            };
            CheckRunner.Equal(movedPath, (await client.RevalidateAsync(selected)).FullPath,
                "A same-item/root/kind/strong-stable-id rename must follow the fresh canonical path.");
            current = null;
            await ExpectCodeAsync("stale_item", () => client.RevalidateAsync(selected));
            File.Move(movedPath, originalPath);
            current = original;
            CheckRunner.Equal((string)original["path"]!, (await client.RevalidateAsync(selected)).FullPath);
        });

        await checks.CheckAsync("Listary moved paths require a non-empty strong stable identity", async () =>
        {
            var state = NewDirectory(sandbox, "weak-move-identity");
            var original = await CreateItemAsync(state, "weak identity.txt", 1);
            original["stable_file_id"] = null;
            Dictionary<string, object?> current = original;
            await using var agent = new FixtureAgent(state, (request, _) =>
                Task.FromResult(request.GetProperty("method").GetString() == "file_search"
                    ? Reply.Success(Page(request, [original], 1)) : Reply.Success(current)));
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var selected = (await client.SearchAsync("weak identity")).Results[0];
            var originalPath = (string)original["path"]!;
            var movedPath = Path.Combine(state, "weak moved.txt");
            File.Move(originalPath, movedPath);
            current = new(original) { ["path"] = movedPath, ["name"] = Path.GetFileName(movedPath) };
            await ExpectCodeAsync("stale_item", () => client.RevalidateAsync(selected));
        });

        await checks.CheckAsync("Listary native deletion and post-index file changes are rejected", async () =>
        {
            var state = NewDirectory(sandbox, "native");
            var original = await CreateItemAsync(state, "changing.txt", 1);
            await using var agent = new FixtureAgent(state, (request, _) =>
                Task.FromResult(request.GetProperty("method").GetString() == "file_search"
                    ? Reply.Success(Page(request, [original], 1)) : Reply.Success(original)));
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var selected = (await client.SearchAsync("changing")).Results[0];
            await File.WriteAllTextAsync((string)original["path"]!, "A replacement file with a different length.");
            await ExpectCodeAsync("stale_item", () => client.RevalidateAsync(selected));
            File.Delete((string)original["path"]!);
            await ExpectCodeAsync("stale_item", () => client.RevalidateAsync(selected));
        });

        await checks.CheckAsync("Listary same-path equal-size and equal-mtime replacement rejects stale indexed identity", async () =>
        {
            var state = NewDirectory(sandbox, "same-path-replaced-object");
            var indexed = await CreateItemAsync(state, "stable.txt", 1);
            var indexedPath = (string)indexed["path"]!;
            var oldBytes = await File.ReadAllBytesAsync(indexedPath);
            await using var agent = new FixtureAgent(state, (request, _) =>
                Task.FromResult(request.GetProperty("method").GetString() == "file_search"
                    ? Reply.Success(Page(request, [indexed], 1)) : Reply.Success(indexed)));
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var selected = (await client.SearchAsync("stable")).Results.Single();
            var originalNativeIdentity = ReadNativeIdentity(indexedPath);
            CheckRunner.True(!string.IsNullOrWhiteSpace(originalNativeIdentity),
                "Original object must have a native stable identity.");
            CheckRunner.Equal(originalNativeIdentity, selected.StableFileId);
            var stamp = File.GetLastWriteTimeUtc(indexedPath);
            var replacement = Path.Combine(state, "replacement.tmp");
            var changedBytes = Enumerable.Repeat((byte)'Z', oldBytes.Length).ToArray();
            CheckRunner.True(!oldBytes.SequenceEqual(changedBytes), "Replacement bytes must change.");
            await File.WriteAllBytesAsync(replacement, changedBytes);
            File.SetLastWriteTimeUtc(replacement, stamp);
            File.Move(indexedPath, Path.Combine(state, "original-moved.txt"));
            File.Move(replacement, indexedPath);
            File.SetLastWriteTimeUtc(indexedPath, stamp);
            CheckRunner.Equal(selected.IndexedSize, new FileInfo(indexedPath).Length);
            CheckRunner.Equal(selected.IndexedModifiedUnixMilliseconds,
                new DateTimeOffset(File.GetLastWriteTimeUtc(indexedPath)).ToUnixTimeMilliseconds());
            CheckRunner.True(!string.Equals(originalNativeIdentity, ReadNativeIdentity(indexedPath), StringComparison.Ordinal),
                "Replacement at the same path must have a different native file identity.");
            await ExpectCodeAsync("stale_item", () => client.RevalidateAsync(selected));
        });

        await checks.CheckAsync("Listary native actions fail closed without a canonical file identity", async () =>
        {
            var state = NewDirectory(sandbox, "invalid-native-identity");
            var canonical = await CreateItemAsync(state, "identity.txt", 1);
            Dictionary<string, object?> current = canonical;
            await using var agent = new FixtureAgent(state, (request, _) =>
                Task.FromResult(request.GetProperty("method").GetString() == "file_search"
                    ? Reply.Success(Page(request, [current], 1)) : Reply.Success(current)));
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var positive = (await client.SearchAsync("identity")).Results.Single();
            CheckRunner.Equal(positive.Entry.FullPath, (await client.RevalidateAsync(positive)).FullPath);
            foreach (var invalidIdentity in new string?[] { null, "", "fixture:unverified-file", "windows:ffffffff:ffffffffffffffff" })
            {
                current = new(canonical) { ["stable_file_id"] = invalidIdentity };
                var selected = (await client.SearchAsync("identity")).Results.Single();
                await ExpectCodeAsync("stale_item", () => client.RevalidateAsync(selected));
            }
        });

        await checks.CheckAsync("Listary directory aliases validate the link object without following its target", async () =>
        {
            var state = NewDirectory(sandbox, "directory-alias");
            var target = NewDirectory(state, "actual-folder");
            await File.WriteAllTextAsync(Path.Combine(target, "child.txt"), "do not index or follow from alias");
            var alias = Path.Combine(state, "folder-alias");
            try { Directory.CreateSymbolicLink(alias, target); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                CheckRunner.NotVerified($"Disposable symlink creation is unavailable: {error.GetType().Name}");
            }
            var nativeLink = OpusMac.Core.Browsing.DirectoryBrowser.ReadEntry(alias);
            CheckRunner.True(nativeLink.IsSymbolicLink, "The fixture must be a real filesystem link.");
            var linkId = ReadNativeIdentity(alias);
            CheckRunner.True(!string.IsNullOrEmpty(linkId) &&
                !string.Equals(linkId, ReadNativeIdentity(target), StringComparison.Ordinal),
                "A directory link has its own indexed occurrence identity.");
            var item = new Dictionary<string, object?>
            {
                ["id"] = 1, ["root_id"] = Guid.NewGuid(), ["path"] = alias,
                ["parent_path"] = state, ["name"] = "folder-alias", ["extension"] = "",
                ["kind"] = "file", ["size"] = 0L,
                ["modified_unix_ms"] = nativeLink.ModifiedUtc.ToUnixTimeMilliseconds(),
                ["available"] = true, ["stable_file_id"] = linkId
            };
            await using var agent = new FixtureAgent(state, (request, _) =>
                Task.FromResult(request.GetProperty("method").GetString() == "file_search"
                    ? Reply.Success(Page(request, [item], 1)) : Reply.Success(item)));
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var selected = (await client.SearchAsync("folder-alias")).Results.Single();
            var current = await client.RevalidateAsync(selected);
            CheckRunner.Equal(alias, current.FullPath);
            CheckRunner.True(current.IsSymbolicLink, "The native selection must remain the link path.");
        });

        await checks.CheckAsync("Listary result projection excludes synthetic, unavailable and malformed paths", async () =>
        {
            var state = NewDirectory(sandbox, "projection");
            var valid = await CreateItemAsync(state, "valid.txt", 1);
            var rows = new List<Dictionary<string, object?>> { valid };
            var invalids = new (string, object?)[]
            {
                ("path", "relative.txt"), ("available", false), ("kind", "unknown"),
                ("name", "unrelated.txt"), ("size", ulong.MaxValue), ("id", "1"),
                ("preview_hint", "workspace_application_v1:{\"launch_path\":\"ignored\"}")
            };
            foreach (var (key, value) in invalids)
                rows.Add(new(valid) { ["id"] = (uint)rows.Count + 1, [key] = value });
            rows.Add(new(valid) { ["id"] = 90, ["path"] = Path.Combine(state, "provider.workspaceapp"), ["name"] = "provider.workspaceapp" });
            rows.Add(new(valid)); // Duplicate stable item must not produce duplicate action rows.
            await using var agent = new FixtureAgent(state, (request, _) => Task.FromResult(Reply.Success(Page(request, rows, 1))));
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var page = await client.SearchAsync("valid");
            CheckRunner.Equal(1, page.Results.Count);
            CheckRunner.Equal(rows.Count - 1, page.RejectedResults);
            CheckRunner.True(Path.IsPathFullyQualified(page.Results[0].Entry.FullPath), "Only native absolute filesystem paths should reach the file manager.");
        });

        await checks.CheckAsync("Listary folder projections use the shared native package classification", async () =>
        {
            var state = NewDirectory(sandbox, "packages");
            var packagePath = NewDirectory(state, "Fixture.app");
            var native = OpusMac.Core.Browsing.DirectoryBrowser.ReadEntry(packagePath);
            var item = new Dictionary<string, object?>
            {
                ["id"] = 1, ["root_id"] = Guid.NewGuid(), ["path"] = packagePath, ["parent_path"] = state,
                ["name"] = "Fixture.app", ["extension"] = "app", ["kind"] = "folder", ["size"] = 0,
                ["modified_unix_ms"] = native.ModifiedUtc.ToUnixTimeMilliseconds(),
                ["available"] = true, ["stable_file_id"] = ReadNativeIdentity(packagePath)
            };
            await using var agent = new FixtureAgent(state, (request, _) =>
                Task.FromResult(request.GetProperty("method").GetString() == "file_search"
                    ? Reply.Success(Page(request, [item], 1)) : Reply.Success(item)));
            await agent.PublishAsync();
            using var client = new ListaryClient(state);
            var selected = (await client.SearchAsync("Fixture")).Results[0];
            CheckRunner.True(selected.Entry.IsDirectory, "Folder-kind app rows must retain directory identity.");
            CheckRunner.Equal(native.IsPackage, selected.Entry.IsPackage);
            CheckRunner.Equal(OperatingSystem.IsMacOS(), selected.Entry.IsPackage);
            var revalidated = await client.RevalidateAsync(selected);
            CheckRunner.Equal(native.IsPackage, revalidated.IsPackage);
            CheckRunner.Equal(native.FullPath, revalidated.FullPath);
        });

        await checks.CheckAsync("Listary invalid limits and unsupported filters reject before networking", async () =>
        {
            using var client = new ListaryClient(NewDirectory(sandbox, "validation"));
            await CheckRunner.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchAsync("q", 1001));
            await CheckRunner.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchAsync("q", 0));
            await CheckRunner.ThrowsAsync<ArgumentException>(() => client.SearchPageAsync(new("q", SortField: "client_sort")));
            await CheckRunner.ThrowsAsync<ArgumentException>(() => client.SearchPageAsync(new("q", Categories: ["malformed"])));
            await CheckRunner.ThrowsAsync<ArgumentException>(() => client.SearchAsync(new string('q', 16385)));
            client.Dispose();
            await CheckRunner.ThrowsAsync<ObjectDisposedException>(() => client.SearchAsync("q"));
        });
    }

    private static Reply Activate(JsonElement parameters, ref int activations)
    {
        CheckRunner.Equal("file_manager", parameters.GetProperty("source").GetString());
        CheckRunner.True(parameters.GetProperty("now_unix_ms").GetInt64() > 0, "Activation needs a real timestamp.");
        Interlocked.Increment(ref activations);
        return Reply.Success(new { recorded = true });
    }

    private static string NewDirectory(string sandbox, string name)
    {
        var path = Path.Combine(sandbox, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Dictionary<string, object?>> CreateItemAsync(string directory, string name, uint id)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, "Listary disposable protocol fixture.");
        var file = new FileInfo(path);
        return new()
        {
            ["id"] = id, ["root_id"] = Guid.NewGuid(), ["path"] = path, ["parent_path"] = directory,
            ["name"] = name, ["extension"] = Path.GetExtension(name).TrimStart('.'), ["kind"] = "file",
            ["size"] = file.Length, ["modified_unix_ms"] = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            ["available"] = true, ["priority"] = "normal", ["stable_file_id"] = ReadNativeIdentity(path), ["preview_hint"] = null
        };
    }

    private static string? ReadNativeIdentity(string path)
    {
        // The provider fixture must publish the same real OS identity as the
        // Rust indexer. Reflection keeps the production method non-public and
        // avoids creating a second independently maintained native OS adapter.
        var method = typeof(ListaryClient).GetMethod("ReadStableFileIdentity",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Listary's native file identity reader is missing.");
        return (string?)method.Invoke(null, [path]);
    }

    private static object Page(JsonElement request, IReadOnlyList<Dictionary<string, object?>> all, ulong generation)
    {
        var parameters = request.GetProperty("params");
        var offset = parameters.GetProperty("offset").GetInt64();
        var limit = parameters.GetProperty("limit").GetInt32();
        var rows = all.Skip(checked((int)offset)).Take(limit).Select(item => new { item }).ToArray();
        return new
        {
            results = rows, total_matches = all.Count, offset, limit, has_more = offset + rows.Length < all.Count,
            catalog_generation = generation, considered_candidates = all.Count, elapsed_micros = 1
        };
    }

    private static async Task ExpectCodeAsync(string code, Func<Task> operation)
    {
        try { await operation(); }
        catch (ListaryException error)
        {
            CheckRunner.Equal(code, error.Code);
            return;
        }
        throw new InvalidOperationException($"Expected Listary error {code}.");
    }

    private sealed record Reply(object? Data, string? Code = null, string? Message = null,
        string? Raw = null, int FragmentSize = 0)
    {
        public static Reply Success(object? data) => new(data);
        public static Reply Failure(string code, string message) => new(null, code, message);
        public static Reply RawFrame(string text) => new(null, Raw: text);
    }

    private sealed class FixtureAgent : IAsyncDisposable
    {
        private readonly string _state;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly ConcurrentBag<Task> _connections = new();
        private readonly ConcurrentQueue<Exception> _faults = new();
        private readonly Task _accepting;
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public ConcurrentQueue<JsonElement> Requests { get; } = new();
        public Func<JsonElement, CancellationToken, Task<Reply>> Handler { get; set; }

        public FixtureAgent(string state, Func<JsonElement, CancellationToken, Task<Reply>> handler)
        {
            _state = state;
            Handler = handler;
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public async Task PublishAsync()
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = 1, protocol_version = 1, process_id = Environment.ProcessId,
                address = _listener.LocalEndpoint.ToString(), token = Token
            });
            var temporary = Path.Combine(_state, $"descriptor-{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, Path.Combine(_state, "integration-endpoint-v1.json"), overwrite: true);
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
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
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_stopping.Token);
                    if (line is null) return;
                    using var document = JsonDocument.Parse(line);
                    var request = document.RootElement.Clone();
                    Requests.Enqueue(request);
                    var reply = request.GetProperty("token").GetString() != Token
                        ? Reply.Failure("authentication_failed", "Invalid fixture token.")
                        : await Handler(request, _stopping.Token);
                    var encoded = reply.Raw ?? JsonSerializer.Serialize(reply.Code is null
                        ? new { id = request.GetProperty("id").GetString(), ok = true, data = reply.Data } as object
                        : new { id = request.GetProperty("id").GetString(), ok = false, error = new { code = reply.Code, message = reply.Message } }) + "\n";
                    var bytes = Encoding.UTF8.GetBytes(encoded);
                    if (reply.FragmentSize > 0)
                    {
                        for (var offset = 0; offset < bytes.Length; offset += reply.FragmentSize)
                            await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(reply.FragmentSize, bytes.Length - offset)), _stopping.Token);
                    }
                    else await stream.WriteAsync(bytes, _stopping.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
                catch (Exception error) { _faults.Enqueue(error); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            await _accepting.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(3));
            _stopping.Dispose();
            if (_faults.TryDequeue(out var fault)) throw new InvalidOperationException("Loopback fixture failed.", fault);
        }
    }
}
