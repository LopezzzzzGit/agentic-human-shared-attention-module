using System.Text.Json;
using System.IO;

namespace AshaLive;

/// <summary>
/// Small, local-only preferences for the live demonstrator.  These are not a
/// permission database: every physical action still needs a short-lived
/// control lease.  Persisting the selected profile makes the orb predictable
/// across restarts without placing personal settings in the repository.
/// </summary>
public sealed class AshaPreferences
{
    public AshaProfile Profile { get; set; } = AshaProfile.Observe;
    public VisionPreference Vision { get; set; } = VisionPreference.OnChange;
    public bool ShowControlPresence { get; set; } = true;
    /// <summary>Explicit opt-in before any selected desktop image may leave this PC.</summary>
    public bool AllowRemoteVision { get; set; }
    /// <summary>
    /// An intentionally started session may survive an ASHA restart. Casual
    /// conversation never writes this value and therefore remains transient.
    /// </summary>
    public string? ActiveSessionId { get; set; }

    public static AshaPreferences Load()
    {
        try
        {
            var path = PreferencesPath();
            if (!File.Exists(path)) return new AshaPreferences();
            return JsonSerializer.Deserialize<AshaPreferences>(File.ReadAllText(path)) ?? new AshaPreferences();
        }
        catch
        {
            // Preferences must never prevent ASHA from starting.
            return new AshaPreferences();
        }
    }

    public void Save()
    {
        var path = PreferencesPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static string PreferencesPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "asha",
        "preferences.json");
}

public enum AshaProfile
{
    Observe,
    Teach,
    Assist,
}

public enum VisionPreference
{
    Off,
    OnChange,
    Live,
}
