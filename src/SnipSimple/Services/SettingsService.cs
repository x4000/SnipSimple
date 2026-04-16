using System.IO;
using System.Text.Json;
using SnipSimple.Models;

namespace SnipSimple.Services;

public class SettingsService
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArcenSettings", "SimpleSnip");

    private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");
    private static readonly string UserSettingsFile = Path.Combine(SettingsFolder, "user-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private AppSettings _settings = new();
    private UserSettings _userSettings = new();

    public AppSettings Settings => _settings;
    public UserSettings UserSettings => _userSettings;

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }

        try
        {
            if (File.Exists(UserSettingsFile))
            {
                var json = File.ReadAllText(UserSettingsFile);
                _userSettings = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
            }
        }
        catch
        {
            _userSettings = new UserSettings();
        }

        // Ensure default save location
        if (string.IsNullOrEmpty(_userSettings.LastSaveLocation))
        {
            _userSettings.LastSaveLocation = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(SettingsFile, json);
        }
        catch { }

        SaveUserSettings();
    }

    public void SaveUserSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            var json = JsonSerializer.Serialize(_userSettings, JsonOptions);
            File.WriteAllText(UserSettingsFile, json);
        }
        catch { }
    }

    public void AddRecentSaveLocation(string folderPath)
    {
        _settings.RecentSaveLocations.Remove(folderPath);
        _settings.RecentSaveLocations.Insert(0, folderPath);

        if (_settings.RecentSaveLocations.Count > AppSettings.MaxRecentLocations)
        {
            _settings.RecentSaveLocations.RemoveRange(
                AppSettings.MaxRecentLocations,
                _settings.RecentSaveLocations.Count - AppSettings.MaxRecentLocations);
        }

        _userSettings.LastSaveLocation = folderPath;
        Save();
    }
}
