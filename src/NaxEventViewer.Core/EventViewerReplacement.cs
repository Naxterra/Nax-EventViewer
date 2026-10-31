using Microsoft.Win32;

namespace NaxEventViewer.Core;

/// <summary>
/// Makes Windows start this app in place of the Windows Event Viewer, through an Image File Execution Options
/// "Debugger" value for <c>eventvwr.exe</c>, the same mechanism that tools use to replace Task Manager. Windows then
/// starts this app whenever <c>eventvwr.exe</c> would start, such as from the Win+X menu or by typing "eventvwr". The
/// Start menu entry under Windows Tools opens <c>eventvwr.msc</c> in the Microsoft Management Console instead, which
/// this setting does not affect.
///
/// The value lives under HKLM, so changing it needs administrator rights. It is removed again when the user turns the
/// option off and when the app is uninstalled, so the Windows Event Viewer keeps working.
/// </summary>
public static class EventViewerReplacement
{
    private const string IfeoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\eventvwr.exe";
    private const string DebuggerValue = "Debugger";

    /// <summary>The program Windows currently starts in place of <c>eventvwr.exe</c>, or null when nothing replaces it.</summary>
    public static string? CurrentTarget()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(IfeoKey);
            return key?.GetValue(DebuggerValue) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>True when the Windows Event Viewer is replaced by this executable.</summary>
    public static bool IsReplacedBy(string executablePath) => IsTarget(CurrentTarget(), executablePath);

    /// <summary>The program that replaces the Windows Event Viewer when it is some other tool, or null when nothing or this app does.</summary>
    public static string? OtherTarget(string executablePath)
    {
        string? target = CurrentTarget();
        if (string.IsNullOrWhiteSpace(target) || IsTarget(target, executablePath)) return null;
        return Unquote(target);
    }

    /// <summary>
    /// True when Windows may be pointed at <paramref name="executablePath"/>: only administrators can change the file
    /// and its folders. A copy installed for one user lives in that user's profile, where any program the user runs
    /// could replace the file that Windows would then start.
    /// </summary>
    public static bool CanReplaceWith(string executablePath) => ProtectedLocation.IsAdminOnly(executablePath);

    /// <summary>
    /// Makes Windows start <paramref name="executablePath"/> in place of the Windows Event Viewer. Needs administrator
    /// rights. Throws <see cref="InvalidOperationException"/> when the file is not in an administrator-only folder.
    /// </summary>
    public static void Enable(string executablePath)
    {
        if (!CanReplaceWith(executablePath)) throw new InvalidOperationException(CoreText.Get("Replace_NeedsMachineInstall"));
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(IfeoKey, writable: true);
        key.SetValue(DebuggerValue, Quote(executablePath), RegistryValueKind.String);
    }

    /// <summary>
    /// Restores the Windows Event Viewer if this executable replaces it. A replacement that another program set up is
    /// left alone. Needs administrator rights when there is something to remove.
    /// </summary>
    public static void DisableIfOurs(string executablePath)
    {
        if (!IsReplacedBy(executablePath)) return;
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(IfeoKey, writable: true);
        key?.DeleteValue(DebuggerValue, throwOnMissingValue: false);
    }

    /// <summary>The value written to the registry: the path in quotes, so a path with spaces starts the right program.</summary>
    internal static string Quote(string executablePath) => "\"" + executablePath + "\"";

    /// <summary>True when <paramref name="target"/>, with or without quotes, names the same file as <paramref name="executablePath"/>.</summary>
    public static bool IsTarget(string? target, string executablePath) =>
        !string.IsNullOrWhiteSpace(target) && PathsEqual(Unquote(target), executablePath);

    private static string Unquote(string value) => value.Trim().Trim('"');

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
