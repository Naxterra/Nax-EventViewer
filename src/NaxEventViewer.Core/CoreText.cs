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

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>A text with {0}-style placeholders.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
