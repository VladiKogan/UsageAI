# UsageAI

A small, personal Windows tray meter for Codex usage limits.

UsageAI uses the Codex CLI's local app-server and your existing Codex login. It does not store API keys, read browser cookies, or send usage data anywhere else.

![UsageAI tray popup](usageai-preview.png)

## What it shows

- Five-hour usage and reset countdown, when Codex reports that window
- Weekly usage and reset countdown
- Available full-reset credits
- Codex plan and credit balance when supplied by Codex
- A dynamic tray icon based on the most-used window
- Five-minute background refresh and manual refresh
- Optional start with Windows

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Codex CLI installed and signed in (`codex login`)

PowerShell can block the `codex.ps1` launcher; UsageAI intentionally uses `codex.cmd`, so it works with the default Windows execution policy.

## Run it

```powershell
dotnet run --project .\UsageAI.csproj
```

The app lives in the system tray. Left-click its icon for the usage panel; right-click for refresh, startup, and exit controls.

## Build a personal executable

```powershell
dotnet publish .\UsageAI.csproj -c Release -o .\publish
```

Run `publish\UsageAI.exe`. The framework-dependent build is deliberately small and uses the installed .NET 8 desktop runtime.

## Troubleshooting

- **Codex CLI was not found:** ensure `codex.cmd` is on `PATH`, or set `CODEX_PATH` to its full path.
- **Codex is not signed in:** run `codex login` in a terminal, then choose **Refresh**.
- **No tray icon:** open the Windows tray overflow menu and pin UsageAI.

The Codex app-server protocol is currently marked experimental by the CLI. UsageAI keeps that dependency isolated in `Services/CodexUsageClient.cs` so future protocol changes stay easy to update.
