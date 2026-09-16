# Changelog

## Unreleased

### Changed

- Expanded the suite to 48 checks with provider parser edge cases, Codex protocol failures, bounded
  Claude CLI authentication probes, Gemini cloud and token-refresh failures, the Antigravity hub
  command line, start backoff, and forced-refresh recovery, metric-mode persistence failures, and
  process-owned port parsing. Aggregate coverage is now 79.89% line, 80.10% branch, and 81.41%
  function coverage.
- Mapping a process id to its listening ports no longer starts PowerShell. The Antigravity probe
  needs that mapping more than once per refresh, because it re-checks that a port is still owned by
  the language server immediately before sending it a token, and `Get-NetTCPConnection` measured
  1.2 seconds per call; `netstat -ano` answers the same question in 15 to 33 milliseconds.

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
- Fixed a dashboard metric-mode change that cannot be written to settings leaving the dashboard
  showing a mode the Status Bar and the next reload do not use. The mode is still applied immediately,
  but a rejected write now rolls it back and rerenders instead of being discarded unobserved.
- Applied dashboard metric-mode changes immediately in both the webview and extension host so All,
  Metered only, and Important only no longer depend on configuration-change propagation to rerender.

## 0.1.14

### Added

- Added persistent collapsible provider cards with semantic keyboard-focusable header buttons,
  `aria-expanded`, visible focus treatment, native Enter/Space behavior, chevrons, and content that
  leaves keyboard and accessibility navigation when collapsed.
- Added globally persisted per-provider expansion preferences under a versioned state key. Connected
  providers still start expanded until changed, and preferences survive view disposal, editor
  restart, workspace changes, provider reordering, and provider disable/re-enable.
- Added **Collapse all** and **Expand all** dashboard actions. Restored and incoming state is limited
  to known provider IDs and ignores malformed, duplicate, or unknown values.
- Added an unfiltered collapsed summary showing the highest-used metered metric, its percentage, and
  reset countdown, with **No metered limit** when the provider reports only balances or unlimited
  metrics.
- Added the `usageai.metricDisplayMode` setting and dashboard shortcut with `all`, `metered`, and
  `important` choices. Metered includes every usable percentage limit; Important selects the
  highest-used Session, Rolling, and Monthly metric with stable first-reported tie-breaking.
- Added **No metered limits reported** to expanded connected cards when the active filter removes all
  rows.
- Added one bounded Claude Code OAuth recovery attempt when the initial usage request is rejected as
  unauthorized/forbidden or fails with unexpected unavailable data. The extension asks the official
  `claude auth status --json` command to validate or refresh the login, rereads the credential file,
  and retries usage once without handling or writing the shared refresh token. Explicit environment
  token overrides remain authoritative and bypass this fallback.

### Changed

- Disconnected, errored, and stale cards now expand temporarily so recovery details cannot be hidden.
  Temporary attention never overwrites the saved preference, and recovered cards return to the
  user's remembered state.
- Dashboard metric-mode changes are validated in the extension host, stored globally through VS
  Code's configuration API, and rerender immediately without fetching provider data.
- Metric filtering applies only to expanded dashboard rows. Collapsed summaries and Status Bar
  selection retain the complete provider response and their previous curated behavior.
- Chevron and spinner animation now honors the editor's reduced-motion preference.
- The dashboard now executes the same exported pure selection, attention, and collapsed-summary
  functions covered by the test suite instead of maintaining test-only equivalents.
- Expanded the editor-extension suite to 38 checks. New fixtures cover manifest settings,
  mirrored metric selection, empty and future-kind inputs, global-state sanitization, individual and
  bulk expansion persistence, malformed messages, forced attention and recovery, unfiltered summary
  ties, semantic/hidden webview markup, activation wiring, configuration updates, Claude
  authorization/invalid-response recovery, bounded single retries, and failed CLI validation.
  Aggregate coverage is now 74.10% line, 71.12% branch, and 78.90% function.

### Fixed

- Fixed Claude Code remaining stale when its access token stopped working before the saved expiry
  time, or when a one-off invalid usage response recovered after Claude CLI validation.
- Declared the dashboard metric display mode as application-scoped so a workspace override cannot
  mask changes made with the dashboard selector.
- Prevented temporary stale or disconnected expansion from silently changing a user's saved card
  preference.
- Prevented unknown provider IDs, invalid expansion booleans, and invalid metric modes from being
  persisted or acted on by the extension host.
- Preserved the earliest provider-reported metric when equal percentages tie within the same kind,
  including duplicate Gemini Session and Rolling limits.
- Kept provider names, plan/account details, errors, and summary text escaped through DOM
  `textContent`; no provider content is injected as HTML.

## 0.1.13

- Read Google Gemini quota from one long-lived Antigravity CLI `--hub` server for the session instead of
  starting `agy -p /usage` on every refresh. Refreshes now spawn no child process, and the CLI starts any
  configured MCP servers through `cmd.exe` once per session rather than on every refresh, which is what
  produced transient console windows on Windows.
- Skipped the Antigravity process probe, and the PowerShell child process it costs, while a hub is
  serving the session.
- Removed the Antigravity CLI port-discovery probe: the service rejects the request bodies it sent, so
  the HTTP query never produced a snapshot.
- Passed `-NonInteractive` to the remaining PowerShell process and listening-port queries so a prompt can
  never block a background refresh.

## 0.1.12

- Prevented background Gemini refreshes from preferring the standalone `agy` on `PATH` or allowing
  its headless `/usage` probe to become interactive.
- Made a stale Gemini reading recover from sleep in one refresh even when an earlier `agy` cold-start
  failure was cached, and replaced obsolete Gemini CLI-only sign-in guidance with `agy`.

## 0.1.11

- Added Gemini quota discovery through the official Antigravity VS Code extension's locally installed
  `agy` backend, including its current snake-case `/usage` response format, so VS Code does not need
  to remain open after sign-in.
- Added rotating refresh indicators to dashboard cards, the dashboard header, and Status Bar
  readings while retaining the last visible percentages. Dashboard actions, the view-title button,
  the Status Bar hover action, and **UsageAI: Refresh** now share VS Code's native window progress
  indicator, including when the dashboard is closed or UsageAI Status Bar items are disabled.
- Fixed retry-only refreshes polling healthy providers and postponing the normal refresh schedule.
  Failed providers now retry independently as soon as their backoff or provider-supplied wait time
  expires, and a successful automatic retry clears their stale state without manual intervention.
- Clarified stale timestamps in the dashboard and Status Bar. Stale views now distinguish the last
  successful reading from the later failed check and show when the next automatic retry is due.
- Fixed disabled providers retaining expired backoff timers and waking the extension scheduler once
  per second. Only enabled providers can now influence the next retry wake-up.
- Ignored malformed cached snapshots and invalid reset timestamps during activation so corrupt or
  obsolete global state cannot break dashboard or Status Bar rendering.
- Expanded automated coverage from parser-focused fixtures to live provider request flows, bounded
  HTTP/file/process handling, OAuth refresh reuse, PID-bound Antigravity port revalidation,
  activation, dashboard messaging, and Status Bar lifecycle behavior. CI now enforces line, branch,
  and function coverage thresholds.

## 0.1.10

- Restored automatic Claude usage recovery after access-token expiry through a bounded official
  `claude auth status --json` probe. Claude Code remains the only process that refreshes or writes its
  credentials; UsageAI rereads the result without accessing the shared refresh token.

## 0.1.9

- Fixed the dashboard and Status Bar preview images on Visual Studio Marketplace and Open VSX by
  resolving packaged README assets from the extension's directory in the monorepo.

## 0.1.8

- Added a bounded official `agy /usage` fallback for Gemini when no local Antigravity language server
  is running, with closed stdin, process-tree cleanup, and negative caching after failed cold starts.
- Made Claude Code authentication strictly read-only so the extension no longer exchanges the CLI's shared refresh token or leaves `.credentials.json` with a stale rotated token.

## 0.1.7

- Added provider-specific Codex, Claude, GitHub Copilot, and Gemini icons to Status Bar readings.
- Added side-by-side session and weekly percentages, segmented quota meters, the last-updated time, and Open/Refresh actions to the Status Bar hover card.

## 0.1.6

- Applied the general Providers order to selected Status Bar items instead of rebuilding them in a fixed order.

## 0.1.5

- Organized the Settings UI into General, Status Bar, and Usage Levels sections.

## 0.1.4

- Added native Settings UI checkboxes for choosing each Status Bar provider.
- Retained the earlier array and single-value setting as a hidden compatibility fallback.

## 0.1.3

- Changed reset countdowns of one day or longer to use days and hours instead of large hour and minute values.

## 0.1.2

- Added multi-provider Status Bar selection, with one compact item per selected provider.
- Kept existing single-provider settings compatible during upgrades.

## 0.1.1

- Fixed the Activity Bar and view icons rendering as a solid white square by using a dedicated monochrome, theme-tinted quota dial.

## 0.1.0

- Added a persistent UsageAI webview view and Status Bar summary.
- Added pure TypeScript clients for Codex, Claude Code, GitHub Copilot, Gemini CLI, and local Antigravity quota probing.
- Added per-provider exponential backoff, throttle hints, stale snapshot preservation, and cached last readings.
- Added strict webview CSP, bounded credential/HTTP input, redirect blocking, minimal child environments, and PID-bound Antigravity probing.
- Added parser, credential-normalization, model, and refresh-orchestration tests.
