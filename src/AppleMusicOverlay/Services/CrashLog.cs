using System.IO;
using System.Text;

namespace AppleMusicOverlay.Services;

/// <summary>Best-effort local logging for failures that previously could terminate the tray app.</summary>
public static class CrashLog
{
    private static readonly object Sync = new();
    public static string LogPath => Path.Combine(AppPaths.SettingsDirectory, "crash.log");

    public static void Write(Exception exception, string source)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.SettingsDirectory);
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 72)}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* Logging must never become a new failure path. */ }
    }

    /// <summary>One line per track lookup, so a "no lyrics" report can be diagnosed from the log.</summary>
    public static void WriteNote(string source, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.SettingsDirectory);
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}: {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* Logging must never become a new failure path. */ }
    }
}
