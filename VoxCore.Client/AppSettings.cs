using System.IO;
using System.Text.Json;

namespace VoxCore.Client;

public sealed class AppSettings
{
    public string Server { get; set; } = "194.31.204.5:9988";
    public string Room { get; set; } = "squad";
    public string Nickname { get; set; } = "Player";
    public int MicDevice { get; set; }
    public double MicGain { get; set; } = 100.0;
    public bool OpenMic { get; set; }
    public bool NoiseSuppression { get; set; } = true;
    public bool AgcEnabled { get; set; } = true;
    public double DfAttLim { get; set; } = 60.0;
    public double VadThreshold { get; set; } = 0.1;
    public float PreVadGain { get; set; } = 3.0f;
    public double EqLow { get; set; }
    public double EqMid { get; set; }
    public double EqHigh { get; set; }
    public Dictionary<string, double> UserVolumes { get; set; } = new();
    public string RoomPassword { get; set; } = "";
    public string? Token { get; set; }
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public string UserColor { get; set; } = "#5865f2";
    public Dictionary<string, string> Notes { get; set; } = new();
    public List<string> Ignored { get; set; } = new();
    public List<string> Blocked { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoxCore", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    // Migration: reset old VAD defaults that don't work with new pipeline (VAD before DFN3)
                    if (loaded.VadThreshold >= 0.2) loaded.VadThreshold = 0.1;
                    if (loaded.PreVadGain <= 2.5f) loaded.PreVadGain = 3.0f;
                    return loaded;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}