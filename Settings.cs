using System.Text.Json;

// Shared contract between the speaker (Program.cs / Engines.cs) and the tray app (Tray.cs).
// Lives at ~/.config/readaloud/settings.json — the tray edits it, every speak run reads it fresh.
public sealed class Settings
{
    public double Speed { get; set; } = 2.5;  // playback tempo multiplier, 0.5 .. 3.0
    public double Pitch { get; set; } = 1.0;  // 1.0 = natural
    public string Engine { get; set; } = "google";  // "google" | "spd" (extend freely)
    public string GoogleLang { get; set; } = "en";
    public int SpdRate { get; set; } = 0;     // -100..100, spd engine only
    public bool MuteClaude { get; set; }      // silence the Claude Code auto-read hook
    public string Shortcut { get; set; } = "<Control><Super>s";

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "readaloud");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
        }
        catch (Exception)
        {
            return new Settings(); // missing or corrupt file: defaults, first Save() heals it
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
