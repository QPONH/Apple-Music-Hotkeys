using System.ComponentModel;
using System.Runtime.CompilerServices;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly OverlaySettingsService _settingsService;
    private readonly OverlaySettings _settings;
    private TrackInfo? _currentTrack;
    private string _statusText = "等待系统媒体会话";

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

    public string CurrentArtist => CurrentTrack?.Artist ?? "打开 Apple Music PWA 后刷新";

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
        StatusText = track == null ? "未读取到系统媒体会话" : $"已连接：{track.SourceAppId}";
    }

    public void Save()
    {
        _settingsService.Save(_settings);
        StatusText = "设置已保存";
    }

    public void SetStatus(string statusText)
    {
        StatusText = statusText;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
