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
    private readonly IMediaSessionSourceService _sourceService;
    private readonly TrackMonitor _trackMonitor;
    private readonly OverlayWindow _overlayWindow;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly TrayIconService _trayIconService;
    private bool _isExiting;
    private bool _isRefreshingSources;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(new OverlaySettingsService());
        var mediaService = new SmtcMediaSessionService();
        _mediaService = mediaService;
        _sourceService = mediaService;
        ApplyPreferredSource();
        _trackMonitor = new TrackMonitor(_mediaService);
        _overlayWindow = new OverlayWindow();
        _overlayWindow.ApplySettings(_viewModel.Settings);
        _hotkeyService = new GlobalHotkeyService(this);
        _trayIconService = new TrayIconService(this, () => _ = ShowTestOverlayAsync(), ExitApplication);
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        _trackMonitor.TrackRead += (_, track) => Dispatcher.Invoke(() => _viewModel.ApplyTrack(track));
        _trackMonitor.TrackChanged += (_, track) => Dispatcher.Invoke(() => _ = _overlayWindow.ShowTrackAsync(track));
        _hotkeyService.ActionRequested += HotkeyService_ActionRequested;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting && _viewModel.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _viewModel.SetStatus("已最小化到托盘");
            return;
        }

        base.OnClosing(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshSourcesAsync();
        RegisterHotkeys();
        _trackMonitor.Start();
        await _trackMonitor.PollOnceAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSourcesAsync();
        await _trackMonitor.PollOnceAsync();
    }

    private async void RefreshSources_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSourcesAsync();
    }

    private async void CaptureSourceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isRefreshingSources)
        {
            return;
        }

        ApplyPreferredSource();
        _viewModel.Save();
        await _trackMonitor.PollOnceAsync();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.PreviousAsync();
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.TogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.NextAsync();
    }

    private async void TestOverlay_Click(object sender, RoutedEventArgs e)
    {
        await ShowTestOverlayAsync();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Save();
        ApplyPreferredSource();
        _overlayWindow.ApplySettings(_viewModel.Settings);
        RegisterHotkeys();
    }

    private void SettingsControl_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_overlayWindow == null)
        {
            return;
        }

        _overlayWindow.ApplySettings(_viewModel.Settings);
    }

    private void RegisterHotkeys()
    {
        _hotkeyService.Clear();
        bool previous = _hotkeyService.Register(AppAction.PreviousTrack, _viewModel.Settings.KeyboardPrevious);
        bool next = _hotkeyService.Register(AppAction.NextTrack, _viewModel.Settings.KeyboardNext);
        bool toggle = _hotkeyService.Register(AppAction.TogglePlayPause, _viewModel.Settings.KeyboardToggle);
        bool test = _hotkeyService.Register(AppAction.ShowTestOverlay, _viewModel.Settings.KeyboardTestOverlay);

        _viewModel.SetStatus(previous && next && toggle && test ? "快捷键已启用" : "部分快捷键未注册");
    }

    private async Task RefreshSourcesAsync()
    {
        try
        {
            _isRefreshingSources = true;
            IReadOnlyList<MediaSessionCandidate> sessions = await _sourceService.ListSessionsAsync();
            _viewModel.ReplaceCaptureSources(sessions);
            ApplyPreferredSource();
            _viewModel.SetStatus(sessions.Count == 0 ? "未检测到媒体源" : $"检测到 {sessions.Count} 个媒体源");
        }
        catch
        {
            _viewModel.SetStatus("媒体源刷新失败");
        }
        finally
        {
            _isRefreshingSources = false;
        }
    }

    private void ApplyPreferredSource()
    {
        _sourceService.PreferredSourceAppUserModelId = _viewModel.Settings.CaptureSourceAppUserModelId;
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
        _overlayWindow.ApplySettings(_viewModel.Settings);
        var track = new TrackInfo("Sapphire Night", "Apple Music", null, "Preview", TimeSpan.FromMinutes(3), true);
        await _overlayWindow.ShowTrackAsync(track);
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _trackMonitor.Dispose();
        _hotkeyService.Dispose();
        _trayIconService.Dispose();
        _overlayWindow.Close();
    }
}
