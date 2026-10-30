using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace NaxEventViewer.Core.Tests;

/// <summary>Reading a saved copy of this PC's Application log through both passes and the detail lookup.</summary>
internal static class ReaderTests
{
    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("reading_saved_log_returns_every_event_newest_first",
            "Reading a saved log returns as many events as Windows counts in the file, each with a source and a time, newest first.",
            () =>
            {
                string file = Fixtures.ApplicationLogFile;
                List<EventItem> items = ReadAll(file, out ReadSummary summary);
                long expected = ChannelCatalog.CountRecords(file, isFile: true) ?? -1;
                Check.Equal(expected, (long)items.Count);
                Check.That(summary.Problems.Count == 0, "no problems: " + string.Join(" ", summary.Problems));
                Check.That(items.All(i => i.Provider.Length > 0 && i.TimeCreatedUtc.Year > 2000), "every event has a source and a time");
                for (int i = 1; i < items.Count; i++)
                    Check.That(items[i - 1].RecordId > items[i].RecordId, "events must come newest first");
            });

        yield return ("second_pass_formats_message_for_every_event",
            "The message pass delivers a non-empty message for every event that the first pass read, exactly once each.",
            () =>
            {
                LogSource source = LogSource.ForFile(Fixtures.ApplicationLogFile);
                List<EventItem> items = ReadAll(Fixtures.ApplicationLogFile, out _);
                var seen = new HashSet<EventItem>();
                EventReader.FormatMessages(source, items, new MessageStore(), batch =>
                {
                    foreach (FormattedMessage message in batch)
                    {
                        Check.That(seen.Add(message.Item), "an event got two messages");
                        Check.That(!string.IsNullOrWhiteSpace(message.Message.ToString()), $"event {message.Item.RecordId} has an empty message");
                    }
                }, CancellationToken.None);
                Check.Equal(items.Count, seen.Count);
            });

        yield return ("selected_event_is_found_again_by_record_number",
            "Selecting an event in the middle of a saved log finds that same event again and shows its XML with the right record number.",
            () =>
            {
                List<EventItem> items = ReadAll(Fixtures.ApplicationLogFile, out _);
                Check.That(items.Count > 0, "the Application log is empty on this PC");
                EventItem middle = items[items.Count / 2];
                EventDetail detail = EventReader.ReadDetail(LogSource.ForFile(Fixtures.ApplicationLogFile), middle);
                Check.That(detail.Error is null, "the event must be found: " + detail.Error);
                Check.That(detail.Xml is not null && detail.Xml.Contains($"<EventRecordID>{middle.RecordId}</EventRecordID>"), "the XML belongs to the selected event");
                Check.That(!string.IsNullOrWhiteSpace(detail.Message), "the detail has a message");
            });

        yield return ("live_log_event_is_found_again_by_record_number",
            "Selecting the newest event of the live System log finds that same event again through its bookmark.",
            () =>
            {
                EventItem? newest = null;
                var stop = new CancellationTokenSource();
                EventReader.ReadRecords(LogSource.ForChannel("System"), batch =>
                {
                    newest ??= batch[0];
                    stop.Cancel();
                }, stop.Token);
                Check.That(newest is not null, "the System log returned no events");
                EventDetail detail = EventReader.ReadDetail(LogSource.ForChannel("System"), newest!);
                Check.That(detail.Xml is not null && detail.Xml.Contains($"<EventRecordID>{newest!.RecordId}</EventRecordID>"), "the XML belongs to the newest event: " + detail.Error);
            });

        yield return ("reading_a_saved_log_leaves_the_file_unchanged",
            "Reading a saved log through both passes and the detail lookup leaves the file byte for byte unchanged.",
            () =>
            {
                string file = Fixtures.ApplicationLogFile;
                byte[] before = SHA256.HashData(File.ReadAllBytes(file));
                LogSource source = LogSource.ForFile(file);
                List<EventItem> items = ReadAll(file, out _);
                EventReader.FormatMessages(source, items, new MessageStore(), _ => { }, CancellationToken.None);
                if (items.Count > 0) EventReader.ReadDetail(source, items[0]);
                Check.That(before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "the file changed");
            });

        yield return ("overview_names_logs_the_user_cannot_read",
            "Without administrator rights, the problems overview over System and Security names Security as left out instead of silently skipping it; with administrator rights nothing is left out.",
            () =>
            {
                bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                ReadSummary summary = EventReader.ReadRecords(LogSource.ForProblemsOverview(["System", "Security"], TimeSpan.FromHours(1)), _ => { }, CancellationToken.None);
                if (elevated)
                    Check.That(summary.LeftOut.Count == 0, "an administrator can read every log");
                else
                    Check.That(summary.LeftOut.SequenceEqual(["Security"]) && summary.AccessDenied, "Security must be named as left out, found: " + string.Join(", ", summary.LeftOut));
            });

        yield return ("live_subscription_delivers_new_event_with_message",
            "While the PowerShell log is watched, starting Windows PowerShell makes Windows write a start-up event, and it arrives within 30 seconds with its process ID and a formatted message.",
            () =>
            {
                const string channel = "Microsoft-Windows-PowerShell/Operational";
                using var arrived = new BlockingCollection<EventItem>();
                using LiveSubscription live = LiveSubscription.Start(channel, null, new MessageStore(), arrived.Add, _ => { });
                using var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command exit") { UseShellExecute = false, CreateNoWindow = true })!;
                uint pid = (uint)process.Id;
                process.WaitForExit(30_000);

                var deadline = Stopwatch.StartNew();
                EventItem? match = null;
                while (match is null && deadline.Elapsed < TimeSpan.FromSeconds(30))
                {
                    if (arrived.TryTake(out EventItem? item, TimeSpan.FromSeconds(1)) && item.ProcessId == pid) match = item;
                }
                Check.That(match is not null, $"no event from PowerShell process {pid} arrived; is {channel} enabled?");
                Check.That(match!.Channel == channel && !string.IsNullOrWhiteSpace(match.Message), "the event has its log and a message");
            });

        yield return ("live_subscription_reports_when_windows_ends_it",
            "When Windows sends the error notice that ends a subscription, for example after the log was cleared, the app is told that live updates stopped and why.",
            () =>
            {
                string? reported = null;
                using LiveSubscription live = LiveSubscription.Start("System", null, new MessageStore(), _ => { }, message => reported = message);
                const int queryResultStale = 15011;
                LiveSubscription.Dispatch(0, live.Id, queryResultStale);
                Check.That(reported is not null && reported.StartsWith("Live updates for System stopped."), "the stop is reported: " + reported);
            });

        yield return ("missing_file_reports_a_problem_instead_of_failing",
            "Opening a .evtx file that does not exist gives no events and a readable problem description instead of an exception.",
            () =>
            {
                string missing = Path.Combine(Fixtures.TempFolder("missing"), "none.evtx");
                ReadSummary summary = EventReader.ReadRecords(LogSource.ForFile(missing), _ => { }, CancellationToken.None);
                Check.Equal(0, summary.Count);
                Check.That(summary.Problems.Count == 1, "one problem is reported");
            });
    }

    private static List<EventItem> ReadAll(string file, out ReadSummary summary)
    {
        var items = new List<EventItem>();
        summary = EventReader.ReadRecords(LogSource.ForFile(file), items.AddRange, CancellationToken.None);
        return items;
    }
}
