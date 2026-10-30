using NaxEventViewer.Core.Tests;

// Console test runner: prints every test as file::name, its scenario and PASS or FAIL. The exit code is the number of
// failures.
var suites = new (string File, IEnumerable<(string Name, string Scenario, Action Body)> Tests)[]
{
    ("FilterTests.cs", FilterTests.All()),
    ("ChannelTreeTests.cs", ChannelTreeTests.All()),
    ("ExportTests.cs", ExportTests.All()),
    ("ReaderTests.cs", ReaderTests.All()),
    ("MessageStoreTests.cs", MessageStoreTests.All()),
    ("SafetyTests.cs", SafetyTests.All()),
};

int failures = 0, count = 0;
foreach (var (file, tests) in suites)
{
    foreach (var (name, scenario, body) in tests)
    {
        count++;
        string result;
        try
        {
            body();
            result = "PASS";
        }
        catch (Exception ex)
        {
            failures++;
            result = "FAIL: " + ex.Message;
        }
        Console.WriteLine($"{file}::{name} | {scenario} | {result}");
    }
}
Fixtures.Cleanup();
Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} of {count} tests failed.");
return failures;

namespace NaxEventViewer.Core.Tests
{
    internal static class Check
    {
        public static void That(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
        }
    }

    /// <summary>Temporary folders and a saved copy of this PC's Application log, removed when the run ends.</summary>
    internal static class Fixtures
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "nax-eventviewer-tests-" + Environment.ProcessId);
        private static readonly Lazy<string> ApplicationCopy = new(() =>
        {
            string path = Path.Combine(TempFolder("fixture"), "application.evtx");
            EventExport.SaveEvtx(LogSource.ForChannel("Application"), path);
            return path;
        });

        /// <summary>A saved copy of the Application log, so tests read a file that cannot change under them.</summary>
        public static string ApplicationLogFile => ApplicationCopy.Value;

        public static string TempFolder(string name)
        {
            string folder = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static void Cleanup()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        public static EventItem Item(DateTime timeUtc, byte level = 4, ushort id = 1, string provider = "Test", ulong keywords = 0, ulong recordId = 1, string? message = null)
        {
            var item = new EventItem { Channel = "Test", Provider = provider, TimeCreatedUtc = timeUtc, Level = level, EventId = id, Keywords = keywords, RecordId = recordId };
            if (message is not null) item.SetFormatted(message, null);
            return item;
        }
    }
}
