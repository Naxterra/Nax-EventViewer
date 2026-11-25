using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NaxEventViewer.Core;

namespace NaxEventViewer.App;

/// <summary>The events the list shows. Replacing them raises one Reset instead of one change per event.</summary>
public sealed partial class EventCollection : ObservableCollection<EventItem>
{
    public void ReplaceAll(List<EventItem> items)
    {
        var list = (List<EventItem>)Items;
        list.Clear();
        list.AddRange(items);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Inserts a new event at its sorted position.</summary>
    public void InsertSorted(EventItem item, IComparer<EventItem> comparer)
    {
        int index = ((List<EventItem>)Items).BinarySearch(item, comparer);
        Insert(index < 0 ? ~index : index, item);
    }
}

public enum LogNodeKind
{
    Folder,
    Channel,
    File,
    Overview,
}

/// <summary>One entry in the log tree: a folder, a log, a saved file or the problems overview.</summary>
public sealed partial class LogNode : INotifyPropertyChanged
{
    private string _countText = "";
    private bool _isExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LogNode(LogNodeKind kind, string name, string path = "")
    {
        Kind = kind;
        Name = name;
        Path = path;
    }

    public LogNodeKind Kind { get; }
    public string Name { get; }

    /// <summary>The channel name or file path.</summary>
    public string Path { get; }

    public ObservableCollection<LogNode> Children { get; } = [];

    public string Glyph => Kind switch
    {
        LogNodeKind.Folder => "",
        LogNodeKind.File => "",
        LogNodeKind.Overview => "",
        _ => "",
    };

    public string CountText
    {
        get => _countText;
        set
        {
            if (_countText == value) return;
            _countText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public static string FormatCount(long? count) => count is long value ? value.ToString("N0", CultureInfo.CurrentCulture) : "";
}

/// <summary>Icons and colors for the event levels, used by the list and the detail pane.</summary>
public static class LevelVisuals
{
    private static readonly SolidColorBrush CriticalBrush = new(ColorHelper.FromArgb(255, 0xC5, 0x0F, 0x1F));
    private static readonly SolidColorBrush ErrorBrush = new(ColorHelper.FromArgb(255, 0xE8, 0x48, 0x56));
    private static readonly SolidColorBrush WarningBrush = new(ColorHelper.FromArgb(255, 0xE0, 0x9A, 0x00));
    private static readonly SolidColorBrush InformationBrush = new(ColorHelper.FromArgb(255, 0x3A, 0x96, 0xDD));
    private static readonly SolidColorBrush VerboseBrush = new(ColorHelper.FromArgb(255, 0x8A, 0x8A, 0x8A));
    private static readonly SolidColorBrush SuccessBrush = new(ColorHelper.FromArgb(255, 0x16, 0xA0, 0x4A));

    public static string Glyph(EventLevelKind kind) => kind switch
    {
        EventLevelKind.Critical => "",
        EventLevelKind.Error => "",
        EventLevelKind.Warning => "",
        EventLevelKind.Verbose => "",
        EventLevelKind.AuditSuccess => "",
        EventLevelKind.AuditFailure => "",
        _ => "",
    };

    public static Brush Brush(EventLevelKind kind) => kind switch
    {
        EventLevelKind.Critical => CriticalBrush,
        EventLevelKind.Error or EventLevelKind.AuditFailure => ErrorBrush,
        EventLevelKind.Warning => WarningBrush,
        EventLevelKind.Verbose => VerboseBrush,
        EventLevelKind.AuditSuccess => SuccessBrush,
        _ => InformationBrush,
    };
}

/// <summary>Shared column widths bound by both the header row and every realized event row.</summary>
public sealed partial class ColumnWidths : INotifyPropertyChanged
{
    private GridLength _level = new(156);
    private GridLength _time = new(164);
    private GridLength _log = new(0);
    private GridLength _source = new(230);
    private GridLength _eventId = new(72);
    private GridLength _task = new(140);
    private double _savedLogWidth = 170;
    private bool _showLog;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GridLength Level { get => _level; set => Set(ref _level, value, nameof(Level)); }
    public GridLength Time { get => _time; set => Set(ref _time, value, nameof(Time)); }
    public GridLength Log { get => _log; set => Set(ref _log, value, nameof(Log)); }
    public GridLength Source { get => _source; set => Set(ref _source, value, nameof(Source)); }
    public GridLength EventId { get => _eventId; set => Set(ref _eventId, value, nameof(EventId)); }
    public GridLength Task { get => _task; set => Set(ref _task, value, nameof(Task)); }

    public Visibility LogVisibility => _showLog ? Visibility.Visible : Visibility.Collapsed;

    public double SavedLogWidth
    {
        get => _savedLogWidth;
        set
        {
            _savedLogWidth = Math.Clamp(value, 100, 500);
            if (_showLog) Log = new GridLength(_savedLogWidth);
        }
    }

    public bool ShowLog
    {
        get => _showLog;
        set
        {
            if (_showLog == value) return;
            _showLog = value;
            Log = value ? new GridLength(_savedLogWidth) : new GridLength(0);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LogVisibility)));
        }
    }

    private void Set(ref GridLength field, GridLength value, string name)
    {
        if (field.Equals(value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
