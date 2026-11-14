namespace OpusMac.Core.Platform;

/// <summary>The selected payload was restored; only its paired Recycle Bin metadata cleanup failed.</summary>
public sealed class TrashRestoreMetadataException(string trashedPath, string restoredPath, Exception innerException)
    : IOException("The item was restored, but Windows could not remove its Recycle Bin record. Do not repeat the restore.", innerException)
{
    public string TrashedPath { get; } = trashedPath;
    public string RestoredPath { get; } = restoredPath;
    public bool RestoreCommitted => true;
}
