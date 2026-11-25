using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using NaxEventViewer.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using VirtualKey = Windows.System.VirtualKey;

namespace NaxEventViewer.App;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan?[] TimeChoices = [null, TimeSpan.FromHours(1), TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(30)];
    private const int CustomTimeIndex = 5;
    private static readonly EventLevelKind[] AllLevels = Enum.GetValues<EventLevelKind>();

    // Events of the current source in arrival order, and the filtered, sorted subset the list shows.
    private readonly List<EventItem> _all = [];
    private readonly EventCollection _view = [];
    private readonly ObservableCollection<LogNode> _roots = [];
    private readonly Dictionary<EventLevelKind, ToggleButton> _levelButtons = [];
    private readonly int[] _levelCounts = new int[AllLevels.Length];
    private readonly Dictionary<string, string> _accountNames = [];
    private readonly HashSet<string> _includedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ColumnWidths _columns;
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _filterDelay;
    private readonly DispatcherQueueTimer _treeFilterDelay;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly Stopwatch _loadWatch = new();
    private readonly Stopwatch _lastRebuild = Stopwatch.StartNew();
    private readonly bool _isElevated;

    private LogNode _overviewNode = null!;
    private LogNode _windowsLogsFolder = null!;
    private LogNode _servicesFolder = null!;
    private LogNode? _filesFolder;
    private List<ChannelInfo>? _channelInfos;
    private LogSource? _source;
    private CancellationTokenSource? _load;
    private LiveSubscription? _live;
    private ReadSummary? _summary;
    private EventFilter _filter = EventFilter.All;
    private EventComparer _comparer = new(SortColumn.Time, descending: true);
    private TimeSpan? _timeWindow;
    private DateTime? _customSinceUtc;
    private DateTime? _customUntilUtc;
    private int _lastTimeIndex;
    private bool _suppressFilterEvents;
    private ConcurrentQueue<EventItem> _livePending = new();
    private MessageStore _messages = new();
    private int _liveWakeQueued;
    private int _loadVersion;
    private int _droppedSinceCompact;
    private bool _compactingMessages;
    private bool _viewDirty;
    private bool _rebuilding;
    private bool _rebuildRunning;
    private bool _rebuildAgain;
    private bool _rebuildAgainKeepsScroll;
    private ScrollViewer? _listScroller;
    private bool _recordsLoading;
    private bool _messagesLoading;
    private int _messagesFormatted;
    private int _missingMessages;
    private bool _idsInvalid;
    // How many events at the start of _all came from reading the log, in which order, and where watching resumes.
    private int _readCount;
    private bool _readNewestFirst = true;
    private string? _resumeBookmark;
    private TimeSpan _recordsElapsed;
    private EventItem? _detailItem;
    private EventDetail? _detail;
    private string? _detailUser;
    private int _detailVersion;

    public MainWindow()
    {
        InitializeComponent();
        _columns = (ColumnWidths)Application.Current.Resources["ColumnWidths"];
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Nax-EventViewer.ico"));

        ApplySavedLayout();
        WireSplitters();

        _isElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        ElevationText.Visibility = _isElevated ? Visibility.Visible : Visibility.Collapsed;
        AdminButton.Visibility = _isElevated ? Visibility.Collapsed : Visibility.Visible;

        EventList.ItemsSource = _view;
        LogTree.ItemsSource = _roots;
        LocalizeStaticText();
        ShowAllLogsBox.IsChecked = Program.Settings.ShowAllLogs;
        CreateLevelButtons();
        UpdateSortHeaders();

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.Tick += (_, _) => OnTick();
        _filterDelay = DispatcherQueue.CreateTimer();
        _filterDelay.Interval = TimeSpan.FromMilliseconds(200);
        _filterDelay.IsRepeating = false;
        _filterDelay.Tick += (_, _) => ApplyFilter();
        _treeFilterDelay = DispatcherQueue.CreateTimer();
        _treeFilterDelay.Interval = TimeSpan.FromMilliseconds(180);
        _treeFilterDelay.IsRepeating = false;
        _treeFilterDelay.Tick += (_, _) => RebuildServicesTree();
        // A "last hour" filter moves along with the clock, and a watched log drops events the log itself overwrote.
        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromMinutes(1);
        _clockTimer.Tick += (_, _) =>
        {
            if (_timeWindow is not null) RequestRebuild(keepScroll: true);
            DropOverwrittenEvents();
            RunningMutex.Hold();
        };
        _clockTimer.Start();

        BuildTree();
        LoadChannelInfoInBackground();
        OpenStartupTarget(Program.Startup);
        AppWindow.Closing += (_, args) =>
        {
            if (RefuseWhileClearing())
            {
                args.Cancel = true;
                return;
            }
            if (!_skipSaveLayoutOnClose) SaveWindowLayout();
        };
        Closed += (_, _) => OnClosed();
    }

    private void ApplySavedLayout()
    {
        AppSettings settings = Program.Settings;
        AppWindow.Resize(new SizeInt32(settings.WindowWidth, settings.WindowHeight));
        if (settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();

        TreeColumn.Width = new GridLength(settings.TreePaneWidth);
        DetailRow.Height = new GridLength(settings.DetailPaneHeight);
        FieldsColumn.Width = new GridLength(settings.FieldsPaneWidth);
        _columns.Level = new GridLength(settings.LevelColumnWidth);
        _columns.Time = new GridLength(settings.TimeColumnWidth);
        _columns.SavedLogWidth = settings.LogColumnWidth;
        _columns.Source = new GridLength(settings.SourceColumnWidth);
        _columns.EventId = new GridLength(settings.EventIdColumnWidth);
        _columns.Task = new GridLength(settings.TaskColumnWidth);
    }

    private bool _skipSaveLayoutOnClose;

    private void CaptureWindowLayout()
    {
        AppSettings settings = Program.Settings;
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        settings.WindowMaximized = maximized;
        if (!maximized)
        {
            SizeInt32 size = AppWindow.Size;
            if (size.Width >= 800 && size.Height >= 560)
            {
                settings.WindowWidth = size.Width;
                settings.WindowHeight = size.Height;
            }
        }
        settings.TreePaneWidth = TreeColumn.Width.Value;
        settings.DetailPaneHeight = DetailRow.Height.Value;
        settings.FieldsPaneWidth = FieldsColumn.Width.Value;
        settings.LevelColumnWidth = _columns.Level.Value;
        settings.TimeColumnWidth = _columns.Time.Value;
        settings.LogColumnWidth = _columns.SavedLogWidth;
        settings.SourceColumnWidth = _columns.Source.Value;
        settings.EventIdColumnWidth = _columns.EventId.Value;
        settings.TaskColumnWidth = _columns.Task.Value;
    }

    private void SaveWindowLayout()
    {
        CaptureWindowLayout();
        Program.Settings.TrySave();
    }

    private void WireSplitters()
    {
        TreeSplitter.DragDelta += delta => TreeColumn.Width = new GridLength(Math.Clamp(TreeColumn.ActualWidth + delta, 180, 700));
        TreeSplitter.ResetRequested += () => { TreeColumn.Width = new GridLength(300); SaveWindowLayout(); };
        TreeSplitter.DragCompleted += SaveWindowLayout;

        DetailSplitter.DragDelta += delta => DetailRow.Height = new GridLength(Math.Clamp(DetailRow.ActualHeight - delta, 140, 1000));
        DetailSplitter.ResetRequested += () => { DetailRow.Height = new GridLength(340); SaveWindowLayout(); };
        DetailSplitter.DragCompleted += SaveWindowLayout;

        FieldsSplitter.DragDelta += delta => FieldsColumn.Width = new GridLength(Math.Clamp(FieldsColumn.ActualWidth - delta, 220, 900));
        FieldsSplitter.ResetRequested += () => { FieldsColumn.Width = new GridLength(400); SaveWindowLayout(); };
        FieldsSplitter.DragCompleted += SaveWindowLayout;

        void WireColumn(PaneSplitter splitter, Func<double> get, Action<double> set, double min, double max, double fallback)
        {
            splitter.DragDelta += delta => set(Math.Clamp(get() + delta, min, max));
            splitter.ResetRequested += () => { set(fallback); SaveWindowLayout(); };
            splitter.DragCompleted += SaveWindowLayout;
        }
        WireColumn(LevelColSplitter, () => _columns.Level.Value, v => _columns.Level = new GridLength(v), 90, 400, 156);
        WireColumn(TimeColSplitter, () => _columns.Time.Value, v => _columns.Time = new GridLength(v), 110, 400, 164);
        WireColumn(LogColSplitter, () => _columns.SavedLogWidth, v => _columns.SavedLogWidth = v, 100, 500, 170);
        WireColumn(SourceColSplitter, () => _columns.Source.Value, v => _columns.Source = new GridLength(v), 100, 600, 230);
        WireColumn(EventIdColSplitter, () => _columns.EventId.Value, v => _columns.EventId = new GridLength(v), 56, 200, 72);
        WireColumn(TaskColSplitter, () => _columns.Task.Value, v => _columns.Task = new GridLength(v), 80, 500, 140);
    }

    /// <summary>
    /// True while logs are being emptied. Ending the app then could stop a clear between emptying a log and moving its
    /// copy out of the temporary folder, so closing and restarting wait until the clear has finished.
    /// </summary>
    private bool _clearRunning;

    private bool RefuseWhileClearing()
    {
        if (_clearRunning) ShowProblem(Loc.Get("Clear_WaitBeforeClosing"), offerAdmin: false);
        return _clearRunning;
    }

    /// <summary>
    /// Set once the window is closed. Work that finishes afterwards, such as a list rebuild, a timer tick or a batch
    /// from the background reader, must not touch the controls any more: XAML is shutting down, and changing a control
    /// then ends the process with an error.
    /// </summary>
    private bool _closed;

    private void OnClosed()
    {
        _closed = true;
        _tick.Stop();
        _filterDelay.Stop();
        _treeFilterDelay.Stop();
        _clockTimer.Stop();
        StopLoading();
    }

    /// <summary>Fills in the fixed labels of the window in the interface language.</summary>
    private void LocalizeStaticText()
    {
        ElevationText.Text = Loc.Get("Elevation_Administrator");
        SearchBox.PlaceholderText = Loc.Get("Search_Placeholder");
        ToolTipService.SetToolTip(SearchBox, Loc.Get("Search_ToolTip"));
        EventIdBox.PlaceholderText = Loc.Get("EventIds_Placeholder");
        ToolTipService.SetToolTip(EventIdBox, Loc.Get("EventIds_ToolTip"));
        _suppressFilterEvents = true;
        foreach (string key in new[] { "Time_Any", "Time_Hour", "Time_Day", "Time_Week", "Time_Month", "Time_Custom" })
            TimeBox.Items.Add(Loc.Get(key));
        TimeBox.SelectedIndex = 0;
        _suppressFilterEvents = false;
        ResetFilterText.Text = Loc.Get("Filter_Reset");
        ToolTipService.SetToolTip(ResetFilterButton, Loc.Get("Filter_ResetTip"));

        void Command(AppBarButton button, string label, string? tip = null)
        {
            button.Label = Loc.Get(label);
            if (tip is not null) ToolTipService.SetToolTip(button, Loc.Get(tip));
        }
        Command(RefreshButton, "Command_Refresh", "Command_RefreshTip");
        LiveToggle.Label = Loc.Get("Command_Live");
        ToolTipService.SetToolTip(LiveToggle, Loc.Get("Command_LiveTip"));
        Command(OpenButton, "Command_Open", "Command_OpenTip");
        Command(SaveButton, "Command_Save");
        SaveCsvItem.Text = Loc.Get("Command_SaveCsv");
        SaveCsvItem.KeyboardAcceleratorTextOverride = Loc.Get("Command_SaveCsvKey");
        SaveEvtxItem.Text = Loc.Get("Command_SaveEvtx");
        Command(ClearButton, "Command_Clear", "Command_ClearTip");
        Command(AdminButton, "Command_Admin", "Command_AdminTip");
        Command(SettingsButton, "Command_Settings");
        OverviewWindowMenu.Text = Loc.Get("Overview_WindowMenu");
        Overview1hItem.Text = Loc.Get("Overview_1h");
        Overview24hItem.Text = Loc.Get("Overview_24h");
        Overview7dItem.Text = Loc.Get("Overview_7d");
        Overview30dItem.Text = Loc.Get("Overview_30d");
        LanguageMenu.Text = Loc.Get("Settings_Language");
        LanguageSystemItem.Text = Loc.Get("Language_System");
        LanguageEnglishItem.Text = Loc.Get("Language_English");
        LanguageGermanItem.Text = Loc.Get("Language_German");
        ReplaceItem.Text = Loc.Get("Settings_Replace");

        ProblemAdminButton.Content = Loc.Get("Problem_RestartAdmin");
        TreeSearchBox.PlaceholderText = Loc.Get("Tree_SearchPlaceholder");
        ShowAllLogsBox.Content = Loc.Get("Tree_ShowAll");
        MessageHeader.Text = Loc.Get("Column_Message");
        DetailEmptyText.Text = Loc.Get("Detail_SelectEvent");
        GeneralTab.Text = Loc.Get("Detail_General");
        DataTab.Text = Loc.Get("Detail_Data");
        DataEmptyText.Text = Loc.Get("Detail_NoData");
        XmlTab.Text = Loc.Get("Detail_Xml");
        ToolTipService.SetToolTip(CopyDetailButton, Loc.Get("Detail_CopyTip"));
        CopyAllDetailsItem.Text = Loc.Get("Detail_CopyAll");
        CopyMessageOnlyItem.Text = Loc.Get("Detail_CopyMessage");
        CopyXmlOnlyItem.Text = Loc.Get("Detail_CopyXml");
    }

    // ----- Settings -----

    private void SettingsMenu_Opening(object sender, object e)
    {
        int hours = Program.Settings.OverviewHours;
        Overview1hItem.IsChecked = hours == 1;
        Overview24hItem.IsChecked = hours == 24;
        Overview7dItem.IsChecked = hours == 168;
        Overview30dItem.IsChecked = hours == 720;

        string language = Program.Settings.Language;
        LanguageSystemItem.IsChecked = language is not (AppSettings.English or AppSettings.German);
        LanguageEnglishItem.IsChecked = language == AppSettings.English;
        LanguageGermanItem.IsChecked = language == AppSettings.German;

        // The replacement can only point at a copy installed for all users, in a folder only administrators can change.
        string? self = Environment.ProcessPath;
        ReplaceItem.IsChecked = self is not null && EventViewerReplacement.IsReplacedBy(self);
        ReplaceItem.IsEnabled = self is not null && (ReplaceItem.IsChecked || EventViewerReplacement.CanReplaceWith(self));
        ToolTipService.SetToolTip(ReplaceItem, ReplaceItem.IsEnabled ? null : CoreText.Get("Replace_NeedsMachineInstall"));
    }

    private void OverviewWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string raw } || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int hours)) return;
        if (hours == Program.Settings.OverviewHours) return;
        Program.Settings.OverviewHours = hours;
        Program.Settings.TrySave();
        UpdateOverviewNodeLabel();
        if (_source?.Kind == LogSourceKind.ProblemsOverview) Open(CreateOverviewSource());
    }

    private string OverviewWindowLabel() => Program.Settings.OverviewHours switch
    {
        1 => Loc.Get("Overview_1h"),
        168 => Loc.Get("Overview_7d"),
        720 => Loc.Get("Overview_30d"),
        _ => Loc.Get("Overview_24h"),
    };

    private void UpdateOverviewNodeLabel()
    {
        int index = _roots.IndexOf(_overviewNode);
        if (index < 0) return;
        bool wasSelected = LogTree.SelectedItem == _overviewNode;
        _overviewNode = new LogNode(LogNodeKind.Overview, Loc.F("Tree_OverviewRange", OverviewWindowLabel()));
        _roots[index] = _overviewNode;
        if (wasSelected) LogTree.SelectedItem = _overviewNode;
    }

    /// <summary>Saves the chosen language and restarts the app in it, on the same log.</summary>
    private void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string language } || language == Program.Settings.Language || RefuseWhileClearing()) return;
        string previous = Program.Settings.Language;
        try
        {
            CaptureWindowLayout();
            Program.Settings.Language = language;
            Program.Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not saved, so not chosen: the menu keeps showing the language the app runs in.
            Program.Settings.Language = previous;
            ShowProblem(Loc.F("Replace_Failed", ex.Message), offerAdmin: false);
            return;
        }
        if (Environment.ProcessPath is not { } executable) return;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (string argument in StartupArguments.For(_source)) start.ArgumentList.Add(argument);
        try
        {
            using Process? started = Process.Start(start);
            if (started is not null)
            {
                _skipSaveLayoutOnClose = true;
                Close();
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowProblem(Loc.F("Replace_Failed", ex.Message), offerAdmin: false);
        }
    }

    /// <summary>Turns the replacement of the Windows Event Viewer on or off through a short-lived elevated copy of the app.</summary>
    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        bool enable = ReplaceItem.IsChecked;
        try
        {
            if (Environment.ProcessPath is not { } self) return;
            if (enable && EventViewerReplacement.OtherTarget(self) is { } other)
            {
                ShowProblem(Loc.F("Replace_Other", Path.GetFileName(other)), offerAdmin: false);
                return;
            }
            var start = new ProcessStartInfo(self) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(enable ? Program.ReplaceOnArgument : Program.ReplaceOffArgument);
            start.ArgumentList.Add(self);
            int exitCode;
            try
            {
                using Process helper = Process.Start(start) ?? throw new InvalidOperationException(Loc.Get("Replace_Cancelled"));
                await helper.WaitForExitAsync();
                exitCode = helper.ExitCode;
                if (_closed) return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                ShowProblem(Loc.Get("Replace_Cancelled"), offerAdmin: false);
                return;
            }
            if (exitCode == 0) ShowInfo(Loc.Get(enable ? "Replace_On" : "Replace_Off"));
            else if (exitCode == Program.HelperNotAdminOnlyFolder) ShowProblem(CoreText.Get("Replace_NeedsMachineInstall"), offerAdmin: false);
            else ShowProblem(Loc.F("Replace_ExitCode", exitCode), offerAdmin: false);
        }
        catch (Exception ex)
        {
            // This is an async void handler, so an exception that escaped here would end the app.
            CrashLog.Write(ex);
            ShowProblem(Loc.F("Replace_Failed", ex.Message), offerAdmin: false);
        }
    }

    // ----- Drag and drop -----

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.Get("Drop_OpenEvtx");
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        DragOperationDeferral? deferral = null;
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            deferral = e.GetDeferral();
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            if (_closed) return;
            string? lastOpened = null;
            foreach (IStorageItem item in items)
            {
                if (item is StorageFile file && file.Path.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase))
                {
                    AddFileNode(file.Path);
                    lastOpened = file.Path;
                }
            }
            if (lastOpened is not null) OpenFile(lastOpened);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ShowProblem(Loc.F("Problem_OpenFailed", ex.Message), offerAdmin: false);
        }
        finally
        {
            deferral?.Complete();
        }
    }

    // ----- Log tree -----

    private void BuildTree()
    {
        _overviewNode = new LogNode(LogNodeKind.Overview, Loc.F("Tree_OverviewRange", OverviewWindowLabel()));
        _roots.Add(_overviewNode);
        _windowsLogsFolder = new LogNode(LogNodeKind.Folder, Loc.Get("Tree_WindowsLogs")) { IsExpanded = true };
        foreach (string channel in ChannelCatalog.WindowsLogs)
            _windowsLogsFolder.Children.Add(new LogNode(LogNodeKind.Channel, channel, channel));
        _roots.Add(_windowsLogsFolder);
        _servicesFolder = new LogNode(LogNodeKind.Folder, Loc.Get("Tree_Services"));
        _servicesFolder.Children.Add(new LogNode(LogNodeKind.Folder, Loc.Get("Tree_Loading")));
        _roots.Add(_servicesFolder);
    }

    /// <summary>Lists every log with its size and type off the UI thread; the tree fills in when this is done.</summary>
    private void LoadChannelInfoInBackground()
    {
        Task.Run(() =>
        {
            List<string> channels = ChannelCatalog.ListChannels();
            return channels.AsParallel().AsOrdered().WithDegreeOfParallelism(4).Select(ChannelCatalog.ReadInfo).ToList();
        }).ContinueWith(task =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;
                if (task.IsFaulted)
                {
                    _servicesFolder.Children.Clear();
                    ShowProblem(task.Exception?.InnerException?.Message ?? CoreText.Get("Error_ListLogs"), offerAdmin: false);
                    return;
                }
                _channelInfos = task.Result;
                RebuildServicesTree();
                // An overview opened before the list was known covered only the Windows logs.
                if (_source?.Kind == LogSourceKind.ProblemsOverview) Open(CreateOverviewSource());
            });
        }, TaskScheduler.Default);
    }

    private void TreeSearch_Changed(object sender, TextChangedEventArgs e)
    {
        _treeFilterDelay.Stop();
        _treeFilterDelay.Start();
    }

    private void RebuildServicesTree()
    {
        if (_channelInfos is null) return;
        string search = TreeSearchBox.Text.Trim();
        bool filtering = search.Length > 0;
        bool showAll = ShowAllLogsBox.IsChecked == true;
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpanded(_servicesFolder, "", expanded);

        var infoByName = _channelInfos.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

        _windowsLogsFolder.Children.Clear();
        foreach (string channel in ChannelCatalog.WindowsLogs)
        {
            infoByName.TryGetValue(channel, out ChannelInfo? info);
            if (!filtering && !showAll && channel == "ForwardedEvents" && info?.RecordCount is null or 0) continue;
            if (filtering && !channel.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            _windowsLogsFolder.Children.Add(new LogNode(LogNodeKind.Channel, channel, channel) { CountText = LogNode.FormatCount(info?.RecordCount) });
        }

        var shown = _channelInfos.Where(i => !ChannelCatalog.WindowsLogs.Contains(i.Name, StringComparer.OrdinalIgnoreCase)
            && (showAll || (!i.IsAnalyticOrDebug && i.RecordCount is not 0))
            && (!filtering || i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));
        ChannelFolder tree = ChannelTree.Build(shown.Select(i => i.Name));
        _servicesFolder.Children.Clear();
        AddFolderContents(_servicesFolder, tree, infoByName, "", expanded, expandAll: filtering);
        if (filtering && _servicesFolder.Children.Count > 0) _servicesFolder.IsExpanded = true;
        SyncTreeSelection(_source);
    }

    private static void CollectExpanded(LogNode folder, string prefix, HashSet<string> result)
    {
        foreach (LogNode child in folder.Children)
        {
            if (child.Kind != LogNodeKind.Folder) continue;
            string key = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
            if (child.IsExpanded) result.Add(key);
            CollectExpanded(child, key, result);
        }
    }

    private static void AddFolderContents(LogNode node, ChannelFolder folder, Dictionary<string, ChannelInfo> infoByName, string prefix, HashSet<string> expanded, bool expandAll)
    {
        foreach (ChannelFolder child in folder.Folders)
        {
            string key = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
            var childNode = new LogNode(LogNodeKind.Folder, child.Name) { IsExpanded = expandAll || expanded.Contains(key) };
            AddFolderContents(childNode, child, infoByName, key, expanded, expandAll);
            node.Children.Add(childNode);
        }
        foreach (var (name, channel) in folder.Channels)
        {
            infoByName.TryGetValue(channel, out ChannelInfo? info);
            node.Children.Add(new LogNode(LogNodeKind.Channel, name, channel) { CountText = LogNode.FormatCount(info?.RecordCount) });
        }
    }

    private void SyncTreeSelection(LogSource? source)
    {
        if (source is null) return;
        LogNode? target = source.Kind switch
        {
            LogSourceKind.ProblemsOverview => _overviewNode,
            LogSourceKind.Channel => FindChannelNode(_roots, source.Path, expandParents: true),
            LogSourceKind.File => _filesFolder?.Children.FirstOrDefault(n => string.Equals(n.Path, source.Path, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        if (target is not null && LogTree.SelectedItem != target)
            LogTree.SelectedItem = target;
    }

    private static LogNode? FindChannelNode(IEnumerable<LogNode> nodes, string channel, bool expandParents)
    {
        foreach (LogNode node in nodes)
        {
            if (node.Kind == LogNodeKind.Channel && string.Equals(node.Path, channel, StringComparison.OrdinalIgnoreCase)) return node;
            if (FindChannelNode(node.Children, channel, expandParents) is { } found)
            {
                if (expandParents && node.Kind == LogNodeKind.Folder) node.IsExpanded = true;
                return found;
            }
        }
        return null;
    }

    private void ShowAllLogs_Click(object sender, RoutedEventArgs e)
    {
        Program.Settings.ShowAllLogs = ShowAllLogsBox.IsChecked == true;
        Program.Settings.TrySave();
        RebuildServicesTree();
    }

    private void LogTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not LogNode node) return;
        switch (node.Kind)
        {
            case LogNodeKind.Folder:
                node.IsExpanded = !node.IsExpanded;
                break;
            case LogNodeKind.Channel:
                Open(LogSource.ForChannel(node.Path));
                break;
            case LogNodeKind.File:
                Open(LogSource.ForFile(node.Path));
                break;
            case LogNodeKind.Overview:
                Open(CreateOverviewSource());
                break;
        }
    }

    /// <summary>The right-click menu of a log, a saved file, a folder, or the overview.</summary>
    private void LogTree_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (FindAncestor<TreeViewItem>(args.OriginalSource as DependencyObject)?.DataContext is not LogNode node) return;
        var menu = new MenuFlyout();
        switch (node.Kind)
        {
            case LogNodeKind.Channel:
                // Saving and clearing act on the log that was right-clicked, which need not be the open one.
                LogSource source = SourceForChannel(node.Path);
                menu.Items.Add(MenuItem("Menu_Open", "\uE8E5", (_, _) => Open(SourceForChannel(node.Path))));
                menu.Items.Add(MenuItem("Menu_SaveEvtx", "\uE74E", SaveEvtx_Click, source));
                menu.Items.Add(MenuItem("Menu_ClearLog", "\uE74D", Clear_Click, new ClearRequest(source.DisplayName, [source], IsFolder: false), enabled: !_clearing));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MenuItem("Menu_CopyLogName", "\uE8C8", (_, _) => CopyText(node.Path)));
                break;
            case LogNodeKind.File:
                menu.Items.Add(MenuItem("Menu_Open", "\uE8E5", (_, _) => Open(LogSource.ForFile(node.Path))));
                menu.Items.Add(MenuItem("Menu_CloseFile", "\uE711", (_, _) => RemoveFileNode(node)));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MenuItem("Menu_CopyPath", "\uE8C8", (_, _) => CopyText(node.Path)));
                menu.Items.Add(MenuItem("Menu_ShowInFolder", "\uED25", (_, _) => ShowInFolder(node.Path)));
                break;
            case LogNodeKind.Overview:
                menu.Items.Add(MenuItem("Menu_Open", "\uE8E5", (_, _) => Open(CreateOverviewSource())));
                break;
            case LogNodeKind.Folder when node.Children.Count > 0:
                menu.Items.Add(node.IsExpanded
                    ? MenuItem("Menu_Collapse", "\uE70E", (_, _) => node.IsExpanded = false)
                    : MenuItem("Menu_Expand", "\uE70D", (_, _) => node.IsExpanded = true));
                menu.Items.Add(MenuItem("Menu_ExpandAll", "\uE70D", (_, _) => SetExpanded(node, true)));
                menu.Items.Add(MenuItem("Menu_CollapseAll", "\uE70E", (_, _) => SetExpanded(node, false)));
                // Clearing a folder covers the logs the tree shows in it, at every level below.
                List<LogSource> folderLogs = [.. Descendants(node).Where(n => n.Kind == LogNodeKind.Channel)
                    .Select(n => n.Path).Distinct(StringComparer.OrdinalIgnoreCase).Select(SourceForChannel)];
                if (folderLogs.Count > 0)
                {
                    menu.Items.Add(new MenuFlyoutSeparator());
                    menu.Items.Add(MenuItem("Menu_ClearFolder", "\uE74D", Clear_Click, new ClearRequest(node.Name, folderLogs, IsFolder: true), enabled: !_clearing));
                }
                break;
            default:
                return;
        }
        args.Handled = true;
        ShowMenu(menu, sender, args);
    }

    private void RemoveFileNode(LogNode node)
    {
        if (_filesFolder is null) return;
        _filesFolder.Children.Remove(node);
        if (_filesFolder.Children.Count == 0)
        {
            _roots.Remove(_filesFolder);
            _filesFolder = null;
        }
        if (_source is { Kind: LogSourceKind.File } open && string.Equals(open.Path, node.Path, StringComparison.OrdinalIgnoreCase))
            Open(LogSource.ForChannel("System"));
    }

    private static IEnumerable<LogNode> Descendants(LogNode node)
    {
        foreach (LogNode child in node.Children)
        {
            yield return child;
            foreach (LogNode below in Descendants(child)) yield return below;
        }
    }

    /// <summary>Opens or closes a folder together with every folder inside it.</summary>
    private static void SetExpanded(LogNode folder, bool expanded)
    {
        folder.IsExpanded = expanded;
        foreach (LogNode child in folder.Children)
            if (child.Kind == LogNodeKind.Folder) SetExpanded(child, expanded);
    }

    /// <summary>The open log if it is this channel, so that commands on it also update the list; otherwise a new source.</summary>
    private LogSource SourceForChannel(string channel) =>
        _source is { Kind: LogSourceKind.Channel } open && string.Equals(open.Path, channel, StringComparison.OrdinalIgnoreCase)
            ? open
            : LogSource.ForChannel(channel);

    private void ShowInFolder(string path)
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using Process? process = Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            ShowProblem(Loc.F("Problem_OpenFailed", ex.Message), offerAdmin: false);
        }
    }

    /// <summary>Reads the number of events of a log again and updates both the tree and cached channel metadata.</summary>
    private void RecountLog(string channel)
    {
        Task.Run(() => ChannelCatalog.CountRecords(channel)).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;
                long? updated = task.Result;
                if (_channelInfos is not null)
                {
                    int index = _channelInfos.FindIndex(i => string.Equals(i.Name, channel, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) _channelInfos[index] = _channelInfos[index] with { RecordCount = updated };
                }
                foreach (LogNode node in ChannelNodes(_roots, channel)) node.CountText = LogNode.FormatCount(updated);
            });
        }, TaskScheduler.Default);
    }

    private static IEnumerable<LogNode> ChannelNodes(IEnumerable<LogNode> nodes, string channel)
    {
        foreach (LogNode node in nodes)
        {
            if (node.Kind == LogNodeKind.Channel && string.Equals(node.Path, channel, StringComparison.OrdinalIgnoreCase)) yield return node;
            foreach (LogNode match in ChannelNodes(node.Children, channel)) yield return match;
        }
    }

    // ----- Right-click menus -----

    private static MenuFlyoutItem MenuItem(string textKey, string glyph, RoutedEventHandler click, object? tag = null, bool enabled = true) =>
        MenuItemWithText(Loc.Get(textKey), glyph, click, tag, enabled);

    private static MenuFlyoutItem MenuItemWithText(string text, string glyph, RoutedEventHandler click, object? tag = null, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, Tag = tag, IsEnabled = enabled };
        item.Click += click;
        return item;
    }

    /// <summary>Shows a menu at the pointer, or next to the element when the menu key or Shift+F10 opened it.</summary>
    private static void ShowMenu(MenuFlyout menu, UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.TryGetPosition(sender, out Windows.Foundation.Point point)) menu.ShowAt(sender, point);
        else menu.ShowAt(args.OriginalSource as FrameworkElement ?? (FrameworkElement)sender);
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is T match) return match;
        return null;
    }

    /// <summary>The overview covers the Windows logs and every enabled administrative log that is not known to be empty.</summary>
    private LogSource CreateOverviewSource()
    {
        IEnumerable<string> channels = ChannelCatalog.WindowsLogs;
        if (_channelInfos is not null)
            channels = channels.Concat(_channelInfos.Where(i => i.Type == ChannelType.Admin && i.Enabled && i.RecordCount is not 0).Select(i => i.Name));
        return LogSource.ForProblemsOverview(channels.ToList(), TimeSpan.FromHours(Program.Settings.OverviewHours));
    }

    private void OpenStartupTarget(StartupTarget? target)
    {
        switch (target?.Kind)
        {
            case LogSourceKind.File:
                if (!TryOpenFile(target.Path)) Open(LogSource.ForChannel("System"));
                break;
            case LogSourceKind.ProblemsOverview:
                Open(CreateOverviewSource());
                break;
            case LogSourceKind.Channel:
                Open(LogSource.ForChannel(target.Path));
                break;
            default:
                Open(LogSource.ForChannel("System"));
                break;
        }
    }

    private void OpenFile(string path) => TryOpenFile(path);

    private bool TryOpenFile(string path)
    {
        LogSource source;
        try
        {
            source = LogSource.ForFile(path);
            if (source.DisplayName.Length == 0 || Directory.Exists(source.Path))
                throw new ArgumentException(CoreText.F("Error_Open", path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowProblem(Loc.F("Problem_OpenFailed", ex.Message), offerAdmin: false);
            return false;
        }
        AddFileNode(source.Path);
        Open(source);
        return true;
    }

    private void AddFileNode(string path)
    {
        LogSource source;
        try
        {
            source = LogSource.ForFile(path);
            if (source.DisplayName.Length == 0 || Directory.Exists(source.Path)) return;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        if (_filesFolder is null)
        {
            _filesFolder = new LogNode(LogNodeKind.Folder, Loc.Get("Tree_SavedLogs")) { IsExpanded = true };
            _roots.Add(_filesFolder);
        }
        if (!_filesFolder.Children.Any(n => string.Equals(n.Path, source.Path, StringComparison.OrdinalIgnoreCase)))
        {
            var node = new LogNode(LogNodeKind.File, source.DisplayName, source.Path);
            _filesFolder.Children.Add(node);
            // Counting opens the file, which can take a while on a slow network share, so it runs off the UI thread.
            Task.Run(() => ChannelCatalog.CountRecords(source.Path, isFile: true)).ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully) DispatcherQueue.TryEnqueue(() => { if (!_closed) node.CountText = LogNode.FormatCount(task.Result); });
            }, TaskScheduler.Default);
        }
    }

    // ----- Loading -----

    private void Open(LogSource source)
    {
        StopLoading();
        _source = source;
        _loadVersion++;
        _droppedSinceCompact = 0;
        _compactingMessages = false;
        _livePending = new ConcurrentQueue<EventItem>();
        _viewDirty = false;
        _all.Clear();
        _view.ReplaceAll([]);
        Array.Clear(_levelCounts);
        UpdateLevelButtons();
        ShowDetail(null);
        ProblemBar.IsOpen = false;
        _summary = null;
        _messagesFormatted = 0;
        _missingMessages = 0;
        _readCount = 0;
        _resumeBookmark = null;
        _recordsLoading = true;
        _messagesLoading = true;
        _loadWatch.Restart();
        _columns.ShowLog = source.Kind == LogSourceKind.ProblemsOverview
            || string.Equals(source.Path, "ForwardedEvents", StringComparison.OrdinalIgnoreCase);
        if (!_columns.ShowLog && _comparer.Column == SortColumn.Log)
        {
            _comparer = new EventComparer(SortColumn.Time, descending: true);
            UpdateSortHeaders();
        }
        LogTitle.Text = source.Kind == LogSourceKind.File ? Loc.F("Title_SavedFile", source.DisplayName)
            : source.Kind == LogSourceKind.ProblemsOverview ? Loc.F("Tree_OverviewRange", OverviewWindowLabel())
            : source.DisplayName;
        Title = $"{source.DisplayName} - Nax Event Viewer";
        SaveEvtxItem.IsEnabled = source.SupportsEvtxExport;
        // Only a single log on this PC can be cleared; a saved file or the overview cannot.
        ClearButton.IsEnabled = source.Kind == LogSourceKind.Channel;
        LiveToggle.IsEnabled = source.SupportsLiveUpdates;
        SyncTreeSelection(source);

        var load = new CancellationTokenSource();
        _load = load;
        CancellationToken token = load.Token;
        DispatcherQueue queue = DispatcherQueue;
        void Post(Action action) => queue.TryEnqueue(() =>
        {
            if (!token.IsCancellationRequested && !_closed) action();
        });

        _messages = new MessageStore();
        LogLoader.Start(source, _messages, new LoadCallbacks
        {
            RecordsRead = batch => Post(() => OnRecordsRead(batch)),
            RecordsDone = summary => Post(() => OnRecordsDone(summary)),
            MessagesFormatted = batch => Post(() => OnMessagesFormatted(batch)),
            MessagesDone = () => Post(OnMessagesDone),
            UnexpectedError = CrashLog.Write,
        }, token);
        _tick.Start();
        UpdateStatus();
    }

    private void StopLoading()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
        _live?.Dispose();
        _live = null;
    }

    private void OnRecordsRead(IReadOnlyList<EventItem> batch)
    {
        bool first = _all.Count == 0;
        _all.AddRange(batch);
        _viewDirty = true;
        // The first batch is shown at once; later batches wait for the tick, which rebuilds at most once a second.
        if (first) RequestRebuild(keepScroll: true);
    }

    private void OnRecordsDone(ReadSummary summary)
    {
        _recordsLoading = false;
        _recordsElapsed = _loadWatch.Elapsed;
        _summary = summary;
        _readCount = _all.Count;
        _readNewestFirst = summary.NewestFirst;
        _resumeBookmark = summary.NewestBookmark;
        RequestRebuild(keepScroll: true);
        if (summary.LeftOut.Count > 0)
            ShowProblem(Loc.F(summary.LeftOut.Count == 1 ? "Problem_LeftOutOne" : "Problem_LeftOutMany", string.Join(", ", summary.LeftOut)), offerAdmin: !_isElevated);
        else if (summary.AccessDenied)
            ShowProblem(Loc.F("Problem_AdminOnly", _source?.DisplayName), offerAdmin: !_isElevated);
        else if (summary.Problems.Count > 0)
            ShowProblem(string.Join(" ", summary.Problems.Distinct().Take(3)), offerAdmin: false);

        // Watching a log that could not be read would only fail again and replace the message above.
        bool readFailed = summary.AccessDenied || (summary.Count == 0 && summary.Problems.Count > 0);
        if (!readFailed) StartLive();
        UpdateStatus();
    }

    private void OnMessagesFormatted(IReadOnlyList<FormattedMessage> batch)
    {
        foreach (FormattedMessage message in batch)
        {
            message.Item.SetFormatted(message.Message, message.TaskName);
            if (message.Item == _detailItem && _detail is null) MessageText.Text = message.Message.ToString();
        }
        _messagesFormatted += batch.Count;
        // New text can change which events match a search, and new task names change the task column order.
        if (_filter.HasText || _comparer.Column == SortColumn.Task) _viewDirty = true;
    }

    private void OnMessagesDone()
    {
        _messagesLoading = false;
        // Events the message pass could not reach, for example because they left the log meanwhile, are named in the status bar.
        _missingMessages = _all.Count(i => !i.HasMessage);
        if (_viewDirty) RequestRebuild(keepScroll: true);
        UpdateStatus();
    }

    private void OnTick()
    {
        if (_closed) return;
        FlushLiveEvents();
        bool loading = _recordsLoading || _messagesLoading;
        // While loading, a rebuild at most once a second keeps the list current without making it jumpy.
        if (_viewDirty && (!loading || _lastRebuild.Elapsed > TimeSpan.FromSeconds(1))) RequestRebuild(keepScroll: true);
        UpdateStatus();
        if (!loading && !_viewDirty && !_rebuildRunning && _livePending.Count == 0) _tick.Stop();
    }

    // ----- Live updates -----

    private void StartLive()
    {
        if (_source is not { SupportsLiveUpdates: true } source || LiveToggle.IsChecked != true || _live is not null || _recordsLoading || _load is null) return;
        CancellationToken token = _load.Token;
        DispatcherQueue queue = DispatcherQueue;
        ConcurrentQueue<EventItem> pending = _livePending;
        try
        {
            // New events collect in a queue and the tick adds them in one go, so a burst of thousands of events per
            // second costs one list update per tick instead of one per event. Watching continues after the newest event
            // this window has, so nothing written while the log was read or while Live was off is missed.
            LiveSubscription? subscription = null;
            subscription = LiveSubscription.Start(source.Path, _resumeBookmark, fromOldest: _all.Count == 0, _messages,
                item =>
                {
                    pending.Enqueue(item);
                    if (Interlocked.Exchange(ref _liveWakeQueued, 1) == 0)
                        queue.TryEnqueue(() => { if (!_closed) _tick.Start(); });
                },
                message => queue.TryEnqueue(() =>
                {
                    // The notice may arrive after the user switched Live off and on again; it must not stop the new subscription.
                    if (!token.IsCancellationRequested && !_closed && subscription is not null && _live == subscription) OnLiveFailed(message);
                }));
            _live = subscription;
            // Without a bookmark, only events written from now on arrive, so say that a few may be missing.
            if (_resumeBookmark is null && _all.Count > 0 && !ProblemBar.IsOpen)
                ShowProblem(Loc.Get("Problem_LiveFromNow"), offerAdmin: false);
        }
        catch (EventLogException ex)
        {
            LiveToggle.IsChecked = false;
            // An earlier problem, such as a read error, explains more than the failed subscription does.
            if (!ProblemBar.IsOpen) ShowProblem(Loc.F("Problem_PressF5", ex.Message), offerAdmin: false);
        }
    }

    private void FlushLiveEvents()
    {
        Interlocked.Exchange(ref _liveWakeQueued, 0);
        if (_livePending.IsEmpty) return;
        var arrived = new List<EventItem>();
        while (_livePending.TryDequeue(out EventItem? item)) arrived.Add(item);
        _all.AddRange(arrived);

        if (_rebuildRunning)
        {
            // When the running rebuild finishes, it merges the events that arrived after its snapshot if there are only
            // a few and the log has finished loading. Otherwise it marks the view dirty, and the next tick rebuilds it.
        }
        else if (arrived.Count > 100 || (_messagesLoading && _comparer.Column == SortColumn.Task))
        {
            _viewDirty = true;
        }
        else
        {
            foreach (EventItem item in arrived)
            {
                if (!_filter.MatchesExceptLevel(item)) continue;
                _levelCounts[(int)item.Kind]++;
                if (_filter.Matches(item)) _view.InsertSorted(item, _comparer);
            }
            UpdateLevelButtons();
            EmptyText.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnLiveFailed(string message)
    {
        // Turning Live on again continues after the last event the failed subscription delivered.
        _live?.Dispose();
        _resumeBookmark = _live?.ResumeBookmark ?? _resumeBookmark;
        _live = null;
        LiveToggle.IsChecked = false;
        ShowProblem(Loc.F("Problem_PressF5", message), offerAdmin: false);
        UpdateStatus();
    }

    /// <summary>
    /// Removes as many of the oldest events as the watched log has overwritten since it was read, so the list of a log
    /// watched for days stays as large as the log instead of growing without limit. It compares counts, not record
    /// numbers, because forwarded events carry the record numbers of the PCs they came from.
    /// </summary>
    private async void DropOverwrittenEvents()
    {
        if (_live is null || _source is not { Kind: LogSourceKind.Channel } source || _recordsLoading || _messagesLoading) return;
        int version = _loadVersion;
        // Events that arrive while Windows counts are in the log but not yet in this number, so the count errs on keeping events.
        int listed = _all.Count;
        long? inLog;
        try
        {
            inLog = await Task.Run(() => ChannelCatalog.CountRecords(source.Path));
            if (_closed) return;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return;
        }
        // A running rebuild remembers how many events it saw, so the list may only shrink between rebuilds.
        if (inLog is not long count || _live is null || _messagesLoading || version != _loadVersion || _rebuildRunning) return;
        if (listed <= count) return;
        int excess = (int)Math.Min(listed - count, listed);
        if (excess <= 0) return;

        // The oldest events are at the old end of the part that was read, then at the start of the live part.
        int fromRead = Math.Min(excess, _readCount);
        _all.RemoveRange(_readNewestFirst ? _readCount - fromRead : 0, fromRead);
        _readCount -= fromRead;
        _all.RemoveRange(_readCount, Math.Min(excess - fromRead, _all.Count - _readCount));
        _missingMessages = _all.Count(i => !i.HasMessage);
        _droppedSinceCompact += excess;
        RequestRebuild(keepScroll: true);

        if (!_compactingMessages && (_droppedSinceCompact >= 5_000 || (_droppedSinceCompact >= 1_000 && _droppedSinceCompact >= _all.Count)))
            CompactMessagesAsync(version);
    }

    /// <summary>Rebuilds MessageStore in the background so lines from overwritten events are reclaimed during multi-day live runs.</summary>
    private async void CompactMessagesAsync(int version)
    {
        _compactingMessages = true;
        EventItem[] snapshot = [.. _all];
        try
        {
            (MessageStore fresh, List<(EventItem Item, StoredMessage Message)> remapped) = await Task.Run(() => MessageStore.Compact(snapshot));
            if (_closed || version != _loadVersion) return;
            foreach (var (item, stored) in remapped) item.ReplaceStoredMessage(stored);
            _messages = fresh;
            _live?.UseStore(fresh);
            // Drain any live events delivered while compaction ran and remap any items still pointing to the old store.
            FlushLiveEvents();
            foreach (EventItem item in _all)
            {
                if (fresh.RemapIfDifferent(item) is { } late) item.ReplaceStoredMessage(late);
            }
            // Clear cached line-match tables in place so an in-flight RunRebuild keeps its filter reference intact.
            _filter.ClearLineCache();
            _droppedSinceCompact = 0;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
        finally
        {
            _compactingMessages = false;
        }
    }

    private void LiveToggle_Click(object sender, RoutedEventArgs e)
    {
        if (LiveToggle.IsChecked == true)
        {
            StartLive();
        }
        else if (_live is not null)
        {
            // Switching Live on again continues after the last event this subscription delivered.
            _live.Dispose();
            _resumeBookmark = _live.ResumeBookmark ?? _resumeBookmark;
            _live = null;
        }
        UpdateStatus();
    }

    // ----- Filtering and sorting -----

    private void CreateLevelButtons()
    {
        foreach (EventLevelKind kind in AllLevels)
        {
            var icon = new FontIcon { Glyph = LevelVisuals.Glyph(kind), Foreground = LevelVisuals.Brush(kind), FontSize = 12 };
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var button = new ToggleButton
            {
                IsChecked = true,
                Padding = new Thickness(8, 4, 8, 4),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon, label } },
                Tag = label,
            };
            ToolTipService.SetToolTip(button, Loc.F("Level_ToolTip", EventItem.KindText(kind)));
            button.Click += (_, _) =>
            {
                ApplyFilter();
                UpdateLevelButtons();
            };
            _levelButtons[kind] = button;
            LevelButtons.Children.Add(button);
        }
        UpdateLevelButtons();
    }

    private void UpdateLevelButtons()
    {
        foreach (EventLevelKind kind in AllLevels)
        {
            ToggleButton button = _levelButtons[kind];
            int count = _levelCounts[(int)kind];
            ((TextBlock)button.Tag).Text = $"{EventItem.KindText(kind)} {count.ToString("N0", CultureInfo.CurrentCulture)}";
            ((StackPanel)button.Content).Children[0].Opacity = button.IsChecked == true ? 1 : 0.35;
            // Rare levels only take space when the log has them or the user has hidden them.
            bool common = kind is EventLevelKind.Critical or EventLevelKind.Error or EventLevelKind.Warning or EventLevelKind.Information;
            button.Visibility = common || count > 0 || button.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void FilterText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressFilterEvents) return;
        _filterDelay.Stop();
        _filterDelay.Start();
    }

    private async void TimeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvents || _filterDelay is null || _closed) return;
        int index = TimeBox.SelectedIndex;
        if (index == CustomTimeIndex)
        {
            try
            {
                bool applied = await PickCustomTimeRangeAsync();
                if (_closed) return;
                if (!applied)
                {
                    _suppressFilterEvents = true;
                    TimeBox.SelectedIndex = _lastTimeIndex;
                    _suppressFilterEvents = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                if (_closed) return;
                _suppressFilterEvents = true;
                TimeBox.SelectedIndex = _lastTimeIndex;
                _suppressFilterEvents = false;
                return;
            }
        }
        else
        {
            _customSinceUtc = null;
            _customUntilUtc = null;
        }
        _lastTimeIndex = index;
        ApplyFilter();
    }

    private async void EditCustomRange_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await PickCustomTimeRangeAsync() && !_closed) ApplyFilter();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private async Task<bool> PickCustomTimeRangeAsync()
    {
        DateTime now = DateTime.Now;
        DateTime defaultStart = _customSinceUtc?.ToLocalTime() ?? now.AddHours(-24);
        DateTime defaultEnd = _customUntilUtc?.ToLocalTime() ?? now;

        var startDate = new DatePicker { Header = Loc.Get("Time_From"), Date = new DateTimeOffset(defaultStart.Date) };
        var startTime = new TimePicker { Time = new TimeSpan(defaultStart.Hour, defaultStart.Minute, 0) };
        var endDate = new DatePicker { Header = Loc.Get("Time_To"), Date = new DateTimeOffset(defaultEnd.Date) };
        var endTime = new TimePicker { Time = new TimeSpan(defaultEnd.Hour, defaultEnd.Minute, 0) };
        var errorText = new TextBlock
        {
            Foreground = LevelVisuals.Brush(EventLevelKind.Error),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(startDate);
        panel.Children.Add(startTime);
        panel.Children.Add(endDate);
        panel.Children.Add(endTime);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Loc.Get("Time_CustomTitle"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Time_Apply"),
            CloseButtonText = Loc.Get("Clear_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            DateTime from = startDate.Date.DateTime.Date + new TimeSpan(startTime.Time.Hours, startTime.Time.Minutes, 0);
            DateTime to = endDate.Date.DateTime.Date + new TimeSpan(endTime.Time.Hours, endTime.Time.Minutes, 0);
            if (to < from)
            {
                errorText.Text = Loc.Get("Time_RangeInvalid");
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (_closed || result != ContentDialogResult.Primary) return false;

        // TimePicker chooses minute precision, so start begins at :00 and end includes the entire chosen minute (:59.9999999).
        DateTime chosenStart = DateTime.SpecifyKind(startDate.Date.DateTime.Date + new TimeSpan(startTime.Time.Hours, startTime.Time.Minutes, 0), DateTimeKind.Local);
        DateTime chosenEnd = DateTime.SpecifyKind(endDate.Date.DateTime.Date + new TimeSpan(endTime.Time.Hours, endTime.Time.Minutes, 59), DateTimeKind.Local)
            .AddTicks(TimeSpan.TicksPerSecond - 1);
        _customSinceUtc = chosenStart.ToUniversalTime();
        _customUntilUtc = chosenEnd.ToUniversalTime();
        return true;
    }

    private bool HasActiveFilter() =>
        SearchBox.Text.Length > 0
        || EventIdBox.Text.Length > 0
        || TimeBox.SelectedIndex > 0
        || _includedSources.Count > 0
        || _excludedSources.Count > 0
        || _levelButtons.Values.Any(b => b.IsChecked != true);

    private void UpdateActiveFilterIndicators()
    {
        bool filtered = HasActiveFilter();
        ResetFilterButton.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;

        bool hasCustomRange = _customSinceUtc is DateTime && _customUntilUtc is DateTime;
        if (hasCustomRange)
        {
            EditCustomRangeText.Text = $"{_customSinceUtc!.Value.ToLocalTime():g} - {_customUntilUtc!.Value.ToLocalTime():g}";
            EditCustomRangeButton.Visibility = Visibility.Visible;
        }
        else
        {
            EditCustomRangeButton.Visibility = Visibility.Collapsed;
        }

        var notes = new List<string>();
        if (_includedSources.Count > 0) notes.Add(Loc.F("Filter_SourceOnly", string.Join(", ", _includedSources)));
        if (_excludedSources.Count == 1) notes.Add(Loc.F("Filter_SourceHiddenOne", _excludedSources.First()));
        else if (_excludedSources.Count > 1) notes.Add(Loc.F("Filter_SourceHiddenMany", Number(_excludedSources.Count)));

        ActiveFilterSummary.Text = string.Join("  |  ", notes);
        ActiveFilterSummary.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveFilterStrip.Visibility = hasCustomRange || notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        _filterDelay.Stop();
        if (_closed) return;
        bool idsValid = EventIdFilter.TryParse(EventIdBox.Text, out EventIdFilter? ids);
        // An invalid list shows nothing rather than everything, so a typo is never mistaken for a working filter.
        _idsInvalid = !idsValid;
        if (!idsValid) ids = EventIdFilter.Nothing;
        ToolTipService.SetToolTip(EventIdBox, idsValid
            ? Loc.Get("EventIds_ToolTip")
            : Loc.Get("EventIds_Invalid"));
        if (idsValid) EventIdBox.ClearValue(Control.BorderBrushProperty);
        else EventIdBox.BorderBrush = LevelVisuals.Brush(EventLevelKind.Error);

        var levels = AllLevels.Where(k => _levelButtons[k].IsChecked == true).ToHashSet();
        DateTime? sinceUtc;
        DateTime? untilUtc = null;
        if (TimeBox.SelectedIndex == CustomTimeIndex && _customSinceUtc is not null && _customUntilUtc is not null)
        {
            _timeWindow = null;
            sinceUtc = _customSinceUtc;
            untilUtc = _customUntilUtc;
        }
        else
        {
            TimeSpan? window = TimeChoices[Math.Clamp(TimeBox.SelectedIndex, 0, TimeChoices.Length - 1)];
            _timeWindow = window;
            sinceUtc = window is TimeSpan span ? DateTime.UtcNow - span : null;
        }
        _filter = new EventFilter
        {
            Text = SearchBox.Text,
            EventIds = ids,
            Levels = levels.Count == AllLevels.Length ? null : levels,
            SinceUtc = sinceUtc,
            UntilUtc = untilUtc,
            IncludedSources = _includedSources.Count > 0 ? new HashSet<string>(_includedSources, StringComparer.OrdinalIgnoreCase) : null,
            ExcludedSources = _excludedSources.Count > 0 ? new HashSet<string>(_excludedSources, StringComparer.OrdinalIgnoreCase) : null,
        };
        UpdateActiveFilterIndicators();
        RequestRebuild(keepScroll: false);
    }

    private void ResetFilter_Click(object sender, RoutedEventArgs e) => ResetFilters();

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out SortColumn column)) return;
        // A new column starts in its most useful direction: newest, most severe or highest ID first, names A to Z.
        bool descending = column == _comparer.Column ? !_comparer.Descending : column is SortColumn.Time or SortColumn.Level or SortColumn.EventId;
        _comparer = new EventComparer(column, descending);
        UpdateSortHeaders();
        RequestRebuild(keepScroll: false);
    }

    private void UpdateSortHeaders()
    {
        (Button Button, SortColumn Column, string Text)[] headers =
        [
            (LevelHeader, SortColumn.Level, Loc.Get("Column_Level")),
            (TimeHeader, SortColumn.Time, Loc.Get("Column_Time")),
            (LogHeader, SortColumn.Log, Loc.Get("Column_Log")),
            (SourceHeader, SortColumn.Source, Loc.Get("Column_Source")),
            (EventIdHeader, SortColumn.EventId, Loc.Get("Column_EventId")),
            (TaskHeader, SortColumn.Task, Loc.Get("Column_Task")),
        ];
        foreach (var (button, column, text) in headers)
            button.Content = column == _comparer.Column ? text + (_comparer.Descending ? "  ↓" : "  ↑") : text;
    }

    /// <summary>A selection larger than this is not restored after a rebuild, because restoring it item by item is slow.</summary>
    private const int MaxRestoredSelection = 500;

    /// <summary>At most this many events that arrived during a rebuild are inserted one by one; more trigger another rebuild.</summary>
    private const int MaxMergedLateEvents = 100;

    /// <summary>
    /// Filters and sorts all events again. The work runs off the UI thread; only one rebuild runs at a time, and a
    /// request that arrives meanwhile runs once the current one is done.
    /// </summary>
    private void RequestRebuild(bool keepScroll)
    {
        _viewDirty = false;
        if (_rebuildRunning)
        {
            _rebuildAgainKeepsScroll = _rebuildAgain ? _rebuildAgainKeepsScroll && keepScroll : keepScroll;
            _rebuildAgain = true;
            return;
        }
        RunRebuild(keepScroll);
    }

    private async void RunRebuild(bool keepScroll)
    {
        _rebuildRunning = true;
        int version = _loadVersion;
        if (_timeWindow is TimeSpan window) _filter = _filter.WithSince(DateTime.UtcNow - window);
        EventFilter filter = _filter;
        EventComparer comparer = _comparer;
        EventItem[] snapshot = [.. _all];
        try
        {
            (List<EventItem> shown, int[] counts) = await Task.Run(() => EventView.FilterAndSort(snapshot, filter, comparer));
            if (_closed) return;
            // Apply only if nothing changed meanwhile; otherwise the newer request below recomputes.
            if (version == _loadVersion && filter == _filter && comparer == _comparer)
                ApplyRebuild(shown, counts, snapshot.Length, keepScroll);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ShowProblem(Loc.F("Problem_ListFailed", ex.Message), offerAdmin: false);
        }
        finally
        {
            _rebuildRunning = false;
            _lastRebuild.Restart();
        }
        if (!_closed && _rebuildAgain)
        {
            _rebuildAgain = false;
            RunRebuild(_rebuildAgainKeepsScroll);
        }
    }

    private void ApplyRebuild(List<EventItem> shown, int[] counts, int snapshotLength, bool keepScroll)
    {
        // Events that arrived while the list was computed. A few live events are merged in here; anything more,
        // record batches of a log that is still being read, or live inserts while sorting by changing Task names
        // wait for the next rebuild instead.
        int late = _all.Count - snapshotLength;
        if (late > MaxMergedLateEvents || _recordsLoading || (_messagesLoading && _comparer.Column == SortColumn.Task))
        {
            if (late > 0)
            {
                _viewDirty = true;
                _tick.Start();
            }
        }
        else
        {
            for (int i = snapshotLength; i < _all.Count; i++)
            {
                EventItem item = _all[i];
                if (!_filter.MatchesExceptLevel(item)) continue;
                counts[(int)item.Kind]++;
                if (!_filter.Matches(item)) continue;
                int index = shown.BinarySearch(item, _comparer);
                shown.Insert(index < 0 ? ~index : index, item);
            }
        }

        var selected = EventList.SelectedItems.Count <= MaxRestoredSelection ? EventList.SelectedItems.OfType<EventItem>().ToHashSet() : [];
        _listScroller ??= FindDescendant<ScrollViewer>(EventList);
        double offset = keepScroll && _listScroller is not null ? _listScroller.VerticalOffset : 0;

        // Replacing the items clears the selection; restoring it must not reload the detail pane each time.
        _rebuilding = true;
        try
        {
            _view.ReplaceAll(shown);
            if (selected.Count > 0)
            {
                foreach (EventItem item in shown)
                {
                    if (selected.Contains(item)) EventList.SelectedItems.Add(item);
                }
            }
            if (offset > 0)
            {
                EventList.UpdateLayout();
                _listScroller!.ChangeView(null, offset, null, disableAnimation: true);
            }
        }
        finally
        {
            _rebuilding = false;
        }
        EventItem? current = EventList.SelectedItems.Count == 1 ? EventList.SelectedItem as EventItem : null;
        if (current != _detailItem) ShowDetail(current);
        else if (current is null) UpdateDetailEmptyText();
        Array.Copy(counts, _levelCounts, counts.Length);
        UpdateLevelButtons();

        // In the overview, logs left out for lack of rights do not make the others unreadable, unless no log could be opened at all.
        bool someLeftOut = _summary is { LeftOut.Count: > 0 };
        bool unreadable = _summary is { Count: 0 } summary
            && (summary.Problems.Count > 0 || summary.NoLogReadable || (summary.AccessDenied && !someLeftOut));
        EmptyText.Text = _all.Count > 0
                ? _idsInvalid ? Loc.Get("Empty_IdsInvalid") : Loc.Get("Empty_NoMatch")
            : _summary is { NoLogReadable: true } ? Loc.Get("Empty_NoneOpened")
            : unreadable ? Loc.Get("Empty_Unreadable")
            : _source?.Kind == LogSourceKind.ProblemsOverview
                ? someLeftOut
                    ? Loc.F("Empty_OverviewSomeRead", OverviewWindowLabel())
                    : Loc.F("Empty_OverviewNone", OverviewWindowLabel())
                : Loc.Get("Empty_NoEvents");
        EmptyText.Visibility = shown.Count == 0 && !_recordsLoading ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is T nested) return nested;
        }
        return null;
    }

    private void UpdateStatus()
    {
        string count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
        var status = new StringBuilder();
        if (_recordsLoading)
        {
            status.Append(Loc.F("Status_Reading", _source?.DisplayName, count(_all.Count)));
        }
        else
        {
            status.Append(Loc.F("Status_Showing", count(_view.Count), count(_all.Count))).Append(' ');
            status.Append(Loc.F("Status_ReadIn", _recordsElapsed.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture)));
            if (_live is not null) status.Append(' ').Append(Loc.Get("Status_Watching"));
            if (_missingMessages > 0 && !_messagesLoading)
                status.Append(' ').Append(Loc.F(_missingMessages == 1 ? "Status_NoMessageOne" : "Status_NoMessageMany", count(_missingMessages)));
        }
        StatusText.Text = status.ToString();

        if (_messagesLoading && !_recordsLoading && _all.Count > 0)
        {
            int percent = (int)Math.Min(100, _messagesFormatted * 100L / Math.Max(1, _all.Count));
            MessageStatusText.Text = Loc.F("Status_LoadingMessages", percent);
            LoadProgress.IsIndeterminate = false;
            LoadProgress.Value = percent;
        }
        else
        {
            MessageStatusText.Text = "";
            LoadProgress.IsIndeterminate = true;
        }
        LoadProgress.Visibility = _recordsLoading || _messagesLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Detail pane -----

    private void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding) return;
        EventItem? item = EventList.SelectedItems.Count == 1 ? EventList.SelectedItem as EventItem : null;
        if (item != _detailItem) ShowDetail(item);
        else if (item is null) UpdateDetailEmptyText();
    }

    private void UpdateDetailEmptyText()
    {
        DetailEmptyText.Text = EventList.SelectedItems.Count > 1
            ? Loc.F("Detail_ManySelected", EventList.SelectedItems.Count.ToString("N0", CultureInfo.CurrentCulture))
            : Loc.Get("Detail_SelectEvent");
    }

    private async void ShowDetail(EventItem? item)
    {
        int version = ++_detailVersion;
        _detailItem = item;
        _detail = null;
        _detailUser = null;
        DetailPanel.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        DetailEmptyText.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetailEmptyText();
        if (item is null || _source is not { } source) return;

        DetailIcon.Glyph = LevelVisuals.Glyph(item.Kind);
        DetailIcon.Foreground = LevelVisuals.Brush(item.Kind);
        DetailTitle.Text = $"{item.Provider} {item.EventId}";
        DetailSubtitle.Text = $"{item.LevelText}, {item.TimeText}";
        MessageText.Text = item.Message ?? Loc.Get("Detail_LoadingMessage");
        XmlText.Text = Loc.Get("Detail_Loading");
        FillFields(item, null, CachedAccount(item.UserSid));
        FillDataFields(null);

        // Resolve the account SID independently so a slow or unreachable domain controller never stalls Message or XML.
        Task<string?> userTask = item.UserSid is { } sid && CachedAccount(sid) is null
            ? Task.Run(() => ResolveAccount(sid))
            : Task.FromResult(CachedAccount(item.UserSid));

        EventDetail detail;
        string? xml;
        try
        {
            (detail, xml) = await Task.Run(() =>
            {
                EventDetail found = EventReader.ReadDetail(source, item);
                return (found, PrettyXml(found.Xml));
            });
        }
        catch (Exception ex)
        {
            // This is an async void handler, so an exception that escaped here would end the app.
            if (version != _detailVersion || _closed) return;
            CrashLog.Write(ex);
            MessageText.Text = item.Message ?? CoreText.F("Error_ReadAgain", ex.Message);
            XmlText.Text = CoreText.F("Error_ReadAgain", ex.Message);
            return;
        }
        if (version != _detailVersion || _closed) return;

        _detail = detail;
        if (!item.HasMessage && detail.Message is not null)
        {
            item.SetFormatted(detail.Message, detail.TaskName);
            if (!_messagesLoading && _missingMessages > 0) _missingMessages--;
        }
        MessageText.Text = item.Message ?? detail.Error ?? "";
        XmlText.Text = xml ?? detail.Error ?? "";
        FillDataFields(detail.Data);
        _detailUser = userTask.IsCompletedSuccessfully ? userTask.Result : CachedAccount(item.UserSid);
        FillFields(item, detail, _detailUser);

        if (!userTask.IsCompleted)
        {
            try
            {
                string? resolved = await userTask;
                if (version != _detailVersion || _closed) return;
                _detailUser = resolved;
                FillFields(item, detail, resolved);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }
        }
    }

    private void FillFields(EventItem item, EventDetail? detail, string? user)
    {
        FieldsGrid.Children.Clear();
        FieldsGrid.RowDefinitions.Clear();
        void Add(string label, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            int row = FieldsGrid.RowDefinitions.Count;
            FieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["FieldLabelStyle"] };
            var text = new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            FieldsGrid.Children.Add(name);
            FieldsGrid.Children.Add(text);
        }

        Add(Loc.Get("Field_Log"), item.Channel);
        Add(Loc.Get("Field_Source"), item.Provider);
        Add(Loc.Get("Field_EventId"), item.EventIdText);
        Add(Loc.Get("Field_Level"), item.LevelText);
        Add(Loc.Get("Field_Task"), detail?.TaskName ?? item.TaskName);
        Add(Loc.Get("Field_Opcode"), detail?.OpcodeName);
        Add(Loc.Get("Field_Keywords"), detail?.KeywordNames);
        Add(Loc.Get("Field_Time"), item.TimeText);
        Add(Loc.Get("Field_User"), user ?? item.UserSid ?? Loc.Get("Field_NotAvailable"));
        Add(Loc.Get("Field_Computer"), item.Computer);
        Add(Loc.Get("Field_ProcessId"), item.ProcessId == 0 ? null : item.ProcessId.ToString(CultureInfo.InvariantCulture));
        Add(Loc.Get("Field_ThreadId"), item.ThreadId == 0 ? null : item.ThreadId.ToString(CultureInfo.InvariantCulture));
        Add(Loc.Get("Field_RecordNumber"), item.RecordId.ToString(CultureInfo.InvariantCulture));
    }

    private void FillDataFields(IReadOnlyList<EventDataField>? data)
    {
        DataFieldsGrid.Children.Clear();
        DataFieldsGrid.RowDefinitions.Clear();
        if (data is null || data.Count == 0)
        {
            DataEmptyText.Visibility = data is null ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        DataEmptyText.Visibility = Visibility.Collapsed;
        for (int i = 0; i < data.Count; i++)
        {
            DataFieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            string label = data[i].Name.Length > 0 ? data[i].Name : $"#{i + 1}";
            var name = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["FieldLabelStyle"], IsTextSelectionEnabled = true };
            var value = new TextBlock { Text = data[i].Value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(name, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            DataFieldsGrid.Children.Add(name);
            DataFieldsGrid.Children.Add(value);
        }
    }

    private string? CachedAccount(string? sid)
    {
        if (sid is null) return null;
        lock (_accountNames)
            return _accountNames.TryGetValue(sid, out string? cached) ? cached : null;
    }

    /// <summary>
    /// Turns a SID into DOMAIN\Name. Resolved names and permanently unmapped SIDs are cached; transient lookup errors
    /// (such as an offline domain controller) fall back to the SID without caching permanently.
    /// </summary>
    private string? ResolveAccount(string? sid)
    {
        if (sid is null) return null;
        if (CachedAccount(sid) is { } cached) return cached;
        try
        {
            string name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            lock (_accountNames) _accountNames[sid] = name;
            return name;
        }
        catch (IdentityNotMappedException)
        {
            lock (_accountNames) _accountNames[sid] = sid;
            return sid;
        }
        catch (SystemException)
        {
            return sid;
        }
    }

    private static string? PrettyXml(string? xml)
    {
        if (xml is null) return null;
        try
        {
            return System.Xml.Linq.XDocument.Parse(xml).ToString();
        }
        catch (System.Xml.XmlException)
        {
            return xml;
        }
    }

    private void DetailTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        GeneralView.Visibility = sender.SelectedItem == GeneralTab ? Visibility.Visible : Visibility.Collapsed;
        DataView.Visibility = sender.SelectedItem == DataTab ? Visibility.Visible : Visibility.Collapsed;
        XmlView.Visibility = sender.SelectedItem == XmlTab ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Copy, open, save -----

    private void EventList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!control || e.Key != VirtualKey.C) return;
        e.Handled = true;
        CopySelectedEvents();
    }

    /// <summary>The right-click menu of the event list. It acts on the selected events, or offers Reset filters when empty.</summary>
    private void EventList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        // A right click on an event that is not selected selects only that event, as in Explorer.
        if (FindAncestor<ListViewItem>(args.OriginalSource as DependencyObject) is { } container)
        {
            int index = EventList.IndexFromContainer(container);
            if (index >= 0 && !EventList.SelectedRanges.Any(r => index >= r.FirstIndex && index <= r.LastIndex))
                EventList.SelectedIndex = index;
        }
        long selected = EventList.SelectedRanges.Sum(r => (long)r.Length);
        bool filtered = HasActiveFilter();
        if (selected == 0 && !filtered) return;
        args.Handled = true;

        var menu = new MenuFlyout();
        if (selected > 0)
        {
            MenuFlyoutItem copy = MenuItem(selected == 1 ? "Menu_CopyEvent" : "Menu_CopyEvents", "\uE8C8", (_, _) => CopySelectedEvents());
            copy.KeyboardAcceleratorTextOverride = Loc.Get("Menu_CopyKey");
            menu.Items.Add(copy);
        }
        if (selected == 1 && EventList.SelectedItem is EventItem item)
        {
            menu.Items.Add(MenuItem("Menu_CopyDetails", "\uE8A5", (_, _) => CopyFullDetailsForAsync(item)));
            menu.Items.Add(MenuItem("Detail_CopyMessage", "\uE8C8", (_, _) => CopyMessageForAsync(item)));
            menu.Items.Add(MenuItem("Detail_CopyXml", "\uE943", (_, _) => CopyXmlForAsync(item)));
            menu.Items.Add(new MenuFlyoutSeparator());
            string id = item.EventId.ToString(CultureInfo.InvariantCulture);
            menu.Items.Add(MenuItemWithText(Loc.F("Menu_OnlyEventId", id), "\uE71C", (_, _) => SetEventIdFilter(id)));
            menu.Items.Add(MenuItemWithText(Loc.F("Menu_HideEventId", id), "\uED1A", (_, _) => HideEventId(id)));
            if (item.Provider.Length > 0)
            {
                menu.Items.Add(MenuItemWithText(Loc.F("Menu_OnlySource", item.Provider), "\uE71C", (_, _) => IncludeOnlySource(item.Provider)));
                menu.Items.Add(MenuItemWithText(Loc.F("Menu_HideSource", item.Provider), "\uED1A", (_, _) => ExcludeSource(item.Provider)));
            }
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Menu_ResetFilters", "\uE894", (_, _) => ResetFilters(), enabled: filtered));
        ShowMenu(menu, sender, args);
    }

    private void SetEventIdFilter(string text)
    {
        _suppressFilterEvents = true;
        EventIdBox.Text = text;
        _suppressFilterEvents = false;
        ApplyFilter();
    }

    private void HideEventId(string id)
    {
        string exclusion = "-" + id;
        List<string> parts = EventIdBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!parts.Any(p => string.Equals(p, exclusion, StringComparison.Ordinal))) parts.Add(exclusion);
        SetEventIdFilter(string.Join(", ", parts));
    }

    private void IncludeOnlySource(string provider)
    {
        _includedSources.Clear();
        _includedSources.Add(provider);
        _excludedSources.Remove(provider);
        ApplyFilter();
    }

    private void ExcludeSource(string provider)
    {
        _includedSources.Remove(provider);
        _excludedSources.Add(provider);
        ApplyFilter();
    }

    private void ResetFilters()
    {
        _suppressFilterEvents = true;
        SearchBox.Text = "";
        EventIdBox.Text = "";
        TimeBox.SelectedIndex = 0;
        _lastTimeIndex = 0;
        _customSinceUtc = null;
        _customUntilUtc = null;
        _includedSources.Clear();
        _excludedSources.Clear();
        foreach (ToggleButton button in _levelButtons.Values) button.IsChecked = true;
        _suppressFilterEvents = false;
        UpdateLevelButtons();
        ApplyFilter();
    }

    private async void CopySelectedEvents()
    {
        try
        {
            // The selected ranges are cheap to read even when a million rows are selected; the text is built off the UI thread.
            EventItem[] view = [.. _view];
            var ranges = EventList.SelectedRanges.Select(r => (r.FirstIndex, r.LastIndex)).ToArray();
            string text = await Task.Run(() =>
            {
                var builder = new StringBuilder();
                foreach (var (first, last) in ranges.OrderBy(r => r.FirstIndex))
                {
                    for (int i = Math.Max(0, first); i <= last && i < view.Length; i++)
                    {
                        EventItem item = view[i];
                        builder.Append(item.LevelText).Append('\t').Append(item.TimeText).Append('\t').Append(item.Provider).Append('\t')
                            .Append(item.EventIdText).Append('\t').Append(item.TaskName).Append('\t').Append(item.MessagePreview).Append("\r\n");
                    }
                }
                return builder.ToString();
            });
            if (text.Length > 0 && !_closed) CopyText(text);
        }
        catch (Exception ex)
        {
            // This is an async void handler, so an exception that escaped here would end the app.
            CrashLog.Write(ex);
            ShowProblem(Loc.F("Problem_CopyFailed", ex.Message), offerAdmin: false);
        }
    }

    private void CopyDetail_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (_detailItem is null) return;
        if (DetailTabs.SelectedItem == XmlTab) CopyXmlForAsync(_detailItem);
        else if (DetailTabs.SelectedItem == DataTab && _detail?.Data is { Count: > 0 } data)
        {
            var text = new StringBuilder();
            foreach (EventDataField field in data)
                text.Append(field.Name.Length > 0 ? field.Name + ": " : "").Append(field.Value).Append("\r\n");
            CopyText(text.ToString());
        }
        else CopyFullDetailsForAsync(_detailItem);
    }

    private void CopyAllDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not null) CopyFullDetailsForAsync(_detailItem);
    }

    private void CopyMessageOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not null) CopyMessageForAsync(_detailItem);
    }

    private void CopyXmlOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not null) CopyXmlForAsync(_detailItem);
    }

    private async void CopyFullDetailsForAsync(EventItem item)
    {
        try
        {
            if (_source is not { } source) return;
            EventDetail detail;
            string? user;
            if (_detailItem == item && _detail is not null && (_detailUser is not null || item.UserSid is null))
            {
                detail = _detail;
                user = _detailUser;
            }
            else
            {
                (detail, user) = await Task.Run(() => (EventReader.ReadDetail(source, item), ResolveAccount(item.UserSid)));
                if (_closed) return;
            }

            var text = new StringBuilder();
            void Add(string label, string? value)
            {
                if (!string.IsNullOrEmpty(value)) text.Append(label).Append(": ").Append(value).Append("\r\n");
            }
            Add(Loc.Get("Field_Log"), item.Channel);
            Add(Loc.Get("Field_Source"), item.Provider);
            Add(Loc.Get("Field_EventId"), item.EventIdText);
            Add(Loc.Get("Field_Level"), item.LevelText);
            Add(Loc.Get("Field_Task"), detail.TaskName ?? item.TaskName);
            Add(Loc.Get("Field_Opcode"), detail.OpcodeName);
            Add(Loc.Get("Field_Keywords"), detail.KeywordNames);
            Add(Loc.Get("Field_Time"), item.TimeText);
            Add(Loc.Get("Field_User"), user ?? item.UserSid ?? Loc.Get("Field_NotAvailable"));
            Add(Loc.Get("Field_Computer"), item.Computer);
            Add(Loc.Get("Field_ProcessId"), item.ProcessId == 0 ? null : item.ProcessId.ToString(CultureInfo.InvariantCulture));
            Add(Loc.Get("Field_ThreadId"), item.ThreadId == 0 ? null : item.ThreadId.ToString(CultureInfo.InvariantCulture));
            Add(Loc.Get("Field_RecordNumber"), item.RecordId.ToString(CultureInfo.InvariantCulture));
            text.Append("\r\n").Append(item.Message ?? detail.Message ?? detail.Error ?? "").Append("\r\n");
            if (PrettyXml(detail.Xml) is { } xml) text.Append("\r\n").Append(xml).Append("\r\n");
            CopyText(text.ToString());
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ShowProblem(Loc.F("Problem_CopyFailed", ex.Message), offerAdmin: false);
        }
    }

    private async void CopyMessageForAsync(EventItem item)
    {
        try
        {
            if (item.Message is { } loaded)
            {
                CopyText(loaded);
                return;
            }
            if (_source is not { } source) return;
            EventDetail detail = await Task.Run(() => EventReader.ReadDetail(source, item));
            if (_closed) return;
            string? message = detail.Message ?? detail.Error;
            if (!string.IsNullOrEmpty(message)) CopyText(message);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ShowProblem(Loc.F("Problem_CopyFailed", ex.Message), offerAdmin: false);
        }
    }

    private async void CopyXmlForAsync(EventItem item)
    {
        try
        {
            if (_detailItem == item && _detail?.Xml is { } existing)
            {
                CopyText(PrettyXml(existing) ?? existing);
                return;
            }
            if (_source is not { } source) return;
            string? xml = await Task.Run(() => PrettyXml(EventReader.ReadDetail(source, item).Xml));
            if (!_closed && !string.IsNullOrEmpty(xml)) CopyText(xml);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ShowProblem(Loc.F("Problem_CopyFailed", ex.Message), offerAdmin: false);
        }
    }

    private void CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            // Another program can hold the clipboard open; that must not end this app.
            ShowProblem(Loc.F("Problem_CopyFailed", ex.Message), offerAdmin: false);
        }
    }

    // The click handlers below are async void, so each one catches everything: an exception escaping them would end the app.

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".evtx");
            PickFileResult? result = await picker.PickSingleFileAsync();
            if (!_closed && result is not null) OpenFile(result.Path);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ShowProblem(Loc.F("Problem_OpenFailed", ex.Message), offerAdmin: false);
        }
    }

    private async void SaveCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_source is null) return;
            // While records are still arriving, the list is incomplete, and saving it would claim a complete file.
            string stillReading = Loc.Get("Save_StillReading");
            if (_recordsLoading)
            {
                ShowProblem(stillReading, offerAdmin: false);
                return;
            }
            int version = _loadVersion;
            string path = await PickSavePath(Loc.Get("Save_CsvType"), ".csv", "-events");
            if (_closed || path.Length == 0) return;
            if (version != _loadVersion || _recordsLoading)
            {
                ShowProblem(_recordsLoading ? stillReading : Loc.Get("Save_OtherLogOpened"), offerAdmin: false);
                return;
            }

            // The list on screen can lag behind the events by up to a second, so the file is built from all events
            // with the current filter and sort order, the same way the list is.
            EventItem[] snapshot = [.. _all];
            EventFilter filter = _timeWindow is TimeSpan window ? _filter.WithSince(DateTime.UtcNow - window) : _filter;
            EventComparer comparer = _comparer;
            await RunSave(() =>
            {
                List<EventItem> items = EventView.FilterAndSort(snapshot, filter, comparer).Shown;
                int missing = items.Count(i => !i.HasMessage);
                int saved = EventExport.SaveCsv(path, items);
                string note = missing > 0 ? " " + Loc.F("Save_MissingNote", missing.ToString("N0", CultureInfo.CurrentCulture)) : "";
                return Loc.F("Save_SavedCsv", saved.ToString("N0", CultureInfo.CurrentCulture), path) + note;
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ShowProblem(Loc.F("Save_Failed", ex.Message), offerAdmin: false);
        }
    }

    private async void SaveEvtx_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The toolbar saves the open log, and the log tree's menu names the log it was opened on.
            LogSource? target = sender is FrameworkElement { Tag: LogSource tagged } ? tagged : _source;
            if (target is not { SupportsEvtxExport: true } source) return;
            string path = await PickSavePath(Loc.Get("Save_EvtxType"), ".evtx", "", source.DisplayName);
            if (_closed || path.Length == 0) return;
            await RunSave(() =>
            {
                EventExport.SaveEvtx(source, path);
                return Loc.F("Save_SavedFile", path);
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ShowProblem(Loc.F("Save_Failed", ex.Message), offerAdmin: false);
        }
    }

    // "Microsoft-Windows-PowerShell/Operational" becomes "Microsoft-Windows-PowerShell-Operational".
    private static string SafeFileName(string name) => string.Concat(name.Replace('/', '-').Split(Path.GetInvalidFileNameChars()));

    private async Task<string> PickSavePath(string typeName, string extension, string suffix, string? logName = null)
    {
        string baseName = SafeFileName(logName ?? _source?.DisplayName ?? "events");
        if (baseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) baseName = baseName[..^extension.Length];
        string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = $"{baseName}{suffix}-{date}",
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(typeName, [extension]);
        PickFileResult? result = await picker.PickSaveFileAsync();
        return result?.Path ?? "";
    }

    /// <summary>
    /// Clears a log or every log of a folder, but only after the user confirmed it in a dialog whose default answer is
    /// Cancel. The user can save copies first; Windows then copies each log's events and empties it in one step.
    /// </summary>
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        // The toolbar clears the open log, and the log tree's menu names the log or folder it was opened on.
        ClearRequest? request = sender is FrameworkElement { Tag: ClearRequest tagged } ? tagged
            : _source is { Kind: LogSourceKind.Channel } current ? new ClearRequest(current.DisplayName, [current], IsFolder: false) : null;
        // One clear at a time: a second click while one runs would ask again about logs that are being emptied.
        if (_clearing || request is null || request.Logs.Count == 0) return;
        _clearing = true;
        ClearButton.IsEnabled = false;
        bool started = false;
        List<LogSource> logs = [.. request.Logs];
        // The open log is read again afterwards, and the question is dropped if the user opens another log meanwhile.
        LogSource? openLog = _source is { } shown && logs.Contains(shown) ? shown : null;
        bool OpenLogChanged() => _closed || (openLog is not null && _source != openLog);
        try
        {
            if (openLog is not null && _recordsLoading)
            {
                ShowProblem(Loc.Get("Clear_StillReading"), offerAdmin: false);
                return;
            }
            int openCount = _all.Count;
            long?[] counts = await Task.Run(() => logs.AsParallel().AsOrdered().WithDegreeOfParallelism(4)
                .Select(log => log == openLog ? openCount : ChannelCatalog.CountRecords(log.Path)).ToArray());
            if (OpenLogChanged()) return;
            if (request.IsFolder)
            {
                // Empty logs need no clearing, and a copy of one would be an empty file.
                var withEvents = logs.Zip(counts).Where(pair => pair.Second is not 0).ToList();
                logs = withEvents.Select(pair => pair.First).ToList();
                counts = withEvents.Select(pair => pair.Second).ToArray();
                if (logs.Count == 0)
                {
                    ShowInfo(Loc.F("Clear_FolderEmpty", request.Name));
                    return;
                }
            }
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = request.IsFolder ? Loc.F("Clear_FolderTitle", Number(logs.Count), request.Name) : Loc.F("Clear_Title", request.Name),
                Content = new TextBlock { Text = ClearQuestion(request, logs.Count, counts), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Clear_SaveAndClear"),
                SecondaryButtonText = Loc.Get("Clear_WithoutCopy"),
                CloseButtonText = Loc.Get("Clear_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            ContentDialogResult answer = await dialog.ShowAsync();
            if (answer == ContentDialogResult.None || OpenLogChanged()) return;

            string?[] backups = new string?[logs.Count];
            string? backupFolder = null;
            if (answer == ContentDialogResult.Primary)
            {
                if (request.IsFolder)
                {
                    backupFolder = await PickFolder();
                    if (backupFolder.Length == 0 || OpenLogChanged()) return;
                    backups = BackupPaths(backupFolder, logs);
                }
                else
                {
                    string backup = await PickSavePath(Loc.Get("Save_EvtxType"), ".evtx", "", request.Name);
                    if (backup.Length == 0 || OpenLogChanged()) return;
                    backups[0] = backup;
                }
            }

            // Watching a log while it is emptied would only report that live updates stopped.
            if (openLog is not null) StopLoading();
            started = true;
            _clearRunning = true;
            // Each log is cleared on its own and every failure is kept with its log, so a log that Windows refuses or an
            // unexpected error does not stop the others or hide where an earlier log's copy was kept.
            Exception?[] failures = await Task.Run(() => logs.Select(Exception? (log, i) =>
            {
                try
                {
                    // Folder names are generated without an overwrite prompt. A file created after BackupPaths ran
                    // must stay intact; the cleared log's copy is kept locally and reported if that happens.
                    LogCleaner.Clear(log.Path, backups[i], overwriteBackup: !request.IsFolder);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }).ToArray());
            _clearRunning = false;
            ReopenIfCleared(logs);
            foreach (LogSource log in logs) RecountLog(log.Path);
            ReportClear(request, logs, backups, backupFolder, failures);
        }
        catch (Exception ex)
        {
            // This is an async void handler, so an exception that escaped here would end the app. Whether the logs were
            // emptied is unknown here, so the message does not claim either, and the open log is read again to show it.
            CrashLog.Write(ex);
            if (started)
            {
                ReopenIfCleared(logs);
                foreach (LogSource log in logs) RecountLog(log.Path);
            }
            ShowProblem(Loc.F("Clear_Unexpected", request.Name, ex.Message), offerAdmin: false);
        }
        finally
        {
            _clearRunning = false;
            _clearing = false;
            if (!_closed) ClearButton.IsEnabled = _source?.Kind == LogSourceKind.Channel;
        }
    }

    private bool _clearing;

    /// <summary>What the user asked to clear: one log, or the logs of a folder in the log tree.</summary>
    private sealed record ClearRequest(string Name, List<LogSource> Logs, bool IsFolder);

    private static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string ClearQuestion(ClearRequest request, int logCount, long?[] counts)
    {
        if (!request.IsFolder)
            return counts[0] is long known ? Loc.F("Clear_Text", Number(known), request.Name) : Loc.F("Clear_TextNoCount", request.Name);
        string text = Loc.F("Clear_FolderText", Number(counts.Sum(c => c ?? 0)), Number(logCount), request.Name);
        int unknown = counts.Count(c => c is null);
        return unknown == 0 ? text : text + " " + Loc.F("Clear_FolderUnknown", Number(unknown));
    }

    /// <summary>Tells the user which logs were cleared, where their copies are, and which could not be cleared and why.</summary>
    private void ReportClear(ClearRequest request, List<LogSource> logs, string?[] backups, string? backupFolder, Exception?[] failures)
    {
        // A copy that stayed in the temporary folder is the only copy of a cleared log, so those messages come first.
        var keptCopies = new List<string>();
        var keptPaths = new List<string>();
        var unexpected = new List<string>();
        var refused = new List<string>();
        bool offerAdmin = false;
        for (int i = 0; i < logs.Count; i++)
        {
            switch (failures[i])
            {
                case null:
                    break;
                case BackupNotSavedException ex:
                    // Always record the recovery path in the crash log so it can never be lost if the InfoBar closes.
                    CrashLog.Write(ex);
                    keptCopies.Add(ex.Message);
                    keptPaths.Add(ex.KeptAt);
                    break;
                case EventLogException ex:
                    // Windows refused before deleting anything.
                    refused.Add(ex.IsAccessDenied ? Loc.F("Clear_NeedsAdmin", logs[i].DisplayName)
                        : request.IsFolder ? Loc.F("Clear_FailedLog", logs[i].DisplayName, ex.Message) : Loc.F("Clear_Failed", ex.Message));
                    offerAdmin |= ex.IsAccessDenied && !_isElevated;
                    break;
                case Exception ex:
                    // Whether this log was emptied is unknown, so the message does not claim either.
                    CrashLog.Write(ex);
                    unexpected.Add(request.IsFolder ? Loc.F("Clear_UnexpectedLog", logs[i].DisplayName, ex.Message) : Loc.F("Clear_Unexpected", request.Name, ex.Message));
                    break;
            }
        }
        if (keptPaths.Count > 0) ShowBackupRecoveryDialogAsync(keptCopies, keptPaths);

        List<string> problems = [.. keptCopies, .. unexpected, .. refused];
        if (!request.IsFolder)
        {
            if (problems.Count > 0) ShowProblem(problems[0], offerAdmin);
            else ShowInfo(backups[0] is { } backup ? Loc.F("Clear_DoneWithCopy", request.Name, backup) : Loc.F("Clear_Done", request.Name));
            return;
        }

        int cleared = failures.Count(f => f is null or BackupNotSavedException);
        var message = new StringBuilder(Loc.F("Clear_FolderDone", Number(cleared), Number(logs.Count), request.Name));
        if (backupFolder is not null && cleared > keptCopies.Count) message.Append(' ').Append(Loc.F("Clear_FolderCopies", backupFolder));
        const int ShownProblems = 5;
        foreach (string problem in problems.Take(ShownProblems)) message.Append('\n').Append(problem);
        if (problems.Count > ShownProblems) message.Append('\n').Append(Loc.F("Clear_FolderMore", Number(problems.Count - ShownProblems)));
        if (problems.Count > 0) ShowProblem(message.ToString(), offerAdmin);
        else ShowInfo(message.ToString());
    }

    /// <summary>Shows a modal dialog with every temporary backup path when moving a cleared log's only copy failed.</summary>
    private async void ShowBackupRecoveryDialogAsync(List<string> messages, List<string> paths)
    {
        if (_closed) return;
        try
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = Loc.Get("Clear_RecoveryBody"), TextWrapping = TextWrapping.Wrap });
            var list = new TextBlock
            {
                Text = string.Join("\r\n\r\n", messages),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            content.Children.Add(new ScrollViewer { MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = Loc.Get("Clear_RecoveryTitle"),
                Content = content,
                PrimaryButtonText = Loc.Get("Clear_RecoveryCopy"),
                SecondaryButtonText = Loc.Get("Clear_RecoveryOpenFolder"),
                CloseButtonText = Loc.Get("Clear_RecoveryClose"),
                DefaultButton = ContentDialogButton.Primary,
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (_closed) return;
            if (result == ContentDialogResult.Primary) CopyText(string.Join("\r\n", paths));
            else if (result == ContentDialogResult.Secondary && paths.Count > 0) ShowInFolder(paths[0]);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private async Task<string> PickFolder()
    {
        PickFolderResult? result = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
        return result?.Path ?? "";
    }

    /// <summary>One file per log in the chosen folder, named after the log and the date, and never one that already exists.</summary>
    private static string?[] BackupPaths(string folder, List<LogSource> logs)
    {
        string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return logs.Select(string? (log) =>
        {
            string baseName = $"{SafeFileName(log.DisplayName)}-{date}";
            string path = Path.Combine(folder, baseName + ".evtx");
            for (int n = 2; File.Exists(path) || !used.Add(path); n++) path = Path.Combine(folder, $"{baseName} ({n}).evtx");
            return path;
        }).ToArray();
    }

    /// <summary>
    /// Reads the open log again if it is one of the cleared logs, whether it was open before the clear or was opened
    /// while the clear ran, so the list never keeps showing events that were deleted.
    /// </summary>
    private void ReopenIfCleared(List<LogSource> logs)
    {
        if (!_closed && _source is { Kind: LogSourceKind.Channel } open
            && logs.Any(log => string.Equals(log.Path, open.Path, StringComparison.OrdinalIgnoreCase)))
            Open(open);
    }

    /// <summary>Runs a save off the UI thread and shows the message it returns, or why it failed.</summary>
    private async Task RunSave(Func<string> save)
    {
        try
        {
            string message = await Task.Run(save);
            if (_closed) return;
            ProblemBar.Severity = InfoBarSeverity.Success;
            ProblemBar.Message = message;
            ProblemAdminButton.Visibility = Visibility.Collapsed;
            ProblemBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            if (ex is NothingToSaveException)
            {
                ShowProblem(Loc.F("Save_Nothing", ex.Message), offerAdmin: false);
                return;
            }
            if (ex is not (IOException or UnauthorizedAccessException or EventLogException)) CrashLog.Write(ex);
            ShowProblem(Loc.F("Save_Failed", ex.Message), offerAdmin: false);
        }
    }

    // ----- Commands -----

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null) return;
        if (_source.Kind == LogSourceKind.Channel) RecountLog(_source.Path);
        Open(_source.Kind == LogSourceKind.ProblemsOverview ? CreateOverviewSource() : _source);
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        string? executable = Environment.ProcessPath;
        if (executable is null || RefuseWhileClearing()) return;
        SaveWindowLayout();
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        foreach (string argument in StartupArguments.For(_source)) start.ArgumentList.Add(argument);
        try
        {
            using Process? started = Process.Start(start);
            if (started is not null)
            {
                _skipSaveLayoutOnClose = true;
                Close();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The UAC prompt was cancelled; keep this window.
        }
    }

    private void ShowInfo(string message)
    {
        if (_closed) return;
        ProblemBar.Severity = InfoBarSeverity.Success;
        ProblemBar.Message = message;
        ProblemAdminButton.Visibility = Visibility.Collapsed;
        ProblemBar.IsOpen = true;
    }

    private void ShowProblem(string message, bool offerAdmin)
    {
        // A failure that arrives after the window closed has nowhere to be shown; changing the bar then would end the app.
        if (_closed) return;
        ProblemBar.Severity = InfoBarSeverity.Warning;
        ProblemBar.Message = message;
        ProblemAdminButton.Visibility = offerAdmin ? Visibility.Visible : Visibility.Collapsed;
        ProblemBar.IsOpen = true;
    }

    private void Refresh_Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Refresh_Click(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void Search_Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
        args.Handled = true;
    }

    private void Open_Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Open_Click(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void SaveCsv_Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SaveCsv_Click(sender, new RoutedEventArgs());
        args.Handled = true;
    }
}
