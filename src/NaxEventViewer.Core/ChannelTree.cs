namespace NaxEventViewer.Core;

/// <summary>A folder in the log tree, holding subfolders and logs, both sorted by name.</summary>
public sealed class ChannelFolder(string name)
{
    public string Name { get; } = name;
    public List<ChannelFolder> Folders { get; } = [];

    /// <summary>The logs in this folder, as the name shown in the tree and the full channel name.</summary>
    public List<(string Name, string Channel)> Channels { get; } = [];

    internal ChannelFolder Folder(string name)
    {
        ChannelFolder? folder = Folders.Find(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            folder = new ChannelFolder(name);
            Folders.Add(folder);
        }
        return folder;
    }

    internal void Sort()
    {
        Folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Channels.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        foreach (ChannelFolder folder in Folders) folder.Sort();
    }
}

/// <summary>
/// Groups log names into folders the way the Windows Event Viewer does under "Applications and Services Logs":
/// "Microsoft-Windows-Kernel-Power/Thermal-Operational" becomes Microsoft, Windows, Kernel-Power, then the log
/// "Thermal-Operational". Names without a slash stay at the top.
/// </summary>
public static class ChannelTree
{
    public static ChannelFolder Build(IEnumerable<string> channels, string rootName = "Applications and Services Logs")
    {
        var root = new ChannelFolder(rootName);
        foreach (string channel in channels)
        {
            int slash = channel.IndexOf('/');
            if (slash <= 0)
            {
                root.Channels.Add((channel, channel));
                continue;
            }

            string provider = channel[..slash];
            string leaf = channel[(slash + 1)..];
            ChannelFolder folder = root;
            foreach (string part in ProviderPath(provider)) folder = folder.Folder(part);
            folder.Channels.Add((leaf.Length == 0 ? channel : leaf, channel));
        }
        root.Sort();
        return root;
    }

    private static IEnumerable<string> ProviderPath(string provider)
    {
        const string windowsPrefix = "Microsoft-Windows-";
        const string microsoftPrefix = "Microsoft-";
        if (provider.StartsWith(windowsPrefix, StringComparison.OrdinalIgnoreCase) && provider.Length > windowsPrefix.Length)
            return ["Microsoft", "Windows", provider[windowsPrefix.Length..]];
        if (provider.StartsWith(microsoftPrefix, StringComparison.OrdinalIgnoreCase) && provider.Length > microsoftPrefix.Length)
            return ["Microsoft", provider[microsoftPrefix.Length..]];
        return [provider];
    }
}
