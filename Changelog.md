# Changelog

All notable changes to UsageAI are documented here.

The project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.3.1] - 2026-07-22

### Fixed

- Reapplied the captured Windows security descriptor after atomically replacing Claude's OAuth credential file, preventing elevated filesystems from changing its owner.

## [0.3.0] - 2026-07-22

### Added

- Optional Claude web-session authentication, tried before OAuth when a session key is explicitly supplied through the UsageAI process environment.
- A no-dependency security regression harness covering secret validation, bounded I/O, minimal child-process environments, Claude organization parsing, OAuth credential ACL preservation, lost-update protection, and cross-process locking.
- Windows CI and a guarded release workflow that validates release metadata, optionally applies Authenticode signing, generates a verified SPDX SBOM and SHA-256 checksum, and verifies published release assets.
- A security policy and automated dependency/workflow update configuration.

### Security

- Bounded provider HTTP responses, credential files, subprocess output, protocol messages, and token sizes to prevent memory-exhaustion paths.
- Disabled redirects and ambient cookies for provider HTTP clients, enabled certificate revocation checks, and replaced static client identifiers with the running app version.
- Restricted provider subprocesses to absolute executables and minimal allowlisted environments so provider secrets are not inherited accidentally.
- Replaced broad Windows Credential Manager enumeration for Claude with exact credential reads and zeroed copied credential buffers after use.
- Made Claude OAuth refresh persistence atomic across processes, protected against lost updates, and preserved the credential file's ACL, ownership, and EFS state.
- Sanitized provider and CLI errors so raw response bodies, stderr, tokens, and local paths are not surfaced to the UI.
- Disabled unsafe BinaryFormatter compatibility, enabled recommended .NET security analyzers as errors, and made Release builds deterministic without portable debug symbols.
- Reduced the distributable to one executable so application code is not left in a separate dependency file and future Authenticode signing covers the complete app payload.
- Required unsigned release titles, notes, and ZIP filenames to be explicitly labeled `UNSIGNED` when no signing certificate is configured.
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
