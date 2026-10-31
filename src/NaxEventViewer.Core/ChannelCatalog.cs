using System.Runtime.InteropServices;
using NaxEventViewer.Core.Native;

namespace NaxEventViewer.Core;

public enum ChannelType
{
    Admin,
    Operational,
    Analytic,
    Debug,
    Unknown,
}

/// <summary>What the app knows about a log before it reads any events. A null count means Windows did not tell.</summary>
public sealed record ChannelInfo(string Name, bool Enabled, ChannelType Type, long? RecordCount)
{
    /// <summary>Analytic and debug logs are hidden by default, as in the Windows Event Viewer.</summary>
    public bool IsAnalyticOrDebug => Type is ChannelType.Analytic or ChannelType.Debug;
}

/// <summary>Lists the logs on this PC and reads their settings and sizes. It never changes a log.</summary>
public static unsafe class ChannelCatalog
{
    /// <summary>The logs the Windows Event Viewer shows under Windows Logs, in its order.</summary>
    public static readonly IReadOnlyList<string> WindowsLogs = ["Application", "Security", "Setup", "System", "ForwardedEvents"];

    public static List<string> ListChannels()
    {
        var channels = new List<string>();
        nint enumerator = Wevtapi.EvtOpenChannelEnum(0, 0);
        if (enumerator == 0) throw EventLogException.FromLastError(CoreText.Get("Error_ListLogs"));
        try
        {
            var buffer = new char[512];
            while (true)
            {
                bool ok;
                int used;
                fixed (char* pointer = buffer)
                    ok = Wevtapi.EvtNextChannelPath(enumerator, buffer.Length, pointer, out used);
                if (ok)
                {
                    channels.Add(new string(buffer, 0, Math.Max(0, used - 1)));
                    continue;
                }
                int error = Marshal.GetLastPInvokeError();
                ListingStep step = ClassifyListingError(error);
                if (step == ListingStep.Retry)
                {
                    buffer = new char[used];
                    continue;
                }
                if (step == ListingStep.Done) break;
                throw new EventLogException(CoreText.F("Error_ListingStopped", channels.Count, new System.ComponentModel.Win32Exception(error).Message), error);
            }
        }
        finally
        {
            Wevtapi.EvtClose(enumerator);
        }
        return channels;
    }

    public static ChannelInfo ReadInfo(string channel)
    {
        bool enabled = true;
        ChannelType type = ChannelType.Unknown;
        nint config = Wevtapi.EvtOpenChannelConfig(0, channel, 0);
        if (config != 0)
        {
            try
            {
                if (ReadProperty(config, Wevtapi.EvtChannelConfigEnabled) is { } enabledValue && enabledValue.BaseType == Wevtapi.EvtVarTypeBoolean)
                    enabled = enabledValue.UInt32 != 0;
                if (ReadProperty(config, Wevtapi.EvtChannelConfigType) is { } typeValue && typeValue.BaseType == Wevtapi.EvtVarTypeUInt32 && typeValue.UInt32 <= 3)
                    type = (ChannelType)typeValue.UInt32;
            }
            finally
            {
                Wevtapi.EvtClose(config);
            }
        }
        return new ChannelInfo(channel, enabled, type, CountRecords(channel));
    }

    /// <summary>Returns the number of events in a log or .evtx file, or null when Windows cannot open it.</summary>
    public static long? CountRecords(string path, bool isFile = false)
    {
        nint log = Wevtapi.EvtOpenLog(0, path, isFile ? Wevtapi.EvtOpenFilePath : Wevtapi.EvtOpenChannelPath);
        if (log == 0) return null;
        try
        {
            EvtVariant value;
            if (!Wevtapi.EvtGetLogInfo(log, Wevtapi.EvtLogNumberOfLogRecords, sizeof(EvtVariant), &value, out _)) return null;
            return value.BaseType == Wevtapi.EvtVarTypeUInt64 ? (long)value.UInt64 : null;
        }
        finally
        {
            Wevtapi.EvtClose(log);
        }
    }

    internal enum ListingStep
    {
        Retry,
        Done,
        Fail,
    }

    /// <summary>Only "no more items" ends the list normally; any other error must be reported, not taken as the end.</summary>
    internal static ListingStep ClassifyListingError(int error) => error switch
    {
        Wevtapi.ErrorInsufficientBuffer => ListingStep.Retry,
        Wevtapi.ErrorNoMoreItems => ListingStep.Done,
        _ => ListingStep.Fail,
    };

    /// <summary>Returns 0 when the log can be opened, or the Win32 error that prevents it.</summary>
    internal static int OpenError(string channel)
    {
        nint log = Wevtapi.EvtOpenLog(0, channel, Wevtapi.EvtOpenChannelPath);
        if (log == 0) return Marshal.GetLastPInvokeError();
        Wevtapi.EvtClose(log);
        return 0;
    }

    private static EvtVariant? ReadProperty(nint config, int property)
    {
        // Enabled and Type are plain numbers, so the variant itself is the whole answer.
        byte* buffer = stackalloc byte[256];
        if (!Wevtapi.EvtGetChannelConfigProperty(config, property, 0, 256, buffer, out _)) return null;
        return *(EvtVariant*)buffer;
    }
}
