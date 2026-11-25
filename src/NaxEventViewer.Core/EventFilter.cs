using System.Collections.Concurrent;
using System.Globalization;

namespace NaxEventViewer.Core;

/// <summary>Which events the list shows. Filtering runs on the events already in memory, so it is instant.</summary>
public sealed class EventFilter
{
    private string _rawText = "";
    private string[] _terms = [];
    private string[] _excludedTerms = [];

    /// <summary>The levels to show, or null for all.</summary>
    public IReadOnlySet<EventLevelKind>? Levels { get; init; }

    public EventIdFilter? EventIds { get; init; }

    /// <summary>Only events at or after this time, or null for no lower bound.</summary>
    public DateTime? SinceUtc { get; init; }

    /// <summary>Only events at or before this time, or null for no upper bound.</summary>
    public DateTime? UntilUtc { get; init; }

    private HashSet<string>? _includedSources;
    private HashSet<string>? _excludedSources;

    /// <summary>Sources to include (case-insensitive), or null for all.</summary>
    public IReadOnlySet<string>? IncludedSources
    {
        get => _includedSources;
        init => _includedSources = value is null ? null : new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Sources to exclude (case-insensitive), or null for none.</summary>
    public IReadOnlySet<string>? ExcludedSources
    {
        get => _excludedSources;
        init => _excludedSources = value is null ? null : new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Words and quoted phrases that must appear (or, with a leading hyphen, must not appear) in the message, source,
    /// task category, log, computer, or event ID, ignoring case.
    /// </summary>
    public string Text
    {
        get => _rawText;
        init
        {
            _rawText = value ?? "";
            (_terms, _excludedTerms) = ParseTerms(_rawText);
        }
    }

    public bool HasText => _terms.Length > 0 || _excludedTerms.Length > 0;

    public static EventFilter All { get; } = new();

    /// <summary>A copy of this filter with another start time, used to move a "last hour" window along with the clock.</summary>
    public EventFilter WithSince(DateTime? sinceUtc) => WithTimeRange(sinceUtc, UntilUtc);

    /// <summary>A copy of this filter with a new time range, keeping all other filter parts and resetting cached line matches.</summary>
    public EventFilter WithTimeRange(DateTime? sinceUtc, DateTime? untilUtc) => new()
    {
        Levels = Levels,
        EventIds = EventIds,
        SinceUtc = sinceUtc,
        UntilUtc = untilUtc,
        IncludedSources = IncludedSources,
        ExcludedSources = ExcludedSources,
        _rawText = _rawText,
        _terms = _terms,
        _excludedTerms = _excludedTerms,
    };

    public bool Matches(EventItem item) => MatchesExceptLevel(item) && (Levels is null || Levels.Contains(item.Kind));

    /// <summary>Used for the counts on the level buttons, which show what each level would add.</summary>
    public bool MatchesExceptLevel(EventItem item)
    {
        if (SinceUtc is DateTime since && item.TimeCreatedUtc < since) return false;
        if (UntilUtc is DateTime until && item.TimeCreatedUtc > until) return false;
        if (IncludedSources is { Count: > 0 } && !IncludedSources.Contains(item.Provider)) return false;
        if (ExcludedSources is { Count: > 0 } && ExcludedSources.Contains(item.Provider)) return false;
        if (EventIds is not null && !EventIds.Matches(item.EventId)) return false;
        foreach (string term in _terms)
        {
            if (!ItemMatchesTerm(item, term)) return false;
        }
        foreach (string excluded in _excludedTerms)
        {
            if (ItemMatchesTerm(item, excluded)) return false;
        }
        return true;
    }

    private bool ItemMatchesTerm(EventItem item, string term) =>
        Contains(item.Provider, term)
        || Contains(item.TaskName, term)
        || Contains(item.Channel, term)
        || Contains(item.Computer, term)
        || Contains(item.EventIdText, term)
        || MessageContains(item, term);

    /// <summary>
    /// Splits search text into required terms and excluded terms (leading hyphen), honoring quoted phrases such as
    /// "access denied" and -"access denied". A standalone hyphen is searched literally.
    /// </summary>
    internal static (string[] Include, string[] Exclude) ParseTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ([], []);
        var include = new List<string>();
        var exclude = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;

            bool isExclude = false;
            if (text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                isExclude = true;
                i++;
            }

            string token;
            if (text[i] == '"')
            {
                i++;
                int start = i;
                while (i < text.Length && text[i] != '"') i++;
                token = text[start..i];
                if (i < text.Length && text[i] == '"') i++;
            }
            else
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                token = text[start..i];
            }

            if (token.Length > 0) (isExclude ? exclude : include).Add(token);
        }
        return (include.ToArray(), exclude.ToArray());
    }

    // A single-line search term can never span two lines; checking line by line is exact. Terms containing a line break
    // fall back to checking the joined message so stored and plain messages always agree.
    private readonly ConcurrentDictionary<(MessageStore Store, string Term), LineMatches> _lineMatches = new();

    /// <summary>Drops cached per-store line match tables so an old MessageStore can be collected without swapping the filter reference.</summary>
    public void ClearLineCache() => _lineMatches.Clear();

    private bool MessageContains(EventItem item, string term)
    {
        if (item.PlainMessage is { } plain) return Contains(plain, term);
        if (item.StoredLines is not var (store, lines)) return false;
        if (term.Contains('\n')) return Contains(store.Join(lines), term);
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

    /// <summary>Matches no event. The window uses it while the event ID text is not valid, so a typo never shows everything.</summary>
    public static EventIdFilter Nothing { get; } = CreateNothing();

    private static EventIdFilter CreateNothing()
    {
        var nothing = new EventIdFilter();
        nothing._exclude.Add((int.MinValue, int.MaxValue));
        return nothing;
    }

    /// <summary>
    /// Parses the text. Empty text gives a null filter. Returns false when the text is not valid. En dashes, em dashes
    /// and minus signs, which autocorrect often puts in place of a hyphen, count as hyphens.
    /// </summary>
    public static bool TryParse(string? text, out EventIdFilter? filter)
    {
        filter = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        text = text.Replace('–', '-').Replace('—', '-').Replace('−', '-');

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
    Log,
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
            SortColumn.Log => string.Compare(x.LogDisplayName, y.LogDisplayName, StringComparison.CurrentCultureIgnoreCase),
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
