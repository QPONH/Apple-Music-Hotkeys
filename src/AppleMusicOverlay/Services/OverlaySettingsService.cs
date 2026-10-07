using System.IO;
using System.Globalization;
using System.Text.Json;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed class OverlaySettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public OverlaySettingsService()
        : this(AppPaths.SettingsFilePath)
    {
    }

    public OverlaySettingsService(string filePath)
    {
        _filePath = filePath;
    }

    public OverlaySettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return CreateDefaultSettings();
            }

            string json = File.ReadAllText(_filePath);
            OverlaySettings? settings = JsonSerializer.Deserialize<OverlaySettings>(json, JsonOptions);
            return OverlaySettingsNormalizer.Normalize(settings ?? CreateDefaultSettings());
        }
        catch
        {
            return CreateDefaultSettings();
        }
    }

    public void Save(OverlaySettings settings)
    {
        OverlaySettings normalized = OverlaySettingsNormalizer.Normalize(settings);
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(normalized, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    private static OverlaySettings CreateDefaultSettings()
    {
        return new OverlaySettings
        {
            LanguageCode = LocalizationService.GetPreferredStartupLanguageCode(CultureInfo.CurrentUICulture)
        };
    }
}
