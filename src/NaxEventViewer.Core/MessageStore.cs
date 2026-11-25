using System.Text;

namespace NaxEventViewer.Core;

/// <summary>
/// Holds the message text of one loaded log. Messages of the same kind repeat most of their lines ("Account Name:",
/// "Logon Type:" and so on), so every distinct line is kept once and each event keeps only the numbers of its lines.
/// For a Security log this needs a fraction of the memory of one string per event, and a search checks every distinct
/// line once instead of every message.
/// <para>Adding is thread-safe. Reading never blocks and may run on any thread while lines are added.</para>
/// </summary>
public sealed class MessageStore
{
    private const int ChunkBits = 12;
    private const int ChunkSize = 1 << ChunkBits;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    // Lines live in fixed-size chunks that never move, so a reader can use any line number below LineCount safely.
    private string[][] _chunks = new string[16][];
    private int _count;

    public MessageStore() => _lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The number of distinct lines stored so far.</summary>
    public int LineCount => Volatile.Read(ref _count);

    /// <summary>Stores a message and returns the numbers of its lines. Splitting at \n keeps \r, so the text reads back unchanged.</summary>
    public StoredMessage Add(string message)
    {
        ReadOnlySpan<char> text = message;
        int lineCount = text.Count('\n') + 1;
        var ids = new int[lineCount];
        lock (_gate)
        {
            int index = 0;
            foreach (Range range in text.Split('\n'))
            {
                ReadOnlySpan<char> line = text[range];
                if (!_lookup.TryGetValue(line, out int id))
                {
                    string created = line.ToString();
                    id = Append(created);
                    _ids.Add(created, id);
                }
                ids[index++] = id;
            }
        }
        return new StoredMessage(this, ids);
    }

    public string Line(int id)
    {
        string[][] chunks = Volatile.Read(ref _chunks);
        return chunks[id >> ChunkBits][id & (ChunkSize - 1)];
    }

    public string Join(int[] lines)
    {
        if (lines.Length == 1) return Line(lines[0]);
        var text = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) text.Append('\n');
            text.Append(Line(lines[i]));
        }
        return text.ToString();
    }

    /// <summary>The list preview: the first line that is not empty, shortened as <see cref="EventItem.MakePreview"/> does.</summary>
    public string Preview(int[] lines)
    {
        foreach (int id in lines)
        {
            string line = Line(id);
            if (!string.IsNullOrWhiteSpace(line)) return EventItem.MakePreview(line);
        }
        return "";
    }

    /// <summary>
    /// Copies the stored messages of <paramref name="items"/> into a fresh store so lines belonging to overwritten
    /// events can be reclaimed. Safe to run off the UI thread; the caller applies the returned mappings on the UI thread.
    /// </summary>
    public static (MessageStore Store, List<(EventItem Item, StoredMessage Message)> Remapped) Compact(IEnumerable<EventItem> items)
    {
        var fresh = new MessageStore();
        var remapped = new List<(EventItem, StoredMessage)>();
        foreach (EventItem item in items)
        {
            if (item.StoredLines is var (oldStore, oldLines))
            {
                remapped.Add((item, fresh.CopyLines(oldStore, oldLines)));
            }
            else if (item.PlainMessage is { } plain)
            {
                remapped.Add((item, fresh.Add(plain)));
            }
        }
        return (fresh, remapped);
    }

    /// <summary>
    /// Remaps an event whose message is still plain or stored in an older <see cref="MessageStore"/> into this store,
    /// returning null when the event has no message or already belongs to this store.
    /// </summary>
    public StoredMessage? RemapIfDifferent(EventItem item)
    {
        if (item.StoredLines is var (oldStore, oldLines))
            return ReferenceEquals(oldStore, this) ? null : CopyLines(oldStore, oldLines);
        if (item.PlainMessage is { } plain)
            return Add(plain);
        return null;
    }

    private StoredMessage CopyLines(MessageStore source, int[] sourceLines)
    {
        var ids = new int[sourceLines.Length];
        lock (_gate)
        {
            for (int i = 0; i < sourceLines.Length; i++)
            {
                string line = source.Line(sourceLines[i]);
                if (!_ids.TryGetValue(line, out int id))
                {
                    id = Append(line);
                    _ids.Add(line, id);
                }
                ids[i] = id;
            }
        }
        return new StoredMessage(this, ids);
    }

    private int Append(string line)
    {
        int id = _count;
        int chunk = id >> ChunkBits;
        if (chunk == _chunks.Length)
        {
            var bigger = new string[_chunks.Length * 2][];
            Array.Copy(_chunks, bigger, _chunks.Length);
            Volatile.Write(ref _chunks, bigger);
        }
        (_chunks[chunk] ??= new string[ChunkSize])[id & (ChunkSize - 1)] = line;
        // Published last, so a reader that sees the new count also sees the line.
        Volatile.Write(ref _count, id + 1);
        return id;
    }
}

/// <summary>A message kept in a <see cref="MessageStore"/>: an immutable pair of the store and its line numbers.</summary>
public sealed record StoredMessage(MessageStore Store, int[] Lines)
{
    public override string ToString() => Store.Join(Lines);
}
