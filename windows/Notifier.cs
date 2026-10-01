using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;
using static Brink.L10n;

namespace Brink;

/// Limit notifications (Windows toasts).
///
/// For every usage window of every provider:
///  • when it fills up (≥ 100 %) → "limit reached, resets at …, I'll let you
///    know" — and a second toast is *scheduled* for the reset time, so it
///    arrives on the minute even if the app is idle;
///  • when the window is observed open again → "reset, full limit available"
///    (and any pending scheduled toast for it is cancelled).
///
/// Nothing fires while the toggle is off; state is tracked per (provider, window).
public class Notifier
{
    public static readonly Notifier Shared = new();

    private readonly HashSet<string> _full = new();            // keys currently at 100 %
    private readonly HashSet<string> _scheduledResets = new();

    private Notifier() { }

    public bool IsEnabled
    {
        get => Settings.Shared.NotificationsEnabled;
        set
        {
            Settings.Shared.NotificationsEnabled = value;
            Settings.Shared.Save();
            if (!value) CancelAll();
        }
    }

    /// Menu → "Test notification": shows the two messages a user will get.
    public void SendTest()
    {
        var when = " " + L("Resets in %d min", 51).LowercasedFirst() + ".";
        Post(L("%@ — %@ limit reached", "Claude", NotificationName("5-hour limit")),
             L("You've used 100 %%.%@ I'll let you know when it resets.", when));
        Schedule("test-reset",
             L("%@ — %@ reset", "Claude", NotificationName("5-hour limit")),
             L("Your full limit is available again."),
             DateTime.Now.AddSeconds(6));
    }

    // MARK: Observation (called after every refresh)

    public void Observe(List<ProviderSnapshot> snapshots)
    {
        if (!IsEnabled) return;
        var now = DateTime.Now;
        var hidden = Settings.Shared.HiddenProviders;
        foreach (var snap in snapshots.Where(s => !s.IsDemo && !hidden.Contains(s.Id)))
        {
            foreach (var window in snap.Windows)
            {
                var key = $"{snap.Id}|{window.Label}";
                bool isFull = window.UsedPercent >= 99.5
                    && (window.ResetsAt is not DateTime r || r > now);

                if (isFull && !_full.Contains(key))
                {
                    _full.Add(key);
                    NotifyFull(snap, window);
                    ScheduleReset(snap, window, key);
                }
                else if (!isFull && _full.Contains(key))
                {
                    _full.Remove(key);
                    // If the scheduled one already fired at reset time, don't repeat it.
                    if (_scheduledResets.Contains(key) && window.ResetsAt is DateTime rr && rr <= now)
                    {
                        _scheduledResets.Remove(key);
                        continue;
                    }
                    CancelScheduled(key);
                    NotifyReset(snap, window);
                }
            }
        }
    }

    // MARK: Messages

    /// Notification titles read "<name> limit reached", so the card labels that
    /// already end in "limit" are shortened ("5-hour limit" -> "5-hour").
    private static string NotificationName(string label) => label switch
    {
        "5-hour limit" => L("5-hour"),
        "Weekly limit" => L("Weekly"),
        _ => L(label),
    };

    private void NotifyFull(ProviderSnapshot snap, UsageWindow w)
    {
        var when = w.ResetText is string t ? " " + t.LowercasedFirst() + "." : "";
        Post(L("%@ — %@ limit reached", snap.Name, NotificationName(w.Label)),
             L("You've used 100 %%.%@ I'll let you know when it resets.", when));
    }

    private void NotifyReset(ProviderSnapshot snap, UsageWindow w)
    {
        Post(L("%@ — %@ reset", snap.Name, NotificationName(w.Label)),
             L("Your full limit is available again."));
    }

    private void ScheduleReset(ProviderSnapshot snap, UsageWindow w, string key)
    {
        if (w.ResetsAt is not DateTime resetsAt) return;
        var fireAt = resetsAt.AddSeconds(20);   // small grace for clock skew
        if (fireAt <= DateTime.Now.AddSeconds(1)) return;
        Schedule(ToastId(key),
            L("%@ — %@ reset", snap.Name, NotificationName(w.Label)),
            L("Your full limit is available again."),
            fireAt);
        _scheduledResets.Add(key);
    }

    private void CancelScheduled(string key)
    {
        _scheduledResets.Remove(key);
        RemoveScheduled(ToastId(key));
    }

    private void CancelAll()
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            foreach (var toast in notifier.GetScheduledToastNotifications())
                notifier.RemoveFromSchedule(toast);
        }
        catch { }
        _scheduledResets.Clear();
    }

    // MARK: Toast plumbing

    /// Scheduled-toast ids are capped at 16 chars, so hash the key.
    private static string ToastId(string key)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in key) { hash ^= c; hash *= 16777619; }
            return "r" + hash.ToString("x8");
        }
    }

    private static void Post(string title, string body)
    {
        try
        {
            new ToastContentBuilder().AddText(title).AddText(body).Show();
        }
        catch { }
    }

    private static void Schedule(string id, string title, string body, DateTime fireAt)
    {
        try
        {
            var content = new ToastContentBuilder().AddText(title).AddText(body).GetToastContent();
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(content.GetContent());
            var toast = new ScheduledToastNotification(xml, fireAt) { Id = id };
            ToastNotificationManagerCompat.CreateToastNotifier().AddToSchedule(toast);
        }
        catch { }
    }

    private static void RemoveScheduled(string id)
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            foreach (var toast in notifier.GetScheduledToastNotifications())
                if (toast.Id == id) notifier.RemoveFromSchedule(toast);
        }
        catch { }
    }
}
