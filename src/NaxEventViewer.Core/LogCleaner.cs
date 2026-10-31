using NaxEventViewer.Core.Native;

namespace NaxEventViewer.Core;

/// <summary>
/// Clears a log. This is the only code that deletes events, and the app calls it only after the user chose to clear a
/// log and confirmed it. Windows only lets administrators clear most logs.
/// </summary>
public static class LogCleaner
{
    internal const string LocalBackupPrefix = "nax-eventviewer-backup-";

    /// <summary>
    /// Deletes every event of <paramref name="channel"/>. With <paramref name="backupPath"/>, Windows first copies the
    /// events and empties the log in one step, so no event is lost in between, and the copy is then saved at that path
    /// the same safe way as any other save. Throws <see cref="EventLogException"/> when Windows refuses, in which case
    /// nothing was cleared, and <see cref="BackupNotSavedException"/> when the log was cleared but the copy could not be
    /// put at the chosen path; the copy is then kept in the temporary folder.
    /// </summary>
    public static void Clear(string channel, string? backupPath)
    {
        if (backupPath is null)
        {
            if (!Wevtapi.EvtClearLog(0, channel, null, 0)) throw EventLogException.FromLastError(CoreText.F("Error_Clear", channel));
            return;
        }

        // Windows writes the copy into the local temporary folder, which it can always write to, as for a .evtx save.
        string localCopy = Path.Combine(Path.GetTempPath(), LocalBackupPrefix + Guid.NewGuid().ToString("N") + ".evtx");
        if (!Wevtapi.EvtClearLog(0, channel, localCopy, 0))
        {
            var error = EventLogException.FromLastError(CoreText.F("Error_Clear", channel));
            AtomicFile.TryDelete(localCopy);
            throw error;
        }
        try
        {
            AtomicFile.Write(backupPath, output =>
            {
                using var input = new FileStream(localCopy, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                input.CopyTo(output, 1 << 20);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is already empty, so the local copy is now the only one and must stay.
            throw new BackupNotSavedException(localCopy, CoreText.F("Error_BackupKept", channel, backupPath, localCopy, ex.Message), ex);
        }
        AtomicFile.TryDelete(localCopy);
    }
}

/// <summary>The log was cleared, but its copy could not be saved at the chosen path. The copy is at <see cref="KeptAt"/>.</summary>
public sealed class BackupNotSavedException(string keptAt, string message, Exception inner) : IOException(message, inner)
{
    public string KeptAt { get; } = keptAt;
}
