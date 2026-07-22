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

The release workflow always publishes an SPDX SBOM and SHA-256 checksum. If Authenticode credentials are configured, it signs and verifies the executable. Otherwise, the release title, notes, and ZIP filename must explicitly say `UNSIGNED`. Treat an unsigned build that lacks those labels, or any build obtained outside the official repository release page, as untrusted.
