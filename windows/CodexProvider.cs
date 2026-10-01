using System.IO;
using System.Net.Http;
using System.Text.Json;
using static Brink.L10n;

namespace Brink;

/// Reads the Codex CLI's OAuth token (%USERPROFILE%\.codex\auth.json, or
/// %CODEX_HOME%\auth.json) and queries the ChatGPT backend usage endpoint.
public class CodexProvider : IUsageProvider
{
    public string Id => "codex";

    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private record Auth(string AccessToken, string? AccountId);

    public async Task<ProviderSnapshot> FetchAsync()
    {
        var snap = new ProviderSnapshot { Id = Id, Name = "Codex" };
        var auth = LoadAuth();
        if (auth == null)
            return ClaudeProvider.DemoSnapshot("Codex", L("Codex CLI credentials not found (~/.codex/auth.json)"));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth.AccessToken}");
            if (auth.AccountId != null)
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
            request.Headers.TryAddWithoutValidation("User-Agent", "Brink/1.0");
            using var response = await ClaudeProvider.Http.SendAsync(request);
            var status = (int)response.StatusCode;
            if (status != 200)
            {
                snap.Error = status == 401
                    ? L("Unauthorized — run `codex` once to refresh login")
                    : L("HTTP %d", status);
                return snap;
            }
            var body = await response.Content.ReadAsStringAsync();
            snap.Windows = ParseUsage(body);
            snap.UpdatedAt = DateTime.Now;
            if (snap.Windows.Count == 0) snap.Error = L("No usage data in response");
            return snap;
        }
        catch (Exception e)
        {
            snap.Error = e.Message;
            return snap;
        }
    }

    // MARK: Auth

    private static Auth? LoadAuth()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var file = Path.Combine(home, "auth.json");
        try
        {
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            JsonElement tokens = default;
            bool hasTokens = root.TryGetProperty("tokens", out tokens)
                && tokens.ValueKind == JsonValueKind.Object;
            var token = (hasTokens ? ClaudeProvider.GetString(tokens, "access_token") : null)
                ?? ClaudeProvider.GetString(root, "access_token");
            if (token == null) return null;
            var account = (hasTokens ? ClaudeProvider.GetString(tokens, "account_id") : null)
                ?? ClaudeProvider.GetString(root, "account_id");
            return new Auth(token, account);
        }
        catch { return null; }
    }

    // MARK: Parsing

    /// Expected shape (fields defensively probed):
    /// { "rate_limit": { "primary_window": { "used_percent": 21, "limit_window_seconds": 18000,
    ///                                       "reset_after_seconds": 3600, "reset_at": 1790000000 },
    ///                   "secondary_window": { ... } | null } }
    public static List<UsageWindow> ParseUsage(string data)
    {
        var windows = new List<UsageWindow>();
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return windows;
            JsonElement rateLimit = root;
            if (root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
                rateLimit = rl;
            else if (root.TryGetProperty("rate_limits", out var rls) && rls.ValueKind == JsonValueKind.Object)
                rateLimit = rls;

            AddWindows(windows, rateLimit);
        }
        catch { }
        return windows;
    }

    // The API has two slots (primary/secondary), but which one holds the 5-hour
    // window and which the weekly one depends on the plan: a Pro plan can send
    // only a 7-day primary_window. So the label comes from the window's length.
    private const double SessionSeconds = 5 * 3600;
    private const double WeekSeconds = 7 * 86400;
    private const double DurationTolerance = 0.1;

    private static readonly (string Key, string FallbackLabel)[] Slots =
    {
        ("primary_window", "Current session"),
        ("primary", "Current session"),
        ("secondary_window", "Weekly limit"),
        ("secondary", "Weekly limit"),
    };

    private static void AddWindows(List<UsageWindow> windows, JsonElement rateLimit)
    {
        foreach (var (key, fallback) in Slots)
        {
            if (!rateLimit.TryGetProperty(key, out var dict) || dict.ValueKind != JsonValueKind.Object) continue;
            var pct = ClaudeProvider.GetNumber(dict, "used_percent");
            if (pct == null) continue;
            var label = LabelFor(dict, fallback);
            if (windows.Any(w => w.Label == label)) continue;
            windows.Add(new UsageWindow { Label = label, UsedPercent = pct.Value, ResetsAt = ResetDate(dict) });
        }
    }

    private static string LabelFor(JsonElement dict, string fallback)
    {
        if (ClaudeProvider.GetNumber(dict, "limit_window_seconds") is double seconds)
        {
            if (IsAbout(seconds, SessionSeconds)) return "5-hour limit";
            if (IsAbout(seconds, WeekSeconds)) return "Weekly limit";
        }
        return fallback;
    }

    private static bool IsAbout(double seconds, double target) =>
        Math.Abs(seconds - target) <= target * DurationTolerance;

    private static DateTime? ResetDate(JsonElement dict)
    {
        foreach (var key in new[] { "reset_after_seconds", "resets_in_seconds" })
            if (ClaudeProvider.GetNumber(dict, key) is double s)
                return DateTime.Now.AddSeconds(s);
        foreach (var key in new[] { "reset_at", "resets_at" })
        {
            if (ClaudeProvider.GetIsoDate(dict, key) is DateTime iso)
                return iso;
            if (ClaudeProvider.GetNumber(dict, key) is double epoch)
            {
                // Could be seconds or milliseconds since epoch.
                var seconds = epoch > 10_000_000_000 ? epoch / 1000.0 : epoch;
                // Out-of-range values must not abort parsing of the other windows.
                if (seconds is < 0 or > 253_402_300_799) return null;
                return DateTimeOffset.FromUnixTimeSeconds((long)seconds).LocalDateTime;
            }
        }
        return null;
    }
}
