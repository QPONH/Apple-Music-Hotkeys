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
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Settings:Load");
            return new OverlaySettings();
        }
    }

    /// <summary>
    /// Write beside the target, then replace it. A crash or interrupted write therefore leaves
    /// the last valid settings file intact instead of truncating it during a drag or apply.
    /// </summary>
    public void Save(OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        string temporary = _filePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(_filePath))
            {
                try
                {
                    File.Replace(temporary, _filePath, destinationBackupFileName: null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporary, _filePath, overwrite: true);
                }
                catch (IOException)
                {
                    // Some filesystems do not support File.Replace even though overwrite-move works.
                    File.Move(temporary, _filePath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporary, _filePath);
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
