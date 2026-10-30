using System.Reflection;
using System.Runtime.InteropServices;

namespace NaxEventViewer.Core.Tests;

/// <summary>Guards that keep the app a pure reader of event logs.</summary>
internal static class SafetyTests
{
    private static readonly string[] ChangingFunctions =
    [
        "EvtClearLog",
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
        yield return ("core_imports_no_function_that_clears_writes_or_reconfigures_a_log",
            "The core library imports no Windows function that clears a log, writes an event or changes a log's settings, so the app cannot change any log.",
            () =>
            {
                var imported = new List<string>();
                foreach (Type type in typeof(EventItem).Assembly.GetTypes())
                {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (method.GetCustomAttribute<DllImportAttribute>() is { } import)
                            imported.Add(import.EntryPoint ?? method.Name);
                    }
                }
                Check.That(imported.Count > 10, $"the scan found only {imported.Count} imports, so it is not looking at the right methods");
                string[] forbidden = imported.Intersect(ChangingFunctions, StringComparer.OrdinalIgnoreCase).ToArray();
                Check.That(forbidden.Length == 0, "forbidden imports: " + string.Join(", ", forbidden));
            });

        yield return ("core_has_no_reference_to_the_classic_eventlog_class",
            "The core library does not use System.Diagnostics.EventLog, whose Clear, WriteEntry and Delete methods could change logs.",
            () =>
            {
                var references = typeof(EventItem).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
                Check.That(!references.Contains("System.Diagnostics.EventLog"), "the core library references System.Diagnostics.EventLog");
            });
    }
}
