using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using NaxEventViewer.Core.Native;

namespace NaxEventViewer.Core;

/// <summary>What happened while reading a source: how many events were read and which problems occurred.</summary>
public sealed class ReadSummary
{
    private readonly List<string> _problems = [];
    private readonly List<string> _leftOut = [];

    public int Count { get; internal set; }
    public TimeSpan Elapsed { get; internal set; }
    public bool AccessDenied { get; private set; }
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Logs of the overview that Windows does not let this user read, so their events are missing.</summary>
    public IReadOnlyList<string> LeftOut => _leftOut;

    /// <summary>True when the overview could open none of its logs, for example because they were locked or missing.</summary>
    public bool NoLogReadable { get; internal set; }

    /// <summary>
    /// For a single log, a Windows bookmark on the newest event read, as XML, or null when no event was read. Watching
    /// the log from this bookmark misses no event written after the read.
    /// </summary>
    public string? NewestBookmark { get; internal set; }

    /// <summary>False when the log could only be read oldest first, as analytic and debug logs are.</summary>
    public bool NewestFirst { get; internal set; } = true;

    internal void AddLeftOut(string channel)
    {
        AccessDenied = true;
        _leftOut.Add(channel);
    }

    internal void AddProblem(string what, int error)
    {
        if (error == Wevtapi.ErrorAccessDenied) AccessDenied = true;
        _problems.Add($"{what} {new System.ComponentModel.Win32Exception(error).Message}");
    }

    internal void AddProblem(string text) => _problems.Add(text);
}

/// <summary>The parts of an event that are only read when the user selects it.</summary>
public sealed record EventDetail(string? Xml, string? Message, string? TaskName, string? OpcodeName, string? KeywordNames, string? Error);

/// <summary>
/// Reads events in two passes. The first pass reads only the System section of every event, which is fast, so the
/// list can show all events at once. The second pass formats the messages, which needs each provider's resources and
/// is the slow part that makes the Windows Event Viewer wait before it shows anything.
/// </summary>
public static unsafe class EventReader
{
    internal const int BatchSize = 512;
    private const int MessageBatchSize = 256;

    /// <summary>Reads the System section of every event and hands the events over in batches, newest first.</summary>
    public static ReadSummary ReadRecords(LogSource source, Action<IReadOnlyList<EventItem>> onBatch, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        var summary = new ReadSummary();
        if (source.Kind == LogSourceKind.ProblemsOverview)
        {
            // The overview query skips logs it cannot read without saying so; name them instead.
            int readable = 0;
            foreach (string channel in source.Channels)
            {
                int error = ChannelCatalog.OpenError(channel);
                if (error == 0) readable++;
                else if (error == Wevtapi.ErrorAccessDenied) summary.AddLeftOut(channel);
            }
            summary.NoLogReadable = source.Channels.Count > 0 && readable == 0;
        }
        var strings = new StringPool();
        using var renderer = new SystemRenderer(strings);
        using var newest = new Bookmark();
        bool trackNewest = source.Kind == LogSourceKind.Channel;
        foreach (QueryPart part in source.Parts())
        {
            ForEachBatch(part, summary, cancellation, (handles, count, newestFirst) =>
            {
                summary.NewestFirst = newestFirst;
                if (trackNewest && count > 0)
                {
                    // Newest first: the first event of the first batch. Oldest first: the last event of each batch.
                    if (!newestFirst) newest.MoveTo(handles[count - 1]);
                    else if (!newest.IsSet) newest.MoveTo(handles[0]);
                }
                var batch = new List<EventItem>(count);
                for (int i = 0; i < count; i++)
                {
                    EventItem? item = renderer.Render(handles[i], part.Path);
                    if (item is not null) batch.Add(item);
                }
                summary.Count += batch.Count;
                if (batch.Count > 0) onBatch(batch);
            });
            if (cancellation.IsCancellationRequested) break;
        }
        if (trackNewest) summary.NewestBookmark = newest.ToXml();
        summary.Elapsed = watch.Elapsed;
        return summary;
    }

    /// <summary>
    /// Reads the source a second time and formats the message and task name of every event in <paramref name="items"/>.
    /// Events that arrived after the first pass are skipped, because the live subscription already formats them.
    /// </summary>
    public static void FormatMessages(LogSource source, IReadOnlyList<EventItem> items, MessageStore store, Action<IReadOnlyList<FormattedMessage>> onBatch, CancellationToken cancellation)
    {
        var byKey = new Dictionary<EventKey, EventItem>(items.Count);
        foreach (EventItem item in items) byKey.TryAdd(item.Key, item);

        var summary = new ReadSummary();
        var strings = new StringPool();
        using var renderer = new SystemRenderer(strings);
        using var formatter = new MessageFormatter();
        var pending = new List<FormattedMessage>(MessageBatchSize);
        foreach (QueryPart part in source.Parts())
        {
            ForEachBatch(part, summary, cancellation, (handles, count, _) =>
            {
                for (int i = 0; i < count && !cancellation.IsCancellationRequested; i++)
                {
                    EventItem? rendered = renderer.Render(handles[i], part.Path);
                    if (rendered is null || !byKey.TryGetValue(rendered.Key, out EventItem? item) || item.HasMessage) continue;
                    pending.Add(new FormattedMessage(item, store.Add(formatter.FormatMessage(handles[i], item)), formatter.FormatTask(handles[i], item)));
                    if (pending.Count >= MessageBatchSize)
                    {
                        onBatch(pending.ToArray());
                        pending.Clear();
                    }
                }
            });
            if (cancellation.IsCancellationRequested) return;
        }
        if (pending.Count > 0) onBatch(pending.ToArray());
    }

    /// <summary>Finds one event again by its record number and reads its XML and the names of its parts.</summary>
    public static EventDetail ReadDetail(LogSource source, EventItem item)
    {
        bool fromFile = source.Kind == LogSourceKind.File;
        // The log the event was read from, which for a forwarded event is ForwardedEvents and not the channel it names.
        string path = fromFile ? source.Path : item.Log.Length > 0 ? item.Log : item.Channel;
        int flags = fromFile ? Wevtapi.EvtQueryFilePath : Wevtapi.EvtQueryChannelPath;

        nint evt = FindEvent(path, flags, item, out int error);
        if (evt == 0)
            return new EventDetail(null, null, null, null, null, $"The event could not be read again. {new System.ComponentModel.Win32Exception(error).Message}");
        try
        {
            using var formatter = new MessageFormatter();
            return new EventDetail(
                formatter.RenderXml(evt),
                formatter.FormatMessage(evt, item),
                formatter.FormatTask(evt, item),
                item.Opcode == 0 ? null : formatter.FormatPart(evt, item, Wevtapi.EvtFormatMessageOpcode),
                item.Keywords == 0 ? null : formatter.FormatPart(evt, item, Wevtapi.EvtFormatMessageKeyword),
                null);
        }
        finally
        {
            Wevtapi.EvtClose(evt);
        }
    }

    /// <summary>
    /// Positions a query on the event through a bookmark, which is fast even in large logs, and falls back to an XPath
    /// query on the record number when the bookmark does not lead to the right event. Several events can share a
    /// record number (forwarded events from different computers), so the fallback checks each of them.
    /// </summary>
    private static nint FindEvent(string path, int flags, EventItem item, out int error)
    {
        string recordId = item.RecordId.ToString(CultureInfo.InvariantCulture);
        string bookmarkChannel = (flags & Wevtapi.EvtQueryFilePath) != 0 ? item.Channel : path;
        string bookmarkXml = $"<BookmarkList><Bookmark Channel=\"{SecurityElement.Escape(bookmarkChannel)}\" RecordId=\"{recordId}\" IsCurrent=\"true\"/></BookmarkList>";
        nint bookmark = Wevtapi.EvtCreateBookmark(bookmarkXml);
        if (bookmark != 0)
        {
            try
            {
                nint query = Wevtapi.EvtQuery(0, path, "*", flags);
                if (query != 0)
                {
                    try
                    {
                        if (Wevtapi.EvtSeek(query, 0, bookmark, 0, Wevtapi.EvtSeekRelativeToBookmark))
                        {
                            nint evt = NextIfMatches(query, item, path, attempts: 1);
                            if (evt != 0)
                            {
                                error = 0;
                                return evt;
                            }
                        }
                    }
                    finally
                    {
                        Wevtapi.EvtClose(query);
                    }
                }
            }
            finally
            {
                Wevtapi.EvtClose(bookmark);
            }
        }

        nint byId = Wevtapi.EvtQuery(0, path, $"*[System[EventRecordID={recordId}]]", flags);
        if (byId == 0)
        {
            error = Marshal.GetLastPInvokeError();
            return 0;
        }
        try
        {
            nint evt = NextIfMatches(byId, item, path, attempts: 1000);
            error = evt == 0 ? Wevtapi.ErrorNoMoreItems : 0;
            return evt;
        }
        finally
        {
            Wevtapi.EvtClose(byId);
        }
    }

    /// <summary>
    /// Returns the first of the next <paramref name="attempts"/> results that is the same event as <paramref name="item"/>,
    /// or 0. Besides the record number, the time, ID, source, channel and computer must match, so an event in a log that
    /// was cleared or a file that was replaced since the list was read is never shown in place of the selected one.
    /// </summary>
    private static nint NextIfMatches(nint query, EventItem item, string path, int attempts)
    {
        using var renderer = new SystemRenderer(new StringPool());
        for (int i = 0; i < attempts; i++)
        {
            nint evt;
            if (!Wevtapi.EvtNext(query, 1, &evt, Wevtapi.Infinite, 0, out int returned) || returned != 1) return 0;
            if (renderer.Render(evt, path) is { } found && IsSameEvent(found, item)) return evt;
            Wevtapi.EvtClose(evt);
        }
        return 0;
    }

    internal static bool IsSameEvent(EventItem found, EventItem item) =>
        found.RecordId == item.RecordId
        && found.TimeCreatedUtc == item.TimeCreatedUtc
        && found.EventId == item.EventId
        && string.Equals(found.Provider, item.Provider, StringComparison.OrdinalIgnoreCase)
        && string.Equals(found.Channel, item.Channel, StringComparison.OrdinalIgnoreCase)
        && string.Equals(found.Computer, item.Computer, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs one query and calls <paramref name="onBatch"/> for each batch of event handles, with whether the query
    /// returns the newest events first. The handles are closed after the callback returns.
    /// </summary>
    private static void ForEachBatch(QueryPart part, ReadSummary summary, CancellationToken cancellation, Action<nint[], int, bool> onBatch)
    {
        bool newestFirst = (part.Flags & Wevtapi.EvtQueryReverseDirection) != 0;
        nint query = Wevtapi.EvtQuery(0, part.Path, part.Query, part.Flags);
        if (query == 0 && newestFirst)
        {
            newestFirst = false;
            // Analytic and debug logs can only be read oldest first. The list sorts the events afterwards anyway.
            int reverseError = Marshal.GetLastPInvokeError();
            query = Wevtapi.EvtQuery(0, part.Path, part.Query, part.Flags & ~Wevtapi.EvtQueryReverseDirection);
            if (query == 0)
            {
                summary.AddProblem($"Windows could not open {part.Path ?? "the logs"}.", reverseError);
                return;
            }
        }
        else if (query == 0)
        {
            summary.AddProblem($"Windows could not open {part.Path ?? "the logs"}.", Marshal.GetLastPInvokeError());
            return;
        }

        var handles = new nint[BatchSize];
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                int returned;
                bool ok;
                fixed (nint* buffer = handles)
                    ok = Wevtapi.EvtNext(query, BatchSize, buffer, Wevtapi.Infinite, 0, out returned);
                if (!ok)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != Wevtapi.ErrorNoMoreItems) summary.AddProblem($"Reading {part.Path ?? "the logs"} stopped early.", error);
                    break;
                }
                try
                {
                    onBatch(handles, returned, newestFirst);
                }
                finally
                {
                    for (int i = 0; i < returned; i++) Wevtapi.EvtClose(handles[i]);
                }
            }
        }
        finally
        {
            Wevtapi.EvtClose(query);
        }
    }
}
