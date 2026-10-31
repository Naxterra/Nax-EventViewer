using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NaxEventViewer.Core;

namespace NaxEventViewer.App;

public static class Program
{
    internal const string ReplaceOnArgument = "--replace-eventviewer-on";
    internal const string ReplaceOffArgument = "--replace-eventviewer-off";

    // Exit codes of the elevated helper.
    internal const int HelperNotElevated = 5;
    internal const int HelperNotAdminOnlyFolder = 6;
    internal const int HelperFailed = 1;
    internal const int HelperWrongPath = 87;

    /// <summary>The log to open at start, from the command line, or null for the System log.</summary>
    internal static StartupTarget? Startup { get; private set; }

    internal static AppSettings Settings { get; private set; } = new();

    [STAThread]
    private static int Main(string[] args)
    {
        // The elevated helper changes the machine-wide replacement setting and ends without opening a window.
        if (args.Length >= 1 && args[0] is ReplaceOnArgument or ReplaceOffArgument) return RunReplaceHelper(args);

        RunningMutex.Hold();
        Settings = AppSettings.Load();
        Startup = StartupArguments.Parse(args);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            Loc.ApplyLanguage(Settings.Language);
            new App();
        });
        return 0;
    }

    /// <summary>
    /// "--replace-eventviewer-on Path" makes this executable replace the Windows Event Viewer, and
    /// "--replace-eventviewer-off Path" undoes it if the replacement is this executable. Any program can start the
    /// helper with any path, so only this executable's own path is accepted, and only from an administrator-only folder.
    /// </summary>
    private static int RunReplaceHelper(string[] args)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return HelperNotElevated;
        if (args.Length != 2 || Environment.ProcessPath is not { } self || !EventViewerReplacement.IsTarget(args[1], self)) return HelperWrongPath;
        try
        {
            if (args[0] == ReplaceOnArgument) EventViewerReplacement.Enable(self);
            else EventViewerReplacement.DisableIfOurs(self);
            return 0;
        }
        catch (InvalidOperationException)
        {
            return HelperNotAdminOnlyFolder;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return HelperFailed;
        }
    }
}
