using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpusMac.Core.Platform;

[SupportedOSPlatform("windows")]
internal static class WindowsFileOperations
{
    private const uint IoReparseTagSymlink = 0xA000000C;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileAttributeTagInfoClass = 9;
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private const uint FileSystemPath = 0x80058000;
    private const uint FileOperationFlags = 0x0004 | 0x0010 | 0x0400 | 0x00080000 | 0x00100000 | 0x20000000;

    public static void CreateHardLink(string source, string destination)
    {
        if (CreateHardLinkNative(NativePath(destination), NativePath(source), 0)) return;
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        throw new IOException($"Hard link failed for {source}. Check that both paths are on the same volume: {error.Message}", error);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkNative(string destination, string source, nint security);

    public static bool TryMoveEntry(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MoveFileEx(NativePath(source), NativePath(destination), 0)) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == 17) return false; // ERROR_NOT_SAME_DEVICE. No COPY_ALLOWED flag.
        throw new IOException($"Move failed for {source}: {new Win32Exception(error).Message}", new Win32Exception(error));
    }

    public static void CopyLeaf(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(source);
        if (attributes.HasFlag(FileAttributes.Directory) && attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (GetReparseTag(source) != IoReparseTagSymlink)
                throw new IOException("The directory reparse point is not a supported symbolic link: " + source);
            var target = new DirectoryInfo(source).LinkTarget;
            if (target == null) throw new IOException("The directory reparse point is not a supported symbolic link: " + source);
            Directory.CreateSymbolicLink(destination, target);
            return;
        }
        CopyProgress callback = (_, _, _, _, _, _, _, _, _) => cancellationToken.IsCancellationRequested ? 1u : 0u;
        var cancel = false;
        var succeeded = CopyFileEx(NativePath(source), NativePath(destination), callback, 0, ref cancel, 0x00000001 | 0x00000800);
        var error = Marshal.GetLastPInvokeError();
        GC.KeepAlive(callback);
        if (!succeeded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException($"Copy failed for {source}: {new Win32Exception(error).Message}", new Win32Exception(error));
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static uint GetReparseTag(string path)
    {
        using var handle = CreateFile(
            NativePath(path),
            0x00000080, // FILE_READ_ATTRIBUTES
            0x00000007, // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
            0,
            3, // OPEN_EXISTING
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            0);
        if (handle.IsInvalid)
            throw new IOException($"The reparse point cannot be inspected: {path}", new Win32Exception(Marshal.GetLastPInvokeError()));
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out var information,
                Marshal.SizeOf<FileAttributeTagInformation>()))
            throw new IOException($"The reparse point type cannot be read: {path}", new Win32Exception(Marshal.GetLastPInvokeError()));
        return information.ReparseTag;
    }

    public static void CopyDirectoryMetadata(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var from = new DirectoryInfo(source);
        var to = new DirectoryInfo(destination);
        to.SetAccessControl(from.GetAccessControl(AccessControlSections.Access));
        to.CreationTimeUtc = from.CreationTimeUtc;
        to.LastWriteTimeUtc = from.LastWriteTimeUtc;
        to.LastAccessTimeUtc = from.LastAccessTimeUtc;
        to.Attributes = from.Attributes & ~FileAttributes.ReparsePoint;
    }

    public static Task<TrashResult> TrashAsync(string path, CancellationToken cancellationToken) => OnStaAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = ShellItem(path);
        var operation = CreateOperation();
        var sink = new ProgressSink(cancellationToken, requireRecycle: true);
        uint cookie = 0;
        try
        {
            Check(operation.SetOperationFlags(FileOperationFlags));
            Check(operation.Advise(sink, out cookie));
            Check(operation.DeleteItem(item, null));
            Check(operation.PerformOperations());
            Check(operation.GetAnyOperationsAborted(out var aborted));
            sink.ThrowIfFailed(aborted);
            if (!sink.Completed) throw new IOException("Windows did not confirm moving the item to the Recycle Bin.");
            return new TrashResult(sink.ResultPath);
        }
        finally
        {
            if (cookie != 0) _ = operation.Unadvise(cookie);
            Release(operation);
            Release(item);
        }
    });

    public static Task RestoreTrashAsync(string trashedPath, string destination, CancellationToken cancellationToken) => OnStaAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        // IFileOperation MoveItem leaves $I records behind on Windows 10 even for shell-enumerated items.
        // Hold the exact paired metadata handle, use an exclusive native rename, then retire only that handle.
        // A changed pathname cannot redirect metadata deletion to another Recycle Bin entry.
        using var record = RecycleRecord.Open(trashedPath);
        var attributes = File.GetAttributes(trashedPath);
        if (!TryMoveEntry(trashedPath, destination, cancellationToken))
            throw new IOException("Restore must remain on the original volume. The recycled item was left in place.");
        try { record.Retire(); }
        catch (IOException exception) { throw new TrashRestoreMetadataException(trashedPath, destination, exception); }
        ChangeNotify(attributes.HasFlag(FileAttributes.Directory) ? 0x00020000u : 0x00000001u, 0x0005, trashedPath, destination);
        return true;
    });

    public static Task RevealAsync(string path, CancellationToken cancellationToken) => OnStaAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        Check(ParseDisplayName(path, 0, out var idList, 0, out _));
        try { Check(OpenFolderAndSelectItems(idList, 0, 0, 0)); }
        finally { Marshal.FreeCoTaskMem(idList); }
        return true;
    });

    private static IFileOperation CreateOperation() =>
        (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;

    private static IShellItem ShellItem(string path)
    {
        var id = ShellItemId;
        Check(CreateItemFromParsingName(path, 0, ref id, out var item));
        return item;
    }

    private static string? GetPath(IShellItem? item)
    {
        if (item == null || item.GetDisplayName(FileSystemPath, out var path) < 0) return null;
        try { return Marshal.PtrToStringUni(path); }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    private static Task<T> OnStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (OperationCanceledException canceled) { completion.TrySetCanceled(canceled.CancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "OpusMac shell operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    private static void Release(object value) { if (Marshal.IsComObject(value)) _ = Marshal.FinalReleaseComObject(value); }
    internal static string NativePath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private sealed class RecycleRecord : IDisposable
    {
        private readonly FileStream _record;
        private RecycleRecord(FileStream record) => _record = record;
        public static RecycleRecord Open(string recycledPath)
        {
            var directory = Path.GetDirectoryName(recycledPath)!;
            using var identity = WindowsIdentity.GetCurrent();
            var expectedSid = identity.User?.Value ?? throw new IOException("The current Windows user identity is unavailable.");
            var payloadName = Path.GetFileName(recycledPath);
            if (!string.Equals(Path.GetFileName(directory), expectedSid, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                !payloadName.StartsWith("$R", StringComparison.Ordinal) || payloadName.Length < 3)
                throw new IOException("The selected item is not in this user's Windows Recycle Bin.");
            var metadataPath = Path.Combine(directory, "$I" + payloadName[2..]);
            // READ | DELETE, share READ | DELETE; deny writes and open the record itself rather than a reparse target.
            var handle = CreateFile(NativePath(metadataPath), 0x80010000, 0x00000005, 0, 3, 0x00200000, 0);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The exact Recycle Bin metadata record cannot be opened.", new Win32Exception(Marshal.GetLastPInvokeError())); }
            FileStream? stream = null;
            try
            {
                if (!GetFileInformation(handle, out var information)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                if ((information.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("The Recycle Bin metadata record is not a regular file.");
                stream = new FileStream(handle, FileAccess.Read);
                if (stream.Length is < 28 or > 65564) throw new IOException("The Recycle Bin metadata record has an unsupported size.");
                var bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                var version = BitConverter.ToUInt64(bytes);
                var offset = version == 1 ? 24 : version == 2 ? 28 : throw new IOException("This Windows Recycle Bin metadata version is not supported.");
                var characters = version == 1 ? (bytes.Length - offset) / 2 : checked((int)BitConverter.ToUInt32(bytes, 24));
                if (characters < 1 || characters > (bytes.Length - offset) / 2)
                    throw new IOException("The selected Recycle Bin metadata path is malformed.");
                var original = Encoding.Unicode.GetString(bytes, offset, characters * 2).TrimEnd('\0');
                if (!Path.IsPathFullyQualified(original) || original.Contains('\0'))
                    throw new IOException("The selected Recycle Bin record has no valid original location.");
                return new RecycleRecord(stream);
            }
            catch
            {
                if (stream != null) stream.Dispose(); else handle.Dispose();
                throw;
            }
        }
        public void Retire()
        {
            var disposition = new FileDisposition { DeleteFile = true };
            if (!SetFileInformation(_record.SafeFileHandle, 4, ref disposition, Marshal.SizeOf<FileDisposition>()))
                throw new IOException("The item was restored, but Windows could not remove its exact Recycle Bin record.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        public void Dispose() => _record.Dispose();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ProgressSink(CancellationToken cancellationToken, bool requireRecycle) : IFileOperationProgressSink
    {
        private const int Abort = unchecked((int)0x80004004);
        private int _failure;
        public bool Completed { get; private set; }
        public string? ResultPath { get; private set; }
        private int CancelStatus => cancellationToken.IsCancellationRequested ? Abort : 0;
        public int StartOperations() => CancelStatus;
        public int FinishOperations(int result) { if (result < 0) _failure = result; return 0; }
        public int PreRenameItem(uint flags, IShellItem item, string newName) => CancelStatus;
        public int PostRenameItem(uint flags, IShellItem item, string newName, int result, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string newName) => CancelStatus;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string newName, int result, IShellItem? created)
        { _failure = result; Completed = result >= 0; ResultPath = GetPath(created); return 0; }
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string newName) => CancelStatus;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string newName, int result, IShellItem? created) => 0;
        public int PreDeleteItem(uint flags, IShellItem item) => requireRecycle && (flags & 0x80) == 0 ? Abort : CancelStatus;
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
        { _failure = result; Completed = result >= 0; ResultPath = GetPath(created); return 0; }
        public int PreNewItem(uint flags, IShellItem destination, string newName) => CancelStatus;
        public int PostNewItem(uint flags, IShellItem destination, string newName, string? template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint completed) => CancelStatus;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
        public void ThrowIfFailed(bool aborted)
        {
            // A commit receipt wins over a late cancellation; otherwise undo history would lose the completed mutation.
            if (Completed) return;
            cancellationToken.ThrowIfCancellationRequested();
            Check(_failure);
            if (aborted) throw new IOException("Windows canceled the file operation; the item was not recycled or restored.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CopyProgress(long total, long transferred, long streamSize, long streamTransferred,
        uint stream, uint reason, nint source, nint destination, nint data);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileEx(string source, string destination, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CopyFileEx(string source, string destination,
        CopyProgress callback, nint data, [MarshalAs(UnmanagedType.Bool)] ref bool cancel, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", CharSet = CharSet.Unicode)]
    private static extern int CreateItemFromParsingName(string path, nint bindContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify", CharSet = CharSet.Unicode)]
    private static extern void ChangeNotify(uint eventId, uint flags, string item1, string item2);
    [DllImport("shell32.dll", EntryPoint = "SHParseDisplayName", CharSet = CharSet.Unicode)]
    private static extern int ParseDisplayName(string path, nint bindContext, out nint idList, uint attributesIn, out uint attributesOut);
    [DllImport("shell32.dll", EntryPoint = "SHOpenFolderAndSelectItems")]
    private static extern int OpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, ref Guid handler, ref Guid interfaceId, out nint result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint name, out nint path);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition
    {
        [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
        public System.Runtime.InteropServices.ComTypes.FILETIME Access;
        public System.Runtime.InteropServices.ComTypes.FILETIME Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformation(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out FileAttributeTagInformation information, int size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformation(SafeFileHandle handle, int informationClass, ref FileDisposition information, int size);

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(nint dialog);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int SetOwnerWindow(nint owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(nint items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        [PreserveSig] int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        [PreserveSig] int MoveItems(nint items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        [PreserveSig] int CopyItems(nint items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        [PreserveSig] int DeleteItems(nint items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
