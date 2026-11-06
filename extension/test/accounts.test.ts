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
} from "../src/account-service";
import {
  accountCardTitle,
  accountFolderProblem,
  accountIdForPath,
  accountLabel,
  claudeProfilePath,
  commandWithEnvironment,
  defaultLoginFolder,
  discoverAccountFolders,
  identityKey,
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
import { addAccount, useExistingFolder, type AddAccountDependencies, type AddAccountUi } from "../src/add-account";
import { noExtensions } from "../src/bundled-cli";
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
  const expected = createHash("sha256").update("c:\\users\\dev\\.claude-team", "utf8").digest("hex").slice(0, 8);
  assert.equal(accountIdForPath("C:\\Users\\Dev\\.claude-team\\", "win32"), expected);
  assert.equal(accountIdForPath("c:\\USERS\\dev\\.claude-team", "win32"), expected);
  assert.equal(normalizeAccountPath("C:\\", "win32"), "c:\\");
  // Only Windows folds case.
  assert.equal(normalizeAccountPath("/home/Dev/.claude-team/", "linux"), "/home/Dev/.claude-team");
  assert.notEqual(accountIdForPath("/home/Dev/.claude-team", "linux"), accountIdForPath("/home/dev/.claude-team", "linux"));
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
    await writeLogin(path.join(home, ".claude-team"), "claude");
    await writeLogin(path.join(home, ".CLAUDE_work"), "claude");
    await mkdir(path.join(home, ".claude-empty"));
    await writeLogin(path.join(home, ".claudex"), "claude");
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
      ["claude", ".claude-team"],
      ["codex", ".codex-alt"],
      ["claude", ".CLAUDE_work"],
    ].sort((left, right) => left[1]!.localeCompare(right[1]!)));
    assert.equal(found[0]?.id, accountIdForPath(found[0]!.path));
  });
  assert.deepEqual(await discoverAccountFolders(path.join(os.tmpdir(), `usageai-missing-${Date.now()}`), {}), []);
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

  assert.equal(claudeProfilePath("/x/.claude", true, "/home/dev"), path.join("/home/dev", ".claude.json"));
  assert.equal(claudeProfilePath("/x/.claude-team", false, "/home/dev"), path.join("/x/.claude-team", ".claude.json"));
  assert.equal(identityKey({ email: "A@Example.com", organizationId: "ORG" }), "a@example.com|org");
  assert.equal(identityKey({ organizationId: "org" }), undefined);

  assert.equal(accountLabel("Team", { email: "a@example.com" }, "/home/dev/.claude-team"), "Team");
  assert.equal(accountLabel(undefined, { email: "a@example.com" }, "/home/dev/.claude-team"), "a@example.com");
  assert.equal(accountLabel(undefined, {}, "/home/dev/.claude-team"), ".claude-team");
  assert.equal(accountCardTitle("Claude Code", "team", 1), "Claude Code");
  assert.equal(accountCardTitle("Claude Code", "team", 2), "Claude Code · team");

  assert.equal(defaultLoginFolder("claude", {}, "/home/dev"), path.join("/home/dev", ".claude"));
  assert.equal(defaultLoginFolder("codex", { CODEX_HOME: "relative" }, "/home/dev"), undefined);
  assert.equal(defaultLoginFolder("codex", { CODEX_HOME: `"${path.resolve("/srv/codex")}"` }, "/home/dev"), path.resolve("/srv/codex"));
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
  const state: AccountState = { shown: [found(".claude-team").id], ignored: [found(".claude-ignored").id] };
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
      { source: found(".claude-team"), identity: { email: "team@example.com", organizationId: "team" } },
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
  assert.equal(resolved.shown[1]?.unitKey, `claude@${found(".claude-team").id}`);
  assert.deepEqual(resolved.pending.map((account) => account.label), ["new@example.com"]);
  assert.deepEqual(resolved.duplicates.map((duplicate) => [path.basename(duplicate.account.path), duplicate.shownAs]), [[".claude-copy", "Personal"]]);

  // A hidden default leaves the provider's other accounts in place.
  const hiddenDefault = resolveAccounts({
    defaults: [{ source: { provider: "claude", path: folder(".claude") }, identity: {} }],
    configured: [{ source: { provider: "claude", path: folder(".claude"), hidden: true }, identity: {} }],
    discovered: [{ source: found(".claude-team"), identity: {} }],
    state,
  });
  assert.deepEqual(hiddenDefault.shown.map((account) => account.source), ["found"]);

  assert.equal(sanitizeAccountState(undefined), undefined);
  assert.deepEqual(sanitizeAccountState({ lastScanAt: -1, shown: ["1a2b3c4d", "1a2b3c4d", "bad", 3], ignored: "x" }), { shown: ["1a2b3c4d"], ignored: [] });
  assert.deepEqual(sanitizeAccountState({ lastScanAt: 5, shown: [], ignored: [] }), { lastScanAt: 5, shown: [], ignored: [] });
});

test("the account service shows first-run finds at once and asks about later ones", async () => {
  await withTemporaryFolder("usageai-accounts-service-", async (home) => {
    await writeLogin(path.join(home, ".claude"), "claude");
    await writeFile(path.join(home, ".claude.json"), JSON.stringify({ oauthAccount: { emailAddress: "me@example.com" } }), "utf8");
    await writeLogin(path.join(home, ".claude-team"), "claude", { email: "team@example.com" });
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
    assert.deepEqual(service.accounts.map((account) => account.label), ["me@example.com", "later@example.com", "team@example.com", ".codex"]);
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
      { provider: "claude", path: path.join(home, ".claude-team"), label: "Team" },
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
      const accountFolder = path.join(directory, ".claude-team");
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
      assert.match(client.signInCommand, /CLAUDE_CONFIG_DIR.*\.claude-team.*claude auth login --claudeai/);
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
      assert.ok(claudeCliEnvironment().inherited.includes("CLAUDE_CONFIG_DIR"));

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
      assert.ok(codexAppServerEnvironment().inherited.includes("CODEX_HOME"));
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
    await writeLogin(path.join(home, ".claude-team"), "claude");
    let settings: unknown[] = [{ provider: "codex", path: path.join(home, ".codex-x") }];
    const added: string[] = [];
    const shown: AccountEntry[] = [entry({ provider: "claude", id: "default", source: "default", path: path.join(home, ".claude"), label: "me@example.com", identity: { email: "me@example.com", organizationId: "org-1" } })];
    const log: string[] = [];
    const dependencies = (script: FakeUiScript, onLogin?: (environment: Readonly<Record<string, string>>) => Promise<void>): AddAccountDependencies => ({
      ui: fakeUi(script, log, onLogin),
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
      { picks: ["Claude Code", "Sign in to another account"], inputs: ["Team", undefined] },
    )), "cancelled");
    assert.ok(log.includes(`input ${path.join(home, ".claude-team-2")} -> -`));
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
      inputs: ["", path.join(home, ".claude-team")],
      infoChoice: "Use this login instead",
    })), "added");
    assert.ok(log.some((line) => line.includes("already holds a Claude login")));
    assert.deepEqual(settings.at(-1), { provider: "claude", path: path.join(home, ".claude-team") });

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
    setTimeout(() => void writeLogin(path.join(home, ".codex-remote"), "codex"), 20).unref();
    assert.equal(await addAccount(remote), "added");
    assert.ok(log.some((line) => line.startsWith("copy ") && line.includes("CODEX_HOME") && line.includes("codex login")));
  });

  assert.equal(sanitizeAccountName("  My Team!! 2 "), "my-team-2");
  assert.equal(await proposeLoginFolder("codex", "", "/home/dev", async (folder) => folder.endsWith("account-2")), path.join("/home/dev", ".codex-account-3"));
});
