# Changelog

All notable changes to UsageAI are documented here.

The project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.4.0] - 2026-07-27

### Added

- Usage history recorded locally, with a trend sparkline and a burn-rate forecast that projects when a window will be exhausted and whether that lands before its reset.
- Tray notifications when a window crosses a usage threshold or rolls over, suppressed on first observation and rate limited per metric.
- A settings window for the refresh interval, alert thresholds, theme, warning and critical colour points, history and forecast, provider visibility and order, the global hotkey, and the opt-in release check.
- Light, dark, and follow-Windows themes, using the Windows accent colour and reacting to system theme changes at runtime.
- A cached last reading, so the popup shows real values immediately at start-up instead of an empty shell.
- The global hotkey Win+Alt+U, keyboard-navigable dashboard cards, and card actions to copy a provider's sign-in command or open its usage page.
- An opt-in check for newer published releases, plus `--help` and `--version`.
- `USAGEAI_DATA_DIR` for a portable install, and a solution file so the app and tests build together.
- Fixture tests for all three provider parsers, settings validation, history, forecasting, alert behaviour, and version comparison.
- A validation-only GitHub workflow that restores, builds, and tests. It does not publish; releases stay manual.

### Changed

- Providers now report a list of usage metrics instead of a fixed session/weekly pair, so every window a provider exposes is shown.
- The capacity meter now fills with what is remaining, matching the headline percentage instead of opposing it.
- Consumption drives the colour of the headline value, the meter, the card rail, and the tray icon, rather than a four-pixel bar alone.
- The compact and expanded cards share one layout grammar, and the detail line spans the full card width instead of truncating the reset countdown.
- The header now names the most-consumed provider instead of counting connections.
- The tray icon is rendered at the size the shell requests, drops its glyph when too small to read, and uses a translucent track that stays legible on light and dark taskbars.
- Fonts resolve through fallback chains, so Windows 10 no longer silently loses the whole type hierarchy when the Segoe UI Variable and Cascadia faces are absent.
- The window declares per-monitor DPI awareness and scales its custom-painted layout, instead of stretching a 96-DPI layout.
- Refresh scheduling is adaptive: per-provider exponential backoff, `Retry-After` when a provider supplies it, a longer interval while no window is open, and an immediate refresh on resume and unlock.
- The GitHub Copilot client remembers which discovered token worked and skips rejected ones, instead of replaying up to sixteen credentials against GitHub on every refresh.
- The dashboard remembers its position and size between runs, not only for the current session.
- A second launch now activates the running instance instead of exiting silently.
- Removed hosted release automation in favour of local validation and manual GitHub releases.

### Fixed

- GitHub Copilot's third quota is no longer discarded; premium requests, chat, and completions are all shown.
- A failed refresh no longer erases a provider's data. The last good reading is kept and marked stale, with the provider's error alongside it.
- Card contents are exposed to screen readers, which previously saw an empty box because every value is custom-painted.

### Removed

- The unused quota meter control, which also leaked a font handle on every paint, and the vestigial single-provider registry setting.

## [0.3.1] - 2026-07-22

### Fixed

- Reapplied the captured Windows security descriptor after atomically replacing Claude's OAuth credential file, preventing elevated filesystems from changing its owner.

## [0.3.0] - 2026-07-22

### Added

- Optional Claude web-session authentication, tried before OAuth when a session key is explicitly supplied through the UsageAI process environment.
- A no-dependency security regression harness covering secret validation, bounded I/O, minimal child-process environments, Claude organization parsing, OAuth credential ACL preservation, lost-update protection, and cross-process locking.
- A security policy and automated dependency update configuration.

### Security

- Bounded provider HTTP responses, credential files, subprocess output, protocol messages, and token sizes to prevent memory-exhaustion paths.
- Disabled redirects and ambient cookies for provider HTTP clients, enabled certificate revocation checks, and replaced static client identifiers with the running app version.
- Restricted provider subprocesses to absolute executables and minimal allowlisted environments so provider secrets are not inherited accidentally.
- Replaced broad Windows Credential Manager enumeration for Claude with exact credential reads and zeroed copied credential buffers after use.
- Made Claude OAuth refresh persistence atomic across processes, protected against lost updates, and preserved the credential file's ACL, ownership, and EFS state.
- Sanitized provider and CLI errors so raw response bodies, stderr, tokens, and local paths are not surfaced to the UI.
- Disabled unsafe BinaryFormatter compatibility, enabled recommended .NET security analyzers as errors, and made Release builds deterministic without portable debug symbols.
- Reduced the distributable to one executable so application code is not left in a separate dependency file.
- Prevented duplicate interactive UsageAI instances from racing provider refreshes.

### Changed

- Browser cookie stores are never scanned; Claude cookie authentication is explicit, memory-only, and falls back safely to Claude Code OAuth.
- GitHub CLI token fallback is now disabled unless `USAGEAI_ENABLE_GH_TOKEN_FALLBACK=1` is explicitly set.
- Migrated from the maintenance-phase .NET 8 runtime to the active .NET 10 LTS runtime.

## [0.2.0] - 2026-07-22

### Added

- Claude Code account support using its existing OAuth login, with five-hour and weekly limits, resets, plan detection, and optional extra usage.
- GitHub Copilot account support for AI credits or premium requests, chat/completion quotas, plan, login, and monthly resets.
- A compact connected-provider view and a full multi-provider dashboard for Codex, Claude Code, and GitHub Copilot.
- Provider-specific diagnostics with `--diagnose codex`, `--diagnose claude`, and `--diagnose copilot`.
- Provider-specific vector icons and visual status signals throughout the usage views.

### Changed

- Generalized the popup, quota cards, tray icon, and tooltips for multiple usage providers and unlimited quotas.
- Left-clicking the tray icon now opens a compact overview of every connected provider, showing one prioritized metric per provider.
- The tray menu's **Open** action now opens a full dashboard with all providers, metrics, and connection details.
- The full dashboard now behaves as a movable, resizable Windows window and remembers its bounds for the current session.
- Dashboard cards now reflow and repaint continuously while the window is being resized, including scrollbar transitions.
- Refined the interface with a modern graphite palette, clearer hierarchy, responsive sizing, and scrollable dashboard layout.

## [0.1.0] - 2026-07-22

### Added

- Native Windows system-tray app for monitoring Codex usage.
- Five-hour and weekly quota meters, shown when reported by Codex.
- Reset countdowns and available full-reset credit count.
- Dynamic tray icon reflecting the most-used quota window.
- Automatic five-minute refresh and manual refresh controls.
- Optional launch at Windows sign-in.
- Diagnostic mode for validating the local Codex connection.
- Privacy-first access through the existing Codex CLI login, without stored API keys or browser cookies.

### Notes

- The first release is a personal, Codex-only tool rather than a multi-provider CodexBar replacement.
- Codex app-server integration is experimental and isolated in the usage client for easier future updates.
