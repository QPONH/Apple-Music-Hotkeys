<h1 align="center">MusicFloat</h1>

<p align="center">
  A lightweight Windows music overlay with SMTC support, customizable track information, shortcuts, and gamepad controls.
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: GPL-3.0-only" src="https://img.shields.io/badge/license-GPL--3.0--only-blue"></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4">
</p>

<p align="center">
  English | <a href="README.zh-CN.md">简体中文</a>
</p>

MusicFloat is a local Windows desktop app for players that expose metadata through Windows System Media Transport Controls (SMTC). It shows a compact floating overlay, lets you control playback, and stays out of the game process: no injection, no DirectX hooks, no driver overlay, and no process patching.

The app executable and project are still named `AppleMusicOverlay` internally for compatibility with the existing codebase.

## Preview

MusicFloat provides a dark desktop control panel with pages for current playback, overlay settings, shortcuts, and common options. The floating overlay displays the current cover art, song title, and artist when supported by the active media session.

## Features

- Reads title, artist, playback state, source, and cover art from Windows SMTC.
- Shows a floating overlay when the song changes or when triggered manually.
- Supports configurable overlay visibility, duration, size, position, title/artist visibility, and track information font.
- Supports keyboard shortcuts for previous track, next track, play/pause, and showing the current track overlay.
- Supports gamepad shortcut binding and triggering for the same playback and overlay actions.
- Provides capture source selection when multiple SMTC media sessions are available.
- Runs locally with a tray icon and optional close-to-tray behavior.
- Uses system-installed fonts only. MusicFloat does not bundle, download, copy, or redistribute Spotify Mix, Apple SF Pro, or other proprietary fonts.

## Download

Download the latest portable Windows x64 build from the [GitHub Releases](https://github.com/Adudumax/MusicFloat/releases/latest) page.

The release ZIP is self-contained. You do not need to install the .NET Desktop Runtime separately.

## Quick Start

1. Download `MusicFloat-v1.0.0-win-x64-portable.zip`.
2. Extract the ZIP to a folder you control.
3. Run `MusicFloat.exe`.
4. Start playback in a supported SMTC media player.
5. Use the control panel to refresh sources, test the overlay, configure overlay behavior, and bind shortcuts.

## Keyboard and Gamepad Controls

MusicFloat supports keyboard and gamepad shortcuts for:

- Previous track
- Next track
- Play / pause
- Show current track overlay

Shortcuts are configurable in the app. Gamepad support depends on devices exposed through Windows gamepad APIs.

## Build from Source

Requirements:

- Windows 10 version 1809 or newer, or Windows 11
- .NET 8 SDK

Build and test:

```powershell
dotnet restore AppleMusicOverlay.sln
dotnet build AppleMusicOverlay.sln
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj
```

Run from source:

```powershell
dotnet run --project src/AppleMusicOverlay/AppleMusicOverlay.csproj
```

Create a portable self-contained Windows x64 build:

```powershell
dotnet publish src/AppleMusicOverlay/AppleMusicOverlay.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishTrimmed=false
```

## Known Limitations

- MusicFloat depends on Windows SMTC. If Windows does not expose a media session for a player, MusicFloat cannot read or control it.
- Metadata quality depends on the player. Some players may provide missing, delayed, or generic title/artist/cover information.
- The overlay is a normal Windows desktop overlay. It does not inject into games and may be hidden by exclusive fullscreen modes.
- Optional track information fonts only appear when already installed on the user's system.
- Favorite or library-management actions are not included because SMTC does not expose those operations.

## License

MusicFloat is licensed under `GPL-3.0-only`. See [LICENSE](LICENSE).
