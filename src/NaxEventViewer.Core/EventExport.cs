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
        if (!source.SupportsEvtxExport) throw new InvalidOperationException(CoreText.Get("Error_EvtxSingleOnly"));
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
                throw EventLogException.FromLastError(CoreText.F("Error_Save", source.DisplayName));
            AtomicFile.Write(targetPath, output =>
            {
                using var input = new FileStream(localCopy, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                input.CopyTo(output, 1 << 20);
            });
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

    /// <summary>
    /// Writes the events as comma-separated values with a header row, readable by Excel, and returns how many events
    /// were written. Fails without creating a file when there are no events, so an unreadable or empty list is never
    /// reported as saved.
    /// </summary>
    public static int SaveCsv(string targetPath, IEnumerable<EventItem> items)
    {
        int written = 0;
        AtomicFile.Write(targetPath, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true);
            writer.Write(CoreText.Get("Csv_Header") + "\r\n");
            foreach (EventItem item in items)
            {
                WriteRow(writer,
                    item.LevelText,
                    item.TimeCreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    item.TimeCreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    item.Provider,
                    item.EventIdText,
                    item.TaskName,
                    item.Channel,
                    item.Computer,
                    item.RecordId.ToString(CultureInfo.InvariantCulture),
                    item.Message ?? "");
                written++;
            }
            if (written == 0) throw new NothingToSaveException(CoreText.Get("Error_NothingToSave"));
        });
        return written;
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
    /// Quotes a cell when needed and makes sure no part of it can run as a spreadsheet formula, because event text
    /// comes from programs. A cell that starts like a formula gets a leading apostrophe. So does every part after a
    /// semicolon, tab or line break that starts like one: Excel set to the semicolon list separator, as in most
    /// European languages, ignores the quotes of a comma-separated file and splits cells at those characters.
    /// </summary>
    internal static string CsvCell(string value)
    {
        if (value.Length > 0 && (StartsFormula(value[0]) || value[0] is '\t' or '\r')) value = "'" + value;
        if (value.AsSpan().IndexOfAny(";\t\r\n") >= 0) value = DefuseAfterSeparators(value);
        bool needsQuotes = value.AsSpan().IndexOfAny(",\";\t\r\n") >= 0;
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static bool StartsFormula(char c) => c is '=' or '+' or '-' or '@';

    private static string DefuseAfterSeparators(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            result.Append(value[i]);
            if (value[i] is ';' or '\t' or '\r' or '\n' && i + 1 < value.Length && StartsFormula(value[i + 1])) result.Append('\'');
        }
        return result.ToString();
    }
}

/// <summary>
/// Writes a file through a hidden temporary file in the same folder and replaces the target only when writing
/// succeeded. A temporary file left behind by a crash or a killed process is removed by the next save to that folder.
/// </summary>
internal static class AtomicFile
{
    /// <summary>A temporary file younger than this may belong to a save that is still running in another window.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    // The temporary name is short and fixed apart from a GUID, so a long target name still leaves room for it.
    internal const string TemporaryPrefix = ".nax-";
    private const string TemporaryMarker = ".partial";

    /// <summary>Calls <paramref name="write"/> with a stream on a new hidden temporary file, then puts that file in place of the target.</summary>
    public static void Write(string targetPath, Action<Stream> write)
    {
        string fullPath = Path.GetFullPath(targetPath);
        string folder = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("The path has no folder.", nameof(targetPath));
        RemoveStaleTemporaryFiles(folder);
        string temporaryPath = Path.Combine(folder, TemporaryPrefix + Guid.NewGuid().ToString("N") + TemporaryMarker + Path.GetExtension(fullPath));
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                File.SetAttributes(temporaryPath, FileAttributes.Hidden);
                write(stream);
            }
            File.SetAttributes(temporaryPath, File.GetAttributes(temporaryPath) & ~FileAttributes.Hidden);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    /// <summary>Deletes earlier temporary files in this folder that are older than <see cref="StaleAfter"/>.</summary>
    private static void RemoveStaleTemporaryFiles(string folder)
    {
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(folder, TemporaryPrefix + "*" + TemporaryMarker + "*"))
            {
                // Only names this class created: the prefix, 32 hexadecimal digits, the marker and the target's extension.
                string name = Path.GetFileName(candidate);
                if (name.Length < TemporaryPrefix.Length + 32 + TemporaryMarker.Length) continue;
                string middle = name.Substring(TemporaryPrefix.Length, 32);
                if (!middle.All(char.IsAsciiHexDigit) || string.CompareOrdinal(name, TemporaryPrefix.Length + 32, TemporaryMarker, 0, TemporaryMarker.Length) != 0) continue;
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
