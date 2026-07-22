# UsageAI

A small, personal Windows tray meter for Codex, Claude Code, and GitHub Copilot usage limits.

UsageAI reuses your existing local provider login. Codex data comes from the Codex CLI app-server; Claude Code data comes from Anthropic using an explicitly supplied Claude web session or Claude Code's saved OAuth login; Copilot data comes from GitHub using a token already saved by a Copilot IDE extension or Copilot CLI's secure Windows credential. UsageAI never prints those credentials and never scans browser storage.

![UsageAI tray popup](usageai-preview.png)

## What it shows

- Codex five-hour and weekly usage, reset countdowns, reset credits, plan, and credit balance
- Claude Code five-hour and weekly usage, resets, plan, and optional extra usage
- GitHub Copilot AI-credit or premium-request usage, chat/completion quotas, monthly reset, plan, and account
- A compact all-provider view with one prioritized metric per connected provider
- A full dashboard with every available metric and connection status for all providers
- Distinct provider icons and color-coded usage signals for quick scanning
- A dynamic tray icon based on the most-used window
- Five-minute background refresh and manual refresh
- Optional start with Windows

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- At least one supported provider login:
  - Codex CLI installed and signed in (`codex login`)
  - Claude Code installed and signed in (`claude`), or an explicitly supplied Claude web session key
  - GitHub Copilot signed in through an IDE extension or Copilot CLI

PowerShell can block the `codex.ps1` launcher; UsageAI intentionally uses `codex.cmd`, so it works with the default Windows execution policy.

## Run it

```powershell
dotnet run --project .\UsageAI.csproj
```

The app lives in the system tray. Left-click its icon for a compact view of every connected provider. The compact view shows the first available metric in this order: five-hour/session, weekly, then credits. Right-click the tray icon and choose **Open** for the full dashboard with all providers and usage metrics. The dashboard behaves like a regular Windows window: move it, resize it, minimize or maximize it, and close it back to the tray. UsageAI remembers its last normal position and size for the current session.

To validate one provider without opening the tray UI:

```powershell
dotnet run --project .\UsageAI.csproj -- --diagnose codex
dotnet run --project .\UsageAI.csproj -- --diagnose claude
dotnet run --project .\UsageAI.csproj -- --diagnose copilot
```

Diagnostic output contains account identity and usage metadata. It never contains provider tokens, but review it before sharing it publicly.

## Claude authentication

UsageAI tries Claude authentication in this order:

1. An explicitly supplied Claude web session key from `USAGEAI_CLAUDE_SESSION_KEY`. The compatible `CLAUDE_AI_SESSION_KEY` and `CLAUDE_WEB_SESSION_KEY` names are also accepted.
2. Claude Code's saved OAuth login from its normal credential store.

The first option follows Win-CodexBar's web-usage approach, but UsageAI deliberately does not extract cookies from browsers. A Claude session cookie is more privileged than a usage-only token: supply it only to the UsageAI process, do not place it in source control or scripts, and rotate it immediately if it is exposed.

For a process-scoped PowerShell session:

```powershell
$env:USAGEAI_CLAUDE_SESSION_KEY = '<your sessionKey value>'
dotnet run --project .\UsageAI.csproj
Remove-Item Env:USAGEAI_CLAUDE_SESSION_KEY
```

The session key is kept in memory, is not forwarded to provider CLI child processes, and is never written by UsageAI. If it is missing, invalid, or no longer accepted, UsageAI safely falls back to Claude Code OAuth.

## Build a personal executable

```powershell
dotnet publish .\UsageAI.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\publish
```

Run `publish\UsageAI.exe`. The single-file, framework-dependent build is deliberately small and uses the installed .NET 10 desktop runtime.

The release workflow always attaches a SHA-256 checksum and SPDX SBOM. When an Authenticode certificate is configured, it signs and verifies the executable. Without one, the release still publishes but its title, notes, and ZIP filename are explicitly marked `UNSIGNED`. A local `dotnet publish` output is also unsigned.

For an unsigned release, confirm that the ZIP hash matches its attached `.sha256` file. This detects corruption but does not establish publisher identity. For a signed release, also verify the executable's Authenticode status:

```powershell
$version = '0.3.0' # Replace with the downloaded release version.
Get-FileHash ".\UsageAI-win-x64-$version-UNSIGNED.zip" -Algorithm SHA256
Get-AuthenticodeSignature .\UsageAI.exe | Select-Object Status, SignerCertificate
```

## Troubleshooting

- **Codex CLI was not found:** ensure `codex.cmd` is on `PATH`, or set `CODEX_PATH` to its full path.
- **Codex is not signed in:** run `codex login` in a terminal, then choose **Refresh**.
- **Claude Code is not signed in:** run `claude` and complete sign-in, or use the opt-in Claude session-key method above, then choose **Refresh**.
- **A custom Claude OAuth token is needed:** set `USAGEAI_CLAUDE_OAUTH_TOKEN` for the UsageAI process. The token must have the `user:profile` scope and is never persisted by UsageAI.
- **Copilot is not signed in:** sign in through a GitHub Copilot IDE extension or Copilot CLI, then choose **Refresh**.
- **A custom Copilot token is needed:** set `COPILOT_GITHUB_TOKEN` for the UsageAI process. The token is used in memory and is not persisted.
- **GitHub CLI fallback is needed:** set `USAGEAI_ENABLE_GH_TOKEN_FALLBACK=1` for the UsageAI process. This is disabled by default because `gh auth token` can expose a broader GitHub token than Copilot usage requires.
- **No tray icon:** open the Windows tray overflow menu and pin UsageAI.

The provider protocols and usage endpoints can change. UsageAI keeps each integration isolated in its own usage client so future changes stay easy to update.
