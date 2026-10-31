using System.Text.Json;

namespace NaxEventViewer.App;

/// <summary>The user's settings, kept in settings.json next to the crash log. Only the interface language so far.</summary>
internal sealed class AppSettings
{
    public const string English = "en-US";
    public const string German = "de-DE";

    /// <summary>"en-US", "de-DE", or empty to follow the Windows display language.</summary>
    public string Language { get; set; } = "";

    private static string FilePath => Path.Combine(CrashLog.Folder, "settings.json");

    /// <summary>Reads the settings. A missing or damaged file gives the defaults, so a bad file never stops the app.</summary>
    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings through a temporary file, so a crash never leaves a half-written file.</summary>
    public void Save()
    {
        Directory.CreateDirectory(CrashLog.Folder);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
        File.Move(temporary, FilePath, overwrite: true);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : System.Text.Json.Serialization.JsonSerializerContext;
