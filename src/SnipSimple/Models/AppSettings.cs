using System.Text.Json.Serialization;

namespace SnipSimple.Models;

/// <summary>
/// Preferences that persist across sessions (settings.json).
/// Includes colors, recent save locations, and other preferences.
/// </summary>
public class AppSettings
{
    [JsonPropertyName("pencilColor")]
    public string PencilColor { get; set; } = "#FF0000";

    [JsonPropertyName("highlighterColor")]
    public string HighlighterColor { get; set; } = "#FFFF00";

    [JsonPropertyName("recentSaveLocations")]
    public List<string> RecentSaveLocations { get; set; } = new();

    [JsonPropertyName("mainWindowTheme")]
    public string MainWindowTheme { get; set; } = "Light";

    [JsonPropertyName("editorTheme")]
    public string EditorTheme { get; set; } = "Dark";

    public const int MaxRecentLocations = 10;
}

/// <summary>
/// User-specific data that changes frequently (user-settings.json).
/// Separated so it can be excluded from version control if desired.
/// </summary>
public class UserSettings
{
    [JsonPropertyName("lastSaveLocation")]
    public string? LastSaveLocation { get; set; }
}
