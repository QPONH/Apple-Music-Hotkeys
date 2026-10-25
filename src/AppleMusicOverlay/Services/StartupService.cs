using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AppleMusicOverlay.Services;

/// <summary>Registers MusicFloat for the current user's Windows sign-in.</summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedFolderKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string ValueName = "MusicFloat";
    private const string ShortcutName = "MusicFloat.lnk";
    // Windows' Startup Apps UI keeps enable/disable state separate from the Run command.
    private static readonly byte[] ApprovedState = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    private sealed record LaunchSpec(string TargetPath, string Arguments, string WorkingDirectory)
    {
        public string RegistryCommand => $"\"{TargetPath}\"" +
            (string.IsNullOrWhiteSpace(Arguments) ? string.Empty : $" {Arguments}");
    }

    public static bool IsEnabled
    {
        get
        {
            try
            {
                LaunchSpec launch = GetLaunchSpec();
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                if (key?.GetValue(ValueName) is string value &&
                    string.Equals(value, launch.RegistryCommand, StringComparison.OrdinalIgnoreCase) &&
                    IsApprovalEnabled(ApprovedRunKeyPath, ValueName))
                    return true;

                // The startup-folder shortcut is also used as a fallback when policy/security
                // software prevents writing the Run key.
                return File.Exists(GetShortcutPath()) && IsApprovalEnabled(ApprovedFolderKeyPath, ShortcutName);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "StartupService:IsEnabled");
                return false;
            }
        }
    }

    /// <summary>
<<<<<<< HEAD
    /// Synchronizes the per-user Run entry with the current executable. If the registry is
    /// unavailable, use the user's Startup folder instead. A successful fallback is reported
    /// as success, with a warning string for the UI to display.
=======
    /// Best-effort registry update. The Run key can be locked down by policy or security
    /// software, and that must never abort an unrelated settings change. Existing commands
    /// are compared by path so moving the app does not leave a stale startup target.
>>>>>>> 732879874cff126ad2dca029241aba9caba94eb6
    /// </summary>
    public static bool TrySetEnabled(bool enabled, out string? error)
    {
        error = null;
        if (!enabled) return TryDisable(out error);

        LaunchSpec launch;
        try
        {
<<<<<<< HEAD
            launch = GetLaunchSpec();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "StartupService:ResolveLaunchPath");
            error = ex.Message;
            return false;
        }

        string registryError;
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("无法创建当前用户的开机启动注册表项。");

            string expected = launch.RegistryCommand;
            if (key.GetValue(ValueName) is not string current ||
                !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, expected, RegistryValueKind.String);
            }

            string? saved = key.GetValue(ValueName) as string;
            if (!string.Equals(saved, expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("开机启动注册表项写入后校验失败。");

            SetApprovalEnabled(ApprovedRunKeyPath, ValueName);

            // Avoid launching twice if an older version left a Startup-folder shortcut behind.
            try { DeleteStartupShortcut(); }
            catch (Exception ex) { CrashLog.Write(ex, "StartupService:RemoveFallbackShortcut"); }
            try { RemoveApproval(ApprovedFolderKeyPath, ShortcutName); }
            catch (Exception ex) { CrashLog.Write(ex, "StartupService:RemoveFolderApproval"); }
=======
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
>>>>>>> 732879874cff126ad2dca029241aba9caba94eb6
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "StartupService:WriteRunKey");
            registryError = ex.Message;
        }

        // Do not leave a half-written Run entry behind if its Windows approval state could not
        // be synchronized; otherwise the user could get two launch paths after fallback.
        try { RemoveRunRegistration(); }
        catch (Exception ex) { CrashLog.Write(ex, "StartupService:CleanupRunBeforeFallback"); }
        try { RemoveApproval(ApprovedRunKeyPath, ValueName); }
        catch (Exception ex) { CrashLog.Write(ex, "StartupService:CleanupRunApprovalBeforeFallback"); }

        try
        {
            CreateStartupShortcut(launch);
            if (!File.Exists(GetShortcutPath()))
                throw new IOException("启动文件夹快捷方式创建后未找到文件。");
            SetApprovalEnabled(ApprovedFolderKeyPath, ShortcutName);
            error = $"注册表启动项不可用，已改用启动文件夹快捷方式（{registryError}）。";
            CrashLog.WriteNote("StartupService", error);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "StartupService:WriteFallbackShortcut");
            try { DeleteStartupShortcut(); } catch (Exception cleanupEx) { CrashLog.Write(cleanupEx, "StartupService:CleanupFailedShortcut"); }
            error = $"注册表方式失败：{registryError}；启动文件夹方式也失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryDisable(out string? error)
    {
        List<string> errors = new();
        void Step(string label, Action action, string source)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, source);
                errors.Add(label + "：" + ex.Message);
            }
        }

        Step("注册表", RemoveRunRegistration, "StartupService:RemoveRunKey");
        Step("启动批准状态", () => RemoveApproval(ApprovedRunKeyPath, ValueName), "StartupService:RemoveRunApproval");
        Step("启动文件夹", DeleteStartupShortcut, "StartupService:RemoveFallbackShortcut");
        Step("启动文件夹批准状态", () => RemoveApproval(ApprovedFolderKeyPath, ShortcutName), "StartupService:RemoveFolderApproval");

        error = errors.Count == 0 ? null : string.Join("；", errors);
        return errors.Count == 0;
    }

    private static void RemoveRunRegistration()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;
        key.DeleteValue(ValueName, throwOnMissingValue: false);
        if (key.GetValue(ValueName) is not null)
            throw new IOException("注册表中的开机启动项删除后仍然存在。");
    }

    private static void SetApprovalEnabled(string keyPath, string valueName)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException("无法更新 Windows 的启动应用批准状态。");
        key.SetValue(valueName, ApprovedState, RegistryValueKind.Binary);
        if (key.GetValue(valueName) is not byte[] actual || actual.Length == 0 || actual[0] != 0x02)
            throw new IOException("Windows 启动应用批准状态写入后校验失败。");
    }

    private static bool IsApprovalEnabled(string keyPath, string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key?.GetValue(valueName) is not byte[] state || state.Length == 0) return true;
        // 0x02 is the normal enabled state used by Windows for per-user startup entries.
        return state[0] == 0x02 || state[0] == 0x06;
    }

    private static void RemoveApproval(string keyPath, string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static LaunchSpec GetLaunchSpec()
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。");
        processPath = Path.GetFullPath(processPath);

        // Environment.ProcessPath is dotnet.exe when launched as `dotnet MusicFloat.dll`.
        // In that case, prefer the adjacent apphost EXE; otherwise preserve the DLL argument.
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string? assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(assemblyPath) &&
                string.Equals(Path.GetExtension(assemblyPath), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                assemblyPath = Path.GetFullPath(assemblyPath);
                string appHostPath = Path.ChangeExtension(assemblyPath, ".exe");
                string directory = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
                if (File.Exists(appHostPath))
                    return new LaunchSpec(appHostPath, string.Empty, directory);

                return new LaunchSpec(processPath, $"\"{assemblyPath}\"", directory);
            }
        }

        if (!File.Exists(processPath))
            throw new FileNotFoundException("当前程序文件不存在，无法配置开机启动。", processPath);

        return new LaunchSpec(processPath, string.Empty, Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory);
    }

    private static string GetShortcutPath()
    {
        string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startupFolder))
            throw new InvalidOperationException("无法确定当前用户的启动文件夹路径。");
        return Path.Combine(startupFolder, ShortcutName);
    }

    private static void CreateStartupShortcut(LaunchSpec launch)
    {
        string shortcutPath = GetShortcutPath();
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建启动快捷方式：Windows Script Host 不可用。");
        object shellObject = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法初始化 Windows 快捷方式组件。");
        object? shortcutObject = null;
        try
        {
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            shortcut.TargetPath = launch.TargetPath;
            shortcut.Arguments = launch.Arguments;
            shortcut.WorkingDirectory = launch.WorkingDirectory;
            shortcut.Description = "MusicFloat 开机启动";
            if (File.Exists(launch.TargetPath)) shortcut.IconLocation = launch.TargetPath + ",0";
            shortcut.Save();
        }
        finally
        {
            if (shortcutObject is not null && Marshal.IsComObject(shortcutObject))
                Marshal.FinalReleaseComObject(shortcutObject);
            if (Marshal.IsComObject(shellObject)) Marshal.FinalReleaseComObject(shellObject);
        }
    }

    private static void DeleteStartupShortcut()
    {
        string path = GetShortcutPath();
        if (File.Exists(path)) File.Delete(path);
    }
}
