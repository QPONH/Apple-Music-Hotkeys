using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppleMusicOverlay.Services;

/// <summary>
/// Reports the integrity level of the current process. A low-integrity (sandboxed) process
/// cannot register a notification-area icon and cannot write the Windows startup key:
/// both are rejected with "access denied" by User Interface Privilege Isolation.
/// </summary>
public static class ProcessIntegrity
{
    private const int TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    private static string? _cached;

    /// <summary>e.g. "Medium", "Low", "High" or "未知".</summary>
    public static string Name => Describe().Name;

    /// <summary>The integrity SID RID, e.g. 0x2000 for medium.</summary>
    public static uint Rid => Describe().Rid;

    /// <summary>True when the process runs below medium integrity.</summary>
    public static bool IsLowOrBelow => Rid != 0 && Rid < 0x2000;

    public static string ToText() => Name == "未知" ? "未知" : $"{Name}（0x{Rid:X}）";

    private static (string Name, uint Rid) Describe()
    {
        if (_cached is not null) return Parse(_cached);
        try
        {
            using var process = Process.GetCurrentProcess();
            if (!OpenProcessToken(process.Handle, TokenQuery, out IntPtr token)) return Cache("error:0");
            try
            {
                int size = 0;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, ref size);
                if (size <= 0) return Cache("error:0");
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, ref size)) return Cache("error:0");
                    IntPtr sid = Marshal.ReadIntPtr(buffer);
                    int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    if (count <= 0) return Cache("error:0");
                    uint rid = (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, count - 1));
                    return Cache($"{Label(rid)}:{rid}");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "ProcessIntegrity");
            return Cache("error:0");
        }
    }

    private static string Label(uint rid) => rid switch
    {
        < 0x1000 => "Untrusted",
        0x1000 => "Low",
        0x2000 => "Medium",
        0x3000 => "High",
        0x4000 => "System",
        _ => "Unknown"
    };

    private static (string Name, uint Rid) Cache(string encoded)
    {
        _cached = encoded;
        return Parse(encoded);
    }

    private static (string Name, uint Rid) Parse(string encoded)
    {
        string[] parts = encoded.Split(':');
        if (parts.Length != 2) return ("未知", 0);
        if (parts[0] == "error") return ("未知", 0);
        return (parts[0], uint.TryParse(parts[1], out uint rid) ? rid : 0);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int infoLength, ref int returnLength);

    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
