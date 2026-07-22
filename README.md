# UsageAI

A small, personal Windows tray meter for Codex, Claude Code, and GitHub Copilot usage limits.

UsageAI reuses your existing local provider login. Codex data comes from the Codex CLI app-server; Claude Code data comes from Anthropic using Claude Code's saved OAuth login; Copilot data comes from GitHub using a token already saved by a Copilot IDE extension, Copilot CLI's secure Windows credential, or GitHub CLI. UsageAI never prints those credentials and does not read browser cookies.

![UsageAI tray popup](usageai-preview.png)

## What it shows

- Codex five-hour and weekly usage, reset countdowns, reset credits, plan, and credit balance
- Claude Code five-hour and weekly usage, resets, plan, and optional extra usage
- GitHub Copilot AI-credit or premium-request usage, chat/completion quotas, monthly reset, plan, and account
- An account selector for switching between Codex, Claude Code, and GitHub Copilot
- A dynamic tray icon based on the most-used window
- Five-minute background refresh and manual refresh
- Optional start with Windows

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- At least one supported provider login:
  - Codex CLI installed and signed in (`codex login`)
  - Claude Code installed and signed in (`claude`)
  - GitHub Copilot or GitHub CLI signed in to an account with Copilot access

PowerShell can block the `codex.ps1` launcher; UsageAI intentionally uses `codex.cmd`, so it works with the default Windows execution policy.

## Run it

```powershell
dotnet run --project .\UsageAI.csproj
```

The app lives in the system tray. Left-click its icon for the usage panel. Right-click and use **Account** to switch providers.

To validate one provider without opening the tray UI:

```powershell
dotnet run --project .\UsageAI.csproj -- --diagnose codex
dotnet run --project .\UsageAI.csproj -- --diagnose claude
dotnet run --project .\UsageAI.csproj -- --diagnose copilot
```

## Build a personal executable

```powershell
dotnet publish .\UsageAI.csproj -c Release -o .\publish
```

Run `publish\UsageAI.exe`. The framework-dependent build is deliberately small and uses the installed .NET 8 desktop runtime.

## Troubleshooting

- **Codex CLI was not found:** ensure `codex.cmd` is on `PATH`, or set `CODEX_PATH` to its full path.
- **Codex is not signed in:** run `codex login` in a terminal, then choose **Refresh**.
- **Claude Code is not signed in:** run `claude` and complete sign-in, then choose **Refresh**.
- **A custom Claude OAuth token is needed:** set `USAGEAI_CLAUDE_OAUTH_TOKEN` for the UsageAI process. The token must have the `user:profile` scope and is never persisted by UsageAI.
- **Copilot is not signed in:** sign in through a GitHub Copilot IDE extension or `gh auth login`, then choose **Refresh**.
- **A custom Copilot token is needed:** set `COPILOT_GITHUB_TOKEN` for the UsageAI process. The token is used in memory and is not persisted.
- **No tray icon:** open the Windows tray overflow menu and pin UsageAI.

The provider protocols and usage endpoints can change. UsageAI keeps each integration isolated in its own usage client so future changes stay easy to update.
