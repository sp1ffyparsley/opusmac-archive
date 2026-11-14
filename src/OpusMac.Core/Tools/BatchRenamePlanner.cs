using System.Globalization;
using System.Text.RegularExpressions;

namespace OpusMac.Core.Tools;

/// <summary>Produces a read-only rename preview; FileOperationService owns every mutation.</summary>
public sealed class BatchRenamePlanner
{
    public RenamePlan CreatePlan(IReadOnlyList<string> sources, RenameOptions options)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (sources.Count == 0) errors.Add("Select at least one item to rename.");
        if (sources.Count > 100000) return new RenamePlan([], ["A rename preview is limited to 100,000 items."]);
        if (options.NumberDigits is < 0 or > 20) errors.Add("Number digits must be between 0 and 20.");
        if (!Enum.IsDefined(options.Case)) errors.Add("The requested letter case is unsupported.");
        Regex? expression = null;
        if (options.UseRegex && !string.IsNullOrEmpty(options.Find))
        {
            try { expression = new Regex(options.Find, RegexOptions.CultureInvariant | (options.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(250)); }
            catch (ArgumentException error) { errors.Add("Invalid regular expression: " + error.Message); }
        }
        if (errors.Count > 0) return new RenamePlan([], errors);
        var items = new List<PendingRename>();
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var original = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            var item = new PendingRename(source, original);
            items.Add(item);
            try
            {
                item.Source = ToolPath.Full(source);
                var parent = Path.GetDirectoryName(item.Source) ?? throw new IOException("A filesystem root cannot be renamed.");
                ToolPath.RequireNoLinks(parent);
                var metadata = ToolFileSnapshot.Read(item.Source);
                item.OriginalName = Path.GetFileName(item.Source);
                var extension = !options.IncludeExtension && !metadata.IsDirectory ? Path.GetExtension(item.OriginalName) : string.Empty;
                var name = extension.Length == 0 ? item.OriginalName : item.OriginalName[..^extension.Length];
                if (expression is not null) name = expression.Replace(name, options.Replace ?? string.Empty);
                else if (!string.IsNullOrEmpty(options.Find)) name = name.Replace(options.Find, options.Replace ?? string.Empty, options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                name = options.Case switch
                {
                    RenameCase.Lower => name.ToLowerInvariant(),
                    RenameCase.Upper => name.ToUpperInvariant(),
                    RenameCase.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()),
                    _ => name
                };
                name = (options.Prefix ?? string.Empty) + name + (options.Suffix ?? string.Empty);
                if (options.NumberStart is { } start)
                {
                    var number = checked((long)start + (long)index * options.NumberStep);
                    name += number.ToString("D" + options.NumberDigits, CultureInfo.InvariantCulture);
                }
                item.NewName = name + extension;
                item.Destination = Path.Combine(parent, item.NewName);
                item.Changed = !string.Equals(item.OriginalName, item.NewName, StringComparison.Ordinal);
                if (ToolPath.NameError(item.NewName) is { } nameError) item.Errors.Add(nameError);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or RegexMatchTimeoutException or OverflowException)
            {
                item.Errors.Add(error.Message);
            }
        }
        foreach (var group in items.GroupBy(item => ToolPath.Key(item.Source), ToolPath.PortableComparer).Where(group => group.Count() > 1))
            foreach (var item in group) item.Errors.Add("This source appears more than once in the rename preview.");
        foreach (var group in items.Where(item => item.Destination.Length > 0).GroupBy(item => ToolPath.Key(item.Destination), ToolPath.PortableComparer).Where(group => group.Count() > 1))
            foreach (var item in group) item.Errors.Add("Several selected items would have the same destination name.");

        var selected = new HashSet<string>(items.Select(item => ToolPath.Key(item.Source)), ToolPath.PortableComparer);
        foreach (var item in items)
        {
            var parent = Path.GetDirectoryName(item.Source);
            while (parent is not null)
            {
                if (selected.Contains(ToolPath.Key(parent))) { item.Errors.Add("Rename a selected directory and its descendants in separate operations."); break; }
                parent = Path.GetDirectoryName(parent);
            }
        }
        var vacating = new HashSet<string>(items.Where(item => item.Changed && item.Errors.Count == 0).Select(item => ToolPath.Key(item.Source)), ToolPath.PortableComparer);
        var existingByParent = new Dictionary<string, Dictionary<string, List<string>>>(ToolPath.PathComparer);
        foreach (var item in items.Where(item => item.Changed && item.Errors.Count == 0))
        {
            try
            {
                var parent = Path.GetDirectoryName(item.Source)!;
                if (!existingByParent.TryGetValue(parent, out var existing))
                {
                    existing = new Dictionary<string, List<string>>(ToolPath.PortableComparer);
                    foreach (var path in Directory.EnumerateFileSystemEntries(parent))
                    {
                        var key = ToolPath.Key(Path.GetFileName(path));
                        if (!existing.TryGetValue(key, out var names)) existing[key] = names = [];
                        names.Add(path);
                    }
                    existingByParent.Add(parent, existing);
                }
                if (existing.TryGetValue(ToolPath.Key(item.NewName), out var collisions) && collisions.Any(path => !vacating.Contains(ToolPath.Key(path))))
                    item.Errors.Add("A file or directory already owns the destination name and is not being renamed away.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                item.Errors.Add("Cannot verify the destination: " + error.Message);
            }
        }
        return new RenamePlan(items.Select(item => new RenamePlanItem(item.Source, item.OriginalName, item.NewName, item.Destination, item.Changed, item.Errors.ToArray())).ToArray(), errors);
    }

    private sealed class PendingRename(string source, string originalName)
    {
        internal string Source { get; set; } = source;
        internal string OriginalName { get; set; } = originalName;
        internal string NewName { get; set; } = originalName;
        internal string Destination { get; set; } = source;
        internal bool Changed { get; set; }
        internal List<string> Errors { get; } = [];
    }
}
