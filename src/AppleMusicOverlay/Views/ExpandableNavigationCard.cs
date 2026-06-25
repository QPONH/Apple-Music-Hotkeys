using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AppleMusicOverlay.Views;

[TemplatePart(Name = ExpansionHostPartName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = ExpansionContentHostPartName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = ExpansionContentPresenterPartName, Type = typeof(ContentPresenter))]
[TemplatePart(Name = ExpansionScalePartName, Type = typeof(ScaleTransform))]
[TemplatePart(Name = ExpansionTranslatePartName, Type = typeof(TranslateTransform))]
public sealed class ExpandableNavigationCard : HeaderedContentControl
{
    private const string ExpansionHostPartName = "ExpansionHost";
    private const string ExpansionContentHostPartName = "ExpansionContentHost";
    private const string ExpansionContentPresenterPartName = "ExpansionContentPresenter";
    private const string ExpansionScalePartName = "ExpansionScale";
    private const string ExpansionTranslatePartName = "ExpansionTranslate";

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(
            nameof(IsExpanded),
            typeof(bool),
            typeof(ExpandableNavigationCard),
            new PropertyMetadata(false, OnIsExpandedChanged));

    public static readonly DependencyProperty ExpansionTitleProperty =
        DependencyProperty.Register(
            nameof(ExpansionTitle),
            typeof(string),
            typeof(ExpandableNavigationCard),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ExpansionMessageProperty =
        DependencyProperty.Register(
            nameof(ExpansionMessage),
            typeof(string),
            typeof(ExpandableNavigationCard),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsPromptPersistentProperty =
        DependencyProperty.Register(
            nameof(IsPromptPersistent),
            typeof(bool),
            typeof(ExpandableNavigationCard),
            new PropertyMetadata(false));

    public static readonly DependencyProperty AutoCollapseDelayProperty =
        DependencyProperty.Register(
            nameof(AutoCollapseDelay),
            typeof(TimeSpan),
            typeof(ExpandableNavigationCard),
            new PropertyMetadata(TimeSpan.FromMilliseconds(900)));

    private readonly DispatcherTimer _autoCollapseTimer = new();
    private FrameworkElement? _expansionHost;
    private FrameworkElement? _expansionContentHost;
    private ContentPresenter? _expansionContentPresenter;
    private ScaleTransform? _expansionScale;
    private TranslateTransform? _expansionTranslate;
    private bool _isExpansionVisible;

    public event EventHandler? ExpansionCollapsed;

    public ExpandableNavigationCard()
    {
        _autoCollapseTimer.Tick += (_, _) =>
        {
            _autoCollapseTimer.Stop();
            if (!IsPromptPersistent)
            {
                ClearPrompt();
            }
        };
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public string ExpansionTitle
    {
        get => (string)GetValue(ExpansionTitleProperty);
        set => SetValue(ExpansionTitleProperty, value);
    }

    public string ExpansionMessage
    {
        get => (string)GetValue(ExpansionMessageProperty);
        set => SetValue(ExpansionMessageProperty, value);
    }

    public bool IsPromptPersistent
    {
        get => (bool)GetValue(IsPromptPersistentProperty);
        set => SetValue(IsPromptPersistentProperty, value);
    }

    public TimeSpan AutoCollapseDelay
    {
        get => (TimeSpan)GetValue(AutoCollapseDelayProperty);
        set => SetValue(AutoCollapseDelayProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _expansionHost = GetTemplateChild(ExpansionHostPartName) as FrameworkElement;
        _expansionContentHost = GetTemplateChild(ExpansionContentHostPartName) as FrameworkElement;
        _expansionContentPresenter = GetTemplateChild(ExpansionContentPresenterPartName) as ContentPresenter;
        _expansionScale = GetTemplateChild(ExpansionScalePartName) as ScaleTransform;
        _expansionTranslate = GetTemplateChild(ExpansionTranslatePartName) as TranslateTransform;

        BeginExpansionAnimation(IsExpanded);
    }

    public void RefreshExpandedContentHeight()
    {
        if (IsExpanded)
        {
            BeginExpansionAnimation(show: true);
        }
    }

    public void ShowPrompt(string title, string message, TimeSpan autoCollapseDelay)
    {
        _autoCollapseTimer.Stop();
        ExpansionTitle = title;
        ExpansionMessage = message;
        IsPromptPersistent = false;
        AutoCollapseDelay = autoCollapseDelay;
        IsExpanded = true;
        RefreshExpandedContentHeight();
        _autoCollapseTimer.Interval = autoCollapseDelay;
        _autoCollapseTimer.Start();
    }

    public void ShowPersistentPrompt(string title, string message)
    {
        _autoCollapseTimer.Stop();
        ExpansionTitle = title;
        ExpansionMessage = message;
        IsPromptPersistent = true;
        IsExpanded = true;
        RefreshExpandedContentHeight();
    }

    public void ClearPrompt()
    {
        _autoCollapseTimer.Stop();
        IsPromptPersistent = false;
        IsExpanded = false;
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ExpandableNavigationCard)d).BeginExpansionAnimation((bool)e.NewValue);
    }

    private void BeginExpansionAnimation(bool show)
    {
        if (_expansionHost == null ||
            _expansionContentHost == null ||
            _expansionScale == null ||
            _expansionTranslate == null)
        {
            return;
        }

        bool wasVisible = _expansionHost.Visibility == Visibility.Visible && _isExpansionVisible;
        _isExpansionVisible = show;

        Duration duration = new(TimeSpan.FromMilliseconds(show ? 220 : 150));
        IEasingFunction easing = new CubicEase
        {
            EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        if (show)
        {
            _expansionHost.Visibility = Visibility.Visible;
            double measureWidth = _expansionHost.ActualWidth > 0 ? _expansionHost.ActualWidth : 152;
            _expansionContentHost.Measure(new Size(measureWidth, double.PositiveInfinity));
        }

        double startHeight = wasVisible ? Math.Max(_expansionHost.ActualHeight, _expansionHost.MaxHeight) : 0;
        double targetHeight = show ? Math.Ceiling(_expansionContentHost.DesiredSize.Height) : 0;
        if (show && !wasVisible)
        {
            _expansionHost.MaxHeight = startHeight;
        }

        DoubleAnimation heightAnimation = new(startHeight, targetHeight, duration)
        {
            EasingFunction = easing
        };
        DoubleAnimation opacityAnimation = new(show ? 1 : 0, duration)
        {
            EasingFunction = easing
        };

        if (!show)
        {
            heightAnimation.Completed += (_, _) =>
            {
                if (!_isExpansionVisible)
                {
                    _expansionHost.Visibility = Visibility.Collapsed;
                    _expansionHost.MaxHeight = 0;
                    ExpansionCollapsed?.Invoke(this, EventArgs.Empty);
                }
            };
        }

        _expansionHost.BeginAnimation(FrameworkElement.MaxHeightProperty, heightAnimation);
        _expansionHost.BeginAnimation(OpacityProperty, opacityAnimation);
        _expansionScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = easing });
        _expansionScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(show ? 1 : 0.88, duration) { EasingFunction = easing });
        _expansionTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(show ? 0 : -8, duration) { EasingFunction = easing });
    }
}
