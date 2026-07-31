using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class CloudMusicPlayingListArtworkFinderTests
{
    [Fact]
    public void FindReturnsExactTitleArtistAndDurationMatch()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                "love yourself",
                ["Troye Sivan"],
                177084,
                "http://p3.music.126.net/cover/current.jpg"),
            CreateTrack(
                "love yourself",
                ["Justin Bieber"],
                233000,
                "http://p3.music.126.net/cover/other.jpg"));

        Uri? result = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            "LOVE YOURSELF",
            "Troye Sivan",
            TimeSpan.FromMilliseconds(177084));

        Assert.Equal("http://p3.music.126.net/cover/current.jpg", result?.AbsoluteUri);
    }

    [Fact]
    public void FindUsesDurationToDisambiguateDifferentVersions()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                "Song",
                ["Artist"],
                180000,
                "http://p3.music.126.net/cover/radio.jpg"),
            CreateTrack(
                "Song",
                ["Artist"],
                240000,
                "http://p3.music.126.net/cover/extended.jpg"));

        Uri? result = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            "Song",
            "Artist",
            TimeSpan.FromSeconds(239));

        Assert.Equal("http://p3.music.126.net/cover/extended.jpg", result?.AbsoluteUri);
    }

    [Fact]
    public void FindReturnsNullWhenDistinctArtworkRemainsAmbiguous()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                "Song",
                ["Artist"],
                180000,
                "http://p3.music.126.net/cover/first.jpg"),
            CreateTrack(
                "Song",
                ["Artist"],
                240000,
                "http://p3.music.126.net/cover/second.jpg"));

        Uri? result = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            "Song",
            "Artist",
            TimeSpan.Zero);

        Assert.Null(result);
    }

    [Fact]
    public void FindMatchesCombinedSmtcArtists()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                "Wait",
                ["Troye Sivan", "Gordi"],
                209533,
                "http://p4.music.126.net/cover/wait.jpg"));

        Uri? result = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            "Wait",
            "Troye Sivan & Gordi",
            TimeSpan.FromMilliseconds(209533));

        Assert.Equal("http://p4.music.126.net/cover/wait.jpg", result?.AbsoluteUri);
    }

    [Fact]
    public void FindRejectsWrongArtistEvenWhenTitleAndDurationMatch()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                "Same Title",
                ["Right Artist"],
                180000,
                "http://p3.music.126.net/cover/right.jpg"));

        Uri? result = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            "Same Title",
            "Wrong Artist",
            TimeSpan.FromMinutes(3));

        Assert.Null(result);
    }

    [Fact]
    public void FindAcceptsIncompleteSmtcArtistWhenTitleAndDurationAreUnique()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                "Collaboration",
                ["Main Artist", "Guest Artist"],
                180000,
                "http://p3.music.126.net/cover/collaboration.jpg"));

        Uri? result = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            "Collaboration",
            "Main Artist",
            TimeSpan.FromMinutes(3));

        Assert.Equal(
            "http://p3.music.126.net/cover/collaboration.jpg",
            result?.AbsoluteUri);
    }

    [Fact]
    public void FindAcceptsUniqueIncompleteSmtcArtistWhenDurationIsMissing()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                1893514633,
                "Sunroof",
                ["Nicky Youre", "hey daisy"],
                163073,
                "http://p4.music.126.net/cover/sunroof.jpg"));

        Uri? artwork = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            "Sunroof",
            "Nicky Youre",
            TimeSpan.Zero);
        long? trackId = CloudMusicPlayingListArtworkFinder.FindTrackId(
            document.RootElement,
            "Sunroof",
            "Nicky Youre",
            TimeSpan.Zero);

        Assert.Equal(
            "http://p4.music.126.net/cover/sunroof.jpg",
            artwork?.AbsoluteUri);
        Assert.Equal(1893514633, trackId);
    }

    [Fact]
    public void FindRejectsIncompleteSmtcArtistWhenTitleAndDurationAreAmbiguous()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                "Collaboration",
                ["Main Artist", "First Guest"],
                180000,
                "http://p3.music.126.net/cover/first.jpg"),
            CreateTrack(
                "Collaboration",
                ["Main Artist", "Second Guest"],
                180000,
                "http://p3.music.126.net/cover/second.jpg"));

        Uri? result = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            "Collaboration",
            "Main Artist",
            TimeSpan.FromMinutes(3));

        Assert.Null(result);
    }

    [Fact]
    public void FindRejectsIncompleteSmtcArtistWithoutDurationWhenTitleIsAmbiguous()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                1,
                "Collaboration",
                ["Main Artist", "First Guest"],
                180000,
                "http://p3.music.126.net/cover/first.jpg"),
            CreateTrack(
                2,
                "Collaboration",
                ["Main Artist", "Second Guest"],
                240000,
                "http://p3.music.126.net/cover/second.jpg"));

        Uri? artwork = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            "Collaboration",
            "Main Artist",
            TimeSpan.Zero);
        long? trackId = CloudMusicPlayingListArtworkFinder.FindTrackId(
            document.RootElement,
            "Collaboration",
            "Main Artist",
            TimeSpan.Zero);

        Assert.Null(artwork);
        Assert.Null(trackId);
    }

    [Fact]
    public void FindRejectsDurationOutsideTightTolerance()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                "Same Title",
                ["Artist"],
                180000,
                "http://p3.music.126.net/cover/original.jpg"));

        Uri? result = CloudMusicPlayingListArtworkFinder.Find(
            document.RootElement,
            "Same Title",
            "Artist",
            TimeSpan.FromMilliseconds(181600));

        Assert.Null(result);
    }

    [Fact]
    public void FindTrackIdUsesTheSameStrictIdentityRulesAsArtwork()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                347230,
                "Someone Like You",
                ["Troye Sivan"],
                206000,
                "http://p3.music.126.net/cover/current.jpg"),
            CreateTrack(
                999999,
                "Someone Like You",
                ["Adele"],
                285000,
                "http://p3.music.126.net/cover/other.jpg"));

        long? result = CloudMusicPlayingListArtworkFinder.FindTrackId(
            document.RootElement,
            "Someone Like You",
            "Troye Sivan",
            TimeSpan.FromSeconds(206));

        Assert.Equal(347230, result);
    }

    [Fact]
    public void FindFollowingArtworkReturnsTheNextQueueEntriesInOrder()
    {
        using JsonDocument document = CreatePlayingList(
            CreateTrack(
                1,
                "Current",
                ["Artist"],
                180000,
                "http://p3.music.126.net/cover/current.jpg"),
            CreateTrack(
                2,
                "Next",
                ["Artist"],
                181000,
                "http://p3.music.126.net/cover/next.jpg"),
            CreateTrack(
                3,
                "Later",
                ["Artist"],
                182000,
                "http://p3.music.126.net/cover/later.jpg"));

        IReadOnlyList<Uri> result =
            CloudMusicPlayingListArtworkFinder.FindFollowingArtwork(
                document.RootElement,
                "Current",
                "Artist",
                TimeSpan.FromMinutes(3),
                count: 2);

        Assert.Equal(
            [
                "http://p3.music.126.net/cover/next.jpg",
                "http://p3.music.126.net/cover/later.jpg"
            ],
            result.Select(uri => uri.AbsoluteUri));
    }

    private static JsonDocument CreatePlayingList(params object[] tracks)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            list = tracks.Select(track => new { track })
        }));
    }

    private static object CreateTrack(
        string name,
        string[] artists,
        long duration,
        string picUrl)
    {
        return CreateTrack(1, name, artists, duration, picUrl);
    }

    private static object CreateTrack(
        long id,
        string name,
        string[] artists,
        long duration,
        string picUrl)
    {
        return new
        {
            id,
            name,
            duration,
            artists = artists.Select(artist => new { name = artist }),
            album = new { picUrl }
        };
    }
}

public sealed class CloudMusicCoverResolverTests
{
    [Fact]
    public async Task LiveCloudMusicCoverReadSucceedsWhenExplicitlyEnabled()
    {
        string? title = Environment.GetEnvironmentVariable(
            "MUSICFLOAT_LIVE_CLOUDMUSIC_COVER_TITLE");
        string? artist = Environment.GetEnvironmentVariable(
            "MUSICFLOAT_LIVE_CLOUDMUSIC_COVER_ARTIST");
        if (string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(artist))
        {
            return;
        }

        var resolver = new CloudMusicCoverResolver();

        Assert.True(await resolver.WarmCacheAsync(
            title,
            artist,
            TimeSpan.Zero));
        Assert.NotNull(resolver.TryGetCachedCover(
            title,
            artist,
            TimeSpan.Zero));
    }

    [Theory]
    [InlineData("cloudmusic.exe")]
    [InlineData(@"C:\Program Files\NetEase\CloudMusic\cloudmusic.exe")]
    public void IsSupportedSourceRecognizesCloudMusic(string sourceAppId)
    {
        var resolver = new CloudMusicCoverResolver();

        Assert.True(resolver.IsSupportedSource(sourceAppId));
        Assert.False(resolver.IsSupportedSource("Spotify.exe"));
    }

    [Fact]
    public async Task WarmCacheDownloadsHttpsArtworkAndCachesHighResolutionImage()
    {
        byte[] downloadedCover = CreateBmp(768, 768);
        var handler = new StubHttpMessageHandler(_ => CreateImageResponse(downloadedCover));
        using var httpClient = new HttpClient(handler);
        string playingListPath = CreateTemporaryPlayingList(
            "love yourself",
            "Troye Sivan",
            177084,
            "http://p3.music.126.net/cover/current.jpg");

        try
        {
            var resolver = new CloudMusicCoverResolver(playingListPath, httpClient);

            bool result = await resolver.WarmCacheAsync(
                "love yourself",
                "Troye Sivan",
                TimeSpan.FromMilliseconds(177084));

            Assert.True(result);
            Assert.Equal(downloadedCover, resolver.TryGetCachedCover(
                "love yourself",
                "Troye Sivan",
                TimeSpan.FromMilliseconds(177084)));
            Assert.Equal("https", handler.LastRequestUri?.Scheme);
            Assert.Equal("?param=768y768", handler.LastRequestUri?.Query);
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            File.Delete(playingListPath);
        }
    }

    [Fact]
    public async Task WarmCacheFallsBackToExactOfficialHttpArtworkWhenHttpsVariantIsMissing()
    {
        byte[] originalCover = CreateBmp(768, 768);
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri?.Scheme == Uri.UriSchemeHttp
                ? CreateImageResponse(originalCover)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);
        const string originalArtworkUrl =
            "http://p4.music.126.net/BMGKMRTGJzwmkvyt39h3aw==/109951168862781931.jpg";
        string playingListPath = CreateTemporaryPlayingList(
            "Someone I Could Love",
            "Charlotte Cardin",
            185245,
            originalArtworkUrl);

        try
        {
            var resolver = new CloudMusicCoverResolver(playingListPath, httpClient);

            bool result = await resolver.WarmCacheAsync(
                "Someone I Could Love",
                "Charlotte Cardin",
                TimeSpan.FromMilliseconds(185245));

            Assert.True(result);
            Assert.Equal(originalCover, resolver.TryGetCachedCover(
                "Someone I Could Love",
                "Charlotte Cardin",
                TimeSpan.FromMilliseconds(185245)));
            Assert.True(handler.RequestUris.Count >= 2);
            Assert.All(
                handler.RequestUris[..^1],
                requestUri =>
                {
                    Assert.Equal(Uri.UriSchemeHttps, requestUri.Scheme);
                    Assert.Equal("?param=768y768", requestUri.Query);
                });
            Assert.Equal(new Uri(originalArtworkUrl), handler.RequestUris[^1]);
            Assert.Empty(handler.RequestUris[^1].Query);
        }
        finally
        {
            File.Delete(playingListPath);
        }
    }

    [Fact]
    public async Task WarmCacheRejectsDownloadedImageBelowHighResolutionThreshold()
    {
        byte[] downloadedCover = CreateBmp(399, 399);
        var handler = new StubHttpMessageHandler(_ => CreateImageResponse(downloadedCover));
        using var httpClient = new HttpClient(handler);
        string playingListPath = CreateTemporaryPlayingList(
            "Song",
            "Artist",
            180000,
            "http://p3.music.126.net/cover/song.jpg");

        try
        {
            var resolver = new CloudMusicCoverResolver(playingListPath, httpClient);

            bool result = await resolver.WarmCacheAsync(
                "Song",
                "Artist",
                TimeSpan.FromMinutes(3));
            bool immediateRetry = await resolver.WarmCacheAsync(
                "Song",
                "Artist",
                TimeSpan.FromMinutes(3));

            Assert.False(result);
            Assert.False(immediateRetry);
            Assert.Null(resolver.TryGetCachedCover(
                "Song",
                "Artist",
                TimeSpan.FromMinutes(3)));
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            File.Delete(playingListPath);
        }
    }

    [Fact]
    public async Task WarmCacheAcceptsOfficial430PixelLegacyArtwork()
    {
        byte[] downloadedCover = CreateBmp(430, 430);
        var handler = new StubHttpMessageHandler(_ => CreateImageResponse(downloadedCover));
        using var httpClient = new HttpClient(handler);
        string playingListPath = CreateTemporaryPlayingList(
            "My jealousy (Original ver.)",
            "DJMAX",
            152624,
            "http://p3.music.126.net/cover/my-jealousy.jpg");

        try
        {
            var resolver = new CloudMusicCoverResolver(playingListPath, httpClient);

            Assert.True(await resolver.WarmCacheAsync(
                "My jealousy (Original ver.)",
                "DJMAX",
                TimeSpan.Zero));
            Assert.Equal(downloadedCover, resolver.TryGetCachedCover(
                "My jealousy (Original ver.)",
                "DJMAX",
                TimeSpan.Zero));
        }
        finally
        {
            File.Delete(playingListPath);
        }
    }

    [Fact]
    public async Task WarmCacheAcceptsOfficial512PixelArtwork()
    {
        byte[] downloadedCover = CreateBmp(512, 512);
        var handler = new StubHttpMessageHandler(_ => CreateImageResponse(downloadedCover));
        using var httpClient = new HttpClient(handler);
        string playingListPath = CreateTemporaryPlayingList(
            "Song",
            "Artist",
            180000,
            "http://p3.music.126.net/cover/song.jpg");

        try
        {
            var resolver = new CloudMusicCoverResolver(playingListPath, httpClient);

            Assert.True(await resolver.WarmCacheAsync(
                "Song",
                "Artist",
                TimeSpan.FromMinutes(3)));
            Assert.Equal(downloadedCover, resolver.TryGetCachedCover(
                "Song",
                "Artist",
                TimeSpan.FromMinutes(3)));
        }
        finally
        {
            File.Delete(playingListPath);
        }
    }

    [Fact]
    public async Task CachedCoverIsResolvedFromTheCurrentPlayingListArtwork()
    {
        byte[] downloadedCover = CreateBmp(768, 768);
        var handler = new StubHttpMessageHandler(_ => CreateImageResponse(downloadedCover));
        using var httpClient = new HttpClient(handler);
        string playingListPath = CreateTemporaryPlayingList(
            "Song",
            "Artist",
            180000,
            "http://p3.music.126.net/cover/song.jpg");

        try
        {
            var resolver = new CloudMusicCoverResolver(playingListPath, httpClient);

            Assert.True(await resolver.WarmCacheAsync(
                "Song",
                "Artist",
                TimeSpan.FromMinutes(3)));
            Assert.Equal(downloadedCover, resolver.TryGetCachedCover(
                "Song",
                "Artist",
                TimeSpan.FromMinutes(3)));
        }
        finally
        {
            File.Delete(playingListPath);
        }
    }

    [Fact]
    public async Task CachedCoverIsNotReusedWhenPlayingListPointsToDifferentArtwork()
    {
        byte[] downloadedCover = CreateBmp(768, 768);
        var handler = new StubHttpMessageHandler(_ => CreateImageResponse(downloadedCover));
        using var httpClient = new HttpClient(handler);
        string playingListPath = CreateTemporaryPlayingList(
            "Song",
            "Artist",
            180000,
            "http://p3.music.126.net/cover/song.jpg");

        try
        {
            var resolver = new CloudMusicCoverResolver(playingListPath, httpClient);

            Assert.True(await resolver.WarmCacheAsync(
                "Song",
                "Artist",
                TimeSpan.FromMinutes(3)));
            WritePlayingList(
                playingListPath,
                "Song",
                "Artist",
                180000,
                "http://p3.music.126.net/cover/different.jpg");

            Assert.Null(resolver.TryGetCachedCover(
                "Song",
                "Artist",
                TimeSpan.FromMinutes(3)));
        }
        finally
        {
            File.Delete(playingListPath);
        }
    }

    [Fact]
    public async Task WarmCacheRejectsArtworkOutsideOfficialCdn()
    {
        var handler = new StubHttpMessageHandler(_ => CreateImageResponse(CreateBmp(320, 320)));
        using var httpClient = new HttpClient(handler);
        string playingListPath = CreateTemporaryPlayingList(
            "Song",
            "Artist",
            180000,
            "https://example.com/untrusted.jpg");

        try
        {
            var resolver = new CloudMusicCoverResolver(playingListPath, httpClient);

            bool result = await resolver.WarmCacheAsync(
                "Song",
                "Artist",
                TimeSpan.FromMinutes(3));

            Assert.False(result);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            File.Delete(playingListPath);
        }
    }

    [Fact]
    public void CoverImageQualityRequiresBothDimensionsToIncrease()
    {
        Assert.True(CoverImageQuality.IsHigherResolution(CreateBmp(320, 320), CreateBmp(162, 162)));
        Assert.False(CoverImageQuality.IsHigherResolution(CreateBmp(162, 320), CreateBmp(162, 162)));
        Assert.False(CoverImageQuality.IsHigherResolution([1, 2, 3], CreateBmp(162, 162)));
        Assert.True(CoverImageQuality.MeetsMinimumResolution(CreateBmp(384, 384), 384));
        Assert.False(CoverImageQuality.MeetsMinimumResolution(CreateBmp(383, 768), 384));
    }

    private static string CreateTemporaryPlayingList(
        string title,
        string artist,
        long duration,
        string picUrl)
    {
        string path = Path.GetTempFileName();
        WritePlayingList(path, title, artist, duration, picUrl);
        return path;
    }

    private static void WritePlayingList(
        string path,
        string title,
        string artist,
        long duration,
        string picUrl)
    {
        string json = JsonSerializer.Serialize(new
        {
            list = new[]
            {
                new
                {
                    track = new
                    {
                        name = title,
                        duration,
                        artists = new[] { new { name = artist } },
                        album = new { picUrl }
                    }
                }
            }
        });
        File.WriteAllText(path, json);
    }

    private static HttpResponseMessage CreateImageResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/bmp");
        return response;
    }

    private static byte[] CreateBmp(int width, int height)
    {
        int rowStride = ((width * 3) + 3) & ~3;
        int pixelBytes = checked(rowStride * height);
        byte[] bytes = new byte[54 + pixelBytes];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2, 4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28, 2), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34, 4), pixelBytes);
        return bytes;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            if (request.RequestUri != null)
            {
                RequestUris.Add(request.RequestUri);
            }

            return Task.FromResult(createResponse(request));
        }
    }
}
