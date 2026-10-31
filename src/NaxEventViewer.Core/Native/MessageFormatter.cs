using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NaxEventViewer.Core.Native;

/// <summary>
/// Turns events into readable text through each provider's message resources. Opening a provider's metadata is the
/// expensive step, so every provider is opened once and kept open. One instance belongs to one thread.
/// </summary>
internal sealed unsafe class MessageFormatter : IDisposable
{
    private readonly Dictionary<string, nint> _publishers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Provider, ushort Task), string?> _taskNames = new();
    private char[] _buffer = new char[2048];

    /// <summary>Returns the event's message, or a description of its data when the provider has no message for it.</summary>
    public string FormatMessage(nint evt, EventItem item)
    {
        nint publisher = Publisher(item.Provider);
        string? text = publisher == 0 ? null : Format(publisher, evt, Wevtapi.EvtFormatMessageEvent);
        if (!string.IsNullOrWhiteSpace(text)) return text.TrimEnd();
        return DescribeWithoutMessage(evt, item);
    }

    /// <summary>Returns the task category name, or null when the provider defines none.</summary>
    public string? FormatTask(nint evt, EventItem item)
    {
        if (item.Task == 0) return null;
        var key = (item.Provider, item.Task);
        if (_taskNames.TryGetValue(key, out string? cached)) return cached;
        nint publisher = Publisher(item.Provider);
        string? name = publisher == 0 ? null : Format(publisher, evt, Wevtapi.EvtFormatMessageTask)?.Trim();
        if (string.IsNullOrEmpty(name)) name = null;
        _taskNames[key] = name;
        return name;
    }

    /// <summary>Returns a display name for the given part of the event (opcode, keywords), or null when none exists.</summary>
    public string? FormatPart(nint evt, EventItem item, int flags)
    {
        nint publisher = Publisher(item.Provider);
        if (publisher == 0) return null;
        string? text = Format(publisher, evt, flags);
        if (text is null) return null;
        // Keyword names come back as a list of null-separated strings.
        text = string.Join(", ", text.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length == 0 ? null : text;
    }

    private nint Publisher(string provider)
    {
        if (provider.Length == 0) return 0;
        if (_publishers.TryGetValue(provider, out nint handle)) return handle;
        handle = Wevtapi.EvtOpenPublisherMetadata(0, provider, null, 0, 0);
        _publishers[provider] = handle;
        return handle;
    }

    private string? Format(nint publisher, nint evt, int flags)
    {
        while (true)
        {
            fixed (char* buffer = _buffer)
            {
                bool ok = Wevtapi.EvtFormatMessage(publisher, evt, 0, 0, 0, flags, _buffer.Length, buffer, out int used);
                int error = ok ? 0 : Marshal.GetLastPInvokeError();
                if (error == Wevtapi.ErrorInsufficientBuffer)
                {
                    _buffer = new char[Math.Max(used, _buffer.Length * 2)];
                    continue;
                }

                // A message with an insert that could not be resolved is still the best text there is.
                if (ok || error is Wevtapi.ErrorEvtUnresolvedValueInsert or Wevtapi.ErrorEvtUnresolvedParameterInsert or Wevtapi.ErrorEvtMaxInsertsReached)
                {
                    int length = Math.Clamp(used, 0, _buffer.Length);
                    while (length > 0 && buffer[length - 1] == '\0') length--;
                    return new string(buffer, 0, length);
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Builds the text shown when a provider is not installed on this PC or has no message for the event: the event's
    /// own data values, as the Windows Event Viewer does.
    /// </summary>
    private string DescribeWithoutMessage(nint evt, EventItem item)
    {
        var text = new StringBuilder();
        text.Append(CoreText.F("Message_NoText", item.EventId, item.Provider.Length == 0 ? CoreText.Get("Message_UnnamedSource") : item.Provider));
        string? xml = RenderXml(evt);
        if (xml is not null)
        {
            var data = ExtractData(xml);
            if (data.Count > 0)
            {
                text.Append(' ').Append(CoreText.Get("Message_DataFollows"));
                foreach (var (name, value) in data)
                {
                    text.AppendLine();
                    if (name.Length > 0) text.Append(name).Append(": ");
                    text.Append(value);
                }
            }
        }
        return text.ToString();
    }

    public string? RenderXml(nint evt)
    {
        while (true)
        {
            fixed (char* buffer = _buffer)
            {
                // EvtRender measures the buffer in bytes.
                if (Wevtapi.EvtRender(0, evt, Wevtapi.EvtRenderEventXml, _buffer.Length * sizeof(char), buffer, out int usedBytes, out _))
                {
                    int length = Math.Clamp(usedBytes / sizeof(char), 0, _buffer.Length);
                    while (length > 0 && buffer[length - 1] == '\0') length--;
                    return new string(buffer, 0, length);
                }
                if (Marshal.GetLastPInvokeError() != Wevtapi.ErrorInsufficientBuffer) return null;
                _buffer = new char[Math.Max(usedBytes / sizeof(char) + 1, _buffer.Length * 2)];
            }
        }
    }

    /// <summary>Returns the name and text of every leaf element under EventData or UserData.</summary>
    internal static List<(string Name, string Value)> ExtractData(string xml)
    {
        var result = new List<(string, string)>();
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return result;
        }

        foreach (XElement section in document.Descendants().Where(e => e.Name.LocalName is "EventData" or "UserData"))
        {
            foreach (XElement leaf in section.Descendants().Where(e => !e.HasElements))
            {
                string name = (string?)leaf.Attribute("Name") ?? (leaf.Name.LocalName == "Data" ? "" : leaf.Name.LocalName);
                string value = leaf.Value.Trim();
                if (name.Length > 0 || value.Length > 0) result.Add((name, value));
            }
        }
        return result;
    }

    public void Dispose()
    {
        foreach (nint handle in _publishers.Values)
            if (handle != 0) Wevtapi.EvtClose(handle);
        _publishers.Clear();
    }
}
