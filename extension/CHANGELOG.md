# Changelog

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
