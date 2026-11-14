using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace OpusMac.Core.Persistence;

public sealed record ImportedFolderColumns(string Label, IReadOnlyList<FileColumnSettings> Columns, string Summary,
    string? PrimarySortColumn, string? ViewMode, bool KeepCurrentColumns, bool? IncludeSubfolders,
    bool InheritSort, bool InheritView, bool Disabled, bool IsUserDefaultEntry)
{
    public override string ToString() => Label;

    public static async Task<IReadOnlyList<ImportedFolderColumns>> ReadAsync(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024
        });
        var root = (await XDocument.LoadAsync(reader, LoadOptions.None, CancellationToken.None)).Root;
        if (root?.Name != "folder_formats")
            throw new InvalidDataException("Select a Directory Opus folder_formats XML file (.off).");

        var formats = new List<ImportedFolderColumns>();
        var defaults = FileColumnSettings.CreateDefaultLayout();
        foreach (var format in root.Descendants("format"))
        {
            if (formats.Count == 500) throw new InvalidDataException("The file contains more than 500 column formats.");
            var columns = new List<FileColumnSettings>();
            var unsupported = 0;
            string? primarySort = null;
            foreach (var field in format.Elements("field"))
            {
                var id = (string?)field.Attribute("id") switch
                {
                    "name" => "Name", "sizeauto" => "Size", "type" => "Type",
                    "modified" => "Modified", "created" => "Created", "attr" => "Attr", "path" => "Path",
                    "thumbnail" => "Thumbnail",
                    _ => null
                };
                if (id is null) { unsupported++; continue; }
                if (columns.Any(column => column.Id == id)) continue;
                var column = defaults.Single(column => column.Id == id).Copy();
                column.Visible = true;
                if ((string?)field.Attribute("fill_max") == "yes")
                {
                    column.WidthMode = "Star";
                    column.Width = 1;
                }
                else if (double.TryParse((string?)field.Attribute("size"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var width) && width is > 0 and <= 10000)
                {
                    var dpi = (string?)field.Attribute("size_dpi") ?? "100";
                    if (int.TryParse(dpi, NumberStyles.None, CultureInfo.InvariantCulture, out var scale) &&
                        scale is >= 50 and <= 800)
                    {
                        column.WidthMode = "Pixel";
                        column.Width = Math.Clamp(width * 100 / scale, SettingsStore.MinimumColumnWidth(id), 1200);
                    }
                }
                if (id != "Thumbnail" && (string?)field.Attribute("sort") == "1" && (string?)format.Attribute("inherit_sorting") != "yes")
                    primarySort ??= id;
                columns.Add(column);
            }

            if (columns.Count == 0) continue;
            foreach (var fallback in defaults.Where(item => columns.All(column => column.Id != item.Id)))
            {
                fallback.Visible = fallback.Id == "Name";
                columns.Add(fallback.Copy());
            }
            var name = (string?)format.Ancestors("path").FirstOrDefault()?.Attribute("name");
            var label = $"Format {formats.Count + 1}";
            if (!string.IsNullOrWhiteSpace(name)) label += $" · {name[..Math.Min(160, name.Length)]}";
            var viewMode = (string?)format.Attribute("inherit_view") == "yes" ? null
                : (string?)format.Attribute("view_mode") switch
                {
                    "details" => "Details",
                    "thumbnails" => "Tiles",
                    _ => null
                };
            var keepColumns = (string?)format.Attribute("inherit_columns") == "yes" ||
                (string?)format.Attribute("inheritcolumns") == "yes";
            bool? subfolders = (string?)format.Attribute("useforsubfolders") switch
            {
                "yes" => true,
                "no" => false,
                _ => null
            };
            var columnSummary = keepColumns
                ? "This format inherits columns. Apply follows the nearest applicable parent column rule or the tab default."
                : $"{unsupported} unsupported column(s).";
            var disabled = (string?)format.Attribute("disable") == "yes";
            if (disabled) label += " (disabled)";
            formats.Add(new(label, columns,
                $"{columnSummary} Review scope and sort direction before applying. Secondary sorting, paths, and filters are outside this import.",
                primarySort, viewMode, keepColumns, subfolders,
                (string?)format.Attribute("inherit_sorting") == "yes", (string?)format.Attribute("inherit_view") == "yes",
                disabled, string.Equals(name, "default", StringComparison.Ordinal)));
        }
        if (formats.Count == 0) throw new InvalidDataException("No supported columns were found in this folder format file.");
        return formats;
    }
}
