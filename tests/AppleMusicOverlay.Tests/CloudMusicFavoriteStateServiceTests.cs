using System.Text.Json;
using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class CloudMusicFavoriteDatabaseParserTests
{
    [Fact]
    public void FindsNestedSpecialTypeFivePlaylistAndCacheTimestamp()
    {
        const string json = """
            {
              "id": "request-key",
              "cache": "{\"result\":{\"playlist\":{\"id\":\"2113225133\",\"specialType\":5}}}"
            }
            """;

        IReadOnlyList<CloudMusicLikedPlaylistCandidate> result =
            CloudMusicFavoriteDatabaseParser.FindLikedPlaylistCandidates(
                json,
                1785145824385);

        CloudMusicLikedPlaylistCandidate candidate = Assert.Single(result);
        Assert.Equal(2113225133, candidate.PlaylistId);
        Assert.Equal(1785145824385, candidate.CacheTimestamp);
    }

    [Fact]
    public void ParsesStringAndNumericTrackIds()
    {
        const string json = """
            {
              "trackIds": [
                { "id": "42", "v": 1 },
                { "id": 99, "v": 2 }
              ]
            }
            """;

        HashSet<long>? result = CloudMusicFavoriteDatabaseParser.ParseTrackIds(json);

        Assert.NotNull(result);
        Assert.Equal([42, 99], result.Order());
    }

    [Theory]
    [InlineData("{\"url\":\"/eapi/v6/playlist/detail\"}-1785145824385", 1785145824385)]
    [InlineData("invalid", 0)]
    public void ParsesTimestampSuffix(string value, long expected)
    {
        Assert.Equal(expected, CloudMusicFavoriteDatabaseParser.ParseCacheTimestamp(value));
    }
}

public sealed class CloudMusicFavoriteStateServiceTests
{
    [Fact]
    public async Task ReadsFavoriteStateFromPlayingListAndLikedPlaylist()
    {
        string playingListPath = CreatePlayingList(42);
        string databasePath = Path.GetTempFileName();
        var sqlite = new StubSqliteTextQuery(
            requestRows:
            [
                [
                    "{\"url\":\"/eapi/v6/playlist/detail\"}-1785145824385",
                    "{\"playlist\":{\"id\":\"2113225133\",\"specialType\":5}}"
                ]
            ],
            playlistRows:
            [
                ["2113225133", "{\"trackIds\":[{\"id\":\"42\",\"v\":1}]}"]
            ]);

        try
        {
            var service = new CloudMusicFavoriteStateService(
                playingListPath,
                databasePath,
                sqlite);
            TrackInfo track = CreateTrack();

            OverlayFavoriteVisualState result = await service.ReadAsync(
                track,
                CancellationToken.None);

            Assert.Equal(OverlayFavoriteVisualState.Favorite, result);
        }
        finally
        {
            File.Delete(playingListPath);
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ReturnsNotFavoriteForKnownTrackOutsideLikedPlaylist()
    {
        string playingListPath = CreatePlayingList(42);
        string databasePath = Path.GetTempFileName();
        var sqlite = new StubSqliteTextQuery(
            requestRows:
            [
                [
                    "{\"url\":\"/eapi/v6/playlist/detail\"}-1785145824385",
                    "{\"playlist\":{\"id\":2113225133,\"specialType\":5}}"
                ]
            ],
            playlistRows:
            [
                ["2113225133", "{\"trackIds\":[{\"id\":\"99\",\"v\":1}]}"]
            ]);

        try
        {
            var service = new CloudMusicFavoriteStateService(
                playingListPath,
                databasePath,
                sqlite);

            OverlayFavoriteVisualState result = await service.ReadAsync(
                CreateTrack(),
                CancellationToken.None);

            Assert.Equal(OverlayFavoriteVisualState.NotFavorite, result);
        }
        finally
        {
            File.Delete(playingListPath);
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ResolvesMultiArtistTrackWhenSmtcOmitsGuestsAndDuration()
    {
        string playingListPath = CreateCollaborativePlayingList(1893514633);
        string databasePath = Path.GetTempFileName();
        var sqlite = new StubSqliteTextQuery(
            requestRows:
            [
                [
                    "{\"url\":\"/eapi/v6/playlist/detail\"}-1785145824385",
                    "{\"playlist\":{\"id\":2113225133,\"specialType\":5}}"
                ]
            ],
            playlistRows:
            [
                [
                    "2113225133",
                    "{\"trackIds\":[{\"id\":\"1893514633\",\"v\":1}]}"
                ]
            ]);

        try
        {
            var service = new CloudMusicFavoriteStateService(
                playingListPath,
                databasePath,
                sqlite);
            var track = new TrackInfo(
                "Sunroof",
                "Nicky Youre",
                null,
                "cloudmusic.exe",
                TimeSpan.Zero,
                true);

            OverlayFavoriteVisualState result = await service.ReadAsync(
                track,
                CancellationToken.None);

            Assert.Equal(OverlayFavoriteVisualState.Favorite, result);
        }
        finally
        {
            File.Delete(playingListPath);
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ForceRefreshObservesFavoriteChangeWithoutDependingOnShortcutKeys()
    {
        string playingListPath = CreatePlayingList(42);
        string databasePath = Path.GetTempFileName();
        var sqlite = new StubSqliteTextQuery(
            requestRows:
            [
                [
                    "{\"url\":\"/eapi/v6/playlist/detail\"}-1785145824385",
                    "{\"playlist\":{\"id\":2113225133,\"specialType\":5}}"
                ]
            ],
            playlistRows:
            [
                ["2113225133", "{\"trackIds\":[{\"id\":\"99\",\"v\":1}]}"]
            ]);

        try
        {
            var service = new CloudMusicFavoriteStateService(
                playingListPath,
                databasePath,
                sqlite);
            Assert.Equal(
                OverlayFavoriteVisualState.NotFavorite,
                await service.ReadAsync(CreateTrack(), CancellationToken.None));

            sqlite.PlaylistRows =
            [
                ["2113225133", "{\"trackIds\":[{\"id\":\"42\",\"v\":2}]}"]
            ];

            OverlayFavoriteVisualState result = await service.ReadAsync(
                CreateTrack(),
                forceRefresh: true,
                CancellationToken.None);

            Assert.Equal(OverlayFavoriteVisualState.Favorite, result);
        }
        finally
        {
            File.Delete(playingListPath);
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task LiveCloudMusicReadSucceedsWhenExplicitlyEnabled()
    {
        string? title = Environment.GetEnvironmentVariable("MUSICFLOAT_LIVE_CLOUDMUSIC_TITLE");
        string? artist = Environment.GetEnvironmentVariable("MUSICFLOAT_LIVE_CLOUDMUSIC_ARTIST");
        string? expectedText = Environment.GetEnvironmentVariable("MUSICFLOAT_LIVE_CLOUDMUSIC_STATE");
        if (string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(artist) ||
            !Enum.TryParse(expectedText, ignoreCase: true, out OverlayFavoriteVisualState expected))
        {
            return;
        }

        var service = new CloudMusicFavoriteStateService();
        var track = new TrackInfo(
            title,
            artist,
            null,
            "cloudmusic.exe",
            TimeSpan.Zero,
            true);

        OverlayFavoriteVisualState result = await service.ReadAsync(
            track,
            forceRefresh: true,
            CancellationToken.None);

        Assert.Equal(expected, result);
    }

    private static TrackInfo CreateTrack()
    {
        return new TrackInfo(
            "Someone Like You",
            "Troye Sivan",
            null,
            "cloudmusic.exe",
            TimeSpan.FromSeconds(206),
            true);
    }

    private static string CreatePlayingList(long trackId)
    {
        string path = Path.GetTempFileName();
        string json = JsonSerializer.Serialize(new
        {
            list = new[]
            {
                new
                {
                    track = new
                    {
                        id = trackId,
                        name = "Someone Like You",
                        duration = 206000,
                        artists = new[] { new { name = "Troye Sivan" } },
                        album = new { picUrl = "https://p3.music.126.net/cover/current.jpg" }
                    }
                }
            }
        });
        File.WriteAllText(path, json);
        return path;
    }

    private static string CreateCollaborativePlayingList(long trackId)
    {
        string path = Path.GetTempFileName();
        string json = JsonSerializer.Serialize(new
        {
            list = new[]
            {
                new
                {
                    track = new
                    {
                        id = trackId,
                        name = "Sunroof",
                        duration = 163073,
                        artists = new[]
                        {
                            new { name = "Nicky Youre" },
                            new { name = "hey daisy" }
                        },
                        album = new
                        {
                            picUrl =
                                "https://p4.music.126.net/cover/sunroof.jpg"
                        }
                    }
                }
            }
        });
        File.WriteAllText(path, json);
        return path;
    }

    private sealed class StubSqliteTextQuery(
        IReadOnlyList<string?[]> requestRows,
        IReadOnlyList<string?[]> playlistRows) : IReadOnlySqliteTextQuery
    {
        public IReadOnlyList<string?[]> PlaylistRows { get; set; } = playlistRows;

        public IReadOnlyList<string?[]> Query(string databasePath, string sql)
        {
            if (sql.Contains("FROM requestCache", StringComparison.Ordinal))
            {
                return requestRows;
            }

            if (sql.Contains("WHERE id =", StringComparison.Ordinal))
            {
                string? playlistId = PlaylistRows.FirstOrDefault()?[0];
                return playlistId == null
                    ? []
                    : [[PlaylistRows.First()[1]]];
            }

            return PlaylistRows;
        }
    }
}
