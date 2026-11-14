using System.Reflection;
using System.Text.Json;
using OpusMac.Checks;
using OpusMac.Core.Integration;

if (args.Length == 3 && args[0] == "--instance-forward")
{
    await using var client = new AppInstanceBroker(args[1]);
    if (client.TryBecomePrimary()) throw new InvalidOperationException("The second process unexpectedly acquired the primary instance lease.");
    await client.ForwardAsync(new OpenRequest([args[2]]));
    return 0;
}

string? chosenOutput = null;
string? suiteFilter = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--suite" && i + 1 < args.Length) suiteFilter = args[++i];
    else if (!args[i].StartsWith("--", StringComparison.Ordinal) && chosenOutput is null) chosenOutput = args[i];
    else throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
}
var output = chosenOutput is not null ? Path.GetFullPath(chosenOutput) : Path.Combine(Path.GetTempPath(), "OpusMacChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var runner = new CheckRunner();
foreach (var type in Assembly.GetExecutingAssembly().GetTypes().Where(t => typeof(ICheckSuite).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false } && (suiteFilter is null || t.Name.Contains(suiteFilter, StringComparison.OrdinalIgnoreCase))).OrderBy(t => t.Name))
{
    var root = Path.Combine(output, type.Name);
    Directory.CreateDirectory(root);
    await ((ICheckSuite)Activator.CreateInstance(type)!).RunAsync(runner, root);
}
var receipt = new { platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, passed = runner.Passed, failed = runner.Failed, unverified = runner.Unverified, all_requested_checks_verified = runner.Failed == 0 && runner.Unverified == 0, checks = runner.Results, macos_runtime_executed = OperatingSystem.IsMacOS(), utc = DateTimeOffset.UtcNow };
await File.WriteAllTextAsync(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Passed: {runner.Passed}; failed: {runner.Failed}; unverified: {runner.Unverified}; receipt: {Path.Combine(output, "checks.json")}");
return runner.Failed == 0 && runner.Passed > 0 ? 0 : 1;

namespace OpusMac.Checks
{
    public interface ICheckSuite { Task RunAsync(CheckRunner checks, string sandbox); }
    public sealed class CheckRunner
    {
        public int Passed { get; private set; }
        public int Failed { get; private set; }
        public int Unverified { get; private set; }
        public List<object> Results { get; } = [];
        public async Task CheckAsync(string name, Func<Task> check)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { await check(); Passed++; Results.Add(new { name, passed = true, elapsed_ms = watch.ElapsedMilliseconds }); Console.WriteLine($"PASS {name}"); }
            catch (CheckNotVerifiedException e) { Unverified++; Results.Add(new { name, passed = false, status = "unverified", elapsed_ms = watch.ElapsedMilliseconds, reason = e.Message }); Console.WriteLine($"UNVERIFIED {name}: {e.Message}"); }
            catch (Exception e) { Failed++; Results.Add(new { name, passed = false, status = "failed", elapsed_ms = watch.ElapsedMilliseconds, error = e.ToString() }); Console.WriteLine($"FAIL {name}: {e.Message}"); }
        }
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        public static void NotVerified(string reason) => throw new CheckNotVerifiedException(reason);
        public static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        public static void Equal<T>(T expected, T actual, string message = "Values differ") { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}; expected {expected}, got {actual}"); }
        public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action(); } catch (T) { return; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}");
        }
    }
    public sealed class CheckNotVerifiedException(string reason) : Exception(reason);
}
