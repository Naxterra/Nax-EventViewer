using System.Runtime.InteropServices;
using System.Security.Principal;

namespace NaxEventViewer.Core.Native;

/// <summary>
/// Reads the System section of an event (provider, ID, level, time and so on) through a system render context. This
/// is the cheap part of reading an event: it never loads a provider's message resources. One instance belongs to one
/// thread.
/// </summary>
internal sealed unsafe class SystemRenderer : IDisposable
{
    // EVT_SYSTEM_PROPERTY_ID
    private const int ProviderName = 0;
    private const int EventId = 2;
    private const int Level = 4;
    private const int Task = 5;
    private const int Opcode = 6;
    private const int Keywords = 7;
    private const int TimeCreated = 8;
    private const int EventRecordId = 9;
    private const int ProcessId = 12;
    private const int ThreadId = 13;
    private const int Channel = 14;
    private const int Computer = 15;
    private const int UserId = 16;

    private readonly nint _context;
    private readonly StringPool _strings;
    private byte[] _buffer = new byte[4096];

    public SystemRenderer(StringPool strings)
    {
        _strings = strings;
        _context = Wevtapi.EvtCreateRenderContext(0, 0, Wevtapi.EvtRenderContextSystem);
        if (_context == 0) throw new EventLogException("Windows could not prepare to read events.", Marshal.GetLastPInvokeError());
    }

    /// <summary>Renders one event. <paramref name="fallbackChannel"/> is used when the event names no channel.</summary>
    public EventItem? Render(nint evt, string fallbackChannel)
    {
        while (true)
        {
            fixed (byte* buffer = _buffer)
            {
                if (Wevtapi.EvtRender(_context, evt, Wevtapi.EvtRenderEventValues, _buffer.Length, buffer, out int used, out int count))
                    return Read((EvtVariant*)buffer, count, fallbackChannel);

                int error = Marshal.GetLastPInvokeError();
                if (error != Wevtapi.ErrorInsufficientBuffer) return null;
                _buffer = new byte[Math.Max(used, _buffer.Length * 2)];
            }
        }
    }

    private EventItem Read(EvtVariant* values, int count, string fallbackChannel)
    {
        EvtVariant Value(int index) => index < count ? values[index] : default;

        return new EventItem
        {
            Provider = String(Value(ProviderName)) ?? "",
            Channel = String(Value(Channel)) ?? fallbackChannel,
            Computer = String(Value(Computer)) ?? "",
            EventId = Value(EventId).BaseType == Wevtapi.EvtVarTypeUInt16 ? Value(EventId).UInt16 : (ushort)0,
            Level = Value(Level).BaseType == Wevtapi.EvtVarTypeByte ? Value(Level).Byte : (byte)0,
            Task = Value(Task).BaseType == Wevtapi.EvtVarTypeUInt16 ? Value(Task).UInt16 : (ushort)0,
            Opcode = Value(Opcode).BaseType == Wevtapi.EvtVarTypeByte ? Value(Opcode).Byte : (byte)0,
            Keywords = Value(Keywords).BaseType is Wevtapi.EvtVarTypeHexInt64 or Wevtapi.EvtVarTypeUInt64 ? Value(Keywords).UInt64 : 0,
            TimeCreatedUtc = Value(TimeCreated).BaseType == Wevtapi.EvtVarTypeFileTime ? DateTime.FromFileTimeUtc((long)Value(TimeCreated).UInt64) : DateTime.MinValue,
            RecordId = Value(EventRecordId).BaseType == Wevtapi.EvtVarTypeUInt64 ? Value(EventRecordId).UInt64 : 0,
            ProcessId = Value(ProcessId).BaseType == Wevtapi.EvtVarTypeUInt32 ? Value(ProcessId).UInt32 : 0,
            ThreadId = Value(ThreadId).BaseType == Wevtapi.EvtVarTypeUInt32 ? Value(ThreadId).UInt32 : 0,
            UserSid = Sid(Value(UserId)),
        };
    }

    private string? String(EvtVariant value) =>
        value.BaseType == Wevtapi.EvtVarTypeString && value.Pointer != 0
            ? _strings.Get(MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)value.Pointer))
            : null;

    private string? Sid(EvtVariant value)
    {
        if (value.BaseType != Wevtapi.EvtVarTypeSid || value.Pointer == 0) return null;
        try
        {
            return _strings.Get(new SecurityIdentifier(value.Pointer).Value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Dispose() => Wevtapi.EvtClose(_context);
}

/// <summary>
/// Shares one string object for repeated values (provider, channel, computer, user), which keeps memory small for
/// logs with hundreds of thousands of events. One instance belongs to one thread.
/// </summary>
internal sealed class StringPool
{
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public StringPool() => _lookup = _set.GetAlternateLookup<ReadOnlySpan<char>>();

    public string Get(ReadOnlySpan<char> value)
    {
        if (_lookup.TryGetValue(value, out string? existing)) return existing;
        string created = value.ToString();
        _set.Add(created);
        return created;
    }
}
