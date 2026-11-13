# Changelog

All notable changes to UsageAI are documented here.

The project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **Search Settings from the box above the page list.** Type part of a setting's name, or a
  word people use for it such as "dark", "login" or "startup", and the results drop down under
  the box, grouped by page. Choosing one opens its page, scrolls the setting into view, moves the
  focus to it, and marks it with a band in the accent colour that fades. Ctrl+F or Ctrl+E jumps
  to the box; Up, Down and Enter pick a result, and Escape closes the results, then clears the
  box. Provider and account names are found too.
- **Each page in Settings has an icon**, drawn with Windows' own icon font, in the page list and
  in the search results.

### Changed

- **Settings shows one page at a time instead of one long scrolling page.** The list on the
  left now switches between General, Alerts, Appearance, Providers, Accounts and About;
  Ctrl+Tab and Ctrl+Page Down move to the next page. History moved onto General, and what the
  tray icon shows moved to Appearance. Each page opens at its top, and the window opens tall
  enough for the longest page; a size you choose is remembered from then on.
- **Every change in Settings applies as you make it, so Save and Cancel are gone.** A theme or
  quota change shows in the popup and tray at once; number boxes save once you stop changing
  them, and providers ticked in a row lead to one refresh, not one each. Escape still closes the
  window. Deleting recorded history, and removing an account UsageAI created, now ask first,
  with No as the answer Enter gives. Reordering providers or accounts in Settings asks before it
  replaces the card order you dragged in the popup.
- **Settings is a window of its own, with a taskbar button, and can be minimized.** Opening it
  again brings back the one already open, on the page asked for. The tray menu's quick settings
  stay available while it is open, and Settings shows what they change.

### Fixed

- **Settings opened on a monitor at a different scale is laid out for that monitor.** It opens
  on the monitor under the mouse, and opened there its text took that monitor's scale while the
  window, its columns and its buttons kept the other one, so labels were cut short and "Move
  down" lost a word.

## [0.19.2] - 2026-11-13

### Fixed

- **Settings opened from the popup stays on screen and answers clicks again.** The popup hides
  when Settings takes the focus, and since 0.19.0 a hidden window is kept invisible to Windows
  until it next paints. Windows applied that to Settings too, because it belongs to the popup,
  so it vanished while still holding the app. A window that owns one on screen is no longer
  hidden that way, and a dialog uncovers its owner as it appears. The same covered What's New,
  Add account and the update prompts when opened from the popup.

## [0.19.1] - 2026-11-13

### Fixed

- **Clicking a tile in the popup no longer crashes UsageAI.** In 0.19.0 the click opened the
  dashboard, which replaces the popup's tiles, and the button's release then reached the tile
  that had just been removed, so Windows showed an unhandled-exception dialog instead of the
  dashboard.

## [0.19.0] - 2026-11-13

### Added

- **Drag any provider or account to reorder it, in the popup and the dashboard.** In the popup,
  drag a tile onto another; in the dashboard, drag a provider by its heading row. With the
  keyboard, focus a tile, or one of a provider's links in the dashboard, and press Alt with an
  arrow key. Each card moves on its own and lands exactly where you drop it, so a second account
  can sit anywhere, not only next to its provider's first. While you drag, the card fades and a
  line shows where it will land; Esc gives the drag up. A new account appears after the last
  card of its provider. The tray tooltip's All connected list follows the same order, and
  nothing is polled when it changes. Until you drag a card the order is the one from Settings,
  and reordering providers or accounts in Settings afterwards goes back to that grouped order.

- **The tray menu changes the settings you switch most, without opening Settings.** Right-click
  the tray icon for **Tray icon shows** (Automatic, All connected, or any shown provider or
  account), **Show quota as** (Used or Remaining) and **Pause notifications** / **Resume
  notifications**. The chosen entry is ticked, the lists are read again each time the menu opens,
  so a new account appears at once, and a change redraws the tray icon, popup and dashboard from
  the readings already there, with no new request to any provider. While the Settings window is
  open these entries are greyed out, since its OK button would write its own values over them.

### Fixed

- **Claude Code says when its login was switched to an API key.** Signing in to Claude Code
  with Console (API) billing, for example from the Claude Code editor extension, replaces the
  Claude subscription login in that folder, so UsageAI had no subscription to read and only said
  the saved login could not be read, which looked like an unexplained sign-out. When the folder
  holds an API key instead, the Claude card now says so and how to sign in with the subscription
  again. Only whether a key is stored is read; the key itself is never shown or kept.
- **Windows no longer flash white as they open.** Windows shows a new window before it has drawn
  anything, so on the dark theme Settings opened as a white sheet for about 50 ms, then as white
  blocks where its controls were still to paint; the dashboard, What's New and the popup did the
  same for 20 to 65 ms. Each window now stays cloaked until it and its controls have painted, and
  appears finished in its first frame. Open dashboard from the popup also stopped flickering: the
  popup used to take on a frame and the dashboard's size on screen, vanish while Windows recreated
  it for the taskbar, and reappear where it had been before jumping to the dashboard's place. It is
  now taken off the screen first, and the dashboard appears once, in its place.

## [0.18.0] - 2026-11-10

### Added

- **Terminal commands you made yourself show up on their account.** Each account scan now looks
  through the local folders on your PATH for `.cmd`, `.bat` and `.ps1` files that set
  `CLAUDE_CONFIG_DIR` or `CODEX_HOME` to an account's folder, such as a `claude-work.cmd` written
  long before UsageAI could make one. Settings lists them in the account's details, and Terminal
  command… no longer suggests a name you already use. They are only read: UsageAI never rewrites or
  deletes them, and Forget says so. Only the folder variable is taken from each file; anything else
  in it, such as a key, is never kept. Network folders, files over 16 KB and UsageAI's own commands
  are skipped, and with Find accounts automatically off the PATH is read only when you press Rescan.

### Changed

- **The tray icon is the UsageAI tank whichever provider or account it follows.** Choosing a
  single provider or account, or leaving it on Automatic, used to bring back the older ring gauge;
  only All connected drew the logo. Every choice now draws the tank, filled to the limit its tooltip
  names and turning amber and red at the same thresholds. Following one provider, its ring wears
  that provider's colour while usage is calm, so Automatic still shows which one it follows. With no
  provider reachable the icon is still a red ring with `!`.

### Fixed

- **Claude's saved limit resets show up again, each kind on its own row.** Claude now reports
  resets only to its own CLI. Every other client gets an empty list marked `ineligible_reason:
  "surface"`, so the "Reset credits" row had quietly disappeared. The one usage request that asks
  for resets now names itself as the CLI does (`claude-cli/<version> (external, cli)`). The version
  is read from the installed CLI's files, never by running it: npm's `package.json`, the Claude Code extension's folder name, or `claude.exe`'s file version.
  `2.1.0` is used when none is found. If Claude refuses that request with a 400, 401 or 403, it is
  sent once more as a plain request, so your limits never depend on the CLI name. The plain retry
  and every other request keep UsageAI's own agent. Each kind of reset gets its own row, named the
  way the Claude CLI names it: "Fable reset" (it gives back what Fable used this week), "5-hour
  reset", "Weekly reset" or "Full reset". Each row shows how many you have and the earliest use-by
  date, with the year only when it isn't this year.
- **The Tray icon provider list names your main login's account too.** While a provider shows more
  than one account, its main login read only as "Claude Code", so it was easy to take for the
  provider as a whole. It now carries the same label as in the popup ("Claude Code · me@example.com"),
  and each extra account is listed right after its provider instead of at the end.
- **The tray menu's labels sit in the middle of their rows.** Windows lays a menu row's text out
  from its top, so with the roomier rows every label sat high with all its spacing below it. Each
  label is now centred between the row's top and bottom, in High Contrast too.

## [0.17.3] - 2026-11-09

### Fixed

- **Settings scrolls smoothly, without flashing.** Each scroll step repainted the strip it uncovered
  with the section cards drawn straight onto the window. Their rounded, antialiased edges made
  Windows read the screen back, which took about 16 ms a step, a whole frame at 60 Hz, so the page
  stuttered, and the strip showed bare background before its card appeared. The page now paints
  through an off-screen buffer: a scroll step takes about 2 ms (about 5 ms for a full mouse-wheel
  notch), and the cards appear whole.

## [0.17.2] - 2026-11-09

### Changed

- **Your main Claude Code and Codex login is always `%USERPROFILE%\.claude` and `.codex`.** It used
  to follow `CLAUDE_CONFIG_DIR` or `CODEX_HOME` whenever UsageAI was started with either variable
  set. Starting it from a terminal set up for another login quietly swapped the main tile to that
  login and hid the real account behind it as a duplicate. The account's usage history was cleared
  in the process. Now the folder such a variable names appears as one more account, found like a
  `.claude-*` folder, and the main tile always reads the standard folder. The Claude Code and Codex
  commands UsageAI runs for the main login get neither variable, so they read the same folder.
- **Settings and banners name the folder instead of saying "default login".** The main login reads
  as `.claude`, and its details as "Standard folder: %USERPROFILE%\.claude". A change of person
  reads as "The Claude login in %USERPROFILE%\.claude is now …".

### Fixed

- **A login that was the main one only because of `CLAUDE_CONFIG_DIR` or `CODEX_HOME` keeps its
  history.** If you had set the variable permanently, the first start after this update moves that
  login to an account of its own. Its usage history, alert memory, label, alert switch, tray choice
  and popup-ring choice move with it, and it keeps the main tile's place in the list, so it doesn't
  start over as if it were someone new. If UsageAI had already found that folder as a duplicate
  account, or you had forgotten that duplicate, the records merge into that account and it is shown
  again. If the standard folder has no login, its tile stays hidden until one appears there, then
  comes back by itself.
- **A login folder named by `CLAUDE_CONFIG_DIR` or `CODEX_HOME` is offered even with automatic
  finding off.** The variable itself names the folder, so startup offers it without listing your
  profile.
- **The main login's sign-in hint works in a terminal that has `CLAUDE_CONFIG_DIR` or `CODEX_HOME`
  set.** When UsageAI inherited either variable, the copied command clears it first, so the sign-in
  lands in the standard folder the main tile reads instead of the variable's folder.

## [0.17.1] - 2026-11-07

### Changed

- **Any login folder whose name starts with `.claude` or `.codex` is found now.** Names such as
  `.claude2`, `.claudework` or `.codex.work` are found as well as `.claude-*` and `.claude_*`. Only
  a folder that holds the provider's own login file counts, so the wider rule picks up nothing
  else. A backup copy of a login already shown is marked as a duplicate and stays hidden. The bare
  `.claude` and `.codex` remain the default logins.

### Fixed

- **A Codex account's terminal command now starts that account, not the default login.** The npm
  install of Codex is a batch file (`codex.cmd`). When the command started it directly, cmd ended
  the command first and dropped its `CODEX_HOME`, so Codex fell back to your default `~\.codex`
  login. The command now starts the CLI in a child `cmd`, which inherits the folder and passes
  arguments through unchanged. A Claude Code installed through npm had the same problem. Commands
  you already made are rewritten at the next start of UsageAI.

## [0.17.0] - 2026-11-06

### Added

- **An account can have its own terminal command, such as `claude-work`.** Choose **Terminal
  command…** under Settings → Accounts. UsageAI suggests a name from the account's label and writes
  a small `.cmd` file that sets `CLAUDE_CONFIG_DIR` or `CODEX_HOME` to that login folder and starts
  the CLI. The files go in `%LOCALAPPDATA%\UsageAI\bin`, which is added to your user PATH once.
  UsageAI refuses a name that another command on your PATH already uses, and it only ever changes
  or deletes files it wrote itself. The command never runs a `claude.bat` or `codex.cmd` that
  happens to be in your current folder, such as a cloned repository, in place of the real CLI. When
  the CLI is the one bundled in an editor extension, the command is pointed at the new copy after
  each extension update, at startup or on the daily account check. Forgetting the account deletes
  its command, and uninstalling removes the PATH entry along with UsageAI's data folder.

### Changed

- **The popup calls Claude Code "Claude", so an account's tile reads "Claude · Personal" in full.**
  "Claude Code · Personal" was cut off with an ellipsis on a half-width tile. The popup header uses
  the short name too. The dashboard table still says Claude Code, since it has the room.

### Fixed

- **Clicking an account in Settings → Accounts selects it without hiding it.** Every click on a
  row used to toggle its tick, so selecting an account to see its details also hid it, and clicking
  it again showed it once more. Now the box, or Space, shows or hides the account, and a click on
  its name only selects it.
- **An account added from Settings shows Connected without reopening the window.** The Accounts
  page used to keep the readings it opened with, so a new account said "Not read yet" until you
  looked at the popup or dashboard. It now updates whenever a reading arrives.
- **The mouse wheel scrolls the Settings page over the Providers and Accounts lists.** Each list
  used to swallow the wheel even with nothing of its own to scroll, so the page stopped scrolling
  under the mouse. A list now passes the wheel to the page whenever it cannot scroll further that
  way itself.
- **The tray menu's items all use one font size again.** The bold **Open dashboard** had its own
  font, separate from the one the other items share. It could end up about a quarter larger than
  the rest, which made its row taller and the menu look out of line. All items now share one face
  made for the menu's current scale, and each time the menu opens it puts that face back on any
  item whose font has changed. Opening the menu on a monitor at another scale still rescales it
  once, correctly.
- **The Tray icon provider and popup-ring dropdowns no longer touch the hint below them.** The
  explanation under each dropdown started about two pixels below it, so the two ran together. It
  now keeps the same gap as the rest of the page.

## [0.16.0] - 2026-11-06

### Added

- **Claude Code and Codex can show several accounts, each on its own tile.** Login folders in your
  user folder named `.claude-*` or `.codex-*` are found at startup, once a day and with **Rescan**
  in Settings. Logins already there when you install or upgrade show at once, with one banner that
  names them; one found later waits in a banner for **Show** or **Ignore**, so an account never
  appears unannounced. A provider with one account keeps its plain tile; with several, each reads
  "Claude Code · label" in the popup and dashboard, "Claude · label" in the tray tooltip and alerts.
- **Add account… signs in to another account or adds a login folder you already have.** It is in
  the popup header, the tray menu and Settings → Accounts. Signing in runs the provider's own
  `claude auth login --claudeai` or `codex login` with `CLAUDE_CONFIG_DIR` or `CODEX_HOME` set to a
  new folder, by default `%USERPROFILE%\.claude-<name>`, which your own CLI can use too. The dialog
  shows the sign-in link in case the browser did not open, gives up after two minutes, and removes
  the folder again if the sign-in did not finish.
- **Settings has an Accounts page for every Claude and Codex login.** Each account can be shown or
  hidden, renamed, moved, given its own alerts switch, re-authenticated, opened in Explorer,
  located after a move, and removed. Removing an account UsageAI created deletes its folder after a
  confirmation that names it; any other folder is only forgotten and never touched.
- **One PowerShell line installs or updates UsageAI.** `irm
  https://raw.githubusercontent.com/VladiKogan/UsageAI/main/scripts/install.ps1 | iex` downloads the
  latest Setup.exe from GitHub Releases, refuses it unless it matches its published SHA-256, installs
  it silently and starts the app.
- **`--diagnose` lists every account of a provider.** With more than one it prints an array,
  default login first, each with its folder and label; `--account <label|email|id>` picks one.

### Changed

- **The tray icon keeps following your default logins.** Its automatic choice ignores extra
  accounts, so adding one never moves the tray; pick an account under Providers → Tray icon
  provider to follow it instead.
- **A login folder now signed in to someone else starts fresh.** UsageAI notices at the next
  refresh, clears that account's history and alert memory, and says who is signed in now.
- **The uninstaller offers to delete the login folders UsageAI created.** It lists them by path and
  defaults to keeping them; other `.claude-*` and `.codex-*` folders are never offered.

## [0.15.1] - 2026-11-05

### Changed

- **An in-app update shows its own progress and says when it is done.** The installer used to open
  a window that sat on "Closing applications…" and then vanished without a word. Setup now runs
  without a window. A small panel in the tray corner fills a bar while the installer downloads and
  is verified. The copy Windows reopens finishes the bar and says "UsageAI updated", with a link to
  What's New. If the installer fails or is cancelled, the panel says so, with the release page.
  Setup also runs with `/NORESTART`, so a very silent install can never reboot Windows unasked.

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
- **Updating no longer freezes for 30 seconds.** UsageAI ignored the request Windows' Restart
  Manager sends to close an app before its files are replaced. Every update waited out Restart
  Manager's 30-second timeout and then had the tray app killed: 30.2, 30.5 and 30.3 seconds in the
  0.13.0, 0.14.0 and 0.15.0 setup logs, against about a second for the install itself. The app now
  exits as soon as Windows asks, which measured 0.2 seconds, and also on sign-out and shutdown.
  The clean exit also stops UsageAI's own `agy --hub`, which each forced close had left running.
- **Downloaded installers no longer pile up.** Every in-app update left its Setup file of about
  2.5 MB in `%LOCALAPPDATA%\UsageAI\updates`, and none was ever removed. They are now deleted when
  the next version starts. Developer previews (`--preview-*`) likewise delete their throwaway data
  folder when they end, and clear any a killed preview left behind for over a day.
- **Uninstalling no longer leaves a broken startup entry.** With "Start with Windows" on, the
  uninstaller left the startup entry behind, so Windows kept listing a startup app whose file was
  gone. It is now removed with the copy it starts; a portable copy's own entry is left alone. The
  uninstaller also asks whether to delete the settings and recorded history in
  `%LOCALAPPDATA%\UsageAI`, keeping them unless the answer is yes, so a reinstall picks up where it
  left off.

## [0.15.0] - 2026-11-05

### Fixed

- **GitHub Copilot names a personal plan as Pro, Pro+ or Free instead of "Individual".** GitHub
  reports `copilot_plan` as `individual` for every personal plan, so a Copilot Pro account,
  including one granted free to a verified teacher, student or open-source maintainer, read as
  "Individual". The plan is now named from the account's `access_type_sku` when it is one UsageAI
  recognises; an unrecognised SKU and every Business or Enterprise plan keep the name GitHub reports.
- **Google Gemini says when Google has stopped serving Gemini CLI quota for a plan.** Since June
  2026 Google no longer answers Gemini CLI usage requests for personal, Google AI Pro and Ultra
  accounts, and UsageAI took the refusal for an expired sign-in, asking for a sign-in that could
  never help. It now recognises Google's `UNSUPPORTED_CLIENT` answer and points to Antigravity or
  `agy` instead. Code Assist Standard or Enterprise and Workspace accounts keep the sign-in message.
- **A Claude plan without usage limits no longer shows as an error.** Free and some education plans
  report no five-hour or weekly window, and the card went stale with a parsing error; it now says
  Claude reports no usage limits for the plan.
- **A Cloudflare browser check on claude.ai no longer reads as a dead web session.** When claude.ai
  answers the opt-in web path with Cloudflare's challenge, UsageAI keeps the session key, uses Claude
  Code OAuth, and leaves claude.ai alone for 15 minutes instead of asking again on every refresh.
- One unexpected text value in a Claude usage answer no longer fails the whole reading; that field is
  skipped.
- **GitHub Copilot says when a subscription has ended.** GitHub answers an ended subscription with
  an ordinary reply, which read as invalid usage data; the card now says the subscription has ended.
- **A GitHub Enterprise Copilot token is never sent to github.com.** UsageAI sent every Copilot
  token it found to api.github.com, including one that belongs to a company's own GitHub Enterprise
  Server. Each token now goes only to the GitHub service it came from, and a token for a host
  UsageAI does not read from is not used at all.

### Added

- **Codex shows model-specific limits such as Spark.** Codex reports a separate rate-limit bucket for
  some models, and UsageAI read only the main one. Each extra bucket now shows its own limits, named
  the way Codex names it ("GPT-5.3-Codex-Spark 5-hour"). The main 5-hour and weekly limits keep
  their names, so their history and alerts carry on unchanged.
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

- **The sample Codex account in the previews now has its 5-hour window.** A Codex Plus plan
  reports a 5-hour limit as well as the weekly one, but the sample data behind `--render-preview`,
  `--preview-window` and `--preview-tray` gave it only the weekly limit, so the README screenshots
  showed a plan that does not exist. The sample now reports both, with history inside the current
  5-hour window so its forecast reads too, and the README previews are regenerated from it.
- The Gemini CLI fallback now asks for quota on the Code Assist project Google reports for the
  account, rather than leaving Google to pick a default project.
- **The Codex server UsageAI starts is sandboxed.** It runs read-only and never asks for approval
  (`-c sandbox_mode="read-only" -c approval_policy="never"` on `app-server` itself), so it can never
  be asked to run or approve anything, whatever sandbox your own Codex configuration uses.
- **The `agy` server UsageAI starts no longer updates itself.** It runs for as long as UsageAI does,
  so it kept the CLI's background updater alive too; UsageAI now sets
  `AGY_CLI_DISABLE_AUTO_UPDATE` for every `agy` it starts, and `agy` sessions you run yourself
  still update it.
- **Codex usage no longer starts the Codex CLI on every refresh.** UsageAI now reads it straight
  from ChatGPT with the access token your Codex sign-in already holds, the same request the CLI
  makes, which spares the `cmd`, `node` and `codex` processes a refresh used to start. When that
  token has expired, the sign-in is an API key, `config.toml` names another backend, or the request
  does not succeed, UsageAI asks the CLI as before, and the CLI renews the login itself; UsageAI never
  refreshes or writes `auth.json`. A rate limit from ChatGPT is reported with its retry window.
  `--diagnose codex` names the path that answered in `Source`.
- **UsageAI shows when a provider reports an incident.** When Claude, GitHub Copilot or Codex reports
  degraded service or an outage on its public status page, the tray icon gets a dot (orange for
  degraded or maintenance, red for an outage), the tooltip names the incident, and the dashboard's
  provider header shows it, so a card that went stale can be told apart from a provider that is
  down. Only the components that serve the usage UsageAI shows count. The pages are read at most
  every five minutes without any credential, and "Show provider incidents" in Settings turns it off.
  On a popup tile the incident turns the header indicator into a "!" whose hover text names it.
  The editor extension shows the same incidents on its Status Bar item and dashboard cards.
  Gemini has no status page that covers its quota.
- **The dashboard says whether you are on pace.** For a limit that is not forecast to run out
  before it resets, the "At this pace" column now compares use with the time the window has run:
  "On pace", "12% under pace", or "8% over pace" in the warning colour. A limit forecast to run out
  still shows when.
- **Forecasts work from the first reading.** Without enough recorded history — just after starting,
  or with History turned off — the run-out forecast now comes from the window's own average since it
  began, once 5% of it has passed. Recorded history still wins when there is enough of it, and
  turning Forecasts off hides both the forecast and the pace.
- **`--diagnose` lists every source a provider tried.** An `Attempts` array names each path in order —
  for Gemini the serving `agy` server, the Antigravity IDE or editor extension, `agy` itself and the
  Gemini CLI; for Claude the web session, OAuth and the CLI recovery; for Codex the HTTPS read and the
  app-server — with a short outcome and how long it took. It is printed even when every source
  fails. Outcomes come from a fixed list, so no token, port or provider text can appear in them.
- **Usage alerts survive a restart.** UsageAI now remembers each limit's last reading and its
  window between runs, so a limit that crossed an alert threshold while UsageAI was closed alerts
  once when it starts, one you were already alerted about stays quiet, and a window that rolled over
  meanwhile starts its thresholds afresh. A clean install still stays quiet, and hiding a provider
  forgets it. The readings live in `alert-state.json` next to the other local state.
- **UsageAI no longer polls while your PC is locked.** Nobody can see the tray then, so scheduled
  refreshes wait and unlocking refreshes at once, as before. That spares the provider requests and
  processes a locked machine used to make overnight.

## [0.14.1] - 2026-11-04

### Changed

- Google Gemini now reads usage from the `agy` server Google's Antigravity editor extension already
  runs, before starting a second one of its own. That saves the duplicate server, about 175 MB while
  UsageAI runs, whenever VS Code or another editor with the Antigravity extension is open, and it uses
  that editor's own sign-in. The token comes from that process's own command line and goes only to
  the loopback port the process owns at that moment. A cold server's first quota-summary answer can
  take seconds, so it gets 15 seconds, and a reading without it falls back to the status data instead
  of starting a second server.
- The Antigravity process listing now uses a provider-side CIM filter. It measured about 390 ms
  instead of about 700 ms, and it runs only when a newly started language server or `agy` process
  appears.
- `--diagnose gemini` now reports which path answered in a `Source` field: the Antigravity IDE, the
  editor extension's `agy` server, UsageAI's own `agy` server, `agy -p`, or the Gemini CLI.
- The combined Gemini error now names Antigravity's IDE or editor extension rather than the IDE alone.

### Fixed

- **Claude no longer reads 100% when a limit is only 1% used.** Anthropic reports utilization as
  a percentage, but values up to 1 were taken for a fraction and multiplied by 100, so a fresh
  5-hour window at 1% showed as full until it passed 1%.

## [0.14.0] - 2026-10-29

### Added

- **The tray can list every connected provider when you point at it.** Choose **All connected**
  under Settings → Providers → Tray icon provider and the tooltip shows one line per provider with
  the limits its popup tile draws, shortest window first — `Codex 10% | 22%`, `Copilot 33%` — under
  the same short names as the editor extension's Status Bar items. A multi-group provider such as
  Gemini reads the same model group as its popup tile, **Show quota as: Remaining** adds one
  trailing `left`, and a provider without a metered limit says so rather than reading 0% or 100%
  left. Windows caps a tray tooltip at 127 characters, so providers that would overrun it are
  counted in a closing `+N more` line rather than cut off mid-reading.
- **In that mode the tray icon is the UsageAI logo, with its tank filled to the busiest limit it
  lists.** No single provider's ring stands for every provider, so the logo's tank holds the
  highest percentage the tooltip lists — or what is left of it when quotas show as remaining. Its
  water and ring turn amber and then red at the Warning and Critical colour thresholds in
  Settings; the ring changes too so that a spent quota shown as remaining, an empty tank, still
  reads as critical. Under Windows High Contrast, which draws every status in one system colour,
  the tooltip marks those limits with `!` and `!!` as the popup does, and the single-provider
  tooltip now does the same. The tank is drawn live at the size the taskbar asks for with the
  logo's own geometry, and the refresh spinner and the no-providers warning still replace it.
- **`--preview-tray` runs the whole tray app on sample providers.** The other previews open one
  window and never put an icon in the tray, so the tray icon, its tooltip and the alerts could only
  be seen with real accounts at whatever usage they happened to report. This mode runs the real app
  — tray, popup, dashboard, Settings and notifications — on four sample providers, with the update
  check and startup registration stubbed and settings kept out of the user's profile. Each
  **Refresh** from the popup or tray menu steps the readings from calm to between the warning and
  critical thresholds, then past critical, and round again, aiming at whatever thresholds Settings
  holds. Automatic polls and the refreshes after sleep or unlock leave the stage alone, the sample
  reset times move with the clock, and the preview never answers a relaunch meant for an installed
  copy, so a screenshot or a screen recording shows the tank change on cue.

### Changed

- **The tray tooltip uses the editor extension's short provider names.** Claude Code reads as
  "Claude" and Google Gemini as "Gemini", as on the extension's Status Bar, so a provider reads the
  same on both sides.
- **UsageAI has a new logo: a ring holding the quota that is left.** The old neon gradient ring
  and glowing spark blurred into a smudge at the 16 px taskbar and title-bar sizes, and its
  gradient-and-glow look read as a generic AI app rather than a usage meter. The new mark is a
  light ring with a level of cyan inside it on a dark tile, so it reads on light and dark taskbars
  alike, and at 24 px and below it switches to a thicker ring and a flat waterline so it stays
  crisp. `Resources\app.ico` now carries 16, 20, 24, 32, 40, 48, 64, 128 and 256 px frames, adding
  the 20, 24 and 40 px sizes Windows picks at 125% and 150% scaling instead of scaling a
  neighbouring frame. `Resources\logo.png` is now a real PNG with transparent corners; it was a
  JPEG with a `.png` name. All of them, and the new vector master `Resources\logo.svg`, come from
  `scripts\render-logo.ps1`, which uses only `System.Drawing`, so the icon and the logos cannot
  drift apart. Settings, What's New, the dashboard and the update prompts now show the logo in
  their title bars and taskbar buttons, where they showed Windows' generic window icon.
- **The tray app has a quieter, Windows 11 look.** The navy palette and a hue per usage band read
  as dated and made every card compete for attention; windows now use a neutral charcoal (or
  white) palette where only a limit at risk is coloured. Meters below the warning threshold share
  one calm colour instead of switching to blue at 50%, and a figure is coloured only when its
  limit needs attention, in shades chosen to keep 4.5:1 contrast on its surface. Usage figures use
  Bahnschrift, the gauge face that ships with Windows, and metric names are shown as providers
  report them instead of in upper case. Provider icons drop their tinted tile so brand colour only
  identifies the provider. The popup gets Windows 11's rounded corners and a theme-coloured frame,
  while the dashboard, Settings and What's New keep the system frame and any accent-coloured
  border the user has turned on. The accent colour now comes from the same light and dark shades
  Windows 11 derives for its own controls, and is adjusted until the button stands out at 3:1 and
  its label reads at 4.5:1, so a pale yellow or deep navy accent no longer gives an invisible
  button or an unreadable label.
- **The tray popup shows each provider as ring gauges, two providers to a row.** The popup used to
  stack one tall card per provider with a single `92% USED` headline, so a second limit (Claude's
  weekly window behind its 5-hour one) never appeared until the dashboard was opened. Each tile
  now carries the provider's glyph and up to two rings side by side, shortest window first, each
  with its figure inside and its window and countdown beneath; a one-ring provider uses the free
  space for its balance or what is left of its allowance. The rings follow the dashboard's
  Important selection, so the two views never disagree, and choosing a tile opens the dashboard.
  A provider that reports more than one model group shows one group's limits, by default the group
  closest to its limit, and an ⓘ beside its name explains which group and lists each limit in full;
  the group can be pinned per provider in **Settings ▸ Providers**. A sentence above the tiles
  names the limit forecast to run out first, or without a forecast the busiest limit of all, in
  place of the `CODEX 92% USED` tag that followed each provider's first window. Refresh and Settings become icon
  buttons beside the title, the refresh arrow spins while a refresh runs, and the footer keeps the
  last-updated time and an Open dashboard button. Stale tiles dim their rings and show a warning
  indicator whose tooltip says how old the reading is, the tooltips use the app's colours, and the
  popup scrolls only when the screen cannot hold it.
- **The dashboard is one table of every limit, in columns that line up.** The 2×2 grid of cards
  squeezed a provider with four limits into the same cell as a provider with one, clipped long
  names such as "Claude and GPT models (5-ho…", and put each figure wherever its card's layout
  landed. The dashboard now lists every provider in one table with fixed columns — limit, usage
  bar, used (or left), resets in, and at this pace — so values compare straight down the page.
  Each provider is a group led by its glyph, name (a link to its usage page), plan and account,
  and its status: connected and how long ago, the last good reading and the next retry with a
  **Retry** link that refreshes that provider alone, or not connected with the sign-in command to
  copy. A provider with several model
  groups gets a sub-heading per group, so names are never cut to fit. The forecast column says
  when a limit runs out ("Out by 17:47") or that it resets first, and narrow windows drop it
  rather than squeeze every cell. The All / Metered / Important filter moves from a dropdown in
  the footer to a segmented control in the header, every option one click away. The table scrolls
  only when the window is shorter than it, keeps its scroll position and keyboard focus as each
  provider's reading arrives, and Tab walks its links and buttons in reading order. Until the
  dashboard is resized by hand it grows to fit its rows, including after moving to a monitor with
  a different scale; a size the user picks is kept. The per-card trend sparkline is gone with the
  cards: the forecast column says where the current pace leads, and the Settings option is now
  **Show burn-rate forecast**.
- **Settings is organised into sections on cards, with a section list to jump between them.** The
  dialog was one narrow column of checkboxes and dropdowns under upper-case headings, and a setting
  near the bottom took a long scroll to find. It is now wider, with General, Alerts, Appearance,
  Providers, History and About listed on the left: choosing one scrolls to it, and the list marks
  the section being read as the page scrolls. Each section is a card. On/off settings are Windows
  11 toggle switches, short choices (theme, dashboard rows, show quota as) are segmented controls
  with every option in view, number fields are compact steppers, and the dropdowns are themed. The
  quota display shows a small ring flipping between "92% used" and "8% left", and the status-colour
  thresholds show where fine, warning and critical begin on the scale as they are typed.
  Refresh, startup and the hotkey move together under General; Delete recorded history reads as
  a destructive action. The window can now be resized, so a taller one shows more of the page,
  and it reopens at the size it was last given, on any monitor; it opened at one fixed size before.
- **The tray icon, tray menu, update prompts and What's New match the new look.** The tray gauge
  is now a clean arc on its track, the same ring the popup draws, without the translucent pie
  behind it or the provider letter inside, which at tray size only blurred the arc; an error still
  shows `!` where there is room to read it. The tray menu is drawn like a Windows 11 context menu,
  with rounded corners, roomier rows, a rounded highlight and quiet dividers, and its first item
  is now named **Open dashboard** and shown in bold as the default action. Update questions — a
  new version is available, the install failed, the release page did not open — used system
  message boxes that ignored the app's theme; they are now themed dialogs with plain-language
  buttons (**Install update**, **Later**, **Open release page**), marked with the app's logo for
  a question and a warning sign for a failure; so is the notice when Settings cannot change Start
  with Windows. What's New is headed "What's
  new", shows the notes on the window itself instead of in a bordered box, names sections in
  sentence case behind a coloured dot, closes with **Got it**, and links to the full changelog
  instead of offering it as a second button.
- **Scrollbars are slim and stay out of the way.** The system scrollbar can only be themed light
  or dark, so the dashboard, popup, Settings and What's New showed a full-width track down the
  whole window, even when the content overflowed by a few pixels. They now show a thin rounded
  thumb in the Windows 11 style at the window's right edge, with no track until it is pointed at,
  widening on hover; it drags, pages on a click above or below, and scrolls with the wheel. Content
  keeps its margin, so the thumb never covers it, and content that fits shows no scrollbar at all.
  High Contrast keeps the system scrollbar.
- **Gauges move to new readings instead of jumping.** When a refresh brings a new value, the
  popup's rings and the dashboard's bars ease to it over 0.4 s and a limit crossing into warning or
  critical crossfades its colour, while the figure shows the new number at once. Opening the popup
  draws everything in place, the refresh arrow now turns smoothly instead of in 24° steps, and all
  motion stops when Windows' animation effects are turned off or High Contrast is on.
- **Every window can be opened with sample data to try a change by hand.** `--render-preview`
  accepts `--theme light|dark`, and the new
  `--preview-window popup|dashboard|settings|whatsnew|prompt|warning|menu` opens the real window
  with sample data at the monitor's own DPI, with no tray icon, provider access or settings file,
  so a UI change can be tried by hand next to an installed copy.
  `--render-preview` now draws at the requested DPI instead of enlarging a 96-DPI image, which
  blurred the README screenshots and, on a display scaled above 100%, clipped their text; the
  dashboard image is as tall as its whole table rather than the screen.

### Fixed

- **Large usage figures are no longer cut off at the bottom.** The headline value was drawn in a
  box shorter than the font's line height, so `92% USED` and `UNLIMITED` lost the bottom of every
  glyph at every display scale. The box is now sized from the font itself.
- **The popup and dashboard fit a second monitor at a different display scale.** Opened on a
  monitor whose scale differed from the main one, the popup was sized for the main monitor and
  then grown in place, so its second row of providers ran off the bottom of the screen, and the
  dashboard kept its main-monitor size, cutting the row filter to "Meter…" and "Import…" and
  dropping the forecast column. Both are now measured for the monitor they open on. Controls
  rebuilt during a scale change also took the old scale from their parent; they now ask the window.
- **Exiting while a refresh is running no longer shows a crash dialog.** A provider that answered
  just as UsageAI shut down still delivered its update to the tray icon, which had already been
  disposed, and WinForms reported the `NullReferenceException`. Updates that arrive after shutdown
  are now dropped.
- **Settings' number fields no longer show white arrow buttons in the dark theme.** Windows draws
  a number field's up and down buttons in its light style whatever the app's theme; they now take
  the field's own colour, with quiet chevrons that shade on hover and press. High Contrast keeps
  the system's buttons.
- **What's New has a dark scrollbar in the dark theme.** The window asked for one when it was
  created, before the release-notes box inside it existed, so the request never reached that box
  and its scrollbar stayed white. The box now asks for it itself as soon as it is created.
- **Forecast times no longer mix in day names from the Windows language.** "Out by" and the
  popup's "runs out first … around" took the weekday and AM/PM from the Windows display
  language, so a Hebrew system showed "Out by 21:52 יום ו׳" inside an English sentence. They now
  read "Out by Fri 21:52", keeping the user's 12- or 24-hour clock.
- **The popup no longer flashes empty when providers update.** Its tiles are rebuilt on each
  update, and the panel was painted bare between removing the old tiles and adding the new ones.

## [0.13.0] - 2026-10-21

### Added

- **Settings ▸ Appearance ▸ Quota display** can show how much quota is left instead of how much is
  used. In **Remaining** mode the card headlines, metric rows, meters, popup header, tray tooltip,
  tray ring, and alert wording all read as what is left, such as `28% LEFT`, and each meter fills
  to the same number its headline states, so the bar and the figure never disagree. Only
  presentation flips: warning and critical colours, High Contrast cues, the alert thresholds
  themselves, forecasts, which provider drives the tray and popup header, and **Important only**
  filtering all keep working from the used percentage, so a nearly exhausted limit is flagged the
  same way in both modes. A provider with no metered limit is never shown as 100% left. Switching
  it re-renders from the last readings without polling any provider. **Used** stays the default,
  and this matches the editor extension's `usageai.quotaDisplay` setting.
- `--render-preview` accepts `--quota-display remaining` to render the remaining-quota view.
- Codex and Claude Code can now use the CLIs bundled in their editor extensions. With only OpenAI's
  Codex extension or Anthropic's Claude Code extension installed, Codex reported "Codex CLI was not
  found" and an expired Claude login stayed stale. The tray app now reads the `extensions.json` of
  VS Code, VS Code Insiders, VSCodium, Cursor, Windsurf, and Antigravity to find the extension
  version each editor actually loads, and checks the extension's own fixed binary paths inside it,
  after `CODEX_PATH` / `CLAUDE_PATH`, `PATH`, and the standalone install folders.

### Changed

- Codex and Claude Code sign-in messages now mention signing in through the provider's editor
  extension. Someone using only the extension has no `codex` or `claude` command to run.

- Google Gemini now says which sign-in paths it tried when all of them fail. Because the Gemini CLI
  fallback runs last, its own "not signed in" used to be the only error shown, even when the
  Antigravity paths were the ones that mattered. The error now opens with "Google Gemini usage is
  unavailable" and what to check, then says that the Antigravity IDE and `agy` (or that `agy` is
  not installed) returned no usage. It no longer claims a sign-out it cannot know about, and a rate
  limit or outage on the Gemini CLI path keeps its own message instead of a sign-in hint.

- The What's New window now shows one sentence per change instead of the changelog's full
  paragraph. The changelog stays the long-form record — it is where a change is explained — but read
  in a window that lists three releases at once, those paragraphs were more than anyone wants on an
  upgrade. Every bullet is written so its opening sentence stands alone as the headline, and the
  bundled parser keeps only that sentence. A sentence ends at a full stop, question mark, or
  exclamation mark followed by the end of the bullet or by whitespace and something that is not a
  lower-case letter, so a version number, a file name, an abbreviation such as `e.g.`, and a dotted
  span inside `code` never cut a line short, and a bullet with no such break is still shown whole.
  **View complete changelog** still opens the full text.
- **Start with Windows** moved into **Settings ▸ System** and now reports whether Windows really
  launches UsageAI. It is saved with every other preference, and it no longer merely reports whether
  a registry value exists. Two states used to make that tick lie. Switching UsageAI off in
  Task Manager or Settings ▸ Startup Apps leaves the Run entry in place and records the veto beside
  it, so the entry outlives its own effect; and an entry written by a copy of UsageAI that has since
  been moved, deleted, or replaced by an install elsewhere keeps pointing at that path. Both now read
  as off with a line saying which one it is, and ticking the box repairs the entry instead of writing
  a duplicate: enabling re-points it at the running executable and clears any Startup Apps veto, so a
  tick always means this copy launches and an empty box always means nothing does. Only a tick the
  person actually changed is written, so saving Settings for some unrelated preference is never how
  a startup entry disappears — running a second copy of UsageAI once cannot unregister the installed
  one. A registry the account cannot write is reported rather than thrown, and every other
  preference is still saved when that write fails.
- The desktop suite grew to 82 checks. The new ones cover remaining-quota rendering and alert
  wording, the editor-extension CLI lookup for Codex and Claude Code (including stale manifest
  entries and paths outside the extensions folder), and the combined Gemini error. Coverage on the
  `UsageAI` package is 88.61% line and 82.89% branch, against a gate of 83% and 76%.

### Fixed

- Claude Code no longer stays stale once its access token expires while no Claude Code session is
  running. The token lasts about eight hours, and the recovery path relied on `claude auth status`,
  which only reports that a login exists and never refreshes an expired token — so after a night's
  sleep, or a day spent in the Claude desktop app, Claude for Chrome, or a terminal on another
  `CLAUDE_CONFIG_DIR`, none of which refresh that credential file, the card kept its last snapshot
  until a Claude Code session happened to start on the default profile. While the stored token is
  still expired, recovery now also runs `claude mcp list`, which needs a live token and makes the
  CLI refresh it through its own credential store; measured on an expired login it rewrote the file
  with a fresh eight-hour token. UsageAI stops that child as soon as the new token lands, bounds it at
  20 seconds, skips it whenever the stored token is still valid, and still never reads, exchanges, or
  writes Claude's refresh token.

## [0.12.0] - 2026-09-17

### Added

- The provider list in Settings can now be reordered by dragging an entry. That is the gesture
  people try first; the Move up and Move down buttons stay, and Alt+Up and Alt+Down do the same
  from the keyboard without leaving the list. The drag rides on the mouse capture the list already
  takes rather than OLE drag-and-drop, because registering a drop target needs an STA thread and
  buys nothing for an entry that never leaves its own list. A `CheckedListBox` refuses every
  owner-draw mode, so the row moves under the cursor instead of a drawn insertion line, which also
  shows the result rather than a promise of it. A press that never passes the system drag threshold
  is still a click and still ticks the box; one that becomes a drag suppresses the tick
  `CheckOnClick` would otherwise apply on button-up, against whichever row is selected by then —
  which after a drag is the entry just moved — so reordering can never quietly show or hide a
  provider. Straying sideways out of the narrow list keeps the entry level with the cursor instead
  of dropping it at the end, a drag that loses the mouse without a button-up is not still armed
  when the next press lands, and Escape abandons a drag in flight and restores the original order,
  claimed before the dialog's Cancel button can close Settings and discard every edit in it.

### Changed

- The What's New window now lays the release notes out as a typed document instead of one
  undifferentiated block of text. Every release gets its own heading and date, the Added, Changed,
  Fixed and Security categories become coloured labels, bullets hang their wrapped lines under the
  text rather than under the marker, and releases, categories and bullets are separated by
  progressively smaller gaps so the structure is visible before a word is read. The dates are
  spelled out the way the changelog writes them rather than through the system long-date format,
  which on a right-to-left locale reversed itself against the version sitting beside it.
- Inline `code` and **strong** spans in a changelog bullet are now given a monospace and a bold face
  instead of being printed with their Markdown delimiters still attached. No Markdown engine is
  involved and nothing is fetched: the bundled changelog is still read through the same bounded
  parser, only the two constructs the changelog actually uses are recognised, and an unpaired or
  empty delimiter stays literal text. Angle brackets are never markup, so HTML in a bullet is still
  shown verbatim.
- The window now says how much it is leaving out. The upgrade summary has always been capped at the
  three newest releases; it reports the number it dropped rather than a bare note that older
  releases exist, and the subheading names how many releases are being shown when there is more than
  one. Windows High Contrast still flattens every category colour to the system foreground, and the
  category name is always spelled out, so colour is never the only cue.
- The desktop suite grew to 77 checks. The new ones cover the dashboard grid's scroll behaviour —
  including that no provider card is ever laid out where nothing could reach it — the whole
  provider-list drag gesture, down to the boundaries that must do nothing: a right-click, a press on the empty
  strip below the last row, and a move that would carry an entry past either end, and the DPI a
  provider card paints its own text at. The drag is exercised with real mouse messages rather than
  by calling the handlers, because the tick it has to suppress is applied by the native list box
  itself on button-up. The card's text is measured out of the painted pixels — how far left the
  right-aligned headline reaches is exactly the width of the face it was drawn with — so the check
  fails on any machine if the text stops tracking the geometry. The grid check also opens a
  dashboard the way the tray does — cards built and squeezed into restored bounds while the form is
  still handle-less, handles arriving only at `Show()` — because that ordering is the one where a
  card re-measuring itself can put the bottom row past an edge that has no scrollbar to reach it.
  Coverage on the `UsageAI` package is 88.20% line and 82.07% branch, against a gate of 83% and 76%.

### Fixed

- Provider cards now paint their text at the DPI of the monitor they are on. The cards draw their
  own type, and a font asked for in points is rasterised once at the DPI the process started on: on
  a second monitor scaled differently from the primary, every pixel measurement followed the window
  while the text stayed behind. At 200% next to a 125% primary that left the dashboard's headlines
  and labels at two thirds of their size inside boxes built for the larger scale, which is where the
  gaps came from, and it squeezed providers reporting four metered limits out of the full row layout
  they had room for. The faces are now sized in pixels off the same DPI as the geometry and rebuilt
  whenever the card's DPI resolves differently.
- The tray popup no longer clips a provider's detail line and drop its meter. A card measured its
  height in its constructor, before the handle existed — and until the handle exists a control still
  reports the DPI the process started on, not the monitor it is about to appear on. WinForms updates
  that value when the handle lands but re-measures nothing the card sizes for itself, so a popup
  opened on a differently scaled monitor painted rows for the new scale inside a box measured for
  the old one: on a 200% screen beside a 125% primary the cards were 122 pixels tall holding 196
  pixels of content, so "100% left" was sliced in half by the card's own edge and the meter fell off
  it entirely. The card now resolves its DPI from its parent before it has a handle and re-measures
  on handle creation and on every later DPI change — but only for a DPI it has not already measured
  at, and a card the full dashboard has sized into a grid cell carries that size across the change
  rather than taking its natural height back. The dashboard never scrolls, so a card that
  re-measured itself out of its cell would lay the bottom row of providers past the client edge
  where nothing could reach it. That explicit cell height is also why the clipping was only ever
  visible in the popup.
- A metric row squeezed below its preferred height no longer drops to the stripped-back compressed
  layout while it still has room for the full one. The preferred height carries breathing room the
  painted stack does not need, and a provider reporting four metered limits lands a couple of pixels
  short of it in a dashboard cell, so it was losing its detail line to a margin. The fallback now
  triggers on what the stack actually occupies. The trend sparkline and forecast line are likewise
  measured against the height the row was given rather than the height it asked for, so they appear
  in any row that can hold them and never paint across the divider that starts the next one.
- Removed the scrollbar from the full dashboard. Its provider cards are always resized to fill the
  window's client area, so there was never anything below the fold to scroll to, yet a vertical
  scrollbar still appeared at common window sizes with an empty scroll range — at a 760x620 client
  area it showed up with `AutoScrollMinSize` at zero. Cards are built at their natural height and
  only compacted to their grid cell a moment later, and that transient overflow was enough to latch
  the scrollbar, which then cost the grid 49 pixels of width and left a dead strip down the
  right-hand side for the rest of the session. The compact tray popup, which can genuinely
  overflow, still scrolls. With nothing left to scroll the grid also stopped assuming two rows: it
  keeps two columns and takes as many rows as the provider count needs, so the four shipped
  providers still fill the same 2x2 at the same size, and a fifth would earn a third row instead of
  being laid out past the client edge where nothing could reach it. Card widths also no longer
  subtract the scrollbar a second time: `ClientSize` already excludes it, so the deduction only
  narrowed the cards further.

## [0.11.0] - 2026-09-16

### Changed

- Expanded risk-focused coverage across the provider, update, and startup paths. The new checks
  cover provider parsing, authentication and protocol failures, Antigravity hub fallbacks, update
  metadata validation, font fallbacks, startup registration, and single-instance messaging. Desktop coverage is now 88.12% line and 81.76% branch; the editor's 48
  checks reach 79.89% line, 80.10% branch, and 81.41% function coverage.
- Provider readings now reach the cards as each one lands rather than after the slowest provider in
  the refresh returns. A provider that answered in 0.6 seconds no longer appears to take as long as
  one that needs 4 seconds. The tray spinner still turns until the last provider is in, and history, the
  snapshot cache, and alerts are still written once per refresh rather than once per provider.
- Refreshes now wake for a quota window that has reset instead of waiting out the whole refresh
  interval. The tray used to show a spent quota long after it rolled over — up to two hours at the
  maximum refresh setting — and reset notifications now arrive on time. A window costs at most three
  polls, spaced a minute apart, because a provider whose counters trail its own published reset
  instant needs asking again; a provider serving out a failure backoff keeps it either way.
- Google Gemini now goes straight to a serving `agy` hub instead of running the Antigravity IDE
  probe ahead of it, which is what the editor extension already did. The probe itself no longer starts
  PowerShell to map a process id to its listening ports, reading them through the IP Helper API
  instead: the two discovery steps measured 0.65 seconds and 1.2 seconds per call and now cost about
  10 milliseconds and under a millisecond. Command lines are still read through PowerShell, but only
  when a language server is actually running, and the result is kept for the life of that process.

### Fixed

- Fixed Google Gemini starting an `agy.exe` child process on every refresh. Antigravity CLI builds
  from September 2026 onwards ignore the CSRF token passed through the hub's environment and mint
  their own, so every hub call was rejected as unauthenticated and each refresh fell back to a
  short-lived `agy -p /usage` read, which boots MCP servers through `cmd.exe` and flashes a console
  window. The token is now also supplied as `--csrf_token`, the way the Antigravity IDE provisions
  the language server it starts, which both older and newer builds accept.
- Held off further Antigravity hub starts for 30 minutes after one fails to become ready. A future
  change to the CLI then costs one 20-second attempt per half hour rather than an extra child
  process and a 20-second stall on every poll.
- Cleared the Antigravity hub and `agy` probe backoffs when the user asks for a refresh. A single
  missed hub start can no longer hold a provider on the degraded per-refresh path until the window
  expires. The existing immediate-`agy` retry after both paths go stale now releases the hub backoff too.
- Capped the full dashboard's scaled minimum size to the active monitor's working area so compact
  displays remain usable at 200% and 300% scaling.
- Ignored malformed GitHub release assets whose size is not numeric instead of allowing update
  metadata parsing to fail unexpectedly.
- Fixed a well-formed but wrongly shaped GitHub release payload throwing out of the update check
  instead of reporting that it failed. Reading a property from a JSON array, string, or number raises
  an exception the check does not catch, so a non-object release root or a non-object entry in
  `assets` could surface on the UI thread from the daily timer or the Settings **Check for updates**
  button. Release roots and asset entries are now required to be objects.

## [0.10.0] - 2026-09-15

### Added

- Added `All`, `Metered only`, and `Important only` metric display modes to the full desktop
  dashboard. Metered mode includes every usable percentage limit; Important mode keeps the
  highest-used Session, Rolling, and Monthly metric with stable first-reported tie-breaking.
- Added the metric display preference to Settings and a full-dashboard shortcut that saves and
  rebuilds cards immediately without refreshing providers. Filtering never changes cached snapshots,
  alerts, forecasts, diagnostics, the tray gauge, or compact-popup metric selection.
- Added an explicit **No metered limits reported** state for connected providers whose balance or
  unlimited metrics are hidden by the selected dashboard mode.
- Added one bounded Claude Code OAuth recovery attempt when the first usage request is rejected as
  unauthorized/forbidden or returns unexpected unavailable data. UsageAI asks the official
  `claude auth status --json` command to validate or refresh the login, rereads Claude's credential
  store, and retries usage once; it never exchanges the refresh token or writes credentials itself.
  Explicit `USAGEAI_CLAUDE_OAUTH_TOKEN` overrides remain authoritative and bypass this fallback.
- Added a native **What's new** window backed by the `Changelog.md` embedded in the executable. Real
  upgrades queue up to three newer released versions and open them only after the user's first popup,
  dashboard, or Settings interaction; clean installations remain quiet.
- Added conservative upgrade-version tracking that handles installations predating the setting,
  skipped releases, same-version launches, downgrades, and malformed stored versions without blocking
  startup. A queued version is marked seen only after the release-notes window is displayed.
- Added **What's new** under Settings > About for reopening the installed release notes, plus a
  **View complete changelog** action and a safe fallback when the installed version has no bundled
  section. Runtime parsing ignores `Unreleased`, accepts only released Added/Changed/Fixed/Security
  bullets, and bounds input, versions, bullets, and output.
- Added an **About** section to the desktop settings window that shows the installed UsageAI
  version and provides a manual **Check for updates** action. The check reports whether the app is
  current, an update is available, or GitHub could not be reached, and newer releases continue
  through the existing verified installer flow.
- Added an explicit-DPI preview and construction seam for 96, 192, and 288 DPI, including the
  `--dpi 96|192|288` preview option.
### Changed

- The full desktop dashboard now keeps all four provider cards in a fixed 2×2 grid. Resizing or
  maximizing the window adjusts both card dimensions and their internal metric spacing. Short cards
  use a denser metric-row presentation so the second provider row remains visible while resizing.
- Windows High Contrast now overrides System, Dark, and Light theme choices. Every palette entry is
  derived from `SystemColors`; provider identity remains available through names and glyphs instead
  of brand colors, and tray menus switch to the native system renderer.
- High Contrast changes now apply while UsageAI is running to the popup, full dashboard, Settings,
  What's New, custom-painted cards, scrollbars, menu renderer, and regenerated tray icon.
- Warning and critical quota states now include visible symbolic cues, while stale, disconnected,
  and refreshing states remain stated in text rather than depending on color alone.
- Popup, dashboard, Settings, and What's New layout metrics now reapply during per-monitor DPI
  transitions. They also preserve practical scroll and logical sizing state, and constrain windows
  to the destination monitor's working area.
- Expanded the desktop regression harness to 70 registered checks. New coverage exercises
  metric filtering and settings isolation, release-note bounds and lifecycle behavior, runtime High
  Contrast propagation, native dialog reopening, successful bitmap painting, responsive 2×2
  dashboard resizing, owned-window/card geometry at 96, 192, and 288 DPI, and bounded Claude CLI
  recovery after authorization and invalid-response failures. The `UsageAI`
  package now measures 86.46% line and 80.16% branch coverage in the coverage gate.

### Fixed

- Replaced the full dashboard's native metric picker chrome with theme-aware drawing so its arrow
  button, menu items, focus border, and selected item no longer turn white in dark mode.
- Fixed Claude Code remaining stale when its access token stopped working before the saved expiry
  time, or when a one-off invalid usage response recovered after Claude CLI validation.
- Fixed explicit-DPI preview rendering so layout and typography scale together independently of the
  host monitor's scale factor.
- Fixed popup, dashboard, and Settings scroll restoration across mixed-DPI monitor transitions by
  preserving the logical position instead of reusing the old physical-pixel offset.
- Fixed the release-notes parser's per-bullet boundary so an oversized first line is truncated to
  the same limit as wrapped continuation text.
- Fixed a disconnected-provider card's copy-sign-in action extending beyond the card's declared
  natural height, which could clip the button or hit target at some display scales.
- Fixed invalid numeric dashboard-mode settings reaching rendering code by normalizing them to
  `All`; invalid string enum JSON continues through the existing safe-load fallback without blocking
  startup.
- Fixed High Contrast rendering paths that could retain translucent brand colors in provider icons,
  balance markers, sparklines, tray gauges, or the UsageAI mark.
- Fixed full-dashboard footer sizing so the metric selector and Settings action remain readable at
  supported scales.
- Registered the desktop tray process with Windows Restart Manager so later in-app updates reopen
  the updated executable automatically after installation. The update that first installs this fix
  may still need one manual launch because the older running process could not register retroactively.

## [0.9.0] - 2026-08-26

### Changed

- Google Gemini now reads quota from one long-lived Antigravity CLI `--hub` server instead of starting
  `agy -p /usage` on every refresh. A running app no longer spawns a child process per poll, and the CLI
  starts any configured MCP servers once per session rather than on every poll, which is what produced
  transient console windows on Windows.

### Removed

- Dropped the Antigravity CLI port-discovery probe and its PowerShell helper. The service rejects the
  request bodies it sent, so the HTTP query never produced a snapshot.

## [0.8.2] - 2026-08-26

### Fixed

- Prevented background Gemini refreshes from preferring the standalone `agy.exe` on `PATH`, forcing
  an interactive terminal, or leaving an owned Antigravity language-server child behind.
- Made a stale Gemini card recover from sleep in one refresh even when an earlier `agy` cold-start
  failure was cached, and replaced obsolete Gemini CLI-only sign-in guidance with the installed
  `agy` command.

## [0.8.1] - 2026-08-26

### Added

- Added Gemini quota discovery through the official Antigravity VS Code extension's locally installed
  `agy` backend, including its current snake-case `/usage` response format, so VS Code does not need
  to remain open after sign-in.
- Added animated refresh-progress indicators across the desktop app and editor extension. The
  Windows tray gauge rotates for manual and automatic refreshes, while the shared compact/full
  dashboard button shows an animated reading indicator. Editor dashboard cards, the dashboard
  header, and Status Bar readings now animate without hiding their previous percentages; every
  manual editor entry point also uses VS Code's native window progress indicator.
- Added enforced CI coverage gates for both applications, focused desktop-test filtering, and safe
  Windows integration checks for startup registration, single-instance messaging, Credential
  Manager reads, and the installer elevation contract. The expanded suites now cover 60 desktop
  checks and 31 editor-extension tests without modifying real startup settings or credentials.

### Fixed

- Fixed stale-provider retry scheduling across the desktop app and editor extension. Failed
  providers now wake the scheduler when their individual backoff or `Retry-After` expires, without
  waiting for the regular polling interval, refreshing healthy providers unnecessarily, or
  postponing the next regular refresh.
- Replaced contradictory or ambiguous stale timestamps across the desktop dashboard, editor
  dashboard, and editor Status Bar. Stale views now distinguish the last successful reading from
  the later failed check and show when the next automatic retry is due.
- Fixed the editor scheduler waking every second for a failed provider after that provider was
  disabled. Retry timers now consider only currently enabled providers.
- Hardened editor startup against malformed cached snapshots and invalid cached reset timestamps,
  discarding unusable state instead of allowing it to break dashboard or Status Bar rendering.

## [0.8.0] - 2026-08-16

### Added

- Added automatic daily GitHub release checks while the desktop app is running. When a newer
  release exists, UsageAI presents an explicit **Yes/No** installation prompt; declining leaves the
  current version untouched and postpones the next prompt for 24 hours.
- Added in-app installer download and launch for installed Windows builds. UsageAI selects the exact
  versioned Setup.exe and checksum assets from the pinned repository and falls back to the release
  page if either asset is unavailable.

### Security

- Update downloads follow only approved GitHub release-asset hosts, enforce advertised and maximum
  sizes plus a five-minute deadline, and must match the published SHA-256 before Windows is allowed
  to execute the installer.
- The updater continues to require explicit user approval before downloading or installing, and
  Windows retains its normal UAC confirmation for the Program Files upgrade.

## [0.7.3] - 2026-08-16

### Fixed

- Fixed broken editor-extension preview images on Visual Studio Marketplace and Open VSX by resolving
  packaged README assets from the extension's directory in the monorepo.
- Restored automatic Claude usage recovery after its short-lived access token expires. UsageAI now
  invokes the official `claude auth status --json` command with bounded runtime and output, waits for
  Claude Code to refresh its own login, then rereads the credentials; UsageAI still never submits the
  shared refresh token or writes `.credentials.json` itself.

## [0.7.2] - 2026-08-13

### Added

- Added a bounded, read-only Google Antigravity CLI fallback for Gemini in both the desktop app and
  editor extension. UsageAI first tries an existing local Antigravity server, then briefly invokes
  official `agy /usage`, and retains the legacy Gemini CLI path for compatibility.

### Fixed

- Made Claude Code authentication strictly read-only in both the desktop app and editor extension. UsageAI no longer exchanges Claude's shared refresh token or rewrites `.credentials.json`, preventing token-rotation races that could force Claude Code to sign in again.

## [0.7.1] - 2026-08-11

### Added

- The editor Status Bar now shows the session and weekly percentages side by side, with segmented quota meters, the last-updated time, and Open/Refresh actions in its hover card.
- Shared monochrome Codex/OpenAI, Claude, GitHub Copilot, and Gemini brand glyphs for the editor Status Bar and desktop provider cards. The icon fonts are bundled with each app, inherit the active theme, and require no external font installation.

### Changed

- Replaced the desktop app's hand-drawn provider symbols and the editor extension's generic pulse icon with the shared provider glyph set, while retaining state-specific loading and warning icons.
- Regenerated the compact popup and full dashboard previews to show the new provider icons.

### Fixed

- Centered desktop provider logos by their visible vector bounds instead of the font line box, removing the upward offset in both compact and full dashboard cards.

## [0.7.0] - 2026-08-11

### Added

- A pure TypeScript UsageAI editor extension under `extension/`, targeting both VS Code and Antigravity with one VSIX. It contributes a persistent Activity Bar dashboard, Status Bar summary, configurable polling, cached stale readings, and local clients for Codex, Claude Code, GitHub Copilot, Gemini CLI, and Antigravity quota summaries.
- Multi-provider Status Bar selection for the editor extension, with one compact item per selected provider and compatibility with earlier single-provider settings.
- Native Settings UI checkboxes for each editor Status Bar provider, avoiding manual `settings.json` editing.
- Editor settings organized into General, Status Bar, and Usage Levels sections for clearer navigation.
- Extension parser and orchestration tests using Node's built-in test runner, plus validation in the existing non-publishing GitHub workflow.

### Fixed

- The editor extension now uses a dedicated monochrome quota-dial icon for Activity Bar surfaces, instead of flattening the full-colour marketplace PNG into a white square.
- Editor reset countdowns of one day or longer now use days and hours instead of large hour and minute values.
- The editor's Providers display order now also controls the order of selected Status Bar items.

## [0.6.0] - 2026-08-10

### Added

- **Google Gemini Preview Block & Sparkline Curves**: Integrated the Google Gemini provider block and multi-point session reset sparklines into the `--render-preview` pipeline (`UI/PreviewRenderer.cs`), displaying Gemini Models and Claude/GPT Models metrics with realistic usage trend curves.
- **High-Resolution Anti-Aliased Preview Pipeline**: Upgraded `--render-preview` to use high-quality bicubic interpolation and smoothing modes, producing crisp vector-smooth high-resolution preview images (`usageai-preview.png` and `usageai-dashboard-preview.png`).
- **Expanded Regression Test Suite**: Expanded the console harness in `UsageAI.Tests` from 30 to 49 checks (`UsageAI.Tests\CoverageExpansionTests.cs`), adding coverage for refresh orchestration, throttling and backoff, shutdown cancellation, provider HTTP and OAuth flows, the Codex `app-server` protocol, corrupt local-state recovery, provider parser edge cases, custom UI rendering, application lifecycle, preview rendering, and command-line entry points. Two of the checks exercise Windows ACL and EFS behaviour and need an elevated shell; without one the suite reports 47 of 49.
- Test seams on the provider clients, `UpdateChecker`, and `UsageApplicationContext`, which now accept an `HttpClient` plus credential and probe delegates through `internal` constructors so fetch and authentication paths can be driven from tests without network access.

### Fixed

- **Full Dashboard Preview Canvas Geometry**: Fixed `--full` dashboard preview rendering in `UI/PreviewRenderer.cs` to use borderless presentation (`FormBorderStyle = FormBorderStyle.None`) and a wider canvas layout (`940×930`), eliminating system caption bar artifacts, vertical footer cutoff, scrollbars, and metric label truncation.
- Cross-process refresh locks were created under a hard-coded `%LOCALAPPDATA%\UsageAI\locks` path, ignoring `USAGEAI_DATA_DIR`. A portable install pointed at another directory now keeps its lock files with the rest of its local state instead of writing into the roaming profile.
- A shutdown race in `UsageRefreshService.Dispose` where an in-flight refresh could release a semaphore that had already been disposed, throwing during exit.

## [0.5.0] - 2026-07-30

### Added

- Google Gemini provider integration (`gemini`), supporting both local Antigravity IDE language server probe detection and Gemini CLI OAuth authentication (`cloudcode-pa.googleapis.com`).
- Antigravity IDE model grouping in the detailed dashboard view into unified model groups (**Gemini Models** and **Claude and GPT models**), reflecting exact real-time remaining fractions and reset countdowns.
- Antigravity quota-summary integration so the detailed Google Gemini card shows both the 5-hour and weekly windows for **Gemini Models** and **Claude and GPT models**.
- Automatic OAuth client credential resolution with fallback for Gemini CLI token refresh.
- Google Gemini icon painting (4-pointed sparkle star) and brand color theme definitions (Google Sky Blue / Brand Blue).
- Application logo and multi-resolution Windows icon (`Resources\app.ico` and `Resources\logo.png`), integrated into `UsageAI.csproj`, the Inno Setup installer (`installer\UsageAI.iss`), application header UI, and project documentation.
- An Inno Setup installer (`installer\UsageAI.iss`) producing `UsageAI-<version>-Setup.exe`, with a Start Menu shortcut, uninstaller, and an install-time check that silently installs the .NET 10 Desktop Runtime if it's missing. The portable single-file build remains available as `UsageAI-<version>-portable.exe`. Both stay part of the existing manual local-build-and-upload release process; no hosted release automation was added.
- **Glance View Details Button**: Added a dedicated **Details** button in the Glance View footer (`DashboardMode.Compact`) to switch into the full detailed dashboard directly.
- **Responsive Multi-Column Dashboard Layout**: Redesigned the detailed dashboard view into an adaptive multi-column grid that adds or removes columns with the available window width while dynamically resizing provider cards, sparkline graphs, capacity meters, and text bounds.
- **Dynamic Content & Height Filling**: Provider usage cards stretch vertically and horizontally to fill available window dimensions evenly.
- **Native Windows Immersive Dark Mode Headers & Scrollbars**: Integrated native Windows DWM title bar styling (`DWMWA_USE_IMMERSIVE_DARK_MODE`) and UxTheme dark scrollbar styling (`DarkMode_Explorer`) across the dashboard and settings windows via `WindowThemeHelpers.cs`.
- **Dynamic Tray Icon Fill & Color Progression**: Implemented color-coded filled pie sectors (`FillPie`) in the system tray icon, advancing from Emerald Green (<50%), Cyan Blue (50%–71%), Amber Orange (72%–89%), to Vivid Red (≥90%).
- **Primary Session Metric Prioritization**: Configured tray tooltips, tray gauge selection, and popup header summaries to prioritize each provider's active primary session metric (e.g. Claude Code 5-hour limit) for real-time tracking during active coding sessions.
- **Expanded Regression Test Suite**: Expanded the test harness in `UsageAI.Tests` from 26 to 30 checks, adding coverage for primary session metric prioritization, theme usage color fill progression, Gemini credential-file ACL/EFS protection, and PID-bound Antigravity probing.

### Fixed

- Handled omitted `remainingFraction` values in Google Gemini / Antigravity IDE model quota info blocks so model groups with depleted quotas (such as **Claude and GPT models**) remain visible at 100% usage with their reset countdown rather than being discarded.
- **Static Footer Timestamp**: Fixed frozen `"Updated just now"` footer status label by adding a 5-second UI tick timer (`_uiTickTimer`) in `UsagePopupForm.cs` that dynamically re-evaluates relative age against `DateTimeOffset.Now` using provider statuses' `LastUpdated` / `FetchedAt` timestamps.
- **Real-Time Thread-Safe UI Updates**: Fixed thread synchronization in `UsageApplicationContext.cs` using `SynchronizationContext` so background usage updates reliably post to the UI thread even when the popup window handle is hidden or recreated.
- **Window Layout Re-Entrancy Crash**: Fixed re-entrancy layout loops during window resizing by batching control size changes and performing one guarded flow-layout pass afterward.

### Security

- Preserved owner, group, DACL, and EFS metadata when persisting refreshed Gemini OAuth credentials, with stale-file and reparse-point checks before replacement.
- Bound Antigravity authentication tokens to listening ports owned by the process that supplied them, with an ownership recheck immediately before each authenticated request.
- Removed hard-coded live Antigravity CSRF credentials, fixed-port probing, certificate-validation bypass, and live response logging from the test harness; replaced them with a synthetic PID-binding regression check.

## [0.4.0] - 2026-07-28

### Added

- Usage history recorded locally, with a trend sparkline and a burn-rate forecast that projects when a window will be exhausted and whether that lands before its reset.
- Tray notifications when a window crosses a usage threshold or rolls over, suppressed on first observation and rate limited per metric.
- A settings window for the refresh interval, alert thresholds, theme, warning and critical colour points, history and forecast, provider visibility and order, tray-icon provider, the global hotkey, and the opt-in release check.
- A persisted tray-icon provider choice. Automatic mode follows the connected provider with the highest usage, while a pinned provider drives the circular gauge and falls back safely if disconnected.
- Light, dark, and follow-Windows themes, using the Windows accent colour and reacting to system theme changes at runtime.
- A cached last reading, so the popup shows real values immediately at start-up instead of an empty shell.
- The global hotkey Win+Alt+U, keyboard-navigable dashboard cards, and card actions to copy a provider's sign-in command or open its usage page.
- An opt-in check for newer published releases, plus `--help` and `--version`.
- `USAGEAI_DATA_DIR` for a portable install, and a solution file so the app and tests build together.
- Thirteen new regression checks, expanding the console harness from 10 to 23 checks across all three provider parsers, settings persistence and scrolling, tray-provider selection, history, snapshots, forecasting, alerts, and version comparison.
- A validation-only GitHub workflow that restores, builds, and tests. It does not publish; releases stay manual.
- Dependabot coverage for GitHub Actions dependencies, in addition to NuGet.

### Changed

- Providers now report a list of usage metrics instead of a fixed session/weekly pair, so every window a provider exposes is shown.
- Codex now exposes account credits and reset credits as metrics, Claude exposes its weekly Opus limit and extra usage, and Copilot retains every quota returned by GitHub.
- Usage meters and headline values now show consumption as the primary signal; remaining capacity is secondary, and provider-specific detail such as Copilot's remaining request count is preserved.
- Consumption drives the colour of the headline value, the meter, the card rail, and the tray icon, rather than a four-pixel bar alone.
- The compact and expanded cards share one layout grammar, and the detail line spans the full card width instead of truncating the reset countdown.
- The header now names the most-consumed provider instead of counting connections.
- The tray icon is rendered at the size the shell requests, drops its glyph when too small to read, and shows a concise selected-provider tooltip containing only the used percentage.
- Fonts resolve through fallback chains, so Windows 10 no longer silently loses the whole type hierarchy when the Segoe UI Variable and Cascadia faces are absent.
- The window declares per-monitor DPI awareness and scales its custom-painted layout, instead of stretching a 96-DPI layout.
- Refresh scheduling is adaptive: per-provider exponential backoff, `Retry-After` when a provider supplies it, a longer interval while no window is open, and an immediate refresh on resume and unlock.
- The GitHub Copilot client remembers which discovered token worked and skips rejected ones, instead of replaying up to sixteen credentials against GitHub on every refresh.
- Provider polling, stale-state retention, backoff, history recording, snapshot caching, and alert orchestration now live in a dedicated refresh service instead of the application context.
- Drawing primitives, preview rendering, reset formatting, typography, and DPI scaling are centralized in shared helpers instead of being duplicated across controls.
- The dashboard remembers its position and size between runs, not only for the current session.
- A second launch now activates the running instance instead of exiting silently.
- Contributor and security documentation now covers local state, the validation-only build workflow, local build commands, manual release policy, and the provider-integration reference.
- The README and checked-in preview were refreshed for the new dashboard, settings, diagnostics, and consumption-first meter design.

### Fixed

- GitHub Copilot's third quota is no longer discarded; premium requests, chat, and completions are all shown.
- A failed refresh no longer erases a provider's data. The last good reading is kept and marked stale, with the provider's error alongside it.
- A zero-usage tray icon now retains a high-contrast ring and provider-coloured center marker instead of fading into the taskbar.
- The settings page now scrolls through its final controls instead of stopping behind the fixed Save/Cancel footer, without introducing horizontal scrolling.
- Card contents are exposed to screen readers, which previously saw an empty box because every value is custom-painted.
- Release builds now pass the enabled recommended analyzers with warnings treated as errors.

### Removed

- The unused quota meter control, which also leaked a font handle on every paint, the vestigial single-provider registry setting, obsolete provider view state, and other dead orchestration and drawing code.

## [0.3.1] - 2026-07-22

### Fixed

- Reapplied the captured Windows security descriptor after atomically replacing Claude's OAuth credential file, preventing elevated filesystems from changing its owner.

## [0.3.0] - 2026-07-22

### Added

- Optional Claude web-session authentication, tried before OAuth when a session key is explicitly supplied through the UsageAI process environment.
- A no-dependency security regression harness covering secret validation, bounded I/O, minimal child-process environments, Claude organization parsing, OAuth credential ACL preservation, lost-update protection, and cross-process locking.
- A security policy and automated dependency update configuration.

### Security

- Bounded provider HTTP responses, credential files, subprocess output, protocol messages, and token sizes to prevent memory-exhaustion paths.
- Disabled redirects and ambient cookies for provider HTTP clients, enabled certificate revocation checks, and replaced static client identifiers with the running app version.
- Restricted provider subprocesses to absolute executables and minimal allowlisted environments so provider secrets are not inherited accidentally.
- Replaced broad Windows Credential Manager enumeration for Claude with exact credential reads and zeroed copied credential buffers after use.
- Made Claude OAuth refresh persistence atomic across processes, protected against lost updates, and preserved the credential file's ACL, ownership, and EFS state.
- Sanitized provider and CLI errors so raw response bodies, stderr, tokens, and local paths are not surfaced to the UI.
- Disabled unsafe BinaryFormatter compatibility, enabled recommended .NET security analyzers as errors, and made Release builds deterministic without portable debug symbols.
- Reduced the distributable to one executable so application code is not left in a separate dependency file.
- Prevented duplicate interactive UsageAI instances from racing provider refreshes.

### Changed

- Browser cookie stores are never scanned; Claude cookie authentication is explicit, memory-only, and falls back safely to Claude Code OAuth.
- GitHub CLI token fallback is now disabled unless `USAGEAI_ENABLE_GH_TOKEN_FALLBACK=1` is explicitly set.
- Migrated from the maintenance-phase .NET 8 runtime to the active .NET 10 LTS runtime.

## [0.2.0] - 2026-07-22

### Added

- Claude Code account support using its existing OAuth login, with five-hour and weekly limits, resets, plan detection, and optional extra usage.
- GitHub Copilot account support for AI credits or premium requests, chat/completion quotas, plan, login, and monthly resets.
- A compact connected-provider view and a full multi-provider dashboard for Codex, Claude Code, and GitHub Copilot.
- Provider-specific diagnostics with `--diagnose codex`, `--diagnose claude`, and `--diagnose copilot`.
- Provider-specific vector icons and visual status signals throughout the usage views.

### Changed

- Generalized the popup, quota cards, tray icon, and tooltips for multiple usage providers and unlimited quotas.
- Left-clicking the tray icon now opens a compact overview of every connected provider, showing one prioritized metric per provider.
- The tray menu's **Open** action now opens a full dashboard with all providers, metrics, and connection details.
- The full dashboard now behaves as a movable, resizable Windows window and remembers its bounds for the current session.
- Dashboard cards now reflow and repaint continuously while the window is being resized, including scrollbar transitions.
- Refined the interface with a modern graphite palette, clearer hierarchy, responsive sizing, and scrollable dashboard layout.

## [0.1.0] - 2026-07-22

### Added

- Native Windows system-tray app for monitoring Codex usage.
- Five-hour and weekly quota meters, shown when reported by Codex.
- Reset countdowns and available full-reset credit count.
- Dynamic tray icon reflecting the most-used quota window.
- Automatic five-minute refresh and manual refresh controls.
- Optional launch at Windows sign-in.
- Diagnostic mode for validating the local Codex connection.
- Privacy-first access through the existing Codex CLI login, without stored API keys or browser cookies.

### Notes

- The first release is a personal, Codex-only tool.
- Codex app-server integration is experimental and isolated in the usage client for easier future updates.
