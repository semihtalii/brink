import Foundation

/// Reads the Codex CLI's OAuth token (~/.codex/auth.json) and queries the
/// ChatGPT backend usage endpoint, as documented by the CodexBar project.
struct CodexProvider: UsageProvider {
    let id = "codex"

    private static let usageURL = URL(string: "https://chatgpt.com/backend-api/wham/usage")!

    func fetch() async -> ProviderSnapshot {
        var snap = ProviderSnapshot(id: id, name: "Codex",
                                    systemImage: "terminal",
                                    windows: [], error: nil)
        guard let auth = Self.loadAuth() else {
            return ClaudeProvider.demoSnapshot(name: "Codex", systemImage: "terminal",
                                               note: L("Codex CLI credentials not found (~/.codex/auth.json)"))
        }

        var request = URLRequest(url: Self.usageURL)
        request.httpMethod = "GET"
        request.setValue("Bearer \(auth.accessToken)", forHTTPHeaderField: "Authorization")
        if let account = auth.accountID {
            request.setValue(account, forHTTPHeaderField: "ChatGPT-Account-Id")
        }
        request.setValue("Brink/1.0", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 15

        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard status == 200 else {
                snap.error = status == 401
                    ? L("Unauthorized — run `codex` once to refresh login")
                    : L("HTTP %d", status)
                return snap
            }
            snap.windows = Self.parseUsage(data)
            snap.updatedAt = Date()
            if snap.windows.isEmpty { snap.error = L("No usage data in response") }
            return snap
        } catch {
            snap.error = error.localizedDescription
            return snap
        }
    }

    // MARK: Auth

    struct Auth {
        var accessToken: String
        var accountID: String?
    }

    static func loadAuth() -> Auth? {
        let home = ProcessInfo.processInfo.environment["CODEX_HOME"].map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        let url = home.appendingPathComponent("auth.json")
        guard let data = try? Data(contentsOf: url),
              let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            return nil
        }
        let tokens = root["tokens"] as? [String: Any]
        guard let token = (tokens?["access_token"] as? String)
                ?? (root["access_token"] as? String) else {
            return nil
        }
        let account = (tokens?["account_id"] as? String) ?? (root["account_id"] as? String)
        return Auth(accessToken: token, accountID: account)
    }

    // MARK: Parsing

    /// Expected shape (fields defensively probed):
    /// { "rate_limit": { "primary_window": { "used_percent": 21, ... },
    ///                   "secondary_window": { "used_percent": 52, ... } } }
    static func parseUsage(_ data: Data) -> [UsageWindow] {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            return []
        }
        let rateLimit = (root["rate_limit"] as? [String: Any])
            ?? (root["rate_limits"] as? [String: Any])
            ?? root
        var windows: [UsageWindow] = []
        let ordered: [(String, String)] = [
            ("primary_window", "Current session"),
            ("primary", "Current session"),
            ("secondary_window", "Weekly"),
            ("secondary", "Weekly"),
        ]
        var seenSlots = Set<String>()
        for (key, fallbackLabel) in ordered {
            guard !seenSlots.contains(fallbackLabel),
                  let dict = rateLimit[key] as? [String: Any],
                  let pct = ClaudeProvider.number(dict["used_percent"]) else { continue }
            // A weekly-only plan can return its weekly quota in primary_window.
            // The slot is not the quota period; use the reported duration when available.
            let label: String
            switch ClaudeProvider.number(dict["limit_window_seconds"]) {
            case 18_000: label = "5-hour"
            case 604_800: label = "Weekly"
            default: label = fallbackLabel
            }
            windows.append(UsageWindow(label: label, usedPercent: pct,
                                       resetsAt: Self.resetDate(dict)))
            seenSlots.insert(fallbackLabel)
        }
        return windows
    }

    static func resetDate(_ dict: [String: Any]) -> Date? {
        // Current Codex responses use reset_at / reset_after_seconds. Keep the
        // older plural field names below for compatibility with previous responses.
        if let epoch = ClaudeProvider.number(dict["reset_at"]) {
            let seconds = epoch > 10_000_000_000 ? epoch / 1000.0 : epoch
            return Date(timeIntervalSince1970: seconds)
        }
        if let seconds = ClaudeProvider.number(dict["reset_after_seconds"]) {
            return Date().addingTimeInterval(seconds)
        }
        if let s = ClaudeProvider.number(dict["resets_in_seconds"]) {
            return Date().addingTimeInterval(s)
        }
        if let iso = ClaudeProvider.isoDate(dict["resets_at"]) {
            return iso
        }
        if let epoch = ClaudeProvider.number(dict["resets_at"]) {
            // Could be seconds or milliseconds since epoch.
            let seconds = epoch > 10_000_000_000 ? epoch / 1000.0 : epoch
            return Date(timeIntervalSince1970: seconds)
        }
        return nil
    }
}
