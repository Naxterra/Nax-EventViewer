using System.Runtime.InteropServices;

namespace NaxEventViewer.Core.Native;

/// <summary>
/// A Windows bookmark on one event. Windows records the event's position in the log it was read from, which is right
/// even for forwarded events, whose record numbers belong to the PCs they came from. Not thread-safe.
/// </summary>
internal sealed unsafe class Bookmark : IDisposable
{
    private nint _handle = Wevtapi.EvtCreateBookmark(null);
    private bool _set;

    /// <summary>Moves the bookmark to <paramref name="evt"/>.</summary>
    public void MoveTo(nint evt)
    {
        if (_handle != 0 && Wevtapi.EvtUpdateBookmark(_handle, evt)) _set = true;
    }

    /// <summary>The bookmark as XML, which <see cref="Wevtapi.EvtCreateBookmark"/> turns back into a bookmark, or null when it points nowhere.</summary>
    public string? ToXml()
    {
        if (!_set) return null;
        char[] text = new char[512];
        while (true)
        {
            fixed (char* buffer = text)
            {
                // EvtRender measures the buffer in bytes.
                if (Wevtapi.EvtRender(0, _handle, Wevtapi.EvtRenderBookmark, text.Length * sizeof(char), buffer, out int usedBytes, out _))
                {
                    int length = Math.Clamp(usedBytes / sizeof(char), 0, text.Length);
                    while (length > 0 && buffer[length - 1] == '\0') length--;
                    return new string(buffer, 0, length);
                }
                if (Marshal.GetLastPInvokeError() != Wevtapi.ErrorInsufficientBuffer) return null;
                text = new char[Math.Max(usedBytes / sizeof(char) + 1, text.Length * 2)];
            }
        }
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) Wevtapi.EvtClose(handle);
    }
}
