namespace NaxEventViewer.Core.Tests;

/// <summary>Saving events as CSV and .evtx files.</summary>
internal static class ExportTests
{
    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("csv_cells_with_commas_quotes_and_newlines_are_quoted",
            "A message with a comma, a quote and a line break is written as one quoted cell with the quote doubled.",
            () =>
            {
                Check.Equal("plain", EventExport.CsvCell("plain"));
                Check.Equal("\"a, \"\"b\"\"\r\nc\"", EventExport.CsvCell("a, \"b\"\r\nc"));
            });

        yield return ("csv_cell_that_looks_like_formula_gets_apostrophe",
            "Event text that starts with =, +, - or @ is written with a leading apostrophe, so a spreadsheet never runs it as a formula.",
            () =>
            {
                Check.Equal("'=HYPERLINK(1)", EventExport.CsvCell("=HYPERLINK(1)"));
                Check.Equal("'-5", EventExport.CsvCell("-5"));
                Check.Equal("'@x", EventExport.CsvCell("@x"));
            });

        yield return ("csv_save_writes_header_and_one_row_per_event",
            "Saving two events as CSV gives a header row and two rows with the level, source, ID and message of each event.",
            () =>
            {
                string path = Path.Combine(Fixtures.TempFolder("csv"), "events.csv");
                var now = DateTime.UtcNow;
                EventExport.SaveCsv(path, [Fixtures.Item(now, level: 2, id: 7, provider: "Disk", message: "Bad block"), Fixtures.Item(now, level: 3, id: 8, provider: "Tcpip", message: "Slow")]);
                string[] lines = File.ReadAllLines(path);
                Check.Equal(3, lines.Length);
                Check.That(lines[0].StartsWith("Level,Date and time,Source,Event ID"), "the header comes first");
                Check.That(lines[1].StartsWith("Error,") && lines[1].Contains(",Disk,7,") && lines[1].EndsWith(",Bad block"), "the first row describes the first event");
            });

        yield return ("csv_save_fails_midway_original_file_unchanged",
            "When writing a CSV file fails halfway, the file that was already at that path keeps its old content and no temporary file is left behind.",
            () =>
            {
                string folder = Fixtures.TempFolder("csv-fail");
                string path = Path.Combine(folder, "events.csv");
                File.WriteAllText(path, "old content");
                try
                {
                    EventExport.SaveCsv(path, FailingItems());
                    throw new Exception("the save should have failed");
                }
                catch (InvalidOperationException)
                {
                }
                Check.Equal("old content", File.ReadAllText(path));
                Check.Equal(1, Directory.GetFiles(folder).Length);
            });

        yield return ("csv_temporary_file_is_hidden_while_written_and_final_file_is_not",
            "While a CSV file is being written, its temporary file in the target folder is hidden, and the finished file is a normal visible file.",
            () =>
            {
                string folder = Fixtures.TempFolder("csv-hidden");
                string path = Path.Combine(folder, "events.csv");
                bool? hiddenWhileWriting = null;
                IEnumerable<EventItem> Items()
                {
                    yield return Fixtures.Item(DateTime.UtcNow, message: "first");
                    string partial = Directory.GetFiles(folder, ".events.*.partial.csv").Single();
                    hiddenWhileWriting = File.GetAttributes(partial).HasFlag(FileAttributes.Hidden);
                }
                EventExport.SaveCsv(path, Items());
                Check.That(hiddenWhileWriting == true, "the temporary file must be hidden while it is written");
                Check.That(!File.GetAttributes(path).HasFlag(FileAttributes.Hidden), "the saved file must not be hidden");
                Check.Equal(1, Directory.GetFiles(folder).Length);
            });

        yield return ("stale_temporary_file_from_crashed_save_is_removed_by_next_save",
            "A temporary file that an earlier crashed save left behind is deleted by the next save to the same path, while a recent one and files with other names stay.",
            () =>
            {
                string folder = Fixtures.TempFolder("csv-stale");
                string path = Path.Combine(folder, "events.csv");
                string stale = Path.Combine(folder, ".events." + new string('a', 32) + ".partial.csv");
                string recent = Path.Combine(folder, ".events." + new string('b', 32) + ".partial.csv");
                string unrelated = Path.Combine(folder, ".events.notours.partial.csv");
                foreach (string file in new[] { stale, recent, unrelated }) File.WriteAllText(file, "x");
                File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - AtomicFile.StaleAfter - TimeSpan.FromMinutes(1));
                File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-1));

                EventExport.SaveCsv(path, [Fixtures.Item(DateTime.UtcNow, message: "x")]);
                Check.That(!File.Exists(stale), "the stale temporary file must be removed");
                Check.That(File.Exists(recent), "a recent temporary file may belong to a running save and must stay");
                Check.That(File.Exists(unrelated), "a file with another name pattern must stay");
            });

        yield return ("evtx_save_of_missing_file_fails_and_keeps_target",
            "Saving a .evtx file from a source that does not exist fails, keeps the existing target file unchanged and leaves no temporary file, neither next to the target nor in the temporary folder.",
            () =>
            {
                int localCopiesBefore = Directory.GetFiles(Path.GetTempPath(), "nax-eventviewer-export-*.evtx").Length;
                string folder = Fixtures.TempFolder("evtx-fail");
                string target = Path.Combine(folder, "copy.evtx");
                File.WriteAllText(target, "old content");
                try
                {
                    EventExport.SaveEvtx(LogSource.ForFile(Path.Combine(folder, "missing.evtx")), target);
                    throw new Exception("the save should have failed");
                }
                catch (EventLogException)
                {
                }
                Check.Equal("old content", File.ReadAllText(target));
                Check.Equal(1, Directory.GetFiles(folder).Length);
                Check.Equal(localCopiesBefore, Directory.GetFiles(Path.GetTempPath(), "nax-eventviewer-export-*.evtx").Length);
            });

        yield return ("evtx_save_removes_local_copy_left_by_a_crashed_save",
            "A full log copy that an earlier crashed .evtx save left in the temporary folder is deleted by the next save, while a recent copy and files with other names stay.",
            () =>
            {
                string temp = Path.GetTempPath();
                string stale = Path.Combine(temp, EventExport.LocalCopyPrefix + new string('c', 32) + ".evtx");
                string recent = Path.Combine(temp, EventExport.LocalCopyPrefix + new string('d', 32) + ".evtx");
                string other = Path.Combine(temp, EventExport.LocalCopyPrefix + "keep-me.evtx");
                try
                {
                    foreach (string file in new[] { stale, recent, other }) File.WriteAllText(file, "x");
                    File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - AtomicFile.StaleAfter - TimeSpan.FromMinutes(1));
                    File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-1));

                    EventExport.SaveEvtx(LogSource.ForFile(Fixtures.ApplicationLogFile), Path.Combine(Fixtures.TempFolder("evtx-stale"), "copy.evtx"));
                    Check.That(!File.Exists(stale), "the stale copy must be removed");
                    Check.That(File.Exists(recent), "a recent copy may belong to a running save and must stay");
                    Check.That(File.Exists(other), "a file with another name must stay");
                }
                finally
                {
                    foreach (string file in new[] { stale, recent, other }) AtomicFile.TryDelete(file);
                }
            });

        yield return ("evtx_save_of_application_log_copies_every_event",
            "Saving this PC's Application log as .evtx gives a file with at least as many events as the log had just before.",
            () =>
            {
                long before = ChannelCatalog.CountRecords("Application") ?? throw new Exception("the Application log could not be opened");
                long saved = ChannelCatalog.CountRecords(Fixtures.ApplicationLogFile, isFile: true) ?? throw new Exception("the saved file could not be opened");
                Check.That(saved >= before && saved > 0, $"the file holds {saved} events, the log held {before}");
            });
    }

    private static IEnumerable<EventItem> FailingItems()
    {
        yield return Fixtures.Item(DateTime.UtcNow, message: "first");
        throw new InvalidOperationException("simulated failure while writing");
    }
}
