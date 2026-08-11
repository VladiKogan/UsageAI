# UsageAI for VS Code and Antigravity

Keep Codex, Claude Code, GitHub Copilot, and Google Gemini quota usage visible while you code.

UsageAI adds two editor-native surfaces:

- A persistent dashboard in the Activity Bar. Move it to the Secondary Sidebar or Panel if that fits your layout better.
- Compact Status Bar readings with provider-specific icons for selected providers, or whichever connected provider is currently hottest. Session and weekly percentages appear side by side, with segmented meters in the hover card.

The extension reads credentials already created by provider CLIs and IDE integrations. It never scans browser storage and never sends credentials into the webview.

## Requirements

- VS Code 1.90 or newer, or a compatible Antigravity IDE build.
- At least one signed-in provider:
  - Codex CLI (`codex login`)
  - Claude Code (`claude`) or an explicitly supplied `USAGEAI_CLAUDE_SESSION_KEY`
  - GitHub Copilot through a supported local credential file, `COPILOT_GITHUB_TOKEN`, or the opt-in GitHub CLI fallback
  - Gemini CLI (`gemini`) or a running Antigravity language server

The extension is desktop-only because provider discovery requires local files and child processes. It deliberately does not run in vscode.dev or github.dev.

## Install

The current extension release is **0.1.7**. Install it from the
[Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=vladikogan.usageai)
or [Open VSX](https://open-vsx.org/extension/vladikogan/usageai). For manual installation, download
[`usageai-0.1.7.vsix`](https://github.com/VladiKogan/UsageAI/releases/download/v0.7.1/usageai-0.1.7.vsix)
and run **Extensions: Install from VSIX...** in VS Code or Antigravity.

## Use

1. Select the UsageAI icon in the Activity Bar.
2. Leave the Usage view open, or move it to the Secondary Sidebar.
3. Select the Status Bar reading whenever you want to bring the view back.

Use **UsageAI: Refresh** and **UsageAI: Open Settings** from the Command Palette for manual control.

Choose providers using the checkboxes in the **UsageAI: Status Bar** settings section. Each checked provider gets its own Status Bar item. If no provider is checked, **Hottest When None Selected** controls whether the highest-usage provider is shown; uncheck it to hide all UsageAI Status Bar items.

The **UsageAI: Providers** list controls both dashboard and Status Bar order. It also determines which providers are enabled and refreshed.

## Privacy and security

- Provider tokens stay in the extension host and are never posted to the webview.
- Provider endpoints are allowlisted, redirects are blocked, and JSON responses are size-limited.
- Antigravity CSRF tokens are paired only with listening ports owned by the process that supplied them, with an ownership recheck immediately before use.
- Refreshed OAuth access tokens are cached only in memory for the life of the editor process.
- Cached snapshots contain usage metadata, plan/account labels, and reset times—but no credentials.
- The optional Claude web session remains environment-only and is never persisted.

## Development

```powershell
cd extension
npm.cmd install
npm.cmd test
```

Open the `extension` folder in VS Code and press `F5` to launch an Extension Development Host.

Package an installable artifact:

```powershell
npm.cmd run package:vsix
```

The resulting VSIX can be installed manually in both VS Code and Antigravity. Marketplace publication uses the same artifact:

```powershell
npm.cmd run publish:vscode -- --packagePath usageai-0.1.7.vsix
npm.cmd run publish:openvsx -- usageai-0.1.7.vsix
```

Publishing requires separate publisher identities and credentials for Visual Studio Marketplace and Open VSX.
