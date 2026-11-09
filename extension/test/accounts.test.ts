import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { chmod, mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import * as os from "node:os";
import * as path from "node:path";
import test, { mock } from "node:test";
import {
  AccountService,
  accountScanIntervalMs,
  resolveAccounts,
  sanitizeAccountState,
  type AccountState,
  type ManagedAccount,
} from "../src/account-service";
import {
  accountCardTitle,
  accountFolderProblem,
  accountIdForPath,
  accountLabel,
  changeAccountSetting,
  claudeProfilePath,
  commandWithEnvironment,
  defaultAccountId,
  defaultLoginFolder,
  discoverAccountFolders,
  discoveredProvider,
  distinctAccountLabels,
  exceedsConfiguredAccounts,
  hasAccountSetting,
  identityKey,
  inheritedLoginFolder,
  mainSignInCommand,
  isAccountUnitKey,
  isKnownUnitKey,
  normalizeAccountPath,
  proposeLoginFolder,
  providerKindOf,
  readClaudeIdentity,
  readCodexIdentity,
  sanitizeAccountName,
  sanitizeAccountSettings,
  unitKey,
  type AccountEntry,
} from "../src/accounts";
import {
  addAccount,
  describeSettingsWriteError,
  SettingNotRegisteredError,
  useExistingFolder,
  type AddAccountDependencies,
  type AddAccountUi,
} from "../src/add-account";
import { manageAccounts, type ManageAccountsDependencies } from "../src/manage-accounts";
import { noExtensions } from "../src/bundled-cli";
import { userHome } from "../src/security";
import type { UsageClient, UsageSnapshot } from "../src/model";
import { claudeCliEnvironment, ClaudeUsageClient, runClaudeAuthRecovery } from "../src/providers/claude";
import { codexAppServerEnvironment, CodexUsageClient } from "../src/providers/codex";
import { UsageRefreshService } from "../src/refresh-service";

const localRequire = createRequire(__filename);
const security = localRequire("../src/security") as typeof import("../src/security");

function restoreEnvironment(name: string, value: string | undefined): void {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}

const segment = (value: unknown): string => Buffer.from(JSON.stringify(value)).toString("base64url");
const jwt = (payload: unknown): string => `${segment({ alg: "none" })}.${segment(payload)}.signature`;

async function withTemporaryFolder<T>(prefix: string, action: (folder: string) => Promise<T>): Promise<T> {
  const folder = await mkdtemp(path.join(os.tmpdir(), prefix));
  try {
    return await action(folder);
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
}

async function writeLogin(folder: string, provider: "claude" | "codex", identity?: { email: string; org?: string }): Promise<void> {
  await mkdir(folder, { recursive: true });
  if (provider === "claude") {
    await writeFile(path.join(folder, ".credentials.json"), JSON.stringify({
      claudeAiOauth: { accessToken: `token-${path.basename(folder)}`, expiresAt: Date.now() + 3_600_000, scopes: ["user:profile"] },
    }), "utf8");
    if (identity) {
      await writeFile(path.join(folder, ".claude.json"), JSON.stringify({
        oauthAccount: { emailAddress: identity.email, organizationUuid: identity.org ?? "org-1", organizationName: "Org" },
      }), "utf8");
    }
  } else {
    await writeFile(path.join(folder, "auth.json"), JSON.stringify({
      tokens: {
        access_token: jwt({ exp: Math.floor(Date.now() / 1000) + 3_600 }),
        ...(identity ? { id_token: jwt({ email: identity.email, "https://api.openai.com/auth": { chatgpt_account_id: identity.org ?? "ws-1" } }) } : {}),
      },
    }), "utf8");
  }
}

test("account ids hash the normalised folder path and unit keys keep the default plain", () => {
  const expected = createHash("sha256").update("c:\\users\\dev\\.claude-lab", "utf8").digest("hex").slice(0, 8);
  assert.equal(accountIdForPath("C:\\Users\\Dev\\.claude-lab\\", "win32"), expected);
  assert.equal(accountIdForPath("c:\\USERS\\dev\\.claude-lab", "win32"), expected);
  assert.equal(normalizeAccountPath("C:\\", "win32"), "c:\\");
  // Only Windows folds case.
  assert.equal(normalizeAccountPath("/home/Dev/.claude-lab/", "linux"), "/home/Dev/.claude-lab");
  assert.notEqual(accountIdForPath("/home/Dev/.claude-lab", "linux"), accountIdForPath("/home/dev/.claude-lab", "linux"));
  assert.match(accountIdForPath("/home/dev/.codex-x", "linux"), /^[0-9a-f]{8}$/);

  assert.equal(unitKey("claude", "default"), "claude");
  assert.equal(unitKey("claude", "1a2b3c4d"), "claude@1a2b3c4d");
  assert.equal(providerKindOf("claude@1a2b3c4d"), "claude");
  assert.equal(providerKindOf("gemini"), "gemini");
  assert.equal(isAccountUnitKey("codex@0f0f0f0f"), true);
  assert.equal(isAccountUnitKey("gemini@0f0f0f0f"), false);
  assert.equal(isAccountUnitKey("claude@XYZ"), false);
  assert.equal(isKnownUnitKey("claude@1a2b3c4d", ["claude"]), true);
  assert.equal(isKnownUnitKey("claude@1a2b3c4d", ["codex"]), false);
  assert.equal(isKnownUnitKey("claude@bad", ["claude"]), false);
  assert.equal(isKnownUnitKey(7, ["claude"]), false);
});

test("discovery lists the home folder once and keeps only provider folders with a login", async () => {
  await withTemporaryFolder("usageai-accounts-home-", async (home) => {
    await writeLogin(path.join(home, ".claude-lab"), "claude");
    await writeLogin(path.join(home, ".CLAUDE_work"), "claude");
    await mkdir(path.join(home, ".claude-empty"));
    await writeLogin(path.join(home, ".claudex"), "claude");
    await writeLogin(path.join(home, ".claude2"), "claude");
    await writeLogin(path.join(home, ".codex.work"), "codex");
    await writeLogin(path.join(home, ".claudx"), "claude");
    // The bare .claude is a default login, never a found one, even when it is not the default here.
    await writeLogin(path.join(home, ".claude"), "claude");
    await writeLogin(path.join(home, ".codex-alt"), "codex");
    await mkdir(path.join(home, ".codex_nologin"));
    await writeFile(path.join(home, ".codex-file"), "not a folder", "utf8");
    // A login folder inside a matching folder is never searched for.
    await writeLogin(path.join(home, ".codex-parent", "nested"), "codex");
    // The default login is skipped even when it has a matching name.
    await writeLogin(path.join(home, ".claude-default"), "claude");

    const found = await discoverAccountFolders(home, {
      claude: path.join(home, ".claude-default"),
      codex: path.join(home, ".codex"),
    });
    assert.deepEqual(found.map((entry) => [entry.provider, path.basename(entry.path)]), [
      ["claude", ".claude-lab"],
      ["codex", ".codex-alt"],
      ["claude", ".CLAUDE_work"],
      ["claude", ".claudex"],
      ["claude", ".claude2"],
      ["codex", ".codex.work"],
    ].sort((left, right) => left[1]!.localeCompare(right[1]!)));
    assert.equal(found[0]?.id, accountIdForPath(found[0]!.path));
  });
  assert.deepEqual(await discoverAccountFolders(path.join(os.tmpdir(), `usageai-missing-${Date.now()}`), {}), []);
});

test("every test runs with a temporary home folder and no inherited login folders", () => {
  assert.match(path.basename(userHome()), /^usageai-test-home-/);
  assert.equal(process.env.CLAUDE_CONFIG_DIR, undefined);
  assert.equal(process.env.CODEX_HOME, undefined);
});

test("a folder name counts when it starts with .claude or .codex and goes on", () => {
  assert.equal(discoveredProvider(".claude-work"), "claude");
  assert.equal(discoveredProvider(".ClaudeWork"), "claude");
  assert.equal(discoveredProvider(".claude2"), "claude");
  assert.equal(discoveredProvider(".CODEX.work"), "codex");
  assert.equal(discoveredProvider(".claude"), undefined);
  assert.equal(discoveredProvider(".Codex"), undefined);
  assert.equal(discoveredProvider(".claudx"), undefined);
  assert.equal(discoveredProvider("claude-work"), undefined);
});

test("usageai.accounts keeps absolute local folders and names every rejected entry", async () => {
  const absolute = path.resolve(os.tmpdir(), "usageai-team");
  const result = sanitizeAccountSettings([
    { provider: "claude", path: absolute, label: "  Team\u0007  ", hidden: true },
    { provider: "claude", path: `${absolute}${path.sep}` },
    { provider: "codex", path: "relative/folder" },
    { provider: "codex", path: "\\\\server\\share\\.codex" },
    { provider: "gemini", path: absolute },
    { provider: "codex" },
    "not an object",
  ]);
  assert.deepEqual(result.entries, [{ provider: "claude", path: absolute, label: "Team", hidden: true }]);
  assert.equal(result.rejected.length, 5);
  assert.ok(result.rejected.some((reason) => /absolute/.test(reason)));
  assert.ok(result.rejected.some((reason) => /UNC/.test(reason)));
  assert.deepEqual(sanitizeAccountSettings(undefined), { entries: [], rejected: [] });
  assert.equal(sanitizeAccountSettings("x").rejected.length, 1);

  const many = Array.from({ length: 20 }, (_, index) => ({ provider: "codex", path: path.resolve(os.tmpdir(), `codex-${index}`) }));
  const capped = sanitizeAccountSettings(many);
  assert.equal(capped.entries.length, 16);
  assert.match(capped.rejected[0] ?? "", /more than 16/);

  await withTemporaryFolder("usageai-accounts-folder-", async (folder) => {
    assert.match(await accountFolderProblem("claude", folder) ?? "", /\.credentials\.json/);
    assert.match(await accountFolderProblem("codex", "relative") ?? "", /absolute/);
    assert.match(await accountFolderProblem("codex", "\\\\server\\share") ?? "", /UNC/);
    await writeLogin(folder, "codex");
    assert.equal(await accountFolderProblem("codex", folder), undefined);
  });
});

test("identity comes from local files only, and labels fall back from name to email to folder", async () => {
  await withTemporaryFolder("usageai-accounts-identity-", async (folder) => {
    await writeLogin(path.join(folder, "claude"), "claude", { email: "team@example.com", org: "org-team" });
    assert.deepEqual(await readClaudeIdentity(path.join(folder, "claude", ".claude.json")), {
      email: "team@example.com",
      organizationId: "org-team",
      organizationName: "Org",
    });
    assert.deepEqual(await readClaudeIdentity(path.join(folder, "missing.json")), {});

    await writeLogin(path.join(folder, "codex"), "codex", { email: "dev@example.com", org: "ws-42" });
    assert.deepEqual(await readCodexIdentity(path.join(folder, "codex")), { email: "dev@example.com", organizationId: "ws-42" });
    await writeFile(path.join(folder, "codex", "auth.json"), JSON.stringify({ tokens: { id_token: "not-a-jwt", account_id: "acct-1" } }), "utf8");
    assert.deepEqual(await readCodexIdentity(path.join(folder, "codex")), { organizationId: "acct-1" });
    await writeFile(path.join(folder, "codex", "auth.json"), "{", "utf8");
    assert.deepEqual(await readCodexIdentity(path.join(folder, "codex")), {});
  });

  // ~/.claude's profile sits beside it; one inside it answers only when it is the only one there.
  const home = path.resolve("/home/dev");
  const files = new Set<string>();
  const isFile = async (file: string): Promise<boolean> => files.has(file);
  assert.equal(await claudeProfilePath(path.join(home, ".claude"), home, isFile), path.join(home, ".claude.json"));
  files.add(path.join(home, ".claude", ".claude.json"));
  assert.equal(await claudeProfilePath(path.join(home, ".claude"), home, isFile), path.join(home, ".claude", ".claude.json"));
  files.add(path.join(home, ".claude.json"));
  assert.equal(await claudeProfilePath(path.join(home, ".claude"), home, isFile), path.join(home, ".claude.json"));
  assert.equal(await claudeProfilePath(path.join(home, ".claude-lab"), home, isFile), path.join(home, ".claude-lab", ".claude.json"));
  assert.equal(identityKey({ email: "A@Example.com", organizationId: "ORG" }), "a@example.com|org");
  assert.equal(identityKey({ organizationId: "org" }), undefined);

  assert.equal(accountLabel("Team", { email: "a@example.com" }, "/home/dev/.claude-lab"), "Team");
  assert.equal(accountLabel(undefined, { email: "a@example.com" }, "/home/dev/.claude-lab"), "a@example.com");
  assert.equal(accountLabel(undefined, {}, "/home/dev/.claude-lab"), ".claude-lab");
  assert.equal(accountCardTitle("Claude Code", "team", 1), "Claude Code");
  assert.equal(accountCardTitle("Claude Code", "team", 2), "Claude Code · team");

  // The main login is always the standard folder; an inherited variable names a found login instead.
  assert.equal(defaultLoginFolder("claude", "/home/dev"), path.join("/home/dev", ".claude"));
  assert.equal(defaultLoginFolder("codex", "/home/dev"), path.join("/home/dev", ".codex"));
  assert.equal(inheritedLoginFolder("codex", {}, "/home/dev"), undefined);
  assert.equal(inheritedLoginFolder("codex", { CODEX_HOME: "relative" }, "/home/dev"), undefined);
  assert.equal(inheritedLoginFolder("codex", { CODEX_HOME: "\\\\server\\share\\.codex" }, "/home/dev", "win32"), undefined);
  assert.equal(inheritedLoginFolder("codex", { CODEX_HOME: path.join("/home/dev", ".codex") }, "/home/dev"), undefined);
  assert.equal(inheritedLoginFolder("codex", { CODEX_HOME: `"${path.resolve("/srv/codex")}"` }, "/home/dev"), path.resolve("/srv/codex"));
  assert.equal(commandWithEnvironment("CODEX_HOME", "C:\\it's", "codex login", "win32"), "$env:CODEX_HOME='C:\\it''s'; codex login");
  assert.equal(commandWithEnvironment("CODEX_HOME", "/it's", "codex login", "linux"), "CODEX_HOME='/it'\\''s' codex login");
});

const entry = (overrides: Partial<AccountEntry> & Pick<AccountEntry, "provider" | "id">): AccountEntry => ({
  unitKey: unitKey(overrides.provider, overrides.id),
  path: `/home/dev/.${overrides.provider}-${overrides.id}`,
  source: "found",
  identity: {},
  label: overrides.id,
  hidden: false,
  ...overrides,
});

test("resolving accounts keeps defaults first, never shows a duplicate, and holds new finds back", () => {
  const home = path.resolve(os.tmpdir(), "usageai-home");
  const folder = (name: string): string => path.join(home, name);
  const found = (name: string, provider: "claude" | "codex" = "claude") => ({
    provider,
    path: folder(name),
    id: accountIdForPath(folder(name)),
  });
  const state: AccountState = { shown: [found(".claude-lab").id], ignored: [found(".claude-ignored").id] };
  const resolved = resolveAccounts({
    defaults: [
      { source: { provider: "claude", path: folder(".claude") }, identity: { email: "me@example.com", organizationId: "personal" } },
      { source: { provider: "codex", path: folder(".codex") }, identity: {} },
    ],
    configured: [
      { source: { provider: "claude", path: folder(".claude"), label: "Personal", hidden: false }, identity: {} },
      { source: { provider: "codex", path: folder(".codex-work"), label: "Work", hidden: false }, identity: { email: "w@example.com" } },
      { source: { provider: "codex", path: folder(".codex-hidden"), hidden: true }, identity: {} },
    ],
    discovered: [
      { source: found(".claude-lab"), identity: { email: "team@example.com", organizationId: "team" } },
      { source: found(".claude-copy"), identity: { email: "ME@example.com", organizationId: "personal" } },
      { source: found(".claude-ignored"), identity: {} },
      { source: found(".claude-new"), identity: { email: "new@example.com" } },
      { source: found(".codex-hidden", "codex"), identity: {} },
    ],
    state,
  });
  assert.deepEqual(resolved.shown.map((account) => [account.unitKey.split("@")[0], account.label, account.source]), [
    ["claude", "Personal", "default"],
    ["claude", "team@example.com", "found"],
    ["codex", ".codex", "default"],
    ["codex", "Work", "added"],
  ]);
  assert.equal(resolved.shown[0]?.unitKey, "claude");
  assert.equal(resolved.shown[1]?.unitKey, `claude@${found(".claude-lab").id}`);
  assert.deepEqual(resolved.pending.map((account) => account.label), ["new@example.com"]);
  assert.deepEqual(resolved.duplicates.map((duplicate) => [path.basename(duplicate.account.path), duplicate.shownAs]), [[".claude-copy", "Personal"]]);
  // Hidden and ignored logins are kept apart for Manage Accounts rather than dropped.
  assert.deepEqual(resolved.hidden.map((account) => [path.basename(account.path), account.source]), [[".codex-hidden", "added"]]);
  assert.deepEqual(resolved.ignored.map((account) => path.basename(account.path)), [".claude-ignored"]);

  // A hidden default leaves the provider's other accounts in place.
  const hiddenDefault = resolveAccounts({
    defaults: [{ source: { provider: "claude", path: folder(".claude") }, identity: {} }],
    configured: [{ source: { provider: "claude", path: folder(".claude"), hidden: true }, identity: {} }],
    discovered: [{ source: found(".claude-lab"), identity: {} }],
    state,
  });
  assert.deepEqual(hiddenDefault.shown.map((account) => account.source), ["found"]);
  assert.deepEqual(hiddenDefault.hidden.map((account) => account.source), ["default"]);

  assert.equal(sanitizeAccountState(undefined), undefined);
  assert.deepEqual(sanitizeAccountState({ lastScanAt: -1, shown: ["1a2b3c4d", "1a2b3c4d", "bad", 3], ignored: "x" }), { shown: ["1a2b3c4d"], ignored: [] });
  assert.deepEqual(sanitizeAccountState({ lastScanAt: 5, shown: [], ignored: [] }), { lastScanAt: 5, shown: [], ignored: [] });
});

test("the account service keeps ~/.claude as the main login and finds an inherited folder", async () => {
  await withTemporaryFolder("usageai-accounts-inherited-", async (home) => {
    await writeLogin(path.join(home, ".claude"), "claude");
    await writeFile(path.join(home, ".claude.json"), JSON.stringify({ oauthAccount: { emailAddress: "me@example.com" } }), "utf8");
    const team = path.join(home, "profiles", "team");
    await writeLogin(team, "claude", { email: "team@example.com" });
    const empty = path.join(home, "profiles", "empty");
    await mkdir(empty, { recursive: true });
    let environment: NodeJS.ProcessEnv = { CLAUDE_CONFIG_DIR: team, CODEX_HOME: empty };
    const service = new AccountService({
      home: () => home,
      environment: () => environment,
      settings: () => [],
      loadState: () => undefined,
      saveState: () => undefined,
      announce: () => undefined,
      askToShow: async () => "show",
      warn: () => undefined,
    });

    // The editor was started with CLAUDE_CONFIG_DIR pointing at another login: the main Claude login
    // still reads ~/.claude, and that folder is offered as an account of its own. A CODEX_HOME
    // folder without a login offers nothing.
    await service.scan();
    const claude = service.accounts.filter((account) => account.provider === "claude");
    assert.deepEqual(claude.map((account) => [account.id === defaultAccountId ? "main" : "found", account.label]), [
      ["main", "me@example.com"],
      ["found", "team@example.com"],
    ]);
    assert.equal(claude[1]?.path, team);
    assert.equal(service.accounts.filter((account) => account.provider === "codex").length, 1);

    // Pointed at the standard folder itself, the variable adds nothing.
    environment = { CLAUDE_CONFIG_DIR: path.join(home, ".claude") };
    const plain = new AccountService({
      home: () => home,
      environment: () => environment,
      settings: () => [],
      loadState: () => undefined,
      saveState: () => undefined,
      announce: () => undefined,
      askToShow: async () => "show",
      warn: () => undefined,
    });
    await plain.scan();
    assert.equal(plain.accounts.filter((account) => account.provider === "claude").length, 1);

    // The one-time move: state saved before the main login stopped following the variable shows the
    // inherited login at once, since the main card was showing it. After that a new one is asked about.
    environment = { CLAUDE_CONFIG_DIR: team };
    let saved: unknown = { shown: [], ignored: [], lastScanAt: 1 };
    const asked: string[] = [];
    const upgraded = (): AccountService => new AccountService({
      home: () => home,
      environment: () => environment,
      settings: () => [],
      loadState: () => saved,
      saveState: (state) => { saved = state; },
      announce: () => undefined,
      askToShow: async (account) => { asked.push(account.label); return undefined; },
      warn: () => undefined,
    });
    const first = upgraded();
    await first.scan();
    assert.deepEqual(first.accounts.filter((account) => account.provider === "claude").map((account) => account.label), ["me@example.com", "team@example.com"]);
    assert.deepEqual(asked, []);
    assert.equal((saved as { standardMain?: boolean }).standardMain, true);

    saved = { shown: [], ignored: [accountIdForPath(team)], lastScanAt: 1 };
    const ignored = upgraded();
    await ignored.scan();
    assert.equal(ignored.accounts.filter((account) => account.provider === "claude").length, 1);

    saved = { shown: [], ignored: [], lastScanAt: 1, standardMain: true };
    const later = upgraded();
    await later.scan();
    assert.equal(later.pending.length, 1);
    assert.deepEqual(asked, ["team@example.com"]);
  });
});

test("the main login's sign-in clears an inherited folder variable first", () => {
  assert.equal(mainSignInCommand("claude", {}, "win32"), "claude");
  assert.equal(mainSignInCommand("codex", { CODEX_HOME: " " }, "linux"), "codex login");
  assert.equal(mainSignInCommand("claude", { CLAUDE_CONFIG_DIR: "C:\work" }, "win32"), "Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction Ignore; claude");
  assert.equal(mainSignInCommand("codex", { CODEX_HOME: "/srv/codex" }, "darwin"), "env -u CODEX_HOME codex login");
  assert.equal(new ClaudeUsageClient().signInCommand, "claude");
});

test("the account service shows first-run finds at once and asks about later ones", async () => {
  await withTemporaryFolder("usageai-accounts-service-", async (home) => {
    await writeLogin(path.join(home, ".claude"), "claude");
    await writeFile(path.join(home, ".claude.json"), JSON.stringify({ oauthAccount: { emailAddress: "me@example.com" } }), "utf8");
    await writeLogin(path.join(home, ".claude-lab"), "claude", { email: "team@example.com" });
    let saved: unknown;
    let settings: unknown = [];
    let now = 1_000_000;
    const announcements: string[] = [];
    const questions: string[] = [];
    const warnings: string[] = [];
    let answer: "show" | "ignore" | undefined = "show";
    const changes: string[][] = [];
    const service = new AccountService({
      home: () => home,
      environment: () => ({}),
      settings: () => settings,
      loadState: () => saved,
      saveState: (state) => { saved = state; },
      now: () => now,
      announce: (message) => announcements.push(message),
      askToShow: async (_account, message) => { questions.push(message); return answer; },
      warn: (message) => warnings.push(message),
    });
    service.onDidChange((accounts) => changes.push(accounts.map((account) => account.unitKey)));

    // The first run shows the logins already here, with one message.
    assert.deepEqual(await service.scan(), { pending: 0 });
    assert.deepEqual(service.accounts.map((account) => account.label), ["me@example.com", "team@example.com", ".codex"]);
    assert.deepEqual(announcements, ["Found 2 Claude accounts: me@example.com, team@example.com."]);
    assert.equal(service.lastScanAt, now);
    assert.equal(changes.length, 1);
    assert.equal(await service.scanIfDue(), false);

    // A login that appears later is held back until the user answers.
    await writeLogin(path.join(home, ".claude-later"), "claude", { email: "later@example.com", org: "later" });
    await writeLogin(path.join(home, ".codex-skip"), "codex", { email: "skip@example.com" });
    answer = undefined;
    now += accountScanIntervalMs;
    assert.equal(await service.scanIfDue(), true);
    assert.equal(service.pending.length, 2);
    await new Promise((resolve) => setImmediate(resolve));
    assert.deepEqual(questions.sort(), ["New Claude account found: later@example.com", "New Codex account found: skip@example.com"]);
    assert.equal(service.accounts.length, 3);
    await service.answer(service.pending.find((account) => account.provider === "claude")!.id, "show");
    await service.answer(service.pending.find((account) => account.provider === "codex")!.id, "ignore");
    // Found accounts keep folder-name order.
    assert.deepEqual(service.accounts.map((account) => account.label), ["me@example.com", "team@example.com", "later@example.com", ".codex"]);
    assert.equal(service.pending.length, 0);
    assert.equal((saved as AccountState).ignored.length, 1);
    assert.equal(announcements.length, 1);

    // Answered with Show straight from the notification.
    await writeLogin(path.join(home, ".claude-third"), "claude", { email: "third@example.com", org: "third" });
    answer = "show";
    await service.scan();
    for (let index = 0; index < 5 && service.pending.length > 0; index += 1) {
      await new Promise((resolve) => setImmediate(resolve));
    }
    assert.ok(service.accounts.some((account) => account.label === "third@example.com"));

    // A label from the setting renames an account; a bad entry is named once.
    settings = [
      { provider: "claude", path: path.join(home, ".claude-lab"), label: "Team" },
      { provider: "codex", path: "relative" },
      { provider: "codex", path: path.join(home, ".codex-none") },
    ];
    await service.reload();
    await service.reload();
    assert.ok(service.accounts.some((account) => account.label === "Team"));
    assert.equal(warnings.length, 1);
    assert.match(warnings[0] ?? "", /2 entries.*absolute.*auth\.json/);
    assert.equal(service.duplicates.length, 0);
    service.dispose();
  });
});

test("an extra Claude account reads only its own folder and gives it to the CLI explicitly", async () => {
  const names = ["USAGEAI_CLAUDE_OAUTH_TOKEN", "USAGEAI_CLAUDE_SESSION_KEY", "CLAUDE_CONFIG_DIR", "CLAUDE_PATH", "PATH"] as const;
  const previous = new Map(names.map((name) => [name, process.env[name]]));
  await withTemporaryFolder("usageai-claude-account-", async (directory) => {
    try {
      const accountFolder = path.join(directory, ".claude-lab");
      await mkdir(accountFolder);
      await writeFile(path.join(accountFolder, ".credentials.json"), JSON.stringify({
        claudeAiOauth: { accessToken: "account-access", expiresAt: Date.now() + 3_600_000, scopes: ["user:profile"], subscriptionType: "max" },
      }), "utf8");
      process.env.USAGEAI_CLAUDE_OAUTH_TOKEN = "environment-override";
      process.env.USAGEAI_CLAUDE_SESSION_KEY = "web-session";
      process.env.CLAUDE_CONFIG_DIR = path.join(directory, "inherited");

      const requests: string[] = [];
      mock.method(security, "requestJson", (async (url, options) => {
        requests.push(`${new URL(String(url)).hostname} ${options.headers?.Authorization ?? ""}`);
        return { status: 200, headers: {}, data: { five_hour: { utilization: 12 } } };
      }) as typeof security.requestJson);
      const client = new ClaudeUsageClient(async () => false, { id: "1a2b3c4d", configDirectory: accountFolder });
      assert.equal(client.id, "claude@1a2b3c4d");
      assert.match(client.signInCommand, /CLAUDE_CONFIG_DIR.*\.claude-lab.*claude auth login --claudeai/);
      const snapshot = await client.getUsage();
      assert.equal(snapshot.providerId, "claude");
      assert.equal(snapshot.metrics[0]?.usedPercent, 12);
      // Never the environment token, never claude.ai's web session: only its own login, to the same host.
      assert.deepEqual(requests, ["api.anthropic.com Bearer account-access"]);
      mock.restoreAll();

      assert.deepEqual(claudeCliEnvironment(accountFolder), {
        inherited: ["CLAUDE_CODE_GIT_BASH_PATH", "NODE_EXTRA_CA_CERTS", "SSL_CERT_DIR", "SSL_CERT_FILE"],
        overrides: { DISABLE_AUTOUPDATER: "1", CLAUDE_CONFIG_DIR: accountFolder },
      });
      // The main login's CLI never inherits CLAUDE_CONFIG_DIR: it uses the standard ~/.claude.
    assert.ok(!claudeCliEnvironment().inherited.includes("CLAUDE_CONFIG_DIR"));
    assert.equal(claudeCliEnvironment().overrides.CLAUDE_CONFIG_DIR, undefined);

      // The recovery child sees the account's folder, not the inherited one.
      const marker = path.join(directory, "config-dir-seen");
      const source = `
        require("node:fs").appendFileSync(${JSON.stringify(marker)}, (process.env.CLAUDE_CONFIG_DIR || "") + "\\n");
        if (process.argv[2] === "auth") process.stdout.write(JSON.stringify({ loggedIn: true }));
      `;
      let scriptPath: string;
      if (process.platform === "win32") {
        const commandPath = path.join(directory, "claude.cmd");
        scriptPath = path.join(directory, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        await mkdir(path.dirname(scriptPath), { recursive: true });
        await writeFile(commandPath, "@echo off\r\n", "utf8");
        process.env.CLAUDE_PATH = commandPath;
        process.env.PATH = `${path.dirname(process.execPath)}${path.delimiter}${previous.get("PATH") ?? ""}`;
      } else {
        scriptPath = path.join(directory, "claude");
        process.env.CLAUDE_PATH = scriptPath;
      }
      await writeFile(scriptPath, process.platform === "win32" ? source : `#!${process.execPath}\n${source}`, "utf8");
      if (process.platform !== "win32") await chmod(scriptPath, 0o755);
      assert.equal(await runClaudeAuthRecovery(undefined, noExtensions, accountFolder), true);
      assert.equal((await readFile(marker, "utf8")).trim(), accountFolder);
    } finally {
      mock.restoreAll();
      for (const name of names) restoreEnvironment(name, previous.get(name));
    }
  });
});

test("an extra Codex account reads its own auth.json and starts the app-server in its own home", async () => {
  const previous = process.env.CODEX_HOME;
  await withTemporaryFolder("usageai-codex-account-", async (directory) => {
    try {
      const inherited = path.join(directory, "inherited");
      const account = path.join(directory, ".codex-work");
      await mkdir(inherited);
      await mkdir(account);
      const accountToken = jwt({ exp: Math.floor(Date.now() / 1000) + 3_600, sub: "account" });
      await writeFile(path.join(inherited, "auth.json"), JSON.stringify({ tokens: { access_token: "inherited-token" } }), "utf8");
      await writeFile(path.join(account, "auth.json"), JSON.stringify({ tokens: { access_token: accountToken, account_id: "acct-9" } }), "utf8");
      process.env.CODEX_HOME = inherited;

      const seen: string[] = [];
      mock.method(security, "requestJson", (async (url, options) => {
        seen.push(`${new URL(String(url)).hostname} ${options.headers?.Authorization ?? ""} ${options.headers?.["ChatGPT-Account-Id"] ?? ""}`);
        return { status: 200, headers: {}, data: { plan_type: "pro", rate_limit: { primary_window: { used_percent: 40, limit_window_seconds: 18_000 } } } };
      }) as typeof security.requestJson);
      const client = new CodexUsageClient(undefined, {}, { id: "5e6f7a8b", home: account });
      assert.equal(client.id, "codex@5e6f7a8b");
      assert.match(client.signInCommand, /CODEX_HOME.*\.codex-work.*codex login/);
      assert.equal((await client.getUsage()).metrics[0]?.usedPercent, 40);
      assert.deepEqual(seen, [`chatgpt.com Bearer ${accountToken} acct-9`]);

      assert.deepEqual(codexAppServerEnvironment(account), {
        inherited: ["NODE_EXTRA_CA_CERTS", "SSL_CERT_DIR", "SSL_CERT_FILE"],
        overrides: { CODEX_HOME: account },
      });
      assert.deepEqual(codexAppServerEnvironment().overrides, {});
      // The main login's app-server never inherits CODEX_HOME: it uses the standard ~/.codex.
    assert.ok(!codexAppServerEnvironment().inherited.includes("CODEX_HOME"));
    assert.equal(codexAppServerEnvironment().overrides.CODEX_HOME, undefined);
    } finally {
      mock.restoreAll();
      restoreEnvironment("CODEX_HOME", previous);
    }
  });
});

test("the refresh service follows account changes and titles cards only when a provider has several", async () => {
  const fixture = (id: string, displayName: string): UsageClient => ({
    id,
    displayName,
    signInCommand: `${id} login`,
    accountUrl: "https://example.com",
    async getUsage(): Promise<UsageSnapshot> {
      return { plan: "Pro", metrics: [], fetchedAt: new Date().toISOString(), providerId: providerKindOf(id), providerName: displayName };
    },
  });
  const labels: Record<string, string> = { claude: "personal@example.com", "claude@1a2b3c4d": "Team" };
  const cachedTeam: UsageSnapshot = { plan: "Max", metrics: [], fetchedAt: "2026-11-01T00:00:00Z", providerId: "claude", providerName: "Claude Code" };
  const service = new UsageRefreshService({
    clients: [fixture("codex", "Codex"), fixture("claude", "Claude Code")],
    enabledProviderIds: () => ["claude", "codex"],
    visibleIntervalMs: () => 60_000,
    backgroundIntervalMs: () => 60_000,
    initialSnapshots: { "claude@1a2b3c4d": cachedTeam },
    accountLabelFor: (unit) => labels[unit],
  });
  await service.refresh(true);
  assert.deepEqual(service.getStates().map((state) => state.displayName), ["Claude Code", "Codex"]);

  const fetched: string[] = [];
  const team = fixture("claude@1a2b3c4d", "Claude Code");
  service.setClients([fixture("codex", "Codex"), fixture("claude", "Claude Code"), {
    ...team,
    async getUsage() { fetched.push(team.id); return team.getUsage(); },
  }]);
  const states = service.getStates();
  assert.deepEqual(states.map((state) => [state.id, state.displayName, state.accountLabel]), [
    ["claude", "Claude Code · personal@example.com", "personal@example.com"],
    ["claude@1a2b3c4d", "Claude Code · Team", "Team"],
    ["codex", "Codex", undefined],
  ]);
  // A new account starts from its cached reading and is fetched at once.
  assert.equal(states[1]?.snapshot, cachedTeam);
  assert.equal(states[1]?.stale, true);
  await service.refresh(false);
  assert.deepEqual(fetched, ["claude@1a2b3c4d"]);
  assert.equal(service.getStates()[1]?.stale, false);

  service.setClients([fixture("codex", "Codex"), fixture("claude", "Claude Code")]);
  assert.deepEqual(service.getStates().map((state) => state.id), ["claude", "codex"]);
  assert.equal(service.getStates()[0]?.accountLabel, undefined);
  service.dispose();
});

interface FakeUiScript {
  readonly picks: Array<string | undefined>;
  readonly inputs: Array<string | undefined | ((validate?: (value: string) => unknown) => Promise<string | undefined>)>;
  readonly folder?: string;
  readonly infoChoice?: string;
}

function fakeUi(script: FakeUiScript, log: string[], onLogin?: (environment: Readonly<Record<string, string>>) => Promise<void>): AddAccountUi {
  return {
    async pick(items) {
      const wanted = script.picks.shift();
      const item = items.find((candidate) => candidate.label === wanted);
      log.push(`pick ${item?.label ?? "-"}`);
      return item;
    },
    async input(options) {
      const next = script.inputs.shift();
      if (typeof next === "function") return next(options.validate);
      log.push(`input ${options.value ?? ""} -> ${next ?? "-"}`);
      return next;
    },
    async pickFolder() { return script.folder; },
    async info(message) { log.push(`info ${message}`); return script.infoChoice; },
    error(message) { log.push(`error ${message}`); },
    async copy(text) { log.push(`copy ${text}`); },
    runLogin(options) {
      log.push(`terminal ${options.executable} ${options.args.join(" ")} ${JSON.stringify(options.environment)}`);
      return { closed: onLogin ? onLogin(options.environment) : new Promise<void>(() => {}) };
    },
    async waitWithProgress(_title, task) { return task(() => false); },
  };
}

test("Add Account signs in to a new folder with the official CLI, or adds a folder already there", async () => {
  await withTemporaryFolder("usageai-add-account-", async (home) => {
    await writeLogin(path.join(home, ".claude-lab"), "claude");
    let settings: unknown[] = [{ provider: "codex", path: path.join(home, ".codex-x") }];
    const added: string[] = [];
    const shown: AccountEntry[] = [entry({ provider: "claude", id: "default", source: "default", path: path.join(home, ".claude"), label: "me@example.com", identity: { email: "me@example.com", organizationId: "org-1" } })];
    const log: string[] = [];
    const dependencies = (script: FakeUiScript, onLogin?: (environment: Readonly<Record<string, string>>) => Promise<void>): AddAccountDependencies => ({
      ui: fakeUi(script, log, onLogin),
      providerName: (provider) => provider === "claude" ? "Claude Code" : "Codex",
      home: () => home,
      accounts: () => shown,
      configuredAccounts: () => settings,
      saveConfiguredAccounts: async (value) => { settings = [...value]; },
      findLogin: async () => ({ executable: process.execPath, args: ["cli.js"] }),
      afterAdd: async (account) => { added.push(account.path); },
      pollIntervalMs: 5,
      timeoutMs: 200,
    });

    // The proposed folder comes from the name; the login runs with CLAUDE_CONFIG_DIR set to it.
    assert.equal(await addAccount(dependencies(
      { picks: ["Claude Code", "Sign in to another account"], inputs: ["Lab", undefined] },
    )), "cancelled");
    assert.ok(log.includes(`input ${path.join(home, ".claude-lab-2")} -> -`));
    log.length = 0;
    const result = await addAccount(dependencies(
      { picks: ["Claude Code", "Sign in to another account"], inputs: ["Work", async (validate) => {
        assert.match(String(await validate?.("relative")), /absolute/);
        assert.match(String(await validate?.("\\\\server\\share")), /UNC/);
        assert.match(String(await validate?.(home)), /missing or empty/);
        assert.equal(await validate?.(path.join(home, ".claude-work")), undefined);
        return path.join(home, ".claude-work");
      }] },
      async (environment) => {
        assert.equal(environment.CLAUDE_CONFIG_DIR, path.join(home, ".claude-work"));
        await writeLogin(path.join(home, ".claude-work"), "claude", { email: "work@example.com" });
      },
    ));
    assert.equal(result, "added");
    assert.ok(log.some((line) => line.startsWith(`terminal ${process.execPath} cli.js auth login --claudeai`)));
    assert.deepEqual(settings.at(-1), { provider: "claude", path: path.join(home, ".claude-work"), label: "Work" });
    assert.equal(settings.length, 2);
    assert.deepEqual(added, [path.join(home, ".claude-work")]);
    assert.ok(log.includes("info Added Claude account Work."));

    // A sign-in that never finishes leaves no folder behind.
    log.length = 0;
    assert.equal(await addAccount(dependencies({ picks: ["Codex", "Sign in to another account"], inputs: ["", path.join(home, ".codex-account-2")] })), "failed");
    assert.ok(log.some((line) => line.includes("cli.js login {\"CODEX_HOME\"")));
    await assert.rejects(readFile(path.join(home, ".codex-account-2", "auth.json")));
    await assert.rejects(stat(path.join(home, ".codex-account-2")));

    // A folder that already holds a login is offered as "use this login instead".
    log.length = 0;
    assert.equal(await addAccount(dependencies({
      picks: ["Claude Code", "Sign in to another account"],
      inputs: ["", path.join(home, ".claude-lab")],
      infoChoice: "Use this login instead",
    })), "added");
    assert.ok(log.some((line) => line.includes("already holds a Claude login")));
    assert.deepEqual(settings.at(-1), { provider: "claude", path: path.join(home, ".claude-lab") });

    // Existing folders: the default login, the same person, and a folder without a login are refused.
    log.length = 0;
    await writeLogin(path.join(home, ".claude"), "claude");
    assert.equal(await addAccount(dependencies({ picks: ["Claude Code", "Use a login folder I already have"], inputs: [], folder: path.join(home, ".claude") })), "duplicate");
    await writeLogin(path.join(home, "elsewhere"), "claude", { email: "ME@example.com", org: "org-1" });
    assert.equal(await useExistingFolder(dependencies({ picks: [], inputs: [] }), "claude", path.join(home, "elsewhere")), "duplicate");
    assert.equal(await useExistingFolder(dependencies({ picks: [], inputs: [] }), "codex", path.join(home, "elsewhere")), "failed");
    assert.equal(await useExistingFolder(dependencies({ picks: [], inputs: [] }), "codex", "\\\\server\\share"), "failed");
    assert.deepEqual(log.filter((line) => line.startsWith("info Already")), ["info Already shown as me@example.com.", "info Already shown as me@example.com."]);
    assert.equal(log.filter((line) => line.startsWith("error UsageAI cannot use")).length, 2);
    await writeLogin(path.join(home, "other-codex"), "codex", { email: "c@example.com" });
    assert.equal(await useExistingFolder(dependencies({ picks: [], inputs: ["  "] }), "codex", path.join(home, "other-codex")), "added");
    assert.deepEqual(settings.at(-1), { provider: "codex", path: path.join(home, "other-codex") });
    assert.equal(await addAccount(dependencies({ picks: [undefined], inputs: [] })), "cancelled");

    // Without a CLI there is no sign-in; in a remote window the command is copied for a local terminal.
    log.length = 0;
    assert.equal(await addAccount({ ...dependencies({ picks: ["Codex", "Sign in to another account"], inputs: ["", path.join(home, ".codex-none")] }), findLogin: async () => undefined }), "failed");
    assert.ok(log.some((line) => /Codex CLI was not found/.test(line)));
    log.length = 0;
    const remote = { ...dependencies({ picks: ["Codex", "Sign in to another account"], inputs: ["remote", path.join(home, ".codex-remote")] }), canRunLoginInTerminal: () => false };
    setTimeout(() => void writeLogin(path.join(home, ".codex-remote"), "codex"), 20);
    assert.equal(await addAccount(remote), "added");
    assert.ok(log.some((line) => line.startsWith("copy ") && line.includes("CODEX_HOME") && line.includes("codex login")));
  });

  assert.equal(sanitizeAccountName("  My Team!! 2 "), "my-team-2");
  assert.equal(await proposeLoginFolder("codex", "", "/home/dev", async (folder) => folder.endsWith("account-2")), path.join("/home/dev", ".codex-account-3"));
});

test("two accounts with one label are told apart by organisation, else by number", () => {
  const byOrg = distinctAccountLabels([
    { label: "me@x.com", identity: { email: "me@x.com", organizationName: "Personal" } },
    { label: "ME@x.com", identity: { email: "me@x.com", organizationName: "Acme" } },
    { label: "team", identity: {} },
  ]);
  assert.deepEqual(byOrg.map((account) => account.label), ["me@x.com · Personal", "ME@x.com · Acme", "team"]);
  const numbered = distinctAccountLabels([
    { label: "me@x.com", identity: { email: "me@x.com" } },
    { label: "me@x.com", identity: { email: "me@x.com", organizationName: "Acme" } },
  ]);
  assert.deepEqual(numbered.map((account) => account.label), ["me@x.com", "me@x.com · Account 2"]);
});

test("a usageai.accounts write the window has not registered asks for a reload instead of failing raw", async () => {
  // VS Code updates an extension in place and restarts only the extension host: the new code runs,
  // but the window keeps the old version's settings, so a setting new in this version is refused.
  const unregistered = new Error("Unable to write to User Settings because usageai.accounts is not a registered configuration.");
  assert.equal(describeSettingsWriteError(unregistered).reload, true);
  assert.match(describeSettingsWriteError(unregistered).message, /Reload the window/);
  assert.deepEqual(describeSettingsWriteError("disk full"), { message: "UsageAI could not save usageai.accounts: disk full", reload: false });
  // In another display language VS Code's text differs; the host's own check still says reload.
  const localised = new SettingNotRegisteredError(new Error("Impossible d'écrire dans les paramètres utilisateur."));
  assert.equal(describeSettingsWriteError(localised).reload, true);

  await withTemporaryFolder("usageai-add-account-unregistered-", async (home) => {
    await writeLogin(path.join(home, ".claude-team"), "claude", { email: "team@example.com" });
    const log: string[] = [];
    const reloads: string[] = [];
    let added = 0;
    const dependencies = (offerReload: boolean, failure: unknown): AddAccountDependencies => ({
      ui: { ...fakeUi({ picks: [], inputs: ["Team"] }, log), ...(offerReload ? { offerReload: (message: string) => { reloads.push(message); } } : {}) },
      providerName: (provider) => provider === "claude" ? "Claude Code" : "Codex",
      home: () => home,
      accounts: () => [],
      configuredAccounts: () => [],
      saveConfiguredAccounts: async () => { throw failure; },
      findLogin: async () => undefined,
      afterAdd: async () => { added += 1; },
    });
    assert.equal(await useExistingFolder(dependencies(true, unregistered), "claude", path.join(home, ".claude-team")), "failed");
    assert.equal(reloads.length, 1);
    assert.match(reloads[0] ?? "", /Reload the window/);
    // Without a reload button the same words arrive as a plain error; nothing claims it was added.
    assert.equal(await useExistingFolder(dependencies(false, unregistered), "claude", path.join(home, ".claude-team")), "failed");
    assert.ok(log.some((line) => line.startsWith("error VS Code has not loaded")));
    assert.equal(added, 0);
    assert.ok(!log.some((line) => line.startsWith("info Added")));

    // Adding a folder whose entry is hidden updates that entry in place and shows it again.
    let settings: unknown[] = ["junk", { provider: "claude", path: path.join(home, ".claude-team"), label: "Old", hidden: true }];
    const readd = (): AddAccountDependencies => ({ ...dependencies(false, undefined), configuredAccounts: () => settings, saveConfiguredAccounts: async (value: readonly unknown[]) => { settings = [...value]; } });
    assert.equal(await useExistingFolder(readd(), "claude", path.join(home, ".claude-team")), "added");
    assert.deepEqual(settings, ["junk", { provider: "claude", path: path.join(home, ".claude-team"), label: "Team" }]);
    settings = Array.from({ length: 16 }, (_, index) => ({ provider: "codex", path: path.join(home, `.codex-${index}`) }));
    log.length = 0;
    assert.equal(await useExistingFolder(readd(), "claude", path.join(home, ".claude-team")), "failed");
    assert.ok(log.some((line) => /already lists 16 folders/.test(line)));
  });
});

test("changing one account's usageai.accounts entry keeps every other entry as written", () => {
  const home = path.resolve(os.tmpdir(), "usageai-manage-home");
  const main = path.join(home, ".claude");
  const work = path.join(home, ".claude-work");
  const other = { provider: "codex", path: path.join(home, ".codex-x"), label: "X" };
  const sameFolderOtherProvider = { provider: "codex", path: work };
  const current = ["junk", { provider: "claude", path: `${work}${path.sep}`, label: "Old" }, other, sameFolderOtherProvider];

  // Renamed in place, the user's own spelling of the path kept.
  assert.deepEqual(changeAccountSetting(current, "claude", work, { label: " New " }), [
    "junk", { provider: "claude", path: `${work}${path.sep}`, label: "New" }, other, sameFolderOtherProvider,
  ]);
  assert.deepEqual(changeAccountSetting(current, "claude", work, { hidden: true }), [
    "junk", { provider: "claude", path: `${work}${path.sep}`, label: "Old", hidden: true }, other, sameFolderOtherProvider,
  ]);
  assert.deepEqual(changeAccountSetting(current, "claude", work, { label: null }), [
    "junk", { provider: "claude", path: `${work}${path.sep}` }, other, sameFolderOtherProvider,
  ]);
  assert.deepEqual(changeAccountSetting(current, "claude", work, "remove"), ["junk", other, sameFolderOtherProvider]);
  // A new entry goes last; the main login's entry goes once it says nothing.
  assert.deepEqual(changeAccountSetting(undefined, "claude", main, { label: "Me" }), [{ provider: "claude", path: main, label: "Me" }]);
  assert.deepEqual(changeAccountSetting([{ provider: "claude", path: main, label: "Me" }], "claude", main, { label: "", dropWhenPlain: true }), []);
  assert.deepEqual(changeAccountSetting([{ provider: "claude", path: main, hidden: true }], "claude", main, { hidden: false, dropWhenPlain: true }), []);
  // A quoted path is read the way the setting reads it.
  assert.equal(hasAccountSetting([{ provider: "claude", path: `"${work}"` }], "claude", work), true);
  assert.equal(hasAccountSetting([{ provider: "codex", path: work }], "claude", work), false);
  assert.equal(hasAccountSetting(undefined, "claude", work), false);
  // The limit counts distinct, well-formed folders only, and never blocks a list already over it.
  const folders = (count: number) => Array.from({ length: count }, (_, index) => ({ provider: "codex", path: path.join(home, `.codex-${index}`) }));
  assert.equal(exceedsConfiguredAccounts([...folders(16), "junk"], [...folders(16), "junk", { provider: "codex", path: "relative" }]), false);
  assert.equal(exceedsConfiguredAccounts(folders(16), [...folders(16), { provider: "claude", path: work }]), true);
  assert.equal(exceedsConfiguredAccounts(folders(17), folders(17)), false);
  assert.equal(exceedsConfiguredAccounts(undefined, folders(15)), false);
});

test("Manage Accounts renames, hides, shows and removes logins without touching their folders", async () => {
  const home = path.resolve(os.tmpdir(), "usageai-manage-flow");
  const mainPath = path.join(home, ".claude");
  const workPath = path.join(home, ".claude-work");
  const labPath = path.join(home, ".claude-lab");
  const newPath = path.join(home, ".codex-new");
  const copyPath = path.join(home, ".claude-copy");
  const gonePath = path.join(home, ".claude-gone");
  const main = entry({ provider: "claude", id: "default", source: "default", path: mainPath, label: "me@example.com", identity: { email: "me@example.com" } });
  const work = entry({ provider: "claude", id: accountIdForPath(workPath), source: "added", path: workPath, label: "Work", userLabel: "Work", identity: { email: "work@example.com" } });
  const lab = entry({ provider: "claude", id: accountIdForPath(labPath), source: "found", path: labPath, label: "lab@example.com" });
  const fresh = entry({ provider: "codex", id: accountIdForPath(newPath), source: "found", path: newPath, label: "new@example.com" });
  const copy = entry({ provider: "claude", id: accountIdForPath(copyPath), source: "found", path: copyPath, label: "me@example.com · copy" });
  const gone = entry({ provider: "claude", id: accountIdForPath(gonePath), source: "added", path: gonePath, label: "Old" });

  let settings: unknown = [{ provider: "claude", path: workPath, label: "Work" }];
  let catalog: ManagedAccount[] = [
    { account: main, status: "shown", found: false },
    { account: work, status: "shown", found: true },
    { account: lab, status: "shown", found: true },
    { account: fresh, status: "pending", found: true },
    { account: copy, status: "duplicate", shownAs: "me@example.com", found: true },
  ];
  const decisions = new Map<string, "show" | "ignore">([[work.id, "show"], [lab.id, "show"]]);
  const log: string[] = [];
  const offered: string[][] = [];
  const run = async (
    script: { picks: Array<string | undefined>; inputs?: Array<string | undefined>; confirm?: boolean; remote?: boolean },
    save?: (value: readonly unknown[]) => Promise<void>,
  ) => {
    log.length = 0;
    offered.length = 0;
    const dependencies: ManageAccountsDependencies = {
      ui: {
        async pick(items) {
          offered.push(items.map((item) => item.label));
          const wanted = script.picks.shift();
          return items.find((item) => item.label === wanted);
        },
        async input(options) {
          log.push(`input ${options.value ?? ""}|${options.placeHolder ?? ""}`);
          return script.inputs?.shift();
        },
        async info(message) { log.push(`info ${message}`); return undefined; },
        error(message) { log.push(`error ${message}`); },
        offerReload(message) { log.push(`reload ${message}`); },
        async copy(text) { log.push(`copy ${text}`); },
        async confirm(message) { log.push(`confirm ${message}`); return script.confirm ?? false; },
        ...(script.remote ? {} : { reveal: (folder: string) => { log.push(`reveal ${folder}`); } }),
      },
      providerName: (provider) => provider === "claude" ? "Claude Code" : "Codex",
      catalog: () => catalog,
      configuredAccounts: () => settings,
      saveConfiguredAccounts: save ?? (async (value) => { settings = [...value]; log.push("saved"); }),
      decisionFor: (id) => decisions.get(id),
      answer: async (id, decision) => {
        log.push(`answer ${id} ${decision ?? "none"}`);
        if (decision) decisions.set(id, decision);
        else decisions.delete(id);
      },
      folderExists: async (folder) => folder !== gonePath,
      addAccount: async () => { log.push("add"); },
    };
    return manageAccounts(dependencies);
  };

  assert.equal(await run({ picks: [undefined] }), "cancelled");
  assert.deepEqual(offered[0], ["me@example.com", "Work", "lab@example.com", "new@example.com", "me@example.com · copy", "$(add) Add Account…"]);
  assert.equal(await run({ picks: ["$(add) Add Account…"] }), "unchanged");
  assert.deepEqual(log, ["add"]);

  // The main login: renamed through its own entry, which goes again once the name is cleared.
  // Saving re-reads the accounts through the settings event, never a second time from here.
  assert.equal(await run({ picks: ["me@example.com", "Rename…"], inputs: ["Personal"] }), "changed");
  assert.deepEqual(offered[1], ["Rename…", "Hide", "Open Folder", "Copy Folder Path"]);
  assert.deepEqual(log, ["input |me@example.com", "saved"]);
  assert.deepEqual(settings, [{ provider: "claude", path: workPath, label: "Work" }, { provider: "claude", path: mainPath, label: "Personal" }]);
  catalog = [{ account: { ...main, userLabel: "Personal", label: "Personal" }, status: "shown", found: false }, ...catalog.slice(1)];
  assert.equal(await run({ picks: ["Personal", "Rename…"], inputs: ["Personal "] }), "unchanged");
  assert.equal(await run({ picks: ["Personal", "Rename…"], inputs: [""] }), "changed");
  assert.deepEqual(settings, [{ provider: "claude", path: workPath, label: "Work" }]);
  assert.equal(await run({ picks: ["Personal", "Rename…"], inputs: [undefined] }), "cancelled");
  assert.equal(await run({ picks: ["Personal", "Hide"] }), "changed");
  assert.deepEqual(settings, [{ provider: "claude", path: workPath, label: "Work" }, { provider: "claude", path: mainPath, hidden: true }]);

  // A found login is hidden by ignoring it, not by writing an entry for it.
  assert.equal(await run({ picks: ["lab@example.com", "Hide"] }), "changed");
  assert.deepEqual(offered[1], ["Rename…", "Hide", "Open Folder", "Copy Folder Path"]);
  assert.deepEqual(log, [`answer ${lab.id} ignore`]);
  assert.equal(await run({ picks: ["new@example.com", "Show"] }), "changed");
  assert.deepEqual(offered[1], ["Show", "Hide", "Open Folder", "Copy Folder Path"]);
  assert.deepEqual(log, [`answer ${fresh.id} show`]);

  // A renamed found login loses its entry once the name is cleared, shown as found again.
  decisions.delete(lab.id);
  settings = [{ provider: "claude", path: labPath, label: "Lab" }];
  catalog = [{ account: { ...lab, source: "added", userLabel: "Lab", label: "Lab" }, status: "shown", found: true }];
  assert.equal(await run({ picks: ["Lab", "Rename…"], inputs: [""] }), "changed");
  assert.deepEqual(log.slice(1), [`answer ${lab.id} show`, "saved"]);
  assert.deepEqual(settings, []);
  // A refused write puts the earlier answer back.
  settings = [{ provider: "claude", path: labPath, label: "Lab" }];
  decisions.delete(lab.id);
  const refused = async () => { throw new Error("Unable to write to User Settings because usageai.accounts is not a registered configuration."); };
  assert.equal(await run({ picks: ["Lab", "Rename…"], inputs: [""] }, refused), "failed");
  assert.deepEqual(log.slice(1, 2), [`answer ${lab.id} show`]);
  assert.ok(log.some((line) => line.startsWith("reload VS Code has not loaded")));
  assert.equal(log.at(-1), `answer ${lab.id} none`);
  assert.equal(decisions.has(lab.id), false);

  // Removing an added login asks first. A found folder is ignored before its entry goes, so it is
  // never shown in between, and a refused write puts the earlier answer back.
  settings = [{ provider: "claude", path: workPath, label: "Work" }, { provider: "claude", path: mainPath, hidden: true }];
  catalog = [{ account: work, status: "shown", found: true }];
  assert.equal(await run({ picks: ["Work", "Remove"], confirm: false }), "cancelled");
  assert.deepEqual(offered[1], ["Rename…", "Hide", "Remove", "Open Folder", "Copy Folder Path"]);
  assert.equal(await run({ picks: ["Work", "Remove"], confirm: true }, refused), "failed");
  assert.deepEqual(log.filter((line) => line.startsWith("answer")), [`answer ${work.id} ignore`, `answer ${work.id} show`]);
  assert.equal(await run({ picks: ["Work", "Remove"], confirm: true }), "changed");
  assert.deepEqual(log.slice(1), [`answer ${work.id} ignore`, "saved"]);
  assert.deepEqual(settings, [{ provider: "claude", path: mainPath, hidden: true }]);

  // A duplicate can only be looked at; its folder can be opened or copied, but opened only where
  // the file manager is on this computer.
  catalog = [{ account: copy, status: "duplicate", shownAs: "me@example.com", found: true }];
  assert.equal(await run({ picks: ["me@example.com · copy", "Open Folder"] }), "unchanged");
  assert.deepEqual(offered[1], ["Open Folder", "Copy Folder Path"]);
  assert.deepEqual(log, [`reveal ${copyPath}`]);
  assert.equal(await run({ picks: ["me@example.com · copy", "Copy Folder Path"] }), "unchanged");
  assert.deepEqual(log, [`copy ${copyPath}`, `info Copied ${copyPath}`]);
  assert.equal(await run({ picks: ["me@example.com · copy", undefined], remote: true }), "cancelled");
  assert.deepEqual(offered[1], ["Copy Folder Path"]);

  // An entry whose folder is gone is listed so it can be removed; it has no folder to open.
  settings = [{ provider: "claude", path: gonePath, label: "Old" }, { provider: "claude", path: mainPath, hidden: true }];
  catalog = [{ account: gone, status: "unavailable", problem: "it holds no .credentials.json", found: false }];
  assert.equal(await run({ picks: [undefined] }), "cancelled");
  assert.match(offered[0]?.[0] ?? "", /^Old$/);
  assert.equal(await run({ picks: ["Old", "Remove"], confirm: true }), "changed");
  assert.deepEqual(offered[1], ["Remove", "Copy Folder Path"]);
  assert.deepEqual(log.slice(1), ["saved"]);
  assert.deepEqual(settings, [{ provider: "claude", path: mainPath, hidden: true }]);

  // A hidden main login is shown again by clearing the flag.
  catalog = [{ account: { ...main, hidden: true }, status: "hidden", found: false }];
  assert.equal(await run({ picks: ["me@example.com", "Show"] }), "changed");
  assert.deepEqual(offered[1], ["Rename…", "Show", "Open Folder", "Copy Folder Path"]);
  assert.deepEqual(settings, []);

  // Only folders the setting reads take a slot: malformed entries never block a rename.
  catalog = [{ account: lab, status: "shown", found: true }];
  const junk = ["junk", { provider: "other", path: workPath }, { provider: "codex", path: "relative" }, { provider: "codex" }];
  settings = [...junk, ...Array.from({ length: 12 }, (_, index) => ({ provider: "codex", path: path.join(home, `.codex-${index}`) }))];
  assert.equal(await run({ picks: ["lab@example.com", "Rename…"], inputs: ["Lab"] }), "changed");
  assert.equal((settings as unknown[]).length, 17);
  settings = Array.from({ length: 16 }, (_, index) => ({ provider: "codex", path: path.join(home, `.codex-${index}`) }));
  assert.equal(await run({ picks: ["lab@example.com", "Rename…"], inputs: ["Lab"] }), "failed");
  assert.ok(log.some((line) => /already lists 16 folders/.test(line)));
  assert.equal((settings as unknown[]).length, 16);
});

test("the account service lists every login for Manage Accounts, shown or not", async () => {
  await withTemporaryFolder("usageai-accounts-catalog-", async (home) => {
    await writeLogin(path.join(home, ".claude"), "claude");
    await writeLogin(path.join(home, ".claude-lab"), "claude", { email: "lab@example.com" });
    await writeLogin(path.join(home, ".claude-skip"), "claude", { email: "skip@example.com", org: "skip" });
    await writeLogin(path.join(home, "elsewhere"), "codex", { email: "c@example.com" });
    let saved: unknown;
    const service = new AccountService({
      home: () => home,
      environment: () => ({}),
      settings: () => [
        { provider: "codex", path: path.join(home, "elsewhere"), hidden: true },
        { provider: "claude", path: path.join(home, "gone"), label: "Old" },
      ],
      loadState: () => saved,
      saveState: (state) => { saved = state; },
    });
    await service.scan();
    await service.answer(accountIdForPath(path.join(home, ".claude-skip")), "ignore");
    assert.deepEqual(service.catalog().map((managed) => [managed.account.label, managed.status, managed.found]), [
      [".claude", "shown", false],
      ["lab@example.com", "shown", true],
      ["skip@example.com", "ignored", true],
      ["Old", "unavailable", false],
      [".codex", "shown", false],
      ["c@example.com", "hidden", false],
    ]);
    assert.match(service.catalog()[3]?.problem ?? "", /credentials\.json/);
    assert.equal(service.decisionFor(accountIdForPath(path.join(home, ".claude-skip"))), "ignore");
    assert.equal(service.decisionFor(accountIdForPath(path.join(home, ".claude-lab"))), "show");
    await service.answer(accountIdForPath(path.join(home, ".claude-lab")), undefined);
    assert.equal(service.decisionFor(accountIdForPath(path.join(home, ".claude-lab"))), undefined);
    service.dispose();
  });
});

test("a found login answered in Manage Accounts ignores a late click on its notification", async () => {
  await withTemporaryFolder("usageai-accounts-late-answer-", async (home) => {
    await writeLogin(path.join(home, ".claude"), "claude");
    let saved: unknown = { shown: [], ignored: [], lastScanAt: 1, standardMain: true };
    let click: ((decision: "show" | "ignore" | undefined) => void) | undefined;
    const service = new AccountService({
      home: () => home,
      environment: () => ({}),
      settings: () => [],
      loadState: () => saved,
      saveState: (state) => { saved = state; },
      askToShow: () => new Promise((resolve) => { click = resolve; }),
    });
    await writeLogin(path.join(home, ".claude-new"), "claude", { email: "new@example.com" });
    await service.scan();
    const id = accountIdForPath(path.join(home, ".claude-new"));
    assert.equal(service.pending[0]?.id, id);
    await service.answer(id, "ignore");
    click?.("show");
    for (let index = 0; index < 5; index += 1) await new Promise((resolve) => setImmediate(resolve));
    assert.equal(service.decisionFor(id), "ignore");
    assert.ok(!service.accounts.some((account) => account.id === id));
    service.dispose();
  });
});
