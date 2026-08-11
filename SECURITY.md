# Security policy

## Supported versions

Security fixes are provided for the latest published UsageAI release. Update to the newest release before reporting a problem that may already be fixed.

## Reporting a vulnerability

Please use the repository's **Security** tab and submit a private vulnerability report. Do not open a public issue for suspected credential exposure, authentication bypass, arbitrary code execution, or another issue that would put users at risk.

Include the affected version, Windows version, provider involved, reproduction steps, and the security impact. Redact tokens, cookies, OAuth credential files, diagnostic account identifiers, and local usernames or paths. UsageAI maintainers will acknowledge the report, investigate it privately, and coordinate a fix and disclosure when appropriate.

If a provider secret may have been exposed, revoke or rotate it with that provider immediately; do not wait for the investigation to finish.

## Credential-handling design

- Provider secrets are read only from explicit environment variables or the provider's existing local credential location.
- Browser storage is never scanned.
- Claude web sessions are opt-in, memory-only, and never persisted by UsageAI.
- Secrets are not forwarded to provider CLI child processes or included in diagnostic output.
- Network destinations and redirects are constrained by the provider clients.

The VS Code/Antigravity extension follows the same contract. Provider credentials stay in the local
Node extension host and are never sent to its webview. The extension caches provider-refreshed access
tokens only in memory for the editor process; its persisted snapshot cache contains usage metadata,
never credentials. Local Antigravity CSRF tokens are bound to ports owned by the process that supplied
them and ownership is checked again immediately before use.

## Local state

UsageAI writes preferences, recorded usage history, and a cached copy of the last reading to `%LOCALAPPDATA%\UsageAI` (or to `USAGEAI_DATA_DIR` when set). These files hold plan names, the account identity a provider reports, metric names, timestamps, and usage percentages. They never hold tokens, cookies, or refresh credentials, they are written with the account's default file protection, and history can be deleted from the settings window. The only outbound request UsageAI makes beyond the provider usage endpoints is the release check, which is off unless you turn it on.
