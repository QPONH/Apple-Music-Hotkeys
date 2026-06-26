using System.Windows.Media;

namespace AppleMusicOverlay.Services;

public static class OverlayTrackFontIds
{
    public const string Default = "default";
    public const string SpotifyMix = "spotify-mix";
    public const string SfPro = "sf-pro";

    public static string NormalizeKnownId(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            SpotifyMix => SpotifyMix,
            SfPro => SfPro,
            _ => Default
        };
    }
}

public sealed record OverlayTrackFontOption(string Id, string DisplayResourceKey)
{
    public string DisplayName => LocalizationService.Current.Text(DisplayResourceKey);
}

public sealed record ResolvedOverlayTrackFont(string TitleFontFamily, string ArtistFontFamily);

public sealed class OverlayTrackFontAvailability
{
    private static readonly string[] SpotifyTitlePriority =
    [
        "Spotify Mix Display",
        "SpotifyMixDisplay",
        "SpotifyMix Display",
        "Spotify Mix",
        "SpotifyMix",
        "Spotify Mix UI",
        "SpotifyMixUI",
        "SpotifyMix UI",
        "Spotify Mix Text",
        "SpotifyMixText",
        "SpotifyMix Text"
    ];

    private static readonly string[] SpotifyArtistPriority =
    [
        "Spotify Mix Text",
        "SpotifyMixText",
        "SpotifyMix Text",
        "Spotify Mix UI",
        "SpotifyMixUI",
        "SpotifyMix UI",
        "Spotify Mix",
        "SpotifyMix",
        "Spotify Mix Display",
        "SpotifyMixDisplay",
        "SpotifyMix Display"
    ];

    private static readonly string[] SfProTitlePriority =
    [
        "SF Pro Display",
        "SF Pro",
        "SF Pro Text"
    ];

    private static readonly string[] SfProArtistPriority =
    [
        "SF Pro Text",
        "SF Pro",
        "SF Pro Display"
    ];

    private readonly IReadOnlyDictionary<string, string> _installedFamilies;

    public OverlayTrackFontAvailability(IReadOnlyDictionary<string, string> installedFamilies)
    {
        _installedFamilies = installedFamilies;
    }

    public bool IsAvailable(string? id)
    {
        return OverlayTrackFontIds.NormalizeKnownId(id) switch
        {
            OverlayTrackFontIds.Default => true,
            OverlayTrackFontIds.SpotifyMix => ResolveByPriority(SpotifyTitlePriority.Concat(SpotifyArtistPriority)) != string.Empty,
            OverlayTrackFontIds.SfPro => ResolveByPriority(SfProTitlePriority.Concat(SfProArtistPriority)) != string.Empty,
            _ => false
        };
    }

    public string NormalizeSelection(string? id)
    {
        string normalized = OverlayTrackFontIds.NormalizeKnownId(id);
        return IsAvailable(normalized) ? normalized : OverlayTrackFontIds.Default;
    }

    public IReadOnlyList<OverlayTrackFontOption> CreateOptions()
    {
        var options = new List<OverlayTrackFontOption>
        {
            new(OverlayTrackFontIds.Default, "OverlayTrackFontDefault")
        };

        if (IsAvailable(OverlayTrackFontIds.SpotifyMix))
        {
            options.Add(new OverlayTrackFontOption(OverlayTrackFontIds.SpotifyMix, "OverlayTrackFontSpotifyMix"));
        }

        if (IsAvailable(OverlayTrackFontIds.SfPro))
        {
            options.Add(new OverlayTrackFontOption(OverlayTrackFontIds.SfPro, "OverlayTrackFontSfPro"));
        }

        return options;
    }

    public ResolvedOverlayTrackFont Resolve(string? id)
    {
        return NormalizeSelection(id) switch
        {
            OverlayTrackFontIds.SpotifyMix => new(
                ResolveByPriority(SpotifyTitlePriority),
                ResolveByPriority(SpotifyArtistPriority)),
            OverlayTrackFontIds.SfPro => new(
                ResolveByPriority(SfProTitlePriority),
                ResolveByPriority(SfProArtistPriority)),
            _ => new ResolvedOverlayTrackFont(string.Empty, string.Empty)
        };
    }

    public string ResolveFontFamilyList(string? id, bool isTitle)
    {
        ResolvedOverlayTrackFont resolved = Resolve(id);
        string primary = isTitle ? resolved.TitleFontFamily : resolved.ArtistFontFamily;
        if (string.IsNullOrWhiteSpace(primary))
        {
            return string.Empty;
        }

        return $"{primary}, Microsoft YaHei UI, Segoe UI, Segoe UI Symbol, Segoe UI Emoji";
    }

    private string ResolveByPriority(IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            if (_installedFamilies.TryGetValue(NormalizeName(candidate), out string? actualName))
            {
                return actualName;
            }
        }

        return string.Empty;
    }

    internal static string NormalizeName(string value)
    {
        return value.Trim().ToUpperInvariant();
    }
}

public sealed class InstalledFontService
{
    public OverlayTrackFontAvailability Detect()
    {
        var names = new List<string>();
        foreach (FontFamily family in Fonts.SystemFontFamilies)
        {
            names.Add(family.Source);
            foreach (string localizedName in family.FamilyNames.Values)
            {
                names.Add(localizedName);
            }
        }

        return DetectForTesting(names);
    }

    public static OverlayTrackFontAvailability DetectForTesting(IEnumerable<string> fontFamilyNames)
    {
        var installedFamilies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string fontFamilyName in fontFamilyNames)
        {
            if (string.IsNullOrWhiteSpace(fontFamilyName))
            {
                continue;
            }

            string actualName = fontFamilyName.Trim();
            installedFamilies.TryAdd(OverlayTrackFontAvailability.NormalizeName(actualName), actualName);
        }

        return new OverlayTrackFontAvailability(installedFamilies);
    }
}
