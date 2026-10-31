using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NaxEventViewer.Core.Tests;

/// <summary>The English and German interface texts of the core library and the app.</summary>
internal static class LanguageTests
{
    public static IEnumerable<(string Name, string Scenario, Action Body)> All()
    {
        yield return ("german_core_texts_cover_every_english_text_with_the_same_placeholders",
            "Every text of the core library has a German translation that is not empty and uses the same {0}-style placeholders, and there is no German text without an English one.",
            () => CompareTranslations(
                Path.Combine(Fixtures.RepositoryRoot, "src", "NaxEventViewer.Core", "Strings.resx"),
                Path.Combine(Fixtures.RepositoryRoot, "src", "NaxEventViewer.Core", "Strings.de.resx")));

        yield return ("german_app_texts_cover_every_english_text_with_the_same_placeholders",
            "Every text of the app window has a German translation that is not empty and uses the same {0}-style placeholders, and there is no German text without an English one.",
            () => CompareTranslations(
                Path.Combine(Fixtures.RepositoryRoot, "src", "NaxEventViewer.App", "Strings", "en-US", "Resources.resw"),
                Path.Combine(Fixtures.RepositoryRoot, "src", "NaxEventViewer.App", "Strings", "de-DE", "Resources.resw")));

        yield return ("core_texts_follow_the_interface_language",
            "With German as the interface language, levels, the empty task category and the CSV header come out in German, and with English in English.",
            () =>
            {
                CultureInfo before = CultureInfo.CurrentUICulture;
                try
                {
                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
                    Check.Equal("Fehler", EventItem.KindText(EventLevelKind.Error));
                    Check.Equal("Überwachung gescheitert", EventItem.KindText(EventLevelKind.AuditFailure));
                    Check.Equal("Keine", Fixtures.Item(DateTime.UtcNow).TaskName);
                    Check.That(CoreText.Get("Csv_Header").StartsWith("Ebene,"), "the CSV header is German");
                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                    Check.Equal("Error", EventItem.KindText(EventLevelKind.Error));
                    Check.Equal("None", Fixtures.Item(DateTime.UtcNow).TaskName);
                }
                finally
                {
                    CultureInfo.CurrentUICulture = before;
                }
            });
    }

    private static void CompareTranslations(string englishFile, string germanFile)
    {
        Dictionary<string, string> english = Read(englishFile), german = Read(germanFile);
        Check.That(english.Count > 10, $"only {english.Count} English texts were found in {Path.GetFileName(englishFile)}");
        string[] missing = english.Keys.Except(german.Keys).ToArray();
        Check.That(missing.Length == 0, "texts without German translation: " + string.Join(", ", missing));
        string[] extra = german.Keys.Except(english.Keys).ToArray();
        Check.That(extra.Length == 0, "German texts without English text: " + string.Join(", ", extra));
        foreach (var (key, text) in english)
        {
            Check.That(german[key].Trim().Length > 0, $"the German text of {key} is empty");
            Check.That(Placeholders(text).SequenceEqual(Placeholders(german[key])), $"the placeholders of {key} differ between English and German");
        }
    }

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(file).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();
}
