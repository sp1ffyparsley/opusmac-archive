using System.Text;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpusMac.Core;
using SkiaSharp;

namespace OpusMac.App;

internal static class PreviewFiles
{
    private static readonly SemaphoreSlim ImageGate = new(2, 2);
    private static readonly SemaphoreSlim PdfGate = new(1, 1);
    public static bool IsImage(string name) => Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".ico" or ".heic" or ".heif" or ".hif";
    public static bool IsPdf(string name) => Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    // PDFtoImage is backed by native PDFium on these desktop platforms. CA1416 does
    // not infer the combined platform guard inside Task.Run, so scope it to this method.
#pragma warning disable CA1416
    public static async Task<(Bitmap Image, int PageCount)> ReadPdfPageAsync(string path, int page, CancellationToken cancellation)
    {
        await PdfGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() &&
                    !OperatingSystem.IsLinux() && !OperatingSystem.IsAndroidVersionAtLeast(31))
                    throw new PlatformNotSupportedException("PDF previews are not available on this platform.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 128L * 1024 * 1024)
                    throw new IOException("This PDF exceeds the 128 MB preview limit. Open it in its document application.");
                var pageCount = PDFtoImage.Conversion.GetPageCount(stream, leaveOpen: true);
                if (pageCount is < 1 or > 100000)
                    throw new IOException("This PDF has no usable pages or exceeds the viewer's page limit.");
                if (page < 0 || page >= pageCount)
                    throw new ArgumentOutOfRangeException(nameof(page));
                cancellation.ThrowIfCancellationRequested();
                var dimensions = PDFtoImage.Conversion.GetPageSize(stream, new Index(page), leaveOpen: true);
                var longest = Math.Max(dimensions.Width, dimensions.Height);
                if (longest <= 0 || !float.IsFinite(longest))
                    throw new IOException("The PDF page has invalid dimensions.");
                // Keep even unusually long pages within a bounded bitmap allocation.
                var dpi = Math.Clamp((int)Math.Floor(1200.0 * 72.0 / longest), 12, 144);
                var width = dimensions.Width * dpi / 72.0;
                var height = dimensions.Height * dpi / 72.0;
                if (width <= 0 || height <= 0 || width * height > 5_000_000 ||
                    width > 4096 || height > 4096)
                    throw new IOException("This PDF page exceeds the preview pixel limit.");
                cancellation.ThrowIfCancellationRequested();
                using var rendered = PDFtoImage.Conversion.ToImage(stream, new Index(page), leaveOpen: true,
                    options: new PDFtoImage.RenderOptions(Dpi: dpi));
                cancellation.ThrowIfCancellationRequested();
                if (rendered.Width <= 0 || rendered.Height <= 0 ||
                    (long)rendered.Width * rendered.Height > 5_000_000)
                    throw new IOException("This PDF page exceeds the preview pixel limit.");
                using var encoded = rendered.Encode(SKEncodedImageFormat.Png, 90)
                    ?? throw new IOException("The PDF page could not be encoded for display.");
                using var pixels = new MemoryStream(encoded.ToArray(), writable: false);
                var bitmap = new Bitmap(pixels);
                if (cancellation.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    cancellation.ThrowIfCancellationRequested();
                }
                return (bitmap, pageCount);
            }, cancellation).ConfigureAwait(false);
        }
        finally { PdfGate.Release(); }
    }
#pragma warning restore CA1416

    public static async Task<Bitmap> ReadImageAsync(string path, int maximumDimension, CancellationToken cancellation = default)
    {
        await ImageGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var bitmap = await Task.Run(() => ReadImage(path, maximumDimension, cancellation), cancellation).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested)
            {
                bitmap.Dispose();
                cancellation.ThrowIfCancellationRequested();
            }
            return bitmap;
        }
        finally { ImageGate.Release(); }
    }

    private static Bitmap ReadImage(string path, int maximumDimension, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (NativeHeifPreview.Handles(path))
            return NativeHeifPreview.Decode(path, maximumDimension, cancellation);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 256L * 1024 * 1024) throw new IOException("The image exceeds the 256 MB preview limit.");
        SKImageInfo info;
        // Inspect dimensions before pixel allocation; also bound very tall and panoramic images.
        // The codec owns its SKStream wrapper, which deliberately leaves this FileStream open.
        using (var codec = SKCodec.Create(new SKManagedStream(stream, false)))
        {
            if (codec is null) throw new IOException("This image format could not be decoded.");
            info = codec.Info;
        }
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 64_000_000 ||
            info.Width > 32768 || info.Height > 32768)
            throw new IOException("This image exceeds the viewer's pixel limit. Open it in its image application.");
        cancellation.ThrowIfCancellationRequested();
        stream.Position = 0;
        return info.Width >= info.Height
            ? Bitmap.DecodeToWidth(stream, Math.Min(maximumDimension, info.Width))
            : Bitmap.DecodeToHeight(stream, Math.Min(maximumDimension, info.Height));
    }

    public static Task<string> ReadTextAsync(string path, CancellationToken cancellation) => Task.Run(async () =>
    {
        const int limit = 65536;
        cancellation.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, true);
        var bytes = new byte[Math.Min(limit, (int)Math.Min(stream.Length, int.MaxValue))];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read), cancellation).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        return DecodeText(bytes, read, stream.Length);
    }, cancellation);

    private static string DecodeText(byte[] bytes, int read, long length)
    {
        var data = bytes.AsSpan(0, read);
        string content;
        if (read >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            content = Encoding.Unicode.GetString(data[2..]);
        else if (read >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            content = Encoding.BigEndianUnicode.GetString(data[2..]);
        else if (data.Contains((byte)0))
        {
            var builder = new StringBuilder("Hex preview\n\n");
            for (var offset = 0; offset < Math.Min(read, 4096); offset += 16)
            {
                builder.Append(offset.ToString("X8")).Append("  ");
                builder.Append(Convert.ToHexString(data.Slice(offset, Math.Min(16, read - offset)))).AppendLine();
            }
            content = builder.ToString();
        }
        else content = Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
        return content + (length > read ? "\n\n[Preview limited to the first 64 KB. Open the file to read it fully.]" : "");
    }
}

internal sealed class FileThumbnail : ContentControl
{
    private Bitmap? _image;
    private bool _attached;
    private long _generation;
    private CancellationTokenSource? _loading;

    public FileThumbnail(FileEntry entry, bool allowImage)
    {
        Control Placeholder() => Ui.Icon(entry.IsDirectory ? "folder" : "file", size: 30);
        Content = Placeholder();
        AttachedToVisualTree += async (_, _) =>
        {
            _attached = true;
            var generation = ++_generation;
            _loading?.Cancel(); _loading?.Dispose();
            var token = (_loading = new CancellationTokenSource()).Token;
            Content = Placeholder();
            if (!allowImage || !PreviewFiles.IsImage(entry.Name) || entry.IsDirectory || entry.IsSymbolicLink ||
                entry.Attributes.HasFlag(FileAttributes.Offline) || entry.Length > 256L * 1024 * 1024) return;
            try
            {
                var bitmap = await PreviewFiles.ReadImageAsync(entry.FullPath, 128, token);
                if (!_attached || generation != _generation) { bitmap.Dispose(); return; }
                _image?.Dispose();
                _image = bitmap;
                Content = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            }
            catch (Exception) { }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false; ++_generation;
            _loading?.Cancel(); _loading?.Dispose(); _loading = null;
            _image?.Dispose(); _image = null;
        };
    }
}

internal sealed class FileTile : UserControl
{
    public FileTile(FileEntry entry, bool allowImage = true, bool filmstrip = false)
    {
        var picture = new FileThumbnail(entry, allowImage) { Width = 62, Height = 52 };
        var title = Ui.Text(entry.Name);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var details = Ui.Text($"{entry.Kind}   {entry.SizeText}", true);
        details.FontSize = 11;
        if (filmstrip)
        {
            Width = 110;
            picture.Width = 100;
            picture.Height = 82;
            Content = new StackPanel { Width = 106, Spacing = 3, Children = { picture, title } };
        }
        else
        {
            var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { title, details } };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("68,*"), Height = 58 };
            row.Children.Add(picture);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            Content = row;
        }
    }
}

internal sealed class PreviewPane : UserControl, IDisposable
{
    private readonly TextBlock _name = Ui.Text("Viewer");
    private readonly TextBlock _info = Ui.Text("Select a file to preview", true);
    private readonly TextBox _metadata = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, MinHeight = 100 };
    private readonly ContentControl _content = new();
    private readonly StackPanel _pdfToolbar = new() { Orientation = Orientation.Horizontal, Spacing = 4, IsVisible = false };
    private readonly StackPanel _imageToolbar = new() { Orientation = Orientation.Horizontal, Spacing = 4, IsVisible = false };
    private readonly StackPanel _videoToolbar = new() { Orientation = Orientation.Horizontal, Spacing = 4, IsVisible = false };
    private readonly TextBlock _pdfPosition = Ui.Text("");
    private readonly Button _pdfPrevious;
    private readonly Button _pdfNext;
    private readonly Button _pdfZoomOut;
    private readonly Button _pdfZoomIn;
    private readonly Button _flipImageButton;
    private readonly Button _stopVideoButton;
    private readonly Button _pauseVideoButton;
    private readonly Button _seekBackButton;
    private readonly Button _seekForwardButton;
    private CancellationTokenSource? _cancellation;
    private Bitmap? _bitmap;
    private Image? _pdfImage;
    private Image? _imageControl;
    private bool _imageFlipped;
    private FileEntry? _videoEntry;
    private VideoPreviewSession? _videoSession;
    private CancellationTokenSource? _videoStart;
    private readonly Stopwatch _videoElapsed = new();
    private double _videoOffsetSeconds;
    private bool _videoPaused;
    private bool _videoMirrored;
    private bool _videoRequested;
    private FileEntry? _pdfEntry;
    private int _pdfPage;
    private int _pdfPageCount;
    private double _pdfZoom = 1;
    private long _generation;
    public string DisplayedName => _name.Text ?? "";
    internal int DisplayedPdfPage => _pdfPageCount == 0 ? 0 : _pdfPage + 1;
    internal int DisplayedPdfPageCount => _pdfPageCount;
    internal bool HasRenderedPdfPage => _pdfPageCount > 0 && _bitmap is not null;
    internal bool HasVideoActions => _videoToolbar.IsVisible;
    internal bool HasActiveVideoPlayer => _videoSession is not null;
    internal bool IsVideoPaused => _videoPaused;
    internal double VideoApproximateSeconds => _videoOffsetSeconds + (_videoElapsed.IsRunning ? _videoElapsed.Elapsed.TotalSeconds : 0);
    internal bool IsImageFlipped => _imageFlipped;
    internal string PreviewStatus => _info.Text ?? "";
    public Task LoadingTask { get; private set; } = Task.CompletedTask;

    public PreviewPane(Func<Task> quickLook, Action<Exception> error)
    {
        _name.FontWeight = FontWeight.SemiBold;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _info.TextWrapping = TextWrapping.Wrap;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 5 };
        header.Children.Add(_name);
        var quickLookButton = Ui.AsyncButton("Quick Look", quickLook, error);
        Grid.SetColumn(quickLookButton, 1); header.Children.Add(quickLookButton);
        _pdfPrevious = Ui.Button("Previous", () => ChangePdfPage(-1), "Previous PDF page");
        _pdfNext = Ui.Button("Next", () => ChangePdfPage(1), "Next PDF page");
        _pdfZoomOut = Ui.Button("-", () => ChangePdfZoom(-0.25), "Zoom PDF out");
        _pdfZoomIn = Ui.Button("+", () => ChangePdfZoom(0.25), "Zoom PDF in");
        _pdfToolbar.Children.Add(_pdfPrevious);
        _pdfToolbar.Children.Add(_pdfPosition);
        _pdfToolbar.Children.Add(_pdfNext);
        _pdfToolbar.Children.Add(_pdfZoomOut);
        _pdfToolbar.Children.Add(_pdfZoomIn);
        _flipImageButton = Ui.Button("Flip image", FlipImage, "Mirror the displayed image horizontally without changing its file");
        _imageToolbar.Children.Add(_flipImageButton);
        _videoToolbar.Children.Add(Ui.AsyncButton("Play video", () => PlayVideoAsync(false), error,
            "Open the selected video in FFplay; Space pauses, arrows seek, Q closes"));
        _videoToolbar.Children.Add(Ui.AsyncButton("Flip & play", () => PlayVideoAsync(true), error,
            "Mirror video during FFplay playback; the original stays unchanged"));
        _pauseVideoButton = Ui.Button("Pause", ToggleVideoPause, "Pause or resume this preview without moving keyboard focus");
        _seekBackButton = Ui.Button("-5 s", () => SeekVideo(-5), "Seek backward five seconds using this preview's player");
        _seekForwardButton = Ui.Button("+5 s", () => SeekVideo(5), "Seek forward five seconds using this preview's player");
        _videoToolbar.Children.Add(_pauseVideoButton);
        _videoToolbar.Children.Add(_seekBackButton);
        _videoToolbar.Children.Add(_seekForwardButton);
        _stopVideoButton = Ui.Button("Stop playback", StopVideo, "Close this preview's FFplay process");
        _stopVideoButton.IsEnabled = false;
        UpdateVideoControls();
        _videoToolbar.Children.Add(_stopVideoButton);
        var toolbars = new StackPanel { Spacing = 3, Children = { _pdfToolbar, _imageToolbar, _videoToolbar } };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), RowSpacing = 7, Margin = new Thickness(10) };
        grid.Children.Add(header); Grid.SetRow(_info, 1); grid.Children.Add(_info);
        Grid.SetRow(toolbars, 2); grid.Children.Add(toolbars);
        Grid.SetRow(_content, 3); grid.Children.Add(_content);
        Grid.SetRow(_metadata, 4); grid.Children.Add(_metadata);
        Content = Ui.Surface(grid, "inset");
    }

    public void Show(FileEntry? entry)
    {
        LoadingTask = LoadAsync(entry, 0);
    }

    internal void ChangePdfPage(int offset)
    {
        if (_pdfEntry is null || _pdfPageCount < 1) return;
        var target = _pdfPage + offset;
        if (target < 0 || target >= _pdfPageCount) return;
        LoadingTask = LoadAsync(_pdfEntry, target);
    }

    private void ChangePdfZoom(double delta)
    {
        _pdfZoom = Math.Clamp(_pdfZoom + delta, 0.5, 3);
        UpdatePdfImageWidth();
        _pdfZoomOut.IsEnabled = _pdfZoom > 0.5;
        _pdfZoomIn.IsEnabled = _pdfZoom < 3;
    }

    private void UpdatePdfImageWidth()
    {
        if (_pdfImage?.Source is Bitmap bitmap)
            _pdfImage.Width = Math.Min(450, bitmap.PixelSize.Width) * _pdfZoom;
    }

    private void FlipImage()
    {
        if (_imageControl is null) return;
        _imageFlipped = !_imageFlipped;
        _imageControl.RenderTransformOrigin = RelativePoint.Center;
        _imageControl.RenderTransform = _imageFlipped ? new ScaleTransform(-1, 1) : null;
        var name = _imageFlipped ? "Unflip image" : "Flip image";
        _flipImageButton.Content = name;
        AutomationProperties.SetName(_flipImageButton, name);
    }

    private void UpdateVideoControls()
    {
        _pauseVideoButton.IsEnabled = _videoRequested;
        _pauseVideoButton.Content = _videoPaused ? "Resume" : "Pause";
        AutomationProperties.SetName(_pauseVideoButton, _videoPaused ? "Resume" : "Pause");
        _seekBackButton.IsEnabled = _videoRequested;
        _seekForwardButton.IsEnabled = _videoRequested;
    }

    private void ToggleVideoPause()
    {
        if (!_videoRequested) return;
        if (_videoPaused)
        {
            _videoPaused = false;
            UpdateVideoControls();
            _ = PlayVideoAsync(_videoMirrored, restart: true);
            return;
        }
        _videoOffsetSeconds = VideoApproximateSeconds;
        _videoElapsed.Reset();
        _videoPaused = true;
        _videoStart?.Cancel();
        _videoSession?.Dispose();
        _videoSession = null;
        _stopVideoButton.IsEnabled = true;
        _info.Text = $"Video · paused near {_videoOffsetSeconds:0.0}s; source unchanged";
        UpdateVideoControls();
    }

    private void SeekVideo(int seconds)
    {
        if (!_videoRequested) return;
        _videoOffsetSeconds = Math.Clamp(VideoApproximateSeconds + seconds, 0, 86400);
        _videoElapsed.Reset();
        if (_videoPaused)
        {
            _info.Text = $"Video · paused near {_videoOffsetSeconds:0.0}s; source unchanged";
            return;
        }
        _ = PlayVideoAsync(_videoMirrored, restart: true);
    }

    private async Task PlayVideoAsync(bool mirrored, bool restart = false)
    {
        var selected = _videoEntry;
        var executable = VideoPreviewSession.FindPlayer();
        if (selected is null || executable is null) return;
        var generation = _generation;
        _videoStart?.Cancel();
        _videoSession?.Dispose();
        _videoSession = null;
        if (!restart) _videoOffsetSeconds = 0;
        _videoElapsed.Reset();
        _videoRequested = true;
        _videoPaused = false;
        _videoMirrored = mirrored;
        UpdateVideoControls();
        var startAtSeconds = _videoOffsetSeconds;
        var request = new CancellationTokenSource();
        _videoStart = request;
        // Let the user cancel immediately, including while Process.Start is slow.
        _stopVideoButton.IsEnabled = true;
        try
        {
            var session = await Task.Run(() => VideoPreviewSession.Start(
                executable, selected.FullPath, mirrored, request.Token, startAtSeconds), request.Token);
            if (generation != _generation || request.IsCancellationRequested || !ReferenceEquals(request, _videoStart))
            {
                session.Dispose();
                return;
            }
            _videoSession?.Dispose();
            _videoSession = session;
            _videoElapsed.Restart();
            _stopVideoButton.IsEnabled = true;
            _info.Text = mirrored ? $"Video · mirrored in FFplay near {startAtSeconds:0.0}s; source unchanged"
                : $"Video · playing in FFplay near {startAtSeconds:0.0}s; source unchanged";
            _ = ObserveVideoExitAsync(session, generation);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            // A superseded or stopped start can fail after a newer playback succeeds.
            // Only the still-active request may publish an error to the preview pane.
            if (generation == _generation && ReferenceEquals(_videoStart, request) &&
                !request.IsCancellationRequested)
                _info.Text = $"Video preview unavailable: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_videoStart, request))
            {
                _videoStart = null;
                if (_videoSession is null && !_videoPaused)
                {
                    _videoRequested = false;
                    _stopVideoButton.IsEnabled = false;
                    UpdateVideoControls();
                }
            }
            request.Dispose();
        }
    }

    private async Task ObserveVideoExitAsync(VideoPreviewSession session, long generation)
    {
        int exitCode;
        try { exitCode = await session.WaitForExitAsync(); }
        catch (ObjectDisposedException) { return; }
        catch (InvalidOperationException) { return; }
        if (generation != _generation || !ReferenceEquals(_videoSession, session)) return;
        _videoSession = null;
        _videoRequested = false;
        _videoElapsed.Reset();
        session.Dispose();
        _stopVideoButton.IsEnabled = false;
        UpdateVideoControls();
        _info.Text = exitCode == 0 ? "Video · playback ended; source unchanged"
            : $"Video preview unavailable: FFplay exited with code {exitCode}; source unchanged";
    }

    private void StopVideo()
    {
        _videoStart?.Cancel();
        _videoSession?.Dispose();
        _videoSession = null;
        _videoElapsed.Reset();
        _videoOffsetSeconds = 0;
        _videoPaused = false;
        _videoRequested = false;
        UpdateVideoControls();
        _stopVideoButton.IsEnabled = false;
        if (_videoEntry is not null) _info.Text = "Video · playback stopped; source unchanged";
    }

    private async Task LoadAsync(FileEntry? entry, int pdfPage)
    {
        StopVideo();
        _videoEntry = null;
        _cancellation?.Cancel(); _cancellation?.Dispose();
        var token = (_cancellation = new CancellationTokenSource()).Token;
        var generation = ++_generation;
        _pdfEntry = null;
        _pdfToolbar.IsVisible = false;
        _imageToolbar.IsVisible = false;
        _videoToolbar.IsVisible = false;
        _imageControl = null;
        _imageFlipped = false;
        _flipImageButton.Content = "Flip image";
        AutomationProperties.SetName(_flipImageButton, "Flip image");
        _pdfPageCount = 0;
        _pdfImage = null;
        _content.Content = null;
        _bitmap?.Dispose(); _bitmap = null;
        if (entry is null) { _name.Text = "Viewer"; _info.Text = "Select a file to preview"; _metadata.Text = ""; return; }
        _name.Text = entry.Name;
        _info.Text = entry.Kind;
        _metadata.Text = $"{entry.FullPath}\n\nSize: {(entry.IsDirectory ? "Folder" : entry.SizeText)}\nModified: {entry.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nCreated: {entry.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nAttributes: {entry.Attributes}";
        if (entry.IsDirectory) { _content.Content = Ui.Icon(entry.IsSymbolicLink ? "link" : "folder", size: 72); return; }
        _info.Text = "Loading preview…";
        try
        {
            if (PreviewFiles.IsImage(entry.Name) && entry.Length <= 256L * 1024 * 1024)
            {
                var bitmap = await PreviewFiles.ReadImageAsync(entry.FullPath, 1600, token);
                if (generation != _generation) { bitmap.Dispose(); return; }
                _bitmap = bitmap;
                _imageControl = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                _content.Content = _imageControl;
                _imageToolbar.IsVisible = true;
                _info.Text = $"{entry.Kind} · {bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} preview";
            }
            else if (PreviewFiles.IsPdf(entry.Name) && !entry.IsSymbolicLink &&
                !entry.Attributes.HasFlag(FileAttributes.Offline))
            {
                var (bitmap, pageCount) = await PreviewFiles.ReadPdfPageAsync(entry.FullPath, pdfPage, token);
                if (generation != _generation || token.IsCancellationRequested) { bitmap.Dispose(); return; }
                _bitmap = bitmap;
                _pdfEntry = entry;
                _pdfPage = pdfPage;
                _pdfPageCount = pageCount;
                _pdfPosition.Text = $"Page {pdfPage + 1} / {pageCount}";
                _pdfPrevious.IsEnabled = pdfPage > 0;
                _pdfNext.IsEnabled = pdfPage + 1 < pageCount;
                _pdfZoomOut.IsEnabled = _pdfZoom > 0.5;
                _pdfZoomIn.IsEnabled = _pdfZoom < 3;
                _pdfImage = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                UpdatePdfImageWidth();
                _content.Content = new ScrollViewer
                {
                    Content = _pdfImage,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                };
                _pdfToolbar.IsVisible = true;
                _info.Text = $"PDF · {bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} preview";
            }
            else if (VideoPreviewSession.IsVideo(entry.Name))
            {
                _videoEntry = entry;
                if (entry.IsSymbolicLink || entry.Attributes.HasFlag(FileAttributes.Offline))
                    _info.Text = "Video preview unavailable for linked or offline files.";
                else if (VideoPreviewSession.FindPlayer() is null)
                    _info.Text = "Video preview requires FFplay on PATH. Open this file in its video application.";
                else
                {
                    _videoToolbar.IsVisible = true;
                    _info.Text = "Video · Play or Flip & play in FFplay (Space pauses, arrows seek, Q closes).";
                }
            }
            else
            {
                var text = await PreviewFiles.ReadTextAsync(entry.FullPath, token);
                if (generation != _generation) return;
                var textBox = new TextBox
                {
                    Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                    FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"), FontSize = 12
                };
                ScrollViewer.SetHorizontalScrollBarVisibility(textBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
                ScrollViewer.SetVerticalScrollBarVisibility(textBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
                _content.Content = textBox;
                _info.Text = text.StartsWith("Hex preview", StringComparison.Ordinal) ? "Binary file · bounded hex preview" : "Text preview";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (generation != _generation) return;
            _info.Text = $"Preview unavailable: {error.Message}";
        }
    }

    public void Dispose() { ++_generation; StopVideo(); _cancellation?.Cancel(); _cancellation?.Dispose(); _bitmap?.Dispose(); }
}
