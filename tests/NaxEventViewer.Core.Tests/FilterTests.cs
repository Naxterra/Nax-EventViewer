namespace NaxEventViewer.Core.Tests;

/// <summary>Filtering and sorting of the events in the list.</summary>
internal static class FilterTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("event_id_filter_accepts_ids_ranges_and_exclusions",
            "The text \"4624, 1000-1010, -1005\" shows 4624 and 1000 to 1010 except 1005, and nothing else.",
            () =>
            {
                Check.That(EventIdFilter.TryParse("4624, 1000-1010, -1005", out EventIdFilter? filter) && filter is not null, "the text must parse");
                Check.That(filter!.Matches(4624), "4624 is listed");
                Check.That(filter.Matches(1000) && filter.Matches(1010), "range ends are included");
                Check.That(!filter.Matches(1005), "1005 is excluded");
                Check.That(!filter.Matches(4625), "4625 is not listed");
            });

        yield return ("event_id_filter_with_only_exclusions_shows_everything_else",
            "The text \"-4634\" shows every event except ID 4634.",
            () =>
            {
                Check.That(EventIdFilter.TryParse("-4634", out EventIdFilter? filter) && filter is not null, "the text must parse");
                Check.That(!filter!.Matches(4634), "4634 is excluded");
                Check.That(filter.Matches(1), "other IDs are shown");
            });

        yield return ("event_id_filter_rejects_letters_and_reversed_ranges",
            "Text with letters, a range that runs backwards or an ID above 65535 is reported as invalid, and empty text means no filter.",
            () =>
            {
                Check.That(!EventIdFilter.TryParse("12a", out _), "letters are invalid");
                Check.That(!EventIdFilter.TryParse("20-10", out _), "a backwards range is invalid");
                Check.That(!EventIdFilter.TryParse("70000", out _), "an ID above 65535 is invalid");
                Check.That(EventIdFilter.TryParse("  ", out EventIdFilter? none) && none is null, "empty text is no filter");
            });

        yield return ("text_filter_needs_every_word_ignoring_case",
            "Searching for \"disk FAILED\" finds an event whose message has both words in any case, and not one with only one of them.",
            () =>
            {
                var filter = new EventFilter { Text = "disk FAILED" };
                Check.That(filter.Matches(Fixtures.Item(Now, message: "The Disk check failed.")), "both words appear");
                Check.That(!filter.Matches(Fixtures.Item(Now, message: "The disk check passed.")), "only one word appears");
            });

        yield return ("text_filter_matches_source_and_event_id_before_messages_are_loaded",
            "While messages are still loading, a search still finds events by source name and event ID.",
            () =>
            {
                Check.That(new EventFilter { Text = "kernel-power" }.Matches(Fixtures.Item(Now, provider: "Microsoft-Windows-Kernel-Power")), "the source matches");
                Check.That(new EventFilter { Text = "41" }.Matches(Fixtures.Item(Now, id: 41)), "the event ID matches");
            });

        yield return ("level_filter_treats_audit_keywords_as_their_own_level",
            "A Security event with the audit failure keyword counts as Audit Failure, not as Information, so hiding Information keeps it visible.",
            () =>
            {
                EventItem audit = Fixtures.Item(Now, level: 0, keywords: EventItem.AuditFailureKeyword);
                Check.Equal(EventLevelKind.AuditFailure, audit.Kind);
                var filter = new EventFilter { Levels = new HashSet<EventLevelKind> { EventLevelKind.AuditFailure, EventLevelKind.Error } };
                Check.That(filter.Matches(audit), "the audit failure is shown");
                Check.That(!filter.Matches(Fixtures.Item(Now, level: 4)), "plain information is hidden");
            });

        yield return ("time_filter_hides_events_older_than_the_limit",
            "With \"since one hour ago\", an event from two hours ago is hidden and one from ten minutes ago is shown.",
            () =>
            {
                var filter = new EventFilter { SinceUtc = Now.AddHours(-1) };
                Check.That(!filter.Matches(Fixtures.Item(Now.AddHours(-2))), "the old event is hidden");
                Check.That(filter.Matches(Fixtures.Item(Now.AddMinutes(-10))), "the recent event is shown");
            });

        yield return ("moving_the_time_window_keeps_the_other_filters",
            "When a \"last hour\" window moves on with the clock, the search words, event IDs and levels of the filter stay the same.",
            () =>
            {
                EventIdFilter.TryParse("41", out EventIdFilter? ids);
                var filter = new EventFilter { Text = "disk", EventIds = ids, Levels = new HashSet<EventLevelKind> { EventLevelKind.Error }, SinceUtc = Now.AddHours(-2) };
                EventFilter moved = filter.WithSince(Now.AddHours(-1));
                Check.That(moved.Matches(Fixtures.Item(Now, level: 2, id: 41, message: "disk full")), "a matching recent error is shown");
                Check.That(!moved.Matches(Fixtures.Item(Now.AddMinutes(-90), level: 2, id: 41, message: "disk full")), "an error from 90 minutes ago is now outside the window");
                Check.That(!moved.Matches(Fixtures.Item(Now, level: 2, id: 42, message: "disk full")), "the event ID filter is kept");
                Check.That(!moved.Matches(Fixtures.Item(Now, level: 3, id: 41, message: "disk full")), "the level filter is kept");
                Check.That(!moved.Matches(Fixtures.Item(Now, level: 2, id: 41, message: "network")), "the search words are kept");
            });

        yield return ("text_filter_ignores_case_of_umlauts",
            "Searching for \"ÄNDERUNG\" finds a message that contains \"Änderung\" and one that contains \"änderung\".",
            () =>
            {
                var filter = new EventFilter { Text = "ÄNDERUNG" };
                Check.That(filter.Matches(Fixtures.Item(Now, message: "Die Änderung wurde gespeichert.")), "capital Ä matches");
                Check.That(filter.Matches(Fixtures.Item(Now, message: "keine änderung")), "small ä matches");
            });

        yield return ("sort_by_level_puts_critical_first_then_newest",
            "Sorting by level puts critical events before errors and warnings, and events of the same level newest first.",
            () =>
            {
                EventItem oldError = Fixtures.Item(Now.AddHours(-2), level: 2, recordId: 1);
                EventItem newError = Fixtures.Item(Now, level: 2, recordId: 2);
                EventItem critical = Fixtures.Item(Now.AddDays(-3), level: 1, recordId: 3);
                EventItem warning = Fixtures.Item(Now, level: 3, recordId: 4);
                var list = new List<EventItem> { warning, oldError, critical, newError };
                list.Sort(new EventComparer(SortColumn.Level, descending: true));
                Check.That(list.SequenceEqual([critical, newError, oldError, warning]), "the order must be critical, new error, old error, warning");
            });

        yield return ("sort_by_task_while_task_names_change_returns_every_event_once",
            "Sorting 100,000 events by task category while another thread keeps changing their task names finishes without an error and returns every event exactly once, and once the names stop changing the list is in order by task name.",
            () =>
            {
                var items = new EventItem[100_000];
                for (int i = 0; i < items.Length; i++)
                {
                    items[i] = Fixtures.Item(Now.AddSeconds(-i), recordId: (ulong)i);
                    items[i].SetFormatted("x", "Task " + (i % 97));
                }
                using var stop = new CancellationTokenSource();
                var renamer = Task.Run(() =>
                {
                    var random = new Random(1);
                    while (!stop.IsCancellationRequested)
                        items[random.Next(items.Length)].SetFormatted("x", "Task " + random.Next(97));
                });
                try
                {
                    for (int round = 0; round < 5; round++)
                    {
                        (List<EventItem> shown, _) = EventView.FilterAndSort(items, EventFilter.All, new EventComparer(SortColumn.Task, descending: false));
                        Check.Equal(items.Length, shown.Count);
                        Check.Equal(items.Length, shown.Distinct().Count());
                    }
                }
                finally
                {
                    stop.Cancel();
                    renamer.Wait();
                }

                // Without changes the result is fully ordered by task name.
                (List<EventItem> stable, _) = EventView.FilterAndSort(items, EventFilter.All, new EventComparer(SortColumn.Task, descending: false));
                for (int i = 1; i < stable.Count; i++)
                    Check.That(string.Compare(stable[i - 1].TaskName, stable[i].TaskName, StringComparison.CurrentCultureIgnoreCase) <= 0, "the list must be ordered by task name");
            });

        yield return ("sort_by_time_ascending_puts_oldest_first",
            "Sorting by time ascending shows the oldest event first.",
            () =>
            {
                EventItem older = Fixtures.Item(Now.AddHours(-1), recordId: 1);
                EventItem newer = Fixtures.Item(Now, recordId: 2);
                var list = new List<EventItem> { newer, older };
                list.Sort(new EventComparer(SortColumn.Time, descending: false));
                Check.That(list[0] == older, "the older event comes first");
            });

        yield return ("message_preview_is_first_line_with_collapsed_spaces",
            "The list shows only the first non-empty line of a message, with runs of spaces and tabs shortened to one space.",
            () =>
            {
                Check.Equal("The service started.", EventItem.MakePreview("\r\n  The service \t  started.\r\nDetails follow."));
                Check.Equal(300, EventItem.MakePreview(new string('x', 1000)).Length);
            });
    }
}
