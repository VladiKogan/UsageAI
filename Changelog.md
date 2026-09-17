# Changelog

All notable changes to UsageAI are documented here.

The project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- The provider list in Settings can now be reordered by dragging an entry, which is the gesture
  people try first; the Move up and Move down buttons stay, and Alt+Up and Alt+Down do the same
  from the keyboard without leaving the list. The drag rides on the mouse capture the list already
  takes rather than OLE drag-and-drop, because registering a drop target needs an STA thread and
  buys nothing for an entry that never leaves its own list. A `CheckedListBox` refuses every
  owner-draw mode, so the row moves under the cursor instead of a drawn insertion line, which also
  shows the result rather than a promise of it. A press that never passes the system drag threshold
  is still a click and still ticks the box; one that becomes a drag suppresses the tick
  `CheckOnClick` would otherwise apply on button-up, against whichever row is selected by then —
  which after a drag is the entry just moved — so reordering can never quietly show or hide a
  provider. Straying sideways out of the narrow list keeps the entry level with the cursor instead
  of dropping it at the end, a drag that loses the mouse without a button-up is not still armed
  when the next press lands, and Escape abandons a drag in flight and restores the original order,
  claimed before the dialog's Cancel button can close Settings and discard every edit in it.

### Changed

- The What's New window now lays the release notes out as a typed document instead of one
  undifferentiated block of text. Every release gets its own heading and date, the Added, Changed,
  Fixed and Security categories become coloured labels, bullets hang their wrapped lines under the
  text rather than under the marker, and releases, categories and bullets are separated by
  progressively smaller gaps so the structure is visible before a word is read. The dates are
  spelled out the way the changelog writes them rather than through the system long-date format,
  which on a right-to-left locale reversed itself against the version sitting beside it.
- Inline `code` and **strong** spans in a changelog bullet are now given a monospace and a bold face
  instead of being printed with their Markdown delimiters still attached. No Markdown engine is
  involved and nothing is fetched: the bundled changelog is still read through the same bounded
  parser, only the two constructs the changelog actually uses are recognised, and an unpaired or
  empty delimiter stays literal text. Angle brackets are never markup, so HTML in a bullet is still
  shown verbatim.
- The window now says how much it is leaving out. The upgrade summary has always been capped at the
  three newest releases; it reports the number it dropped rather than a bare note that older
  releases exist, and the subheading names how many releases are being shown when there is more than
  one. Windows High Contrast still flattens every category colour to the system foreground, and the
  category name is always spelled out, so colour is never the only cue.
- The desktop suite grew to 76 checks, adding the dashboard grid's scroll behaviour — including
  that no provider card is ever laid out where nothing could reach it — and the whole
  provider-list drag gesture, down to the boundaries that must do nothing: a right-click, a press
  on the empty strip below the last row, and a move that would carry an entry past either end. The
  drag is exercised with real mouse messages rather than by calling the handlers, because the tick
  it has to suppress is applied by the native list box itself on button-up.
  Coverage on the `UsageAI` package is 88.26% line and 82.13% branch, against a gate of 83% and 76%.

### Fixed

- Removed the scrollbar from the full dashboard. Its provider cards are always resized to fill the
  window's client area, so there was never anything below the fold to scroll to, yet a vertical
  scrollbar still appeared at common window sizes with an empty scroll range — at a 760x620 client
  area it showed up with `AutoScrollMinSize` at zero. Cards are built at their natural height and
  only compacted to their grid cell a moment later, and that transient overflow was enough to latch
  the scrollbar, which then cost the grid 49 pixels of width and left a dead strip down the
  right-hand side for the rest of the session. The compact tray popup, which can genuinely
  overflow, still scrolls. With nothing left to scroll the grid also stopped assuming two rows: it
  keeps two columns and takes as many rows as the provider count needs, so the four shipped
  providers still fill the same 2x2 at the same size, and a fifth would earn a third row instead of
  being laid out past the client edge where nothing could reach it. Card widths also no longer
  subtract the scrollbar a second time: `ClientSize` already excludes it, so the deduction only
  narrowed the cards further.

## [0.11.0] - 2026-09-16

### Changed

- Expanded risk-focused coverage for provider parsing, authentication and protocol failures,
  Antigravity hub fallbacks, update metadata validation, font fallbacks, startup registration, and
  single-instance messaging. Desktop coverage is now 88.12% line and 81.76% branch; the editor's 48
  checks reach 79.89% line, 80.10% branch, and 81.41% function coverage.
- Provider readings now reach the cards as each one lands rather than after the slowest provider in
  the refresh returns, so a provider that answered in 0.6 seconds no longer appears to take as long
  as one that needs 4. The tray spinner still turns until the last provider is in, and history, the
  snapshot cache, and alerts are still written once per refresh rather than once per provider.
- Refreshes now wake for a quota window that has reset instead of waiting out the whole refresh
  interval, so the tray stops showing a spent quota long after it rolled over — up to two hours at
  the maximum refresh setting — and reset notifications arrive on time. A window costs at most three
  polls, spaced a minute apart, because a provider whose counters trail its own published reset
  instant needs asking again; a provider serving out a failure backoff keeps it either way.
- Google Gemini now goes straight to a serving `agy` hub instead of running the Antigravity IDE probe
  ahead of it, which is what the editor extension already did. The probe itself no longer starts
  PowerShell to map a process id to its listening ports, reading them through the IP Helper API
  instead: the two discovery steps measured 0.65 seconds and 1.2 seconds per call and now cost about
  10 milliseconds and under a millisecond. Command lines are still read through PowerShell, but only
  when a language server is actually running, and the result is kept for the life of that process.

### Fixed

- Fixed Google Gemini starting an `agy.exe` child process on every refresh. Antigravity CLI builds
  from September 2026 onwards ignore the CSRF token passed through the hub's environment and mint
  their own, so every hub call was rejected as unauthenticated and each refresh fell back to a
  short-lived `agy -p /usage` read, which boots MCP servers through `cmd.exe` and flashes a console
  window. The token is now also supplied as `--csrf_token`, the way the Antigravity IDE provisions
  the language server it starts, which both older and newer builds accept.
- Held off further Antigravity hub starts for 30 minutes after one fails to become ready, so a future
  change to the CLI costs one 20-second attempt per half hour rather than an extra child process and
  a 20-second stall on every poll.
- Cleared the Antigravity hub and `agy` probe backoffs when the user asks for a refresh, so a single
  missed hub start cannot hold a provider on the degraded per-refresh path until the window expires.
  The existing immediate-`agy` retry after both paths go stale now releases the hub backoff too.
- Capped the full dashboard's scaled minimum size to the active monitor's working area so compact
  displays remain usable at 200% and 300% scaling.
- Ignored malformed GitHub release assets whose size is not numeric instead of allowing update
  metadata parsing to fail unexpectedly.
- Fixed a well-formed but wrongly shaped GitHub release payload throwing out of the update check
  instead of reporting that it failed. Reading a property from a JSON array, string, or number raises
  an exception the check does not catch, so a non-object release root or a non-object entry in
  `assets` could surface on the UI thread from the daily timer or the Settings **Check for updates**
  button. Release roots and asset entries are now required to be objects.

## [0.10.0] - 2026-09-15

### Added

- Added `All`, `Metered only`, and `Important only` metric display modes to the full desktop
  dashboard. Metered mode includes every usable percentage limit; Important mode keeps the
  highest-used Session, Rolling, and Monthly metric with stable first-reported tie-breaking.
- Added the metric display preference to Settings and a full-dashboard shortcut that saves and
  rebuilds cards immediately without refreshing providers. Filtering never changes cached snapshots,
  alerts, forecasts, diagnostics, the tray gauge, or compact-popup metric selection.
- Added an explicit **No metered limits reported** state for connected providers whose balance or
  unlimited metrics are hidden by the selected dashboard mode.
- Added one bounded Claude Code OAuth recovery attempt when the first usage request is rejected as
  unauthorized/forbidden or returns unexpected unavailable data. UsageAI asks the official
  `claude auth status --json` command to validate or refresh the login, rereads Claude's credential
  store, and retries usage once; it never exchanges the refresh token or writes credentials itself.
  Explicit `USAGEAI_CLAUDE_OAUTH_TOKEN` overrides remain authoritative and bypass this fallback.
- Added a native **What's new** window backed by the `Changelog.md` embedded in the executable. Real
  upgrades queue up to three newer released versions and open them only after the user's first popup,
  dashboard, or Settings interaction; clean installations remain quiet.
- Added conservative upgrade-version tracking that handles installations predating the setting,
  skipped releases, same-version launches, downgrades, and malformed stored versions without blocking
  startup. A queued version is marked seen only after the release-notes window is displayed.
- Added **What's new** under Settings > About for reopening the installed release notes, plus a
  **View complete changelog** action and a safe fallback when the installed version has no bundled
  section. Runtime parsing ignores `Unreleased`, accepts only released Added/Changed/Fixed/Security
  bullets, and bounds input, versions, bullets, and output.
- Added an **About** section to the desktop settings window that shows the installed UsageAI
  version and provides a manual **Check for updates** action. The check reports whether the app is
  current, an update is available, or GitHub could not be reached, and newer releases continue
  through the existing verified installer flow.
- Added an explicit-DPI preview and construction seam for 96, 192, and 288 DPI, including the
  `--dpi 96|192|288` preview option.
### Changed

- The full desktop dashboard now keeps all four provider cards in a fixed 2×2 grid. Resizing or
  maximizing the window adjusts both card dimensions and their internal metric spacing. Short cards
  use a denser metric-row presentation so the second provider row remains visible while resizing.
- Windows High Contrast now overrides System, Dark, and Light theme choices. Every palette entry is
  derived from `SystemColors`; provider identity remains available through names and glyphs instead
  of brand colors, and tray menus switch to the native system renderer.
- High Contrast changes now apply while UsageAI is running to the popup, full dashboard, Settings,
  What's New, custom-painted cards, scrollbars, menu renderer, and regenerated tray icon.
- Warning and critical quota states now include visible symbolic cues, while stale, disconnected,
  and refreshing states remain stated in text rather than depending on color alone.
- Popup, dashboard, Settings, and What's New layout metrics now reapply during per-monitor DPI
  transitions, preserve practical scroll and logical sizing state, and constrain windows to the
  destination monitor's working area.
- Expanded the desktop regression harness to 70 registered checks. New coverage exercises
  metric filtering and settings isolation, release-note bounds and lifecycle behavior, runtime High
  Contrast propagation, native dialog reopening, successful bitmap painting, responsive 2×2
  dashboard resizing, owned-window/card geometry at 96, 192, and 288 DPI, and bounded Claude CLI
  recovery after authorization and invalid-response failures. The `UsageAI`
  package now measures 86.46% line and 80.16% branch coverage in the coverage gate.

### Fixed

- Replaced the full dashboard's native metric picker chrome with theme-aware drawing so its arrow
  button, menu items, focus border, and selected item no longer turn white in dark mode.
- Fixed Claude Code remaining stale when its access token stopped working before the saved expiry
  time, or when a one-off invalid usage response recovered after Claude CLI validation.
- Fixed explicit-DPI preview rendering so layout and typography scale together independently of the
  host monitor's scale factor.
- Fixed popup, dashboard, and Settings scroll restoration across mixed-DPI monitor transitions by
  preserving the logical position instead of reusing the old physical-pixel offset.
- Fixed the release-notes parser's per-bullet boundary so an oversized first line is truncated to
  the same limit as wrapped continuation text.
- Fixed a disconnected-provider card's copy-sign-in action extending beyond the card's declared
  natural height, which could clip the button or hit target at some display scales.
- Fixed invalid numeric dashboard-mode settings reaching rendering code by normalizing them to
  `All`; invalid string enum JSON continues through the existing safe-load fallback without blocking
  startup.
- Fixed High Contrast rendering paths that could retain translucent brand colors in provider icons,
  balance markers, sparklines, tray gauges, or the UsageAI mark.
- Fixed full-dashboard footer sizing so the metric selector and Settings action remain readable at
  supported scales.
- Registered the desktop tray process with Windows Restart Manager so later in-app updates reopen
  the updated executable automatically after installation. The update that first installs this fix
  may still need one manual launch because the older running process could not register retroactively.

## [0.9.0] - 2026-08-26

### Changed

- Google Gemini now reads quota from one long-lived Antigravity CLI `--hub` server instead of starting
  `agy -p /usage` on every refresh. A running app no longer spawns a child process per poll, and the CLI
  starts any configured MCP servers once per session rather than on every poll, which is what produced
  transient console windows on Windows.

### Removed

- Dropped the Antigravity CLI port-discovery probe and its PowerShell helper. The service rejects the
  request bodies it sent, so the HTTP query never produced a snapshot.

## [0.8.2] - 2026-08-26

### Fixed

- Prevented background Gemini refreshes from preferring the standalone `agy.exe` on `PATH`, forcing
  an interactive terminal, or leaving an owned Antigravity language-server child behind.
- Made a stale Gemini card recover from sleep in one refresh even when an earlier `agy` cold-start
  failure was cached, and replaced obsolete Gemini CLI-only sign-in guidance with the installed
  `agy` command.

## [0.8.1] - 2026-08-26

### Added

- Added Gemini quota discovery through the official Antigravity VS Code extension's locally installed
  `agy` backend, including its current snake-case `/usage` response format, so VS Code does not need
  to remain open after sign-in.
- Added animated refresh-progress indicators across the desktop app and editor extension. The
  Windows tray gauge rotates for manual and automatic refreshes, while the shared compact/full
  dashboard button shows an animated reading indicator. Editor dashboard cards, the dashboard
  header, and Status Bar readings now animate without hiding their previous percentages; every
  manual editor entry point also uses VS Code's native window progress indicator.
- Added enforced CI coverage gates for both applications, focused desktop-test filtering, and safe
  Windows integration checks for startup registration, single-instance messaging, Credential
  Manager reads, and the installer elevation contract. The expanded suites now cover 60 desktop
  checks and 31 editor-extension tests without modifying real startup settings or credentials.

### Fixed

- Fixed stale-provider retry scheduling across the desktop app and editor extension. Failed
  providers now wake the scheduler when their individual backoff or `Retry-After` expires, without
  waiting for the regular polling interval, refreshing healthy providers unnecessarily, or
  postponing the next regular refresh.
- Replaced contradictory or ambiguous stale timestamps across the desktop dashboard, editor
  dashboard, and editor Status Bar. Stale views now distinguish the last successful reading from
  the later failed check and show when the next automatic retry is due.
- Fixed the editor scheduler waking every second for a failed provider after that provider was
  disabled. Retry timers now consider only currently enabled providers.
- Hardened editor startup against malformed cached snapshots and invalid cached reset timestamps,
  discarding unusable state instead of allowing it to break dashboard or Status Bar rendering.

## [0.8.0] - 2026-08-16

### Added

- Added automatic daily GitHub release checks while the desktop app is running. When a newer
  release exists, UsageAI presents an explicit **Yes/No** installation prompt; declining leaves the
  current version untouched and postpones the next prompt for 24 hours.
- Added in-app installer download and launch for installed Windows builds. UsageAI selects the exact
  versioned Setup.exe and checksum assets from the pinned repository and falls back to the release
  page if either asset is unavailable.

### Security

- Update downloads follow only approved GitHub release-asset hosts, enforce advertised and maximum
  sizes plus a five-minute deadline, and must match the published SHA-256 before Windows is allowed
  to execute the installer.
- The updater continues to require explicit user approval before downloading or installing, and
  Windows retains its normal UAC confirmation for the Program Files upgrade.

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
