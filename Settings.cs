using System.Text.Json;

// Shared contract between the speaker (Program.cs / Engines.cs) and the tray app (Tray.cs).
// Lives at ~/.config/readaloud/settings.json — the tray edits it, every speak run reads it fresh.
public sealed class Settings
{
    public double Speed { get; set; } = 2.5;  // playback tempo multiplier, 0.5 .. 3.0
    public double Pitch { get; set; } = 1.0;  // 1.0 = natural
    public string Engine { get; set; } = "google";  // "google" | "inflect" | "spd"
    public string GoogleLang { get; set; } = "en";
    public int SpdRate { get; set; } = 0;     // -100..100, spd engine only
    // Inflect-Micro-v2 (local neural): seed fixes the random sample; variation is steadiness (0..1)
    public int InflectSeed { get; set; } = 7;
    public double InflectVariation { get; set; } = 0.667;
    public bool MuteClaude { get; set; } = true; // Claude auto-read off by default (voices trample with multi-agent)
    public bool WiggleEnabled { get; set; } = true; // shake the mouse over a selection to read it
    public bool WigglePop { get; set; } = true;     // little pop sound when a wiggle is caught
    // named feel the tray picks; ApplyWiggleFeel() fills the four knobs below
    public string WiggleFeel { get; set; } = "normal";
    public int WiggleFlips { get; set; } = 5;       // direction reversals needed; lower = more sensitive
    public int WiggleWindowMs { get; set; } = 500;  // those flips must land inside this window
    public int WiggleMinPx { get; set; } = 25;      // min travel per stroke before a flip counts
    public int WiggleCooldownMs { get; set; } = 1800; // ignore further shakes after a trigger
    public string Shortcut { get; set; } = "<Control><Super>s";

    // tray presets — one radio click sets a coherent bundle (don't half-tune one field)
    public static void ApplyWiggleFeel(Settings s, string feel)
    {
        s.WiggleFeel = feel;
        switch (feel)
        {
            case "sensitive": // small, quick shake is enough
                s.WiggleFlips = 3;
                s.WiggleWindowMs = 700;
                s.WiggleMinPx = 12;
                s.WiggleCooldownMs = 1200;
                break;
            case "firm": // deliberate shake; ignores nervous mouse
                s.WiggleFlips = 6;
                s.WiggleWindowMs = 450;
                s.WiggleMinPx = 35;
                s.WiggleCooldownMs = 2200;
                break;
            case "stubborn": // almost has to mean it
                s.WiggleFlips = 8;
                s.WiggleWindowMs = 400;
                s.WiggleMinPx = 50;
                s.WiggleCooldownMs = 2800;
                break;
            default: // "normal" — less twitchy than the old defaults
                s.WiggleFeel = "normal";
                s.WiggleFlips = 5;
                s.WiggleWindowMs = 500;
                s.WiggleMinPx = 25;
                s.WiggleCooldownMs = 1800;
                break;
        }
    }

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "readaloud");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            Settings s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
            s.Normalize(); // old files omit new fields → 0 ints, which make wiggle insanely sensitive
            return s;
        }
        catch (Exception)
        {
            return new Settings(); // missing or corrupt file: defaults, first Save() heals it
        }
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }

    // Clamp / fill after deserialize. Missing JSON numbers become 0 and would break wiggle.
    public void Normalize()
    {
        if (Speed < 0.5 || Speed > 3.0)
        {
            Speed = 2.5;
        }

        if (Pitch < 0.5 || Pitch > 2.0)
        {
            Pitch = 1.0;
        }

        if (Engine is not ("google" or "inflect" or "spd"))
        {
            Engine = "google";
        }

        if (string.IsNullOrWhiteSpace(WiggleFeel))
        {
            WiggleFeel = "normal";
        }

        // 0 = field absent from an older settings.json; re-apply the named feel (or normal)
        if (WiggleFlips < 2 || WiggleFlips > 20
            || WiggleWindowMs < 100 || WiggleWindowMs > 5000
            || WiggleMinPx < 5 || WiggleMinPx > 200
            || WiggleCooldownMs < 200 || WiggleCooldownMs > 10_000)
        {
            ApplyWiggleFeel(this, WiggleFeel);
        }

        if (string.IsNullOrWhiteSpace(Shortcut))
        {
            Shortcut = "<Control><Super>s";
        }

        if (string.IsNullOrWhiteSpace(GoogleLang))
        {
            GoogleLang = "en";
        }
    }
}
