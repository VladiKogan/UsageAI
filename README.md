<p align="center">
  <img src="Resources/logo.png" alt="UsageAI Logo" width="128" />
</p>

<h1 align="center">UsageAI</h1>

<p align="center">
  <b>Every AI coding limit you're burning through — in one tiny tray icon.</b><br />
  Codex · Claude Code · GitHub Copilot · Google Gemini
</p>

<p align="center">
  <a href="https://github.com/VladiKogan/UsageAI/releases/latest"><img src="https://img.shields.io/github/v/release/VladiKogan/UsageAI?style=flat-square&color=2ea043" alt="Latest release" /></a>
  <a href="https://github.com/VladiKogan/UsageAI/releases"><img src="https://img.shields.io/github/downloads/VladiKogan/UsageAI/total?style=flat-square&color=0aa5c9" alt="Downloads" /></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4?style=flat-square" alt="Windows 10 and 11" />
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue?style=flat-square" alt="MIT license" /></a>
</p>

<p align="center">
  <a href="#-get-it"><b>Download</b></a> ·
  <a href="#-what-youll-see">What you'll see</a> ·
  <a href="#-your-credentials-stay-yours">Privacy</a> ·
  <a href="#-troubleshooting">Troubleshooting</a>
</p>

---

You're mid-refactor and the model stops. Weekly limit. Nobody told you it was close.

**UsageAI** is a tiny Windows tray app that keeps every AI coding limit you have in one glance — how much you've used, how fast you're burning it, and exactly when it resets. No dashboards to open, no logins to repeat, no surprises at 2 a.m.

<p align="center">
  <img src="usageai-preview.png?v=0.7.1" alt="UsageAI 0.7.1 compact tray popup with centered provider icons" width="420" />
</p>

## ⚡ Get it

Current release: **UsageAI for Windows 0.7.1** and **UsageAI editor extension 0.1.7**.
You can also browse the complete **[Releases page](https://github.com/VladiKogan/UsageAI/releases)**.

| | |
| --- | --- |
| 🚀 **[UsageAI-0.7.1-Setup.exe](https://github.com/VladiKogan/UsageAI/releases/download/v0.7.1/UsageAI-0.7.1-Setup.exe)** ([SHA-256](https://github.com/VladiKogan/UsageAI/releases/download/v0.7.1/UsageAI-0.7.1-Setup.exe.sha256)) | The easy one. Start Menu shortcut, clean uninstall, and it installs the .NET 10 Desktop Runtime for you if you don't have it. |
| 🎒 **[UsageAI-0.7.1-portable.exe](https://github.com/VladiKogan/UsageAI/releases/download/v0.7.1/UsageAI-0.7.1-portable.exe)** ([SHA-256](https://github.com/VladiKogan/UsageAI/releases/download/v0.7.1/UsageAI-0.7.1-portable.exe.sha256)) | One file. No install. Drop it anywhere and double-click — it uses the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) if you already have it. |
| 🧩 **[usageai-0.1.7.vsix](https://github.com/VladiKogan/UsageAI/releases/download/v0.7.1/usageai-0.1.7.vsix)** ([SHA-256](https://github.com/VladiKogan/UsageAI/releases/download/v0.7.1/usageai-0.1.7.vsix.sha256)) | The VS Code and Antigravity extension. Install it from the editor's **Extensions: Install from VSIX...** command. |

Windows 10 or 11 (x64), plus at least one AI tool you're already signed in to. That's the whole setup — UsageAI reuses the login you already have, so there's nothing new to create, paste, or remember.

### VS Code and Antigravity extension

The pure TypeScript editor extension lives in [`extension/`](extension/README.md). It adds a UsageAI
Activity Bar view that can stay pinned in the Primary or Secondary Sidebar, plus an always-available
Status Bar summary. The Status Bar shows session and weekly percentages side by side; hover it for
segmented meters, the last-updated time, and quick Open/Refresh actions. One VSIX targets both VS Code
and Antigravity; marketplace publication is kept separate for Visual Studio Marketplace and Open VSX.

Install it from the [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=vladikogan.usageai)
or [Open VSX](https://open-vsx.org/extension/vladikogan/usageai). For manual installation, download
the VSIX from the GitHub release and select **Extensions: Install from VSIX...** in VS Code or
Antigravity. To build it from source instead:

```powershell
cd extension
npm.cmd install
npm.cmd test
npm.cmd run package:vsix
```

> Every download ships with a matching `.sha256` file if you like to verify what you run.

## 👀 What you'll see

**A tray icon that actually tells you something.** It fills up like a gauge as you burn through your limit and shifts colour along the way — green under 50%, blue up to 71%, amber to 89%, red once you're at 90% and it's time to pace yourself.

**Every window each provider reports — not just one number.** Codex 5-hour, weekly, credits and reset credits. Claude 5-hour, weekly, weekly Opus and extra usage. Copilot AI credits or premium requests, chat and completions. Gemini model groups (Gemini Models, Claude and GPT models) or Cloud Code API quotas.

**"At this pace, empty by 15:20."** A trend sparkline plus a burn-rate forecast, so you find out you're running hot *before* the wall, not after it.

**A heads-up before it matters.** Tray notifications when a limit crosses the threshold you picked — and again when it resets and you're free to go.

**Reset countdowns, plan and account** for each provider, so you always know what you're on and how long until it refills.

**The providers look like themselves.** Codex, Claude, GitHub Copilot, and Gemini use the same recognizable monochrome brand marks in the Windows dashboard and editor Status Bar, coloured by the surrounding native theme.

**One glance or the full picture.** Left-click for a compact popup; open the dashboard for a responsive multi-column view that reflows as you resize it and fills the space you give it.

<p align="center">
  <img src="usageai-dashboard-preview.png?v=0.7.1" alt="UsageAI 0.7.1 dashboard with centered provider icons" width="750" />
</p>

**It never goes blank.** If a refresh fails, UsageAI keeps the last good reading and marks it **stale**, with the provider's own error message attached — then backs off before retrying instead of hammering a rate-limited API.

**It looks like it belongs on your desktop.** Native Windows dark title bars and scrollbars, light/dark/follow-Windows themes, your Windows accent colour, and window size and position remembered between runs.

## 🖱️ Living in the tray

| Do this | Get this |
| --- | --- |
| Left-click the tray icon | Compact view of every connected provider |
| **Win+Alt+U** | Same thing, from anywhere |
| Right-click ▸ **Open** | Full dashboard, all providers and metrics |
| Right-click ▸ **Settings...** | Preferences |
| Launch UsageAI again | Brings the running window forward |
| `Esc` | Back to the tray |
| `Tab` / `Enter` on a card | Opens that provider's usage page — or copies its sign-in command if it's disconnected |

The dashboard is a normal Windows window: move it, resize it, maximize it, close it back to the tray.

## 🔌 Works with what you already use

| Provider | You need |
| --- | --- |
| **Codex** | Codex CLI signed in (`codex login`) |
| **Claude Code** | Claude Code signed in (`claude`), or a Claude web session key you supply yourself |
| **GitHub Copilot** | Signed in through a Copilot IDE extension or Copilot CLI |
| **Google Gemini** | Signed in via the `gemini` CLI, or Antigravity IDE running |

One is enough to get going. Connect the rest whenever you like — cards appear as providers show up.

## 🔒 Your credentials stay yours

UsageAI reads the login your tools already made, and nothing else:

- 🚫 **Never scans your browser storage** — no cookie extraction, ever.
- 🚫 **Never prints or writes your credentials.** Tokens are used in memory and stay there.
- 🏠 **Nothing leaves your machine** except the usage request to the provider itself.
- 🗑️ **Your history is yours to delete** — one button in Settings.

Preferences, usage history and the cached last reading live in `%LOCALAPPDATA%\UsageAI` (set `USAGEAI_DATA_DIR` to move them for a portable install). History is just timestamps, provider ids, metric names and percentages.

<details>
<summary><b>Using a Claude web session key (optional)</b></summary>

UsageAI tries Claude authentication in this order:

1. An explicitly supplied Claude web session key from `USAGEAI_CLAUDE_SESSION_KEY` (the compatible `CLAUDE_AI_SESSION_KEY` and `CLAUDE_WEB_SESSION_KEY` names also work).
2. Claude Code's saved OAuth login from its normal credential store.

The first option follows [Win-CodexBar](https://github.com/nesszer/Win-CodexBar)'s web-usage approach, but UsageAI deliberately does not extract cookies from browsers. A Claude session cookie is more privileged than a usage-only token: supply it only to the UsageAI process, keep it out of source control and scripts, and rotate it immediately if it's exposed.

For a process-scoped PowerShell session:

```powershell
$env:USAGEAI_CLAUDE_SESSION_KEY = '<your sessionKey value>'
dotnet run --project .\UsageAI.csproj
Remove-Item Env:USAGEAI_CLAUDE_SESSION_KEY
```

The key is kept in memory, is never forwarded to provider CLI child processes, and is never written to disk by UsageAI. If it's missing, invalid or no longer accepted, UsageAI quietly falls back to Claude Code OAuth.

</details>

## ⚙️ Make it yours

**Settings...** in the tray menu lets you tune:

- How often it refreshes — and whether to ease off while no window is open
- Alert thresholds, and whether resets get announced
- Theme, and where the warning and critical colours kick in
- Whether history is recorded, and whether the trend and forecast are shown
- Which provider drives the tray icon (or let it follow whichever is running hottest), which providers appear, and in what order
- The global hotkey
- An opt-in check for newer releases

## 🩹 Troubleshooting

<details>
<summary><b>Something's not showing up</b></summary>

- **"Codex CLI was not found"** — make sure `codex.cmd` is on your `PATH`, or set `CODEX_PATH` to its full path. (UsageAI intentionally uses `codex.cmd` rather than `codex.ps1`, so it works with the default Windows PowerShell execution policy.)
- **"Codex is not signed in"** — run `codex login` in a terminal, then hit **Refresh**.
- **"Claude Code is not signed in"** — run `claude` and finish signing in, or use the session-key method above, then hit **Refresh**.
- **"Copilot is not signed in"** — sign in through a Copilot IDE extension or Copilot CLI, then hit **Refresh**.
- **A card says "stale"** — the last refresh failed, so you're looking at the previous good reading. The card shows the provider's own error message, and UsageAI backs off before retrying instead of hammering a failing or rate-limited provider.
- **The hotkey does nothing** — another app already owns Win+Alt+U. Turn it off in **Settings...** to release it.
- **No tray icon** — open the Windows tray overflow menu and pin UsageAI.

</details>

<details>
<summary><b>Supplying your own tokens</b></summary>

- **Custom Claude OAuth token:** set `USAGEAI_CLAUDE_OAUTH_TOKEN` for the UsageAI process. It needs the `user:profile` scope and is never persisted.
- **Custom Copilot token:** set `COPILOT_GITHUB_TOKEN` for the UsageAI process. Used in memory only.
- **GitHub CLI fallback:** set `USAGEAI_ENABLE_GH_TOKEN_FALLBACK=1`. Off by default, because `gh auth token` can expose a broader GitHub token than usage tracking needs.

</details>

Provider protocols and usage endpoints change over time. UsageAI keeps each integration isolated in its own usage client, with response parsing covered by fixture tests, so keeping up stays cheap.

## 🛠️ Build it yourself

<details>
<summary><b>Run from source, test, and package</b></summary>

You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

**Run it:**

```powershell
dotnet run --project .\UsageAI.csproj
```

**Build and test:**

```powershell
dotnet build .\UsageAI.sln -c Release
dotnet run --project .\UsageAI.Tests\UsageAI.Tests.csproj
```

The test project is a dependency-free console harness covering credential handling, bounded I/O, provider response parsing, settings validation, history, forecasting and alert behaviour. It runs with `dotnet run`, not `dotnet test`.

**Check one provider without the UI:**

```powershell
dotnet run --project .\UsageAI.csproj -- --diagnose codex
dotnet run --project .\UsageAI.csproj -- --diagnose claude
dotnet run --project .\UsageAI.csproj -- --diagnose copilot
dotnet run --project .\UsageAI.csproj -- --diagnose gemini
```

`--help` lists every switch; `--version` prints the build version. Diagnostic output includes account identity and usage metadata — never provider tokens, but review it before sharing publicly.

**Regenerate the screenshots:**

```powershell
dotnet run --project .\UsageAI.csproj -- --render-preview .\usageai-preview.png
dotnet run --project .\UsageAI.csproj -- --render-preview .\usageai-dashboard-preview.png --full
```

**Build the portable executable:**

```powershell
dotnet publish .\UsageAI.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\publish
```

Run `publish\UsageAI.exe`. The single-file, framework-dependent build is deliberately small and uses the installed .NET 10 desktop runtime. Rename it to `UsageAI-<version>-portable.exe` before publishing it as a release asset.

**Build the installer** — install [Inno Setup](https://jrsoftware.org/isinfo.php) 6.3 or newer, publish the portable build above, then:

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" .\installer\UsageAI.iss /DMyAppVersion=<version>
```

This produces `installer\Output\UsageAI-<version>-Setup.exe`. It installs to Program Files, adds a Start Menu shortcut and uninstaller, and silently installs the .NET 10 Desktop Runtime at install time if it's missing (see `installer\UsageAI.iss`).

**Name and checksum both Windows assets before uploading:**

```powershell
$version = '<version>'
$portable = "publish\UsageAI-$version-portable.exe"
Copy-Item 'publish\UsageAI.exe' $portable

foreach ($file in $portable, "installer\Output\UsageAI-$version-Setup.exe") {
    $hash = (Get-FileHash -Path $file -Algorithm SHA256).Hash.ToLower()
    "$hash  $(Split-Path $file -Leaf)" | Set-Content -NoNewline "$file.sha256"
}
```

Releases are built and checked locally, then uploaded manually to GitHub Releases as
`UsageAI-<version>-Setup.exe`, `UsageAI-<version>-portable.exe`, the separately versioned editor VSIX,
and their `.sha256` files. The GitHub `Build` workflow only restores, builds and tests — it never publishes.

</details>

---

<p align="center">
  <a href="https://github.com/VladiKogan/UsageAI/releases/latest"><b>⬇️ Download UsageAI</b></a> ·
  <a href="Changelog.md">Changelog</a> ·
  <a href="SECURITY.md">Security</a> ·
  <a href="LICENSE">MIT</a>
</p>
