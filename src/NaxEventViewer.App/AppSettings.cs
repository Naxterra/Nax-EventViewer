using System.Text.Json;

namespace NaxEventViewer.App;

/// <summary>The user's settings and persisted window layout, kept in settings.json next to the crash log.</summary>
internal sealed class AppSettings
{
    public const string English = "en-US";
    public const string German = "de-DE";

    private static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromMinutes(10);

    /// <summary>"en-US", "de-DE", or empty to follow the Windows display language.</summary>
    public string Language { get; set; } = "";

    public int WindowWidth { get; set; } = 1600;
    public int WindowHeight { get; set; } = 940;
    public bool WindowMaximized { get; set; }
    public double TreePaneWidth { get; set; } = 300;
    public double DetailPaneHeight { get; set; } = 340;
    public double FieldsPaneWidth { get; set; } = 400;
    public bool ShowAllLogs { get; set; }
    public int OverviewHours { get; set; } = 24;

    public double LevelColumnWidth { get; set; } = 156;
    public double TimeColumnWidth { get; set; } = 164;
    public double LogColumnWidth { get; set; } = 170;
    public double SourceColumnWidth { get; set; } = 230;
    public double EventIdColumnWidth { get; set; } = 72;
    public double TaskColumnWidth { get; set; } = 140;

    private static string FilePath => Path.Combine(CrashLog.Folder, "settings.json");

    /// <summary>Reads the settings. A missing or damaged file gives the defaults, so a bad file never stops the app.</summary>
    public static AppSettings Load()
    {
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            AppSettings loaded = JsonSerializer.Deserialize(stream, AppSettingsJson.Default.AppSettings) ?? new AppSettings();
            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    private void Normalize()
    {
        WindowWidth = Math.Clamp(WindowWidth, 800, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 560, 4320);
        TreePaneWidth = Math.Clamp(TreePaneWidth, 180, 700);
        DetailPaneHeight = Math.Clamp(DetailPaneHeight, 140, 1200);
        FieldsPaneWidth = Math.Clamp(FieldsPaneWidth, 220, 900);
        OverviewHours = OverviewHours is 1 or 24 or 168 or 720 ? OverviewHours : 24;
        LevelColumnWidth = Math.Clamp(LevelColumnWidth, 90, 400);
        TimeColumnWidth = Math.Clamp(TimeColumnWidth, 110, 400);
        LogColumnWidth = Math.Clamp(LogColumnWidth, 100, 500);
        SourceColumnWidth = Math.Clamp(SourceColumnWidth, 100, 600);
        EventIdColumnWidth = Math.Clamp(EventIdColumnWidth, 56, 200);
        TaskColumnWidth = Math.Clamp(TaskColumnWidth, 80, 500);
    }

    /// <summary>Writes the settings through a temporary file, so a crash never leaves a half-written file.</summary>
    public void Save()
    {
        Directory.CreateDirectory(CrashLog.Folder);
        RemoveStaleTemporaries();
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, this, AppSettingsJson.Default.AppSettings);
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Saves layout changes quietly without interrupting the user if the file is briefly locked.</summary>
    public void TrySave()
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RemoveStaleTemporaries()
    {
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(CrashLog.Folder, "settings.json.*.tmp"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(candidate) > StaleTemporaryAge)
                {
                    try { File.Delete(candidate); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : System.Text.Json.Serialization.JsonSerializerContext;
