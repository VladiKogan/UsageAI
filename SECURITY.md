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
  `claude auth status --json` command with bounded runtime and output and, while the stored token is
  still expired, `claude mcp list`, which makes the CLI refresh the login. Claude Code remains the sole
  owner of any resulting token exchange and credential update; UsageAI waits briefly and rereads the
  access token afterward.
- Codex credentials are read-only to UsageAI. When `auth.json` in the standard `~\.codex` (or an
  account's own folder) holds a ChatGPT login whose access token has not expired, UsageAI sends that access
  token, with the login's account id, to `https://chatgpt.com/backend-api/wham/usage` only, the
  endpoint the Codex CLI itself uses. It never reads the refresh token for use, never refreshes the
  login, and never writes the file. An API-key login, an expired token, a `chatgpt_base_url` override
  in `config.toml`, or any rejection or unexpected answer leaves usage to the official
  `codex app-server`, started with a read-only sandbox and no approval prompts, which renews the
  login itself.
- Extra Claude Code and Codex accounts are read from their own login folders under the same rules:
  the folder's `.credentials.json` or `auth.json` is read only, and its token goes only to the hosts
  the main login's token goes to (`api.anthropic.com`, `chatgpt.com`). The main login is always the
  standard `~\.claude` / `~\.codex`; an inherited `CLAUDE_CONFIG_DIR` / `CODEX_HOME` never redirects
  it. An extra account never uses `USAGEAI_CLAUDE_OAUTH_TOKEN`, a Claude web session key, or the
  Windows Credential Manager fallback, which all belong to the main login. Its CLI children
  (`claude auth status`, `claude mcp list`, `codex app-server`) receive `CLAUDE_CONFIG_DIR` /
  `CODEX_HOME` set explicitly to that folder; the main login's children receive neither, never an
  inherited one. Folders are found by one non-recursive listing of `%USERPROFILE%` for names that
  start with `.claude` / `.codex` and that hold that provider's credential file, from an inherited
  `CLAUDE_CONFIG_DIR` / `CODEX_HOME`, or chosen by the user, and must be absolute and local (no
  UNC). Who is signed in is read from local files only (`.claude.json`'s `oauthAccount`, the email
  in Codex's `id_token`) and kept as a hash plus the email, for labels. When a Claude login cannot
  be read, `.claude.json` is checked for a `primaryApiKey` only to say the login was switched to
  API billing; whether one exists is all that is used, and the key is never kept, sent or shown.
- "Sign in to another account" starts the provider's official login, `claude auth login --claudeai`
  or `codex login`, with `CLAUDE_CONFIG_DIR` / `CODEX_HOME` pointing at a new or empty folder the user
  confirmed. The CLI performs the whole OAuth sign-in in the browser and writes its own credential
  file there; UsageAI reads only the sign-in link it prints (to offer it if the browser did not open)
  and waits, at most two minutes, for that file to appear. UsageAI never reads a password, runs an
  OAuth exchange, refreshes or writes a token, or changes which account a CLI uses by default. A
  sign-in that does not finish removes the folder only when that attempt created it. UsageAI deletes
  a login folder only when the user removes an account UsageAI created, after a confirmation that
  names the path, or answers yes to the uninstaller's question listing those folders.
- An account's terminal command is a `.cmd` file UsageAI writes to `%LOCALAPPDATA%\UsageAI\bin`
  (added once to the user's PATH) that sets `CLAUDE_CONFIG_DIR` / `CODEX_HOME` and starts the CLI.
  It holds no credential, and UsageAI replaces or deletes only files that start with its marker
  line. To list commands the user made themselves, each account scan (and **Rescan**, but not a
  startup with automatic finding off) reads the `.cmd`, `.bat` and `.ps1` files of at most 16 KB in
  the PATH's local folders; shares and mapped network drives are skipped. Only the line that sets
  `CLAUDE_CONFIG_DIR` / `CODEX_HOME` is used, and only the file's path is kept in settings. Anything
  else such a file holds, an API key included, is never stored, logged or sent, and the files are
  never changed.
- GitHub Copilot tokens are sent only to the GitHub service that issued them: `api.github.com` for
  github.com and `api.<host>` for a GitHub Enterprise Cloud host (`*.ghe.com`), as named by the
  credential file the token came from. A token for any other host, such as a self-hosted GitHub
  Enterprise Server, is not used.
- With "Show provider incidents" on (the default), the tray app reads the providers' public status
  pages at most every five minutes: `status.claude.com`, `www.githubstatus.com` and
  `status.openai.com`. These requests carry no credential, cookie or account identifier, go through
  the same no-redirect, size-capped client, and only status JSON is read. Turning the setting off
  stops them.
- Browser storage is never scanned.
- Claude web sessions are opt-in, memory-only, and never persisted by UsageAI.
- Secrets are not forwarded to provider CLI child processes or included in diagnostic output.
- Codex and Claude Code CLIs are launched from an explicit `CODEX_PATH` / `CLAUDE_PATH`, then `PATH`
  and the providers' standard install folders, and only after those from the copy bundled in the
  provider's official editor extension (OpenAI's Codex extension, Anthropic's Claude Code extension).
  The tray app finds that copy through each editor's own `extensions.json`, which names the version
  the editor actually loads as a bare folder inside its extensions folder; the editor extension asks
  VS Code for the provider extension's folder in its own extension host. Either way only the
  extension's own fixed binary paths are tried, never a search, and outside Windows the file must be
  executable. The child gets the same minimal environment as any other provider CLI.
- Network destinations and redirects are constrained by the provider clients.
- Antigravity credentials remain owned by Google's official `agy` CLI. UsageAI starts it only as a
  `--hub` server bound to a loopback port, or on older builds through its read-only `/usage` path,
  each with stdin closed, bounded output and runtime, a minimal environment, and cleanup restricted to
  the exact process tree UsageAI created. The hub is reached over plain HTTP on 127.0.0.1 only, gated
  by a random token UsageAI mints per session and sends on every call; no other endpoint may be
  plaintext. That token is supplied to the child both in its environment and as `--csrf_token`,
  because CLI builds from September 2026 onwards read only the latter. The command-line form is
  visible to local process listings, and to any process-creation auditing or endpoint agent that
  records command lines, so unlike the environment form it can reach a log on disk. This is how
  Google's Antigravity IDE already provisions the language server it starts on the same machine. The
  token is minted per session, is never persisted by UsageAI, and reaches only that child's loopback
  port — but it authorises that port's whole `LanguageServerService`, not a quota-only endpoint, for
  the lifetime of that child. UsageAI never opens or modifies the Antigravity keyring or credential
  files.
- UsageAI also reuses `agy --hub` servers that Google's Antigravity editor extension starts, the same
  way it already reuses the Antigravity IDE's language server. It reads the server's `--hub-port`
  and `--csrf_token` from the command line of a process owned by the current user (other users'
  command lines are not returned on Windows and are filtered out by uid on Linux and macOS),
  re-reads that the process still owns that loopback port immediately before the token is first sent
  to it on each refresh, and sends the token only there, over plain HTTP on 127.0.0.1. Hubs started
  with `--app_data_dir`, as the editor extension does, are tried before hubs other UsageAI instances
  started. It calls only the read-only
  `GetUserStatus` and `RetrieveUserQuotaSummary` methods. The token is held in memory only, never
  persisted or logged, and is dropped as soon as that process stops owning the port.

The VS Code/Antigravity extension follows the same contract. Provider credentials stay in the local
Node extension host and are never sent to its webview. Claude credentials remain read-only to the
extension, which uses the same bounded official-CLI recovery path; other provider-refreshed access
tokens are cached only in memory for the editor process. Its persisted
snapshot cache contains usage metadata, never credentials. Local Antigravity CSRF tokens are bound to
ports owned by the process that supplied them, and the token for the extension's own `agy --hub` child
is generated per session and never persisted by the extension, with the same command-line exposure and
session-scoped grant described above. The extension applies the same bounded
official-`agy` handling and never sends its output to the dashboard webview except after it has been
reduced to ordinary quota metadata.

## Local state

UsageAI writes preferences, recorded usage history, and a cached copy of the last reading to `%LOCALAPPDATA%\UsageAI` (or to `USAGEAI_DATA_DIR` when set). These files hold plan names, the account identity a provider reports, the paths of extra login folders (and, in `created-logins.txt`, the ones UsageAI created), metric names, timestamps, and usage percentages. They never hold tokens, cookies, or refresh credentials, they are written with the account's default file protection, and history can be deleted from the settings window. Beyond provider usage endpoints, UsageAI contacts the pinned GitHub repository automatically at most once per day while running, plus whenever the user selects **Check for updates** in Settings. It never downloads or installs an update without the user's approval.
