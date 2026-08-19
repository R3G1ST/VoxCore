using System.IO;
using System.Text.Json;

namespace VoxCore.Client;

public sealed class AppSettings
{
    public string Server { get; set; } = "194.31.204.5:9987";
    public string Room { get; set; } = "squad";
    public string Nickname { get; set; } = "Player";
    public int MicDevice { get; set; }
    public double MicGain { get; set; } = 100.0;
    public bool OpenMic { get; set; }
    public bool NoiseSuppression { get; set; } = true;
    public string RoomPassword { get; set; } = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoxCore", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
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