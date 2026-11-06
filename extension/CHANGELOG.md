# Changelog

## Unreleased

### Changed

- **The extension is listed under the AI category and found by more search words.** Both
  marketplaces now list it under AI, Machine Learning and Visualization instead of Other, and its
  keywords follow the repository's GitHub topics: `claude-code`, `github-copilot`, `rate-limit`,
  `usage-monitor` and other words people search for, such as `anthropic`, `openai` and `tokens`.

## 0.1.21

### Added

- **Claude Code and Codex can show several accounts, each on its own card.** Login folders in your
  home folder named `.claude-*` or `.codex-*` are found when the extension starts, once a day,
  and with **UsageAI: Rescan Accounts**; logins found on the first run show at once, and one
  found later waits for your **Show** or **Ignore**. Each account refreshes, goes stale and backs
  off on its own, and a login of someone already shown is never added twice.
- **UsageAI: Add Account… signs in to another account or adds a login folder you already have.**
  Signing in runs the provider's own `claude auth login --claudeai` or `codex login` in an editor
  terminal with `CLAUDE_CONFIG_DIR` or `CODEX_HOME` set to a new folder such as `~/.claude-team`,
  which your own CLI can use too. UsageAI still never writes or refreshes a token itself.
- **The `usageai.accounts` setting lists added login folders, with an optional label or hidden
  flag.** Each entry must be an absolute, local folder that holds the provider's credential file;
  any other entry is named in one warning and left out.
- **The Status Bar can show any account.** The provider checkboxes keep following the default
  login, and `usageai.statusBarProviders.accounts` adds others by label or email.

### Changed

- **The dashboard remembers expanded cards per account.** The preference moved to a new storage
  key and is carried over once from the previous one, so single-account users see no change.

## 0.1.20

### Fixed

- **Google Gemini no longer shows a weekly limit twice as "5-hour" and "Weekly".** Google dropped
  the 5-hour window from the Google AI Plus plan, and UsageAI still filled the missing 5-hour row
  from Antigravity's older status reading, which carries no window name and is the weekly figure on
  such a plan. Gemini's quota summary names every window a group has, so it is now the whole answer:
  a weekly-only plan shows only its weekly limits, and a plan with a 5-hour window still shows both.
- **A Gemini model without any quota no longer reads as 100% used.** Antigravity lists some models,
  such as "Gemini 4 Argon", with an empty quota block, which UsageAI took for an exhausted limit. A
  missing fraction now means exhausted only when the block still has its reset time, as Google's
  format omits a zero; a block with neither is skipped.

## 0.1.19

### Fixed

- **GitHub Copilot names a personal plan as Pro, Pro+ or Free instead of "Individual".** GitHub
  reports `copilot_plan` as `individual` for every personal plan, so a Copilot Pro account,
  including one granted free to a verified teacher, student or open-source maintainer, read as
  "Individual". The plan is now named from the account's `access_type_sku` when it is one UsageAI
  recognises; an unrecognised SKU and every Business or Enterprise plan keep the name GitHub reports.
- **Google Gemini says when Google has stopped serving Gemini CLI quota for a plan.** Since June
  2026 Google no longer answers Gemini CLI usage requests for personal, Google AI Pro and Ultra
  accounts, and UsageAI took the refusal for an expired sign-in. It now recognises Google's
  `UNSUPPORTED_CLIENT` answer and points to Antigravity or `agy` instead. Code Assist Standard or Enterprise
  and Workspace accounts keep the sign-in message.
- **A Claude plan without usage limits no longer shows as an error.** Free and some education plans
  report no five-hour or weekly window, and the card showed a parsing error; it now says Claude
  reports no usage limits for the plan.
- **A Cloudflare browser check on claude.ai no longer reads as a dead web session.** When claude.ai
  answers the opt-in web path with Cloudflare's challenge, UsageAI keeps the session key, uses Claude
  Code OAuth, and leaves claude.ai alone for 15 minutes instead of asking again on every refresh.
- **GitHub Copilot says when a subscription has ended.** GitHub answers an ended subscription with
  an ordinary reply, which read as invalid usage data; the card now says the subscription has ended.
- **A GitHub Enterprise Copilot token is never sent to github.com.** UsageAI sent every Copilot
  token it found to api.github.com, including one that belongs to a company's own GitHub Enterprise
  Server. Each token now goes only to the GitHub service it came from, and a token for a host
  UsageAI does not read from is not used at all.

### Added

- **UsageAI shows when a provider reports an incident.** When Claude, GitHub Copilot or Codex reports
  degraded service or an outage on its public status page, the provider's Status Bar item gets a
  warning or error marker, its tooltip names the incident, and its dashboard card shows it, so a card
  that went stale can be told apart from a provider that is down. Only the components that serve the
  usage UsageAI shows count. The pages are read at most every five minutes without any credential;
  `usageai.showProviderIncidents` turns it off. Gemini has no status page that covers its quota.
- **The dashboard says whether you are on pace.** Each metered limit's footer now adds, after the
  reset countdown, how use compares with the time its window has run: "On pace", "12% under pace" or
  "8% over pace", once 5% of the window has passed.
- **Codex shows model-specific limits such as Spark.** Codex reports a separate rate-limit bucket for
  some models, and UsageAI read only the main one. Each extra bucket now shows its own limits, named
  the way Codex names it ("GPT-5.3-Codex-Spark 5-hour"); the main limits keep their names.
- **The Codex card names the signed-in account.** UsageAI asks the Codex CLI which account is signed
  in, without refreshing the login; an older CLI that cannot answer still shows usage.
- **Claude shows your saved limit resets.** Claude grants some accounts free limit resets, and UsageAI
  now asks for them and shows how many you can use right now as "Reset credits"; paused, expired or
  not-yet-started grants are not counted.
- **Claude shows every model's own weekly limit.** Claude now reports weekly limits per model, such as
  Opus or Sonnet, and UsageAI shows each of them by name instead of only Opus.
- **GitHub Copilot works with GitHub Enterprise Cloud (`*.ghe.com`).** A Copilot sign-in to an
  Enterprise Cloud host is now read from that host's own API.

### Changed

- **The README previews show the current dashboard and Status Bar.** The August screenshots
  predated collapsible cards, the metric filter and the new provider icons. `npm.cmd run previews`
  now renders both from the compiled extension and sample providers in Dark Modern colours, so they
  can be regenerated after any UI change instead of retaken by hand.
- The Gemini CLI fallback now asks for quota on the Code Assist project Google reports for the
  account, rather than leaving Google to pick a default project.
- The Codex server UsageAI starts now runs with a read-only sandbox and never asks for approval
  (`-c sandbox_mode="read-only" -c approval_policy="never"` on `app-server` itself), so it can never
  be asked to run or approve anything, whatever sandbox your own Codex configuration uses.
- **The `agy` server UsageAI starts no longer updates itself.** It runs for as long as the editor
  does, so it kept the CLI's background updater alive too; UsageAI now sets
  `AGY_CLI_DISABLE_AUTO_UPDATE` for every `agy` it starts, and `agy` sessions you run yourself
  still update it.
- **Codex usage no longer starts the Codex CLI on every refresh.** UsageAI now reads it straight
  from ChatGPT with the access token your Codex sign-in already holds, the same request the CLI
  makes. When that token has expired, the sign-in is an API key, `config.toml` names another backend,
  or the request does not succeed, UsageAI asks the CLI as before, and the CLI renews the login
  itself; UsageAI never refreshes or writes `auth.json`. A rate limit from ChatGPT is reported with
  its retry window.

## 0.1.18

### Changed

- Google Gemini now reads usage from the `agy` server Google's Antigravity editor extension already
  runs, before starting a second one of its own. On hosts where the standalone `agy` keeps a
  separate login, such as Linux over Remote-SSH, that removes the extra `agy` sign-in reported in
  #17; everywhere it saves the duplicate server, about 175 MB while UsageAI runs. The token comes from
  that process's own command line, goes only to the port the process owns at that moment, and only
  the current user's processes are considered. The cold server's first quota-summary answer can take
  seconds, so it gets 15 seconds, and a reading without it falls back to the status data instead of a
  second server.
- The Antigravity probe now remembers the server that answered and, on later refreshes, only
  re-checks that the process still owns its port before asking it again. Measured here, a refresh
  dropped from about 970 ms (a PowerShell process listing) to about 35 ms. The listing itself now uses
  a provider-side CIM filter, about 390 ms instead of about 700 ms when it does run.
- On Linux, the probe reads listening ports from `/proc` instead of requiring `lsof`, which servers
  often lack and without which the Antigravity probe found nothing.
- The combined Gemini error now names Antigravity's IDE or editor extension rather than the IDE alone.
- **The extension uses UsageAI's new logo: a ring holding the quota that is left.** The
  Marketplace and Open VSX icon (`media/logo.png`) is the same mark as the desktop app, now a real
  PNG with transparent corners instead of a JPEG with a `.png` name, and the Activity Bar icon
  (`media/activity.svg`) is a one-colour version of it in place of the old dial, so the sidebar,
  the Marketplace listing and the tray app read as one product.
- **The packaging tools pick up `qs` 6.17.1, clearing a medium-severity advisory.** `@vscode/vsce`
  reaches `qs` through `typed-rest-client`, and 6.16.0 could throw from `qs.stringify` on crafted
  array elements (GHSA-6pq8-jhm2-2vcw). The fix sits inside the range `typed-rest-client` already
  allows, so only the lock file moves, and nothing in the published VSIX changes.
- **The packaging tools pick up `@azure/msal-node` 7.1.2, clearing a high-severity advisory.**
  `@vscode/vsce` reaches it through `@azure/identity`, and versions before 7.1.0 could disclose
  authorization-code parameters to an attacker-controlled endpoint (GHSA-xcx6-jx8p-x3h5). The fix
  sits inside the ranges already allowed, so only the lock file moves, and nothing in the published
  VSIX changes.

### Fixed

- **Claude no longer reads 100% when a limit is only 1% used.** Anthropic reports utilization as
  a percentage, but values up to 1 were taken for a fraction and multiplied by 100, so a fresh
  5-hour window at 1% showed as full until it passed 1%.

## 0.1.17

### Added

- Added support for the Codex and Claude Code CLIs bundled inside their official VS Code
  extensions. With only OpenAI's Codex extension or Anthropic's Claude Code extension installed and
  signed in, UsageAI now finds the CLI that extension ships, so no separate CLI install is needed.
  `CODEX_PATH` / `CLAUDE_PATH`, a CLI on `PATH`, and standalone install folders still come first.
  Only the extension's own fixed binary paths inside the folder VS Code reports are checked, never a
  search, and only for an extension in UsageAI's own extension host. When UsageAI runs locally in a
  remote window and finds no Codex CLI, the error now points to `remote.extensionKind`, since Codex
  may be installed on the remote host, where a local UsageAI cannot look.
- Added opt-in support for running on a remote host. The extension now declares
  `["ui", "workspace"]`, so it still runs locally by default in Remote-SSH, WSL, and dev container
  windows, and `remote.extensionKind` can move it to the remote host where providers are signed in
  there. The README explains the setting; remote use is community-tested.

### Changed

- Codex and Claude Code sign-in messages now mention signing in through the provider's extension.
  Someone using only the extension has no `codex` or `claude` command to run.
- Google Gemini now says which sign-in paths it tried when all of them fail. Because the Gemini CLI
  fallback runs last, its own "not signed in" used to be the only error shown, even when the
  Antigravity paths were the ones that mattered. The error now opens with "Google Gemini usage is
  unavailable" and what to check, then says that the Antigravity IDE and `agy` (or that `agy` is
  not installed) returned no usage. It no longer claims a sign-out it cannot know about, and a rate
  limit or outage on the Gemini CLI path keeps its own message instead of a sign-in hint.
- Renamed the sidebar view to UsageAI and its dashboard heading to Usage limits. The view title
  now reads **UsageAI** instead of **UsageAI: Usage**, and the heading above the provider cards
  says what they show instead of "Quota instruments".
- Expanded the suite to 58 checks covering bundled-CLI lookup, the remote-window hint, the combined
  Gemini error, and a manifest guard for the hosting order and view toolbar. Aggregate coverage is
  now 81.84% line, 81.42% branch, and 81.01% function coverage.

### Fixed

- Fixed the view's toolbar buttons overlapping the view title in a narrow sidebar. Refresh and
  Open Settings now show as the native `refresh` and `gear` icons, following the active product
  icon theme, with "Refresh" and "Open Settings" as their tooltips; a narrow sidebar now shortens
  the title instead of covering it. The Command Palette still lists them as **UsageAI: Refresh**
  and **UsageAI: Open Settings**, and the command IDs are unchanged.

## 0.1.16

### Added

- Added a Quota Display setting that shows remaining quota instead of used quota. With
  `usageai.quotaDisplay` set to `remaining`, dashboard meters fill by what is left, the card
  headline, metric rows, and collapsed summaries read `72% left`, and the Status Bar reads
  `Codex 72% | 59% left` with matching tooltip meters. Warning and critical colors, Hottest
  provider selection, and Important Only filtering still follow the quota used, so a nearly
  exhausted limit is flagged the same way in both modes. The default stays `used`, so nothing
  changes until the setting is switched.

### Changed

- Expanded the suite to 51 checks with a Claude CLI fixture that, like the real CLI, refreshes an
  expired login only through `mcp list` and never through `auth status`, and with used and remaining
  Quota Display rendering in the Status Bar and dashboard. Aggregate coverage is now 80.46% line,
  80.17% branch, and 81.18% function coverage.
- Updated the Open VSX publishing tool to `ovsx` 1.3.0, clearing a high-severity advisory in the
  build tooling. `ovsx` 1.2.0 bundled its own `@vscode/vsce` 3.9.2, whose `secretlint` chain pulls
  in `braces` 3.0.3 (GHSA-vfj7-8cjw-p6xm, which has no patched release); 1.3.0 shares the
  `@vscode/vsce` 4.0.0 already used for packaging. Nothing in the published VSIX changes.

### Fixed

- Fixed Claude Code staying stale once its access token expired while no Claude Code session was
  running. Recovery relied on `claude auth status`, which only reports that a login exists and never
  refreshes an expired token, so after about eight hours without a Claude Code session on the
  default profile the card kept its last snapshot until one happened to start. While the stored
  token is still expired, recovery now also runs `claude mcp list`, which needs a live token and
  makes the CLI refresh it through its own credential store. The extension returns as soon as the
  new token lands, bounds the child at 20 seconds, skips it whenever the stored token is still
  valid, and still never reads, exchanges, or writes Claude's refresh token.

## 0.1.15

### Changed

- Expanded the suite to 48 checks with provider parser edge cases, Codex protocol failures, bounded
  Claude CLI authentication probes, Gemini cloud and token-refresh failures, the Antigravity hub
  command line, start backoff, and forced-refresh recovery, metric-mode persistence failures, and
  process-owned port parsing. Aggregate coverage is now 79.89% line, 80.10% branch, and 81.41%
  function coverage.
- Mapping a process id to its listening ports no longer starts PowerShell. The Antigravity probe
  reads that mapping twice per language server, because it re-checks ownership after building the
  candidate list and before any CSRF token is sent, so a port the server has released in between is
  never handed one. `Get-NetTCPConnection` measured 1.2 seconds per call; `netstat -ano` answers the
  same question in 15 to 33 milliseconds.

### Fixed

- Fixed Google Gemini starting an `agy.exe` child process on every refresh. Antigravity CLI builds
  from September 2026 onwards ignore the CSRF token passed through the hub's environment and mint
  their own, so every hub call was rejected as unauthenticated and each refresh fell back to a
  short-lived `agy -p /usage` read, which boots MCP servers through `cmd.exe` and flashes a console
  window. The token is now also supplied as `--csrf_token`, the way the Antigravity IDE provisions
  the language server it starts, which both older and newer builds accept.
- Held off further Antigravity hub starts for 30 minutes after one fails to become ready, so a future
  change to the CLI costs one 20-second attempt per half hour rather than an extra child process and
  a 20-second stall on every poll.
- Cleared the Antigravity hub and `agy` probe backoffs when the user asks for a refresh, so a single
  missed hub start cannot hold a provider on the degraded per-refresh path until the window expires.
  The existing immediate-`agy` retry after both paths go stale now releases the hub backoff too.
- Fixed a dashboard metric-mode change that cannot be written to settings leaving the dashboard
  showing a mode the Status Bar and the next reload do not use. The mode is still applied immediately,
  but a rejected write now rolls it back and rerenders instead of being discarded unobserved.
- Applied dashboard metric-mode changes immediately in both the webview and extension host so All,
  Metered only, and Important only no longer depend on configuration-change propagation to rerender.

## 0.1.14

### Added

- Added persistent collapsible provider cards with semantic keyboard-focusable header buttons,
  `aria-expanded`, visible focus treatment, native Enter/Space behavior, chevrons, and content that
  leaves keyboard and accessibility navigation when collapsed.
- Added globally persisted per-provider expansion preferences under a versioned state key. Connected
  providers still start expanded until changed, and preferences survive view disposal, editor
  restart, workspace changes, provider reordering, and provider disable/re-enable.
- Added **Collapse all** and **Expand all** dashboard actions. Restored and incoming state is limited
  to known provider IDs and ignores malformed, duplicate, or unknown values.
- Added an unfiltered collapsed summary showing the highest-used metered metric, its percentage, and
  reset countdown, with **No metered limit** when the provider reports only balances or unlimited
  metrics.
- Added the `usageai.metricDisplayMode` setting and dashboard shortcut with `all`, `metered`, and
  `important` choices. Metered includes every usable percentage limit; Important selects the
  highest-used Session, Rolling, and Monthly metric with stable first-reported tie-breaking.
- Added **No metered limits reported** to expanded connected cards when the active filter removes all
  rows.
- Added one bounded Claude Code OAuth recovery attempt when the initial usage request is rejected as
  unauthorized/forbidden or fails with unexpected unavailable data. The extension asks the official
  `claude auth status --json` command to validate or refresh the login, rereads the credential file,
  and retries usage once without handling or writing the shared refresh token. Explicit environment
  token overrides remain authoritative and bypass this fallback.

### Changed

- Disconnected, errored, and stale cards now expand temporarily so recovery details cannot be hidden.
  Temporary attention never overwrites the saved preference, and recovered cards return to the
  user's remembered state.
- Dashboard metric-mode changes are validated in the extension host, stored globally through VS
  Code's configuration API, and rerender immediately without fetching provider data.
- Metric filtering applies only to expanded dashboard rows. Collapsed summaries and Status Bar
  selection retain the complete provider response and their previous curated behavior.
- Chevron and spinner animation now honors the editor's reduced-motion preference.
- The dashboard now executes the same exported pure selection, attention, and collapsed-summary
  functions covered by the test suite instead of maintaining test-only equivalents.
- Expanded the editor-extension suite to 38 checks. New fixtures cover manifest settings,
  mirrored metric selection, empty and future-kind inputs, global-state sanitization, individual and
  bulk expansion persistence, malformed messages, forced attention and recovery, unfiltered summary
  ties, semantic/hidden webview markup, activation wiring, configuration updates, Claude
  authorization/invalid-response recovery, bounded single retries, and failed CLI validation.
  Aggregate coverage is now 74.10% line, 71.12% branch, and 78.90% function.

### Fixed

- Fixed Claude Code remaining stale when its access token stopped working before the saved expiry
  time, or when a one-off invalid usage response recovered after Claude CLI validation.
- Declared the dashboard metric display mode as application-scoped so a workspace override cannot
  mask changes made with the dashboard selector.
- Prevented temporary stale or disconnected expansion from silently changing a user's saved card
  preference.
- Prevented unknown provider IDs, invalid expansion booleans, and invalid metric modes from being
  persisted or acted on by the extension host.
- Preserved the earliest provider-reported metric when equal percentages tie within the same kind,
  including duplicate Gemini Session and Rolling limits.
- Kept provider names, plan/account details, errors, and summary text escaped through DOM
  `textContent`; no provider content is injected as HTML.

## 0.1.13

- Read Google Gemini quota from one long-lived Antigravity CLI `--hub` server for the session instead of
  starting `agy -p /usage` on every refresh. Refreshes now spawn no child process, and the CLI starts any
  configured MCP servers through `cmd.exe` once per session rather than on every refresh, which is what
  produced transient console windows on Windows.
- Skipped the Antigravity process probe, and the PowerShell child process it costs, while a hub is
  serving the session.
- Removed the Antigravity CLI port-discovery probe: the service rejects the request bodies it sent, so
  the HTTP query never produced a snapshot.
- Passed `-NonInteractive` to the remaining PowerShell process and listening-port queries so a prompt can
  never block a background refresh.

## 0.1.12

- Prevented background Gemini refreshes from preferring the standalone `agy` on `PATH` or allowing
  its headless `/usage` probe to become interactive.
- Made a stale Gemini reading recover from sleep in one refresh even when an earlier `agy` cold-start
  failure was cached, and replaced obsolete Gemini CLI-only sign-in guidance with `agy`.

## 0.1.11

- Added Gemini quota discovery through the official Antigravity VS Code extension's locally installed
  `agy` backend, including its current snake-case `/usage` response format, so VS Code does not need
  to remain open after sign-in.
- Added rotating refresh indicators to dashboard cards, the dashboard header, and Status Bar
  readings while retaining the last visible percentages. Dashboard actions, the view-title button,
  the Status Bar hover action, and **UsageAI: Refresh** now share VS Code's native window progress
  indicator, including when the dashboard is closed or UsageAI Status Bar items are disabled.
- Fixed retry-only refreshes polling healthy providers and postponing the normal refresh schedule.
  Failed providers now retry independently as soon as their backoff or provider-supplied wait time
  expires, and a successful automatic retry clears their stale state without manual intervention.
- Clarified stale timestamps in the dashboard and Status Bar. Stale views now distinguish the last
  successful reading from the later failed check and show when the next automatic retry is due.
- Fixed disabled providers retaining expired backoff timers and waking the extension scheduler once
  per second. Only enabled providers can now influence the next retry wake-up.
- Ignored malformed cached snapshots and invalid reset timestamps during activation so corrupt or
  obsolete global state cannot break dashboard or Status Bar rendering.
- Expanded automated coverage from parser-focused fixtures to live provider request flows, bounded
  HTTP/file/process handling, OAuth refresh reuse, PID-bound Antigravity port revalidation,
  activation, dashboard messaging, and Status Bar lifecycle behavior. CI now enforces line, branch,
  and function coverage thresholds.

## 0.1.10

- Restored automatic Claude usage recovery after access-token expiry through a bounded official
  `claude auth status --json` probe. Claude Code remains the only process that refreshes or writes its
  credentials; UsageAI rereads the result without accessing the shared refresh token.

## 0.1.9

- Fixed the dashboard and Status Bar preview images on Visual Studio Marketplace and Open VSX by
  resolving packaged README assets from the extension's directory in the monorepo.

## 0.1.8

- Added a bounded official `agy /usage` fallback for Gemini when no local Antigravity language server
  is running, with closed stdin, process-tree cleanup, and negative caching after failed cold starts.
- Made Claude Code authentication strictly read-only so the extension no longer exchanges the CLI's shared refresh token or leaves `.credentials.json` with a stale rotated token.

## 0.1.7

- Added provider-specific Codex, Claude, GitHub Copilot, and Gemini icons to Status Bar readings.
- Added side-by-side session and weekly percentages, segmented quota meters, the last-updated time, and Open/Refresh actions to the Status Bar hover card.

## 0.1.6

- Applied the general Providers order to selected Status Bar items instead of rebuilding them in a fixed order.

## 0.1.5

- Organized the Settings UI into General, Status Bar, and Usage Levels sections.

## 0.1.4

- Added native Settings UI checkboxes for choosing each Status Bar provider.
- Retained the earlier array and single-value setting as a hidden compatibility fallback.

## 0.1.3

- Changed reset countdowns of one day or longer to use days and hours instead of large hour and minute values.

## 0.1.2

- Added multi-provider Status Bar selection, with one compact item per selected provider.
- Kept existing single-provider settings compatible during upgrades.

## 0.1.1

- Fixed the Activity Bar and view icons rendering as a solid white square by using a dedicated monochrome, theme-tinted quota dial.

## 0.1.0

- Added a persistent UsageAI webview view and Status Bar summary.
- Added pure TypeScript clients for Codex, Claude Code, GitHub Copilot, Gemini CLI, and local Antigravity quota probing.
- Added per-provider exponential backoff, throttle hints, stale snapshot preservation, and cached last readings.
- Added strict webview CSP, bounded credential/HTTP input, redirect blocking, minimal child environments, and PID-bound Antigravity probing.
- Added parser, credential-normalization, model, and refresh-orchestration tests.
