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
    public void HotkeyCopyTellsUserThatBindingsAutoSave()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.Contains("录入成功后会自动保存", xaml);
        Assert.DoesNotContain("修改完成后请点击保存", xaml);
        Assert.DoesNotContain("保存快捷键", xaml);
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
        Assert.Contains("KeyboardHotkeyBindingManager.Apply", code);
        Assert.Contains("TryRegisterSnapshot", code);
        Assert.Contains("RegisterHotkeySnapshotDirect", code);
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
        Assert.Contains("ShowHotkeyCapturePanel(result.Message, autoHide: true, autoHideMilliseconds: 650)", code);
        Assert.Contains("ClearHotkeyCapture(hidePanel: false)", code);
    }

    [Fact]
    public void HotkeyEditingUsesAutoSaveValidationAndPerRowDeleteButtons()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());
        string code = File.ReadAllText(GetMainWindowCodeBehindPath());

        Assert.Equal(4, CountOccurrences(xaml, "Click=\"DeleteHotkey_Click\""));
        Assert.Equal(4, CountOccurrences(xaml, "Content=\"删除\""));
        Assert.Contains("DeleteHotkey_Click", code);
        Assert.Contains("HotkeyUnsetText", code);
        Assert.Contains("KeyboardHotkeyBindingManager.Apply", code);
        Assert.Contains("TryRegisterSnapshot", code);
        Assert.Contains("该快捷键无法注册", File.ReadAllText(GetKeyboardHotkeyBindingManagerPath()));
        Assert.DoesNotContain("FindHotkeyConflict(box.Tag as string, hotkeyText)", code);
        Assert.DoesNotContain("isConflict: conflict != null", code);
    }

    [Fact]
    public void OverlayCoverUsesSoftShadowAndReadableArtistText()
    {
        string overlayXaml = File.ReadAllText(GetOverlayWindowXamlPath());
        string overlayVisual = ExtractBetween(overlayXaml, "<Canvas x:Name=\"VisualGroup\"", "<Border x:Name=\"PositionEditBar\"");
        string titleTextBlock = ExtractBetween(overlayXaml, "<TextBlock x:Name=\"TitleText\"", "</TextBlock>");
        string artistTextBlock = ExtractBetween(overlayXaml, "<TextBlock x:Name=\"ArtistText\"", "</TextBlock>");

        Assert.DoesNotContain("ShadowDepth=\"10\"", overlayXaml);
        Assert.DoesNotContain("Opacity=\"0.38\"", overlayXaml);
        Assert.DoesNotContain("BorderBrush=\"#26FFFFFF\"", overlayVisual);
        Assert.DoesNotContain("BorderBrush=\"#22FFFFFF\"", overlayVisual);
        Assert.DoesNotContain("Background=\"#7A000000\"", overlayVisual);
        Assert.DoesNotContain("BorderBrush=", overlayVisual);
        Assert.DoesNotContain("BorderThickness=", overlayVisual);
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

    [Fact]
    public void OverlaySettingsPageUsesAutoSaveWithoutInlinePreviewActions()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string overlayTab = ExtractBetween(mainXaml, "<Slider x:Name=\"CoverShadowSizeSlider\"", "<TabItem>");

        Assert.DoesNotContain("预览测试", overlayTab);
        Assert.DoesNotContain("Click=\"Save_Click\"", overlayTab);
        Assert.DoesNotContain("Click=\"ShowCurrentTrack_Click\"", overlayTab);
        Assert.Equal(1, CountOccurrences(mainXaml, "Click=\"ShowCurrentTrack_Click\""));

        Assert.Contains("ValueChanged=\"OverlaySettingSlider_ValueChanged\"", ExtractBetween(mainXaml, "<Slider x:Name=\"CoverShadowSizeSlider\"", "/>"));
        Assert.Contains("ValueChanged=\"OverlaySettingSlider_ValueChanged\"", ExtractBetween(mainXaml, "<Slider x:Name=\"DisplaySecondsSlider\"", "/>"));
        Assert.Contains("ValueChanged=\"OverlaySettingSlider_ValueChanged\"", ExtractBetween(mainXaml, "<Slider x:Name=\"ScaleSlider\"", "/>"));
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", ExtractBetween(mainXaml, "<Slider x:Name=\"CoverShadowSizeSlider\"", "/>"));
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", ExtractBetween(mainXaml, "<Slider x:Name=\"DisplaySecondsSlider\"", "/>"));
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", ExtractBetween(mainXaml, "<Slider x:Name=\"ScaleSlider\"", "/>"));

        Assert.Contains("_overlaySettingsSaveDebounceTimer", mainCode);
        Assert.Contains("private void OverlaySettingSlider_ValueChanged", mainCode);
        Assert.Contains("QueueOverlaySettingsAutoSave(debounce: true)", mainCode);
        Assert.Contains("QueueOverlaySettingsAutoSave(debounce: false)", mainCode);
        Assert.Contains("FlushOverlaySettingsAutoSave", mainCode);
        Assert.Contains("SaveOverlaySettingsNow", mainCode);
    }

    [Fact]
    public void PauseOverlayToggleSynchronizesRuntimeImmediately()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());
        string pauseToggle = ExtractElementAround(mainXaml, "Settings.PauseOverlay");
        string pauseHandler = ExtractBetween(mainCode, "private void PauseOverlay_Changed", "private void DeleteHotkey_Click");
        string showCurrentOverlay = ExtractBetween(mainCode, "private async Task ShowCurrentTrackOverlayAsync()", "private void ExitApplication()");

        Assert.Contains("Mode=TwoWay", pauseToggle);
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", pauseToggle);
        Assert.Contains("Checked=\"PauseOverlay_Changed\"", pauseToggle);
        Assert.Contains("Unchecked=\"PauseOverlay_Changed\"", pauseToggle);
        Assert.Contains("sender is not CheckBox pauseOverlayToggle", pauseHandler);
        Assert.Contains("bool isPaused = pauseOverlayToggle.IsChecked == true", pauseHandler);
        Assert.Contains("_viewModel.Settings.PauseOverlay = isPaused", pauseHandler);
        Assert.Contains("_overlayWindow.ApplySettings(_viewModel.Settings)", pauseHandler);
        Assert.Contains("_trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack", pauseHandler);
        Assert.Contains("_overlayWindow.ShowTrackAsync(track)", pauseHandler);
        Assert.Contains("if (isPaused)", pauseHandler);
        Assert.Contains("DispatcherPriority.Background", pauseHandler);
        Assert.Contains("_viewModel.Save", pauseHandler);
        Assert.DoesNotContain("_overlayWindow.ApplySettings(_viewModel.Settings)", showCurrentOverlay);
        Assert.Contains("private TrackInfo? _currentTrack", overlayCode);
        Assert.Contains("pauseOverlayChanged", overlayCode);
        Assert.Contains("ApplyPauseOverlayMode(pauseOverlayChanged)", overlayCode);
        Assert.Contains("ShowTrackAsync(_currentTrack)", overlayCode);
        Assert.Contains("BeginExitAnimation();", overlayCode);
    }

    [Fact]
    public void TrackChangeTemporaryOverlayRestoresAutoHideVisualLayer()
    {
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());
        string showTrack = ExtractBetween(overlayCode, "public Task ShowTrackAsync", "public void UpdateTrack");

        Assert.Contains("RestorePointerAutoHideVisual(force: true)", showTrack);
        Assert.Contains("HideAfterDelayAsync", showTrack);
        Assert.Contains("if (!_settings.PauseOverlay)", showTrack);
        Assert.Contains("_displayRevision++", overlayCode);
        Assert.Contains("int exitRevision = _displayRevision", overlayCode);
        Assert.Contains("if (_displayRevision == exitRevision)", overlayCode);
    }

    [Fact]
    public void PauseOverlaySubOptionsExposeMouseAutoHideAndPositionUi()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string settingsCode = File.ReadAllText(GetOverlaySettingsPath());
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());

        Assert.Contains("Settings.AutoHideOnMouseNear", mainXaml);
        Assert.Contains("MouseAutoHide_Changed", mainXaml);
        Assert.Contains("IsEnabled=\"{Binding Settings.PauseOverlay}\"", mainXaml);
        Assert.Contains("开启常驻显示悬浮窗后可用", mainXaml);
        Assert.Contains("鼠标靠近时自动隐藏", mainXaml);
        Assert.Contains("悬浮窗位置", mainXaml);
        Assert.Contains("调整位置", mainXaml);
        Assert.Contains("PositionOverlay_Click", mainXaml);

        Assert.Contains("public bool AutoHideOnMouseNear", settingsCode);
        Assert.Contains("private void MouseAutoHide_Changed", mainCode);
        Assert.Contains("_overlayWindow.ApplySettings(_viewModel.Settings)", ExtractBetween(mainCode, "private void MouseAutoHide_Changed", "private void PositionOverlay_Click"));
        Assert.Contains("private void PositionOverlay_Click", mainCode);
        Assert.Contains("StartPointerAutoHideTracking", overlayCode);
        Assert.Contains("StopPointerAutoHideTracking", overlayCode);
        Assert.Contains("CoverClip.PointToScreen", overlayCode);
        Assert.Contains("GetCursorPos", overlayCode);
        Assert.Contains("HandoffBehavior.SnapshotAndReplace", overlayCode);
        Assert.Contains("AutoHideGroup.BeginAnimation(OpacityProperty", overlayCode);
        Assert.Contains("private const double PointerAutoHideOpacity = 0;", overlayCode);
    }

    [Fact]
    public void PositionEditModeUsesOverlayInteractionWithoutChangingSavedAutoHideToggle()
    {
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string overlayXaml = File.ReadAllText(GetOverlayWindowXamlPath());
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());
        string windowStyleServiceCode = File.ReadAllText(GetWindowStyleServicePath());
        string positionHandler = ExtractBetween(mainCode, "private void PositionOverlay_Click", "private void DeleteHotkey_Click");

        Assert.Contains("_overlayWindow.BeginPositionEdit(_viewModel.Settings", positionHandler);
        Assert.Contains("_viewModel.Save", positionHandler);
        Assert.DoesNotContain("AutoHideOnMouseNear = false", positionHandler);

        Assert.Contains("x:Name=\"PositionEditBar\"", overlayXaml);
        Assert.Contains("正在调整位置", overlayXaml);
        Assert.Contains("Click=\"PositionEditCancel_Click\"", overlayXaml);
        Assert.Contains("Click=\"PositionEditDone_Click\"", overlayXaml);
        Assert.Contains("MouseLeftButtonDown=\"CoverClip_MouseLeftButtonDown\"", overlayXaml);
        Assert.Contains("MouseMove=\"CoverClip_MouseMove\"", overlayXaml);
        Assert.Contains("MouseLeftButtonUp=\"CoverClip_MouseLeftButtonUp\"", overlayXaml);
        Assert.DoesNotContain("x:Name=\"ShadowHost\"\r\n                        Canvas.Left=\"0\"\r\n                        Canvas.Top=\"0\"\r\n                        Width=\"256\"\r\n                        Height=\"256\"\r\n                        Background=\"Transparent\"\r\n                        IsHitTestVisible=\"False\"", overlayXaml);
        Assert.Contains("x:Name=\"PositionEditScale\"", overlayXaml);
        Assert.Contains("x:Name=\"PositionEditTranslate\"", overlayXaml);

        Assert.Contains("public void BeginPositionEdit", overlayCode);
        Assert.Contains("private void CompletePositionEdit", overlayCode);
        Assert.Contains("private void CancelPositionEdit", overlayCode);
        Assert.Contains("private void CleanupPositionEdit", overlayCode);
        Assert.Contains("StopPointerAutoHideTracking(restoreVisual: true)", ExtractBetween(overlayCode, "public void BeginPositionEdit", "private void PositionEditDone_Click"));
        Assert.Contains("AutoHideGroup.IsHitTestVisible = true", overlayCode);
        Assert.Contains("AutoHideGroup.IsHitTestVisible = false", overlayCode);
        Assert.Contains("WindowStyleService.ApplyOverlayStyles(this, clickThrough: !_isPositionEditing)", overlayCode);
        Assert.Contains("Mouse.Capture(CoverClip)", overlayCode);
        Assert.Contains("Mouse.Capture(null)", overlayCode);
        Assert.Contains("SystemParameters.VirtualScreenLeft", overlayCode);
        Assert.Contains("PositionEditBar.BeginAnimation(OpacityProperty", overlayCode);
        Assert.Contains("HandoffBehavior.SnapshotAndReplace", overlayCode);
        Assert.Contains("CalculatePositionPercents", overlayCode);
        Assert.Contains("GetOverlayVisibleContentRectInWindow", overlayCode);
        Assert.Contains("TryGetTextVisibleRectInWindow", overlayCode);
        Assert.Contains("FormattedText", overlayCode);
        Assert.Contains("GetMonitorWorkAreaForRect", overlayCode);
        Assert.Contains("StartPositionEditBarFollow", overlayCode);
        Assert.Contains("CompositionTarget.Rendering", overlayCode);
        Assert.Contains("UpdatePositionEditBarFollow", overlayCode);
        Assert.Contains("PreparePositionEditMotionResources", overlayCode);
        Assert.Contains("BeginNoOpAnimation", overlayCode);
        Assert.Contains("PositionEditEase", overlayCode);
        Assert.Contains("PreparePositionEditMotionResources();", ExtractBetween(overlayCode, "public void BeginPositionEdit", "private void PositionEditDone_Click"));
        Assert.Contains("SetPositionEditBarTarget", overlayCode);
        Assert.Contains("ApplyPositionEditShadowSettings", overlayCode);
        Assert.Contains("PositionEditLiftScale", overlayCode);
        Assert.Contains("PositionEditBarMaxLag", overlayCode);
        Assert.Contains("PositionEditBarSettleBoost", overlayCode);
        Assert.DoesNotContain("DispatcherTimer _positionEdit", overlayCode);
        Assert.DoesNotContain("double width = BaseWidth * scale", overlayCode);
        Assert.DoesNotContain("double height = BaseHeight * scale", overlayCode);
        Assert.DoesNotContain("Rect virtualScreen = GetVirtualScreenRect();\r\n        double dx = 0;", overlayCode);

        Assert.Contains("bool clickThrough = true", windowStyleServiceCode);
        Assert.Contains("style &= ~WsExTransparent", windowStyleServiceCode);
    }

    [Fact]
    public void PositionEditBarUsesCompactGlassTreatment()
    {
        string overlayXaml = File.ReadAllText(GetOverlayWindowXamlPath());
        string editBar = ExtractBetween(overlayXaml, "<Border x:Name=\"PositionEditBar\"", "</Border>");

        Assert.Contains("x:Name=\"PositionEditBar\"", editBar);
        Assert.Contains("PositionEditBarStyle", overlayXaml);
        Assert.Contains("PositionEditButtonStyle", overlayXaml);
        Assert.Contains("PositionEditDoneButtonStyle", overlayXaml);
        Assert.Contains("Value=\"#B81B2432\"", overlayXaml);
        Assert.Contains("Value=\"#38FFFFFF\"", overlayXaml);
        Assert.Contains("Value=\"8,5\"", overlayXaml);
        Assert.DoesNotContain("Width=\"240\"", editBar);
        Assert.DoesNotContain("Height=\"42\"", editBar);
        Assert.DoesNotContain("<ColumnDefinition Width=\"*\" />", editBar);
    }

    [Fact]
    public void GamepadHotkeySectionShowsDeviceStatusSelectionAndBindingInputs()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string gamepadSection = ExtractBetween(mainXaml, "<Border x:Name=\"GamepadHotkeyCard\"", "</Border>");
        string gamepadInputService = File.ReadAllText(GetGamepadInputServicePath());

        Assert.Contains("手柄快捷键", gamepadSection);
        Assert.Contains("x:Name=\"GamepadConnectedDot\"", gamepadSection);
        Assert.Contains("x:Name=\"GamepadStatusTitleText\"", gamepadSection);
        Assert.Contains("未检测到手柄", gamepadSection);
        Assert.Contains("请通过 USB 或蓝牙连接 Xbox 或 DualSense 手柄。", gamepadSection);
        Assert.Contains("x:Name=\"GamepadDeviceSelector\"", gamepadSection);
        Assert.Contains("SelectionChanged=\"GamepadDeviceSelector_SelectionChanged\"", gamepadSection);
        Assert.Contains("Click=\"RefreshGamepads_Click\"", gamepadSection);
        Assert.Contains("x:Name=\"GamepadBindingsHost\"", gamepadSection);
        Assert.Contains("x:Name=\"GamepadBindingsHostScale\"", gamepadSection);
        Assert.Contains("x:Name=\"GamepadPreviousBox\"", gamepadSection);
        Assert.Contains("x:Name=\"GamepadNextBox\"", gamepadSection);
        Assert.Contains("x:Name=\"GamepadToggleBox\"", gamepadSection);
        Assert.Contains("x:Name=\"GamepadShowCurrentBox\"", gamepadSection);
        Assert.Equal(4, CountOccurrences(gamepadSection, "PreviewMouseDown=\"GamepadBindingBox_PreviewMouseDown\""));
        Assert.Equal(4, CountOccurrences(gamepadSection, "Click=\"ClearGamepadBinding_Click\""));
        Assert.DoesNotContain("Phase 2", gamepadSection);
        Assert.DoesNotContain("Click=\"Save_Click\"", gamepadSection);

        Assert.Contains("private readonly GamepadInputService _gamepadService", mainCode);
        Assert.Contains("_gamepadService.DevicesChanged += GamepadService_DevicesChanged", mainCode);
        Assert.Contains("_gamepadService.SelectedButtonsChanged += GamepadService_SelectedButtonsChanged", mainCode);
        Assert.Contains("_gamepadService.Start()", mainCode);
        Assert.Contains("UpdateGamepadDeviceUi", mainCode);
        Assert.Contains("GamepadDeviceSelector_SelectionChanged", mainCode);
        Assert.Contains("RefreshGamepads_Click", mainCode);
        Assert.Contains("BeginGamepadBindingCapture", mainCode);
        Assert.Contains("RenderGamepadCaptureState", mainCode);
        Assert.Contains("SaveGamepadBindings", mainCode);
        Assert.Contains("SafeDispose(_gamepadService)", mainCode);
        Assert.Contains("public event EventHandler<GamepadButtonsChangedEventArgs>? SelectedButtonsChanged", gamepadInputService);
        Assert.Contains("public IReadOnlySet<GamepadButton> CurrentSelectedButtons", gamepadInputService);
        Assert.Contains("GamepadButtonReader", gamepadInputService);
    }

    [Fact]
    public void GamepadCaptureReusesLeftHotkeyCapturePanel()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string header = ExtractBetween(mainXaml, "<TabItem.Header>", "</TabItem.Header>");

        Assert.Contains("x:Name=\"HotkeyCapturePanel\"", header);
        Assert.Contains("x:Name=\"HotkeyCaptureActionsPanel\"", header);
        Assert.Contains("x:Name=\"GamepadCapturePrimaryButton\"", header);
        Assert.Contains("x:Name=\"GamepadCaptureSecondaryButton\"", header);
        Assert.Contains("GamepadCapturePrimary_Click", header);
        Assert.Contains("GamepadCaptureSecondary_Click", header);
        Assert.Contains("title: \"手柄快捷键修改\"", mainCode);
        Assert.Contains("手柄快捷键修改", mainCode);
        Assert.Contains("按下要绑定的手柄按键或组合键", mainCode);
        Assert.Contains("请先松开手柄上的所有按键", mainCode);
        Assert.Contains("仍然使用", mainCode);
        Assert.Contains("重新录入", mainCode);
        Assert.Contains("替换原绑定", mainCode);
        Assert.Contains("GamepadCaptureState.SingleButtonWarning", mainCode);
        Assert.Contains("GamepadCaptureState.Conflict", mainCode);
    }

    [Fact]
    public void HotkeyConfirmationPanelCanReceiveClicksBeforeOutsideCancelRuns()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string header = ExtractBetween(mainXaml, "<TabItem.Header>", "</TabItem.Header>");
        string hitTestMethod = ExtractBetween(mainCode, "private bool IsClickInsideCurrentHotkeyBox", "private static bool IsModifierKey");

        Assert.DoesNotContain("IsHitTestVisible=\"False\"", header);
        Assert.Contains("ReferenceEquals(source, HotkeyCapturePanel)", hitTestMethod);
    }

    [Fact]
    public void GamepadRuntimeShortcutsUseSharedAppActionDispatch()
    {
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());

        Assert.Contains("private readonly GamepadShortcutRuntime _gamepadShortcutRuntime", mainCode);
        Assert.Contains("private readonly object _gamepadRuntimeLock", mainCode);
        Assert.Contains("private GamepadBindingSet _gamepadRuntimeBindings", mainCode);
        Assert.Contains("HandleGamepadShortcutButtons(e.Buttons)", mainCode);
        Assert.Contains("isCaptureActive: _gamepadRuntimeCaptureActive", mainCode);
        Assert.Contains("RefreshGamepadRuntimeBindings()", mainCode);
        Assert.Contains("ResumeGamepadRuntimeAfterCapture", mainCode);
        Assert.Contains("ExecuteAppActionAsync(action.Value)", mainCode);
        Assert.Contains("await ExecuteAppActionAsync(e.Action)", mainCode);
        Assert.Contains("_gamepadShortcutRuntime.Reset()", mainCode);
        Assert.Contains("GamepadShortcutRuntime", File.ReadAllText(GetGamepadShortcutRuntimePath()));
    }

    [Fact]
    public void KeyboardCaptureSuppressesGlobalHotkeyActionsAndUsesConflictActions()
    {
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string hotkeyHandler = ExtractBetween(mainCode, "private async void HotkeyService_HotkeyPressed", "private async Task ExecuteAppActionAsync");
        string keyboardConflictCancel = ExtractBetween(mainCode, "private void HandleKeyboardHotkeyConflictSecondary", "private void HandleGamepadCaptureDeviceDisconnected");
        string keyboardEscape = ExtractBetween(mainCode, "private void HotkeyBox_PreviewKeyDown", "private void HotkeyBox_PreviewKeyUp");
        string outsideCancel = ExtractBetween(mainCode, "private void CancelHotkeyCaptureIfClickOutside", "private bool IsClickInsideCurrentHotkeyBox");
        string gamepadCancel = ExtractBetween(mainCode, "private void CancelGamepadCapture", "private void ClearGamepadCaptureState");

        Assert.Contains("private KeyboardHotkeyConflict? _keyboardHotkeyConflict", mainCode);
        Assert.Contains("private bool IsKeyboardHotkeyInputSuppressed", mainCode);
        Assert.Contains("if (IsKeyboardHotkeyInputSuppressed)", hotkeyHandler);
        Assert.Contains("TryCommitRegisteredHotkeyCandidate(e.HotkeyText)", hotkeyHandler);
        Assert.Contains("return;", hotkeyHandler);
        Assert.Contains("HandleKeyboardHotkeyConflictPrimary", mainCode);
        Assert.Contains("HandleKeyboardHotkeyConflictSecondary", mainCode);
        Assert.Contains("ApplyReplacingConflict", mainCode);
        Assert.Contains("title: \"键盘快捷键修改\"", mainCode);
        Assert.Contains("primaryAction: \"替换原绑定\"", mainCode);
        Assert.Contains("secondaryAction: \"取消\"", mainCode);
        Assert.Contains("DisplayHotkeyBoxValues()", keyboardConflictCancel);
        Assert.DoesNotContain("RestoreHotkeyBox(box)", keyboardConflictCancel);
        Assert.Contains("DisplayHotkeyBoxValues()", keyboardEscape);
        Assert.Contains("DisplayHotkeyBoxValues()", outsideCancel);
        Assert.Contains("DisplayGamepadBindingBoxValues()", gamepadCancel);
    }

    [Fact]
    public void RegisteredHotkeysCanFeedKeyboardCaptureCandidates()
    {
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string hotkeyService = File.ReadAllText(GetGlobalHotkeyServicePath());
        string hotkeyHandler = ExtractBetween(mainCode, "private async void HotkeyService_HotkeyPressed", "private async Task ExecuteAppActionAsync");

        Assert.Contains("public sealed record GlobalHotkeyEventArgs", hotkeyService);
        Assert.Contains("string HotkeyText", hotkeyService);
        Assert.Contains("RegisteredHotkey", hotkeyService);
        Assert.Contains("HotkeyPressed?.Invoke", hotkeyService);
        Assert.Contains("_hotkeyService.HotkeyPressed += HotkeyService_HotkeyPressed", mainCode);
        Assert.Contains("TryCommitRegisteredHotkeyCandidate", mainCode);
        Assert.Contains("_pendingHotkeyText = hotkeyText", mainCode);
        Assert.Contains("TryCommitPendingHotkey(_capturingHotkeyBox)", mainCode);
        Assert.Contains("TryCommitRegisteredHotkeyCandidate(e.HotkeyText)", hotkeyHandler);
        Assert.Contains("await ExecuteAppActionAsync(e.Action)", hotkeyHandler);
    }

    [Fact]
    public void OverlayTitleAndArtistTogglesApplyToRuntimeOverlayImmediately()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string titleToggle = ExtractElementAround(mainXaml, "Settings.ShowTitle");
        string artistToggle = ExtractElementAround(mainXaml, "Settings.ShowArtist");
        string handler = ExtractBetween(mainCode, "private void OverlayTextOption_Changed", "private void PauseOverlay_Changed");

        Assert.Contains("Checked=\"OverlayTextOption_Changed\"", titleToggle);
        Assert.Contains("Unchecked=\"OverlayTextOption_Changed\"", titleToggle);
        Assert.Contains("Checked=\"OverlayTextOption_Changed\"", artistToggle);
        Assert.Contains("Unchecked=\"OverlayTextOption_Changed\"", artistToggle);
        Assert.Contains("_overlayWindow.ApplySettings(_viewModel.Settings)", handler);
        Assert.Contains("_trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack", handler);
        Assert.Contains("_overlayWindow.UpdateTrack(track)", handler);
        Assert.Contains("QueueOverlaySettingsAutoSave(debounce: false)", handler);
    }

    [Fact]
    public void DesktopShellUsesSharedMusicFloatIconEverywhere()
    {
        string project = File.ReadAllText(GetProjectFilePath());
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string trayCode = File.ReadAllText(GetTrayIconServicePath());

        Assert.Contains("<ApplicationIcon>Assets\\MusicFloat.ico</ApplicationIcon>", project);
        Assert.Contains("<Content Include=\"Assets\\MusicFloat.ico\" CopyToOutputDirectory=\"PreserveNewest\" />", project);
        Assert.Contains("ApplyAppIcon", mainCode);
        Assert.Contains("Assets\", \"MusicFloat.ico", mainCode);
        Assert.Contains("ShowInTaskbar=\"True\"", mainXaml);
        Assert.Contains("Assets\", \"MusicFloat.ico", trayCode);
        Assert.Contains("Icon.ExtractAssociatedIcon", trayCode);
    }

    [Fact]
    public void TrayMenuUsesAsyncRestoreActionsAndCompleteExitLabels()
    {
        string trayCode = File.ReadAllText(GetTrayIconServicePath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());

        Assert.Contains("打开 MusicFloat", trayCode);
        Assert.Contains("显示悬浮窗", trayCode);
        Assert.Contains("退出 MusicFloat", trayCode);
        Assert.Contains("new Forms.ToolStripSeparator()", trayCode);
        Assert.Contains("BeginInvoke", trayCode);
        Assert.Contains("PrepareForExit", trayCode);
        Assert.DoesNotContain("Dispatcher.Invoke", trayCode);
        Assert.Contains("RequestApplicationExit", mainCode);
        Assert.Contains("_trayIconService.PrepareForExit()", mainCode);
    }

    [Fact]
    public void MinimizeButtonKeepsWindowOnTaskbarAndExitLifecycleIsSessionAware()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string appCode = File.ReadAllText(GetAppCodeBehindPath());

        Assert.Contains("ShowInTaskbar=\"True\"", mainXaml);
        Assert.Contains("WindowState = WindowState.Minimized", mainCode);
        Assert.Contains("private bool _hasCleanedUpForExit", mainCode);
        Assert.Contains("CleanupForExit", mainCode);
        Assert.Contains("RequestApplicationExit", mainCode);
        Assert.Contains("SessionEnding", appCode);
        Assert.Contains("RequestApplicationExit(isSessionEnding: true)", appCode);
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

    private static string ExtractElementAround(string text, string marker)
    {
        int markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Missing marker: {marker}");

        int start = text.LastIndexOf('<', markerIndex);
        int end = text.IndexOf("/>", markerIndex, StringComparison.Ordinal);
        Assert.True(start >= 0 && end >= 0, $"Could not extract element around marker: {marker}");

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

    private static string GetGamepadInputServicePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Services", "GamepadInputService.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate GamepadInputService.cs from the test output directory.");
    }

    private static string GetGlobalHotkeyServicePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Services", "GlobalHotkeyService.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate GlobalHotkeyService.cs from the test output directory.");
    }

    private static string GetKeyboardHotkeyBindingManagerPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Services", "KeyboardHotkeyBindingManager.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate KeyboardHotkeyBindingManager.cs from the test output directory.");
    }

    private static string GetGamepadShortcutRuntimePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Services", "GamepadShortcutRuntime.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate GamepadShortcutRuntime.cs from the test output directory.");
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

    private static string GetTrayIconServicePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Services", "TrayIconService.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate TrayIconService.cs from the test output directory.");
    }

    private static string GetProjectFilePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "AppleMusicOverlay.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate AppleMusicOverlay.csproj from the test output directory.");
    }

    private static string GetAppCodeBehindPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "App.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate App.xaml.cs from the test output directory.");
    }

}
