namespace AppleMusicOverlay.Tests;

public sealed class ControlPanelXamlTests
{
    [Fact]
    public void MainWindowUsesThreePageControlPanelShell()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.Contains("TabControl", xaml);
        Assert.Contains("当前播放", xaml);
        Assert.Contains("悬浮窗", xaml);
        Assert.Contains("快捷键", xaml);
    }

    [Fact]
    public void MainWindowAvoidsLargeDecorativeGradients()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.DoesNotContain("BrandGradientBrush", xaml);
        Assert.DoesNotContain("RadialGradientBrush", xaml);
    }

    [Fact]
    public void HeaderRemovesDecorativeIconAndWindowsCopy()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.DoesNotContain("Windows 桌面音乐悬浮工具", xaml);
        Assert.DoesNotContain("Data=\"M 24 8 L 24 24", xaml);
    }

    [Fact]
    public void NavigationAndScrollBarsUseCustomRoundedStyles()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.Contains("RoundedScrollBarStyle", xaml);
        Assert.Contains("StackPanel x:Name=\"HeaderPanel\"", xaml);
        Assert.DoesNotContain("TabPanel x:Name=\"HeaderPanel\"", xaml);
    }

    [Fact]
    public void HotkeyFieldsCaptureKeyPressesInsteadOfFreeTextInput()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.Contains("HotkeyBox_PreviewKeyDown", xaml);
        Assert.Contains("HotkeyBox_PreviewKeyUp", xaml);
        Assert.Contains("按下想要的快捷键", xaml);
        Assert.Contains("IsReadOnly=\"True\"", xaml);
        Assert.Contains("Mode=OneWay", xaml);
    }

    [Fact]
    public void WindowShellUsesTransparentRoundedCornersAndNonStickyCaptionButtons()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.Contains("AllowsTransparency=\"True\"", xaml);
        Assert.Contains("Background=\"Transparent\"", xaml);
        Assert.Contains("CornerRadius=\"20\"", xaml);
        Assert.Contains("Focusable=\"False\"", xaml);
        Assert.Contains("x:Name=\"MaximizeButton\"", xaml);
    }

    [Fact]
    public void HotkeyCopyTellsUserToSaveAfterEditing()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.Contains("修改完成后请点击保存", xaml);
        Assert.DoesNotContain("保存后重新注册", xaml);
    }

    [Fact]
    public void WindowShellAvoidsSelfShadowPaddingThatCreatesHardCornerBlocks()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());
        string code = File.ReadAllText(GetMainWindowCodeBehindPath());

        Assert.Contains("x:Name=\"WindowFrame\"", xaml);
        Assert.DoesNotContain("Effect=\"{StaticResource SoftWindowShadow}\"", xaml);
        Assert.DoesNotContain("Margin=\"22\"", xaml);
        Assert.DoesNotContain("DropShadowEffect", code);
        Assert.DoesNotContain("NormalWindowFrameMargin", code);
        Assert.DoesNotContain("AnimateWindowShadow", code);
        Assert.Contains("WindowFrame.Margin = new Thickness(0)", code);
        Assert.Contains("WindowFrame.Effect = null", code);
        Assert.Contains("ApplyWindowShellState", code);
        Assert.Contains("WindowState == WindowState.Maximized", code);
        Assert.Contains("WindowFrame.CornerRadius = new CornerRadius(0)", code);
    }

    [Fact]
    public void WindowShadowUsesDedicatedSoftShadowWindowWithActiveAndInactiveStates()
    {
        string shadowXaml = File.ReadAllText(GetShadowWindowXamlPath());
        string shadowCode = File.ReadAllText(GetShadowWindowCodeBehindPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string windowStyleServiceCode = File.ReadAllText(GetWindowStyleServicePath());

        Assert.Contains("x:Class=\"AppleMusicOverlay.Views.ShadowWindow\"", shadowXaml);
        Assert.Contains("ShowActivated=\"False\"", shadowXaml);
        Assert.Contains("ShowInTaskbar=\"False\"", shadowXaml);
        Assert.Contains("DropShadowEffect", shadowXaml);
        Assert.Contains("ShadowDepth=\"0\"", shadowXaml);
        Assert.Contains("BlurRadius=\"62\"", shadowXaml);
        Assert.Contains("ShadowPadding", shadowCode);
        Assert.Contains("ApplyActiveState", shadowCode);
        Assert.Contains("SyncWith", shadowCode);
        Assert.Contains("_shadowWindow", mainCode);
        Assert.Contains("SyncShadowWindow", mainCode);
        Assert.Contains("_shadowWindow.ApplyActiveState(true)", mainCode);
        Assert.Contains("_shadowWindow.ApplyActiveState(false)", mainCode);
        Assert.Contains("_shadowWindow.Hide()", mainCode);
        Assert.Contains("ApplyShadowStyles", windowStyleServiceCode);
        Assert.Contains("PlaceShadowBehind", windowStyleServiceCode);
    }

    [Fact]
    public void ShadowSliderAndHotkeyEditingUseStrongerPolishedStates()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());
        string code = File.ReadAllText(GetMainWindowCodeBehindPath());
        string shadowXaml = File.ReadAllText(GetShadowWindowXamlPath());
        string shadowCode = File.ReadAllText(GetShadowWindowCodeBehindPath());

        Assert.Contains("BlurRadius=\"62\"", shadowXaml);
        Assert.Contains("ShadowDepth=\"14\"", shadowXaml);
        Assert.Contains("isActive ? 0.42 : 0.28", shadowCode);
        Assert.Contains("isActive ? 0.28 : 0.17", shadowCode);
        Assert.Contains("Thumb Width=\"24\"", xaml);
        Assert.DoesNotContain("TargetName=\"ThumbRoot\" Property=\"Width\" Value=\"22\"", xaml);
        Assert.DoesNotContain("TargetName=\"ThumbRoot\" Property=\"Height\" Value=\"22\"", xaml);
        Assert.Contains("ApplyPendingHotkeyEdits", code);
        Assert.Contains("ApplyHotkeyEdit(\"KeyboardPrevious\", KeyboardPreviousBox.Text)", code);
        Assert.Contains("box.Text = _pendingHotkeyText", code);
        Assert.Contains("ClearHotkeyCapture()", code);
        Assert.Contains("ShowHotkeyCapturePanel", code);
        Assert.Contains("NormalizeCapturedHotkeyText", code);
    }

    [Fact]
    public void HotkeyEditingExpandsNavigationItemInsteadOfShowingPopup()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());
        string code = File.ReadAllText(GetMainWindowCodeBehindPath());
        string header = ExtractBetween(xaml, "<TabItem.Header>", "</TabItem.Header>");

        Assert.Contains("x:Name=\"HotkeyCapturePanel\"", xaml);
        Assert.Contains("x:Name=\"HotkeyCaptureTitleText\"", xaml);
        Assert.Contains("x:Name=\"HotkeyCaptureStatusText\"", xaml);
        Assert.Contains("x:Name=\"HotkeyCaptureScale\"", xaml);
        Assert.Contains("x:Name=\"HotkeyCaptureTranslate\"", xaml);
        Assert.Contains("x:Name=\"HotkeyCaptureContent\"", header);
        Assert.Contains("<TabItem.Header>", xaml);
        Assert.Contains("MaxHeight=\"0\"", xaml);
        Assert.Contains("ClipToBounds=\"True\"", xaml);
        Assert.Contains("RenderTransformOrigin=\"0.5,0\"", xaml);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
        Assert.DoesNotContain("Panel.ZIndex=\"40\"", xaml);
        Assert.DoesNotContain("GotKeyboardFocus=\"HotkeyBox_GotKeyboardFocus\"", xaml);
        Assert.Contains("ShowHotkeyCapturePanel", code);
        Assert.Contains("HideHotkeyCapturePanel", code);
        Assert.Contains("BeginHotkeyCapturePanelAnimation", code);
        Assert.Contains("bool wasVisible", code);
        Assert.Contains("double startHeight", code);
        Assert.Contains("HotkeyCaptureContent.Measure", code);
        Assert.Contains("HotkeyCaptureStatusText.Text = statusText", code);
        Assert.Contains("HotkeyCapturePanel.Visibility = Visibility.Visible", code);
        Assert.Contains("HotkeyCapturePanel.Visibility = Visibility.Collapsed", code);
        Assert.Contains("PreviewMouseDown += MainWindow_PreviewMouseDown", code);
        Assert.Contains("MainWindow_PreviewMouseDown", code);
        Assert.Contains("IsClickInsideCurrentHotkeyBox", code);
        Assert.Contains("BeginHotkeyCapture(box)", code);
        Assert.DoesNotContain("_viewModel.SetStatus(statusText);", code);
    }

    [Fact]
    public void ActionButtonsDoNotKeepKeyboardFocusHighlightAfterClick()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());
        string secondaryButtonStyle = ExtractBetween(
            xaml,
            "<Style x:Key=\"SecondaryButtonStyle\" TargetType=\"{x:Type Button}\">",
            "<Style x:Key=\"PrimaryButtonStyle\"");

        Assert.Contains("<Setter Property=\"Focusable\" Value=\"False\" />", secondaryButtonStyle);
        Assert.DoesNotContain("IsKeyboardFocused", secondaryButtonStyle);
    }

    [Fact]
    public void HotkeyEditingIgnoresRepeatKeysAndAutoHidesSaveFeedback()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());
        string code = File.ReadAllText(GetMainWindowCodeBehindPath());

        Assert.Equal(4, CountOccurrences(xaml, "IsReadOnly=\"True\"\n                                                     Focusable=\"False\""));
        Assert.Contains("if (e.IsRepeat && _pressedHotkeyKeys.Contains(key))", code);
        Assert.Contains("DispatcherTimer", code);
        Assert.Contains("_hotkeyCaptureAutoHideTimer", code);
        Assert.Contains("autoHideMilliseconds", code);
        Assert.Contains("ShowHotkeyCapturePanel(\"快捷键已保存并生效。\", autoHide: true", code);
        Assert.Contains("ClearHotkeyCapture(hidePanel: false)", code);
    }

    [Fact]
    public void HotkeyEditingUsesSaveTimeValidationAndPerRowDeleteButtons()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());
        string code = File.ReadAllText(GetMainWindowCodeBehindPath());

        Assert.Equal(4, CountOccurrences(xaml, "Click=\"DeleteHotkey_Click\""));
        Assert.Equal(4, CountOccurrences(xaml, "Content=\"删除\""));
        Assert.Contains("DeleteHotkey_Click", code);
        Assert.Contains("HotkeyUnsetText", code);
        Assert.Contains("ValidateHotkeyEdits", code);
        Assert.Contains("FindDuplicateHotkey", code);
        Assert.Contains("快捷键重复", code);
        Assert.DoesNotContain("FindHotkeyConflict(box.Tag as string, hotkeyText)", code);
        Assert.DoesNotContain("isConflict: conflict != null", code);
    }

    [Fact]
    public void OverlayCoverUsesSoftShadowAndReadableArtistText()
    {
        string overlayXaml = File.ReadAllText(GetOverlayWindowXamlPath());
        string titleTextBlock = ExtractBetween(overlayXaml, "<TextBlock x:Name=\"TitleText\"", "</TextBlock>");
        string artistTextBlock = ExtractBetween(overlayXaml, "<TextBlock x:Name=\"ArtistText\"", "</TextBlock>");

        Assert.DoesNotContain("ShadowDepth=\"10\"", overlayXaml);
        Assert.DoesNotContain("Opacity=\"0.38\"", overlayXaml);
        Assert.DoesNotContain("BorderBrush=\"#26FFFFFF\"", overlayXaml);
        Assert.DoesNotContain("BorderBrush=\"#22FFFFFF\"", overlayXaml);
        Assert.DoesNotContain("Background=\"#7A000000\"", overlayXaml);
        Assert.DoesNotContain("BorderBrush=", overlayXaml);
        Assert.DoesNotContain("BorderThickness=", overlayXaml);
        Assert.Contains("Width=\"256\"", overlayXaml);
        Assert.Contains("Height=\"308\"", overlayXaml);
        Assert.DoesNotContain("Grid.RowDefinitions", overlayXaml);
        Assert.Contains("x:Name=\"ShadowHost\"", overlayXaml);
        Assert.Contains("Canvas.Left=\"0\"", overlayXaml);
        Assert.Contains("Canvas.Top=\"0\"", overlayXaml);
        Assert.Contains("x:Name=\"VisualGroup\"", overlayXaml);
        Assert.Contains("x:Name=\"VisualScale\"", overlayXaml);
        Assert.Contains("CenterX=\"128\"", overlayXaml);
        Assert.Contains("CenterY=\"158\"", overlayXaml);
        Assert.Contains("x:Name=\"AmbientShadowCaster\"", overlayXaml);
        Assert.Contains("x:Name=\"KeyShadowCaster\"", overlayXaml);
        Assert.Contains("Canvas.Left=\"42\"", overlayXaml);
        Assert.Contains("Canvas.Top=\"42\"", overlayXaml);
        Assert.Contains("Width=\"172\"", overlayXaml);
        Assert.Contains("Height=\"172\"", overlayXaml);
        Assert.Contains("x:Name=\"AmbientShadowEffect\"", overlayXaml);
        Assert.Contains("x:Name=\"KeyShadowEffect\"", overlayXaml);
        Assert.Contains("x:Name=\"CoverClip\"", overlayXaml);
        Assert.Contains("Canvas.Left=\"40\"", overlayXaml);
        Assert.Contains("Canvas.Top=\"40\"", overlayXaml);
        Assert.Contains("Canvas.Top=\"228\"", overlayXaml);
        Assert.Contains("Canvas.Top=\"249\"", overlayXaml);
        Assert.Contains("Direction=\"270\"", overlayXaml);
        Assert.Contains("IsHitTestVisible=\"False\"", overlayXaml);
        Assert.Contains("x:Name=\"AmbientShadowEffect\"", overlayXaml);
        Assert.Contains("ShadowDepth=\"0\"", overlayXaml);
        Assert.Contains("BlurRadius=\"28\"", overlayXaml);
        Assert.Contains("x:Name=\"KeyShadowEffect\"", overlayXaml);
        Assert.Contains("ShadowDepth=\"8\"", overlayXaml);
        Assert.Contains("BlurRadius=\"32\"", overlayXaml);
        Assert.Contains("Foreground=\"#C8CEDA\"", overlayXaml);
        Assert.Contains("Color=\"#111827\"", overlayXaml);
        Assert.DoesNotContain("BlurRadius=\"8\"", titleTextBlock);
        Assert.DoesNotContain("Opacity=\"0.8\"", titleTextBlock);
        Assert.DoesNotContain("BlurRadius=\"5\"", artistTextBlock);
        Assert.DoesNotContain("Opacity=\"0.92\"", artistTextBlock);
        Assert.Contains("BlurRadius=\"2\"", titleTextBlock);
        Assert.Contains("Opacity=\"0.42\"", titleTextBlock);
        Assert.Contains("BlurRadius=\"2\"", artistTextBlock);
        Assert.Contains("Opacity=\"0.34\"", artistTextBlock);
        Assert.Contains("ShadowCasterSize = CoverSize - (ShadowCasterInset * 2)", File.ReadAllText(GetOverlayWindowCodeBehindPath()));
    }

    [Fact]
    public void OverlayScaleUsesCenteredVisualGroupTransform()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string overlayXaml = File.ReadAllText(GetOverlayWindowXamlPath());
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());
        string settingsCode = File.ReadAllText(GetOverlaySettingsPath());
        string normalizerCode = File.ReadAllText(GetOverlaySettingsNormalizerPath());
        string scaleSlider = ExtractBetween(mainXaml, "<Slider x:Name=\"ScaleSlider\"", "/>");

        Assert.Contains("public double ScalePercent", settingsCode);
        Assert.Contains("settings.ScalePercent = Clamp", normalizerCode);
        Assert.DoesNotContain("(int)Clamp(settings.ScalePercent", normalizerCode);
        Assert.DoesNotContain("IsSnapToTickEnabled=\"True\"", scaleSlider);
        Assert.Contains("TickFrequency=\"0.5\"", scaleSlider);
        Assert.DoesNotContain("<Viewbox", overlayXaml);
        Assert.Contains("<Canvas x:Name=\"WindowCanvas\"", overlayXaml);
        Assert.Contains("x:Name=\"VisualGroup\"", overlayXaml);
        Assert.Contains("Height=\"568.8\"", overlayXaml);
        Assert.Contains("Canvas.Top=\"126.4\"", overlayXaml);
        Assert.Contains("RenderTransformOrigin=\"0,0\"", overlayXaml);
        Assert.Contains("x:Name=\"VisualScale\"", overlayXaml);
        Assert.Contains("CenterX=\"128\"", overlayXaml);
        Assert.Contains("CenterY=\"158\"", overlayXaml);
        Assert.Contains("MaxOverlayScale = 1.8", overlayCode);
        Assert.Contains("WindowWidth = MaxVisualHorizontalExtent * 2 * MaxOverlayScale", overlayCode);
        Assert.Contains("WindowHeight = MaxVisualVerticalExtent * 2 * MaxOverlayScale", overlayCode);
        Assert.Contains("ApplyScaleTransform", overlayCode);
        Assert.Contains("VisualScale.BeginAnimation", overlayCode);
        Assert.DoesNotContain("Width = BaseWidth * scale", overlayCode);
        Assert.DoesNotContain("Height = BaseHeight * scale", overlayCode);
    }

    [Fact]
    public void OverlaySettingsExposeCoverShadowSizeControl()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());
        string settingsCode = File.ReadAllText(GetOverlaySettingsPath());
        string normalizerCode = File.ReadAllText(GetOverlaySettingsNormalizerPath());

        Assert.Contains("封面阴影大小", mainXaml);
        Assert.Contains("x:Name=\"CoverShadowSizeSlider\"", mainXaml);
        Assert.Contains("Settings.CoverShadowSizePercent", mainXaml);
        Assert.DoesNotContain("IsSnapToTickEnabled=\"True\"", ExtractBetween(mainXaml, "<Slider x:Name=\"CoverShadowSizeSlider\"", "/>"));
        Assert.Contains("TickFrequency=\"0.5\"", mainXaml);
        Assert.Contains("public double CoverShadowSizePercent", settingsCode);
        Assert.Contains("CoverShadowSizePercent = Clamp", normalizerCode);
        Assert.Contains("ApplyCoverShadowSettings", overlayCode);
        Assert.Contains("double t = _settings.CoverShadowSizePercent / 100.0", overlayCode);
        Assert.Contains("AmbientShadowEffect.BlurRadius = MaxAmbientShadowBlur * t", overlayCode);
        Assert.Contains("KeyShadowEffect.ShadowDepth = MaxKeyShadowDepth * t", overlayCode);
        Assert.DoesNotContain("if (amount <= 0", overlayCode);
        Assert.DoesNotContain("MinAmbientShadowBlur", overlayCode);
        Assert.DoesNotContain("MinKeyShadowBlur", overlayCode);
        Assert.Contains("KeyShadowEffect.BlurRadius", overlayCode);
        Assert.Contains("AmbientShadowEffect.BlurRadius", overlayCode);
        Assert.Contains("KeyShadowEffect.ShadowDepth", overlayCode);
        Assert.Contains("AmbientShadowEffect.ShadowDepth", overlayCode);
        Assert.Contains("MaxAmbientShadowDepth = 0", overlayCode);
        Assert.Contains("MaxKeyShadowDepth = 8", overlayCode);
        Assert.Contains("MaxKeyShadowOpacity = 0.22", overlayCode);
        Assert.Contains("MaxAmbientShadowOpacity = 0.19", overlayCode);
        Assert.DoesNotContain("MaxKeyShadowDepth = 18", overlayCode);
        Assert.DoesNotContain("MaxKeyShadowOpacity = 0.26", overlayCode);
        Assert.Contains("KeyShadowCaster.Opacity = 0", overlayCode);
    }

    private static string GetMainWindowXamlPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate MainWindow.xaml from the test output directory.");
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ExtractBetween(string text, string startMarker, string endMarker)
    {
        int start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");

        int end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Missing end marker: {endMarker}");

        return text[start..end];
    }

    private static string GetMainWindowCodeBehindPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "MainWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate MainWindow.xaml.cs from the test output directory.");
    }

    private static string GetShadowWindowXamlPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Views", "ShadowWindow.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate ShadowWindow.xaml from the test output directory.");
    }

    private static string GetShadowWindowCodeBehindPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Views", "ShadowWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate ShadowWindow.xaml.cs from the test output directory.");
    }

    private static string GetWindowStyleServicePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Services", "WindowStyleService.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate WindowStyleService.cs from the test output directory.");
    }

    private static string GetOverlayWindowXamlPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Views", "OverlayWindow.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate OverlayWindow.xaml from the test output directory.");
    }

    private static string GetOverlayWindowCodeBehindPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Views", "OverlayWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate OverlayWindow.xaml.cs from the test output directory.");
    }

    private static string GetOverlaySettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Models", "OverlaySettings.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate OverlaySettings.cs from the test output directory.");
    }

    private static string GetOverlaySettingsNormalizerPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Services", "OverlaySettingsNormalizer.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate OverlaySettingsNormalizer.cs from the test output directory.");
    }

}
