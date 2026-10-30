using System.Globalization;
using System.Text;
using NaxEventViewer.Core.Native;

namespace NaxEventViewer.Core;

/// <summary>Saves events to files. Every save writes a temporary file first, so a failed save never leaves a half-written file.</summary>
public static class EventExport
{
    /// <summary>Copies every event of a log or .evtx file into a new .evtx file. The source is only read.</summary>
    public static void SaveEvtx(LogSource source, string targetPath)
    {
        if (!source.SupportsEvtxExport) throw new InvalidOperationException("Only a single log or file can be saved as .evtx.");
        // No EvtExportLogTolerateQueryErrors: with it, a source that cannot be read gives an empty file instead of an error.
        int flags = source.Kind == LogSourceKind.File ? Wevtapi.EvtExportLogFilePath : Wevtapi.EvtExportLogChannelPath;

        // The export goes to the user's local temporary folder first, which Windows can always write to, and is then
        // copied to the chosen place. That keeps network shares and folders with unusual permissions working.
        // A copy left there by an earlier save that crashed holds a whole log, so it is removed first.
        RemoveStaleLocalCopies();
        string localCopy = Path.Combine(Path.GetTempPath(), LocalCopyPrefix + Guid.NewGuid().ToString("N") + ".evtx");
        try
        {
            if (!Wevtapi.EvtExportLog(0, source.Path, "*", localCopy, flags))
                throw EventLogException.FromLastError($"Windows could not save {source.DisplayName}.");
            AtomicFile.Write(targetPath, temporaryPath => File.Copy(localCopy, temporaryPath));
        }
        finally
        {
            AtomicFile.TryDelete(localCopy);
        }
    }

    internal const string LocalCopyPrefix = "nax-eventviewer-export-";

    /// <summary>Deletes local export copies older than <see cref="AtomicFile.StaleAfter"/> whose names this class created.</summary>
    private static void RemoveStaleLocalCopies()
    {
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(Path.GetTempPath(), LocalCopyPrefix + "*.evtx"))
            {
                string middle = Path.GetFileNameWithoutExtension(candidate)[LocalCopyPrefix.Length..];
                if (middle.Length != 32 || !middle.All(char.IsAsciiHexDigit)) continue;
                // A copy that is still being copied somewhere is open, so deleting it fails and it stays.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(candidate) > AtomicFile.StaleAfter) AtomicFile.TryDelete(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes the events as comma-separated values with a header row, readable by Excel.</summary>
    public static void SaveCsv(string targetPath, IEnumerable<EventItem> items)
    {
        AtomicFile.Write(targetPath, temporaryPath =>
        {
            using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write);
            File.SetAttributes(temporaryPath, FileAttributes.Hidden);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.Write("Level,Date and time,Source,Event ID,Task category,Log,Computer,Record ID,Message\r\n");
            foreach (EventItem item in items)
            {
                WriteRow(writer,
                    item.LevelText,
                    item.TimeCreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    item.Provider,
                    item.EventIdText,
                    item.TaskName,
                    item.Channel,
                    item.Computer,
                    item.RecordId.ToString(CultureInfo.InvariantCulture),
                    item.Message ?? "");
            }
        });
    }

    private static void WriteRow(TextWriter writer, params ReadOnlySpan<string> cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) writer.Write(',');
            writer.Write(CsvCell(cells[i]));
        }
        writer.Write("\r\n");
    }

    /// <summary>
    /// Quotes a cell when needed. A cell that starts like a spreadsheet formula gets a leading apostrophe, because
    /// event text comes from programs and must never run as a formula when the file is opened.
    /// </summary>
    internal static string CsvCell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        bool needsQuotes = value.AsSpan().IndexOfAny(",\"\r\n") >= 0;
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

/// <summary>
/// Writes a file through a temporary file in the same folder and replaces the target only when writing succeeded.
/// A temporary file left behind by a crash or a killed process is removed by the next save to the same target.
/// </summary>
internal static class AtomicFile
{
    /// <summary>A temporary file younger than this may belong to a save that is still running in another window.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public static void Write(string targetPath, Action<string> writeTemporaryFile)
    {
        string fullPath = Path.GetFullPath(targetPath);
        string folder = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("The path has no folder.", nameof(targetPath));
        RemoveStaleTemporaryFiles(fullPath);
        string temporaryPath = Path.Combine(folder, TemporaryPrefix(fullPath) + Guid.NewGuid().ToString("N") + TemporarySuffix(fullPath));
        try
        {
            writeTemporaryFile(temporaryPath);
            File.SetAttributes(temporaryPath, File.GetAttributes(temporaryPath) & ~FileAttributes.Hidden);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static string TemporaryPrefix(string fullPath) => "." + Path.GetFileNameWithoutExtension(fullPath) + ".";

    private static string TemporarySuffix(string fullPath) => ".partial" + Path.GetExtension(fullPath);

    /// <summary>Deletes earlier temporary files of this exact target that are older than <see cref="StaleAfter"/>.</summary>
    private static void RemoveStaleTemporaryFiles(string fullPath)
    {
        string folder = Path.GetDirectoryName(fullPath)!;
        string prefix = TemporaryPrefix(fullPath);
        string suffix = TemporarySuffix(fullPath);
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(folder, prefix + "*" + suffix))
            {
                string name = Path.GetFileName(candidate);
                string middle = name[prefix.Length..^suffix.Length];
                // Only names this class created: the prefix, 32 hexadecimal digits, the suffix.
                if (middle.Length != 32 || !middle.All(char.IsAsciiHexDigit)) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(candidate) > StaleAfter) TryDelete(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleaning up is a courtesy; the save itself must not fail because of it.
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
