namespace NaxEventViewer.Core;

/// <summary>What to open at start: a log, a saved file or the problems overview.</summary>
public sealed record StartupTarget(LogSourceKind Kind, string Path);

/// <summary>
/// Reads the command line. Besides the app's own arguments it understands how Windows starts the app when it replaces
/// the Windows Event Viewer: the path of eventvwr.exe first, followed by eventvwr's own options.
/// </summary>
public static class StartupArguments
{
    public const string LogArgument = "--log";
    public const string FileArgument = "--file";
    public const string OverviewArgument = "--overview";

    /// <summary>
    /// Accepts "--log Name", "--file Path", "--overview", a single .evtx path (for opening a file from Explorer), and
    /// eventvwr's "/l:Path" (a saved log) and "/c:Name" (a log). Anything else, such as the name of another computer,
    /// gives null, which opens the System log.
    /// </summary>
    public static StartupTarget? Parse(string[] args)
    {
        // Started in place of eventvwr.exe: Windows passes the path of eventvwr.exe before its arguments.
        if (args.Length > 0 && System.IO.Path.GetFileName(args[0].Trim('"')).Equals("eventvwr.exe", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string argument in args[1..])
            {
                if (TryOption(argument, "/l:", out string file)) return new StartupTarget(LogSourceKind.File, file);
                if (TryOption(argument, "/c:", out string channel)) return new StartupTarget(LogSourceKind.Channel, channel);
            }
            return null;
        }

        if (args.Length == 2 && args[0] == LogArgument && args[1].Length > 0) return new StartupTarget(LogSourceKind.Channel, args[1]);
        if (args.Length == 2 && args[0] == FileArgument && args[1].Length > 0) return new StartupTarget(LogSourceKind.File, args[1]);
        if (args.Length == 1 && args[0] == OverviewArgument) return new StartupTarget(LogSourceKind.ProblemsOverview, "");
        if (args.Length == 1 && args[0].EndsWith(".evtx", StringComparison.OrdinalIgnoreCase)) return new StartupTarget(LogSourceKind.File, args[0]);
        return null;
    }

    /// <summary>The command-line arguments that reopen <paramref name="source"/>, used when the app restarts itself.</summary>
    public static string[] For(LogSource? source) => source?.Kind switch
    {
        LogSourceKind.Channel => [LogArgument, source.Path],
        LogSourceKind.File => [FileArgument, source.Path],
        LogSourceKind.ProblemsOverview => [OverviewArgument],
        _ => [],
    };

    private static bool TryOption(string argument, string prefix, out string value)
    {
        value = argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? argument[prefix.Length..].Trim('"') : "";
        return value.Length > 0;
    }
}
