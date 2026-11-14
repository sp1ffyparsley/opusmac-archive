using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using OpusMac.Core;
using PathShape = Avalonia.Controls.Shapes.Path;

namespace OpusMac.App;

internal static class FileTypeSummary
{
    private static readonly Dictionary<string, string> Groups = new (string Group, string Extensions)[]
    {
        ("Archives", "000 001 7z arj bz2 bzip2 cab cb7 cbr cbz esd gz gzip jar lha lzh lzma lzma86 r00 r01 rar swm tar taz tbz tbz2 tgz tlz tlzma tpz txz wim xz z zip"),
        ("Documents", "doc dot docx dotx mdb xls xlsb xlsx xlt xltx ppt pps pot pptx ppsx potx"),
        ("Images", "jpg jpe jpeg jfif gif png iff ilbm lbm bmp info tif tiff psd psp wmf emf clip webp heic heif hif avif dds 3fr arw bay cr2 cr3 crw dc2 dcr dcs dng erf fff iiq kdc mdc mef mos mrw nef nrw orf pef ptx raf raw rw2 rwz sr2 srf srw x3f"),
        ("Movies", "asf asx avi wm wmv mp2v mpg mpeg m1v mpe mpv2 m2ts vob mov mp4 3gp flv mkv webm m2v m4v"),
        ("Music", "mp3 mp2 mp2v mp1 m4a m4b mka wav wma aac flac m4p mid ogg spx"),
        ("Programs", "exe dll cpl ocx")
    }.SelectMany(entry => entry.Extensions.Split(' ').Select(extension => (extension, entry.Group)))
        .GroupBy(item => item.extension).ToDictionary(group => group.Key, group => group.First().Group);

    private static readonly IBrush[] Colors =
        new[] { "#3188CE", "#DA7445", "#529C57", "#A568C6", "#BA9432", "#278F9B", "#A64964", "#777D8A" }.Select(Brush.Parse).ToArray();

    internal sealed record Bucket(string Label, long Value);

    internal static Bucket[] Summarize(IEnumerable<FileEntry> entries, bool bySize = false, bool byGroup = false) =>
        entries.Where(entry => !entry.IsDirectory).GroupBy(entry =>
        {
            var extension = entry.Extension.TrimStart('.').ToLowerInvariant();
            if (byGroup) return Groups.GetValueOrDefault(extension, "Other");
            return extension switch { "" => "(no extension)", "jpeg" or "jpe" => ".jpg", _ => "." + extension };
        }).Select(group => new Bucket(group.Key, bySize ? group.Sum(entry => entry.Length) : group.LongCount()))
        .OrderByDescending(bucket => bucket.Value).ThenBy(bucket => bucket.Label, StringComparer.Ordinal).ToArray();

    internal static string Compact(IReadOnlyList<Bucket> buckets) =>
        string.Join(", ", buckets.Take(3).Select(bucket => $"{bucket.Value:N0} {StatusLabel(bucket.Label)}")
            .Concat(buckets.Count > 3 ? [$"{buckets.Count - 3:N0} more"] : []));

    internal static Control StatusBadges(IReadOnlyList<Bucket> buckets, int maximumTypes)
    {
        var panel = new StackPanel { Name = "FileTypeSummaryBadges", Orientation = Orientation.Horizontal, Spacing = 3 };
        var displayed = Math.Min(buckets.Count, maximumTypes);
        for (var index = 0; index < displayed; index++)
        {
            var bucket = buckets[index];
            panel.Children.Add(Ui.Text(bucket.Value.ToString("N0")));
            var frame = new Border
            {
                Classes = { "inactive" }, CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1), Padding = new Thickness(3, 0),
                Child = Ui.Text(StatusLabel(bucket.Label))
            };
            panel.Children.Add(frame);
            if (index < buckets.Count - 1) panel.Children.Add(Ui.Text(","));
        }
        if (buckets.Count > displayed) panel.Children.Add(Ui.Text($"{buckets.Count - displayed:N0} more"));
        return panel;
    }

    private static string StatusLabel(string extension) => extension.TrimStart('.');

    internal static Control Chart(IReadOnlyList<Bucket> buckets, bool bySize, bool compact)
    {
        if (buckets.Count == 0) return Ui.Text("No files");
        var diameter = compact ? 100d : 196d;
        var center = diameter / 2;
        var radius = center - 2;
        var canvas = new Canvas { Name = "FileTypeSummaryPie", Width = diameter, Height = diameter };
        var firstLegend = new StackPanel { Spacing = 2 };
        var secondLegend = new StackPanel { Spacing = 2 };
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { firstLegend, secondLegend } };
        var legendRows = compact ? 8 : 9;
        var limit = compact ? legendRows : 2 * legendRows;
        var segments = buckets.Take(limit).ToArray();
        if (buckets.Count > limit)
            segments[^1] = new Bucket($"{buckets.Count - limit + 1} more", buckets.Skip(limit - 1).Sum(bucket => bucket.Value));
        var total = segments.Sum(bucket => (double)bucket.Value);
        var angle = -Math.PI / 2;
        for (var index = 0; index < segments.Length; index++)
        {
            var bucket = segments[index];
            var brush = Colors[index % Colors.Length];
            var text = new TextBlock
            {
                Text = $"{bucket.Label}   {(bySize ? FileEntry.FormatSize(bucket.Value) : bucket.Value.ToString("N0"))}",
                VerticalAlignment = VerticalAlignment.Center
            };
            var mark = new Border { Width = 10, Height = 10, Background = brush, VerticalAlignment = VerticalAlignment.Center };
            (index < legendRows ? firstLegend : secondLegend).Children.Add(Ui.Row(mark, text));
            if (bucket.Value == 0 || total == 0) continue;
            if (bucket.Value == total)
            {
                canvas.Children.Add(new Ellipse { Width = diameter - 4, Height = diameter - 4, Fill = brush, Margin = new Thickness(2) });
                continue;
            }
            var sweep = 2 * Math.PI * bucket.Value / total;
            var next = angle + sweep;
            var startX = center + radius * Math.Cos(angle);
            var startY = center + radius * Math.Sin(angle);
            var endX = center + radius * Math.Cos(next);
            var endY = center + radius * Math.Sin(next);
            var data = FormattableString.Invariant($"M {center},{center} L {startX},{startY} A {radius},{radius} 0 {(sweep > Math.PI ? 1 : 0)} 1 {endX},{endY} Z");
            canvas.Children.Add(new PathShape { Data = Geometry.Parse(data), Fill = brush });
            angle = next;
        }
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = compact ? 8 : 18, Children = { canvas, legend } };
    }
}
