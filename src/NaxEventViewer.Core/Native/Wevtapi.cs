using System.Runtime.InteropServices;

namespace NaxEventViewer.Core.Native;

/// <summary>
/// The parts of the Windows Event Log API (wevtapi.dll) this app uses. The app only reads logs, so functions that
/// change a log or its configuration (EvtClearLog, EvtSetChannelConfigProperty, EvtSaveChannelConfig) are deliberately
/// not declared here. SafetyTests checks that they stay absent.
/// </summary>
internal static unsafe partial class Wevtapi
{
    private const string Dll = "wevtapi.dll";

    public const int ErrorAccessDenied = 5;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorEvtChannelNotFound = 15007;
    public const int ErrorEvtPublisherMetadataNotFound = 15002;
    public const int ErrorEvtMessageNotFound = 15027;
    public const int ErrorEvtMessageIdNotFound = 15028;
    public const int ErrorEvtUnresolvedValueInsert = 15029;
    public const int ErrorEvtUnresolvedParameterInsert = 15030;
    public const int ErrorEvtMaxInsertsReached = 15031;

    public const int Infinite = -1;

    // EVT_QUERY_FLAGS
    public const int EvtQueryChannelPath = 0x1;
    public const int EvtQueryFilePath = 0x2;
    public const int EvtQueryReverseDirection = 0x200;
    public const int EvtQueryTolerateQueryErrors = 0x1000;

    // EVT_RENDER_CONTEXT_FLAGS and EVT_RENDER_FLAGS
    public const int EvtRenderContextSystem = 1;
    public const int EvtRenderEventValues = 0;
    public const int EvtRenderEventXml = 1;

    // EVT_FORMAT_MESSAGE_FLAGS
    public const int EvtFormatMessageEvent = 1;
    public const int EvtFormatMessageLevel = 2;
    public const int EvtFormatMessageTask = 3;
    public const int EvtFormatMessageOpcode = 4;
    public const int EvtFormatMessageKeyword = 5;

    // EVT_OPEN_LOG_FLAGS and EVT_LOG_PROPERTY_ID
    public const int EvtOpenChannelPath = 1;
    public const int EvtOpenFilePath = 2;
    public const int EvtLogNumberOfLogRecords = 5;
    public const int EvtLogOldestRecordNumber = 6;

    // EVT_EVENT_PROPERTY_ID
    public const int EvtEventPath = 1;

    // EVT_CHANNEL_CONFIG_PROPERTY_ID
    public const int EvtChannelConfigEnabled = 0;
    public const int EvtChannelConfigType = 2;

    // EVT_SEEK_FLAGS
    public const int EvtSeekRelativeToBookmark = 4;

    // EVT_SUBSCRIBE_FLAGS and EVT_SUBSCRIBE_NOTIFY_ACTION
    public const int EvtSubscribeToFutureEvents = 1;
    public const int EvtSubscribeStartAtOldestRecord = 2;
    public const int EvtSubscribeStartAfterBookmark = 3;
    public const int EvtSubscribeActionError = 0;
    public const int EvtSubscribeActionDeliver = 1;

    // EVT_EXPORTLOG_FLAGS
    public const int EvtExportLogChannelPath = 0x1;
    public const int EvtExportLogFilePath = 0x2;

    // EVT_VARIANT_TYPE
    public const uint EvtVarTypeNull = 0;
    public const uint EvtVarTypeString = 1;
    public const uint EvtVarTypeByte = 4;
    public const uint EvtVarTypeUInt16 = 6;
    public const uint EvtVarTypeUInt32 = 8;
    public const uint EvtVarTypeUInt64 = 10;
    public const uint EvtVarTypeBoolean = 13;
    public const uint EvtVarTypeFileTime = 17;
    public const uint EvtVarTypeSid = 19;
    public const uint EvtVarTypeHexInt32 = 20;
    public const uint EvtVarTypeHexInt64 = 21;
    public const uint EvtVarTypeMask = 0x7F;

    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtQuery(nint session, string? path, string? query, int flags);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtNext(nint resultSet, int eventsSize, nint* events, int timeout, int flags, out int returned);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtSeek(nint resultSet, long position, nint bookmark, int timeout, int flags);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint EvtCreateRenderContext(int valuePathsCount, nint valuePaths, int flags);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtRender(nint context, nint fragment, int flags, int bufferSize, void* buffer, out int bufferUsed, out int propertyCount);

    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtOpenPublisherMetadata(nint session, string publisherId, string? logFilePath, int locale, int flags);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtFormatMessage(nint publisherMetadata, nint evt, uint messageId, int valueCount, nint values, int flags, int bufferSize, char* buffer, out int bufferUsed);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtClose(nint handle);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint EvtOpenChannelEnum(nint session, int flags);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtNextChannelPath(nint channelEnum, int bufferSize, char* buffer, out int bufferUsed);

    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtOpenLog(nint session, string path, int flags);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtGetLogInfo(nint log, int propertyId, int bufferSize, void* buffer, out int bufferUsed);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtGetEventInfo(nint evt, int propertyId, int bufferSize, void* buffer, out int bufferUsed);

    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtOpenChannelConfig(nint session, string channelPath, int flags);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtGetChannelConfigProperty(nint channelConfig, int propertyId, int flags, int bufferSize, void* buffer, out int bufferUsed);

    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtCreateBookmark(string? bookmarkXml);

    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtSubscribe(nint session, nint signalEvent, string? channelPath, string? query, nint bookmark, nint context, delegate* unmanaged<int, nint, nint, int> callback, int flags);

    /// <summary>Copies matching events into a new .evtx file. It reads the source log and never changes it.</summary>
    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtExportLog(nint session, string? path, string query, string targetFilePath, int flags);
}

/// <summary>EVT_VARIANT: an 8-byte value or pointer, followed by an element count and a type code.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct EvtVariant
{
    [FieldOffset(0)] public ulong UInt64;
    [FieldOffset(0)] public nint Pointer;
    [FieldOffset(0)] public uint UInt32;
    [FieldOffset(0)] public ushort UInt16;
    [FieldOffset(0)] public byte Byte;
    [FieldOffset(8)] public uint Count;
    [FieldOffset(12)] public uint Type;

    public readonly uint BaseType => Type & Wevtapi.EvtVarTypeMask;
}
