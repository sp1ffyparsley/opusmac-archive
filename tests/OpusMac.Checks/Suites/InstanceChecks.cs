using System.Diagnostics;
using System.IO.Pipes;
using OpusMac.Core.Integration;

namespace OpusMac.Checks.Suites;

public sealed class InstanceChecks : ICheckSuite
{
    public async Task RunAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Instance: a second process connection forwards exact Unicode paths to one owner", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First instance did not acquire its state.");
            var requests = new List<OpenRequest>();
            primary.Start(request => { lock (requests) requests.Add(request); return Task.CompletedTask; });
            var path = Path.Combine(sandbox, "한글 folder", "café");
            var hostPath = Environment.ProcessPath ?? throw new InvalidOperationException("The test host executable path is unavailable.");
            var start = new ProcessStartInfo(hostPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            if (Path.GetFileNameWithoutExtension(hostPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(InstanceChecks).Assembly.Location);
            start.ArgumentList.Add("--instance-forward");
            start.ArgumentList.Add(state);
            start.ArgumentList.Add(path);
            using var child = Process.Start(start) ?? throw new IOException("Could not start the second instance process.");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await child.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                throw new TimeoutException("The second instance process did not complete its forwarding request.");
            }
            CheckRunner.Equal(0, child.ExitCode, $"The second instance process failed. stdout={await stdout}; stderr={await stderr}");
            lock (requests)
            {
                CheckRunner.Equal(1, requests.Count);
                CheckRunner.Equal(path, requests[0].Paths.Single());
                CheckRunner.True(requests[0].Activate, "Activation intent was lost.");
            }
        });

        await checks.CheckAsync("Instance: profile isolation and shutdown lock release", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using (var first = new AppInstanceBroker(state))
            {
                CheckRunner.True(first.TryBecomePrimary(), "First lock failed.");
                await using var isolated = new AppInstanceBroker(state + "-other");
                CheckRunner.True(isolated.TryBecomePrimary(), "An isolated profile was blocked by another profile.");
                CheckRunner.True(first.PipeName != isolated.PipeName, "Isolated profiles share a pipe.");
            }
            await using var reopened = new AppInstanceBroker(state);
            CheckRunner.True(reopened.TryBecomePrimary(), "The lock remained held after shutdown.");
        });

        await checks.CheckAsync("Instance: refused navigation remains a visible failure and receiver survives", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First lock failed.");
            var fail = true;
            primary.Start(_ => { if (fail) throw new InvalidOperationException("Folder refused by fixture."); return Task.CompletedTask; });
            await using var secondary = new AppInstanceBroker(state);
            IOException? refusal = null;
            try { await secondary.ForwardAsync(new OpenRequest([sandbox])); }
            catch (IOException exception) { refusal = exception; }
            CheckRunner.True(refusal is not null, "The explicit server refusal must remain visible to the client.");
            CheckRunner.True(refusal!.Message.Contains("Folder refused by fixture.", StringComparison.Ordinal),
                "The explicit refusal reason was not preserved.");
            CheckRunner.True(!refusal.Message.Contains("outcome may be unknown", StringComparison.OrdinalIgnoreCase),
                "A complete explicit refusal must not be mislabeled as an unknown outcome.");
            fail = false;
            await secondary.ForwardAsync(new OpenRequest([]));
            await CheckRunner.ThrowsAsync<ArgumentException>(() => secondary.ForwardAsync(new OpenRequest([new string('x', 4097)])));
        });

        await checks.CheckAsync("Instance: unexpected application exception is refused and receiver remains available", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First lock failed.");
            var nextHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.Start(request =>
            {
                var name = Path.GetFileName(request.Paths.SingleOrDefault() ?? "");
                if (name == "unexpected-handler")
                    throw new FormatException("The fixture handler encountered an unexpected application error.");
                if (name == "after-unexpected-handler")
                    nextHandled.TrySetResult(true);
                return Task.CompletedTask;
            });
            await using var secondary = new AppInstanceBroker(state);
            var failedPath = Path.Combine(sandbox, "unexpected-handler");
            await CheckRunner.ThrowsAsync<IOException>(() => secondary.ForwardAsync(new OpenRequest([failedPath])));
            var nextPath = Path.Combine(sandbox, "after-unexpected-handler");
            await secondary.ForwardAsync(new OpenRequest([nextPath]));
            await nextHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await checks.CheckAsync("Instance: filesystem and cancellation failures refuse one request without terminating the receiver", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First lock failed.");
            var accepted = new List<string>();
            primary.Start(request =>
            {
                var name = Path.GetFileName(request.Paths.SingleOrDefault() ?? "");
                switch (name)
                {
                    case "denied": throw new UnauthorizedAccessException("The fixture folder is inaccessible.");
                    case "security": throw new System.Security.SecurityException("The fixture folder is denied by policy.");
                    case "unsupported": throw new NotSupportedException("The fixture path is unsupported.");
                    case "cancelled": throw new OperationCanceledException("The fixture navigation was cancelled.");
                    case "timed-out": throw new TimeoutException("The fixture navigation timed out.");
                    case "win32": throw new System.ComponentModel.Win32Exception(5, "The fixture shell operation was denied.");
                }
                lock (accepted) accepted.AddRange(request.Paths);
                return Task.CompletedTask;
            });

            await using var secondary = new AppInstanceBroker(state);
            foreach (var failure in new[] { "denied", "security", "unsupported", "cancelled", "timed-out", "win32" })
            {
                var path = Path.Combine(sandbox, failure);
                await CheckRunner.ThrowsAsync<IOException>(() => secondary.ForwardAsync(new OpenRequest([path])));
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var acceptedPath = Path.Combine(sandbox, "accepted-after-refusals");
            await secondary.ForwardAsync(new OpenRequest([acceptedPath]), deadline.Token);
            lock (accepted)
            {
                CheckRunner.Equal(1, accepted.Count, "Only the final valid request should reach the handler.");
                CheckRunner.Equal(acceptedPath, accepted[0], "The receiver did not preserve the later valid path.");
            }
        });

        await checks.CheckAsync("Instance: caller cancellation after dispatch disconnects one client and receiver accepts the next request", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First lock failed.");
            var handlerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nextHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new List<string>();
            primary.Start(async request =>
            {
                var path = request.Paths.SingleOrDefault() ?? "";
                if (Path.GetFileName(path) == "client-cancelled")
                {
                    handlerEntered.TrySetResult(true);
                    await releaseHandler.Task.ConfigureAwait(false);
                    firstHandled.TrySetResult(true);
                }
                else if (Path.GetFileName(path) == "after-client-cancel")
                {
                    nextHandled.TrySetResult(true);
                }
                lock (accepted) accepted.Add(path);
            });

            await using var secondary = new AppInstanceBroker(state);
            using var callerCancellation = new CancellationTokenSource();
            var cancelledPath = Path.Combine(sandbox, "client-cancelled");
            var inFlight = secondary.ForwardAsync(new OpenRequest([cancelledPath]), callerCancellation.Token);
            try
            {
                await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                callerCancellation.Cancel();
                OperationCanceledException? cancellation = null;
                try { await inFlight; }
                catch (OperationCanceledException exception) { cancellation = exception; }
                CheckRunner.True(cancellation is not null, "The cancelled forwarding request must preserve cancellation.");
                CheckRunner.True(cancellation!.Message.Contains("outcome may be unknown", StringComparison.OrdinalIgnoreCase),
                    "Post-send cancellation must disclose that the request may already have been dispatched.");
                CheckRunner.True(cancellation.Message.Contains("reconcile", StringComparison.OrdinalIgnoreCase),
                    "Post-send cancellation must require reconciliation before retrying.");
                releaseHandler.TrySetResult(true);
                await firstHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));

                var nextPath = Path.Combine(sandbox, "after-client-cancel");
                await secondary.ForwardAsync(new OpenRequest([nextPath]));
                await nextHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                lock (accepted)
                {
                    CheckRunner.Equal(2, accepted.Count, "The in-flight handler and the subsequent request should each run once.");
                    CheckRunner.Equal(cancelledPath, accepted[0], "The dispatched request path changed after client cancellation.");
                    CheckRunner.Equal(nextPath, accepted[1], "The receiver did not accept the request after the disconnected client.");
                }
            }
            finally
            {
                releaseHandler.TrySetResult(true);
            }
        });

        await checks.CheckAsync("Instance: disconnect after handler dispatch reports an unknown outcome and releases the lease", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First lock failed.");
            var handlerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.Start(_ =>
            {
                handlerEntered.TrySetResult(true);
                primary.Dispose();
                return Task.CompletedTask;
            });

            await using var secondary = new AppInstanceBroker(state);
            IOException? transportFailure = null;
            try { await secondary.ForwardAsync(new OpenRequest([Path.Combine(sandbox, "dispatched-before-disconnect")])); }
            catch (IOException exception) { transportFailure = exception; }
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            CheckRunner.True(transportFailure is not null, "The client should observe the primary disconnect.");
            CheckRunner.True(transportFailure!.Message.Contains("outcome may be unknown", StringComparison.OrdinalIgnoreCase),
                "A post-dispatch disconnect must disclose that the request may already have been accepted.");
            CheckRunner.True(transportFailure.Message.Contains("reconcile", StringComparison.OrdinalIgnoreCase),
                "A post-dispatch disconnect must require reconciliation before retrying.");

            await primary.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await using var replacement = new AppInstanceBroker(state);
            CheckRunner.True(replacement.TryBecomePrimary(), "The primary lease was not released after the disconnected receiver exited.");
        });

        await checks.CheckAsync("Instance: real handler deadline reports an unknown outcome and receiver accepts the next request", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First lock failed.");
            var handlerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timedOutHandlerFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nextHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.Start(async request =>
            {
                var name = Path.GetFileName(request.Paths.SingleOrDefault() ?? "");
                if (name == "slow-handler")
                {
                    handlerEntered.TrySetResult(true);
                    await releaseHandler.Task.ConfigureAwait(false);
                    timedOutHandlerFinished.TrySetResult(true);
                }
                else if (name == "after-handler-timeout")
                {
                    nextHandled.TrySetResult(true);
                }
            });

            await using var secondary = new AppInstanceBroker(state);
            var slowPath = Path.Combine(sandbox, "slow-handler");
            var inFlight = secondary.ForwardAsync(new OpenRequest([slowPath]));
            try
            {
                await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                TimeoutException? timeout = null;
                try { await inFlight; }
                catch (TimeoutException exception) { timeout = exception; }
                CheckRunner.True(timeout is not null, "The server deadline must be reported as a timeout.");
                CheckRunner.True(timeout!.Message.Contains("outcome", StringComparison.OrdinalIgnoreCase),
                    "A timed-out handler must be reported as outcome-unknown.");
                CheckRunner.True(timeout.Message.Contains("reconcile", StringComparison.OrdinalIgnoreCase),
                    "The timeout must instruct the caller to reconcile before retrying.");

                releaseHandler.TrySetResult(true);
                await timedOutHandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var nextPath = Path.Combine(sandbox, "after-handler-timeout");
                await secondary.ForwardAsync(new OpenRequest([nextPath]));
                await nextHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                releaseHandler.TrySetResult(true);
            }
        });

        await checks.CheckAsync("Instance: incomplete client frame expires and receiver accepts the next request", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First lock failed.");
            var nextHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.Start(request =>
            {
                if (Path.GetFileName(request.Paths.SingleOrDefault() ?? "") == "after-incomplete-frame")
                    nextHandled.TrySetResult(true);
                return Task.CompletedTask;
            });

            await using (var stalled = new NamedPipeClientStream(
                ".", primary.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await stalled.ConnectAsync(2000);
                await stalled.WriteAsync(new byte[] { (byte)'{' });
                await stalled.FlushAsync();
                await Task.Delay(TimeSpan.FromSeconds(9));
            }

            await using var secondary = new AppInstanceBroker(state);
            var nextPath = Path.Combine(sandbox, "after-incomplete-frame");
            await secondary.ForwardAsync(new OpenRequest([nextPath]));
            await nextHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await checks.CheckAsync("Instance: malformed complete JSON frame is rejected and receiver accepts the next request", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First lock failed.");
            var nextHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.Start(request =>
            {
                if (Path.GetFileName(request.Paths.SingleOrDefault() ?? "") == "after-malformed-frame")
                    nextHandled.TrySetResult(true);
                return Task.CompletedTask;
            });

            await using (var malformed = new NamedPipeClientStream(
                ".", primary.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await malformed.ConnectAsync(2000);
                var bytes = System.Text.Encoding.UTF8.GetBytes("{not-json}\n");
                await malformed.WriteAsync(bytes);
                await malformed.FlushAsync();
                await Task.Delay(100);
            }

            await using var secondary = new AppInstanceBroker(state);
            await secondary.ForwardAsync(new OpenRequest([Path.Combine(sandbox, "after-malformed-frame")]));
            await nextHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });

        await checks.CheckAsync("Instance: oversized client frame is bounded and receiver accepts the next request", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First lock failed.");
            var received = new List<string>();
            var nextHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.Start(request =>
            {
                var path = request.Paths.SingleOrDefault() ?? "";
                lock (received) received.Add(path);
                if (Path.GetFileName(path) == "after-oversized-frame") nextHandled.TrySetResult(true);
                return Task.CompletedTask;
            });

            await using (var oversized = new NamedPipeClientStream(
                ".", primary.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await oversized.ConnectAsync(2000);
                var payload = new byte[64 * 1024 + 1];
                Array.Fill(payload, (byte)'x');
                try
                {
                    await oversized.WriteAsync(payload);
                    await oversized.WriteAsync(new byte[] { (byte)'\n' });
                    await oversized.FlushAsync();
                }
                catch (IOException)
                {
                    // The receiver may close the oversized connection while the client is writing.
                }
            }

            await using var secondary = new AppInstanceBroker(state);
            var acceptedPath = Path.Combine(sandbox, "after-oversized-frame");
            await secondary.ForwardAsync(new OpenRequest([acceptedPath]));
            await nextHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            lock (received)
            {
                CheckRunner.Equal(1, received.Count, "The oversized request must not reach the application handler.");
                CheckRunner.Equal(acceptedPath, received.Single(), "The receiver did not accept the valid request after the oversized frame.");
            }
        });

        await checks.CheckAsync("Instance: shutdown interrupts an incomplete client frame and releases the primary lease", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state);
            CheckRunner.True(primary.TryBecomePrimary(), "First lock failed.");
            primary.Start(_ => Task.CompletedTask);

            await using (var stalled = new NamedPipeClientStream(
                ".", primary.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await stalled.ConnectAsync(2000);
                await stalled.WriteAsync(new byte[] { (byte)'{' });
                await stalled.FlushAsync();
                await Task.Delay(50);
                await primary.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }

            await using var replacement = new AppInstanceBroker(state);
            CheckRunner.True(replacement.TryBecomePrimary(), "Shutdown left the primary lease held after interrupting the incomplete client.");
        });

        await checks.CheckAsync("Instance: equivalent state paths with a trailing separator forward to the same primary", async () =>
        {
            var state = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
            await using var primary = new AppInstanceBroker(state + Path.DirectorySeparatorChar);
            CheckRunner.True(primary.TryBecomePrimary(), "Primary instance did not acquire its state.");
            var received = new List<OpenRequest>();
            primary.Start(request => { lock (received) received.Add(request); return Task.CompletedTask; });

            await using var secondary = new AppInstanceBroker(state);
            CheckRunner.Equal(primary.PipeName, secondary.PipeName,
                "Equivalent state paths must derive the same pipe identity.");
            CheckRunner.True(!secondary.TryBecomePrimary(), "Equivalent state paths must contend for one instance lease.");
            await secondary.ForwardAsync(new OpenRequest([Path.Combine(sandbox, "trailing separator fixture") ]));
            lock (received)
            {
                CheckRunner.Equal(1, received.Count, "The request was not forwarded to the existing primary.");
                CheckRunner.Equal(Path.Combine(sandbox, "trailing separator fixture"), received[0].Paths.Single());
            }
        });
    }
}
