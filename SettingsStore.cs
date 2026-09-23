using System.IO;
using System.Text.Json;

namespace AudioScrub;

sealed class SettingsDto
{
    public bool Enabled { get; set; } = true;
    public double Volume { get; set; } = 100;
    public int? FrameCount { get; set; }
}

static class SettingsStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string FilePath
    {
        get
        {
            var dir = Path.GetDirectoryName(typeof(SettingsStore).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioScrub");
            return Path.Combine(dir, "settings.json");
        }
    }

    public static SettingsDto Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return new SettingsDto();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SettingsDto>(json, JsonOptions) ?? new SettingsDto();
        }
        catch (Exception ex)
        {
            AudioScrubLog.Write("settings load: " + ex.Message);
            return new SettingsDto();
        }
    }

    public static void Save(SettingsDto dto)
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex)
        {
            AudioScrubLog.Write("settings save: " + ex.Message);
        }
    }
}
