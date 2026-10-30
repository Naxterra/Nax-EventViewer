namespace NaxEventViewer.Core;

/// <summary>Receives the results of a load. Every callback runs on the loader's background thread.</summary>
public sealed class LoadCallbacks
{
    public required Action<IReadOnlyList<EventItem>> RecordsRead { get; init; }
    public required Action<ReadSummary> RecordsDone { get; init; }
    public required Action<IReadOnlyList<FormattedMessage>> MessagesFormatted { get; init; }
    public required Action MessagesDone { get; init; }

    /// <summary>Receives an unexpected error that ended a pass early, so it can be written to the crash log.</summary>
    public Action<Exception>? UnexpectedError { get; init; }
}

/// <summary>Runs both reading passes for one source on a dedicated background thread.</summary>
public static class LogLoader
{
    /// <summary>Starts reading. Messages go into <paramref name="store"/>, which the live subscription of the same log should share.</summary>
    public static Task Start(LogSource source, MessageStore store, LoadCallbacks callbacks, CancellationToken cancellation) =>
        Task.Factory.StartNew(() => Run(source, store, callbacks, cancellation), cancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // Every exception is caught in both passes: nobody observes this task, so an escaped exception would leave the
    // window waiting for RecordsDone or MessagesDone forever.
    private static void Run(LogSource source, MessageStore store, LoadCallbacks callbacks, CancellationToken cancellation)
    {
        var all = new List<EventItem>();
        ReadSummary summary;
        try
        {
            summary = EventReader.ReadRecords(source, batch =>
            {
                all.AddRange(batch);
                callbacks.RecordsRead(batch);
            }, cancellation);
        }
        catch (EventLogException ex)
        {
            summary = new ReadSummary { Count = all.Count };
            summary.AddProblem(ex.Message);
        }
        catch (Exception ex)
        {
            callbacks.UnexpectedError?.Invoke(ex);
            summary = new ReadSummary { Count = all.Count };
            summary.AddProblem($"Reading stopped early because of an unexpected error. {ex.Message}");
        }
        if (cancellation.IsCancellationRequested) return;
        callbacks.RecordsDone(summary);

        try
        {
            EventReader.FormatMessages(source, all, store, callbacks.MessagesFormatted, cancellation);
        }
        catch (EventLogException)
        {
            // The list stays usable without messages; the detail pane formats a selected event on its own.
        }
        catch (Exception ex)
        {
            callbacks.UnexpectedError?.Invoke(ex);
        }
        if (!cancellation.IsCancellationRequested) callbacks.MessagesDone();
    }
}
