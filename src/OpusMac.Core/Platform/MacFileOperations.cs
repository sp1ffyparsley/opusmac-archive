using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpusMac.Core.Platform;

/// <summary>Apple libSystem copyfile and Foundation trash/move APIs; no interpreter or helper dependency.</summary>
internal static class MacFileOperations
{
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private const uint Metadata = 1 | 2 | 4; // COPYFILE_ACL | COPYFILE_STAT | COPYFILE_XATTR.
    private const uint All = Metadata | 8;
    private const uint Exclusive = 1u << 17;
    private const uint NoFollow = (1u << 18) | (1u << 19);
    private const uint Clone = 1u << 24;
    private const int OpenDirectoryNoFollow = 0x100000 | 0x100 | 0x1000000;
    private static readonly Lazy<nint> Foundation = new(() => NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation"));

    public static bool TryMoveEntry(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RenameExclusive(source, destination, 4) == 0) return true; // RENAME_EXCL, never replace.
        var error = Marshal.GetLastPInvokeError();
        if (error == 18) return false; // EXDEV: the operation owner performs a staged copy and commit.
        throw NativeError("Move", source, error);
    }

    public static void CopyLeaf(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WithCopyState(cancellationToken, state =>
        {
            var result = CopyFile(source, destination, state, All | Exclusive | NoFollow | Clone);
            var error = Marshal.GetLastPInvokeError();
            if (result < 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw NativeError("Copy", source, error);
            }
            cancellationToken.ThrowIfCancellationRequested();
        });
    }

    public static void CopyDirectoryMetadata(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceHandle = Open(source, OpenDirectoryNoFollow);
        if (sourceHandle < 0) throw NativeError("Read directory metadata", source, Marshal.GetLastPInvokeError());
        try
        {
            var destinationHandle = Open(destination, OpenDirectoryNoFollow);
            if (destinationHandle < 0) throw NativeError("Open metadata destination", destination, Marshal.GetLastPInvokeError());
            try
            {
                WithCopyState(cancellationToken, state =>
                {
                    var result = FCopyFile(sourceHandle, destinationHandle, state, Metadata);
                    var error = Marshal.GetLastPInvokeError();
                    if (result < 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw NativeError("Copy directory metadata", source, error);
                    }
                });
            }
            finally { _ = Close(destinationHandle); }
        }
        finally { _ = Close(sourceHandle); }
    }

    public static TrashResult Trash(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var pool = new AutoreleaseScope();
        var fileManager = SendPointer(GetClass("NSFileManager"), Selector("defaultManager"));
        var url = FileUrl(path);
        var succeeded = SendTrash(fileManager, Selector("trashItemAtURL:resultingItemURL:error:"), url, out var resultingUrl, out var error);
        // Once the OS committed the mutation, return its receipt even if cancellation arrived afterwards.
        if (succeeded == 0) throw FoundationError("Move to Trash", path, error);
        var resultingPath = resultingUrl == 0 ? null : StringValue(SendPointer(resultingUrl, Selector("path")));
        if (string.IsNullOrWhiteSpace(resultingPath)) return new TrashResult(null);
        return new TrashResult(Path.GetFullPath(resultingPath));
    }

    public static void RestoreTrash(string trashedPath, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var pool = new AutoreleaseScope();
        var fileManager = SendPointer(GetClass("NSFileManager"), Selector("defaultManager"));
        var succeeded = SendMove(fileManager, Selector("moveItemAtURL:toURL:error:"), FileUrl(trashedPath), FileUrl(destination), out var error);
        if (succeeded == 0) throw FoundationError("Restore from Trash", trashedPath, error);
    }

    private static void WithCopyState(CancellationToken cancellationToken, Action<nint> action)
    {
        var state = CopyStateAllocate();
        if (state == 0) throw new OutOfMemoryException("macOS could not allocate a file copy state.");
        // QUIT on native errors avoids retrying failed writes forever. Successful APFS clones need no data callbacks.
        CopyCallback callback = (_, stage, _, _, _, _) => cancellationToken.IsCancellationRequested || stage == 3 ? 2 : 0;
        try
        {
            if (CopyStateSet(state, 6, Marshal.GetFunctionPointerForDelegate(callback)) < 0)
                throw NativeError("Install copy cancellation", string.Empty, Marshal.GetLastPInvokeError());
            action(state);
        }
        finally
        {
            GC.KeepAlive(callback);
            _ = CopyStateFree(state);
        }
    }

    public static void CreateHardLink(string source, string destination)
    {
        if (CreateHardLinkNative(source, destination) != 0)
            throw NativeError("Hard link (requires one volume)", source, Marshal.GetLastPInvokeError());
    }

    [DllImport(SystemLibrary, EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkNative([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    private static IOException NativeError(string action, string path, int error) =>
        new($"{action} failed for {path}: {new Win32Exception(error).Message} (errno {error}).");

    private static IOException FoundationError(string action, string path, nint error)
    {
        var message = error == 0 ? "macOS did not provide error details." : StringValue(SendPointer(error, Selector("localizedDescription")));
        return new IOException($"{action} failed for {path}: {message}");
    }

    private static nint GetClass(string name)
    {
        _ = Foundation.Value;
        var value = ObjcGetClass(name);
        return value == 0 ? throw new InvalidOperationException("macOS Foundation class is unavailable: " + name) : value;
    }

    private static nint FileUrl(string path)
    {
        var text = SendUtf8(GetClass("NSString"), Selector("stringWithUTF8String:"), path);
        if (text == 0) throw new IOException("macOS could not represent the selected path.");
        var url = SendArgument(GetClass("NSURL"), Selector("fileURLWithPath:"), text);
        return url == 0 ? throw new IOException("macOS could not create a file URL.") : url;
    }

    private static string? StringValue(nint value) =>
        value == 0 ? null : Marshal.PtrToStringUTF8(SendPointer(value, Selector("UTF8String")));

    private sealed class AutoreleaseScope : IDisposable
    {
        private readonly nint _pool = SendPointer(SendPointer(GetClass("NSAutoreleasePool"), Selector("alloc")), Selector("init"));
        public void Dispose() { if (_pool != 0) SendVoid(_pool, Selector("drain")); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CopyCallback(int what, int stage, nint state, nint source, nint destination, nint context);
    [DllImport(SystemLibrary, EntryPoint = "copyfile", SetLastError = true)]
    private static extern int CopyFile([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, nint state, uint flags);
    [DllImport(SystemLibrary, EntryPoint = "fcopyfile", SetLastError = true)]
    private static extern int FCopyFile(int source, int destination, nint state, uint flags);
    [DllImport(SystemLibrary, EntryPoint = "copyfile_state_alloc", SetLastError = true)] private static extern nint CopyStateAllocate();
    [DllImport(SystemLibrary, EntryPoint = "copyfile_state_free")] private static extern int CopyStateFree(nint state);
    [DllImport(SystemLibrary, EntryPoint = "copyfile_state_set", SetLastError = true)] private static extern int CopyStateSet(nint state, uint flag, nint source);
    [DllImport(SystemLibrary, EntryPoint = "open", SetLastError = true)] private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport(SystemLibrary, EntryPoint = "close")] private static extern int Close(int handle);
    [DllImport(SystemLibrary, EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusive([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
    [DllImport(ObjectiveC, EntryPoint = "objc_getClass")] private static extern nint ObjcGetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjectiveC, EntryPoint = "sel_registerName")] private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint SendPointer(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint SendArgument(nint receiver, nint selector, nint argument);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint SendUtf8(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(nint receiver, nint selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern byte SendTrash(nint receiver, nint selector, nint url, out nint resultingUrl, out nint error);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern byte SendMove(nint receiver, nint selector, nint source, nint destination, out nint error);
}
