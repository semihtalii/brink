using System.IO;
using System.Net.Http;
using System.Text.Json;
using static Brink.L10n;

namespace Brink;

/// Reads Claude Code's OAuth access token (%USERPROFILE%\.claude\.credentials.json —
/// on Windows Claude Code stores credentials in this file, no keychain involved)
/// and queries Anthropic's usage endpoint — the same request Claude Code itself
/// makes for `/usage`.
///
/// Brink never refreshes tokens itself: doing so with Claude Code's refresh token
/// could rotate it and log the user out. When the access token expires we simply
/// re-read Claude Code's store (Claude Code keeps it fresh whenever it runs).
///
/// The usage endpoint rate-limits fairly easily; on a 429 we back off for the
/// `Retry-After` window and keep serving the last good snapshot.
public class ClaudeProvider : IUsageProvider
{
    public string Id => "claude";

    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private record Credentials(string AccessToken, DateTime? ExpiresAt)
    {
        public bool IsExpired => ExpiresAt is DateTime e && (e - DateTime.UtcNow).TotalSeconds < 30;
    }

    private static ProviderSnapshot? _cache;
    private static DateTime? _cooldownUntil;
    private static Credentials? _memoryCache;

    public async Task<ProviderSnapshot> FetchAsync()
    {
        if (_cooldownUntil is DateTime until && DateTime.Now < until)
            return RateLimitedSnapshot(until);

        var snap = NewSnapshot();
        var creds = LoadCredentials();
        if (creds == null)
        {
            var demo = DemoSnapshot("Claude", L("Claude Code credentials not found"));
            demo.Accent = UsageColor.ClaudeOrange;
            return demo;
        }

        try
        {
            var activeToken = creds.AccessToken;
            var (data, status, retryAfter) = await RequestUsageAsync(activeToken);
            if (status == 401)
            {
                // Claude Code rotated its token: re-read its store and retry once.
                _memoryCache = null;
                var fresh = LoadCredentials();
                if (fresh != null && fresh.AccessToken != creds.AccessToken)
                {
                    activeToken = fresh.AccessToken;
                    (data, status, retryAfter) = await RequestUsageAsync(activeToken);
                }
            }
            if (status == 401)
            {
                snap.Error = L("Unauthorized — open Claude Code once to refresh login");
                return snap;
            }
            if (status == 429)
            {
                // The window is short (often "Retry-After: 0"): quietly try again
                // twice within ~5 s before showing anything. Only if that fails do
                // we back off for the Retry-After window (min 30 s) and say so.
                foreach (var delay in new[] { 2, 3 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                    (data, status, retryAfter) = await RequestUsageAsync(activeToken);
                    if (status != 429) break;
                }
            }
            if (status == 429)
            {
                var wait = retryAfter ?? TimeSpan.FromSeconds(60);
                if (wait < TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
                var cooldown = DateTime.Now + wait;
                _cooldownUntil = cooldown;
                return RateLimitedSnapshot(cooldown);
            }
            if (status != 200)
            {
                snap.Error = L("HTTP %d", status);
                return snap;
            }
            snap.Windows = ParseUsage(data);
            snap.UpdatedAt = DateTime.Now;
            if (snap.Windows.Count == 0) snap.Error = L("No usage data in response");
            _cooldownUntil = null;
            _cache = snap;
            return snap;
        }
        catch (Exception e)
        {
            snap.Error = e.Message;
            return snap;
        }
    }

    private static ProviderSnapshot NewSnapshot() => new()
    {
        Id = "claude",
        Name = "Claude",
        Accent = UsageColor.ClaudeOrange,
    };

    /// While rate-limited, prefer the last good snapshot over a blank ring.
    private static ProviderSnapshot RateLimitedSnapshot(DateTime retryAt)
    {
        int waitMin = Math.Max(1, (int)((retryAt - DateTime.Now).TotalMinutes));
        if (_cache != null)
        {
            _cache.Error = L("Rate limited — showing cached usage, retrying in ~%d min", waitMin);
            return _cache;
        }
        var snap = NewSnapshot();
        snap.Error = L("Rate limited (429) — retrying in ~%d min", waitMin);
        return snap;
    }

    private static async Task<(string Data, int Status, TimeSpan? RetryAfter)> RequestUsageAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", "Brink/1.0");
        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        TimeSpan? retryAfter = null;
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is TimeSpan delta) retryAfter = delta;
        else if (ra?.Date is DateTimeOffset date) retryAfter = date - DateTimeOffset.Now;
        if (retryAfter is TimeSpan t && t < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
        return (body, (int)response.StatusCode, retryAfter);
    }

    // MARK: Parsing

    /// Newer responses carry a structured `limits` array:
    /// `{kind: session|weekly_all|weekly_scoped, percent, resets_at, scope:{model:{display_name}}}`.
    /// Older responses only have `five_hour`, `seven_day`, `seven_day_sonnet`,
    /// `seven_day_opus` dicts with `utilization` (0-100) and `resets_at`.
    public static List<UsageWindow> ParseUsage(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new();
            if (root.TryGetProperty("limits", out var limits))
            {
                var fromLimits = ParseLimitsArray(limits);
                if (fromLimits.Count > 0) return fromLimits;
            }
            return ParseLegacyWindows(root);
        }
        catch { return new(); }
    }

    private static List<UsageWindow> ParseLimitsArray(JsonElement limits)
    {
        var windows = new List<UsageWindow>();
        if (limits.ValueKind != JsonValueKind.Array) return windows;
        foreach (var item in limits.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var kind = GetString(item, "kind");
            var pct = GetNumber(item, "percent");
            if (kind == null || pct == null) continue;
            var resets = GetIsoDate(item, "resets_at");
            string label;
            switch (kind)
            {
                case "session": label = "5-hour limit"; break;
                case "weekly_all": label = "All models"; break;
                case "weekly_scoped":
                    label = GetNestedString(item, "scope", "model", "display_name")
                        ?? GetNestedString(item, "scope", "surface", "display_name")
                        ?? "Scoped";
                    break;
                default:
                    // Unknown kind — still surface it rather than silently drop.
                    label = System.Globalization.CultureInfo.InvariantCulture.TextInfo
                        .ToTitleCase(kind.Replace('_', ' '));
                    break;
            }
            if (windows.Any(w => w.Label == label)) continue;
            windows.Add(new UsageWindow { Label = label, UsedPercent = pct.Value, ResetsAt = resets });
        }
        return windows;
    }

    private static List<UsageWindow> ParseLegacyWindows(JsonElement root)
    {
        var windows = new List<UsageWindow>();
        (string Key, string Label)[] ordered =
        {
            ("five_hour", "5-hour limit"),
            ("seven_day", "All models"),
            ("seven_day_sonnet", "Sonnet"),
            ("seven_day_opus", "Opus"),
        };
        foreach (var (key, label) in ordered)
        {
            if (!root.TryGetProperty(key, out var dict) || dict.ValueKind != JsonValueKind.Object) continue;
            var pct = GetNumber(dict, "utilization");
            if (pct == null) continue;
            windows.Add(new UsageWindow
            {
                Label = label,
                UsedPercent = pct.Value,
                ResetsAt = GetIsoDate(dict, "resets_at"),
            });
        }
        return windows;
    }

    // MARK: JSON helpers (shared with CodexProvider)

    public static string? GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static double? GetNumber(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null,
        };
    }

    public static DateTime? GetIsoDate(JsonElement obj, string key)
    {
        var s = GetString(obj, key);
        if (s == null) return null;
        return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d.LocalDateTime : null;
    }

    private static string? GetNestedString(JsonElement obj, params string[] path)
    {
        var current = obj;
        for (int i = 0; i < path.Length - 1; i++)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(path[i], out current))
                return null;
        }
        return GetString(current, path[^1]);
    }

    // MARK: Credentials

    /// Lookup order mirrors the macOS app:
    ///  1) In-memory cache (no I/O while the app is running)
    ///  2) %USERPROFILE%\.claude\.credentials.json (file store used by the CLI)
    ///  3) Windows Credential Manager entry "Claude Code-credentials"
    ///     (the Windows counterpart of the macOS Keychain item).
    /// Expired tokens are discarded so Claude Code's fresher copy is picked up.
    private static Credentials? LoadCredentials()
    {
        if (_memoryCache is { IsExpired: false }) return _memoryCache;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var file = Path.Combine(home, ".claude", ".credentials.json");
        try
        {
            if (File.Exists(file))
            {
                var creds = ParseCredentialsJson(File.ReadAllText(file));
                if (creds is { IsExpired: false })
                {
                    _memoryCache = creds;
                    return creds;
                }
            }
        }
        catch { }

        foreach (var target in new[] { "Claude Code-credentials", "Claude Code" })
        {
            var json = CredentialManager.ReadGeneric(target);
            if (json == null) continue;
            var creds = ParseCredentialsJson(json);
            if (creds is { IsExpired: false })
            {
                _memoryCache = creds;
                return creds;
            }
        }
        return null;
    }

    private static Credentials? ParseCredentialsJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            var token = GetString(oauth, "accessToken");
            if (token == null) return null;
            DateTime? expires = null;
            if (GetNumber(oauth, "expiresAt") is double ms)
                expires = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime;
            return new Credentials(token, expires);
        }
        catch { return null; }
    }

    // MARK: Demo fallback

    public static ProviderSnapshot DemoSnapshot(string name, string note) => new()
    {
        Id = name.ToLowerInvariant(),
        Name = name,
        Windows = new()
        {
            new UsageWindow { Label = "5-hour limit", UsedPercent = 73, ResetsAt = DateTime.Now.AddMinutes(51) },
            new UsageWindow { Label = "All models", UsedPercent = 7, ResetsAt = DateTime.Now.AddHours(36) },
            new UsageWindow { Label = "Fable", UsedPercent = 4, ResetsAt = DateTime.Now.AddHours(36) },
        },
        Error = note,
        IsDemo = true,
        UpdatedAt = DateTime.Now,
    };
}
