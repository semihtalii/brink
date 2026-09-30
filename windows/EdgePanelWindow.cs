using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using static Brink.L10n;

namespace Brink;

/// The borderless edge panel: a thin strip hugging the right screen edge that
/// expands into a notch-shaped tab with one usage ring per provider.
/// Mirrors PanelController + PanelRootView/TabView from the macOS app.
public class EdgePanelWindow : Window
{
    // Layout constants (mirror the design mockup)
    public const double TabWidth = 70;        // total width incl. the curve room on the left
    public const double TabBodyInset = 5;
    public const double CurveZone = 49;       // height of each S-curve
    public const double RingSize = 43;
    public const double RingGap = 18;
    public const double RingBlockHeight = 43 + 6 + 16;  // ring + gap + percent label
    public const double TabPadding = 41;
    public const double StripWidth = 6;
    public const double StripHoverWidth = 10;
    public const double StripHeight = 153;
    public const double CollapsedWidth = 14;
    public const double ExpandedWidth = TabWidth + 24;

    private readonly UsageStore _store;
    private readonly DetailWindow _detail;

    private Grid _root = null!;
    private Border _strip = null!;
    private Grid _tab = null!;
    private TranslateTransform _tabOffset = null!;

    private bool _expanded, _menuOpen;
    private DispatcherTimer? _collapseTimer;
    private readonly DispatcherTimer _farAwayTimer;

    private static double TabHeight(int providers) =>
        TabPadding * 2 + providers * RingBlockHeight + Math.Max(providers - 1, 0) * RingGap;

    public EdgePanelWindow(UsageStore store)
    {
        _store = store;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;

        _detail = new DetailWindow(BuildMenu) { HoverChanged = _ => HoverStateChanged() };

        SourceInitialized += (_, _) => WindowHelper.MakeUnfocusablePanel(this);
        _store.Updated += () => Dispatcher.Invoke(Render);

        // Follow the desktop: re-anchor when the resolution / work area changes,
        // re-render when the Windows light/dark preference flips (System theme).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            Dispatcher.Invoke(PositionWindow);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is Microsoft.Win32.UserPreferenceCategory.General
                or Microsoft.Win32.UserPreferenceCategory.Desktop
                or Microsoft.Win32.UserPreferenceCategory.VisualStyle)
                Dispatcher.Invoke(Render);
        };

        Render();
        PositionWindow();

        // Reconcile actual cursor bounds even if a non-activating window misses MouseLeave.
        _farAwayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _farAwayTimer.Tick += (_, _) => HoverStateChanged();
        _farAwayTimer.Start();
    }

    private Palette CurrentPalette => Palette.Resolve(ThemeExtensions.FromKey(Settings.Shared.Theme));
    private List<ProviderSnapshot> VisibleSnapshots => Settings.Shared.Visible(_store.Snapshots);

    // MARK: Geometry

    private void PositionWindow()
    {
        var wa = SystemParameters.WorkArea;
        double h = Math.Max(TabHeight(VisibleSnapshots.Count), StripHeight) + 24;
        Width = _expanded ? ExpandedWidth : CollapsedWidth;
        Height = h;
        Left = wa.Right - Width;
        Top = wa.Top + (wa.Height - h) / 2;
    }

    // MARK: Content

    /// Rebuilds the whole visual tree for the current snapshots/theme/language
    /// and applies the current expanded state without animating.
    public void Render()
    {
        var palette = CurrentPalette;

        _root = new Grid
        {
            // Nearly-invisible but hit-testable, so hover works across the panel.
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
        };
        _root.MouseEnter += (_, _) => HoverStateChanged();
        _root.MouseLeave += (_, _) => HoverStateChanged();
        _root.ContextMenu = BuildMenu();

        // Collapsed strip
        _strip = new Border
        {
            Width = StripWidth,
            Height = StripHeight,
            CornerRadius = new CornerRadius(6, 0, 0, 6),
            Background = new SolidColorBrush(palette.StripTint),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = _expanded ? 0 : 1,
        };
        _strip.MouseEnter += (_, _) => AnimateStripWidth(StripHoverWidth);
        _strip.MouseLeave += (_, _) => AnimateStripWidth(StripWidth);
        _root.Children.Add(_strip);

        // Expanded tab
        _tab = BuildTab(palette);
        _tabOffset = new TranslateTransform(_expanded ? 0 : TabWidth * 1.1, 0);
        _tab.RenderTransform = _tabOffset;
        _tab.Opacity = _expanded ? 1 : 0;
        _tab.IsHitTestVisible = _expanded;
        _root.Children.Add(_tab);

        Content = _root;
        PositionWindow();

        if (_detail.IsShown) _detail.Refresh(VisibleSnapshots);
    }

    private Grid BuildTab(Palette palette)
    {
        var visible = VisibleSnapshots;
        double h = TabHeight(visible.Count);
        var tab = new Grid
        {
            Width = TabWidth,
            Height = h,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        tab.Children.Add(new Path
        {
            Data = NotchGeometry(h),
            Fill = new SolidColorBrush(palette.Tint),
        });

        var rings = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(TabBodyInset, 0, 0, 0),
        };
        for (int i = 0; i < visible.Count; i++)
        {
            var element = BuildRing(visible[i], palette);
            element.Margin = new Thickness(0, 0, 0, i < visible.Count - 1 ? RingGap : 0);
            rings.Children.Add(element);
        }
        tab.Children.Add(rings);
        return tab;
    }

    /// The notch-style tab: flares out to the screen edge with an S-curve at
    /// top and bottom.
    private static Geometry NotchGeometry(double h)
    {
        double s = CurveZone, x0 = TabBodyInset, xw = TabWidth;
        var fig = new PathFigure { StartPoint = new Point(xw, 0), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new BezierSegment(
            new Point(xw, s * 0.62), new Point(x0, s * 0.30), new Point(x0, s), true));
        fig.Segments.Add(new LineSegment(new Point(x0, h - s), true));
        fig.Segments.Add(new BezierSegment(
            new Point(x0, h - s * 0.30), new Point(xw, h - s * 0.62), new Point(xw, h), true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    private FrameworkElement BuildRing(ProviderSnapshot snap, Palette palette)
    {
        bool hasData = snap.Windows.Count > 0;
        double percent = snap.Primary?.UsedPercent ?? 0;

        var ringGrid = new Grid { Width = RingSize, Height = RingSize, Opacity = hasData ? 1 : 0.35 };
        ringGrid.Children.Add(new Ellipse
        {
            Stroke = new SolidColorBrush(palette.Track),
            StrokeThickness = 5,
        });
        if (hasData && snap.Primary is UsageWindow primary)
        {
            ringGrid.Children.Add(new Path
            {
                Data = ArcGeometry(RingSize / 2, RingSize / 2, (RingSize - 5) / 2, primary.Fraction),
                Stroke = new SolidColorBrush(UsageColor.For(snap, percent)),
                StrokeThickness = 5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }
        var icon = Icons.For(snap.Id, 18, palette.Fg);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        ringGrid.Children.Add(icon);

        var scale = new ScaleTransform(1, 1, RingSize / 2, RingSize / 2);
        ringGrid.RenderTransform = scale;

        var label = new TextBlock
        {
            Text = hasData ? $"{Math.Round(percent)}%" : "--",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(palette.Fg),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };

        // A fixed height creates a layout clip that cuts off the scaled ring at the top.
        var block = new StackPanel { Orientation = Orientation.Vertical, MinHeight = RingBlockHeight };
        block.Children.Add(ringGrid);
        block.Children.Add(label);
        block.Background = Brushes.Transparent;

        block.MouseEnter += (_, _) =>
        {
            AnimateScale(scale, 1.08);
            var center = ringGrid.TranslatePoint(new Point(RingSize / 2, RingSize / 2), this);
            _detail.ShowFor(snap, Top + center.Y, CurrentPalette);
        };
        block.MouseLeave += (_, _) => AnimateScale(scale, 1.0);
        return block;
    }

    private static Geometry ArcGeometry(double cx, double cy, double r, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction <= 0.0005) return Geometry.Empty;
        double angle = Math.Min(fraction, 0.9995) * 360.0;
        double rad = (angle - 90) * Math.PI / 180.0;
        var fig = new PathFigure { StartPoint = new Point(cx, cy - r) };
        fig.Segments.Add(new ArcSegment(
            new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad)),
            new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    // MARK: Animations

    private void AnimateStripWidth(double to) =>
        _strip.BeginAnimation(WidthProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

    private static void AnimateScale(ScaleTransform scale, double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    // MARK: Hover / expand logic (mirrors PanelController)

    private bool IsHovered => _menuOpen || WindowHelper.IsCursorOver(this)
        || (_detail.IsShown && WindowHelper.IsCursorOver(_detail));

    private void HoverStateChanged()
    {
        if (IsHovered)
        {
            _detail.CancelHide();
            _collapseTimer?.Stop();
            _collapseTimer = null;
            if (!_expanded) Expand();
        }
        else
        {
            ScheduleCollapse();
        }
    }

    private void Expand()
    {
        _expanded = true;
        PositionWindow();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _tab.IsHitTestVisible = true;
        _tab.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        _tabOffset.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        _strip.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    private void ScheduleCollapse()
    {
        if (_collapseTimer != null || (!_expanded && !_detail.IsShown)) return;
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer?.Stop();
            _collapseTimer = null;
            if (IsHovered) { HoverStateChanged(); return; }
            if (_expanded) Collapse();
            else _detail.HideSoon();
        };
        _collapseTimer.Start();
    }

    private void Collapse()
    {
        _expanded = false;
        _detail.HideSoon();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _tab.IsHitTestVisible = false;
        _tab.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        _tabOffset.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(TabWidth * 1.1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        _strip.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });

        var shrink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        shrink.Tick += (_, _) =>
        {
            shrink.Stop();
            if (!_expanded) PositionWindow();
        };
        shrink.Start();
    }

    // MARK: Settings menu (shared by the edge panel and the detail card)

    public ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        // Keep the panel (and detail card) open while the menu is showing —
        // the popup steals the cursor, which would otherwise schedule a collapse.
        menu.Opened += (_, _) => { _menuOpen = true; HoverStateChanged(); };
        menu.Closed += (_, _) => { _menuOpen = false; HoverStateChanged(); };

        var refresh = new MenuItem { Header = L("Refresh now") };
        refresh.Click += (_, _) => _ = _store.RefreshAllAsync();
        menu.Items.Add(refresh);
        menu.Items.Add(new Separator());

        var providers = new MenuItem { Header = L("Providers") };
        foreach (var snap in _store.Snapshots)
        {
            var item = new MenuItem
            {
                Header = snap.IsDemo ? $"{snap.Name} ({L("DEMO")})" : snap.Name,
                IsCheckable = true,
                IsChecked = Settings.Shared.IsVisible(snap),
                StaysOpenOnClick = true,
            };
            var s = snap;
            item.Click += (_, _) =>
            {
                Settings.Shared.SetVisible(s, item.IsChecked, _store.Snapshots);
                Render();
            };
            providers.Items.Add(item);
        }
        menu.Items.Add(providers);

        var appearance = new MenuItem { Header = L("Appearance") };
        foreach (var theme in new[] { Theme.Black, Theme.System })
        {
            var item = new MenuItem
            {
                Header = theme.Title(),
                IsCheckable = true,
                IsChecked = Settings.Shared.Theme == theme.Key(),
            };
            var t = theme;
            item.Click += (_, _) =>
            {
                Settings.Shared.Theme = t.Key();
                Settings.Shared.Save();
                Render();
            };
            appearance.Items.Add(item);
        }
        menu.Items.Add(appearance);

        var language = new MenuItem { Header = L("Language") };
        var systemDefault = new MenuItem
        {
            Header = L("System default"),
            IsCheckable = true,
            IsChecked = Override == "",
        };
        systemDefault.Click += (_, _) => { Override = ""; Render(); };
        language.Items.Add(systemDefault);
        language.Items.Add(new Separator());
        foreach (var (code, name) in Available)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = Override == code };
            var c = code;
            item.Click += (_, _) => { Override = c; Render(); };
            language.Items.Add(item);
        }
        menu.Items.Add(language);

        var launch = new MenuItem
        {
            Header = L("Launch at login"),
            IsCheckable = true,
            IsChecked = LaunchAtLogin.IsEnabled,
        };
        launch.Click += (_, _) => LaunchAtLogin.Set(launch.IsChecked);
        menu.Items.Add(launch);

        var notifications = new MenuItem
        {
            Header = L("Notifications"),
            IsCheckable = true,
            IsChecked = Notifier.Shared.IsEnabled,
        };
        notifications.Click += (_, _) => Notifier.Shared.IsEnabled = notifications.IsChecked;
        menu.Items.Add(notifications);

        var test = new MenuItem { Header = L("Test notification") };
        test.Click += (_, _) => Notifier.Shared.SendTest();
        menu.Items.Add(test);

        menu.Items.Add(new Separator());
        var quit = new MenuItem { Header = L("Quit Brink") };
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);

        return menu;
    }
}
