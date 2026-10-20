MusicFloat v14.26 —— Apple Music 歌词浮窗 + 全局快捷键
（Apple-Music-Hotkeys 修改版）

使用方法
1. 把压缩包完整解压到任意文件夹。
2. 二选一运行（两个都是单文件，任选其一即可）：
   · MusicFloat.exe                —— 体积小，需要先安装 .NET 8 Desktop Runtime x64。
   · MusicFloat-self-contained.exe —— 自带 .NET 运行时，不需要安装任何东西；首次启动稍慢几百毫秒。
3. 启动后出现设置窗口（或已按设置隐藏到托盘）；在设置里勾选“歌词”并点“应用”即可显示歌词浮窗。

本版修复的三个问题
· 有时 Apple Music 明明有歌词却提示找不到：
  网络 / TLS 握手偶发失败或接口限流时，不再把“没找到”永久记住，而是自动重试
  （2s → 4s → 8s → 16s → 30s → … → 3min）；同时对 “Song (feat. X)”“Song - Remastered”
  “A & B” 这类写法生成标题/艺人变体，并按标题 + 艺人 + 时长择优，避免匹配到现场版或翻唱。
· 关闭 Apple Music 后出现“未知来源”的歌词：
  浮窗只认 Apple Music 的 SMTC 会话，不再退回到浏览器 / 直播 / 其他播放器；
  播放器退出后残留的 Closed 会话也会被忽略。没有 Apple Music 会话时显示
  “未识别到正在播放的 Apple Music 歌曲”。
· 歌词浮窗不总在最上方：
  除 Topmost 之外每 0.8 秒自检一次，被其他置顶窗口压住时用 SetWindowPos 重新置顶
  （不抢焦点、不改变位置大小）；开始菜单 / Alt-Tab 等系统界面出现时不抢，浮窗右键菜单打开时暂停。

歌词浮窗操作
· 右键菜单：打开设置 / 重新查找歌词 / 退出 MusicFloat。
· 开启背景后可直接拖动窗口；拖边缘可调整大小（位置尺寸会自动保存）。
· 设置里可调整字号、颜色、透明度、横排/竖排、歌词偏移等。

运行时依赖（仅 MusicFloat.exe 需要）
.NET 8 Desktop Runtime x64：
https://dotnet.microsoft.com/download/dotnet/8.0/runtime
在页面中选择 “.NET Desktop Runtime 8.x - Windows x64”。

---

MusicFloat v14.26 (English)

1. Extract the archive to any folder.
2. Run one of the two single-file builds:
   - MusicFloat.exe                - small, requires the .NET 8 Desktop Runtime x64.
   - MusicFloat-self-contained.exe - bundles the runtime, nothing to install.
3. Open the settings window, enable "Lyrics" and click Apply to show the floating lyrics.

Fixes in this build
- Lyrics are no longer permanently cached as "not found" when a request fails;
  lookups retry with backoff and match title/artist variants by duration.
- The overlay only follows the Apple Music SMTC session, so closing Apple Music
  no longer shows lyrics of browsers or other players.
- The overlay re-asserts HWND_TOPMOST while visible so it stays above other
  always-on-top windows (without stealing focus or covering the Start menu).
