# Changelog

## Unreleased

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
