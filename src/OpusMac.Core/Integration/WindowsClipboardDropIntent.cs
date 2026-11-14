using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace OpusMac.Core.Integration;

public enum WindowsClipboardTransferDecision { Refuse, Copy, Move }

/// <summary>
/// A non-mutating Windows CF_HDROP/Preferred DropEffect snapshot. An external
/// Move is advisory until the UI separately revalidates source identities and
/// destination state at the operation boundary.
/// </summary>
public sealed class WindowsClipboardDropSnapshot
{
    private readonly string[] _paths;

    public uint Sequence { get; }
    public IReadOnlyList<string> Paths { get; }
    public uint PreferredEffect { get; }

    public WindowsClipboardDropSnapshot(uint sequence, IEnumerable<string> paths, uint preferredEffect)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Sequence = sequence;
        _paths = paths.ToArray();
        Paths = Array.AsReadOnly(_paths);
        PreferredEffect = preferredEffect;
    }

    /// <summary>
    /// An external file transfer must retain its exact clipboard intent. Any
    /// changed sequence, partial path list, or ambiguous selection is refused;
    /// a stale Move must never silently turn into a Copy.
    /// </summary>
    public WindowsClipboardTransferDecision DecideFor(IReadOnlyList<string> currentPaths, uint currentSequence)
    {
        if (PreferredEffect is not (WindowsClipboardDropIntent.CopyEffect or WindowsClipboardDropIntent.MoveEffect))
            return WindowsClipboardTransferDecision.Refuse;
        if (!MatchesSelection(currentPaths, currentSequence))
            return WindowsClipboardTransferDecision.Refuse;
        return PreferredEffect == WindowsClipboardDropIntent.MoveEffect
            ? WindowsClipboardTransferDecision.Move
            : WindowsClipboardTransferDecision.Copy;
    }

    public bool IsMoveFor(IReadOnlyList<string> currentPaths, uint currentSequence)
        => DecideFor(currentPaths, currentSequence) == WindowsClipboardTransferDecision.Move;

    public WindowsClipboardTransferDecision DecideIfCurrent(WindowsClipboardDropSnapshot? current)
    {
        if (current is null || current.Sequence != Sequence || current.PreferredEffect != PreferredEffect)
            return WindowsClipboardTransferDecision.Refuse;
        if (!MatchesSelection(current.Paths, current.Sequence))
            return WindowsClipboardTransferDecision.Refuse;
        return PreferredEffect switch
        {
            WindowsClipboardDropIntent.CopyEffect => WindowsClipboardTransferDecision.Copy,
            WindowsClipboardDropIntent.MoveEffect => WindowsClipboardTransferDecision.Move,
            _ => WindowsClipboardTransferDecision.Refuse
        };
    }

    private bool MatchesSelection(IReadOnlyList<string> currentPaths, uint currentSequence)
    {
        if (currentPaths is null || Sequence == 0 || currentSequence != Sequence ||
            _paths.Length == 0 || _paths.Length != currentPaths.Count)
            return false;
        // Case-only changes can identify a different file in a Windows
        // directory with per-directory case sensitivity enabled. Clipboard
        // path spelling is stable across captures, so require exact spelling.
        var comparer = StringComparer.Ordinal;
        var normalized = new string[_paths.Length];
        for (var i = 0; i < _paths.Length; i++)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_paths[i]) || string.IsNullOrWhiteSpace(currentPaths[i]) ||
                    !Path.IsPathFullyQualified(_paths[i]) || !Path.IsPathFullyQualified(currentPaths[i]) ||
                    !comparer.Equals(Path.GetFullPath(_paths[i]), Path.GetFullPath(currentPaths[i])))
                    return false;
                normalized[i] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths[i]));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
        // Keep exact spelling for capture/commit comparisons above. For
        // duplicates and parent/descendant overlap, fail closed even across
        // case variants: most Windows directories are case-insensitive, and
        // a selected folder must not be operated on twice via different names.
        var overlapComparer = StringComparer.OrdinalIgnoreCase;
        var selected = normalized.ToHashSet(overlapComparer);
        if (selected.Count != normalized.Length) return false;
        foreach (var path in normalized)
        {
            var parent = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(parent))
            {
                if (selected.Contains(parent)) return false;
                var next = Path.GetDirectoryName(parent);
                if (overlapComparer.Equals(next, parent)) break;
                parent = next;
            }
        }
        return true;
    }
}

/// <summary>
/// Reads Explorer's native file-drop intent without changing clipboard content
/// or opening Listary's index. Missing, changing or ambiguous formats fail closed.
/// Only a single explicit DWORD MOVE (2) can authorize later Move handling.
/// </summary>
public static class WindowsClipboardDropIntent
{
    public const uint CopyEffect = 1;
    public const uint MoveEffect = 2;
    // Windows Explorer's actual Copy clipboard may advertise COPY | LINK.
    // Treat that option set as Copy only; any MOVE combination is ambiguous.
    public const uint CopyOrLinkEffect = 5;
    private const uint CfHdrop = 15;
    private const uint MaximumPaths = 4096;
    private const uint MaximumPathCharacters = 32767;

    public static uint CurrentSequence =>
        OperatingSystem.IsWindows() ? GetClipboardSequenceNumber() : 0;

    public static bool TryParsePreferredEffect(ReadOnlySpan<byte> bytes, out uint effect)
    {
        effect = 0;
        // GlobalSize can report allocator padding beyond the DWORD payload.
        // Only the first four bytes are the registered format's value.
        if (bytes.Length < sizeof(uint)) return false;
        var candidate = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (candidate is not (CopyEffect or MoveEffect or CopyOrLinkEffect)) return false;
        effect = candidate == CopyOrLinkEffect ? CopyEffect : candidate;
        return true;
    }

    public static WindowsClipboardDropSnapshot? TryCapture()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var before = CurrentSequence;
        if (before == 0 || !OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var dropHandle = GetClipboardData(CfHdrop);
            var effectFormat = RegisterClipboardFormat("Preferred DropEffect");
            var effectHandle = effectFormat == 0 ? IntPtr.Zero : GetClipboardData(effectFormat);
            if (dropHandle == IntPtr.Zero || effectHandle == IntPtr.Zero ||
                GlobalSize(effectHandle) < (nuint)sizeof(uint))
                return null;
            var locked = GlobalLock(effectHandle);
            if (locked == IntPtr.Zero) return null;
            uint effect;
            try
            {
                var bytes = new byte[sizeof(uint)];
                Marshal.Copy(locked, bytes, 0, bytes.Length);
                if (!TryParsePreferredEffect(bytes, out effect)) return null;
            }
            finally { GlobalUnlock(effectHandle); }

            var count = DragQueryFile(dropHandle, uint.MaxValue, null, 0);
            if (count == 0 || count > MaximumPaths) return null;
            var paths = new string[count];
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(dropHandle, i, null, 0);
                if (length == 0 || length > MaximumPathCharacters) return null;
                var buffer = new StringBuilder(checked((int)length + 1));
                if (DragQueryFile(dropHandle, i, buffer, checked((uint)buffer.Capacity)) != length)
                    return null;
                paths[i] = buffer.ToString();
                if (string.IsNullOrWhiteSpace(paths[i])) return null;
            }
            return before == CurrentSequence
                ? new WindowsClipboardDropSnapshot(before, paths, effect)
                : null;
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint DragQueryFile(IntPtr dropHandle, uint index, StringBuilder? buffer, uint length);
}
