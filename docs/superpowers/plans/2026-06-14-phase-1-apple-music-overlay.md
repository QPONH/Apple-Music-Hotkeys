# Phase 1 Apple Music Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first usable Windows desktop version: local-only Apple Music PWA SMTC reader, default A-style minimal cover overlay, keyboard controls, tray residency, and local settings.

**Architecture:** Use a .NET 8 WPF app with a small set of pure services around settings, hotkey parsing, track identity, and media monitoring. Isolate Windows-only APIs behind service classes so most behavior is testable without a visible window or active media session. The overlay is a non-activated transparent topmost WPF window using Win32 extended window styles; no game injection, DirectX hook, driver overlay, or process patching is allowed.

**Tech Stack:** .NET 8, WPF, Windows.Media.Control SMTC, Win32 user32 hotkeys/window styles, Windows Forms NotifyIcon for tray, xUnit tests.

---

## Scope Check

The PRD includes three phases. This implementation plan covers only Phase 1:

- SMTC current track reading for Edge Apple Music PWA and other current SMTC sessions.
- A-style minimal cover overlay.
- Keyboard shortcuts for previous, next, play/pause, and test overlay.
- Local JSON settings.
- Tray residency.

This plan intentionally excludes Xbox input, DS5 input, B/C visual styles, lyrics, spectrum, and Apple Music "favorite" automation. The `Favorite` action can appear as a disabled action in the UI with an explicit message because SMTC does not expose a universal favorite command.

## Toolchain Preflight

Current local observation: `dotnet --info` reports installed .NET runtimes but no SDK. Implementation requires the .NET 8 SDK.

- If execution reaches Task 1 and `dotnet --list-sdks` is empty, ask the user before installing the SDK.
- Recommended install command on Windows: `winget install Microsoft.DotNet.SDK.8`.
- If network or installer access is blocked, stop at the exact installer error and do not scaffold files by hand.

## File Structure

Create this structure:

```text
AppleMusicOverlay.sln
src/AppleMusicOverlay/
  AppleMusicOverlay.csproj
  App.xaml
  App.xaml.cs
  MainWindow.xaml
  MainWindow.xaml.cs
  Models/
    AppAction.cs
    DisplayStyle.cs
    HotkeyDefinition.cs
    OverlaySettings.cs
    TrackInfo.cs
  Services/
    AppPaths.cs
    GlobalHotkeyService.cs
    HotkeyParser.cs
    IMediaSessionService.cs
    OverlaySettingsNormalizer.cs
    OverlaySettingsService.cs
    SmtcMediaSessionService.cs
    TrackIdentity.cs
    TrackMonitor.cs
    TrayIconService.cs
    WindowStyleService.cs
  ViewModels/
    MainViewModel.cs
  Views/
    OverlayWindow.xaml
    OverlayWindow.xaml.cs
tests/AppleMusicOverlay.Tests/
  AppleMusicOverlay.Tests.csproj
  HotkeyParserTests.cs
  OverlaySettingsTests.cs
  TrackIdentityTests.cs
  TrackMonitorTests.cs
```

Responsibilities:

- `Models/*`: small data records/enums only.
- `Services/HotkeyParser.cs`: parse text like `Ctrl+Shift+Right` into Win32 modifier and virtual key values.
- `Services/GlobalHotkeyService.cs`: register/unregister Win32 keyboard hotkeys and raise app actions.
- `Services/SmtcMediaSessionService.cs`: read track metadata and control playback via Windows SMTC.
- `Services/TrackMonitor.cs`: low-frequency polling loop that compares track keys and raises `TrackChanged`.
- `Views/OverlayWindow.*`: visual A-style cover overlay and Win32 transparency/click-through setup.
- `ViewModels/MainViewModel.cs`: binds main settings UI to services without embedding business logic in `MainWindow.xaml.cs`.
- `Services/TrayIconService.cs`: system tray menu and lifecycle actions.

## Task 1: Install/Confirm SDK and Scaffold Solution

**Files:**
- Create: `AppleMusicOverlay.sln`
- Create: `src/AppleMusicOverlay/AppleMusicOverlay.csproj`
- Create: `tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj`

- [ ] **Step 1: Verify SDK availability**

Run:

```powershell
dotnet --list-sdks
```

Expected when ready: at least one line beginning with `8.`.

If output is empty, ask for permission to install:

```powershell
winget install Microsoft.DotNet.SDK.8
```

Expected after install: `dotnet --list-sdks` includes an `8.x.x` SDK.

- [ ] **Step 2: Scaffold solution and projects**

Run:

```powershell
dotnet new sln -n AppleMusicOverlay
dotnet new wpf -n AppleMusicOverlay -o src/AppleMusicOverlay -f net8.0-windows
dotnet new xunit -n AppleMusicOverlay.Tests -o tests/AppleMusicOverlay.Tests -f net8.0
dotnet sln AppleMusicOverlay.sln add src/AppleMusicOverlay/AppleMusicOverlay.csproj
dotnet sln AppleMusicOverlay.sln add tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj
dotnet add tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj reference src/AppleMusicOverlay/AppleMusicOverlay.csproj
```

Expected: solution and two projects are created successfully.

- [ ] **Step 3: Configure WPF project for SMTC**

Modify `src/AppleMusicOverlay/AppleMusicOverlay.csproj` to use this property group:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Add app manifest**

Create `src/AppleMusicOverlay/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="AppleMusicOverlay"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 5: Verify empty scaffold builds**

Run:

```powershell
dotnet build AppleMusicOverlay.sln
```

Expected: `Build succeeded`.

- [ ] **Step 6: Commit scaffold**

Run:

```powershell
git add AppleMusicOverlay.sln src/AppleMusicOverlay tests/AppleMusicOverlay.Tests
git commit -m "chore: scaffold apple music overlay app"
```

## Task 2: Models and Settings Persistence

**Files:**
- Create: `src/AppleMusicOverlay/Models/AppAction.cs`
- Create: `src/AppleMusicOverlay/Models/DisplayStyle.cs`
- Create: `src/AppleMusicOverlay/Models/OverlaySettings.cs`
- Create: `src/AppleMusicOverlay/Models/TrackInfo.cs`
- Create: `src/AppleMusicOverlay/Services/AppPaths.cs`
- Create: `src/AppleMusicOverlay/Services/OverlaySettingsNormalizer.cs`
- Create: `src/AppleMusicOverlay/Services/OverlaySettingsService.cs`
- Test: `tests/AppleMusicOverlay.Tests/OverlaySettingsTests.cs`

- [ ] **Step 1: Write failing settings tests**

Create `tests/AppleMusicOverlay.Tests/OverlaySettingsTests.cs`:

```csharp
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class OverlaySettingsTests
{
    [Fact]
    public void DefaultSettingsUseMinimalCoverStyle()
    {
        var settings = new OverlaySettings();

        Assert.Equal(DisplayStyle.MinimalCover, settings.DisplayStyle);
        Assert.True(settings.ShowTitle);
        Assert.True(settings.ShowArtist);
        Assert.False(settings.ShowControls);
        Assert.Equal(5, settings.DisplaySeconds);
    }

    [Fact]
    public void NormalizeClampsNumericSettings()
    {
        var settings = new OverlaySettings
        {
            LeftPercent = -0.25,
            TopPercent = 2.0,
            ScalePercent = 240,
            DisplaySeconds = 45
        };

        var normalized = OverlaySettingsNormalizer.Normalize(settings);

        Assert.Equal(0, normalized.LeftPercent);
        Assert.Equal(1, normalized.TopPercent);
        Assert.Equal(180, normalized.ScalePercent);
        Assert.Equal(10, normalized.DisplaySeconds);
    }

    [Fact]
    public void SettingsServiceRoundTripsJson()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AppleMusicOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        var service = new OverlaySettingsService(path);
        var input = new OverlaySettings
        {
            LeftPercent = 0.4,
            TopPercent = 0.7,
            KeyboardNext = "Ctrl+Shift+Right",
            PauseOverlay = true
        };

        service.Save(input);
        OverlaySettings loaded = service.Load();

        Assert.Equal(0.4, loaded.LeftPercent);
        Assert.Equal(0.7, loaded.TopPercent);
        Assert.Equal("Ctrl+Shift+Right", loaded.KeyboardNext);
        Assert.True(loaded.PauseOverlay);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj --filter OverlaySettingsTests
```

Expected: FAIL because `AppleMusicOverlay.Models` and services do not exist.

- [ ] **Step 3: Add model files**

Create `src/AppleMusicOverlay/Models/DisplayStyle.cs`:

```csharp
namespace AppleMusicOverlay.Models;

public enum DisplayStyle
{
    MinimalCover = 0,
    LiquidGlassControls = 1,
    ImmersiveCoverCard = 2
}
```

Create `src/AppleMusicOverlay/Models/AppAction.cs`:

```csharp
namespace AppleMusicOverlay.Models;

public enum AppAction
{
    PreviousTrack = 0,
    NextTrack = 1,
    TogglePlayPause = 2,
    ShowTestOverlay = 3,
    FavoriteTrack = 4
}
```

Create `src/AppleMusicOverlay/Models/TrackInfo.cs`:

```csharp
namespace AppleMusicOverlay.Models;

public sealed record TrackInfo(
    string Title,
    string Artist,
    byte[]? CoverBytes,
    string SourceAppId,
    TimeSpan Duration,
    bool IsPlaying);
```

Create `src/AppleMusicOverlay/Models/OverlaySettings.cs`:

```csharp
namespace AppleMusicOverlay.Models;

public sealed class OverlaySettings
{
    public int SchemaVersion { get; set; } = 1;
    public DisplayStyle DisplayStyle { get; set; } = DisplayStyle.MinimalCover;
    public double LeftPercent { get; set; } = 0.04;
    public double TopPercent { get; set; } = 0.42;
    public int ScalePercent { get; set; } = 100;
    public int DisplaySeconds { get; set; } = 5;
    public bool ShowTitle { get; set; } = true;
    public bool ShowArtist { get; set; } = true;
    public bool ShowControls { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public bool AutoStart { get; set; } = false;
    public bool PauseOverlay { get; set; } = false;
    public string KeyboardPrevious { get; set; } = "Ctrl+Shift+Left";
    public string KeyboardNext { get; set; } = "Ctrl+Shift+Right";
    public string KeyboardToggle { get; set; } = "Ctrl+Shift+Down";
    public string KeyboardTestOverlay { get; set; } = "Ctrl+Shift+Up";
}
```

- [ ] **Step 4: Add settings services**

Create `src/AppleMusicOverlay/Services/AppPaths.cs`:

```csharp
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
```

Create `src/AppleMusicOverlay/Services/OverlaySettingsNormalizer.cs`:

```csharp
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public static class OverlaySettingsNormalizer
{
    public static OverlaySettings Normalize(OverlaySettings settings)
    {
        settings.LeftPercent = Clamp(settings.LeftPercent, 0, 1);
        settings.TopPercent = Clamp(settings.TopPercent, 0, 1);
        settings.ScalePercent = (int)Clamp(settings.ScalePercent, 70, 180);
        settings.DisplaySeconds = (int)Clamp(settings.DisplaySeconds, 1, 10);

        if (!Enum.IsDefined(settings.DisplayStyle))
        {
            settings.DisplayStyle = DisplayStyle.MinimalCover;
        }

        settings.KeyboardPrevious = NormalizeText(settings.KeyboardPrevious, "Ctrl+Shift+Left");
        settings.KeyboardNext = NormalizeText(settings.KeyboardNext, "Ctrl+Shift+Right");
        settings.KeyboardToggle = NormalizeText(settings.KeyboardToggle, "Ctrl+Shift+Down");
        settings.KeyboardTestOverlay = NormalizeText(settings.KeyboardTestOverlay, "Ctrl+Shift+Up");
        return settings;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
```

Create `src/AppleMusicOverlay/Services/OverlaySettingsService.cs`:

```csharp
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

    public OverlaySettingsService() : this(AppPaths.SettingsFilePath)
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
                return new OverlaySettings();
            }

            string json = File.ReadAllText(_filePath);
            OverlaySettings? settings = JsonSerializer.Deserialize<OverlaySettings>(json, JsonOptions);
            return OverlaySettingsNormalizer.Normalize(settings ?? new OverlaySettings());
        }
        catch
        {
            return new OverlaySettings();
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

        File.WriteAllText(_filePath, JsonSerializer.Serialize(normalized, JsonOptions));
    }
}
```

- [ ] **Step 5: Run settings tests**

Run:

```powershell
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj --filter OverlaySettingsTests
```

Expected: PASS.

- [ ] **Step 6: Commit settings foundation**

Run:

```powershell
git add src/AppleMusicOverlay/Models src/AppleMusicOverlay/Services/AppPaths.cs src/AppleMusicOverlay/Services/OverlaySettingsNormalizer.cs src/AppleMusicOverlay/Services/OverlaySettingsService.cs tests/AppleMusicOverlay.Tests/OverlaySettingsTests.cs
git commit -m "feat: add overlay settings model"
```

## Task 3: Hotkey Parsing and Keyboard Registration

**Files:**
- Create: `src/AppleMusicOverlay/Models/HotkeyDefinition.cs`
- Create: `src/AppleMusicOverlay/Services/HotkeyParser.cs`
- Create: `src/AppleMusicOverlay/Services/GlobalHotkeyService.cs`
- Test: `tests/AppleMusicOverlay.Tests/HotkeyParserTests.cs`

- [ ] **Step 1: Write failing hotkey parser tests**

Create `tests/AppleMusicOverlay.Tests/HotkeyParserTests.cs`:

```csharp
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class HotkeyParserTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Right", 0x0002u | 0x0004u, 0x27u)]
    [InlineData("Alt+Left", 0x0001u, 0x25u)]
    [InlineData("Ctrl+Shift+Up", 0x0002u | 0x0004u, 0x26u)]
    public void TryParseParsesSupportedCombinations(string text, uint modifiers, uint virtualKey)
    {
        bool ok = HotkeyParser.TryParse(text, out var result);

        Assert.True(ok);
        Assert.Equal(modifiers, result.Modifiers);
        Assert.Equal(virtualKey, result.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+UnknownKey")]
    [InlineData("Ctrl+Shift+Alt")]
    public void TryParseRejectsInvalidInput(string text)
    {
        Assert.False(HotkeyParser.TryParse(text, out _));
    }
}
```

- [ ] **Step 2: Run parser tests to verify failure**

Run:

```powershell
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj --filter HotkeyParserTests
```

Expected: FAIL because parser files do not exist.

- [ ] **Step 3: Add hotkey definition and parser**

Create `src/AppleMusicOverlay/Models/HotkeyDefinition.cs`:

```csharp
namespace AppleMusicOverlay.Models;

public readonly record struct HotkeyDefinition(uint Modifiers, uint VirtualKey);
```

Create `src/AppleMusicOverlay/Services/HotkeyParser.cs`:

```csharp
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public static class HotkeyParser
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private static readonly Dictionary<string, uint> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Escape"] = 0x1B,
        ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45,
        ["F"] = 0x46, ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A,
        ["K"] = 0x4B, ["L"] = 0x4C, ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F,
        ["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52, ["S"] = 0x53, ["T"] = 0x54,
        ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58, ["Y"] = 0x59,
        ["Z"] = 0x5A
    };

    public static bool TryParse(string? text, out HotkeyDefinition hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint modifiers = 0;
        uint? virtualKey = null;
        foreach (string rawToken in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawToken.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    if (virtualKey.HasValue || !KeyMap.TryGetValue(rawToken, out uint key))
                    {
                        return false;
                    }
                    virtualKey = key;
                    break;
            }
        }

        if (!virtualKey.HasValue)
        {
            return false;
        }

        hotkey = new HotkeyDefinition(modifiers, virtualKey.Value);
        return true;
    }
}
```

- [ ] **Step 4: Add Win32 hotkey service**

Create `src/AppleMusicOverlay/Services/GlobalHotkeyService.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly Window _owner;
    private readonly Dictionary<int, AppAction> _actions = new();
    private HwndSource? _source;
    private IntPtr _hwnd;
    private int _nextId = 0x7100;

    public event EventHandler<AppAction>? ActionRequested;

    public GlobalHotkeyService(Window owner)
    {
        _owner = owner;
    }

    public bool Register(AppAction action, string hotkeyText)
    {
        EnsureHook();
        if (!HotkeyParser.TryParse(hotkeyText, out HotkeyDefinition hotkey))
        {
            return false;
        }

        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, hotkey.Modifiers, hotkey.VirtualKey))
        {
            return false;
        }

        _actions[id] = action;
        return true;
    }

    public void Clear()
    {
        foreach (int id in _actions.Keys.ToArray())
        {
            UnregisterHotKey(_hwnd, id);
        }
        _actions.Clear();
    }

    private void EnsureHook()
    {
        if (_source != null)
        {
            return;
        }

        _hwnd = new WindowInteropHelper(_owner).Handle;
        _source = HwndSource.FromHwnd(_hwnd) ?? throw new InvalidOperationException("Unable to attach window message hook.");
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out AppAction action))
        {
            ActionRequested?.Invoke(this, action);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Clear();
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
```

- [ ] **Step 5: Run hotkey parser tests**

Run:

```powershell
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj --filter HotkeyParserTests
```

Expected: PASS.

- [ ] **Step 6: Commit hotkey parsing**

Run:

```powershell
git add src/AppleMusicOverlay/Models/HotkeyDefinition.cs src/AppleMusicOverlay/Services/HotkeyParser.cs src/AppleMusicOverlay/Services/GlobalHotkeyService.cs tests/AppleMusicOverlay.Tests/HotkeyParserTests.cs
git commit -m "feat: add keyboard hotkey support"
```

## Task 4: Track Identity, SMTC Service, and Change Monitor

**Files:**
- Create: `src/AppleMusicOverlay/Services/TrackIdentity.cs`
- Create: `src/AppleMusicOverlay/Services/IMediaSessionService.cs`
- Create: `src/AppleMusicOverlay/Services/SmtcMediaSessionService.cs`
- Create: `src/AppleMusicOverlay/Services/TrackMonitor.cs`
- Test: `tests/AppleMusicOverlay.Tests/TrackIdentityTests.cs`
- Test: `tests/AppleMusicOverlay.Tests/TrackMonitorTests.cs`

- [ ] **Step 1: Write failing track identity tests**

Create `tests/AppleMusicOverlay.Tests/TrackIdentityTests.cs`:

```csharp
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class TrackIdentityTests
{
    [Fact]
    public void BuildKeyIncludesSourceTitleAndArtist()
    {
        var track = new TrackInfo("  Song A  ", " Artist A ", null, "MSEdge", TimeSpan.Zero, true);

        string key = TrackIdentity.BuildKey(track);

        Assert.Equal("MSEdge|Song A|Artist A", key);
    }
}
```

- [ ] **Step 2: Write failing track monitor tests**

Create `tests/AppleMusicOverlay.Tests/TrackMonitorTests.cs`:

```csharp
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class TrackMonitorTests
{
    [Fact]
    public async Task PollOnceRaisesTrackChangedOnlyWhenKeyChanges()
    {
        var first = new TrackInfo("Song A", "Artist", null, "MSEdge", TimeSpan.Zero, true);
        var second = new TrackInfo("Song B", "Artist", null, "MSEdge", TimeSpan.Zero, true);
        var fake = new FakeMediaSessionService(first, first, second);
        var monitor = new TrackMonitor(fake);
        var changed = new List<TrackInfo>();
        monitor.TrackChanged += (_, track) => changed.Add(track);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(2, changed.Count);
        Assert.Equal("Song A", changed[0].Title);
        Assert.Equal("Song B", changed[1].Title);
    }

    private sealed class FakeMediaSessionService : IMediaSessionService
    {
        private readonly Queue<TrackInfo?> _tracks;

        public FakeMediaSessionService(params TrackInfo?[] tracks)
        {
            _tracks = new Queue<TrackInfo?>(tracks);
        }

        public Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_tracks.Count == 0 ? null : _tracks.Dequeue());
        }

        public Task<bool> PreviousAsync() => Task.FromResult(true);
        public Task<bool> NextAsync() => Task.FromResult(true);
        public Task<bool> TogglePlayPauseAsync() => Task.FromResult(true);
    }
}
```

- [ ] **Step 3: Run monitor tests to verify failure**

Run:

```powershell
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj --filter "TrackIdentityTests|TrackMonitorTests"
```

Expected: FAIL because track services do not exist.

- [ ] **Step 4: Add identity and media interface**

Create `src/AppleMusicOverlay/Services/TrackIdentity.cs`:

```csharp
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public static class TrackIdentity
{
    public static string BuildKey(TrackInfo track)
    {
        return $"{Normalize(track.SourceAppId)}|{Normalize(track.Title)}|{Normalize(track.Artist)}";
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }
}
```

Create `src/AppleMusicOverlay/Services/IMediaSessionService.cs`:

```csharp
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public interface IMediaSessionService
{
    Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken);
    Task<bool> PreviousAsync();
    Task<bool> NextAsync();
    Task<bool> TogglePlayPauseAsync();
}
```

- [ ] **Step 5: Add SMTC service**

Create `src/AppleMusicOverlay/Services/SmtcMediaSessionService.cs`:

```csharp
using AppleMusicOverlay.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AppleMusicOverlay.Services;

public sealed class SmtcMediaSessionService : IMediaSessionService
{
    public async Task<TrackInfo?> GetCurrentTrackAsync(CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSession? session = await GetCurrentSessionAsync();
        if (session == null)
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties media = await session.TryGetMediaPropertiesAsync();
        string title = string.IsNullOrWhiteSpace(media.Title) ? string.Empty : media.Title.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string artist = string.IsNullOrWhiteSpace(media.Artist) ? "Unknown Artist" : media.Artist.Trim();
        byte[]? cover = await ReadThumbnailBytesAsync(media.Thumbnail);
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
        bool isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        return new TrackInfo(
            title,
            artist,
            cover,
            string.IsNullOrWhiteSpace(session.SourceAppUserModelId) ? "SMTC" : session.SourceAppUserModelId,
            timeline.EndTime,
            isPlaying);
    }

    public async Task<bool> PreviousAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = await GetCurrentSessionAsync();
        return session != null && await session.TrySkipPreviousAsync();
    }

    public async Task<bool> NextAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = await GetCurrentSessionAsync();
        return session != null && await session.TrySkipNextAsync();
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = await GetCurrentSessionAsync();
        if (session == null)
        {
            return false;
        }

        GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
        return playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? await session.TryPauseAsync()
            : await session.TryPlayAsync();
    }

    private static async Task<GlobalSystemMediaTransportControlsSession?> GetCurrentSessionAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSessionManager manager =
            await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return manager.GetCurrentSession();
    }

    private static async Task<byte[]?> ReadThumbnailBytesAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail == null)
        {
            return null;
        }

        try
        {
            using IRandomAccessStreamWithContentType stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > int.MaxValue)
            {
                return null;
            }

            var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
            IBuffer loaded = await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);
            byte[] bytes = new byte[loaded.Length];
            using DataReader reader = DataReader.FromBuffer(loaded);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 6: Add track monitor**

Create `src/AppleMusicOverlay/Services/TrackMonitor.cs`:

```csharp
using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed class TrackMonitor : IDisposable
{
    private readonly IMediaSessionService _mediaSessionService;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _lastKey = string.Empty;

    public event EventHandler<TrackInfo>? TrackChanged;
    public event EventHandler<TrackInfo?>? TrackRead;

    public TrackMonitor(IMediaSessionService mediaSessionService, TimeSpan? interval = null)
    {
        _mediaSessionService = mediaSessionService;
        _interval = interval ?? TimeSpan.FromMilliseconds(900);
    }

    public void Start()
    {
        if (_loop != null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        TrackInfo? track = await _mediaSessionService.GetCurrentTrackAsync(cancellationToken);
        TrackRead?.Invoke(this, track);
        if (track == null)
        {
            _lastKey = string.Empty;
            return;
        }

        string key = TrackIdentity.BuildKey(track);
        if (!string.Equals(_lastKey, key, StringComparison.Ordinal))
        {
            _lastKey = key;
            TrackChanged?.Invoke(this, track);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollOnceAsync(cancellationToken);
            await Task.Delay(_interval, cancellationToken);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }
}
```

- [ ] **Step 7: Run identity and monitor tests**

Run:

```powershell
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj --filter "TrackIdentityTests|TrackMonitorTests"
```

Expected: PASS.

- [ ] **Step 8: Commit media monitor foundation**

Run:

```powershell
git add src/AppleMusicOverlay/Services/TrackIdentity.cs src/AppleMusicOverlay/Services/IMediaSessionService.cs src/AppleMusicOverlay/Services/SmtcMediaSessionService.cs src/AppleMusicOverlay/Services/TrackMonitor.cs tests/AppleMusicOverlay.Tests/TrackIdentityTests.cs tests/AppleMusicOverlay.Tests/TrackMonitorTests.cs
git commit -m "feat: add smtc track monitor"
```

## Task 5: A-Style Minimal Overlay Window

**Files:**
- Create: `src/AppleMusicOverlay/Services/WindowStyleService.cs`
- Create: `src/AppleMusicOverlay/Views/OverlayWindow.xaml`
- Create: `src/AppleMusicOverlay/Views/OverlayWindow.xaml.cs`

- [ ] **Step 1: Add Win32 window style service**

Create `src/AppleMusicOverlay/Services/WindowStyleService.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AppleMusicOverlay.Services;

public static class WindowStyleService
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolwindow = 0x00000080;

    public static void ApplyOverlayStyles(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        int exStyle = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, exStyle | WsExLayered | WsExTransparent | WsExToolwindow);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
```

- [ ] **Step 2: Create overlay XAML**

Create `src/AppleMusicOverlay/Views/OverlayWindow.xaml`:

```xml
<Window x:Class="AppleMusicOverlay.Views.OverlayWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Overlay"
        Width="168"
        Height="184"
        WindowStyle="None"
        ResizeMode="NoResize"
        AllowsTransparency="True"
        Background="Transparent"
        ShowInTaskbar="False"
        ShowActivated="False"
        Topmost="True"
        Focusable="False">
    <Grid x:Name="OverlayRoot" Opacity="0" RenderTransformOrigin="0.5,0.5">
        <Grid.RenderTransform>
            <TransformGroup>
                <ScaleTransform x:Name="RootScale" ScaleX="0.96" ScaleY="0.96" />
                <TranslateTransform x:Name="RootTranslate" Y="8" />
            </TransformGroup>
        </Grid.RenderTransform>
        <Grid.RowDefinitions>
            <RowDefinition Height="124" />
            <RowDefinition Height="60" />
        </Grid.RowDefinitions>

        <Border Width="112"
                Height="112"
                CornerRadius="24"
                HorizontalAlignment="Center"
                VerticalAlignment="Center"
                Background="#252B34"
                BorderBrush="#55FFFFFF"
                BorderThickness="1">
            <Border.Effect>
                <DropShadowEffect BlurRadius="28" ShadowDepth="10" Direction="270" Opacity="0.55" Color="#101318" />
            </Border.Effect>
            <Image x:Name="CoverImage" Stretch="UniformToFill" />
        </Border>

        <StackPanel Grid.Row="1" HorizontalAlignment="Center" VerticalAlignment="Top" Width="156">
            <TextBlock x:Name="TitleText"
                       FontFamily="Segoe UI Variable Display, Segoe UI"
                       FontSize="15"
                       FontWeight="SemiBold"
                       Foreground="#F7F8FB"
                       TextAlignment="Center"
                       TextTrimming="CharacterEllipsis"
                       TextWrapping="NoWrap">
                <TextBlock.Effect>
                    <DropShadowEffect BlurRadius="16" ShadowDepth="0" Opacity="0.9" Color="#05070A" />
                </TextBlock.Effect>
            </TextBlock>
            <TextBlock x:Name="ArtistText"
                       Margin="0,3,0,0"
                       FontFamily="Segoe UI Variable Text, Segoe UI"
                       FontSize="12"
                       Foreground="#B8C0CC"
                       TextAlignment="Center"
                       TextTrimming="CharacterEllipsis"
                       TextWrapping="NoWrap">
                <TextBlock.Effect>
                    <DropShadowEffect BlurRadius="12" ShadowDepth="0" Opacity="0.85" Color="#05070A" />
                </TextBlock.Effect>
            </TextBlock>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 3: Create overlay code-behind**

Create `src/AppleMusicOverlay/Views/OverlayWindow.xaml.cs`:

```csharp
using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Views;

public partial class OverlayWindow : Window
{
    private CancellationTokenSource? _hideCts;
    private OverlaySettings _settings = new();

    public OverlayWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        SourceInitialized += (_, _) => WindowStyleService.ApplyOverlayStyles(this);
    }

    public void ApplySettings(OverlaySettings settings)
    {
        _settings = OverlaySettingsNormalizer.Normalize(settings);
        double scale = _settings.ScalePercent / 100.0;
        Width = 168 * scale;
        Height = 184 * scale;
        Left = Math.Max(0, SystemParameters.PrimaryScreenWidth - Width) * _settings.LeftPercent;
        Top = Math.Max(0, SystemParameters.PrimaryScreenHeight - Height) * _settings.TopPercent;
        TitleText.Visibility = _settings.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
        ArtistText.Visibility = _settings.ShowArtist ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task ShowTrackAsync(TrackInfo track)
    {
        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        TitleText.Text = track.Title;
        ArtistText.Text = track.Artist;
        CoverImage.Source = CreateCover(track);
        Show();
        Visibility = Visibility.Visible;
        BeginEnterAnimation();

        if (!_settings.PauseOverlay)
        {
            CancellationToken token = _hideCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_settings.DisplaySeconds), token);
                    await Dispatcher.InvokeAsync(BeginExitAnimation);
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        }
    }

    private void BeginEnterAnimation()
    {
        OverlayRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)));
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)));
        RootTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)));
    }

    private void BeginExitAnimation()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Hide();
        OverlayRoot.BeginAnimation(OpacityProperty, fade);
    }

    private static BitmapSource CreateCover(TrackInfo track)
    {
        if (track.CoverBytes is { Length: > 0 })
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(track.CoverBytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        return CreatePlaceholder();
    }

    private static BitmapSource CreatePlaceholder()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0x34, 0x2B, 0x25, 0xFF }, 4);
        bitmap.Freeze();
        return bitmap;
    }
}
```

- [ ] **Step 4: Build app**

Run:

```powershell
dotnet build src/AppleMusicOverlay/AppleMusicOverlay.csproj
```

Expected: PASS.

- [ ] **Step 5: Commit overlay window**

Run:

```powershell
git add src/AppleMusicOverlay/Services/WindowStyleService.cs src/AppleMusicOverlay/Views/OverlayWindow.xaml src/AppleMusicOverlay/Views/OverlayWindow.xaml.cs
git commit -m "feat: add minimal cover overlay"
```

## Task 6: Main Window, ViewModel, and App Wiring

**Files:**
- Modify: `src/AppleMusicOverlay/App.xaml`
- Modify: `src/AppleMusicOverlay/App.xaml.cs`
- Modify: `src/AppleMusicOverlay/MainWindow.xaml`
- Modify: `src/AppleMusicOverlay/MainWindow.xaml.cs`
- Create: `src/AppleMusicOverlay/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Create main view model**

Create `src/AppleMusicOverlay/ViewModels/MainViewModel.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly OverlaySettingsService _settingsService;
    private OverlaySettings _settings;
    private TrackInfo? _currentTrack;
    private string _statusText = "请先在 Apple Music 中播放一首歌曲";

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(OverlaySettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = settingsService.Load();
    }

    public OverlaySettings Settings => _settings;

    public TrackInfo? CurrentTrack
    {
        get => _currentTrack;
        private set
        {
            _currentTrack = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentArtist));
        }
    }

    public string CurrentTitle => CurrentTrack?.Title ?? "未检测到歌曲";
    public string CurrentArtist => CurrentTrack?.Artist ?? "请确认 Apple Music PWA 正在播放";

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public void ApplyTrack(TrackInfo? track)
    {
        CurrentTrack = track;
        StatusText = track == null ? "未读取到系统媒体会话" : "已连接 Apple Music";
    }

    public void Save()
    {
        _settingsService.Save(_settings);
        StatusText = "设置已保存";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
```

- [ ] **Step 2: Replace main window XAML**

Replace `src/AppleMusicOverlay/MainWindow.xaml`:

```xml
<Window x:Class="AppleMusicOverlay.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Apple Music Overlay"
        Width="820"
        Height="520"
        MinWidth="720"
        MinHeight="460"
        Background="#101318">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <StackPanel>
            <TextBlock Text="Apple Music Overlay" FontSize="26" FontWeight="SemiBold" Foreground="#F7F8FB" />
            <TextBlock Text="本地、免费、无注入的 Apple Music PWA 悬浮封面工具" Margin="0,6,0,0" Foreground="#AAB3C2" />
        </StackPanel>

        <Grid Grid.Row="1" Margin="0,24,0,20">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="260" />
                <ColumnDefinition Width="24" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>

            <Border CornerRadius="14" Background="#171C25" BorderBrush="#2A3442" BorderThickness="1" Padding="16">
                <StackPanel>
                    <TextBlock Text="当前播放" Foreground="#AAB3C2" />
                    <TextBlock Text="{Binding CurrentTitle}" Margin="0,16,0,0" FontSize="20" FontWeight="SemiBold" Foreground="#F7F8FB" TextWrapping="Wrap" />
                    <TextBlock Text="{Binding CurrentArtist}" Margin="0,6,0,0" Foreground="#AAB3C2" TextWrapping="Wrap" />
                    <Button Content="测试覆盖层" Margin="0,24,0,0" Height="34" Click="TestOverlay_Click" />
                    <Button Content="立即刷新" Margin="0,10,0,0" Height="34" Click="Refresh_Click" />
                </StackPanel>
            </Border>

            <Border Grid.Column="2" CornerRadius="14" Background="#171C25" BorderBrush="#2A3442" BorderThickness="1" Padding="16">
                <StackPanel>
                    <TextBlock Text="Phase 1 设置" FontSize="18" FontWeight="SemiBold" Foreground="#F7F8FB" />
                    <CheckBox Content="显示歌名" Margin="0,18,0,0" Foreground="#F7F8FB" IsChecked="{Binding Settings.ShowTitle}" />
                    <CheckBox Content="显示歌手" Margin="0,10,0,0" Foreground="#F7F8FB" IsChecked="{Binding Settings.ShowArtist}" />
                    <CheckBox Content="暂停覆盖层" Margin="0,10,0,0" Foreground="#F7F8FB" IsChecked="{Binding Settings.PauseOverlay}" />
                    <TextBlock Text="键盘快捷键默认值：Ctrl+Shift+Left / Right / Down / Up" Margin="0,22,0,0" Foreground="#AAB3C2" TextWrapping="Wrap" />
                    <Button Content="保存设置" Margin="0,24,0,0" Width="110" Height="34" HorizontalAlignment="Left" Click="Save_Click" />
                </StackPanel>
            </Border>
        </Grid>

        <TextBlock Grid.Row="2" Text="{Binding StatusText}" Foreground="#AAB3C2" />
    </Grid>
</Window>
```

- [ ] **Step 3: Wire main window**

Replace `src/AppleMusicOverlay/MainWindow.xaml.cs`:

```csharp
using System.Windows;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;
using AppleMusicOverlay.ViewModels;
using AppleMusicOverlay.Views;

namespace AppleMusicOverlay;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IMediaSessionService _mediaService;
    private readonly TrackMonitor _trackMonitor;
    private readonly OverlayWindow _overlayWindow;
    private readonly GlobalHotkeyService _hotkeyService;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(new OverlaySettingsService());
        _mediaService = new SmtcMediaSessionService();
        _trackMonitor = new TrackMonitor(_mediaService);
        _overlayWindow = new OverlayWindow();
        _overlayWindow.ApplySettings(_viewModel.Settings);
        _hotkeyService = new GlobalHotkeyService(this);
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        _trackMonitor.TrackRead += (_, track) => Dispatcher.Invoke(() => _viewModel.ApplyTrack(track));
        _trackMonitor.TrackChanged += async (_, track) => await Dispatcher.InvokeAsync(() => _overlayWindow.ShowTrackAsync(track));
        _hotkeyService.ActionRequested += HotkeyService_ActionRequested;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RegisterHotkeys();
        _trackMonitor.Start();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await _trackMonitor.PollOnceAsync();
    }

    private async void TestOverlay_Click(object sender, RoutedEventArgs e)
    {
        await ShowTestOverlayAsync();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Save();
        _overlayWindow.ApplySettings(_viewModel.Settings);
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        _hotkeyService.Clear();
        _hotkeyService.Register(AppAction.PreviousTrack, _viewModel.Settings.KeyboardPrevious);
        _hotkeyService.Register(AppAction.NextTrack, _viewModel.Settings.KeyboardNext);
        _hotkeyService.Register(AppAction.TogglePlayPause, _viewModel.Settings.KeyboardToggle);
        _hotkeyService.Register(AppAction.ShowTestOverlay, _viewModel.Settings.KeyboardTestOverlay);
    }

    private async void HotkeyService_ActionRequested(object? sender, AppAction action)
    {
        switch (action)
        {
            case AppAction.PreviousTrack:
                await _mediaService.PreviousAsync();
                break;
            case AppAction.NextTrack:
                await _mediaService.NextAsync();
                break;
            case AppAction.TogglePlayPause:
                await _mediaService.TogglePlayPauseAsync();
                break;
            case AppAction.ShowTestOverlay:
                await ShowTestOverlayAsync();
                break;
        }
    }

    private async Task ShowTestOverlayAsync()
    {
        var track = new TrackInfo("Sapphire Night", "Apple Music", null, "Preview", TimeSpan.FromMinutes(3), true);
        _overlayWindow.ApplySettings(_viewModel.Settings);
        await _overlayWindow.ShowTrackAsync(track);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _trackMonitor.Dispose();
        _hotkeyService.Dispose();
        _overlayWindow.Close();
    }
}
```

- [ ] **Step 4: Build app**

Run:

```powershell
dotnet build AppleMusicOverlay.sln
```

Expected: PASS.

- [ ] **Step 5: Commit app wiring**

Run:

```powershell
git add src/AppleMusicOverlay/App.xaml src/AppleMusicOverlay/App.xaml.cs src/AppleMusicOverlay/MainWindow.xaml src/AppleMusicOverlay/MainWindow.xaml.cs src/AppleMusicOverlay/ViewModels/MainViewModel.cs
git commit -m "feat: wire phase one overlay app"
```

## Task 7: Tray Residency

**Files:**
- Create: `src/AppleMusicOverlay/Services/TrayIconService.cs`
- Modify: `src/AppleMusicOverlay/MainWindow.xaml.cs`

- [ ] **Step 1: Add tray icon service**

Create `src/AppleMusicOverlay/Services/TrayIconService.cs`:

```csharp
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace AppleMusicOverlay.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Window _owner;
    private readonly Action _testOverlay;
    private readonly Forms.NotifyIcon _notifyIcon;

    public TrayIconService(Window owner, Action testOverlay)
    {
        _owner = owner;
        _testOverlay = testOverlay;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Apple Music Overlay",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => ShowOwner();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => ShowOwner());
        menu.Items.Add("测试显示", null, (_, _) => _owner.Dispatcher.Invoke(_testOverlay));
        menu.Items.Add("退出", null, (_, _) => _owner.Dispatcher.Invoke(() => Application.Current.Shutdown()));
        return menu;
    }

    private void ShowOwner()
    {
        _owner.Show();
        _owner.WindowState = WindowState.Normal;
        _owner.Activate();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
```

- [ ] **Step 2: Wire tray into main window**

Modify `src/AppleMusicOverlay/MainWindow.xaml.cs`:

```csharp
private readonly TrayIconService _trayIconService;
```

Initialize it after `_hotkeyService`:

```csharp
_trayIconService = new TrayIconService(this, () => _ = ShowTestOverlayAsync());
```

Add this override:

```csharp
protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
{
    if (_viewModel.Settings.CloseToTray && Application.Current.ShutdownMode != ShutdownMode.OnExplicitShutdown)
    {
        e.Cancel = true;
        Hide();
        return;
    }

    base.OnClosing(e);
}
```

Dispose it in `MainWindow_Closed`:

```csharp
_trayIconService.Dispose();
```

- [ ] **Step 3: Build app**

Run:

```powershell
dotnet build AppleMusicOverlay.sln
```

Expected: PASS.

- [ ] **Step 4: Commit tray support**

Run:

```powershell
git add src/AppleMusicOverlay/Services/TrayIconService.cs src/AppleMusicOverlay/MainWindow.xaml.cs
git commit -m "feat: add tray residency"
```

## Task 8: Phase 1 Verification and Packaging

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add README**

Create `README.md`:

```markdown
# Apple Music Overlay

Free, open-source, local-only Windows overlay for Apple Music PWA.

## Phase 1

- Reads current track metadata and cover art from Windows SMTC.
- Shows a minimal cover overlay when the song changes.
- Supports keyboard shortcuts for previous, next, play/pause, and test overlay.
- Runs locally with a tray icon.
- Does not use game injection, DirectX hooks, driver overlays, or process patching.

## Requirements

- Windows 10 1809 or newer / Windows 11.
- .NET 8 Desktop Runtime for running published framework-dependent builds.
- Edge Apple Music PWA or another player that exposes Windows SMTC metadata.

## Development

```powershell
dotnet build AppleMusicOverlay.sln
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj
dotnet run --project src/AppleMusicOverlay/AppleMusicOverlay.csproj
```

## Publish

```powershell
dotnet publish src/AppleMusicOverlay/AppleMusicOverlay.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```
```

- [ ] **Step 2: Run automated verification**

Run:

```powershell
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj
dotnet build AppleMusicOverlay.sln -c Release
```

Expected: tests PASS and release build PASS.

- [ ] **Step 3: Manual verification**

Run:

```powershell
dotnet run --project src/AppleMusicOverlay/AppleMusicOverlay.csproj
```

Manual checks:

- Launch Edge Apple Music PWA and play a track.
- Click `立即刷新`; main window should show title and artist.
- Change track; A-style overlay should appear for the configured duration.
- Press `Ctrl+Shift+Right`; Apple Music should skip next if SMTC accepts the command.
- Press `Ctrl+Shift+Down`; Apple Music should pause or resume if SMTC accepts the command.
- Close the settings window; app should remain in tray.
- Use tray menu `测试显示`; A-style overlay should appear.

- [ ] **Step 4: Publish local package**

Run:

```powershell
dotnet publish src/AppleMusicOverlay/AppleMusicOverlay.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

Expected: `publish/win-x64/AppleMusicOverlay.exe` exists.

- [ ] **Step 5: Commit verification docs**

Run:

```powershell
git add README.md
git commit -m "docs: add phase one usage notes"
```

## Self-Review Notes

Spec coverage:

- SMTC reading: Task 4.
- A-style minimal overlay: Task 5.
- Keyboard shortcuts: Task 3 and Task 6.
- Tray residency: Task 7.
- Local settings: Task 2 and Task 6.
- No injection/hook/driver overlay: stated in architecture, README, and limited implementation choices.
- B/C styles, Xbox, DS5, and favorite action: intentionally outside Phase 1 and require separate plans.

Type consistency:

- `TrackInfo`, `OverlaySettings`, `AppAction`, and `DisplayStyle` are defined before any task uses them.
- `IMediaSessionService` is defined before `TrackMonitor` and `MainWindow` depend on it.
- `GlobalHotkeyService` raises `AppAction`, matching `MainWindow` dispatch.

Verification:

- Pure behavior is covered by xUnit tests.
- WPF overlay and tray behavior require manual verification because they depend on a visible Windows shell.
