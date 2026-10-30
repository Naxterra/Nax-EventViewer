using System.ComponentModel;
using System.Globalization;

namespace NaxEventViewer.Core;

/// <summary>How an event is shown in the Level column, matching the categories of the Windows Event Viewer.</summary>
public enum EventLevelKind
{
    Critical,
    Error,
    Warning,
    Information,
    Verbose,
    AuditSuccess,
    AuditFailure,
}

/// <summary>
/// One event as shown in the list. The system fields come from the fast first pass. The message and the task name
/// are filled in later by the background pass and raise <see cref="PropertyChanged"/>, which must happen on the UI
/// thread once the item is shown.
/// </summary>
public sealed class EventItem : INotifyPropertyChanged
{
    public const ulong AuditFailureKeyword = 0x0010_0000_0000_0000;
    public const ulong AuditSuccessKeyword = 0x0020_0000_0000_0000;
    private const int PreviewLength = 300;

    // The message is either a plain string (an event formatted on its own) or lines in the load's MessageStore.
    private string? _plainMessage;
    private MessageStore? _store;
    private int[]? _lines;
    private string? _taskName;
    private string? _timeText;
    private string? _eventIdText;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Channel { get; init; }
    public required string Provider { get; init; }
    public string Computer { get; init; } = "";
    public ulong RecordId { get; init; }
    public DateTime TimeCreatedUtc { get; init; }
    public byte Level { get; init; }
    public ushort EventId { get; init; }
    public ushort Task { get; init; }
    public byte Opcode { get; init; }
    public ulong Keywords { get; init; }
    public uint ProcessId { get; init; }
    public uint ThreadId { get; init; }
    public string? UserSid { get; init; }

    public EventLevelKind Kind =>
        (Keywords & AuditFailureKeyword) != 0 ? EventLevelKind.AuditFailure
        : (Keywords & AuditSuccessKeyword) != 0 ? EventLevelKind.AuditSuccess
        : Level switch
        {
            1 => EventLevelKind.Critical,
            2 => EventLevelKind.Error,
            3 => EventLevelKind.Warning,
            5 => EventLevelKind.Verbose,
            _ => EventLevelKind.Information,
        };

    public string LevelText => KindText(Kind);

    public string TimeText => _timeText ??= TimeCreatedUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);

    public string EventIdText => _eventIdText ??= EventId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The full message, or null while the background pass has not reached this event. Built on each call.</summary>
    public string? Message
    {
        get
        {
            if (_plainMessage is { } plain) return plain;
            int[]? lines = Volatile.Read(ref _lines);
            return lines is null ? null : _store!.Join(lines);
        }
    }

    /// <summary>The first line of the message, shortened for the list. Built on each call; only visible rows ask for it.</summary>
    public string MessagePreview
    {
        get
        {
            if (_plainMessage is { } plain) return MakePreview(plain);
            int[]? lines = Volatile.Read(ref _lines);
            return lines is null ? "" : _store!.Preview(lines);
        }
    }

    internal string? PlainMessage => _plainMessage;

    /// <summary>The store and line numbers of the message, or null when the message is plain or not loaded yet.</summary>
    internal (MessageStore Store, int[] Lines)? StoredLines
    {
        get
        {
            int[]? lines = Volatile.Read(ref _lines);
            return lines is null || _plainMessage is not null ? null : (_store!, lines);
        }
    }

    /// <summary>The task category name, or the task number when the provider gives no name.</summary>
    public string TaskName => _taskName ?? (Task == 0 ? "None" : Task.ToString(CultureInfo.InvariantCulture));

    public bool HasMessage => _plainMessage is not null || Volatile.Read(ref _lines) is not null;

    public static string KindText(EventLevelKind kind) => kind switch
    {
        EventLevelKind.Critical => "Critical",
        EventLevelKind.Error => "Error",
        EventLevelKind.Warning => "Warning",
        EventLevelKind.Verbose => "Verbose",
        EventLevelKind.AuditSuccess => "Audit Success",
        EventLevelKind.AuditFailure => "Audit Failure",
        _ => "Information",
    };

    /// <summary>Stores a message formatted on its own, such as by the detail pane. Call this on the UI thread once the item is visible.</summary>
    public void SetFormatted(string message, string? taskName)
    {
        _plainMessage = message;
        Changed(taskName);
    }

    /// <summary>Stores a message kept in the load's <see cref="MessageStore"/>. Call this on the UI thread once the item is visible.</summary>
    public void SetFormatted(StoredMessage message, string? taskName)
    {
        _store = message.Store;
        // Published after the store, so a reader on another thread that sees the lines also sees their store.
        Volatile.Write(ref _lines, message.Lines);
        Changed(taskName);
    }

    private void Changed(string? taskName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MessagePreview)));
        if (taskName is not null && taskName != _taskName)
        {
            _taskName = taskName;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TaskName)));
        }
    }

    /// <summary>Returns the first non-empty line of a message with runs of white space collapsed, at most 300 characters long.</summary>
    public static string MakePreview(string message)
    {
        ReadOnlySpan<char> text = message.AsSpan().Trim();
        int lineEnd = text.IndexOfAny('\r', '\n');
        if (lineEnd >= 0) text = text[..lineEnd].TrimEnd();

        var builder = new System.Text.StringBuilder(Math.Min(text.Length, PreviewLength));
        bool lastWasSpace = false;
        foreach (char c in text)
        {
            if (builder.Length >= PreviewLength) break;
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                builder.Append(c);
                lastWasSpace = false;
            }
        }
        return builder.ToString();
    }

    /// <summary>The key that identifies an event within one query result (record numbers are unique per log).</summary>
    public (string Channel, ulong RecordId) Key => (Channel, RecordId);
}

/// <summary>The formatted text the background pass found for one event.</summary>
public readonly record struct FormattedMessage(EventItem Item, StoredMessage Message, string? TaskName);
