using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NaxEventViewer.Core;

namespace NaxEventViewer.App;

public static class Program
{
    internal const string LogArgument = "--log";
    internal const string FileArgument = "--file";
    internal const string OverviewArgument = "--overview";

    /// <summary>The log to open at start, from the command line, or null for the System log.</summary>
    internal static StartupTarget? Startup { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        Startup = ParseArguments(args);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    /// <summary>Accepts "--log Name", "--file Path", "--overview", or a single .evtx path (for opening a file from Explorer).</summary>
    internal static StartupTarget? ParseArguments(string[] args)
    {
        if (args.Length == 2 && args[0] == LogArgument && args[1].Length > 0) return new StartupTarget(LogSourceKind.Channel, args[1]);
        if (args.Length == 2 && args[0] == FileArgument && args[1].Length > 0) return new StartupTarget(LogSourceKind.File, args[1]);
        if (args.Length == 1 && args[0] == OverviewArgument) return new StartupTarget(LogSourceKind.ProblemsOverview, "");
        if (args.Length == 1 && args[0].EndsWith(".evtx", StringComparison.OrdinalIgnoreCase)) return new StartupTarget(LogSourceKind.File, args[0]);
        return null;
    }

    /// <summary>The command-line arguments that reopen <paramref name="source"/>, used when restarting as administrator.</summary>
    internal static string[] ArgumentsFor(LogSource? source) => source?.Kind switch
    {
        LogSourceKind.Channel => [LogArgument, source.Path],
        LogSourceKind.File => [FileArgument, source.Path],
        LogSourceKind.ProblemsOverview => [OverviewArgument],
        _ => [],
    };
}

internal sealed record StartupTarget(LogSourceKind Kind, string Path);
