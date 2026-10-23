# Changelog

All notable changes to Brink are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Fixed
- macOS: Codex reset times now read `reset_at` / `reset_after_seconds`, while retaining older field names. Five-hour and weekly quota labels follow `limit_window_seconds`, including weekly-only plans that report their quota in `primary_window`.

## [0.6.0] - 2026-08-31

### Added
- **"What used it"** on the Claude card (macOS): a collapsible breakdown of the current session by project, built by attributing each observed rise in the limit to the turns that caused it. Consumption with no local explanation is shown as **Elsewhere**, never hidden. Reads only token counts from Claude Code's own transcripts — never message content, tool results or attachments — and stores them in a local SQLite database that never leaves the machine.
- Range picker — **Session**, **Week**, **Month**, **All**. The first two are Anthropic's own limit windows and report percent of that limit, matching the card. Month and All have no limit behind them, so they report each project's share of the work in that range; the list says which, under the rows.
- Sessions started in subfolders are grouped by git root, so each repository appears once.
- The rows always add up to the percentage on the card. Attribution can only observe rises while Brink is running, so anything it missed — the Mac asleep, the app closed, a period that began before Brink was ever launched — is spread across the turns recorded in that period instead of quietly going missing.

### Changed
- Providers are now fetched in parallel and each ring updates as its own answer arrives, so one slow or rate-limited source no longer leaves the whole card blank. Every request has a 15 s timeout.
- An open detail card now re-renders when new data arrives instead of showing the values it opened with.
- The Cursor token is cached like Claude's, so the Keychain prompt appears once rather than after every update.

### Note
- The breakdown is macOS only in this release. Windows keeps the rings, card, notifications and languages it already had.

## [0.5.2] - 2026-08-30

### Added
- App icon on both platforms — `.icns` in the macOS bundle, multi-size `.ico` embedded in `Brink.exe`.

## [0.5.1] - 2026-08-30

### Fixed
- **macOS: the app crashed on launch on every Mac except the build machine** (#8). Resources are now shipped in `Contents/Resources` and read via `Bundle.main` instead of SwiftPM's `Bundle.module`, whose accessor only knew the build machine's path. Thanks @nadalpiantini for the precise report.

### Added
- Homebrew tap: `brew tap semihtalii/brink && brew install --cask brink` (cask strips quarantine, no Gatekeeper dance).
- winget manifest under `packaging/winget/` (`SemihTali.Brink`, pending submission to winget-pkgs).
- Releases now ship `SHA256SUMS-*.txt` and a GitHub build-provenance attestation for both the DMG and the Windows zip.
- Issue and pull-request templates.

### Changed
- macOS build is a **universal binary** (arm64 + x86_64); 0.5.0 and earlier were Apple Silicon only.
- README: correct first-launch steps for macOS 15+ (Settings → Privacy & Security → Open Anyway, or `xattr -d`); uninstall notes; comparison table now lists CodexBar and ClaudeBar.
- GitHub release notes are generated from this changelog instead of the commit list.

## [0.5.0] - 2026-08-30

### Added
- **Brink for Windows** — native C# / WPF port in `windows/`, with the same rings, detail card, notifications, 10 languages, launch at login and provider handling. Ships as a single self-contained `Brink.exe` (`Brink-Windows-x64.zip`) built by GitHub Actions on every release.

## [0.4.0] - 2026-08-30

### Added
- **Cursor** provider: included / auto / API usage (and on-demand when enabled) from the Cursor CLI login. Thanks @BryanPinheiro77 (#1).
- Empty state: when no CLI is signed in, a single "?" ring explains what to install instead of showing demo numbers.

### Changed
- Providers without credentials are hidden by default; switch them on in **Providers** to see a DEMO ring.
- Claude: on 429 Brink quietly retries twice within ~5 s before backing off (min 30 s) — no more "Rate limited" note on a fresh launch.

## [0.3.1] - 2026-08-30

### Fixed
- Claude: on HTTP 429 the app now backs off for the `Retry-After` window and keeps showing the last good numbers instead of blanking the ring and re-polling. Thanks @fherryfherry (#2).
- Translations for the new rate-limit messages in all 10 languages.

## [0.3.0] - 2026-08-29

### Added
- **Providers** menu: choose which rings are shown (e.g. Claude only). Hidden providers don't notify either. At least one stays visible.

### Changed
- Detail card tail is now an arrow pointing at the ring (design v3); thicker ring stroke, thinner bars.
- Whole panel ~10 % smaller.

## [0.2.0] - 2026-08-29

### Added
- Notifications: when any usage window (session / weekly / per-model) reaches 100 %, and again when it resets — the reset one is scheduled for the exact reset time. Toggle + test item in the menu.
- Localization in 10 languages, following the macOS system language by default; **Language** menu to override.
- Panel folds away when the mouse moves far (> 480 pt) from the edge.

### Changed
- Liquid Glass / System now use Apple's adaptive `.regular` glass with system text styles, so type flips light/dark with the background.
- Slimmer proportions from the v2 design (78 pt tab, 48 pt rings, 296 pt card); card shadow removed.
- Right-click menu available on the detail card as well.

### Fixed
- Token rotation by Claude Code no longer shows "Unauthorized" until the next cycle — Brink re-reads and retries immediately.
- CI builds on `macos-26` (required for the Liquid Glass SDK).

## [0.1.0] - 2026-08-29

### Added
- Appearance menu: **Black**, **Liquid Glass**, **System** — real Liquid Glass (`.glassEffect`) on macOS 26+, `NSVisualEffectView` blur on older systems; choice persisted.
- Notch-style tab shape, hover-scaling rings, detail card that glides between rings with a crossfade.
- Provider logos as template images.
- Right-click anywhere on the panel or the detail card for the settings menu.
- If Claude Code rotates its token, Brink re-reads it and retries immediately instead of showing "Unauthorized" until the next cycle.
- `BRINK_PREVIEW=1` starts expanded with the first card open (screenshots / design review).
- Right-edge slide-out panel with a usage ring per provider and a detail card on hover.
- Claude Code source: reads the OAuth token from the macOS Keychain (`Claude Code-credentials`) or `~/.claude/.credentials.json`; parses Anthropic's structured `limits` response (session / all models / per-model weekly) with a fallback for the older `five_hour` / `seven_day` shape.
- Codex source: reads `~/.codex/auth.json` and the ChatGPT usage endpoint.
- Auto-refresh every 2 minutes; **Refresh now** and **Quit** in the context menu.
- **Launch at login** toggle (SMAppService).
- `build.sh` producing `Brink.app` and `Brink.dmg`; GitHub Actions release on `v*` tags.

### Security
- Only the short-lived access token and its expiry are cached locally (`0600`). Refresh tokens are never stored and Brink never refreshes tokens itself, so it cannot invalidate Claude Code's session.

[Unreleased]: https://github.com/semihtalii/brink/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/semihtalii/brink/releases/tag/v0.6.0
[0.5.2]: https://github.com/semihtalii/brink/releases/tag/v0.5.2
[0.5.1]: https://github.com/semihtalii/brink/releases/tag/v0.5.1
[0.5.0]: https://github.com/semihtalii/brink/releases/tag/v0.5.0
[0.4.0]: https://github.com/semihtalii/brink/releases/tag/v0.4.0
[0.3.1]: https://github.com/semihtalii/brink/releases/tag/v0.3.1
[0.3.0]: https://github.com/semihtalii/brink/releases/tag/v0.3.0
[0.2.0]: https://github.com/semihtalii/brink/releases/tag/v0.2.0
[0.1.0]: https://github.com/semihtalii/brink/releases/tag/v0.1.0
