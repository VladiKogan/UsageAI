# Changelog

All notable changes to UsageAI are documented here.

The project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.7.3] - 2026-08-16

### Fixed

- Fixed broken editor-extension preview images on Visual Studio Marketplace and Open VSX by resolving
  packaged README assets from the extension's directory in the monorepo.
- Restored automatic Claude usage recovery after its short-lived access token expires. UsageAI now
  invokes the official `claude auth status --json` command with bounded runtime and output, waits for
  Claude Code to refresh its own login, then rereads the credentials; UsageAI still never submits the
  shared refresh token or writes `.credentials.json` itself.

## [0.7.2] - 2026-08-13

### Added

- Added a bounded, read-only Google Antigravity CLI fallback for Gemini in both the desktop app and
  editor extension. UsageAI first tries an existing local Antigravity server, then briefly invokes
  official `agy /usage`, and retains the legacy Gemini CLI path for compatibility.

### Fixed

- Made Claude Code authentication strictly read-only in both the desktop app and editor extension. UsageAI no longer exchanges Claude's shared refresh token or rewrites `.credentials.json`, preventing token-rotation races that could force Claude Code to sign in again.

## [0.7.1] - 2026-08-11

### Added

- The editor Status Bar now shows the session and weekly percentages side by side, with segmented quota meters, the last-updated time, and Open/Refresh actions in its hover card.
- Shared monochrome Codex/OpenAI, Claude, GitHub Copilot, and Gemini brand glyphs for the editor Status Bar and desktop provider cards. The icon fonts are bundled with each app, inherit the active theme, and require no external font installation.

### Changed

- Replaced the desktop app's hand-drawn provider symbols and the editor extension's generic pulse icon with the shared provider glyph set, while retaining state-specific loading and warning icons.
- Regenerated the compact popup and full dashboard previews to show the new provider icons.

### Fixed

- Centered desktop provider logos by their visible vector bounds instead of the font line box, removing the upward offset in both compact and full dashboard cards.

## [0.7.0] - 2026-08-11

### Added

- A pure TypeScript UsageAI editor extension under `extension/`, targeting both VS Code and Antigravity with one VSIX. It contributes a persistent Activity Bar dashboard, Status Bar summary, configurable polling, cached stale readings, and local clients for Codex, Claude Code, GitHub Copilot, Gemini CLI, and Antigravity quota summaries.
- Multi-provider Status Bar selection for the editor extension, with one compact item per selected provider and compatibility with earlier single-provider settings.
- Native Settings UI checkboxes for each editor Status Bar provider, avoiding manual `settings.json` editing.
- Editor settings organized into General, Status Bar, and Usage Levels sections for clearer navigation.
- Extension parser and orchestration tests using Node's built-in test runner, plus validation in the existing non-publishing GitHub workflow.

### Fixed

- The editor extension now uses a dedicated monochrome quota-dial icon for Activity Bar surfaces, instead of flattening the full-colour marketplace PNG into a white square.
- Editor reset countdowns of one day or longer now use days and hours instead of large hour and minute values.
- The editor's Providers display order now also controls the order of selected Status Bar items.

## [0.6.0] - 2026-08-10

### Added

- **Google Gemini Preview Block & Sparkline Curves**: Integrated the Google Gemini provider block and multi-point session reset sparklines into the `--render-preview` pipeline (`UI/PreviewRenderer.cs`), displaying Gemini Models and Claude/GPT Models metrics with realistic usage trend curves.
- **High-Resolution Anti-Aliased Preview Pipeline**: Upgraded `--render-preview` to use high-quality bicubic interpolation and smoothing modes, producing crisp vector-smooth high-resolution preview images (`usageai-preview.png` and `usageai-dashboard-preview.png`).
- **Expanded Regression Test Suite**: Expanded the console harness in `UsageAI.Tests` from 30 to 49 checks (`UsageAI.Tests\CoverageExpansionTests.cs`), adding coverage for refresh orchestration, throttling and backoff, shutdown cancellation, provider HTTP and OAuth flows, the Codex `app-server` protocol, corrupt local-state recovery, provider parser edge cases, custom UI rendering, application lifecycle, preview rendering, and command-line entry points. Two of the checks exercise Windows ACL and EFS behaviour and need an elevated shell; without one the suite reports 47 of 49.
- Test seams on the provider clients, `UpdateChecker`, and `UsageApplicationContext`, which now accept an `HttpClient` plus credential and probe delegates through `internal` constructors so fetch and authentication paths can be driven from tests without network access.

### Fixed

- **Full Dashboard Preview Canvas Geometry**: Fixed `--full` dashboard preview rendering in `UI/PreviewRenderer.cs` to use borderless presentation (`FormBorderStyle = FormBorderStyle.None`) and a wider canvas layout (`940×930`), eliminating system caption bar artifacts, vertical footer cutoff, scrollbars, and metric label truncation.
- Cross-process refresh locks were created under a hard-coded `%LOCALAPPDATA%\UsageAI\locks` path, ignoring `USAGEAI_DATA_DIR`. A portable install pointed at another directory now keeps its lock files with the rest of its local state instead of writing into the roaming profile.
- A shutdown race in `UsageRefreshService.Dispose` where an in-flight refresh could release a semaphore that had already been disposed, throwing during exit.

## [0.5.0] - 2026-07-30

### Added

- Google Gemini provider integration (`gemini`), supporting both local Antigravity IDE language server probe detection and Gemini CLI OAuth authentication (`cloudcode-pa.googleapis.com`).
- Antigravity IDE model grouping in the detailed dashboard view into unified model groups (**Gemini Models** and **Claude and GPT models**), reflecting exact real-time remaining fractions and reset countdowns.
- Antigravity quota-summary integration so the detailed Google Gemini card shows both the 5-hour and weekly windows for **Gemini Models** and **Claude and GPT models**.
- Automatic OAuth client credential resolution with fallback for Gemini CLI token refresh.
- Google Gemini icon painting (4-pointed sparkle star) and brand color theme definitions (Google Sky Blue / Brand Blue).
- Application logo and multi-resolution Windows icon (`Resources\app.ico` and `Resources\logo.png`), integrated into `UsageAI.csproj`, the Inno Setup installer (`installer\UsageAI.iss`), application header UI, and project documentation.
- An Inno Setup installer (`installer\UsageAI.iss`) producing `UsageAI-<version>-Setup.exe`, with a Start Menu shortcut, uninstaller, and an install-time check that silently installs the .NET 10 Desktop Runtime if it's missing. The portable single-file build remains available as `UsageAI-<version>-portable.exe`. Both stay part of the existing manual local-build-and-upload release process; no hosted release automation was added.
- **Glance View Details Button**: Added a dedicated **Details** button in the Glance View footer (`DashboardMode.Compact`) to switch into the full detailed dashboard directly.
- **Responsive Multi-Column Dashboard Layout**: Redesigned the detailed dashboard view into an adaptive multi-column grid that adds or removes columns with the available window width while dynamically resizing provider cards, sparkline graphs, capacity meters, and text bounds.
- **Dynamic Content & Height Filling**: Provider usage cards stretch vertically and horizontally to fill available window dimensions evenly.
- **Native Windows Immersive Dark Mode Headers & Scrollbars**: Integrated native Windows DWM title bar styling (`DWMWA_USE_IMMERSIVE_DARK_MODE`) and UxTheme dark scrollbar styling (`DarkMode_Explorer`) across the dashboard and settings windows via `WindowThemeHelpers.cs`.
- **Dynamic Tray Icon Fill & Color Progression**: Implemented color-coded filled pie sectors (`FillPie`) in the system tray icon, advancing from Emerald Green (<50%), Cyan Blue (50%–71%), Amber Orange (72%–89%), to Vivid Red (≥90%).
- **Primary Session Metric Prioritization**: Configured tray tooltips, tray gauge selection, and popup header summaries to prioritize each provider's active primary session metric (e.g. Claude Code 5-hour limit) for real-time tracking during active coding sessions.
- **Expanded Regression Test Suite**: Expanded the test harness in `UsageAI.Tests` from 26 to 30 checks, adding coverage for primary session metric prioritization, theme usage color fill progression, Gemini credential-file ACL/EFS protection, and PID-bound Antigravity probing.

### Fixed

- Handled omitted `remainingFraction` values in Google Gemini / Antigravity IDE model quota info blocks so model groups with depleted quotas (such as **Claude and GPT models**) remain visible at 100% usage with their reset countdown rather than being discarded.
- **Static Footer Timestamp**: Fixed frozen `"Updated just now"` footer status label by adding a 5-second UI tick timer (`_uiTickTimer`) in `UsagePopupForm.cs` that dynamically re-evaluates relative age against `DateTimeOffset.Now` using provider statuses' `LastUpdated` / `FetchedAt` timestamps.
- **Real-Time Thread-Safe UI Updates**: Fixed thread synchronization in `UsageApplicationContext.cs` using `SynchronizationContext` so background usage updates reliably post to the UI thread even when the popup window handle is hidden or recreated.
- **Window Layout Re-Entrancy Crash**: Fixed re-entrancy layout loops during window resizing by batching control size changes and performing one guarded flow-layout pass afterward.

### Security

- Preserved owner, group, DACL, and EFS metadata when persisting refreshed Gemini OAuth credentials, with stale-file and reparse-point checks before replacement.
- Bound Antigravity authentication tokens to listening ports owned by the process that supplied them, with an ownership recheck immediately before each authenticated request.
- Removed hard-coded live Antigravity CSRF credentials, fixed-port probing, certificate-validation bypass, and live response logging from the test harness; replaced them with a synthetic PID-binding regression check.

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
