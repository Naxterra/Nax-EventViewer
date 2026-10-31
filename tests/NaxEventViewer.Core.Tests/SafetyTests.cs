using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NaxEventViewer.Core.Tests;

/// <summary>Guards that keep the app a pure reader of event logs.</summary>
internal static class SafetyTests
{
    // EvtClearLog is not in this list: the user may clear a log on purpose, which the test below confines to one place.
    private static readonly string[] ChangingFunctions =
    [
        "EvtSetChannelConfigProperty",
        "EvtSaveChannelConfig",
        "EvtArchiveExportedLog",
        "ClearEventLogW",
        "ClearEventLogA",
        "ReportEventW",
        "ReportEventA",
        "RegisterEventSourceW",
        "RegisterEventSourceA",
    ];

    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("core_imports_no_function_that_writes_events_or_reconfigures_a_log",
            "The core library imports no Windows function that writes an event or changes a log's settings. Clearing a log is checked by the next test.",
            () =>
            {
                string[] imported = ImportedFunctions(typeof(EventItem).Assembly.Location);
                Check.That(imported.Length > 10, $"the scan found only {imported.Length} imports, so it is not looking at the right methods");
                CheckNoChangingFunction(imported);
            });

        yield return ("app_imports_no_function_that_writes_events_or_reconfigures_a_log",
            "The app's own program file imports no Windows function that writes an event or changes a log's settings either.",
            () => CheckNoChangingFunction(ImportedFunctions(AppAssemblyPath())));

        yield return ("logs_are_cleared_only_from_the_confirmed_clear_command",
            "Only LogCleaner calls the Windows function that clears a log, and the app calls LogCleaner in exactly one place: the Clear log command, after a confirmation dialog whose default answer is Cancel.",
            () =>
            {
                string src = Path.Combine(Fixtures.RepositoryRoot, "src");
                var sources = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    .ToDictionary(f => Path.GetRelativePath(src, f), File.ReadAllText);

                string[] clearCallers = sources.Where(s => s.Value.Contains("EvtClearLog(")).Select(s => s.Key).Order().ToArray();
                Check.That(clearCallers.SequenceEqual([Path.Combine("NaxEventViewer.Core", "LogCleaner.cs"), Path.Combine("NaxEventViewer.Core", "Native", "Wevtapi.cs")]),
                    "EvtClearLog appears in: " + string.Join(", ", clearCallers));

                var appCalls = sources.Where(s => !s.Key.EndsWith("LogCleaner.cs", StringComparison.Ordinal))
                    .SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s.Value, @"LogCleaner\.Clear\(").Select(_ => s.Key)).ToArray();
                Check.That(appCalls.SequenceEqual([Path.Combine("NaxEventViewer.App", "MainWindow.xaml.cs")]), "LogCleaner.Clear is called from: " + string.Join(", ", appCalls));

                string window = sources[Path.Combine("NaxEventViewer.App", "MainWindow.xaml.cs")];
                int handler = window.IndexOf("private async void Clear_Click(", StringComparison.Ordinal);
                int call = window.IndexOf("LogCleaner.Clear(", StringComparison.Ordinal);
                int nextMember = window.IndexOf("    private ", handler + 1, StringComparison.Ordinal);
                Check.That(handler >= 0 && call > handler && call < nextMember, "the call is inside the Clear log command");
                string body = window[handler..nextMember];
                Check.That(body.Contains("DefaultButton = ContentDialogButton.Close") && body.Contains("if (answer == ContentDialogResult.None"),
                    "the command asks first, with Cancel as the default, and stops when the user cancels");
            });

        yield return ("core_and_app_have_no_reference_to_the_classic_eventlog_class",
            "Neither the core library nor the app uses System.Diagnostics.EventLog, whose Clear, WriteEntry and Delete methods could change logs.",
            () =>
            {
                foreach (string path in new[] { typeof(EventItem).Assembly.Location, AppAssemblyPath() })
                {
                    using var pe = new PEReader(File.OpenRead(path));
                    MetadataReader metadata = pe.GetMetadataReader();
                    bool referencesAssembly = metadata.AssemblyReferences.Any(h => metadata.GetString(metadata.GetAssemblyReference(h).Name) == "System.Diagnostics.EventLog");
                    bool referencesType = metadata.TypeReferences.Any(h =>
                    {
                        TypeReference type = metadata.GetTypeReference(h);
                        return metadata.GetString(type.Namespace) == "System.Diagnostics" && metadata.GetString(type.Name) == "EventLog";
                    });
                    Check.That(!referencesAssembly && !referencesType, $"{Path.GetFileName(path)} uses System.Diagnostics.EventLog");
                }
            });
    }

    private static void CheckNoChangingFunction(string[] imported)
    {
        string[] forbidden = imported.Intersect(ChangingFunctions, StringComparer.OrdinalIgnoreCase).ToArray();
        Check.That(forbidden.Length == 0, "forbidden imports: " + string.Join(", ", forbidden));
    }

    /// <summary>Reads the names of all Windows functions an assembly imports from its metadata, without loading it.</summary>
    private static string[] ImportedFunctions(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        MetadataReader metadata = pe.GetMetadataReader();
        var names = new List<string>();
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
            MethodImport import = method.GetImport();
            names.Add(metadata.GetString(import.Name.IsNil ? method.Name : import.Name));
        }
        return [.. names];
    }

    /// <summary>The app's program file from the newest build of the solution.</summary>
    private static string AppAssemblyPath()
    {
        string root = Fixtures.RepositoryRoot;
        string bin = Path.Combine(root, "src", "NaxEventViewer.App", "bin");
        FileInfo? newest = Directory.Exists(bin)
            ? new DirectoryInfo(bin).EnumerateFiles("Nax-EventViewer.dll", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        return newest?.FullName ?? throw new Exception("The app has not been built. Build NaxEventViewer.slnx before running the tests.");
    }
}
