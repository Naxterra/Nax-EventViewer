using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace NaxEventViewer.App;

/// <summary>
/// Interface text from Strings/&lt;language&gt;/Resources.resw. Only the app's own wording is translated: log names,
/// sources and event messages stay exactly as Windows provides them.
/// </summary>
internal static class Loc
{
    private static ResourceLoader? _loader;

    /// <summary>
    /// Applies the language setting before any window exists. An empty setting follows the Windows display language,
    /// with English for languages that have no translation.
    /// </summary>
    public static void ApplyLanguage(string setting)
    {
        string tag = setting is AppSettings.English or AppSettings.German ? setting : "";
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = tag;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Without the override the app follows Windows, which is the default anyway.
        }
        if (tag.Length > 0)
        {
            // The core library's texts and the number and date formats follow the same choice.
            CultureInfo culture = CultureInfo.GetCultureInfo(tag);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }

    public static string Get(string key)
    {
        try
        {
            _loader ??= new ResourceLoader();
            string value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return key;
        }
    }

    /// <summary>A text with {0}-style placeholders.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
