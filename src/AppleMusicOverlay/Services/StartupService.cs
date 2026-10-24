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
    /// software, and that must never abort an unrelated settings change. Existing commands
    /// are compared by path so moving the app does not leave a stale startup target.
    /// </summary>
    public static bool TrySetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            // Open writable even when the setting appears disabled: IsEnabled intentionally
            // treats access errors as false, which must not turn a failed disable into success.
            using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (!enabled)
            {
                if (existing is null) return true;
                existing.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定程序路径。");
            string expectedValue = $"\"{executablePath}\"";
            if (existing?.GetValue(ValueName) is string current
                && string.Equals(current, expectedValue, StringComparison.OrdinalIgnoreCase))
                return true;

            if (existing is not null)
            {
                existing.SetValue(ValueName, expectedValue);
                return true;
            }

            using RegistryKey created = Registry.CurrentUser.CreateSubKey(RunKeyPath)
                ?? throw new InvalidOperationException("无法创建开机启动注册表项。");
            created.SetValue(ValueName, expectedValue);
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
