using System.Runtime.InteropServices;

namespace NaxEventViewer.App;

/// <summary>
/// A named mutex that exists while any window of the app is open. The installer and the uninstaller check it
/// (AppMutex in the Inno Setup script) and wait until every window is closed.
/// </summary>
internal static partial class RunningMutex
{
    internal const string Name = "NaxEventViewer.Running";

    // Everyone in the session may open the mutex, and its low integrity label lets a normal process, such as a second
    // window or the installer, open the mutex that a window running as administrator created. Without this, Windows
    // gives that mutex the administrator's default permissions and a high integrity label, and only elevated
    // processes could see that the app is running.
    private const string Permissions = "D:(A;;GA;;;WD)S:(ML;;NW;;;LW)";

    private static nint _handle;

    /// <summary>Creates or opens the mutex and keeps it until the process ends. Tries again on the next call if that failed.</summary>
    public static void Hold()
    {
        if (_handle != 0) return;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(Permissions, 1, out nint descriptor, 0)) return;
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            _handle = CreateMutex(attributes, initialOwner: false, Name);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int Inherit;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, int revision, out nint securityDescriptor, nint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateMutex(in SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
