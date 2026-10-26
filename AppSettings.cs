using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace QuietIsland;

internal sealed class AppSettings
{
    public bool Hidden { get; set; }
    public bool TaskbarPlacement { get; set; } = true;
    public bool VolumeAlerts { get; set; } = true;
    public bool OnlineLyrics { get; set; } = true;
    public bool ShowLyrics { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public string Placement { get; set; } = "left";
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietIsland");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        try { Directory.CreateDirectory(DataDirectory); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log(ex); }
    }
    public static bool StartupEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("QuietIsland") is string; }
    }
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("QuietIsland", "\"" + Environment.ProcessPath + "\" --startup");
        else key.DeleteValue("QuietIsland", false);
    }
    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, "errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024) File.WriteAllText(path, "");
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + ex.Message + Environment.NewLine);
        }
        catch { }
    }
}
