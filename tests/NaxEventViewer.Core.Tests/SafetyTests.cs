using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

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
            "The core library imports no Windows function that clears a log, writes an event or changes a log's settings, so it cannot change any log.",
            () =>
            {
                string[] imported = ImportedFunctions(typeof(EventItem).Assembly.Location);
                Check.That(imported.Length > 10, $"the scan found only {imported.Length} imports, so it is not looking at the right methods");
                CheckNoChangingFunction(imported);
            });

        yield return ("app_imports_no_function_that_clears_writes_or_reconfigures_a_log",
            "The app's own program file imports no Windows function that clears a log, writes an event or changes a log's settings either.",
            () => CheckNoChangingFunction(ImportedFunctions(AppAssemblyPath())));

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
