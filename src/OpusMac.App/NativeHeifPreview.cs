using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace OpusMac.App;

/// <summary>
/// The native HEIF decoders are used only for high-efficiency images. Skia handles
/// ordinary raster formats. This path has no external process, temp image, or daemon.
/// The byte cache owns compressed preview PNGs, never disposable Avalonia Bitmaps.
/// </summary>
internal static class NativeHeifPreview
{
    private const long MaximumCacheBytes = 48L * 1024 * 1024;
    private const int MaximumEntries = 24;
    private static readonly object CacheLock = new();
    private static readonly LinkedList<CacheEntry> Cache = new();
    private static long _cacheBytes;
    private static int _nativeDecodes;
    private static int _cacheHits;
    private static int _derivedThumbnails;

    private sealed record CacheEntry(string Path, long Length, long ModifiedTicks,
        long CreatedTicks, int MaximumDimension, byte[] Png);

    public static bool Handles(string filename) =>
        Path.GetExtension(filename).ToLowerInvariant() is ".heic" or ".heif" or ".hif";

    public static Bitmap Decode(string path, int maximumDimension, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDimension, 1);
        cancellation.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("The image is no longer available.", fullPath);
        if (file.Length > 256L * 1024 * 1024)
            throw new IOException("The image exceeds the 256 MB preview limit.");
        var length = file.Length;
        var modified = file.LastWriteTimeUtc.Ticks;
        var created = file.CreationTimeUtc.Ticks;

        // A fresh Bitmap belongs to each caller; the cache can evict its PNG data
        // without invalidating the image already shown in the UI.
        byte[]? png = null;
        byte[]? largerCachedPng = null;
        var nearestLargerDimension = int.MaxValue;
        lock (CacheLock)
        {
            var node = Cache.First;
            while (node is not null)
            {
                var next = node.Next;
                var item = node.Value;
                if (SamePath(item.Path, fullPath))
                {
                    if (item.Length != length || item.ModifiedTicks != modified ||
                        item.CreatedTicks != created)
                    {
                        // A changed source invalidates *every* resolution, not just
                        // the one currently requested by the caller.
                        _cacheBytes -= item.Png.Length;
                        Cache.Remove(node);
                    }
                    else if (item.MaximumDimension == maximumDimension)
                    {
                        png = item.Png;
                        _cacheHits++;
                        Cache.Remove(node);
                        Cache.AddFirst(node);
                    }
                    else if (item.MaximumDimension > maximumDimension &&
                             item.MaximumDimension < nearestLargerDimension)
                    {
                        nearestLargerDimension = item.MaximumDimension;
                        largerCachedPng = item.Png;
                    }
                }
                node = next;
            }
        }

        if (png is null)
        {
            if (largerCachedPng is not null)
            {
                // Switching from a full preview to a 128px tile should not invoke
                // the expensive native HEIF decoder for the same file again.
                png = DownscaleCachedPng(largerCachedPng, maximumDimension, cancellation);
                lock (CacheLock)
                {
                    _cacheHits++;
                    _derivedThumbnails++;
                }
            }
            else
            {
                lock (CacheLock) _nativeDecodes++;
                png = OperatingSystem.IsMacOS()
                    ? MacImageIo.Decode(fullPath, maximumDimension, cancellation)
                    : OperatingSystem.IsWindows()
                        ? WindowsWic.Decode(fullPath, maximumDimension, cancellation)
                        : throw new PlatformNotSupportedException("HEIF preview requires native macOS Image I/O or Windows WIC.");
            }
            cancellation.ThrowIfCancellationRequested();
            file.Refresh();
            if (!file.Exists || file.Length != length ||
                file.LastWriteTimeUtc.Ticks != modified || file.CreationTimeUtc.Ticks != created)
                throw new IOException("The image changed while its preview was being generated.");
            if (png.LongLength <= MaximumCacheBytes)
            {
                lock (CacheLock)
                {
                    // Another request may have decoded the same file; keep one entry.
                    for (var node = Cache.First; node is not null;)
                    {
                        var next = node.Next;
                        if (SamePath(node.Value.Path, fullPath) &&
                            node.Value.MaximumDimension == maximumDimension)
                        {
                            _cacheBytes -= node.Value.Png.Length;
                            Cache.Remove(node);
                        }
                        node = next;
                    }
                    Cache.AddFirst(new CacheEntry(fullPath, length, modified, created,
                        maximumDimension, png));
                    _cacheBytes += png.Length;
                    while (Cache.Count > MaximumEntries || _cacheBytes > MaximumCacheBytes)
                    {
                        var last = Cache.Last!;
                        _cacheBytes -= last.Value.Png.Length;
                        Cache.RemoveLast();
                    }
                }
            }
        }
        else
        {
            // The metadata snapshot used for the cache key must still be current
            // after a hit. Never return a preview for a file changed mid-request.
            file.Refresh();
            if (!file.Exists || file.Length != length ||
                file.LastWriteTimeUtc.Ticks != modified || file.CreationTimeUtc.Ticks != created)
                throw new IOException("The image changed while its cached preview was being loaded.");
        }
        cancellation.ThrowIfCancellationRequested();
        using var preview = new MemoryStream(png, writable: false);
        return new Bitmap(preview);
    }

    internal static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
            _cacheBytes = 0;
            _nativeDecodes = 0;
            _cacheHits = 0;
            _derivedThumbnails = 0;
        }
    }

    internal static (int Entries, long Bytes, int NativeDecodes, int CacheHits, int DerivedThumbnails) CacheStats()
    {
        lock (CacheLock)
            return (Cache.Count, _cacheBytes, _nativeDecodes, _cacheHits, _derivedThumbnails);
    }

    private static byte[] DownscaleCachedPng(byte[] encoded, int maximumDimension,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var image = SKBitmap.Decode(encoded)
            ?? throw new IOException("The cached HEIF image is no longer decodable.");
        var longest = Math.Max(image.Width, image.Height);
        if (longest < 1) throw new IOException("The cached HEIF image has invalid dimensions.");
        if (longest <= maximumDimension) return encoded;

        var ratio = maximumDimension / (double)longest;
        var width = Math.Max(1, (int)Math.Round(image.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(image.Height * ratio));
        using var resized = new SKBitmap(new SKImageInfo(width, height,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(resized))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(image, new SKRect(0, 0, width, height), paint);
            canvas.Flush();
        }
        cancellation.ThrowIfCancellationRequested();
        using var png = resized.Encode(SKEncodedImageFormat.Png, 95)
            ?? throw new IOException("Could not encode the scaled cached HEIF preview.");
        return png.ToArray();
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static byte[] EncodeBgra(byte[] bgra, int width, int height, bool premultiplied)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888,
            premultiplied ? SKAlphaType.Premul : SKAlphaType.Unpremul));
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 95)
            ?? throw new IOException("The HEIF preview could not be encoded.");
        return png.ToArray();
    }

    private static class WindowsWic
    {
        private static readonly Guid FactoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
        private static readonly Guid FactoryInterface = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
        private static readonly Guid Bgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context,
            ref Guid iid, out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int OpenFile(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr vendor, uint access, uint metadataOptions, out IntPtr decoder);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateObject(IntPtr self, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetFrame(IntPtr self, uint index, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetSize(IntPtr self, out uint width, out uint height);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitScaler(IntPtr self, IntPtr source, uint width, uint height, uint mode);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitConverter(IntPtr self, IntPtr source, ref Guid format,
            uint dither, IntPtr palette, double alphaThreshold, uint paletteType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CopyPixels(IntPtr self, IntPtr rect, uint stride, uint size, IntPtr pixels);

        private static T Method<T>(IntPtr obj, int index) where T : Delegate
        {
            var vtable = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, index * IntPtr.Size));
        }

        public static byte[] Decode(string path, int maximumDimension, CancellationToken cancellation)
        {
            IntPtr factory = IntPtr.Zero, decoder = IntPtr.Zero, frame = IntPtr.Zero;
            IntPtr scaler = IntPtr.Zero, converter = IntPtr.Zero;
            try
            {
                var factoryClass = FactoryClass;
                var factoryInterface = FactoryInterface;
                Marshal.ThrowExceptionForHR(CoCreateInstance(ref factoryClass, IntPtr.Zero, 1,
                    ref factoryInterface, out factory));
                // 0x80000000 = GENERIC_READ, metadata cache on demand = 0.
                Marshal.ThrowExceptionForHR(Method<OpenFile>(factory, 3)(factory, path,
                    IntPtr.Zero, 0x80000000, 0, out decoder));
                Marshal.ThrowExceptionForHR(Method<GetFrame>(decoder, 13)(decoder, 0, out frame));
                Marshal.ThrowExceptionForHR(Method<GetSize>(frame, 3)(frame, out var w, out var h));
                if (w == 0 || h == 0 || (long)w * h > 64_000_000)
                    throw new IOException("The HEIF file has unsupported image dimensions.");
                cancellation.ThrowIfCancellationRequested();
                var scale = Math.Min(1.0, maximumDimension / (double)Math.Max(w, h));
                var width = Math.Max(1, (uint)Math.Round(w * scale));
                var height = Math.Max(1, (uint)Math.Round(h * scale));
                Marshal.ThrowExceptionForHR(Method<CreateObject>(factory, 11)(factory, out scaler));
                // WICBitmapInterpolationModeFant = 3 (high-quality downscaling).
                Marshal.ThrowExceptionForHR(Method<InitScaler>(scaler, 8)(scaler, frame,
                    width, height, 3));
                Marshal.ThrowExceptionForHR(Method<CreateObject>(factory, 10)(factory, out converter));
                var format = Bgra32;
                Marshal.ThrowExceptionForHR(Method<InitConverter>(converter, 8)(converter,
                    scaler, ref format, 0, IntPtr.Zero, 0, 0));
                cancellation.ThrowIfCancellationRequested();
                var byteCount = checked((int)(width * height * 4));
                var pixels = new byte[byteCount];
                var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<CopyPixels>(converter, 7)(converter,
                        IntPtr.Zero, width * 4, (uint)byteCount, pin.AddrOfPinnedObject()));
                }
                finally { pin.Free(); }
                cancellation.ThrowIfCancellationRequested();
                return EncodeBgra(pixels, (int)width, (int)height, premultiplied: false);
            }
            catch (COMException error) when (error.HResult is unchecked((int)0x88982F50) or unchecked((int)0xC00D5212))
            {
                throw new IOException("No compatible Windows WIC HEIF decoder is installed.", error);
            }
            finally
            {
                if (converter != IntPtr.Zero) Marshal.Release(converter);
                if (scaler != IntPtr.Zero) Marshal.Release(scaler);
                if (frame != IntPtr.Zero) Marshal.Release(frame);
                if (decoder != IntPtr.Zero) Marshal.Release(decoder);
                if (factory != IntPtr.Zero) Marshal.Release(factory);
            }
        }
    }

    private static class MacImageIo
    {
        private const string ImageIo = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private static readonly Lazy<nint> ImageIoHandle = new(() => NativeLibrary.Load(ImageIo));
        private static readonly Lazy<nint> FoundationHandle = new(() => NativeLibrary.Load(CoreFoundation));
        private static readonly Lazy<nint> GraphicsHandle = new(() => NativeLibrary.Load(CoreGraphics));

        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public double X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Size { public double Width, Height; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public Point Origin; public Size Size; }

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator,
            byte[] bytes, nint length, [MarshalAs(UnmanagedType.I1)] bool isDirectory);
        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity,
            IntPtr keyCallbacks, IntPtr valueCallbacks);
        [DllImport(CoreFoundation)]
        private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
        [DllImport(CoreFoundation)]
        private static extern IntPtr CFNumberCreate(IntPtr allocator, int type, ref int value);
        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr value);

        [DllImport(ImageIo)]
        private static extern IntPtr CGImageSourceCreateWithURL(IntPtr url, IntPtr options);
        [DllImport(ImageIo)]
        private static extern IntPtr CGImageSourceCreateThumbnailAtIndex(IntPtr source,
            nuint index, IntPtr options);
        [DllImport(CoreGraphics)]
        private static extern nuint CGImageGetWidth(IntPtr image);
        [DllImport(CoreGraphics)]
        private static extern nuint CGImageGetHeight(IntPtr image);
        [DllImport(CoreGraphics)]
        private static extern IntPtr CGColorSpaceCreateWithName(IntPtr name);
        [DllImport(CoreGraphics)]
        private static extern void CGColorSpaceRelease(IntPtr colorSpace);
        [DllImport(CoreGraphics)]
        private static extern void CGImageRelease(IntPtr image);
        [DllImport(CoreGraphics)]
        private static extern IntPtr CGBitmapContextCreate(IntPtr data, nuint width, nuint height,
            nuint bitsPerComponent, nuint bytesPerRow, IntPtr colorspace, uint bitmapInfo);
        [DllImport(CoreGraphics)]
        private static extern void CGContextTranslateCTM(IntPtr context, double tx, double ty);
        [DllImport(CoreGraphics)]
        private static extern void CGContextScaleCTM(IntPtr context, double sx, double sy);
        [DllImport(CoreGraphics)]
        private static extern void CGContextDrawImage(IntPtr context, Rect rectangle, IntPtr image);
        [DllImport(CoreGraphics)]
        private static extern void CGContextRelease(IntPtr context);

        private static IntPtr Key(string symbol) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(ImageIoHandle.Value, symbol));
        private static IntPtr TrueValue =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(FoundationHandle.Value, "kCFBooleanTrue"));

        public static byte[] Decode(string path, int maximumDimension, CancellationToken cancellation)
        {
            IntPtr url = IntPtr.Zero, source = IntPtr.Zero, options = IntPtr.Zero;
            IntPtr number = IntPtr.Zero, thumbnail = IntPtr.Zero, color = IntPtr.Zero;
            IntPtr context = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                var filename = System.Text.Encoding.UTF8.GetBytes(path);
                url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, filename,
                    filename.Length, false);
                if (url == IntPtr.Zero) throw new IOException("Could not read the HEIF path.");
                source = CGImageSourceCreateWithURL(url, IntPtr.Zero);
                if (source == IntPtr.Zero) throw new IOException("Image I/O cannot open this HEIF file.");
                options = CFDictionaryCreateMutable(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
                if (options == IntPtr.Zero) throw new OutOfMemoryException();
                number = CFNumberCreate(IntPtr.Zero, 9, ref maximumDimension);
                if (number == IntPtr.Zero) throw new OutOfMemoryException();
                CFDictionarySetValue(options, Key("kCGImageSourceCreateThumbnailFromImageAlways"), TrueValue);
                CFDictionarySetValue(options, Key("kCGImageSourceCreateThumbnailWithTransform"), TrueValue);
                CFDictionarySetValue(options, Key("kCGImageSourceThumbnailMaxPixelSize"), number);
                cancellation.ThrowIfCancellationRequested();
                thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, options);
                if (thumbnail == IntPtr.Zero) throw new IOException("Image I/O could not decode the HEIF image.");
                var w = CGImageGetWidth(thumbnail);
                var h = CGImageGetHeight(thumbnail);
                if (w == 0 || h == 0 || w > 32768 || h > 32768 || (long)w * (long)h > 16_000_000)
                    throw new IOException("The HEIF thumbnail dimensions are not safe.");
                var width = checked((int)w);
                var height = checked((int)h);
                var byteCount = checked(width * height * 4);
                cancellation.ThrowIfCancellationRequested();
                buffer = Marshal.AllocHGlobal(byteCount);
                // The Avalonia bitmap / cached PNG is untagged 8-bit BGRA. Convert
                // embedded source profiles (including Display P3) into a stable
                // named sRGB output space rather than the current monitor's
                // device-dependent RGB. HDR fidelity remains an explicit Mac gate.
                var srgbName = Marshal.ReadIntPtr(
                    NativeLibrary.GetExport(GraphicsHandle.Value, "kCGColorSpaceSRGB"));
                color = CGColorSpaceCreateWithName(srgbName);
                if (color == IntPtr.Zero) throw new IOException("Could not create an sRGB preview color space.");
                // kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst = BGRA.
                context = CGBitmapContextCreate(buffer, w, h, 8, (nuint)(width * 4), color, 0x2002);
                if (context == IntPtr.Zero) throw new IOException("Could not create an image rendering context.");
                CGContextTranslateCTM(context, 0, height);
                CGContextScaleCTM(context, 1, -1);
                CGContextDrawImage(context, new Rect { Size = new Size { Width = width, Height = height } }, thumbnail);
                cancellation.ThrowIfCancellationRequested();
                var pixels = new byte[byteCount];
                Marshal.Copy(buffer, pixels, 0, byteCount);
                return EncodeBgra(pixels, width, height, premultiplied: true);
            }
            finally
            {
                if (context != IntPtr.Zero) CGContextRelease(context);
                if (color != IntPtr.Zero) CGColorSpaceRelease(color);
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (thumbnail != IntPtr.Zero) CGImageRelease(thumbnail);
                if (number != IntPtr.Zero) CFRelease(number);
                if (options != IntPtr.Zero) CFRelease(options);
                if (source != IntPtr.Zero) CFRelease(source);
                if (url != IntPtr.Zero) CFRelease(url);
            }
        }
    }
}
