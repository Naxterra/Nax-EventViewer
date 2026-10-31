namespace NaxEventViewer.Core.Tests;

/// <summary>Replacing the Windows Event Viewer, and the command line Windows passes when it starts the app in its place.</summary>
internal static class ReplacementTests
{
    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("start_in_place_of_eventvwr_opens_the_requested_log",
            "When Windows starts the app in place of eventvwr.exe, the eventvwr path is skipped, \"/l:file\" opens that saved log, \"/c:name\" opens that log, and anything else opens the default log.",
            () =>
            {
                const string eventvwr = @"C:\WINDOWS\system32\eventvwr.exe";
                Check.That(StartupArguments.Parse([eventvwr]) is null, "eventvwr without options opens the default log");
                Check.Equal(new StartupTarget(LogSourceKind.File, @"C:\Logs\saved.evtx"), StartupArguments.Parse([eventvwr, @"/l:""C:\Logs\saved.evtx"""]));
                Check.Equal(new StartupTarget(LogSourceKind.Channel, "Application"), StartupArguments.Parse([$"\"{eventvwr}\"", "/C:Application"]));
                Check.That(StartupArguments.Parse([eventvwr, "OTHER-PC"]) is null, "another computer's name opens the default log");
            });

        yield return ("own_command_line_still_opens_logs_files_and_the_overview",
            "The app's own arguments still work: --log opens a log, --file and a bare .evtx path open a saved file, and --overview opens the problems overview; the arguments for reopening a source read back the same way.",
            () =>
            {
                Check.Equal(new StartupTarget(LogSourceKind.Channel, "Security"), StartupArguments.Parse(["--log", "Security"]));
                Check.Equal(new StartupTarget(LogSourceKind.File, @"D:\a.evtx"), StartupArguments.Parse(["--file", @"D:\a.evtx"]));
                Check.Equal(new StartupTarget(LogSourceKind.File, @"D:\a.evtx"), StartupArguments.Parse([@"D:\a.evtx"]));
                Check.Equal(new StartupTarget(LogSourceKind.ProblemsOverview, ""), StartupArguments.Parse(["--overview"]));
                Check.Equal(new StartupTarget(LogSourceKind.Channel, "System"), StartupArguments.Parse(StartupArguments.For(LogSource.ForChannel("System"))));
            });

        yield return ("replacement_is_refused_for_a_program_a_standard_user_can_change",
            "A program file in the user's own folders can never become the replacement: the check refuses it, and enabling the replacement with it fails before Windows is changed. A file in the Windows folder, which only administrators can change, passes the check.",
            () =>
            {
                string userCopy = Path.Combine(Fixtures.TempFolder("replace"), "Nax-EventViewer.exe");
                File.WriteAllText(userCopy, "x");
                Check.That(!EventViewerReplacement.CanReplaceWith(userCopy), "a file in the user's temporary folder must be refused");
                string? before = EventViewerReplacement.CurrentTarget();
                try
                {
                    EventViewerReplacement.Enable(userCopy);
                    throw new Exception("enabling the replacement with a user copy should have failed");
                }
                catch (InvalidOperationException)
                {
                }
                Check.Equal(before, EventViewerReplacement.CurrentTarget());
                Check.That(EventViewerReplacement.CanReplaceWith(Path.Combine(Environment.SystemDirectory, "eventvwr.exe")), "a file in the Windows folder passes the check");
            });

        yield return ("replacement_of_another_program_is_never_taken_for_this_app",
            "The replacement counts as this app's only when it names this exact program file, with or without quotes and in any letter case, so a replacement that another program set up is left alone.",
            () =>
            {
                const string self = @"C:\Program Files\Nax-EventViewer\Nax-EventViewer.exe";
                Check.That(EventViewerReplacement.IsTarget("\"C:\\Program Files\\Nax-EventViewer\\Nax-EventViewer.exe\"", self), "the quoted path is this app");
                Check.That(EventViewerReplacement.IsTarget(@"c:\program files\nax-eventviewer\NAX-EVENTVIEWER.EXE", self), "letter case does not matter");
                Check.That(!EventViewerReplacement.IsTarget(@"C:\Tools\OtherViewer.exe", self), "another program is not this app");
                Check.That(!EventViewerReplacement.IsTarget(@"C:\Users\Someone\AppData\Local\Programs\Nax-EventViewer\Nax-EventViewer.exe", self), "another copy of this app is not this app");
                Check.That(!EventViewerReplacement.IsTarget(null, self) && !EventViewerReplacement.IsTarget("  ", self), "no replacement is not this app");
            });
    }
}
