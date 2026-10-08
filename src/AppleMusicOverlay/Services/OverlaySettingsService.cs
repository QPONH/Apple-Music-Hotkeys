using System.IO;
using System.Text.Json;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed class OverlaySettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public OverlaySettingsService() : this(AppPaths.SettingsFilePath) { }
    public OverlaySettingsService(string filePath) => _filePath = filePath;

    public OverlaySettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new OverlaySettings();
            return JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(_filePath), JsonOptions)
                   ?? new OverlaySettings();
        }
        catch
        {
            return new OverlaySettings();
        }
    }

    public void Save(OverlaySettings settings)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
