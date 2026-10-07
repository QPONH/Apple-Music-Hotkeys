using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AppleMusicOverlay.Services;

/// <summary>
/// Controls Apple Music's application-level audio-session volume in the Windows volume mixer.
/// This does not change the Windows master volume or other applications' volume.
/// </summary>
public sealed class AppleMusicVolumeService : IDisposable
{
    private MMDeviceEnumerator? _enumerator;

    public bool TryChange(float delta, out float newVolume)
    {
        newVolume = 0;

        try
        {
            // Do not assume Apple Music is using the Windows default endpoint.
            // Windows Volume Mixer can assign an application to a different output device.
            foreach (MMDevice device in GetEnumerator().EnumerateAudioEndPoints(
                         DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    SessionCollection sessions = device.AudioSessionManager.Sessions;
                    AudioSessionControl? target = FindAppleMusicSession(sessions);
                    if (target == null)
                    {
                        continue;
                    }

                    SimpleAudioVolume volume = target.SimpleAudioVolume;
                    float current = volume.Volume;
                    newVolume = Math.Clamp(current + delta, 0f, 1f);
                    volume.Volume = newVolume;
                    return true;
                }
            }
        }
        catch
        {
            // Keep the hotkey handler alive if an audio endpoint/session disappears
            // while Windows is refreshing the audio session list.
        }

        return false;
    }

    private MMDeviceEnumerator GetEnumerator() => _enumerator ??= new MMDeviceEnumerator();

    private static AudioSessionControl? FindAppleMusicSession(SessionCollection sessions)
    {
        for (int i = 0; i < sessions.Count; i++)
        {
            AudioSessionControl session;
            try
            {
                session = sessions[i];
            }
            catch
            {
                continue;
            }

            // Ignore disconnected/inactive sessions. The Windows mixer can retain
            // recently-used sessions even after the application stops producing audio.
            try
            {
                if (session.State != AudioSessionState.AudioSessionStateActive)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            string displayName = SafeDisplayName(session);
            if (LooksLikeAppleMusic(displayName))
            {
                return session;
            }

            uint processId;
            try
            {
                processId = session.GetProcessID;
            }
            catch
            {
                continue;
            }

            if (processId == 0)
            {
                continue;
            }

            try
            {
                using Process process = Process.GetProcessById((int)processId);
                if (LooksLikeAppleMusic(process))
                {
                    return session;
                }
            }
            catch
            {
                // The process may have exited between session enumeration and inspection.
            }
        }

        return null;
    }

    private static string SafeDisplayName(AudioSessionControl session)
    {
        try
        {
            return session.DisplayName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool LooksLikeAppleMusic(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Cover the normal Windows Apple Music labels as well as localized/display-name
        // variations such as simply "Music".
        return value.Contains("Apple Music", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Music", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAppleMusic(Process process)
    {
        string processName = process.ProcessName;
        if (processName.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("Music", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            string executablePath = process.MainModule?.FileName ?? string.Empty;
            return executablePath.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Access to MainModule can fail for a protected process; process name is enough
            // for the normal Apple Music desktop process.
            return false;
        }
    }

    public void Dispose()
    {
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
