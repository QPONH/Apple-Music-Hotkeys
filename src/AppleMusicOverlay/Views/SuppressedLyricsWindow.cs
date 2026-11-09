using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Views;

/// <summary>
/// Lists the tracks whose lyric sources were all rejected ("歌词不对"), with one restore button per
/// track. Kept in its own window so the settings page stays readable once many tracks are marked.
/// </summary>
public sealed class SuppressedLyricsWindow : Window
{
    private readonly LyricsService _lyricsService;
    private readonly StackPanel _list = new() { Margin = new Thickness(0) };

    /// <summary>Raised when the mark set changed, so the opener can refresh its counter.</summary>
    public event Action? Changed;

    public SuppressedLyricsWindow(LyricsService lyricsService)
    {
        _lyricsService = lyricsService;

        Title = "不再显示歌词的歌";
        Width = 620;
        Height = 520;
        MinWidth = 460;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x22));
        Foreground = Brushes.WhiteSmoke;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Content = BuildContent();
        Refresh();
    }

    private FrameworkElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "不再显示歌词的歌",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        header.Children.Add(new TextBlock
        {
            Text = "下面这些歌的所有歌词来源都被排除了。点「恢复」让某一首重新参与查找。",
            Margin = new Thickness(0, 6, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC))
        });
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var restoreAll = new Button
        {
            Content = "全部恢复",
            Width = 120,
            Height = 34,
            Style = TryFindStyle("SecondaryButtonStyle")
        };
        restoreAll.Click += (_, _) =>
        {
            _lyricsService.ClearBlocked();
            Refresh();
            Changed?.Invoke();
        };
        footer.Children.Add(restoreAll);

        var close = new Button
        {
            Content = "关闭",
            Width = 100,
            Height = 34,
            Margin = new Thickness(10, 0, 0, 0),
            Style = TryFindStyle("SecondaryButtonStyle")
        };
        close.Click += (_, _) => Close();
        footer.Children.Add(close);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private Style? TryFindStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style;

    private void Refresh()
    {
        _list.Children.Clear();
        IReadOnlyDictionary<string, IReadOnlyList<string>> map = _lyricsService.GetRejectedMap();

        if (map.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "（暂无）",
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF))
            });
            return;
        }

        foreach ((string key, IReadOnlyList<string> sources) in map.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string[] parts = key.Split('\n');
            string title = parts.Length > 0 ? parts[0] : key;
            string artist = parts.Length > 1 ? parts[1] : string.Empty;
            bool exhausted = sources.Count >= LyricsService.AllProviderNames.Length;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Margin = new Thickness(0, 2, 12, 0) };
            text.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title} — {artist}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.WhiteSmoke
            });
            text.Children.Add(new TextBlock
            {
                Text = exhausted
                    ? "全部来源已排除"
                    : $"已排除：{string.Join("、", sources)}",
                Margin = new Thickness(0, 2, 0, 0),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF))
            });
            row.Children.Add(text);

            var restore = new Button
            {
                Content = "恢复",
                Width = 90,
                Height = 32,
                Style = TryFindStyle("SecondaryButtonStyle")
            };
            string capturedKey = key;
            restore.Click += (_, _) =>
            {
                _lyricsService.UnblockTrack(capturedKey);
                Refresh();
                Changed?.Invoke();
            };
            Grid.SetColumn(restore, 1);
            restore.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(restore);

            _list.Children.Add(new Border
            {
                Style = TryFindStyle("CardStyle"),
                Margin = new Thickness(0, 0, 0, 10),
                Child = row
            });
        }
    }
}
