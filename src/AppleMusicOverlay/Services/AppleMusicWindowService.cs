using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AppleMusicOverlay.Services;

/// <summary>
/// Reads the song list of the running Apple Music window through UI Automation.
///
/// The Music app is a WinUI3 (MSIX) application: the UIA tree is only reachable from a .NET
/// Framework client, while .NET's own automation client sees the window but no children. The scan
/// therefore runs in Windows PowerShell (which ships with the .NET Framework automation assemblies)
/// and its tab-separated output is parsed here.
/// </summary>
public sealed class AppleMusicWindowService
{
    private const string WindowName = "Apple Music";

    /// <summary>
    /// Opens the Music window's playlist and walks it to the end by scrolling, then returns the list
    /// to the top. Scrolling is visible, so the caller should tell the user first.
    /// </summary>
    public async Task<IReadOnlyList<PlaylistTrack>> ReadPlaylistAsync(
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        // Overridable so a diagnostic harness can run the scan from a writable folder.
        string directory = Environment.GetEnvironmentVariable("MUSICFLOAT_SCAN_DIR") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(directory)) directory = AppPaths.SettingsDirectory;
        string scriptPath = Path.Combine(directory, "scan-playlist.ps1");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(scriptPath, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AppleMusic:Script");
            return Array.Empty<PlaylistTrack>();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var tracks = new List<PlaylistTrack>();
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null) return tracks;

            Task<string> output = process.StandardOutput.ReadToEndAsync(token);
            Task<string> errors = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            string text = await output;
            string errorText = await errors;
            CrashLog.WriteNote("AppleMusic:Scan",
                $"exit={process.ExitCode} stdout={text.Length} stderr={errorText.Length}");
            if (errorText.Trim().Length > 0) CrashLog.WriteNote("AppleMusic:Script", errorText.Trim());

            tracks.AddRange(ParseOutput(text));
            progress?.Report($"已读取 {tracks.Count} 首");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CrashLog.Write(ex, "AppleMusic:Scan");
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("MUSICFLOAT_SCAN_KEEP") != "1")
            {
                try { File.Delete(scriptPath); } catch { /* temp file */ }
            }
        }

        return tracks;
    }

    /// <summary>Parses the tab-separated rows printed by the scan script (title / artist / album / length).</summary>
    public static List<PlaylistTrack> ParseOutput(string text)
    {
        var tracks = new List<PlaylistTrack>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] cells = line.TrimEnd('\r').Split('\t');
            if (cells.Length < 2) continue;
            string title = cells[0].Trim();
            string artist = cells[1].Trim();
            if (title.Length == 0) continue;
            string album = cells.Length > 2 ? cells[2].Trim() : string.Empty;
            double seconds = cells.Length > 3 ? ParseLength(cells[3]) : 0;
            tracks.Add(new PlaylistTrack(title, artist, album, seconds));
        }
        return tracks;
    }

    private static double ParseLength(string text)
    {
        string[] parts = text.Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out int minutes) && int.TryParse(parts[1], out int seconds))
            return minutes * 60 + seconds;
        if (parts.Length == 3
            && int.TryParse(parts[0], out int hours)
            && int.TryParse(parts[1], out int minutes2)
            && int.TryParse(parts[2], out int seconds2))
            return hours * 3600 + minutes2 * 60 + seconds2;
        return 0;
    }

    /// <summary>Walks the realised rows of the playlist list view, scrolling once per round.</summary>
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $windowCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Apple Music')
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
        if (-not $window) { exit 2 }

        $listCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'ListView')
        $list = $null
        for ($attempt = 0; $attempt -lt 12; $attempt++) {
            $list = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCondition)
            if ($list) { break }
            Start-Sleep -Milliseconds 300
        }
        if (-not $list) { exit 3 }

        $scroll = $null
        try { $scroll = $list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch { }

        $seen = New-Object 'System.Collections.Generic.HashSet[string]'
        $noScroll = [System.Windows.Automation.ScrollPattern]::NoScroll
        $idle = 0
        $percent = 0
        for ($round = 0; $round -lt 500; $round++) {
            $before = $seen.Count
            foreach ($row in $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
                if ($row.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { continue }
                $cells = @()
                foreach ($cell in $row.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
                    if ($cell.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text) { $cells += $cell.Current.Name }
                }
                if ($cells.Count -lt 2) { continue }
                $album = ''
                if ($cells.Count -gt 2) { $album = $cells[2] }
                $length = ''
                if ($cells.Count -gt 5) { $length = $cells[5] }
                $line = $cells[0] + [char]9 + $cells[1] + [char]9 + $album + [char]9 + $length
                if ($seen.Add($line)) { Write-Output $line }
            }

            if ($seen.Count -eq $before) { $idle++ } else { $idle = 0 }
            if ($idle -ge 4) { break }
            if (-not $scroll) { break }

            # A WinUI3 list answers LargeIncrement by doing nothing, so the scroll position is set
            # explicitly, a few percent at a time, until the bottom is reached.
            $advanced = $false
            try {
                $current = $scroll.Current.VerticalScrollPercent
                $percent = [Math]::Min(100, $current + 8)
                $scroll.SetScrollPercent($noScroll, $percent)
                $advanced = $true
            } catch { }

            if (-not $advanced) {
                try { $scroll.Scroll($noScroll, [System.Windows.Automation.ScrollAmount]::LargeIncrement) }
                catch { break }
            }

            Start-Sleep -Milliseconds 420
        }

        if ($scroll) {
            try { $scroll.SetScrollPercent($noScroll, 0) } catch { }
            for ($i = 0; $i -lt 60; $i++) {
                try { if ($scroll.Current.VerticalScrollPercent -le 0.5) { break } } catch { break }
                try { $scroll.Scroll($noScroll, [System.Windows.Automation.ScrollAmount]::LargeDecrement) }
                catch { break }
                Start-Sleep -Milliseconds 120
            }
        }
        """;
}
