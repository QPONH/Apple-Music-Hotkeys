using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly OverlaySettingsService _settingsService;
    private readonly OverlaySettings _settings;
    private TrackInfo? _currentTrack;
    private string _statusText = "等待播放源";

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(OverlaySettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = settingsService.Load();
        CaptureSources.Add(MediaSessionSourceOption.Automatic);
    }

    public OverlaySettings Settings => _settings;

    public ObservableCollection<MediaSessionSourceOption> CaptureSources { get; } = new();

    public TrackInfo? CurrentTrack
    {
        get => _currentTrack;
        private set
        {
            _currentTrack = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentArtist));
            OnPropertyChanged(nameof(TransportButtonText));
        }
    }

    public string CurrentTitle => CurrentTrack?.Title ?? "未检测到歌曲";

    public string CurrentArtist => CurrentTrack?.Artist ?? "播放音乐后自动同步";

    public string TransportButtonText => CurrentTrack?.IsPlaying == true ? "暂停" : "播放";

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
        StatusText = track == null ? "未检测到正在播放的音乐" : $"已连接：{track.SourceAppId}";
    }

    public void Save()
    {
        _settingsService.Save(_settings);
    }

    public void SetStatus(string statusText)
    {
        StatusText = statusText;
    }

    public void ReplaceCaptureSources(IEnumerable<MediaSessionCandidate> sessions)
    {
        string selected = Settings.CaptureSourceAppUserModelId;
        CaptureSources.Clear();
        CaptureSources.Add(MediaSessionSourceOption.Automatic);

        foreach (MediaSessionCandidate session in sessions.OrderBy(session => session.SourceAppUserModelId))
        {
            CaptureSources.Add(MediaSessionSourceOption.FromCandidate(session));
        }

        if (!string.IsNullOrWhiteSpace(selected) &&
            CaptureSources.All(source => !source.SourceAppUserModelId.Equals(selected, StringComparison.OrdinalIgnoreCase)))
        {
            CaptureSources.Add(new MediaSessionSourceOption(selected, $"{selected}（未检测到）"));
        }

        OnPropertyChanged(nameof(CaptureSources));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record MediaSessionSourceOption(string SourceAppUserModelId, string DisplayName)
{
    public static MediaSessionSourceOption Automatic { get; } = new(string.Empty, "自动选择");

    public static MediaSessionSourceOption FromCandidate(MediaSessionCandidate candidate)
    {
        string title = string.IsNullOrWhiteSpace(candidate.Title) ? "未知媒体" : candidate.Title;
        string source = SimplifySourceName(candidate.SourceAppUserModelId);
        return new MediaSessionSourceOption(candidate.SourceAppUserModelId, $"{source} · {title}");
    }

    private static string SimplifySourceName(string sourceAppUserModelId)
    {
        if (sourceAppUserModelId.Contains("edge", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Edge";
        }

        if (sourceAppUserModelId.Contains("chrome", StringComparison.OrdinalIgnoreCase))
        {
            return "Google Chrome";
        }

        return sourceAppUserModelId;
    }
}
