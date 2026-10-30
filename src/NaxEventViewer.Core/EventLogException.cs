using NaxEventViewer.Core.Native;

namespace NaxEventViewer.Core;

/// <summary>An error reported by the Windows Event Log API, with the Win32 error code.</summary>
public sealed class EventLogException(string message, int errorCode) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;

    public bool IsAccessDenied => ErrorCode == Wevtapi.ErrorAccessDenied;

    /// <summary>Builds the exception from the last Win32 error, with the system's text for that error appended.</summary>
    internal static EventLogException FromLastError(string what)
    {
        int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return new EventLogException($"{what} {new System.ComponentModel.Win32Exception(error).Message}", error);
    }
}

/// <summary>A save that was refused because there was nothing to write, so no file was created.</summary>
public sealed class NothingToSaveException(string message) : InvalidOperationException(message);
