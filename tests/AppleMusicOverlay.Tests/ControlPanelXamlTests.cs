namespace AppleMusicOverlay.Tests;

public sealed class ControlPanelXamlTests
{
    [Fact]
    public void MainWindowUsesFourPageControlPanelShell()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.Contains("TabControl", xaml);
        Assert.Contains("[NavCurrent]", xaml);
        Assert.Contains("[NavOverlay]", xaml);
        Assert.Contains("[NavShortcuts]", xaml);
        Assert.Contains("[NavGeneral]", xaml);
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
    public void SidebarNavigationUsesSharedExpandableCardsInLayoutFlow()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());
        string cardCode = File.ReadAllText(GetExpandableNavigationCardPath());

        Assert.Equal(4, CountOccurrences(xaml, "<views:ExpandableNavigationCard x:Name="));
        Assert.Contains("x:Name=\"CurrentNavigationCard\"", xaml);
        Assert.Contains("x:Name=\"OverlayNavigationCard\"", xaml);
        Assert.Contains("x:Name=\"HotkeyNavigationCard\"", xaml);
        Assert.Contains("x:Name=\"GeneralNavigationCard\"", xaml);
        Assert.Contains("Content=\"{TemplateBinding Content}\"", xaml);
        Assert.Contains("x:Name=\"ExpansionHost\"", xaml);
        Assert.Contains("x:Name=\"ExpansionContentPresenter\"", xaml);
        Assert.Contains("x:Name=\"PART_HeaderRoot\"", xaml);
        Assert.Contains("AncestorType={x:Type TabItem}", xaml);
        Assert.Contains("BeginExpansionAnimation", cardCode);
        Assert.Contains("HeaderedContentControl", cardCode);
        Assert.DoesNotContain("Canvas", xaml);
        Assert.DoesNotContain("RetiredHotkeyCapturePanel", xaml);
    }

    [Fact]
    public void ExpandableNavigationCardOwnsSharedPromptStateAndAutoCollapseTimer()
    {
        string cardCode = File.ReadAllText(GetExpandableNavigationCardPath());
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.Contains("ExpansionTitleProperty", cardCode);
        Assert.Contains("ExpansionMessageProperty", cardCode);
        Assert.Contains("IsPromptPersistentProperty", cardCode);
        Assert.Contains("AutoCollapseDelayProperty", cardCode);
        Assert.Contains("private readonly DispatcherTimer _autoCollapseTimer", cardCode);
        Assert.Equal(1, CountOccurrences(cardCode, "DispatcherTimer _autoCollapseTimer = new()"));
        Assert.Contains("public void ShowPrompt", cardCode);
        Assert.Contains("public void ShowPersistentPrompt", cardCode);
        Assert.Contains("public void ClearPrompt", cardCode);
        Assert.Contains("_autoCollapseTimer.Stop()", cardCode);
        Assert.Contains("_autoCollapseTimer.Start()", cardCode);
        Assert.Contains("if (!IsPromptPersistent)", cardCode);
        Assert.Contains("x:Name=\"DefaultExpansionContent\"", xaml);
        Assert.Contains("Text=\"{TemplateBinding ExpansionTitle}\"", xaml);
        Assert.Contains("Text=\"{TemplateBinding ExpansionMessage}\"", xaml);
    }

    [Fact]
    public void CurrentAndOverlayOperationsUseNavigationPromptCardsForTemporaryFeedback()
    {
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.Contains("ShowCurrentNavigationPrompt", mainCode);
        Assert.Contains("ShowOverlayNavigationPrompt", mainCode);
        Assert.Contains("ShowPersistentOverlayNavigationPrompt", mainCode);
        Assert.Contains("ClearInactiveTransientNavigationPrompts", mainCode);
        Assert.Contains("CurrentNavigationCard.ShowPrompt(Localizer.Text(titleKey), Localizer.Text(messageKey), autoCollapseDelay)", mainCode);
        Assert.Contains("OverlayNavigationCard.ShowPrompt(Localizer.Text(titleKey), Localizer.Text(messageKey), autoCollapseDelay)", mainCode);
        Assert.Contains("OverlayNavigationCard.ShowPersistentPrompt(Localizer.Text(titleKey), Localizer.Text(messageKey))", mainCode);
        Assert.Contains("GeneralNavigationCard.ShowPrompt(Localizer.Text(titleKey), Localizer.Text(messageKey), autoCollapseDelay)", mainCode);
        Assert.Contains("ShowCurrentNavigationPrompt(\"CurrentRefreshInProgressTitle\"", mainCode);
        Assert.Contains("ShowCurrentNavigationPrompt(\"CurrentRefreshCompleteTitle\"", mainCode);
        Assert.Contains("ShowCurrentNavigationPrompt(\"CurrentNoMusicTitle\"", mainCode);
        Assert.Contains("ShowOverlayNavigationPrompt(\"OverlaySavedTitle\"", mainCode);
        Assert.Contains("ShowOverlayNavigationPrompt(\"OverlayShownTitle\"", mainCode);
        Assert.Contains("ShowPersistentOverlayNavigationPrompt(\"OverlayAdjustingTitle\"", mainCode);
        Assert.Contains("ShowOverlayNavigationPrompt(\"OverlayPositionSavedTitle\"", mainCode);
        Assert.Contains("ShowOverlayNavigationPrompt(\"OverlayPositionCancelledTitle\"", mainCode);
        Assert.Contains("ShowGeneralNavigationPrompt(\"GeneralLanguageChangedTitle\"", mainCode);
        Assert.Contains("RefreshSourcesAsync(showNavigationFeedback: true", mainCode);
        Assert.Contains("BeginPositionEdit(_viewModel.Settings, SaveOverlayPositionSettingsNow, HandleOverlayPositionEditCompleted)", mainCode);
        Assert.Contains("<TextBlock Text=\"{Binding StatusText}\"", xaml);
        Assert.DoesNotContain("_viewModel.SetStatus(\"设置已保存", mainCode);
        Assert.DoesNotContain("? \"快捷键已保存并生效。\"", mainCode);
        Assert.DoesNotContain(": \"部分快捷键未注册，请检查组合键是否被占用。\"", mainCode);
    }

    [Fact]
    public void HotkeyFieldsCaptureKeyPressesInsteadOfFreeTextInput()
    {
        string xaml = File.ReadAllText(GetMainWindowXamlPath());

        Assert.Contains("HotkeyBox_PreviewKeyDown", xaml);
        Assert.Contains("HotkeyBox_PreviewKeyUp", xaml);
        Assert.Contains("[\"HotkeyCapturePrompt\"]", File.ReadAllText(GetLocalizationServicePath()));
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

        Assert.Contains("[ShortcutsPageDescription]", xaml);
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
        string cardCode = File.ReadAllText(GetExpandableNavigationCardPath());
        string header = ExtractHotkeyNavigationCard(xaml);

        Assert.Contains("x:Name=\"HotkeyNavigationCard\"", header);
        Assert.Contains("x:Name=\"HotkeyCapturePanel\"", xaml);
        Assert.Contains("x:Name=\"HotkeyCaptureTitleText\"", xaml);
        Assert.Contains("x:Name=\"HotkeyCaptureStatusText\"", xaml);
        Assert.Contains("x:Name=\"ExpansionScale\"", xaml);
        Assert.Contains("x:Name=\"ExpansionTranslate\"", xaml);
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
        Assert.Contains("HotkeyNavigationCard.IsExpanded = show", code);
        Assert.Contains("HotkeyNavigationCard.RefreshExpandedContentHeight()", code);
        Assert.Contains("bool wasVisible", cardCode);
        Assert.Contains("double startHeight", cardCode);
        Assert.Contains("_expansionContentHost.Measure", cardCode);
        Assert.Contains("HotkeyCaptureStatusText.Text = statusText", code);
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
        string keyboardHotkeySection = ExtractBetween(xaml, "<TextBox x:Name=\"KeyboardPreviousBox\"", "<Border x:Name=\"GamepadHotkeyCard\"");

        Assert.Equal(4, CountOccurrences(keyboardHotkeySection, "IsReadOnly=\"True\""));
        Assert.Equal(4, CountOccurrences(keyboardHotkeySection, "Focusable=\"False\""));
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
        Assert.Equal(4, CountOccurrences(xaml, "Content=\"{Binding [Delete]"));
        Assert.Contains("DeleteHotkey_Click", code);
        Assert.Contains("HotkeyUnsetText", code);
        Assert.Contains("KeyboardHotkeyBindingManager.Apply", code);
        Assert.Contains("TryRegisterSnapshot", code);
        Assert.Contains("HotkeyRegistrationFailed", File.ReadAllText(GetKeyboardHotkeyBindingManagerPath()));
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
        Assert.Contains("CornerRadius=\"13\"", overlayVisual);
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
        Assert.Contains("FontFamily=\"Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI\"", titleTextBlock);
        Assert.Contains("FontSize=\"14.2\"", titleTextBlock);
        Assert.Contains("FontWeight=\"SemiBold\"", titleTextBlock);
        Assert.Contains("FontFamily=\"Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI\"", artistTextBlock);
        Assert.Contains("FontSize=\"12\"", artistTextBlock);
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
    public void OverlayRuntimeUsesLayeredBitmapHostWhileKeepingWpfVisualSource()
    {
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());
        string layeredHostCode = File.ReadAllText(GetLayeredOverlayWindowPath());

        Assert.Contains("private readonly LayeredOverlayWindow _layeredOverlayWindow", mainCode);
        Assert.Contains("_trackMonitor.TrackChanged += (_, track) => PostToDispatcher(() => _ = ShowOverlayTrackAsync(track))", mainCode);
        Assert.Contains("_trackMonitor.TrackRefreshed += (_, track) => PostToDispatcher(() => UpdateOverlayTrack(track))", mainCode);
        Assert.Contains("await ShowOverlayTrackAsync(track)", mainCode);
        Assert.Contains("_layeredOverlayWindow.HideImmediately()", mainCode);
        Assert.Contains("SafeDispose(_layeredOverlayWindow)", mainCode);
        Assert.Contains("public OverlaySnapshot CreateSnapshot(TrackInfo track)", overlayCode);
        Assert.Contains("RenderTargetBitmap", overlayCode);
        Assert.Contains("x:Name=\"VisualGroup\"", File.ReadAllText(GetOverlayWindowXamlPath()));
        Assert.Contains("UpdateLayeredWindow", layeredHostCode);
        Assert.Contains("WS_EX_LAYERED", layeredHostCode);
        Assert.Contains("WS_EX_TRANSPARENT", layeredHostCode);
        Assert.Contains("AC_SRC_ALPHA", layeredHostCode);
        Assert.Contains("EnterStartOffsetY = 8", layeredHostCode);
        Assert.Contains("ExitEndOffsetY = -4", layeredHostCode);
        Assert.Contains("private sealed class LayeredFrame", layeredHostCode);
        Assert.Contains("UpdateLayeredWindow(_hwnd, screenDc, ref destination", layeredHostCode);
        Assert.DoesNotContain("TransformSnapshot", layeredHostCode);
        Assert.DoesNotContain("SampleBilinear", layeredHostCode);
        Assert.Contains("DpiScaleY", layeredHostCode);
        Assert.Contains("ConfigurePointerAutoHide", layeredHostCode);
        Assert.Contains("GetCursorPos", layeredHostCode);
        Assert.Contains("RefreshLayeredOverlaySnapshot", mainCode);
        Assert.Contains("_overlayWindow.ApplySettings(_viewModel.Settings, animateScale: false)", ExtractBetween(mainCode, "private void OverlaySettingSlider_ValueChanged", "private void OverlayTextOption_Changed"));
        Assert.Contains("ShowWpfSettingsPreview(animateScale: false);", ExtractBetween(mainCode, "private void OverlaySettingSlider_ValueChanged", "private void OverlayTextOption_Changed"));
        Assert.DoesNotContain("QueueLayeredOverlaySnapshotRefresh();", ExtractBetween(mainCode, "private void OverlaySettingSlider_ValueChanged", "private void OverlayTextOption_Changed"));
        Assert.DoesNotContain("RefreshLayeredOverlaySnapshot();", ExtractBetween(mainCode, "private void OverlaySettingSlider_ValueChanged", "private void OverlayTextOption_Changed"));
        Assert.Contains("ShowWpfSettingsPreview(animateScale: false);", ExtractBetween(mainCode, "private void OverlayTrackFontCombo_SelectionChanged", "private void RefreshOverlayTrackFontOptions"));
        Assert.Contains("ConfigureLayeredOverlayRuntime(snapshot)", mainCode);
        Assert.Contains("GetDpiForWindow", overlayCode);
        Assert.Contains("CoverScreenLeft", layeredHostCode);
        Assert.Contains("TryUpdate(snapshot)", layeredHostCode);
        Assert.Contains("Marshal.Copy(snapshot.Pixels, 0, _bits", layeredHostCode);
        Assert.Contains("private void ApplyScaleTransform(double scale, bool animate)", overlayCode);
    }

    [Fact]
    public void OverlaySettingsPreviewUsesLiveWpfBeforeLayeredRuntimeHandoff()
    {
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());
        string layeredHostCode = File.ReadAllText(GetLayeredOverlayWindowPath());

        Assert.Contains("private enum OverlayRenderMode", mainCode);
        Assert.Contains("WpfSettingsPreview", mainCode);
        Assert.Contains("WpfPositionEdit", mainCode);
        Assert.Contains("_settingsPreviewSettleTimer", mainCode);
        Assert.Contains("Interval = TimeSpan.FromMilliseconds(300)", mainCode);
        Assert.Contains("private void ShowWpfSettingsPreview(bool animateScale)", mainCode);
        Assert.Contains("_overlayWindow.ShowSettingsPreview(track, _viewModel.Settings, animateScale)", mainCode);
        Assert.Contains("_layeredOverlayWindow.HideImmediately();", ExtractBetween(mainCode, "private void ShowWpfSettingsPreview", "private void QueueSettingsPreviewLayeredHandoff"));
        Assert.Contains("QueueSettingsPreviewLayeredHandoff();", mainCode);
        Assert.Contains("private void CompleteSettingsPreviewLayeredHandoff()", mainCode);
        Assert.Contains("_layeredOverlayWindow.ShowSnapshotImmediately(snapshot, _viewModel.Settings.DisplaySeconds, _viewModel.Settings.PauseOverlay)", mainCode);
        Assert.Contains("_overlayWindow.HideOverlayVisualImmediately();", ExtractBetween(mainCode, "private void CompleteSettingsPreviewLayeredHandoff", "private void RefreshLayeredOverlaySnapshot"));

        string showTrack = ExtractBetween(mainCode, "private async Task ShowOverlayTrackAsync", "private void UpdateOverlayTrack");
        Assert.Contains("if (_overlayRenderMode == OverlayRenderMode.WpfSettingsPreview)", showTrack);
        Assert.Contains("_overlayWindow.ShowSettingsPreview(track, _viewModel.Settings, animateScale: false)", showTrack);
        Assert.Contains("return;", showTrack);

        string updateTrack = ExtractBetween(mainCode, "private void UpdateOverlayTrack", "private void CompleteSettingsPreviewLayeredHandoff");
        Assert.Contains("if (_overlayRenderMode == OverlayRenderMode.WpfSettingsPreview)", updateTrack);
        Assert.Contains("_overlayWindow.ShowSettingsPreview(track, _viewModel.Settings, animateScale: false)", updateTrack);

        string fontHandler = ExtractBetween(mainCode, "private void OverlayTrackFontCombo_SelectionChanged", "private void RefreshOverlayTrackFontOptions");
        Assert.Contains("ShowWpfSettingsPreview(animateScale: false);", fontHandler);
        Assert.DoesNotContain("RefreshLayeredOverlaySnapshot();", fontHandler);

        string positionHandler = ExtractBetween(mainCode, "private void PositionOverlay_Click", "private void RefreshGamepads_Click");
        Assert.Contains("_overlayRenderMode = OverlayRenderMode.WpfPositionEdit", positionHandler);
        Assert.Contains("_settingsPreviewSettleTimer.Stop();", positionHandler);

        string positionCompleted = ExtractBetween(mainCode, "private void HandleOverlayPositionEditCompleted", "private void DeleteHotkey_Click");
        Assert.Contains("_overlayRenderMode = OverlayRenderMode.LayeredRuntime", positionCompleted);
        Assert.Contains("RefreshLayeredOverlaySnapshot();", positionCompleted);

        Assert.Contains("public void ShowSettingsPreview(TrackInfo track, OverlaySettings settings, bool animateScale)", overlayCode);
        Assert.Contains("_isSettingsPreviewing", overlayCode);
        Assert.Contains("if (_isPositionEditing || _isSettingsPreviewing)", overlayCode);
        Assert.Contains("ShowSnapshotImmediately", layeredHostCode);
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

        Assert.Contains("[CoverShadowSize]", mainXaml);
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
    public void OverlaySettingsPageExposesTrackInformationFontCombo()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string settingsCode = File.ReadAllText(GetOverlaySettingsPath());
        string normalizerCode = File.ReadAllText(GetOverlaySettingsNormalizerPath());

        Assert.Contains("[OverlayTrackFont]", mainXaml);
        Assert.Contains("[OverlayTrackFontDescription]", mainXaml);
        Assert.Contains("x:Name=\"OverlayTrackFontCombo\"", mainXaml);
        Assert.Contains("DisplayMemberPath=\"DisplayName\"", mainXaml);
        Assert.Contains("SelectedValuePath=\"Id\"", mainXaml);
        Assert.Contains("Settings.OverlayTrackFont", mainXaml);
        Assert.Contains("SelectionChanged=\"OverlayTrackFontCombo_SelectionChanged\"", mainXaml);
        Assert.Contains("public string OverlayTrackFont", settingsCode);
        Assert.Contains("OverlayTrackFontIds.NormalizeKnownId", normalizerCode);
    }

    [Fact]
    public void OverlayContentSettingsRemainAvailableWhenPauseOverlayIsOff()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string pauseDependentSettings = ExtractBetween(
            mainXaml,
            "x:Name=\"PauseOverlayDependentSettings\"",
            "x:Name=\"OverlayContentSettings\"");
        string overlayContentSettings = ExtractBetween(
            mainXaml,
            "x:Name=\"OverlayContentSettings\"",
            "x:Name=\"HotkeyNavigationCard\"");

        Assert.Contains("IsEnabled=\"{Binding Settings.PauseOverlay}\"", pauseDependentSettings);
        Assert.Contains("Settings.AutoHideOnMouseNear", pauseDependentSettings);
        Assert.Contains("PositionOverlay_Click", pauseDependentSettings);
        Assert.DoesNotContain("Settings.ShowTitle", pauseDependentSettings);
        Assert.DoesNotContain("Settings.ShowArtist", pauseDependentSettings);
        Assert.DoesNotContain("OverlayTrackFontCombo", pauseDependentSettings);

        Assert.Contains("Settings.ShowTitle", overlayContentSettings);
        Assert.Contains("Settings.ShowArtist", overlayContentSettings);
        Assert.Contains("OverlayTrackFontCombo", overlayContentSettings);
        Assert.DoesNotContain("IsEnabled=\"{Binding Settings.PauseOverlay}\"", overlayContentSettings);
        Assert.DoesNotContain("[PauseOverlayRequired]", overlayContentSettings);
    }

    [Fact]
    public void ComboBoxDisabledTemplateKeepsDarkThemeSurface()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string comboBoxStyle = ExtractBetween(
            mainXaml,
            "<Style TargetType=\"{x:Type ComboBox}\">",
            "<Style TargetType=\"{x:Type CheckBox}\">");
        string disabledTrigger = ExtractBetween(
            comboBoxStyle,
            "<Trigger Property=\"IsEnabled\" Value=\"False\">",
            "</Trigger>");

        Assert.Contains("<ControlTemplate TargetType=\"{x:Type ToggleButton}\">", comboBoxStyle);
        Assert.Contains("Background=\"{TemplateBinding Background}\"", comboBoxStyle);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", comboBoxStyle);
        Assert.Contains("VerticalAlignment=\"Stretch\"", comboBoxStyle);
        Assert.Contains("x:Name=\"SelectionText\"", comboBoxStyle);
        Assert.Contains("x:Name=\"DropDownArrow\"", comboBoxStyle);
        Assert.Contains("TargetName=\"Input\" Property=\"Background\" Value=\"{StaticResource SurfaceControlBrush}\"", disabledTrigger);
        Assert.Contains("TargetName=\"Input\" Property=\"BorderBrush\" Value=\"{StaticResource BorderSoftBrush}\"", disabledTrigger);
        Assert.Contains("TargetName=\"SelectionText\" Property=\"Foreground\" Value=\"{StaticResource TextMutedBrush}\"", disabledTrigger);
        Assert.Contains("TargetName=\"DropDownArrow\" Property=\"Stroke\" Value=\"{StaticResource TextMutedBrush}\"", disabledTrigger);
        Assert.DoesNotContain("White", disabledTrigger, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#FFFFFF", disabledTrigger, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SystemColors", disabledTrigger);
    }

    [Fact]
    public void OverlayTrackFontOnlyUpdatesOverlayTitleAndArtist()
    {
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());

        Assert.Contains("ApplyTrackInformationFonts", overlayCode);
        Assert.Contains("TitleText.FontFamily", overlayCode);
        Assert.Contains("ArtistText.FontFamily", overlayCode);
        Assert.DoesNotContain("Application.Current", overlayCode);
        Assert.DoesNotContain("PositionEditTitleText.FontFamily", overlayCode);
    }

    [Fact]
    public void OverlayTrackFontChangeRoutesFeedbackToOverlayCardWithoutRestartingMediaCapture()
    {
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string handler = ExtractBetween(mainCode, "private void OverlayTrackFontCombo_SelectionChanged", "private void RefreshOverlayTrackFontOptions");

        Assert.Contains("OverlayTrackFontCombo_SelectionChanged", mainCode);
        Assert.Contains("ShowOverlayNavigationPrompt(\"OverlayFontUpdatedTitle\"", handler);
        Assert.Contains("QueueOverlaySettingsAutoSave(debounce: false)", handler);
        Assert.Contains("_overlayWindow.ApplyTrackFontSetting", handler);
        Assert.DoesNotContain("new SmtcMediaSessionService", handler);
        Assert.DoesNotContain("_trackMonitor = ", handler);
        Assert.DoesNotContain("RefreshSourcesAsync", handler);
    }

    [Fact]
    public void OverlayTrackFontLocalizationKeysExistForChineseAndEnglish()
    {
        string localizerCode = File.ReadAllText(GetLocalizationServicePath());

        foreach (string key in new[]
                 {
                     "OverlayTrackFont",
                     "OverlayTrackFontDescription",
                     "OverlayTrackFontDefault",
                     "OverlayTrackFontSpotifyMix",
                     "OverlayTrackFontSfPro",
                     "OverlayFontUpdatedTitle",
                     "OverlayFontUpdatedMessage"
                 })
        {
            Assert.Equal(2, CountOccurrences(localizerCode, $"[\"{key}\"]"));
        }
    }

    [Fact]
    public void PauseOverlayToggleSynchronizesRuntimeImmediately()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());
        string pauseToggle = ExtractElementAround(mainXaml, "Settings.PauseOverlay");
        string pauseHandler = ExtractBetween(mainCode, "private void PauseOverlay_Changed", "private void DeleteHotkey_Click");
        string showCurrentOverlay = ExtractBetween(mainCode, "private async Task<bool> ShowCurrentTrackOverlayAsync()", "private void ExitApplication()");

        Assert.Contains("Mode=TwoWay", pauseToggle);
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", pauseToggle);
        Assert.Contains("Checked=\"PauseOverlay_Changed\"", pauseToggle);
        Assert.Contains("Unchecked=\"PauseOverlay_Changed\"", pauseToggle);
        Assert.Contains("sender is not CheckBox pauseOverlayToggle", pauseHandler);
        Assert.Contains("bool isPaused = pauseOverlayToggle.IsChecked == true", pauseHandler);
        Assert.Contains("_viewModel.Settings.PauseOverlay = isPaused", pauseHandler);
        Assert.Contains("_overlayWindow.ApplySettings(_viewModel.Settings)", pauseHandler);
        Assert.Contains("_trackMonitor.CurrentTrack ?? _viewModel.CurrentTrack", pauseHandler);
        Assert.Contains("_ = ShowOverlayTrackAsync(track)", pauseHandler);
        Assert.Contains("_layeredOverlayWindow.HideImmediately()", pauseHandler);
        Assert.Contains("if (isPaused)", pauseHandler);
        Assert.Contains("DispatcherPriority.Background", pauseHandler);
        Assert.Contains("_viewModel.Save", pauseHandler);
        Assert.Contains("await ShowOverlayTrackAsync(track)", showCurrentOverlay);
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
        Assert.Contains("[PauseOverlayRequired]", mainXaml);
        Assert.Contains("[AutoHideOnMouseNear]", mainXaml);
        Assert.Contains("[OverlayPosition]", mainXaml);
        Assert.Contains("[AdjustPosition]", mainXaml);
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
        Assert.Contains("[PositionEditBarTitle]", overlayXaml);
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

        Assert.Contains("[GamepadShortcuts]", gamepadSection);
        Assert.Contains("x:Name=\"GamepadConnectedDot\"", gamepadSection);
        Assert.Contains("x:Name=\"GamepadStatusTitleText\"", gamepadSection);
        Assert.Contains("[GamepadNotDetected]", gamepadSection);
        Assert.Contains("[GamepadNotDetectedDescription]", gamepadSection);
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
        string header = ExtractHotkeyNavigationCard(mainXaml);

        Assert.Contains("x:Name=\"HotkeyCapturePanel\"", header);
        Assert.Contains("x:Name=\"HotkeyCaptureActionsPanel\"", header);
        Assert.Contains("x:Name=\"GamepadCapturePrimaryButton\"", header);
        Assert.Contains("x:Name=\"GamepadCaptureSecondaryButton\"", header);
        Assert.Contains("GamepadCapturePrimary_Click", header);
        Assert.Contains("GamepadCaptureSecondary_Click", header);
        Assert.Contains("Localizer.Text(\"GamepadHotkeyEditTitle\")", mainCode);
        Assert.Contains("Localizer.Text(\"GamepadListenInstruction\")", mainCode);
        Assert.Contains("Localizer.Text(\"GamepadReleaseAllButtons\")", mainCode);
        Assert.Contains("Localizer.Text(\"UseAnyway\")", mainCode);
        Assert.Contains("Localizer.Text(\"Retry\")", mainCode);
        Assert.Contains("Localizer.Text(\"ReplaceOriginalBinding\")", mainCode);
        Assert.Contains("GamepadCaptureState.SingleButtonWarning", mainCode);
        Assert.Contains("GamepadCaptureState.Conflict", mainCode);
    }

    [Fact]
    public void HotkeyConfirmationPanelCanReceiveClicksBeforeOutsideCancelRuns()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string header = ExtractHotkeyNavigationCard(mainXaml);
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
        Assert.Contains("title: Localizer.Text(\"KeyboardHotkeyEditTitle\")", mainCode);
        Assert.Contains("primaryAction: Localizer.Text(\"ReplaceOriginalBinding\")", mainCode);
        Assert.Contains("secondaryAction: Localizer.Text(\"Cancel\")", mainCode);
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
        Assert.Contains("ShowWpfSettingsPreview(animateScale: false)", handler);
        Assert.Contains("QueueOverlaySettingsAutoSave(debounce: false)", handler);
    }

    [Fact]
    public void GeneralPageOwnsLanguageAndCloseToTraySettings()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());
        string currentPage = ExtractBetween(mainXaml, "x:Name=\"CurrentNavigationCard\"", "x:Name=\"OverlayNavigationCard\"");
        string generalPage = ExtractBetween(mainXaml, "x:Name=\"GeneralNavigationCard\"", "</TabItem>");

        Assert.DoesNotContain("Settings.CloseToTray", currentPage);
        Assert.DoesNotContain("Settings.ShowTitle", currentPage);
        Assert.DoesNotContain("Settings.ShowArtist", currentPage);
        Assert.Contains("x:Name=\"LanguageCombo\"", generalPage);
        Assert.Contains("[Language]", generalPage);
        Assert.Contains("[WindowBehavior]", generalPage);
        Assert.Contains("Settings.CloseToTray", generalPage);
        Assert.Contains("GeneralSetting_Changed", generalPage);
        Assert.Contains("LanguageCombo_SelectionChanged", mainCode);
        Assert.Contains("ShowGeneralNavigationPrompt(\"GeneralLanguageChangedTitle\"", mainCode);
        Assert.Contains("ShowGeneralNavigationPrompt(\"GeneralSavedTitle\"", mainCode);
        Assert.DoesNotContain("SetStatus(\"设置已保存", mainCode);
        Assert.DoesNotContain("SetLocalizedStatus(\"GeneralSaved", mainCode);
    }

    [Fact]
    public void OverlayTitleAndArtistTogglesOnlyAppearOnOverlayPage()
    {
        string mainXaml = File.ReadAllText(GetMainWindowXamlPath());
        string currentPage = ExtractBetween(mainXaml, "x:Name=\"CurrentNavigationCard\"", "x:Name=\"OverlayNavigationCard\"");
        string overlayPage = ExtractBetween(mainXaml, "x:Name=\"OverlayNavigationCard\"", "x:Name=\"HotkeyNavigationCard\"");

        Assert.DoesNotContain("Settings.ShowTitle", currentPage);
        Assert.DoesNotContain("Settings.ShowArtist", currentPage);
        Assert.Equal(1, CountOccurrences(mainXaml, "Settings.ShowTitle"));
        Assert.Equal(1, CountOccurrences(mainXaml, "Settings.ShowArtist"));
        Assert.Contains("[ShowTitle]", overlayPage);
        Assert.Contains("[ShowArtist]", overlayPage);
    }

    [Fact]
    public void OverlayPositionEditReportsCompletionToNavigationPromptWithoutReplacingOverlayUi()
    {
        string overlayCode = File.ReadAllText(GetOverlayWindowCodeBehindPath());
        string mainCode = File.ReadAllText(GetMainWindowCodeBehindPath());

        Assert.Contains("public enum OverlayPositionEditResult", overlayCode);
        Assert.Contains("Action<OverlayPositionEditResult>? positionEditCompleted", overlayCode);
        Assert.Contains("_positionEditCompletedCallback", overlayCode);
        Assert.Contains("completedCallback?.Invoke(editResult.Value)", overlayCode);
        Assert.Contains("OverlayPositionEditResult.Saved", overlayCode);
        Assert.Contains("OverlayPositionEditResult.Cancelled", overlayCode);
        Assert.Contains("PositionEditBar.Visibility = Visibility.Visible", overlayCode);
        Assert.Contains("HandleOverlayPositionEditCompleted", mainCode);
        Assert.Contains("OverlayPositionEditResult.Saved", mainCode);
        Assert.Contains("OverlayPositionCancelledTitle", mainCode);
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

        Assert.Contains("TrayOpen", trayCode);
        Assert.Contains("TrayShowOverlay", trayCode);
        Assert.Contains("TrayExit", trayCode);
        Assert.Contains("UpdateText", trayCode);
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

    private static string ExtractHotkeyNavigationCard(string xaml)
    {
        return ExtractBetween(xaml, "<views:ExpandableNavigationCard x:Name=\"HotkeyNavigationCard\"", "</views:ExpandableNavigationCard>");
    }

    private static string GetExpandableNavigationCardPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Views", "ExpandableNavigationCard.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate ExpandableNavigationCard.cs from the test output directory.");
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

    private static string GetLocalizationServicePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Services", "LocalizationService.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate LocalizationService.cs from the test output directory.");
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

    private static string GetLayeredOverlayWindowPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "AppleMusicOverlay", "Views", "LayeredOverlayWindow.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate LayeredOverlayWindow.cs from the test output directory.");
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
