<p align="center">
  <img src="docs/assets/musicfloat-banner.png" alt="MusicFloat banner">
</p>

<h1 align="center">MusicFloat</h1>

<p align="center">
  轻量的 Windows 音乐悬浮窗，支持 SMTC、歌曲信息自定义、快捷键和手柄控制。
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: GPL-3.0-only" src="https://img.shields.io/badge/license-GPL--3.0--only-blue"></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4">
</p>

<p align="center">
  <a href="README.md">English</a> | 简体中文
</p>

MusicFloat 是一个本地 Windows 桌面应用，面向能够通过 Windows System Media Transport Controls（SMTC）暴露媒体信息的播放器。它可以显示紧凑的音乐悬浮窗、控制播放，并且不会进入游戏进程：不注入、不使用 DirectX Hook、不使用驱动级覆盖层，也不修改其他进程。

为了兼容现有代码结构，应用的内部项目名和可执行文件名仍保留为 `AppleMusicOverlay`。

## 预览

MusicFloat 提供深色桌面控制面板，包含当前播放、悬浮窗设置、快捷键和常用选项等页面。悬浮窗会在播放器支持时显示当前封面、歌名和歌手。

## 功能

- 通过 Windows SMTC 读取歌名、歌手、播放状态、媒体来源和封面。
- 切歌时或手动触发时显示悬浮窗。
- 支持配置悬浮窗显示方式、显示时长、大小、位置、歌名/歌手显示开关和歌曲信息字体。
- 支持键盘快捷键：上一首、下一首、播放/暂停、显示当前歌曲悬浮窗。
- 支持为相同操作绑定和触发手柄快捷键。
- 当系统存在多个 SMTC 媒体会话时，可以选择抓取源。
- 支持托盘驻留和关闭到托盘。
- 只读取系统已安装字体。MusicFloat 不内置、不下载、不复制、不重新分发 Spotify Mix、Apple SF Pro 或其他专有字体。

## 下载

请在 [GitHub Releases](https://github.com/Adudumax/MusicFloat/releases/latest) 页面下载最新 Windows x64 便携版。

发布 ZIP 是 self-contained 版本，不需要用户额外安装 .NET Desktop Runtime。

## 快速开始

1. 下载 `MusicFloat-v1.0.0-win-x64-portable.zip`。
2. 解压到你自己的文件夹。
3. 运行 `MusicFloat.exe`。
4. 在支持 SMTC 的播放器中开始播放音乐。
5. 在控制面板中刷新来源、测试悬浮窗、调整悬浮窗设置并绑定快捷键。

## 键盘和手柄控制

MusicFloat 支持为以下操作配置键盘和手柄快捷键：

- 上一首
- 下一首
- 播放 / 暂停
- 显示当前歌曲悬浮窗

快捷键可在应用内配置。手柄支持取决于设备是否能通过 Windows 游戏手柄 API 正常暴露。

## 从源码构建

要求：

- Windows 10 1809 或更新版本，或 Windows 11
- .NET 8 SDK

构建和测试：

```powershell
dotnet restore AppleMusicOverlay.sln
dotnet build AppleMusicOverlay.sln
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj
```

从源码运行：

```powershell
dotnet run --project src/AppleMusicOverlay/AppleMusicOverlay.csproj
```

创建 Windows x64 self-contained 便携版：

```powershell
dotnet publish src/AppleMusicOverlay/AppleMusicOverlay.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishTrimmed=false
```

## 已知限制

- MusicFloat 依赖 Windows SMTC。如果 Windows 没有为某个播放器暴露媒体会话，MusicFloat 就无法读取或控制它。
- 媒体信息质量取决于播放器。有些播放器可能提供缺失、延迟或较泛化的歌名、歌手和封面。
- 悬浮窗是普通 Windows 桌面窗口，不会注入游戏，在独占全屏模式下可能不可见。
- 可选歌曲信息字体只有在用户系统中已经安装时才会显示。
- 收藏或资料库管理等操作不包含在本版本中，因为 SMTC 不提供这些能力。

## 许可证

MusicFloat 使用 `GPL-3.0-only` 许可证发布。详见 [LICENSE](LICENSE)。
