using System.Collections.Concurrent;
using System.Globalization;

namespace NaxEventViewer.Core;

/// <summary>Which events the list shows. Filtering runs on the events already in memory, so it is instant.</summary>
public sealed class EventFilter
{
    private string[] _terms = [];

    /// <summary>The levels to show, or null for all.</summary>
    public IReadOnlySet<EventLevelKind>? Levels { get; init; }

    public EventIdFilter? EventIds { get; init; }

    /// <summary>Only events at or after this time, or null for any time.</summary>
    public DateTime? SinceUtc { get; init; }

    /// <summary>Words that must all appear in the message, source, task category, log or event ID, ignoring case.</summary>
    public string Text
    {
        get => string.Join(' ', _terms);
        init => _terms = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public bool HasText => _terms.Length > 0;

    public static EventFilter All { get; } = new();

    /// <summary>A copy of this filter with another start time, used to move a "last hour" window along with the clock.</summary>
    public EventFilter WithSince(DateTime? sinceUtc) => new() { Levels = Levels, EventIds = EventIds, SinceUtc = sinceUtc, _terms = _terms };

    public bool Matches(EventItem item) => MatchesExceptLevel(item) && (Levels is null || Levels.Contains(item.Kind));

    /// <summary>Used for the counts on the level buttons, which show what each level would add.</summary>
    public bool MatchesExceptLevel(EventItem item)
    {
        if (SinceUtc is DateTime since && item.TimeCreatedUtc < since) return false;
        if (EventIds is not null && !EventIds.Matches(item.EventId)) return false;
        foreach (string term in _terms)
        {
            if (!Contains(item.Provider, term) && !Contains(item.TaskName, term) && !Contains(item.Channel, term)
                && !Contains(item.EventIdText, term) && !MessageContains(item, term))
                return false;
        }
        return true;
    }

    // A search word never contains white space, so it can never span two lines; checking line by line is exact.
    private readonly ConcurrentDictionary<(MessageStore Store, string Term), LineMatches> _lineMatches = new();

    private bool MessageContains(EventItem item, string term)
    {
        if (item.PlainMessage is { } plain) return Contains(plain, term);
        if (item.StoredLines is not var (store, lines)) return false;
        return _lineMatches.GetOrAdd((store, term), static _ => new LineMatches()).AnyMatch(store, term, lines);
    }

    /// <summary>Which lines of a store contain one search word. New lines are checked when they first matter.</summary>
    private sealed class LineMatches
    {
        private readonly object _gate = new();
        private bool[] _matches = [];
        private int _checked;

        public bool AnyMatch(MessageStore store, string term, int[] lines)
        {
            bool[] matches;
            lock (_gate)
            {
                int count = store.LineCount;
                if (_checked < count)
                {
                    if (_matches.Length < count) Array.Resize(ref _matches, Math.Max(count, _matches.Length * 2));
                    for (int id = _checked; id < count; id++) _matches[id] = Contains(store.Line(id), term);
                    _checked = count;
                }
                matches = _matches;
            }
            // Every line of the item was stored before the item got its lines, so each number is below the count read above.
            foreach (int id in lines)
                if (matches[id]) return true;
            return false;
        }
    }

    // Ordinal comparison ignoring case also folds letters such as Ä and ä, and it is several times faster than the
    // culture-aware comparison, which matters when a search runs over hundreds of thousands of messages.
    private static bool Contains(string? text, string term) => text is not null && text.Contains(term, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An event ID filter in the syntax of the Windows Event Viewer: IDs and ranges separated by commas, such as
/// "4624, 4625, 1000-1010", and IDs with a leading minus to exclude them, such as "-4634".
/// </summary>
public sealed class EventIdFilter
{
    private readonly List<(int From, int To)> _include = [];
    private readonly List<(int From, int To)> _exclude = [];

    private EventIdFilter()
    {
    }

    /// <summary>Parses the text. Empty text gives a null filter. Returns false when the text is not valid.</summary>
    public static bool TryParse(string? text, out EventIdFilter? filter)
    {
        filter = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var result = new EventIdFilter();
        foreach (string rawPart in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (rawPart.Length == 0) continue;
            bool exclude = rawPart.StartsWith('-');
            string part = exclude ? rawPart[1..].Trim() : rawPart;
            int dash = part.IndexOf('-');
            int from, to;
            if (dash < 0)
            {
                if (!TryParseId(part, out from)) return false;
                to = from;
            }
            else if (!TryParseId(part[..dash].Trim(), out from) || !TryParseId(part[(dash + 1)..].Trim(), out to) || to < from)
            {
                return false;
            }
            (exclude ? result._exclude : result._include).Add((from, to));
        }
        if (result._include.Count == 0 && result._exclude.Count == 0) return true;
        filter = result;
        return true;
    }

    private static bool TryParseId(string text, out int id) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id <= ushort.MaxValue;

    public bool Matches(int eventId)
    {
        foreach (var (from, to) in _exclude)
            if (eventId >= from && eventId <= to) return false;
        if (_include.Count == 0) return true;
        foreach (var (from, to) in _include)
            if (eventId >= from && eventId <= to) return true;
        return false;
    }
}

/// <summary>Builds the list the window shows from all events of a log.</summary>
public static class EventView
{
    /// <summary>
    /// Returns the events that match the filter, sorted, and how many events of each level match the filter apart
    /// from its level part (the counts on the level buttons). Safe to run off the UI thread.
    /// </summary>
    public static (List<EventItem> Shown, int[] Counts) FilterAndSort(IReadOnlyList<EventItem> items, EventFilter filter, IComparer<EventItem> comparer)
    {
        var counts = new int[Enum.GetValues<EventLevelKind>().Length];
        var shown = new List<EventItem>(items.Count);
        foreach (EventItem item in items)
        {
            if (!filter.MatchesExceptLevel(item)) continue;
            counts[(int)item.Kind]++;
            if (filter.Levels is null || filter.Levels.Contains(item.Kind)) shown.Add(item);
        }
        if (comparer is EventComparer { Column: SortColumn.Task } byTask)
        {
            // Task names change while messages load, possibly on another thread during this sort. Each name is read
            // once here, so every comparison within the sort sees the same value and the comparer stays consistent.
            var keyed = new (EventItem Item, string Task)[shown.Count];
            for (int i = 0; i < keyed.Length; i++) keyed[i] = (shown[i], shown[i].TaskName);
            Array.Sort(keyed, (a, b) => byTask.Compare(a.Item, a.Task, b.Item, b.Task));
            for (int i = 0; i < keyed.Length; i++) shown[i] = keyed[i].Item;
        }
        else
        {
            shown.Sort(comparer);
        }
        return (shown, counts);
    }
}

public enum SortColumn
{
    Level,
    Time,
    Source,
    EventId,
    Task,
}

/// <summary>Orders events by one column. Equal values fall back to newest first, so the order is always the same.</summary>
public sealed class EventComparer(SortColumn column, bool descending) : IComparer<EventItem>
{
    public SortColumn Column { get; } = column;
    public bool Descending { get; } = descending;

    public int Compare(EventItem? x, EventItem? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return Compare(x, x.TaskName, y, y.TaskName);
    }

    /// <summary>Compares with task names the caller read beforehand, so a name that changes meanwhile cannot change the answer.</summary>
    internal int Compare(EventItem x, string xTask, EventItem y, string yTask)
    {
        if (ReferenceEquals(x, y)) return 0;
        int result = Column switch
        {
            SortColumn.Level => Severity(x.Kind).CompareTo(Severity(y.Kind)),
            SortColumn.Source => string.Compare(x.Provider, y.Provider, StringComparison.CurrentCultureIgnoreCase),
            SortColumn.EventId => x.EventId.CompareTo(y.EventId),
            SortColumn.Task => string.Compare(xTask, yTask, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };
        if (Descending) result = -result;
        if (result != 0) return result;

        // Newest first for the time column descending and as the tie-breaker; oldest first only when time is sorted ascending.
        bool oldestFirst = Column == SortColumn.Time && !Descending;
        result = x.TimeCreatedUtc.CompareTo(y.TimeCreatedUtc);
        if (result == 0) result = x.RecordId.CompareTo(y.RecordId);
        if (result == 0) result = string.CompareOrdinal(x.Channel, y.Channel);
        return oldestFirst ? result : -result;
    }

    /// <summary>Higher is more severe, so sorting by level descending puts critical events first.</summary>
    private static int Severity(EventLevelKind kind) => kind switch
    {
        EventLevelKind.Critical => 6,
        EventLevelKind.Error => 5,
        EventLevelKind.AuditFailure => 4,
        EventLevelKind.Warning => 3,
        EventLevelKind.Information => 2,
        EventLevelKind.AuditSuccess => 1,
        _ => 0,
    };
}
