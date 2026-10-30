using System.Xml.Linq;

namespace NaxEventViewer.Core.Tests;

/// <summary>Grouping of log names in the tree and the queries of the problems overview.</summary>
internal static class ChannelTreeTests
{
    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("windows_channel_goes_under_microsoft_windows_folder",
            "The log Microsoft-Windows-Kernel-Power/Thermal-Operational is shown as Thermal-Operational in the folder Microsoft, Windows, Kernel-Power.",
            () =>
            {
                ChannelFolder root = ChannelTree.Build(["Microsoft-Windows-Kernel-Power/Thermal-Operational"]);
                ChannelFolder folder = root.Folders.Single(f => f.Name == "Microsoft").Folders.Single(f => f.Name == "Windows").Folders.Single(f => f.Name == "Kernel-Power");
                Check.Equal(("Thermal-Operational", "Microsoft-Windows-Kernel-Power/Thermal-Operational"), folder.Channels.Single());
            });

        yield return ("other_vendor_channel_gets_its_own_folder",
            "A log from another vendor, such as OpenSSH/Operational, is shown in a folder named after the vendor part.",
            () =>
            {
                ChannelFolder root = ChannelTree.Build(["OpenSSH/Operational", "Microsoft-Client-Licensing-Platform/Admin"]);
                Check.Equal("Operational", root.Folders.Single(f => f.Name == "OpenSSH").Channels.Single().Name);
                Check.Equal("Admin", root.Folders.Single(f => f.Name == "Microsoft").Folders.Single(f => f.Name == "Client-Licensing-Platform").Channels.Single().Name);
            });

        yield return ("channel_without_slash_stays_at_top",
            "A log without a slash in its name, such as Windows PowerShell, is shown directly under Applications and Services Logs.",
            () =>
            {
                ChannelFolder root = ChannelTree.Build(["Windows PowerShell", "HardwareEvents"]);
                Check.That(root.Folders.Count == 0, "no folder is created");
                Check.That(root.Channels.Select(c => c.Name).SequenceEqual(["HardwareEvents", "Windows PowerShell"]), "the logs are sorted by name");
            });

        yield return ("log_listing_error_other_than_end_is_reported",
            "While listing the logs, only \"no more items\" counts as the normal end; a too-small buffer is retried, and any other error, such as access denied, is reported instead of quietly cutting the list short.",
            () =>
            {
                Check.Equal(ChannelCatalog.ListingStep.Done, ChannelCatalog.ClassifyListingError(259));
                Check.Equal(ChannelCatalog.ListingStep.Retry, ChannelCatalog.ClassifyListingError(122));
                Check.Equal(ChannelCatalog.ListingStep.Fail, ChannelCatalog.ClassifyListingError(5));
                Check.Equal(ChannelCatalog.ListingStep.Fail, ChannelCatalog.ClassifyListingError(1717));
            });

        yield return ("log_listing_finds_the_windows_logs",
            "Listing the logs on this PC succeeds and includes Application, System and the PowerShell log.",
            () =>
            {
                List<string> channels = ChannelCatalog.ListChannels();
                foreach (string expected in new[] { "Application", "System", "Microsoft-Windows-PowerShell/Operational" })
                    Check.That(channels.Contains(expected, StringComparer.OrdinalIgnoreCase), expected + " is missing");
            });

        yield return ("overview_query_escapes_channel_names_and_splits_into_chunks",
            "The problems overview over 150 logs, one with an ampersand and quote in its name, becomes three valid XML queries that each select critical events, errors and warnings.",
            () =>
            {
                var channels = Enumerable.Range(0, 149).Select(i => "Log" + i).Append("A&B\"C/Operational").ToList();
                IReadOnlyList<string> queries = LogSource.BuildOverviewQueries(channels, TimeSpan.FromHours(24));
                Check.Equal(3, queries.Count);
                var paths = new List<string>();
                foreach (string query in queries)
                {
                    XElement list = XElement.Parse(query);
                    foreach (XElement select in list.Descendants("Select"))
                    {
                        paths.Add((string)select.Attribute("Path")!);
                        Check.That(select.Value.Contains("Level=1 or Level=2 or Level=3") && select.Value.Contains("timediff(@SystemTime) <= 86400000"), "each select filters levels and time");
                    }
                }
                Check.That(paths.SequenceEqual(channels), "every log appears once, with its name unchanged");
            });
    }
}
