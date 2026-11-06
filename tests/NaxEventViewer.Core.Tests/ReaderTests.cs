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

        yield return ("second_pass_rejects_reused_record_numbers",
            "When a log is cleared or a saved file replaced between passes, a record with a different time or event ID never supplies the old event's message, while an unchanged event still gets its message.",
            () =>
            {
                LogSource source = LogSource.ForFile(Fixtures.ApplicationLogFile);
                List<EventItem> real = ReadAll(Fixtures.ApplicationLogFile, out _);
                Check.That(real.Count >= 3, "the fixture needs three events");
                EventItem otherTime = Copy(real[0], timeUtc: real[0].TimeCreatedUtc.AddSeconds(-1));
                EventItem otherId = Copy(real[1], eventId: (ushort)(real[1].EventId ^ 1));
                Check.Equal(real[0].Key, otherTime.Key);
                Check.Equal(real[1].Key, otherId.Key);
                var seen = new List<EventItem>();
                EventReader.FormatMessages(source, [otherTime, otherId, real[2]], new MessageStore(),
                    batch => seen.AddRange(batch.Select(message => message.Item)), CancellationToken.None);
                Check.That(seen.SequenceEqual([real[2]]), "only the event whose identity still matches may get a message");
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

        yield return ("detail_lookup_rejects_an_event_that_only_shares_the_record_number",
            "When the event found under the selected record number has another time, computer or ID, as after a log was cleared, a file replaced or for forwarded events from another PC, the detail pane reports that the event was not found instead of showing the other one.",
            () =>
            {
                LogSource source = LogSource.ForFile(Fixtures.ApplicationLogFile);
                EventItem real = ReadAll(Fixtures.ApplicationLogFile, out _)[0];
                EventItem otherTime = Copy(real, timeUtc: real.TimeCreatedUtc.AddSeconds(-1));
                EventItem otherComputer = Copy(real, computer: real.Computer + "-other");
                EventItem otherId = Copy(real, eventId: (ushort)(real.EventId + 1));
                foreach (EventItem impostor in new[] { otherTime, otherComputer, otherId })
                {
                    EventDetail detail = EventReader.ReadDetail(source, impostor);
                    Check.That(detail.Xml is null && detail.Error is not null, "a different event must not be shown");
                }
                Check.That(EventReader.ReadDetail(source, real).Xml is not null, "the real event is still found");
            });

        yield return ("overview_events_remember_the_log_they_were_read_from",
            "Events in the problems overview over System and Application know which of the two logs they were read from, which the detail pane needs for forwarded events.",
            () =>
            {
                var items = new List<EventItem>();
                EventReader.ReadRecords(LogSource.ForProblemsOverview(["System", "Application"], TimeSpan.FromDays(30)), items.AddRange, CancellationToken.None);
                Check.That(items.Count > 0, "System and Application had no warnings or errors in 30 days");
                EventItem? wrong = items.FirstOrDefault(i => !string.Equals(i.Log, i.Channel, StringComparison.OrdinalIgnoreCase)
                    || !(i.Log.Equals("System", StringComparison.OrdinalIgnoreCase) || i.Log.Equals("Application", StringComparison.OrdinalIgnoreCase)));
                Check.That(wrong is null, $"event {wrong?.RecordId} of {wrong?.Channel} says it was read from \"{wrong?.Log}\"");
                EventDetail detail = EventReader.ReadDetail(LogSource.ForProblemsOverview(["System", "Application"], TimeSpan.FromDays(30)), items[0]);
                Check.That(detail.Xml is not null, "the first overview event is found again: " + detail.Error);
            });

        yield return ("events_from_different_computers_with_the_same_record_number_count_as_different",
            "Two events that share log, channel and record number but come from different computers, as forwarded events can, count as different events.",
            () =>
            {
                EventItem a = new() { Log = "ForwardedEvents", Channel = "Security", Computer = "PC-A", Provider = "x", RecordId = 7 };
                EventItem b = new() { Log = "ForwardedEvents", Channel = "Security", Computer = "PC-B", Provider = "x", RecordId = 7 };
                EventItem c = new() { Log = "forwardedevents", Channel = "SECURITY", Computer = "pc-a", Provider = "x", RecordId = 7 };
                Check.That(a.Key != b.Key, "events of different computers must have different keys");
                Check.That(a.Key == c.Key && a.Key.GetHashCode() == c.Key.GetHashCode(), "names compare ignoring case");
            });

        yield return ("damaged_timestamp_becomes_the_earliest_date_instead_of_an_error",
            "A time value out of range, as a damaged or crafted .evtx file can hold, is read as the earliest date instead of stopping the read with an exception.",
            () =>
            {
                Check.Equal(DateTime.MinValue, Native.SystemRenderer.FileTimeToUtc(ulong.MaxValue));
                Check.Equal(DateTime.MinValue, Native.SystemRenderer.FileTimeToUtc((ulong)long.MaxValue));
                var time = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
                Check.Equal(time, Native.SystemRenderer.FileTimeToUtc((ulong)time.ToFileTimeUtc()));
            });

        yield return ("unexpected_error_while_reading_ends_the_load_with_a_problem",
            "When reading stops because of an unexpected error, the load still reports that the records are done with a problem, reports the error, and finishes the message pass, so the window never waits forever.",
            () =>
            {
                ReadSummary? done = null;
                Exception? reported = null;
                using var messagesDone = new ManualResetEventSlim();
                LogLoader.Start(LogSource.ForFile(Fixtures.ApplicationLogFile), new MessageStore(), new LoadCallbacks
                {
                    RecordsRead = _ => throw new InvalidOperationException("simulated failure"),
                    RecordsDone = summary => done = summary,
                    MessagesFormatted = _ => { },
                    MessagesDone = messagesDone.Set,
                    UnexpectedError = ex => reported = ex,
                }, CancellationToken.None);
                Check.That(messagesDone.Wait(TimeSpan.FromSeconds(60)), "the load must finish");
                Check.That(done is not null && done.Problems.Any(p => p.Contains("simulated failure")), "the records end with the problem");
                Check.That(reported is InvalidOperationException, "the error is reported for the crash log");
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

        yield return ("overview_where_no_log_can_be_opened_says_so",
            "A problems overview over logs that do not exist reports that no log could be read, while an overview that includes System does not.",
            () =>
            {
                ReadSummary none = EventReader.ReadRecords(LogSource.ForProblemsOverview(["Nax-Missing-Log-A", "Nax-Missing-Log-B/Operational"], TimeSpan.FromHours(1)), _ => { }, CancellationToken.None);
                Check.That(none.NoLogReadable && none.Count == 0, "an overview over missing logs must report that no log could be read");
                ReadSummary some = EventReader.ReadRecords(LogSource.ForProblemsOverview(["System", "Nax-Missing-Log-A"], TimeSpan.FromHours(1)), _ => { }, CancellationToken.None);
                Check.That(!some.NoLogReadable, "an overview that includes System has a readable log");
            });

        yield return ("live_subscription_delivers_new_event_with_message",
            "While the PowerShell log is watched, starting Windows PowerShell makes Windows write a start-up event, and it arrives within 30 seconds with its process ID and a formatted message.",
            () =>
            {
                const string channel = "Microsoft-Windows-PowerShell/Operational";
                using var arrived = new BlockingCollection<EventItem>();
                using LiveSubscription live = LiveSubscription.Start(channel, NewestBookmark(channel), fromOldest: false, new MessageStore(), arrived.Add, _ => { });
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
                using LiveSubscription live = LiveSubscription.Start("System", NewestBookmark("System"), fromOldest: false, new MessageStore(), _ => { }, message => reported = message);
                const int queryResultStale = 15011;
                LiveSubscription.Dispatch(0, live.Id, queryResultStale);
                Check.That(reported is not null && reported.StartsWith("Live updates for System stopped."), "the stop is reported: " + reported);
            });

        yield return ("live_subscription_after_reading_no_events_delivers_events_already_in_the_log",
            "When the read found no events, watching the log also delivers events that were written before watching started, so an event written between the read and the start of watching is not lost.",
            () =>
            {
                using var arrived = new BlockingCollection<EventItem>();
                using LiveSubscription live = LiveSubscription.Start("System", null, fromOldest: true, new MessageStore(), arrived.Add, _ => { });
                Check.That(arrived.TryTake(out EventItem? first, TimeSpan.FromSeconds(20)), "no existing System event arrived");
                Check.That(first!.Channel == "System" && first.Log == "System", "the event belongs to System");
            });

        yield return ("live_subscription_from_the_read_bookmark_delivers_an_event_written_before_watching_started",
            "An event that Windows writes after the log was read but before watching starts still arrives when watching starts from the bookmark of the read, and no event from the read arrives again.",
            () =>
            {
                const string channel = "Microsoft-Windows-PowerShell/Operational";
                var read = new HashSet<ulong>();
                using var stop = new CancellationTokenSource();
                ReadSummary summary = EventReader.ReadRecords(LogSource.ForChannel(channel), batch =>
                {
                    read.UnionWith(batch.Select(i => i.RecordId));
                    stop.Cancel();
                }, stop.Token);
                Check.That(summary.NewestBookmark is not null, "the read gives a bookmark");

                // The event is written completely before watching starts.
                using (var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command exit") { UseShellExecute = false, CreateNoWindow = true })!)
                {
                    uint pid = (uint)process.Id;
                    process.WaitForExit(30_000);
                    Thread.Sleep(TimeSpan.FromSeconds(2));

                    using var arrived = new BlockingCollection<EventItem>();
                    using LiveSubscription live = LiveSubscription.Start(channel, summary.NewestBookmark, fromOldest: false, new MessageStore(), arrived.Add, _ => { });
                    var seen = new List<EventItem>();
                    var deadline = Stopwatch.StartNew();
                    while (!seen.Any(i => i.ProcessId == pid) && deadline.Elapsed < TimeSpan.FromSeconds(30))
                    {
                        if (arrived.TryTake(out EventItem? item, TimeSpan.FromSeconds(1))) seen.Add(item);
                    }
                    Check.That(seen.Any(i => i.ProcessId == pid), $"the event of PowerShell process {pid}, written before watching started, did not arrive");
                    Check.That(!seen.Any(i => read.Contains(i.RecordId)), "an event that was already read arrived again");
                }
            });

        yield return ("live_subscription_resumed_from_its_bookmark_continues_without_repeating_events",
            "When watching stops and starts again from the bookmark the first subscription left, the second one continues after the last delivered event and repeats none of the events the first one delivered.",
            () =>
            {
                var first = new List<EventItem>();
                LiveSubscription live = LiveSubscription.Start("System", null, fromOldest: true, new MessageStore(), item => { lock (first) first.Add(item); }, _ => { });
                Thread.Sleep(TimeSpan.FromSeconds(2));
                live.Dispose();
                string? resume = live.ResumeBookmark;
                Check.That(first.Count > 0 && resume is not null, "the first subscription delivered events and left a bookmark");
                var delivered = first.Select(i => i.RecordId).ToHashSet();

                var second = new BlockingCollection<EventItem>();
                using (LiveSubscription again = LiveSubscription.Start("System", resume, fromOldest: false, new MessageStore(), second.Add, _ => { }))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                }
                EventItem? repeated = second.FirstOrDefault(i => delivered.Contains(i.RecordId));
                Check.That(repeated is null, $"event {repeated?.RecordId} was delivered twice");
            });

        yield return ("live_subscription_with_an_unusable_bookmark_fails_instead_of_skipping_events",
            "Watching from a bookmark that Windows cannot use fails with an error, instead of quietly watching only new events and missing the ones written since the bookmark.",
            () =>
            {
                try
                {
                    using LiveSubscription live = LiveSubscription.Start("System", "<not a bookmark", fromOldest: false, new MessageStore(), _ => { }, _ => { });
                    throw new Exception("watching from an unusable bookmark should have failed");
                }
                catch (EventLogException ex)
                {
                    // The bookmark itself is rejected, before Windows is asked to watch at all.
                    Check.That(ex.Message.Contains("could not continue watching"), "the error must come from the unusable bookmark: " + ex.Message);
                }
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

    /// <summary>The bookmark on the newest event of a log, read the way the window reads it.</summary>
    private static string? NewestBookmark(string channel)
    {
        using var stop = new CancellationTokenSource();
        return EventReader.ReadRecords(LogSource.ForChannel(channel), _ => stop.Cancel(), stop.Token).NewestBookmark;
    }

    private static EventItem Copy(EventItem item, DateTime? timeUtc = null, string? computer = null, ushort? eventId = null) => new()
    {
        Log = item.Log,
        Channel = item.Channel,
        Provider = item.Provider,
        Computer = computer ?? item.Computer,
        RecordId = item.RecordId,
        TimeCreatedUtc = timeUtc ?? item.TimeCreatedUtc,
        EventId = eventId ?? item.EventId,
        Level = item.Level,
    };

    private static List<EventItem> ReadAll(string file, out ReadSummary summary)
    {
        var items = new List<EventItem>();
        summary = EventReader.ReadRecords(LogSource.ForFile(file), items.AddRange, CancellationToken.None);
        return items;
    }
}
