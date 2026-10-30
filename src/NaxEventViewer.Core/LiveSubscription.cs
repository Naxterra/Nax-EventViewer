using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using NaxEventViewer.Core.Native;

namespace NaxEventViewer.Core;

/// <summary>
/// Delivers new events of one log as Windows writes them. Each event arrives with its message already formatted.
/// The callback runs on a Windows thread pool thread.
/// </summary>
public sealed unsafe class LiveSubscription : IDisposable
{
    // The native callback finds its subscription through this table instead of a GCHandle, so a callback that is
    // still running while the subscription is disposed can never touch freed memory.
    private static readonly ConcurrentDictionary<nint, LiveSubscription> Active = new();
    private static long _nextId;

    private readonly object _gate = new();
    private readonly string _channel;
    private readonly Action<EventItem> _onEvent;
    private readonly Action<string> _onError;
    private readonly SystemRenderer _renderer;
    private readonly MessageFormatter _formatter = new();
    private readonly MessageStore _store;
    private readonly nint _id;
    private nint _subscription;
    private bool _disposed;

    private LiveSubscription(string channel, MessageStore store, Action<EventItem> onEvent, Action<string> onError)
    {
        _channel = channel;
        _store = store;
        _onEvent = onEvent;
        _onError = onError;
        _renderer = new SystemRenderer(new Native.StringPool());
        _id = (nint)Interlocked.Increment(ref _nextId);
    }

    /// <summary>
    /// Starts delivering events of <paramref name="channel"/> that come after record <paramref name="afterRecordId"/>,
    /// or only future events when it is null. <paramref name="onError"/> receives a description when Windows ends the
    /// subscription, for example because the log was cleared; no further events arrive after that.
    /// </summary>
    public static LiveSubscription Start(string channel, ulong? afterRecordId, MessageStore store, Action<EventItem> onEvent, Action<string> onError)
    {
        var subscription = new LiveSubscription(channel, store, onEvent, onError);
        Active[subscription._id] = subscription;

        nint bookmark = 0;
        if (afterRecordId is ulong recordId)
        {
            string id = recordId.ToString(CultureInfo.InvariantCulture);
            bookmark = Wevtapi.EvtCreateBookmark($"<BookmarkList><Bookmark Channel=\"{SecurityElement.Escape(channel)}\" RecordId=\"{id}\" IsCurrent=\"true\"/></BookmarkList>");
        }
        try
        {
            // No EvtSubscribeTolerateQueryErrors: for a single log it would turn a failure into a subscription that never delivers.
            int flags = bookmark != 0 ? Wevtapi.EvtSubscribeStartAfterBookmark : Wevtapi.EvtSubscribeToFutureEvents;
            subscription._subscription = Wevtapi.EvtSubscribe(0, 0, channel, "*", bookmark, subscription._id, &Callback, flags);
            if (subscription._subscription == 0)
            {
                var error = EventLogException.FromLastError($"Windows could not watch {channel} for new events.");
                subscription.Dispose();
                throw error;
            }
        }
        finally
        {
            if (bookmark != 0) Wevtapi.EvtClose(bookmark);
        }
        return subscription;
    }

    [UnmanagedCallersOnly]
    private static int Callback(int action, nint context, nint evt) => Dispatch(action, context, evt);

    /// <summary>What the native callback does; tests call it directly to deliver the error action Windows sends.</summary>
    internal static int Dispatch(int action, nint context, nint evt)
    {
        if (!Active.TryGetValue(context, out LiveSubscription? subscription)) return 0;
        if (action == Wevtapi.EvtSubscribeActionDeliver) subscription.Deliver(evt);
        // For the error action, the event handle carries the Win32 error code instead of an event.
        else if (action == Wevtapi.EvtSubscribeActionError) subscription.Fail((int)evt);
        return 0;
    }

    internal nint Id => _id;

    private void Fail(int error)
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        try
        {
            _onError($"Live updates for {_channel} stopped. {new System.ComponentModel.Win32Exception(error).Message}");
        }
        catch (Exception)
        {
            // An exception must never cross back into the native callback.
        }
    }

    private void Deliver(nint evt)
    {
        EventItem? item;
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                item = _renderer.Render(evt, _channel);
                // Nobody listens to the new item yet, so setting the text here on the pool thread is safe.
                item?.SetFormatted(_store.Add(_formatter.FormatMessage(evt, item)), _formatter.FormatTask(evt, item));
            }
            catch (Exception)
            {
                // An exception must never cross back into the native callback.
                return;
            }
        }
        if (item is not null)
        {
            try
            {
                _onEvent(item);
            }
            catch (Exception)
            {
                // Same as above.
            }
        }
    }

    public void Dispose()
    {
        Active.TryRemove(_id, out _);
        nint subscription = Interlocked.Exchange(ref _subscription, 0);
        if (subscription != 0) Wevtapi.EvtClose(subscription);
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _renderer.Dispose();
            _formatter.Dispose();
        }
    }
}
