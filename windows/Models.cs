using System.Windows.Media;
using System.Windows.Threading;

namespace Brink;

// MARK: Data model — mirrors Models.swift

public class UsageWindow
{
    public string Label { get; set; } = "";     // "5-hour limit", "All models"...
    public double UsedPercent { get; set; }     // 0...100
    public DateTime? ResetsAt { get; set; }     // local time

    public double Fraction => Math.Min(Math.Max(UsedPercent / 100.0, 0), 1);

    public string? ResetText
    {
        get
        {
            if (ResetsAt is not DateTime resets) return null;
            var seconds = (resets - DateTime.Now).TotalSeconds;
            if (seconds <= 0) return L10n.L("Resets soon");
            if (seconds < 3600) return L10n.L("Resets in %d min", (int)(seconds / 60));
            if (seconds < 86400)
            {
                int h = (int)(seconds / 3600);
                int m = (int)(seconds % 3600 / 60);
                return m > 0 ? L10n.L("Resets in %d h %d min", h, m) : L10n.L("Resets in %d h", h);
            }
            return L10n.L("Resets %@", resets.ToString("ddd HH:mm", L10n.Culture));
        }
    }
}

public class ProviderSnapshot
{
    public string Id { get; set; } = "";        // "claude", "codex"
    public string Name { get; set; } = "";
    public List<UsageWindow> Windows { get; set; } = new();
    public string? Error { get; set; }
    public bool IsDemo { get; set; }
    public Color? Accent { get; set; }          // fixed brand color; null = percent-based scale
    public DateTime? UpdatedAt { get; set; }

    public UsageWindow? Primary => Windows.Count > 0 ? Windows[0] : null;
}

// MARK: Ring color scale

public static class UsageColor
{
    public static readonly Color ClaudeOrange = Color.FromRgb(0xD9, 0x77, 0x57);

    public static Color For(ProviderSnapshot snapshot, double percent)
        => snapshot.Accent ?? For(percent);

    public static Color For(double percent)
    {
        if (percent < 50.0) return Color.FromRgb(0x2F, 0xD4, 0x87);
        if (percent < 70.0) return Color.FromRgb(0xF2, 0xDF, 0x2A);
        return Color.FromRgb(0xFF, 0x44, 0x00);
    }
}

// MARK: Provider protocol

public interface IUsageProvider
{
    string Id { get; }
    Task<ProviderSnapshot> FetchAsync();
}

// MARK: Store

public class UsageStore
{
    public List<ProviderSnapshot> Snapshots { get; private set; }
    public DateTime? LastRefresh { get; private set; }
    public event Action? Updated;

    private readonly List<IUsageProvider> _providers;
    private DispatcherTimer? _timer;
    private bool _refreshing;

    public UsageStore(IEnumerable<IUsageProvider> providers)
    {
        _providers = providers.ToList();
        Snapshots = _providers.Select(p => new ProviderSnapshot
        {
            Id = p.Id,
            Name = char.ToUpperInvariant(p.Id[0]) + p.Id[1..],
        }).ToList();
    }

    public void StartAutoRefresh(TimeSpan interval)
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => _ = RefreshAllAsync();
        _timer.Start();
        _ = RefreshAllAsync();
    }

    public async Task RefreshAllAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var results = new List<ProviderSnapshot>();
            foreach (var provider in _providers)
                results.Add(await provider.FetchAsync());
            Snapshots = results;
            LastRefresh = DateTime.Now;
            Notifier.Shared.Observe(results);
            Updated?.Invoke();
        }
        finally
        {
            _refreshing = false;
        }
    }
}
