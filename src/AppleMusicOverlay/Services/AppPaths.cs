using System.IO;

namespace AppleMusicOverlay.Services;

public static class AppPaths
{
    public static string SettingsDirectory
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "AppleMusicOverlay");
        }
    }

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");
}
