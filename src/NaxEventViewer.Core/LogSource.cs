using System.Globalization;
using System.Security;
using System.Text;
using NaxEventViewer.Core.Native;

namespace NaxEventViewer.Core;

public enum LogSourceKind
{
    /// <summary>A live log on this PC, such as System or Microsoft-Windows-PowerShell/Operational.</summary>
    Channel,
    /// <summary>A saved .evtx file.</summary>
    File,
    /// <summary>Critical events, errors and warnings from many logs within a recent time window.</summary>
    ProblemsOverview,
}

/// <summary>One query the reader runs. Several parts are run one after another and their results are combined.</summary>
internal readonly record struct QueryPart(string? Path, string Query, int Flags);

/// <summary>What the user is looking at: one log, one saved file, or the cross-log overview of recent problems.</summary>
public sealed class LogSource
{
    /// <summary>Wevtapi rejects structured queries with very many Select elements, so the overview runs in chunks.</summary>
    internal const int ChannelsPerOverviewQuery = 64;

    private LogSource(LogSourceKind kind, string path, string displayName, IReadOnlyList<string> channels, TimeSpan window)
    {
        Kind = kind;
        Path = path;
        DisplayName = displayName;
        Channels = channels;
        Window = window;
    }

    public LogSourceKind Kind { get; }

    /// <summary>The channel name or the file path. Empty for the overview.</summary>
    public string Path { get; }

    public string DisplayName { get; }

    /// <summary>The logs the overview covers. Empty for the other kinds.</summary>
    public IReadOnlyList<string> Channels { get; }

    /// <summary>How far back the overview looks.</summary>
    public TimeSpan Window { get; }

    public bool SupportsLiveUpdates => Kind == LogSourceKind.Channel;

    public bool SupportsEvtxExport => Kind is LogSourceKind.Channel or LogSourceKind.File;

    public static LogSource ForChannel(string channel) =>
        new(LogSourceKind.Channel, channel, channel, [], TimeSpan.Zero);

    public static LogSource ForFile(string filePath) =>
        new(LogSourceKind.File, System.IO.Path.GetFullPath(filePath), System.IO.Path.GetFileName(filePath), [], TimeSpan.Zero);

    public static LogSource ForProblemsOverview(IReadOnlyList<string> channels, TimeSpan window) =>
        new(LogSourceKind.ProblemsOverview, "", CoreText.Get("Overview_Name"), channels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), window);

    /// <summary>The queries that read every event of this source, newest first where the log allows it.</summary>
    internal IReadOnlyList<QueryPart> Parts()
    {
        const int reverse = Wevtapi.EvtQueryReverseDirection;
        return Kind switch
        {
            LogSourceKind.Channel => [new QueryPart(Path, "*", Wevtapi.EvtQueryChannelPath | reverse)],
            LogSourceKind.File => [new QueryPart(Path, "*", Wevtapi.EvtQueryFilePath | reverse)],
            _ => BuildOverviewQueries(Channels, Window)
                .Select(xml => new QueryPart(null, xml, Wevtapi.EvtQueryChannelPath | reverse | Wevtapi.EvtQueryTolerateQueryErrors))
                .ToArray(),
        };
    }

    /// <summary>
    /// Builds structured XML queries that select critical events, errors and warnings newer than the window from the
    /// given channels, at most <see cref="ChannelsPerOverviewQuery"/> channels per query.
    /// </summary>
    internal static IReadOnlyList<string> BuildOverviewQueries(IReadOnlyList<string> channels, TimeSpan window)
    {
        long milliseconds = Math.Max(1, (long)window.TotalMilliseconds);
        string filter = "*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) &lt;= "
            + milliseconds.ToString(CultureInfo.InvariantCulture) + "]]]";

        var queries = new List<string>();
        foreach (string[] chunk in channels.Chunk(ChannelsPerOverviewQuery))
        {
            var xml = new StringBuilder("<QueryList><Query Id=\"0\">");
            foreach (string channel in chunk)
                xml.Append("<Select Path=\"").Append(SecurityElement.Escape(channel)).Append("\">").Append(filter).Append("</Select>");
            xml.Append("</Query></QueryList>");
            queries.Add(xml.ToString());
        }
        return queries;
    }
}
