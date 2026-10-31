using System.Globalization;
using System.Resources;

namespace NaxEventViewer.Core;

/// <summary>
/// The core library's own wording, from Strings.resx and its translations, in the current interface language. Names
/// and messages that come from Windows or from the logs are never translated here.
/// </summary>
public static class CoreText
{
    private static readonly ResourceManager Resources = new("NaxEventViewer.Core.Strings", typeof(CoreText).Assembly);

    // Level names and the empty task category are read for every event in a search or sort, so each text is looked up
    // once per language instead of going through the resource manager every time.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Culture, string Key), string> Cache = new();

    public static string Get(string key)
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        return Cache.GetOrAdd((culture.Name, key), static (k, c) => Resources.GetString(k.Key, c) ?? k.Key, culture);
    }

    /// <summary>A text with {0}-style placeholders.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
