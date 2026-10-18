using Microsoft.Win32;

namespace AppleMusicOverlay.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MusicFloat";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Best-effort registry update. The Run key can be locked down by policy or security
    /// software, and that must never abort an unrelated settings change.
    /// </summary>
    public static bool TrySetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            // Skip the write entirely when the stored state already matches: most applies
            // only change lyric settings and never need to touch the Run key.
            if (IsEnabled == enabled) return true;

            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定程序路径。");
            key.SetValue(ValueName, $"\"{executablePath}\"");
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "StartupService:SetEnabled");
            error = ex.Message;
            return false;
        }
    }
}
