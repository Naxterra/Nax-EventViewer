using System.Diagnostics;

namespace NaxEventViewer.Core.Tests;

/// <summary>The compact message storage, searching in it, and the size and speed limits for large logs.</summary>
internal static class MessageStoreTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("stored_message_reads_back_exactly",
            "A message with Windows line breaks, empty lines, tabs, umlauts, an emoji and a trailing line break reads back from the store character for character.",
            () =>
            {
                var store = new MessageStore();
                string message = "Die Änderung\r\n\r\n\tKonto:\t\tSYSTEM \U0001F600\r\nEnde\r\n";
                Check.Equal(message, store.Add(message).ToString());
                Check.Equal("", store.Add("").ToString());
            });

        yield return ("repeated_lines_are_stored_once",
            "Two messages that share three of their four lines add only five distinct lines to the store.",
            () =>
            {
                var store = new MessageStore();
                store.Add("Subject:\r\n\tAccount Name:\tSYSTEM\r\n\tLogon ID:\t0x3E7\r\nLogon Type: 5");
                store.Add("Subject:\r\n\tAccount Name:\tSYSTEM\r\n\tLogon ID:\t0x3E7\r\nLogon Type: 2");
                Check.Equal(5, store.LineCount);
            });

        yield return ("search_finds_the_same_events_in_stored_and_plain_messages",
            "A search gives the same result for an event whose message is stored line by line as for the same message held as one string, also for lines added after the first search.",
            () =>
            {
                var store = new MessageStore();
                string[] messages = ["Ein Konto wurde erfolgreich angemeldet.\r\n\tKontoname:\tNaxUser", "Dienst gestartet.", "Fehler beim LADEN des Treibers."];
                var stored = new List<EventItem>();
                var plain = new List<EventItem>();
                foreach (string text in messages)
                {
                    var item = Fixtures.Item(Now);
                    item.SetFormatted(store.Add(text), null);
                    stored.Add(item);
                    plain.Add(Fixtures.Item(Now, message: text));
                }
                foreach (string term in new[] { "kontoname", "laden", "gestartet", "Treibers.", "fehlt" })
                {
                    var filter = new EventFilter { Text = term };
                    Check.That(stored.Select(filter.Matches).SequenceEqual(plain.Select(filter.Matches)), $"\"{term}\" matched differently");
                }

                // A line stored after the filter has searched once must still be found.
                var search = new EventFilter { Text = "neu" };
                Check.That(!stored.Any(search.Matches), "nothing matches yet");
                var late = Fixtures.Item(Now);
                late.SetFormatted(store.Add("Ganz neue Zeile"), null);
                Check.That(search.Matches(late), "the line added later is found");
            });

        yield return ("preview_of_stored_message_matches_plain_preview",
            "The list preview of a stored message is the same as the preview of the same message held as one string.",
            () =>
            {
                string text = "\r\n  Der Dienst   \"Spooler\" wurde beendet.\r\nWeitere Angaben.";
                var item = Fixtures.Item(Now);
                item.SetFormatted(new MessageStore().Add(text), null);
                Check.Equal(EventItem.MakePreview(text), item.MessagePreview);
            });

        yield return ("security_style_messages_need_far_less_memory_than_plain_strings",
            "Twenty thousand logon messages of 40 lines that differ only in a few lines take less than a third of the memory in the store that they take as separate strings.",
            () =>
            {
                const int count = 20_000;
                long baseline = GC.GetTotalMemory(forceFullCollection: true);
                var strings = new string[count];
                for (int i = 0; i < count; i++) strings[i] = LogonMessage(i);
                long asStrings = GC.GetTotalMemory(forceFullCollection: true) - baseline;
                GC.KeepAlive(strings);

                var store = new MessageStore();
                var stored = new StoredMessage[count];
                baseline = GC.GetTotalMemory(forceFullCollection: true);
                for (int i = 0; i < count; i++) stored[i] = store.Add(strings[i]);
                long inStore = GC.GetTotalMemory(forceFullCollection: true) - baseline;
                GC.KeepAlive(stored);

                Check.That(inStore * 3 < asStrings, $"the store needs {inStore / count} bytes per message, plain strings {asStrings / count}");
                Check.Equal(strings[12_345], stored[12_345].ToString());
            });

        yield return ("search_and_sort_of_300000_events_takes_under_three_seconds",
            "Filtering 300,000 events with stored messages by a search word and sorting the result by source takes less than three seconds.",
            () =>
            {
                var store = new MessageStore();
                var items = new EventItem[300_000];
                for (int i = 0; i < items.Length; i++)
                {
                    var item = Fixtures.Item(Now.AddSeconds(-i), level: (byte)(1 + i % 4), id: (ushort)(4600 + i % 50), provider: "Provider" + (i % 40), recordId: (ulong)i);
                    item.SetFormatted(store.Add(LogonMessage(i)), null);
                    items[i] = item;
                }
                var watch = Stopwatch.StartNew();
                (List<EventItem> shown, int[] counts) = EventView.FilterAndSort(items, new EventFilter { Text = "0x1F" }, new EventComparer(SortColumn.Source, descending: false));
                watch.Stop();
                Check.That(watch.Elapsed < TimeSpan.FromSeconds(3), $"it took {watch.Elapsed.TotalSeconds:0.00} s");
                Check.That(shown.Count > 0 && counts.Sum() == shown.Count, "the search found events and the counts match");
            });
    }

    /// <summary>A message shaped like a Security logon event: mostly fixed lines, with a few lines that change per event.</summary>
    private static string LogonMessage(int i)
    {
        var lines = new List<string> { "Ein Konto wurde erfolgreich angemeldet.", "", "Antragsteller:" };
        for (int line = 0; line < 30; line++) lines.Add($"\tFeld {line}:\t\tWert {line % 7}");
        lines.Add($"\tAnmelde-ID:\t\t0x{i:X}");
        lines.Add($"\tProzess-ID:\t\t0x{i % 997:X}");
        lines.Add($"\tZeit:\t\t{i}");
        lines.Add("Dieses Ereignis wird beim Erstellen einer Anmeldesitzung generiert.");
        return string.Join("\r\n", lines);
    }
}
