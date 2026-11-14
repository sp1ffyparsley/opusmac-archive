using System.Text.Json;

namespace OpusMac.Core.Tools;

/// <summary>Stores complete duplicate scans separately from the bounded preferences document.</summary>
public sealed class DuplicateCollectionStore(string stateDirectory)
{
    private readonly string _directory = Path.Combine(stateDirectory, "duplicate-collections");
    private const long MaximumBytes = 128L * 1024 * 1024;

    private sealed record StoredGroup(string Hash, long Length, ToolFileSnapshot[] Files);
    private sealed record StoredCollection(int Version, DuplicateComparison Comparison, int Accuracy, bool IgnoreOffline,
        bool NumberGroups, string[] OutsideReferences, StoredGroup[] Groups);

    public string[] ListNames() => Directory.Exists(_directory)
        ? Directory.EnumerateFiles(_directory, "*.json").Select(path => Path.GetFileNameWithoutExtension(path)!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
        : [];

    private string CollectionPath(string name)
    {
        if (name.Length > 80 || ToolPath.NameError(name) is { } )
            throw new ArgumentException("Enter a portable collection name of up to 80 characters.", nameof(name));
        return Path.Combine(_directory, name + ".json");
    }

    public async Task SaveAsync(string name, DuplicateResult result, bool numberGroups, CancellationToken token)
    {
        var path = CollectionPath(name);
        if (result.Truncated || result.Errors.Count > 0)
            throw new IOException("Only a complete duplicate scan can replace a collection.");
        var stored = new StoredCollection(1, result.Comparison, result.Md5Accuracy, result.IgnoreOffline, numberGroups,
            result.OutsideReferences.ToArray(), result.Groups.Select(group =>
                new StoredGroup(group.Hash, group.Length, group.ReviewedFiles.ToArray())).ToArray());
        Directory.CreateDirectory(_directory);
        ToolPath.RequireNoLinks(_directory);
        var temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, stored, cancellationToken: token).ConfigureAwait(false);
                if (stream.Length > MaximumBytes) throw new IOException("Duplicate collection exceeds the storage limit.");
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<(DuplicateResult Result, bool NumberGroups)> LoadAsync(string name, CancellationToken token)
    {
        var path = CollectionPath(name);
        ToolPath.RequireNoLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length > MaximumBytes) throw new IOException("Duplicate collection exceeds the storage limit.");
        var stored = await JsonSerializer.DeserializeAsync<StoredCollection>(stream, cancellationToken: token).ConfigureAwait(false);
        if (stored is null || stored.Version != 1 || !Enum.IsDefined(stored.Comparison) || stored.Accuracy is < 1 or > 100 ||
            stored.Groups is null || stored.OutsideReferences is null ||
            stored.Groups.Any(group => group is null || group.Hash is null || group.Length < 0 || group.Files is not { Length: >= 2 } ||
                group.Files.Any(file => file is null || file.IsLink || file.IsDirectory || file.NativeIdentity is null ||
                    string.IsNullOrEmpty(file.Path) || !Path.IsPathFullyQualified(file.Path))))
            throw new IOException("Duplicate collection is invalid. Run a new scan.");
        var groups = stored.Groups.Select(group => new DuplicateGroup(group.Hash, group.Length,
            group.Files.Select(file => file.Path).ToArray()) { ReviewedFiles = group.Files }).ToArray();
        return (new DuplicateResult(groups, [], [], false, 0, 0, stored.Comparison)
        {
            OutsideReferences = stored.OutsideReferences,
            Md5Accuracy = stored.Accuracy,
            IgnoreOffline = stored.IgnoreOffline
        }, stored.NumberGroups);
    }
}
