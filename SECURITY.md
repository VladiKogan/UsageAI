# Security policy

## Supported versions

Security fixes are provided for the latest published UsageAI release. Update to the newest release before reporting a problem that may already be fixed.

## Reporting a vulnerability

Please use the repository's **Security** tab and submit a private vulnerability report. Do not open a public issue for suspected credential exposure, authentication bypass, arbitrary code execution, or another issue that would put users at risk.

Include the affected version, Windows version, provider involved, reproduction steps, and the security impact. Redact tokens, cookies, OAuth credential files, diagnostic account identifiers, and local usernames or paths. UsageAI maintainers will acknowledge the report, investigate it privately, and coordinate a fix and disclosure when appropriate.

If a provider secret may have been exposed, revoke or rotate it with that provider immediately; do not wait for the investigation to finish.

## Credential-handling design

- Provider secrets originate only from explicit environment variables or the provider's existing local credential location.
- Claude Code credentials are read-only to UsageAI: it never submits Claude's shared refresh token or
  writes the credential file. When the access token expires, UsageAI may invoke the official
  `claude auth status --json` command with bounded runtime and output. Claude Code remains the sole
  owner of any resulting token exchange and credential update; UsageAI waits briefly and rereads the
  access token afterward.
- Browser storage is never scanned.
- Claude web sessions are opt-in, memory-only, and never persisted by UsageAI.
- Secrets are not forwarded to provider CLI child processes or included in diagnostic output.
- Network destinations and redirects are constrained by the provider clients.
- Antigravity credentials remain owned by Google's official `agy` CLI. UsageAI invokes only its
  read-only `/usage` path with stdin closed, bounded output and runtime, a minimal environment, and
  cleanup restricted to the exact process tree UsageAI created. UsageAI never opens or modifies the
  Antigravity keyring or credential files.

The VS Code/Antigravity extension follows the same contract. Provider credentials stay in the local
Node extension host and are never sent to its webview. Claude credentials remain read-only to the
extension, which uses the same bounded official-CLI recovery path; other provider-refreshed access
tokens are cached only in memory for the editor process. Its persisted
snapshot cache contains usage metadata, never credentials. Local Antigravity CSRF tokens are bound to
ports owned by the process that supplied them and ownership is checked again immediately before use.
The extension applies the same bounded official-`agy` fallback and never sends its output to the
dashboard webview except after it has been reduced to ordinary quota metadata.

## Local state

UsageAI writes preferences, recorded usage history, and a cached copy of the last reading to `%LOCALAPPDATA%\UsageAI` (or to `USAGEAI_DATA_DIR` when set). These files hold plan names, the account identity a provider reports, metric names, timestamps, and usage percentages. They never hold tokens, cookies, or refresh credentials, they are written with the account's default file protection, and history can be deleted from the settings window. Beyond provider usage endpoints, UsageAI contacts the pinned GitHub repository at most once per day while running to discover new releases. It never downloads or installs an update without the user's approval.
