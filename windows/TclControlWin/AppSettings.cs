using System;
using System.IO;
using System.Text.Json;

namespace TclControlWin;

public class AppSettings
{
    // Known TCL S45H - used as the default quick-connect target until a device has
    // actually been connected once (then the real last device wins). Name confirmed via live
    // BLE scan (advertised LocalName); the address is the Android-bonded identity address, which
    // may not resolve on a PC that hasn't paired with the soundbar - see PROGRESS.md.
    public string Address { get; set; } = "00:A4:1C:CD:CC:EC";
    public string Name { get; set; } = "tcl_B14S45H0_CCEC";

    // Last known device state, persisted so the UI starts from the last confirmed values
    // instead of hardcoded defaults on the next launch. Updated from device status echoes
    // (TclSoundbarBle.StatusReceived), not from the buttons/sliders themselves - so it always
    // reflects the last value the soundbar actually confirmed, not just what was requested.
    public int Volume { get; set; } = 20;
    public bool Mute { get; set; }
    public bool Atmos { get; set; }
    public int Source { get; set; } // 0 = unknown/unset
    public int SoundMode { get; set; } // 0 = unknown/unset
    public int Bass { get; set; }
    public int Treble { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TclControlWin", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null) return settings;
            }
        }
        catch
        {
            // fall back to defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // best effort
        }
    }
}
