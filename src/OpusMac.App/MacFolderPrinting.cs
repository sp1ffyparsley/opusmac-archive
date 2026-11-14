using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using OpusMac.Core.Persistence;

namespace OpusMac.App;

internal static class MacFolderPrinting
{
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private static readonly Lazy<nint> Framework = new(() => NativeLibrary.Load(AppKit));
    private static readonly Lazy<nint> PrintView = new(RegisterPrintView);
    private static readonly PageLabel ReadPageLabel = GetAssociatedObject;

    private static void EnsureAppKit()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Folder listing printing uses the macOS print panel.");
        Dispatcher.UIThread.VerifyAccess();
        _ = Framework.Value;
        if (SendBool(Class("NSThread"), Selector("isMainThread")) == 0)
            throw new InvalidOperationException("Open the print settings from the macOS main thread.");
    }

    public static string[] AvailableFontNames()
    {
        EnsureAppKit();
        var pool = NewPool();
        try
        {
            var manager = SendPointer(Class("NSFontManager"), Selector("sharedFontManager"));
            var names = SendPointer(manager, Selector("availableFonts"));
            var count = SendPointer(names, Selector("count"));
            var result = new List<string>();
            for (nint index = 0; index < count; index++)
            {
                var name = SendObject(names, Selector("objectAtIndex:"), index);
                var utf8 = SendPointer(name, Selector("UTF8String"));
                if (utf8 != 0 && Marshal.PtrToStringUTF8(utf8) is { Length: > 0 } value)
                    result.Add(value);
            }
            return result.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { if (pool != 0) SendVoid(pool, Selector("drain")); }
    }

    public static bool Print(string title, string text, FolderListingPrintFont selection, IReadOnlyList<double> columnWidths,
        string header, string footer)
    {
        EnsureAppKit();
        if (SendPointer(Class("NSPrintOperation"), Selector("currentOperation")) != 0)
            throw new InvalidOperationException("Finish the existing macOS print operation first.");

        var pool = NewPool();
        var view = nint.Zero;
        try
        {
            Trace.TraceInformation("Print Folder: preparing the AppKit print panel.");
            var normalizedText = text.Replace("\r\n", "\n");
            var contents = NativeString(normalizedText);
            var jobTitle = NativeString(title);
            var font = selection.PostScriptName is { } name
                ? SendNamedFont(Class("NSFont"), Selector("fontWithName:size:"), NativeString(name), selection.PointSize)
                : SendFont(Class("NSFont"), Selector("monospacedSystemFontOfSize:weight:"), selection.PointSize, 0);
            if (font == 0)
                throw new IOException("The selected printer font is unavailable. Choose an installed font in Print Folder.");
            view = InitFrame(SendPointer(PrintView.Value, Selector("alloc")), Selector("initWithFrame:"),
                new NativeRect(0, 0, 510, 20));
            if (view == 0) throw new IOException("AppKit could not prepare the folder listing for printing.");
            SetSize(view, Selector("setMaxSize:"), new NativeSize(510, 100_000_000));
            SetBool(view, Selector("setHorizontallyResizable:"), 0);
            SetBool(view, Selector("setVerticallyResizable:"), 1);
            SetObject(view, Selector("setString:"), contents);
            SetObject(view, Selector("setFont:"), font);
            ApplyColumnTabs(view, font, normalizedText.Length, columnWidths);
            SetPageLabel(view, "pageHeader", header);
            SetPageLabel(view, "pageFooter", footer);
            SendVoid(view, Selector("sizeToFit"));
            var headerFooterKey = AppKitConstant("NSPrintHeaderAndFooter");
            var labelsEnabled = NumberWithBool(Class("NSNumber"), Selector("numberWithBool:"),
                (byte)(header.Length > 0 || footer.Length > 0 ? 1 : 0));
            var operation = SendObject(Class("NSPrintOperation"), Selector("printOperationWithView:"), view);
            if (operation == 0) throw new IOException("AppKit could not create the folder listing print operation.");
            var info = SendPointer(operation, Selector("printInfo"));
            var settings = SendPointer(info, Selector("dictionary"));
            SetDictionaryEntry(settings, Selector("setObject:forKey:"), labelsEnabled, headerFooterKey);
            SetObject(operation, Selector("setJobTitle:"), jobTitle);
            SetBool(operation, Selector("setShowsPrintPanel:"), 1);
            var completed = SendBool(operation, Selector("runOperation")) != 0;
            Trace.TraceInformation("Print Folder: AppKit operation {0}.", completed ? "completed" : "did not complete");
            return completed;
        }
        finally
        {
            if (view != 0) SendVoid(view, Selector("release"));
            if (pool != 0) SendVoid(pool, Selector("drain"));
        }
    }

    private static nint RegisterPrintView()
    {
        var subclass = AllocateClassPair(Class("NSTextView"), "OpusMacFolderListingPrintView", 0);
        if (subclass == 0) throw new IOException("AppKit could not allocate the print view class.");
        var callback = Marshal.GetFunctionPointerForDelegate(ReadPageLabel);
        if (AddMethod(subclass, Selector("pageHeader"), callback, "@@:") == 0 ||
            AddMethod(subclass, Selector("pageFooter"), callback, "@@:") == 0)
        {
            DisposeClassPair(subclass);
            throw new IOException("AppKit could not register the print page labels.");
        }
        RegisterClassPair(subclass);
        return subclass;
    }

    private static void SetPageLabel(nint view, string selector, string text)
    {
        var contents = NativeString(text);
        var attributed = SendObject(SendPointer(Class("NSAttributedString"), Selector("alloc")), Selector("initWithString:"), contents);
        if (attributed == 0) throw new IOException("AppKit could not create the print page label.");
        // OBJC_ASSOCIATION_RETAIN_NONATOMIC keeps each label with its view until the job is released.
        SetAssociatedObject(view, Selector(selector), attributed, 1);
        SendVoid(attributed, Selector("release"));
    }

    private static nint AppKitConstant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Framework.Value, name));

    private static void ApplyColumnTabs(nint view, nint font, int length, IReadOnlyList<double> widths)
    {
        if (length == 0 || widths.Count < 2) return;
        var fontKey = AppKitConstant("NSFontAttributeName");
        var paragraphKey = AppKitConstant("NSParagraphStyleAttributeName");
        var attributes = DictionaryWithEntry(Class("NSDictionary"), Selector("dictionaryWithObject:forKey:"), font, fontKey);
        var nominalWidth = GetTextSize(NativeString("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"),
            Selector("sizeWithAttributes:"), attributes).Width / 62;
        if (!double.IsFinite(nominalWidth) || nominalWidth <= 0)
            throw new IOException("AppKit could not measure the selected print font.");

        var tabs = SendPointer(Class("NSMutableArray"), Selector("array"));
        var options = SendPointer(Class("NSDictionary"), Selector("dictionary"));
        var location = 0d;
        for (var index = 0; index < widths.Count - 1; index++)
        {
            location += widths[index] * nominalWidth;
            var tab = InitTab(SendPointer(Class("NSTextTab"), Selector("alloc")),
                Selector("initWithTextAlignment:location:options:"), 0, location, options);
            if (tab == 0) throw new IOException("AppKit could not create the print column tab stop.");
            SetObject(tabs, Selector("addObject:"), tab);
            SendVoid(tab, Selector("release"));
        }
        var style = SendPointer(Class("NSMutableParagraphStyle"), Selector("new"));
        if (style == 0) throw new IOException("AppKit could not create the print paragraph style.");
        try
        {
            SetObject(style, Selector("setTabStops:"), tabs);
            var storage = SendPointer(view, Selector("textStorage"));
            AddAttribute(storage, Selector("addAttribute:value:range:"), paragraphKey, style, new NativeRange(0, (nuint)length));
        }
        finally { SendVoid(style, Selector("release")); }
    }

    private static nint NewPool() =>
        SendPointer(SendPointer(Class("NSAutoreleasePool"), Selector("alloc")), Selector("init"));

    private static nint Class(string name)
    {
        var value = GetClass(name);
        return value != 0 ? value : throw new IOException($"AppKit class {name} is unavailable.");
    }

    private static nint NativeString(string value)
    {
        var text = SendUtf8(Class("NSString"), Selector("stringWithUTF8String:"), value);
        return text != 0 ? text : throw new IOException("AppKit could not represent the folder listing as text.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect(double x, double y, double width, double height)
    {
        private readonly double _x = x, _y = y, _width = width, _height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(double width, double height)
    {
        private readonly double _width = width, _height = height;
        public double Width => _width;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRange(nuint start, nuint length)
    {
        private readonly nuint _start = start, _length = length;
    }

    [DllImport(ObjectiveC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjectiveC, EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint SendPointer(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(nint receiver, nint selector, nint argument);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void SetObject(nint receiver, nint selector, nint argument);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint SendUtf8(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint SendFont(nint receiver, nint selector, double size, double weight);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint SendNamedFont(nint receiver, nint selector, nint name, double size);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint InitFrame(nint receiver, nint selector, NativeRect frame);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern byte SendBool(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void SetBool(nint receiver, nint selector, byte value);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void SetSize(nint receiver, nint selector, NativeSize value);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint DictionaryWithEntry(nint receiver, nint selector, nint value, nint key);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern NativeSize GetTextSize(nint receiver, nint selector, nint attributes);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint InitTab(nint receiver, nint selector, nint alignment, double location, nint options);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void AddAttribute(nint receiver, nint selector, nint key, nint value, NativeRange range);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint NumberWithBool(nint receiver, nint selector, byte value);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void SetDictionaryEntry(nint receiver, nint selector, nint value, nint key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PageLabel(nint view, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_allocateClassPair")]
    private static extern nint AllocateClassPair(nint parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);
    [DllImport(ObjectiveC, EntryPoint = "class_addMethod")]
    private static extern byte AddMethod(nint type, nint selector, nint implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [DllImport(ObjectiveC, EntryPoint = "objc_registerClassPair")]
    private static extern void RegisterClassPair(nint type);
    [DllImport(ObjectiveC, EntryPoint = "objc_disposeClassPair")]
    private static extern void DisposeClassPair(nint type);
    [DllImport(ObjectiveC, EntryPoint = "objc_getAssociatedObject")]
    private static extern nint GetAssociatedObject(nint view, nint key);
    [DllImport(ObjectiveC, EntryPoint = "objc_setAssociatedObject")]
    private static extern void SetAssociatedObject(nint view, nint key, nint value, nuint policy);
}
