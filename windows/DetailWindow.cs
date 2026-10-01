using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using static Brink.L10n;

namespace Brink;

/// The detail bubble: a card with an arrow tail pointing at the hovered ring,
/// listing every usage window of that provider. Mirrors DetailBubbleView.
public class DetailWindow : Window
{
    public const double CardWidth = 266;
    public const double CardRadius = 17;
    public const double TailWidth = 14;
    public const double TailHeight = 23;
    public const double TailRoom = 16;
    public const double ShadowPad = 28;

    public Action<bool>? HoverChanged;
    public bool IsShown { get; private set; }

    private readonly Func<ContextMenu> _menuFactory;
    private readonly Canvas _canvas;
    private readonly Border _card;
    private readonly Polygon _tail;
    private string? _currentId;
    private DispatcherTimer? _hideTimer;

    public DetailWindow(Func<ContextMenu> menuFactory)
    {
        _menuFactory = menuFactory;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SourceInitialized += (_, _) => WindowHelper.MakeUnfocusablePanel(this);

        _tail = new Polygon
        {
            Points = new PointCollection
            {
                new Point(0, 0), new Point(TailWidth, TailHeight / 2), new Point(0, TailHeight),
            },
        };

        _card = new Border
        {
            Width = CardWidth,
            CornerRadius = new CornerRadius(CardRadius),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(15, 13, 15, 15),
            Effect = new DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.35,
                Color = Colors.Black,
            },
        };

        _canvas = new Canvas();
        _canvas.Children.Add(_tail);   // behind the body so the overlap is hidden
        _canvas.Children.Add(_card);
        Canvas.SetLeft(_card, ShadowPad);
        Canvas.SetTop(_card, ShadowPad);
        Canvas.SetLeft(_tail, ShadowPad + CardWidth - 1);

        _canvas.MouseEnter += (_, _) => HoverChanged?.Invoke(true);
        _canvas.MouseLeave += (_, _) => HoverChanged?.Invoke(false);

        Content = _canvas;
    }

    private double _lastRingY;
    private List<ProviderSnapshot>? _pendingSnapshots;

    public void ShowFor(ProviderSnapshot snap, double ringScreenY, Palette palette)
    {
        _pendingSnapshots = null;
        CancelHide();
        _lastRingY = ringScreenY;

        _card.Background = new SolidColorBrush(palette.Tint);
        _card.BorderBrush = new SolidColorBrush(palette.CardBorder);
        _tail.Fill = new SolidColorBrush(palette.Tint);
        _card.Child = BuildCard(snap, palette);
        if (_canvas.ContextMenu?.IsOpen != true) _canvas.ContextMenu = _menuFactory();

        // Measure the card for this snapshot.
        _card.Measure(new Size(CardWidth, double.PositiveInfinity));
        double cardHeight = Math.Max(_card.DesiredSize.Height, 100);

        var wa = SystemParameters.WorkArea;
        double cardTop = ringScreenY - 83;
        cardTop = Math.Min(Math.Max(cardTop, wa.Top + 10), wa.Bottom - 10 - cardHeight);
        double tailY = Math.Min(Math.Max(ringScreenY - cardTop, 20), cardHeight - 20);

        Left = wa.Right - EdgePanelWindow.TabWidth - 12 - TailWidth - CardWidth - ShadowPad;
        Top = cardTop - ShadowPad;
        Width = CardWidth + TailRoom + ShadowPad * 2;
        Height = cardHeight + ShadowPad * 2;
        Canvas.SetTop(_tail, ShadowPad + tailY - TailHeight / 2);

        bool fresh = !IsShown;
        _currentId = snap.Id;
        IsShown = true;
        Show();
        if (fresh)
        {
            _canvas.Opacity = 0;
            var slide = new TranslateTransform(14, 0);
            _canvas.RenderTransform = slide;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _canvas.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        }
    }

    public void CancelHide()
    {
        if (!IsShown || _hideTimer == null) return;
        _hideTimer.Stop();
        _hideTimer = null;
        if (_pendingSnapshots is { } snapshots)
        {
            _pendingSnapshots = null;
            Refresh(snapshots);
            if (!IsShown) return;
        }
        _canvas.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// Re-render the open card after a data refresh.
    public void Refresh(List<ProviderSnapshot> snapshots)
    {
        if (!IsShown || _currentId == null) return;
        // A refresh must not cancel a fade, but reentry still needs the latest data.
        if (_hideTimer != null) { _pendingSnapshots = snapshots; return; }
        var snap = snapshots.FirstOrDefault(s => s.Id == _currentId);
        if (snap == null) { HideNow(); return; }
        ShowFor(snap, _lastRingY, Palette.Resolve(ThemeExtensions.FromKey(Settings.Shared.Theme)));
    }

    public void HideSoon()
    {
        if (!IsShown || _hideTimer != null) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _canvas.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer?.Stop();
            _hideTimer = null;
            HideNow();
        };
        _hideTimer.Start();
    }

    private void HideNow()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
        _pendingSnapshots = null;
        IsShown = false;
        _currentId = null;
        Hide();
    }

    // MARK: Card content (mirrors DetailCardContent)

    private static FrameworkElement BuildCard(ProviderSnapshot snap, Palette palette)
    {
        var fg = new SolidColorBrush(palette.Fg);
        var muted = new SolidColorBrush(palette.Muted);
        var soft = new SolidColorBrush(palette.Soft);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        // Header
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 11),
        };
        var icon = Icons.For(snap.Id, 14.5, palette.Fg);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 8, 0);
        header.Children.Add(icon);
        header.Children.Add(new TextBlock
        {
            Text = snap.Id == Settings.PlaceholderId ? "Brink" : L("%@ Usage", snap.Name),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (snap.IsDemo)
        {
            header.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(palette.Track),
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = L("DEMO"),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = fg,
                },
            });
        }
        stack.Children.Add(header);

        if (snap.Windows.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = snap.Error ?? L("No data"),
                FontSize = 12,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            for (int i = 0; i < snap.Windows.Count; i++)
            {
                var window = snap.Windows[i];
                bool last = i == snap.Windows.Count - 1;

                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var resetText = window.ResetText;
                if (resetText != null)
                {
                    var reset = new TextBlock
                    {
                        Text = resetText,
                        FontSize = 10.5,
                        Foreground = muted,
                        VerticalAlignment = VerticalAlignment.Bottom,
                    };
                    DockPanel.SetDock(reset, Dock.Right);
                    row.Children.Add(reset);
                }
                row.Children.Add(new TextBlock
                {
                    Text = L(window.Label),
                    FontSize = 11.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = fg,
                });
                stack.Children.Add(row);

                var barGrid = new Grid { Height = 4.5, Margin = new Thickness(0, 0, 0, 5.5) };
                barGrid.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(palette.BarTrack),
                });
                const double barFullWidth = CardWidth - 30;  // card padding
                barGrid.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(UsageColor.For(snap, window.UsedPercent)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(7, barFullWidth * window.Fraction),
                });
                stack.Children.Add(barGrid);

                stack.Children.Add(new TextBlock
                {
                    Text = L("%d%% Used", (int)Math.Round(window.UsedPercent)),
                    FontSize = 10.5,
                    Foreground = soft,
                    Margin = new Thickness(0, 0, 0, last ? 0 : 12),
                });
            }
        }

        if (snap.Error != null && (snap.Windows.Count > 0 || snap.IsDemo))
        {
            stack.Children.Add(new TextBlock
            {
                Text = snap.Error,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(230, 0xFF, 0xA5, 0x00)),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 32,
                Margin = new Thickness(0, 10, 0, 0),
            });
        }

        return stack;
    }
}
