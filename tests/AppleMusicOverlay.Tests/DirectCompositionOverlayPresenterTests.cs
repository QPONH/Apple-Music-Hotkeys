namespace AppleMusicOverlay.Tests;

public sealed class DirectCompositionOverlayPresenterTests
{
    [Fact]
    public void PresenterReusesLayeredWindowRasterizationWithoutRealtimeEffects()
    {
        string code = File.ReadAllText(SourcePath(
            "src",
            "AppleMusicOverlay",
            "Views",
            "DirectCompositionOverlayPresenter.cs"));

        Assert.Contains("DCompositionCreateDevice2", code);
        Assert.Contains("CreateSurfaceFromHwnd", code);
        Assert.Contains("DWMWA_CLOAK", code);
        Assert.Contains("WS_EX_NOREDIRECTIONBITMAP", code);
        Assert.Contains("_sourceSurface", code);
        Assert.DoesNotContain("DropShadowEffect", code);
        Assert.DoesNotContain("BlurEffect", code);
        Assert.DoesNotContain("CompositionTarget.Rendering", code);
        Assert.DoesNotContain("Task.Delay(16", code);
    }

    [Fact]
    public void PresenterUsesOneReusableCompositionObjectGraph()
    {
        string code = File.ReadAllText(SourcePath(
            "src",
            "AppleMusicOverlay",
            "Views",
            "DirectCompositionOverlayPresenter.cs"));

        Assert.Contains("private nint _device", code);
        Assert.Contains("private nint _target", code);
        Assert.Contains("private nint _visual", code);
        Assert.Contains("private nint _scaleTransform", code);
        Assert.Contains("private nint _effectGroup", code);
        Assert.Contains("public static bool TryCreate", code);
        Assert.Contains("public void Dispose()", code);
    }

    [Fact]
    public void PresenterTargetWindowExplicitlyRejectsMouseHitTesting()
    {
        string code = File.ReadAllText(SourcePath(
            "src",
            "AppleMusicOverlay",
            "Views",
            "DirectCompositionOverlayPresenter.cs"));

        Assert.Contains("WM_NCHITTEST", code);
        Assert.Contains("HTTRANSPARENT", code);
        Assert.Contains("TargetWindowProc", code);
        Assert.Contains("SetWindowLongPtr", code);
    }

    [Fact]
    public void PresenterTargetWindowUsesLayeredTransparencyForCrossProcessClickThrough()
    {
        string code = File.ReadAllText(SourcePath(
            "src",
            "AppleMusicOverlay",
            "Views",
            "DirectCompositionOverlayPresenter.cs"));

        string createTarget = ExtractBetween(code, "_targetHwnd = CreateWindowEx(", "if (_targetHwnd == nint.Zero)");
        Assert.Contains("WS_EX_LAYERED", code);
        Assert.Contains("WS_EX_LAYERED", createTarget);
        Assert.Contains("WS_EX_TRANSPARENT", createTarget);
    }

    [Fact]
    public void LayeredOverlayKeepsStableFallbackAndDisablesCompositionAfterFailure()
    {
        string code = File.ReadAllText(SourcePath(
            "src",
            "AppleMusicOverlay",
            "Views",
            "LayeredOverlayWindow.cs"));

        Assert.Contains("DirectCompositionOverlayPresenter? _compositionPresenter", code);
        Assert.Contains("TryEnsureCompositionPresenter", code);
        Assert.Contains("DisableComposition", code);
        Assert.Contains("UpdateLayeredWindow", code);
        Assert.Contains("DwmUncloakSource", code);
    }

    [Fact]
    public void ProjectEnablesOnlyBuiltInUnsafeInteropWithoutRuntimePackage()
    {
        string project = File.ReadAllText(SourcePath(
            "src",
            "AppleMusicOverlay",
            "AppleMusicOverlay.csproj"));

        Assert.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", project);
        Assert.DoesNotContain("Microsoft.Windows.CsWin32", project);
        Assert.DoesNotContain("Vortice", project);
    }

    private static string SourcePath(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, "AppleMusicOverlay.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find start marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find end marker: {endMarker}");
        return source[start..end];
    }
}
