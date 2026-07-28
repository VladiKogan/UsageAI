# Changelog

All notable changes to UsageAI are documented here.

The project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- An Inno Setup installer (`installer\UsageAI.iss`) producing `UsageAI-<version>-Setup.exe`, with a Start Menu shortcut, uninstaller, and an install-time check that silently installs the .NET 10 Desktop Runtime if it's missing. The portable single-file build remains available as `UsageAI-<version>-portable.exe`. Both stay part of the existing manual local-build-and-upload release process; no hosted release automation was added.

## [0.4.0] - 2026-07-28

### Added

- Usage history recorded locally, with a trend sparkline and a burn-rate forecast that projects when a window will be exhausted and whether that lands before its reset.
- Tray notifications when a window crosses a usage threshold or rolls over, suppressed on first observation and rate limited per metric.
- A settings window for the refresh interval, alert thresholds, theme, warning and critical colour points, history and forecast, provider visibility and order, tray-icon provider, the global hotkey, and the opt-in release check.
- A persisted tray-icon provider choice. Automatic mode follows the connected provider with the highest usage, while a pinned provider drives the circular gauge and falls back safely if disconnected.
- Light, dark, and follow-Windows themes, using the Windows accent colour and reacting to system theme changes at runtime.
- A cached last reading, so the popup shows real values immediately at start-up instead of an empty shell.
- The global hotkey Win+Alt+U, keyboard-navigable dashboard cards, and card actions to copy a provider's sign-in command or open its usage page.
- An opt-in check for newer published releases, plus `--help` and `--version`.
- `USAGEAI_DATA_DIR` for a portable install, and a solution file so the app and tests build together.
- Thirteen new regression checks, expanding the console harness from 10 to 23 checks across all three provider parsers, settings persistence and scrolling, tray-provider selection, history, snapshots, forecasting, alerts, and version comparison.
- A validation-only GitHub workflow that restores, builds, and tests. It does not publish; releases stay manual.
- Dependabot coverage for GitHub Actions dependencies, in addition to NuGet.

### Changed

- Providers now report a list of usage metrics instead of a fixed session/weekly pair, so every window a provider exposes is shown.
- Codex now exposes account credits and reset credits as metrics, Claude exposes its weekly Opus limit and extra usage, and Copilot retains every quota returned by GitHub.
- Usage meters and headline values now show consumption as the primary signal; remaining capacity is secondary, and provider-specific detail such as Copilot's remaining request count is preserved.
- Consumption drives the colour of the headline value, the meter, the card rail, and the tray icon, rather than a four-pixel bar alone.
- The compact and expanded cards share one layout grammar, and the detail line spans the full card width instead of truncating the reset countdown.
- The header now names the most-consumed provider instead of counting connections.
- The tray icon is rendered at the size the shell requests, drops its glyph when too small to read, and shows a concise selected-provider tooltip containing only the used percentage.
- Fonts resolve through fallback chains, so Windows 10 no longer silently loses the whole type hierarchy when the Segoe UI Variable and Cascadia faces are absent.
- The window declares per-monitor DPI awareness and scales its custom-painted layout, instead of stretching a 96-DPI layout.
- Refresh scheduling is adaptive: per-provider exponential backoff, `Retry-After` when a provider supplies it, a longer interval while no window is open, and an immediate refresh on resume and unlock.
- The GitHub Copilot client remembers which discovered token worked and skips rejected ones, instead of replaying up to sixteen credentials against GitHub on every refresh.
- Provider polling, stale-state retention, backoff, history recording, snapshot caching, and alert orchestration now live in a dedicated refresh service instead of the application context.
- Drawing primitives, preview rendering, reset formatting, typography, and DPI scaling are centralized in shared helpers instead of being duplicated across controls.
- The dashboard remembers its position and size between runs, not only for the current session.
- A second launch now activates the running instance instead of exiting silently.
- Contributor and security documentation now covers local state, the validation-only build workflow, local build commands, manual release policy, and the provider-integration reference.
- The README and checked-in preview were refreshed for the new dashboard, settings, diagnostics, and consumption-first meter design.

### Fixed

- GitHub Copilot's third quota is no longer discarded; premium requests, chat, and completions are all shown.
- A failed refresh no longer erases a provider's data. The last good reading is kept and marked stale, with the provider's error alongside it.
- A zero-usage tray icon now retains a high-contrast ring and provider-coloured center marker instead of fading into the taskbar.
- The settings page now scrolls through its final controls instead of stopping behind the fixed Save/Cancel footer, without introducing horizontal scrolling.
- Card contents are exposed to screen readers, which previously saw an empty box because every value is custom-painted.
- Release builds now pass the enabled recommended analyzers with warnings treated as errors.

### Removed

- The unused quota meter control, which also leaked a font handle on every paint, the vestigial single-provider registry setting, obsolete provider view state, and other dead orchestration and drawing code.

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
