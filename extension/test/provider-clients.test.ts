import assert from "node:assert/strict";
import { chmod, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import * as os from "node:os";
import * as path from "node:path";
import test, { mock } from "node:test";
import { UsageProviderError } from "../src/errors";
import {
  ClaudeUsageClient,
  countResetGrants,
  isCloudflareChallenge,
  noLimitsReportedText,
  parseClaudeSnapshot,
  runClaudeAuthRecovery,
  runClaudeAuthStatus,
} from "../src/providers/claude";
import { CodexUsageClient, jwtExpiryMs, parseCodexHttpSnapshot, readCodexHttpCredentials } from "../src/providers/codex";
import { CopilotUsageClient } from "../src/providers/copilot";
import {
  agyHubArguments,
  agyOwnedEnvironment,
  antigravityProcessQuery,
  clearHubStartBackoff,
  combinedGeminiError,
  consumerTierShutdownMessage,
  GeminiUsageClient,
  hubQuotaSummaryTimeoutMs,
  hubRetryIntervalMs,
  ownProcessLines,
  parseAntigravityProcessLines,
  parseCodeAssist,
  parseNetstatListeningPorts,
  parseProcNetListeners,
  procListeningPorts,
  queryDiscoveredHub,
  quotaRequestBody,
  recordHubStartFailure,
  shouldSkipHubStart,
  tryFetchAntigravitySnapshot,
} from "../src/providers/gemini";

const localRequire = createRequire(__filename);
const security = localRequire("../src/security") as typeof import("../src/security");

function restoreEnvironment(name: string, value: string | undefined): void {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}

test("Claude client sends OAuth usage requests and honors throttling hints", async () => {
  const oldToken = process.env.USAGEAI_CLAUDE_OAUTH_TOKEN;
  const oldScopes = process.env.USAGEAI_CLAUDE_OAUTH_SCOPES;
  const oldSession = process.env.USAGEAI_CLAUDE_SESSION_KEY;
  process.env.USAGEAI_CLAUDE_OAUTH_TOKEN = "claude-access-token";
  process.env.USAGEAI_CLAUDE_OAUTH_SCOPES = "user:profile";
  delete process.env.USAGEAI_CLAUDE_SESSION_KEY;
  try {
    let authorization = "";
    mock.method(security, "requestJson", (async (_url, options) => {
      authorization = options.headers?.Authorization ?? "";
      return {
        status: 200,
        headers: {},
        data: { five_hour: { utilization: 37, resets_at: "2026-08-18T00:00:00Z" } },
      };
    }) as typeof security.requestJson);
    const snapshot = await new ClaudeUsageClient().getUsage();
    assert.equal(authorization, "Bearer claude-access-token");
    assert.equal(snapshot.metrics[0]?.usedPercent, 37);
    assert.equal(snapshot.plan, "Claude (OAuth)");

    mock.restoreAll();
    mock.method(security, "requestJson", (async () => ({
      status: 429,
      headers: { "retry-after": "2" },
      data: {},
    })) as typeof security.requestJson);
    let throttled: unknown;
    try {
      await new ClaudeUsageClient().getUsage();
    } catch (error) {
      throttled = error;
    }
    assert.ok(throttled instanceof UsageProviderError);
    assert.equal(throttled.retryAfterMs, 2_000);
    assert.match(throttled.message, /rate-limited/i);
  } finally {
    mock.restoreAll();
    restoreEnvironment("USAGEAI_CLAUDE_OAUTH_TOKEN", oldToken);
    restoreEnvironment("USAGEAI_CLAUDE_OAUTH_SCOPES", oldScopes);
    restoreEnvironment("USAGEAI_CLAUDE_SESSION_KEY", oldSession);
  }
});

test("Claude client validates through the CLI and retries one unexpected usage failure", async () => {
  const environmentNames = [
    "USAGEAI_CLAUDE_OAUTH_TOKEN",
    "USAGEAI_CLAUDE_OAUTH_SCOPES",
    "USAGEAI_CLAUDE_SESSION_KEY",
    "CLAUDE_AI_SESSION_KEY",
    "CLAUDE_WEB_SESSION_KEY",
    "CLAUDE_CONFIG_DIR",
  ] as const;
  const previous = new Map(environmentNames.map((name) => [name, process.env[name]]));
  for (const name of environmentNames) delete process.env[name];

  const credentialJson = JSON.stringify({
    claudeAiOauth: {
      accessToken: "file-access-token",
      expiresAt: Date.now() + 3_600_000,
      scopes: ["user:profile"],
      subscriptionType: "pro",
    },
  });
  try {
    mock.method(security, "readBoundedText", (async () => credentialJson) as typeof security.readBoundedText);
    let requests = 0;
    let probes = 0;
    mock.method(security, "requestJson", (async () => {
      requests += 1;
      return requests === 1
        ? { status: 401, headers: {}, data: {} }
        : {
            status: 200,
            headers: {},
            data: { five_hour: { utilization: 26, resets_at: "2026-09-16T00:00:00Z" } },
          };
    }) as typeof security.requestJson);
    const recovered = await new ClaudeUsageClient(async () => {
      probes += 1;
      return true;
    }).getUsage();
    assert.equal(recovered.metrics[0]?.usedPercent, 26);
    assert.equal(requests, 2);
    assert.equal(probes, 1);

    mock.restoreAll();
    mock.method(security, "readBoundedText", (async () => credentialJson) as typeof security.readBoundedText);
    requests = 0;
    probes = 0;
    mock.method(security, "requestJson", (async () => {
      requests += 1;
      if (requests === 1) throw new SyntaxError("synthetic invalid response");
      return {
        status: 200,
        headers: {},
        data: { seven_day: { utilization: 44, resets_at: "2026-09-22T00:00:00Z" } },
      };
    }) as typeof security.requestJson);
    const retried = await new ClaudeUsageClient(async () => {
      probes += 1;
      return true;
    }).getUsage();
    assert.equal(retried.metrics[0]?.usedPercent, 44);
    assert.equal(requests, 2);
    assert.equal(probes, 1);

    mock.restoreAll();
    mock.method(security, "readBoundedText", (async () => credentialJson) as typeof security.readBoundedText);
    requests = 0;
    mock.method(security, "requestJson", (async () => {
      requests += 1;
      return { status: 403, headers: {}, data: {} };
    }) as typeof security.requestJson);
    await assert.rejects(
      () => new ClaudeUsageClient(async () => false).getUsage(),
      /cannot read account usage/i,
    );
    // A 403 to the reset-credits request is asked once more without the parameter first.
    assert.equal(requests, 2);

    mock.restoreAll();
    mock.method(security, "readBoundedText", (async () => credentialJson) as typeof security.readBoundedText);
    requests = 0;
    probes = 0;
    mock.method(security, "requestJson", (async () => {
      requests += 1;
      return { status: 401, headers: {}, data: {} };
    }) as typeof security.requestJson);
    await assert.rejects(
      () => new ClaudeUsageClient(async () => {
        probes += 1;
        return true;
      }).getUsage(),
      /login has expired/i,
    );
    assert.equal(requests, 2);
    assert.equal(probes, 1);
  } finally {
    mock.restoreAll();
    for (const name of environmentNames) restoreEnvironment(name, previous.get(name));
  }
});

test("Claude reads model-scoped weekly limits, reset credits and plans without limits", async () => {
  const now = Date.parse("2026-11-05T10:00:00Z");
  const usage = {
    five_hour: { utilization: 28.0, resets_at: "2026-11-05T12:29:59.539726+00:00" },
    seven_day: { utilization: 46.0, resets_at: "2026-11-09T12:59:59.539741+00:00" },
    seven_day_opus: { utilization: 99.0 },
    seven_day_sonnet: { utilization: 15.0, resets_at: "2026-11-09T12:59:59Z" },
    limits: [
      { kind: "session", group: "session", percent: 28, scope: null },
      { kind: "weekly_all", group: "weekly", percent: 46, scope: null },
      { kind: "weekly_scoped", group: "weekly", percent: 61, scope: { model: { id: "claude-opus-5", display_name: "Claude Opus 5" } } },
      { kind: "weekly_scoped", group: "weekly", percent: 12, scope: { model: { id: "claude-haiku-5" } } },
      { kind: "weekly_scoped", group: "weekly", percent: "n/a", scope: { model: { display_name: "Broken" } } },
    ],
    cedar_ember: {
      eligible: true,
      grants: [
        { id: "grant-handle-never-read", resets_left: 2, paused: false, starts_at: "2026-11-01T00:00:00Z", ends_at: "2026-12-01T00:00:00Z" },
        { resets_left: 1, paused: true },
        { resets_left: 1 },
        { resets_left: 1, paused: false, starts_at: "2026-11-06T00:00:00Z" },
        { resets_left: 1, paused: false, ends_at: "2026-11-05T09:00:00Z" },
        { resets_left: 1, paused: false },
      ],
    },
  };
  const snapshot = parseClaudeSnapshot(usage, "Pro", now);
  assert.deepEqual(snapshot.metrics.map((metric) => metric.name), [
    "5-hour", "Weekly", "Weekly Opus 5", "Weekly claude-haiku-5", "Weekly Opus", "Weekly Sonnet", "Reset credits",
  ]);
  assert.equal(snapshot.metrics[2]?.usedPercent, 61);
  assert.equal(snapshot.metrics[2]?.kind, "rolling");
  assert.equal(snapshot.metrics[6]?.remainingText, "3");
  assert.equal(countResetGrants({ cedar_ember: { grants: [{ resets_left: 40, paused: false }, { resets_left: 20, paused: false }] } }, now), 0);
  assert.equal(countResetGrants({ cedar_ember: null }, now), 0);

  // A plan Claude meters nothing for reads as such, never as 0% used or as a failure.
  const free = parseClaudeSnapshot({ five_hour: null, seven_day: null, limits: [] }, "Free", now);
  assert.deepEqual(free.metrics.map((metric) => [metric.name, metric.usedPercent, metric.usageText]), [["Usage limits", null, noLimitsReportedText]]);
  assert.throws(() => parseClaudeSnapshot({ unexpected: true }, "Pro", now), /without five-hour or weekly/);

  // Cloudflare's browser check is recognised from its header or its HTML page.
  assert.equal(isCloudflareChallenge(403, { "cf-mitigated": "challenge" }), true);
  assert.equal(isCloudflareChallenge(503, { "content-type": "text/html; charset=UTF-8", server: "cloudflare" }), true);
  assert.equal(isCloudflareChallenge(403, { "content-type": "application/json" }), false);

  const names = ["USAGEAI_CLAUDE_OAUTH_TOKEN", "USAGEAI_CLAUDE_OAUTH_SCOPES", "USAGEAI_CLAUDE_SESSION_KEY", "CLAUDE_AI_SESSION_KEY", "CLAUDE_WEB_SESSION_KEY"];
  const previous = new Map(names.map((name) => [name, process.env[name]]));
  try {
    process.env.USAGEAI_CLAUDE_OAUTH_TOKEN = "claude-access-token";
    process.env.USAGEAI_CLAUDE_OAUTH_SCOPES = "user:profile";
    for (const name of names.slice(2)) delete process.env[name];

    // Reset credits are asked for; an API that rejects the parameter is asked again without it.
    const urls: string[] = [];
    mock.method(security, "requestJson", (async (url) => {
      urls.push(String(url));
      return String(url).includes("cedar_ember")
        ? { status: 400, headers: {}, data: {} }
        : { status: 200, headers: {}, data: { five_hour: { utilization: 7 } } };
    }) as typeof security.requestJson);
    assert.equal((await new ClaudeUsageClient(async () => false).getUsage()).metrics[0]?.usedPercent, 7);
    assert.deepEqual(urls, ["https://api.anthropic.com/api/oauth/usage?cedar_ember=1", "https://api.anthropic.com/api/oauth/usage"]);
    mock.restoreAll();

    // A Cloudflare challenge on the opt-in web path pauses that path and falls back to OAuth, without
    // asking claude.ai again on the next refresh.
    process.env.USAGEAI_CLAUDE_SESSION_KEY = "sk-ant-sid01-synthetic";
    let webRequests = 0;
    mock.method(security, "requestJson", (async (url) => {
      if (String(url).startsWith("https://claude.ai/")) {
        webRequests += 1;
        throw new security.NonJsonResponseError(403, { "cf-mitigated": "challenge", "content-type": "text/html" });
      }
      return { status: 200, headers: {}, data: { five_hour: { utilization: 9 } } };
    }) as typeof security.requestJson);
    const client = new ClaudeUsageClient(async () => false);
    assert.equal((await client.getUsage()).metrics[0]?.usedPercent, 9);
    assert.equal((await client.getUsage()).metrics[0]?.usedPercent, 9);
    assert.equal(webRequests, 1);
  } finally {
    mock.restoreAll();
    for (const name of names) restoreEnvironment(name, previous.get(name));
  }
});

test("Copilot client rejects bad credentials and prioritizes the working token", async () => {
  const oldToken = process.env.COPILOT_GITHUB_TOKEN;
  process.env.COPILOT_GITHUB_TOKEN = "rejected-token";
  const authorizations: string[] = [];
  try {
    mock.method(security, "readBoundedText", (async () => JSON.stringify({
      nested: { oauth_token: "working-token" },
    })) as typeof security.readBoundedText);
    mock.method(security, "requestJson", (async (_url, options) => {
      const authorization = options.headers?.Authorization ?? "";
      authorizations.push(authorization);
      if (authorization === "Bearer rejected-token") {
        return { status: 401, headers: {}, data: {} };
      }
      return {
        status: 200,
        headers: {},
        data: {
          copilot_plan: "individual_pro",
          quota_snapshots: {
            premium_interactions: { entitlement: 300, remaining: 150, percent_remaining: 50 },
          },
        },
      };
    }) as typeof security.requestJson);

    const client = new CopilotUsageClient(() => false);
    const first = await client.getUsage();
    const second = await client.getUsage();
    assert.equal(first.metrics[0]?.usedPercent, 50);
    assert.equal(second.metrics[0]?.usedPercent, 50);
    assert.deepEqual(authorizations, [
      "Bearer rejected-token",
      "Bearer working-token",
      "Bearer working-token",
    ]);
  } finally {
    mock.restoreAll();
    restoreEnvironment("COPILOT_GITHUB_TOKEN", oldToken);
  }
});

test("Codex client completes the app-server handshake through the configured CLI", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "usageai-codex-fixture-"));
  const oldCodexPath = process.env.CODEX_PATH;
  // An empty CODEX_HOME holds no login, so the client goes straight to the app-server fixture.
  const oldCodexHome = process.env.CODEX_HOME;
  process.env.CODEX_HOME = directory;
  const oldPath = process.env.PATH;
  const fixtureSource = `
const readline = require("node:readline");
// UsageAI always starts the server sandboxed; anything else fails the handshake.
if (process.argv.slice(2, 7).join(" ") !== "-s read-only -a never app-server") process.exit(4);
const input = readline.createInterface({ input: process.stdin });
input.on("line", (line) => {
  const message = JSON.parse(line);
  if (message.id === 1) {
    process.stdout.write(JSON.stringify({ id: 1, result: {} }) + "\\n");
  } else if (message.id === 3) {
    // The optional account lookup must never refresh the login.
    if (message.method !== "account/read" || message.params?.refreshToken !== false) process.exit(5);
    process.stdout.write(JSON.stringify({ id: 3, result: { account: { type: "chatgpt", email: "dev@example.com", planType: "plus" } } }) + "\\n");
  } else if (message.id === 2) {
    process.stdout.write(JSON.stringify({
      id: 2,
      result: {
        rateLimitsByLimitId: {
          codex: {
            planType: "plus",
            primary: { usedPercent: 31, windowDurationMins: 300 },
            secondary: { usedPercent: 52, windowDurationMins: 10080 }
          }
        }
      }
    }) + "\\n");
  }
});

`;
  try {
    if (process.platform === "win32") {
      const commandPath = path.join(directory, "codex.cmd");
      const scriptPath = path.join(directory, "node_modules", "@openai", "codex", "bin", "codex.js");
      await mkdir(path.dirname(scriptPath), { recursive: true });
      await writeFile(commandPath, "@echo off\r\n", "utf8");
      await writeFile(scriptPath, fixtureSource, "utf8");
      process.env.CODEX_PATH = commandPath;
      process.env.PATH = `${path.dirname(process.execPath)}${path.delimiter}${oldPath ?? ""}`;
    } else {
      const commandPath = path.join(directory, "codex");
      await writeFile(commandPath, `#!${process.execPath}\n${fixtureSource}`, "utf8");
      await chmod(commandPath, 0o755);
      process.env.CODEX_PATH = commandPath;
    }

    const snapshot = await new CodexUsageClient().getUsage();
    assert.equal(snapshot.plan, "Plus");
    assert.deepEqual(snapshot.metrics.map((metric) => metric.usedPercent), [31, 52]);
    assert.equal(snapshot.accountName, "dev@example.com");
  } finally {
    restoreEnvironment("CODEX_PATH", oldCodexPath);
    restoreEnvironment("CODEX_HOME", oldCodexHome);
    restoreEnvironment("PATH", oldPath);
    await rm(directory, { recursive: true, force: true });
  }
});

test("Codex client translates protocol and authentication failures", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "usageai-codex-errors-"));
  const oldCodexPath = process.env.CODEX_PATH;
  // An empty CODEX_HOME holds no login, so the client goes straight to the app-server fixture.
  const oldCodexHome = process.env.CODEX_HOME;
  process.env.CODEX_HOME = directory;
  const oldPath = process.env.PATH;
  const installFixture = async (fixtureSource: string): Promise<void> => {
    if (process.platform === "win32") {
      const commandPath = path.join(directory, "codex.cmd");
      const scriptPath = path.join(directory, "node_modules", "@openai", "codex", "bin", "codex.js");
      await mkdir(path.dirname(scriptPath), { recursive: true });
      await writeFile(commandPath, "@echo off\r\n", "utf8");
      await writeFile(scriptPath, fixtureSource, "utf8");
      process.env.CODEX_PATH = commandPath;
      process.env.PATH = `${path.dirname(process.execPath)}${path.delimiter}${oldPath ?? ""}`;
    } else {
      const commandPath = path.join(directory, "codex");
      await writeFile(commandPath, `#!${process.execPath}\n${fixtureSource}`, "utf8");
      await chmod(commandPath, 0o755);
      process.env.CODEX_PATH = commandPath;
    }
  };
  const protocolFixture = (secondResponse: string): string => `
const readline = require("node:readline");
const input = readline.createInterface({ input: process.stdin });
input.on("line", (line) => {
  const message = JSON.parse(line);
  if (message.id === 1) process.stdout.write(JSON.stringify({ id: "1", result: {} }) + "\\n");
  if (message.id === 2) process.stdout.write(JSON.stringify(${secondResponse}) + "\\n");
});
`;
  try {
    await installFixture(protocolFixture(`({ id: 2, error: { message: "authentication required" } })`));
    await assert.rejects(() => new CodexUsageClient().getUsage(), /not signed in/i);

    await installFixture(protocolFixture(`({ id: 2, error: { message: "service unavailable" } })`));
    await assert.rejects(() => new CodexUsageClient().getUsage(), /could not read the account rate limits/i);

    await installFixture(protocolFixture(`({ id: 2, result: null })`));
    await assert.rejects(() => new CodexUsageClient().getUsage(), /without usage data/i);

    await installFixture(`process.stderr.write("authentication failed\\n");`);
    await assert.rejects(() => new CodexUsageClient().getUsage(), /not signed in/i);
  } finally {
    restoreEnvironment("CODEX_PATH", oldCodexPath);
    restoreEnvironment("CODEX_HOME", oldCodexHome);
    restoreEnvironment("PATH", oldPath);
    await rm(directory, { recursive: true, force: true });
  }
});

test("Codex reads usage over HTTPS first and falls back to the app-server", async () => {
  const segment = (value: unknown): string => Buffer.from(JSON.stringify(value)).toString("base64url");
  const jwt = (payload: unknown): string => `${segment({ alg: "none" })}.${segment(payload)}.signature`;
  const now = Date.now();
  const live = jwt({ exp: Math.floor(now / 1000) + 3_600 });
  assert.equal(jwtExpiryMs(live), (Math.floor(now / 1000) + 3_600) * 1000);
  assert.equal(jwtExpiryMs("not-a-jwt"), undefined);
  assert.equal(jwtExpiryMs("a.!!!.c"), undefined);

  const home = await mkdtemp(path.join(os.tmpdir(), "usageai-codex-http-"));
  try {
    const writeAuth = (value: unknown): Promise<void> => writeFile(path.join(home, "auth.json"), typeof value === "string" ? value : JSON.stringify(value), "utf8");
    const read = (): Promise<unknown> => readCodexHttpCredentials(home, now);
    // No login file, or an API-key login, leaves usage to the app-server.
    assert.equal(await read(), undefined);
    await writeAuth({ auth_mode: "apikey", OPENAI_API_KEY: "sk-synthetic" });
    assert.equal(await read(), undefined);
    await writeAuth({ auth_mode: "chatgpt", tokens: { access_token: live, account_id: "acct-123_x", refresh_token: "never-read" } });
    assert.deepEqual(await read(), { accessToken: live, accountId: "acct-123_x" });
    // An expired or nearly expired token is the CLI's to renew.
    await writeAuth({ tokens: { access_token: jwt({ exp: Math.floor(now / 1000) + 30 }) } });
    assert.equal(await read(), undefined);
    // A token that is no JWT is used as is; a malformed account id is left out.
    await writeAuth({ tokens: { access_token: "opaque-token", account_id: "bad id\r\n" } });
    assert.deepEqual(await read(), { accessToken: "opaque-token" });
    await writeAuth({ tokens: { access_token: "bad token" } });
    assert.equal(await read(), undefined);
    await writeAuth("not json");
    assert.equal(await read(), undefined);
    // A custom backend is honoured by the app-server only, so the token never goes elsewhere.
    await writeAuth({ tokens: { access_token: live } });
    await writeFile(path.join(home, "config.toml"), '# chatgpt_base_url = "https://example.test"\nmodel = "x"\n', "utf8");
    assert.notEqual(await read(), undefined);
    await writeFile(path.join(home, "config.toml"), '[profile]\n  chatgpt_base_url = "https://example.test"\n', "utf8");
    assert.equal(await read(), undefined);
  } finally {
    await rm(home, { recursive: true, force: true });
  }

  const usage = {
    email: "dev@example.com",
    plan_type: "pro",
    rate_limit: {
      primary_window: { used_percent: 12.6, limit_window_seconds: 18_000, reset_at: 1_793_883_681 },
      secondary_window: { used_percent: 71, limit_window_seconds: 604_800, reset_at: 1_794_140_279 },
    },
    additional_rate_limits: [
      { limit_name: "GPT-5.3-Codex-Spark", rate_limit: { primary_window: { used_percent: 40, limit_window_seconds: 18_000 } } },
      { limit_name: "No windows", rate_limit: {} },
      "noise",
    ],
    credits: { has_credits: true, unlimited: false, balance: "4.50" },
    rate_limit_reset_credits: { available_count: 2, applicable_available_count: 0 },
  };
  const parsed = parseCodexHttpSnapshot(usage);
  assert.equal(parsed.plan, "Pro");
  assert.equal(parsed.accountName, "dev@example.com");
  assert.deepEqual(parsed.metrics.map((metric) => metric.name), ["Codex (5-hour)", "Codex (Weekly)", "GPT-5.3-Codex-Spark (5-hour)", "Credits", "Reset credits"]);
  assert.equal(parsed.metrics[0]?.usedPercent, 13);
  assert.equal(parsed.metrics[0]?.durationMinutes, 300);
  assert.equal(parsed.metrics[1]?.durationMinutes, 10_080);
  assert.equal(parsed.metrics[1]?.resetsAt, new Date(1_794_140_279_000).toISOString());
  assert.throws(() => parseCodexHttpSnapshot({ rate_limit: { primary_window: null } }), /no rate-limit windows/);

  // The app-server fallback is pointed at a missing CLI, so reaching it shows as "not found".
  const oldCodexPath = process.env.CODEX_PATH;
  process.env.CODEX_PATH = path.join(os.tmpdir(), `usageai-missing-codex-${Date.now()}.cmd`);
  const client = new CodexUsageClient(undefined, { loadCredentials: async () => ({ accessToken: "access-token", accountId: "acct-1" }) });
  try {
    let requests = 0;
    const respond = (response: { status: number; headers?: Record<string, string>; data?: unknown } | Error): void => {
      mock.restoreAll();
      mock.method(security, "requestJson", (async (url, options) => {
        requests += 1;
        assert.equal(String(url), "https://chatgpt.com/backend-api/wham/usage");
        assert.deepEqual(options.allowedHosts, ["chatgpt.com"]);
        assert.equal(options.headers?.Authorization, "Bearer access-token");
        assert.equal(options.headers?.["ChatGPT-Account-Id"], "acct-1");
        if (response instanceof Error) throw response;
        return { status: response.status, headers: response.headers ?? {}, data: response.data };
      }) as typeof security.requestJson);
    };

    // A live ChatGPT login is read over HTTPS, and the app-server is never started.
    respond({ status: 200, data: usage });
    assert.equal((await client.getUsage()).metrics[1]?.usedPercent, 71);

    // A rejected token, an outage, or an unexpected answer falls back to the app-server.
    for (const response of [{ status: 401 }, { status: 500 }, { status: 200, data: { unexpected: true } }, new Error("synthetic network failure")]) {
      respond(response);
      await assert.rejects(client.getUsage(), /not found|CODEX_PATH/i);
    }

    // A rate limit is reported with its retry window rather than retried through the app-server.
    respond({ status: 429, headers: { "retry-after": "120" } });
    await assert.rejects(client.getUsage(), (error: unknown) =>
      error instanceof UsageProviderError && error.retryAfterMs === 120_000 && /rate-limited/.test(error.message));
    assert.equal(requests, 6);

    // Without usable credentials HTTP is never attempted.
    const withoutLogin = new CodexUsageClient(undefined, { loadCredentials: async () => undefined });
    await assert.rejects(withoutLogin.getUsage(), /not found|CODEX_PATH/i);
    assert.equal(requests, 6);
  } finally {
    mock.restoreAll();
    restoreEnvironment("CODEX_PATH", oldCodexPath);
  }
});

test("Gemini client follows local fallback order before cloud quota", async () => {
  const localSnapshot = {
    plan: "Antigravity",
    metrics: [{ name: "Gemini Models", kind: "rolling" as const, usedPercent: 22 }],
    fetchedAt: new Date().toISOString(),
    providerId: "gemini",
    providerName: "Google Gemini",
  };
  let agyCalls = 0;
  let credentialCalls = 0;
  const localClient = new GeminiUsageClient({
    fetchAntigravity: async () => localSnapshot,
    fetchAgy: async () => { agyCalls += 1; return undefined; },
    loadCredentials: async () => {
      credentialCalls += 1;
      return { accessToken: "unused", sourcePath: "fixture" };
    },
  });
  assert.equal(await localClient.getUsage(), localSnapshot);
  assert.equal(agyCalls, 0);
  assert.equal(credentialCalls, 0);

  const requestedUrls: string[] = [];
  try {
    mock.method(security, "requestJson", (async (url, options) => {
      requestedUrls.push(String(url));
      assert.equal(options.headers?.Authorization, "Bearer gemini-access-token");
      if (String(url).includes("loadCodeAssist")) {
        return { status: 200, headers: {}, data: { paidTier: { name: "Google AI Pro" } } };
      }
      return {
        status: 200,
        headers: {},
        data: { buckets: [{ modelId: "gemini-pro", remainingFraction: 0.62 }] },
      };
    }) as typeof security.requestJson);
    const cloudClient = new GeminiUsageClient({
      fetchAntigravity: async () => undefined,
      fetchAgy: async () => undefined,
      loadCredentials: async () => ({
        accessToken: "gemini-access-token",
        expiresAt: Date.now() + 3_600_000,
        sourcePath: "fixture",
      }),
    });
    const cloud = await cloudClient.getUsage();
    assert.equal(cloud.plan, "Google AI Pro");
    assert.equal(cloud.metrics[0]?.usedPercent, 38);
    assert.deepEqual(requestedUrls, [
      "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
      "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota",
    ]);
  } finally {
    mock.restoreAll();
  }
});

test("Gemini refreshes expired OAuth once and reuses the in-memory token", async () => {
  const requestedUrls: string[] = [];
  let refreshBodies = 0;
  try {
    mock.method(security, "requestJson", (async (url, options) => {
      const address = String(url);
      requestedUrls.push(address);
      if (address.includes("oauth2.googleapis.com")) {
        refreshBodies += 1;
        assert.match(options.body ?? "", /grant_type=refresh_token/);
        assert.match(options.body ?? "", /refresh_token=fixture-refresh-token/);
        return {
          status: 200,
          headers: {},
          data: { access_token: "refreshed-access-token", expires_in: 3_600 },
        };
      }
      assert.equal(options.headers?.Authorization, "Bearer refreshed-access-token");
      if (address.includes("loadCodeAssist")) {
        return { status: 200, headers: {}, data: { currentTier: { id: "standard-tier" } } };
      }
      return {
        status: 200,
        headers: {},
        data: { buckets: [{ modelId: "gemini-pro", remainingFraction: 0.4 }] },
      };
    }) as typeof security.requestJson);
    const client = new GeminiUsageClient({
      fetchAntigravity: async () => undefined,
      fetchAgy: async () => undefined,
      loadCredentials: async () => ({
        accessToken: "expired-access-token",
        refreshToken: "fixture-refresh-token",
        expiresAt: Date.now() - 60_000,
        sourcePath: "fixture",
      }),
    });

    const first = await client.getUsage();
    const second = await client.getUsage();
    assert.equal(first.plan, "Paid");
    assert.equal(second.metrics[0]?.usedPercent, 60);
    assert.equal(refreshBodies, 1);
    assert.equal(requestedUrls.filter((url) => url.includes("retrieveUserQuota")).length, 2);
  } finally {
    mock.restoreAll();
  }
});

test("Gemini reports Google's consumer-tier shutdown and sends the Code Assist project", async () => {
  const shutDown = {
    currentTier: { id: "free-tier" },
    ineligibleTiers: [{ reasonCode: "UNSUPPORTED_CLIENT", reasonMessage: "Use Antigravity" }],
    cloudaicompanionProject: "proj-9",
  };
  assert.deepEqual(parseCodeAssist(shutDown, undefined), { plan: "Free", project: "proj-9", consumerTierShutDown: true });
  // Accounts that keep Gemini CLI access are never told it was shut down.
  assert.equal(parseCodeAssist({ ...shutDown, paidTier: { name: "Gemini Code Assist Pro" } }, undefined).consumerTierShutDown, false);
  assert.equal(parseCodeAssist({ ...shutDown, currentTier: { id: "standard-tier" } }, undefined).consumerTierShutDown, false);
  assert.equal(parseCodeAssist(shutDown, "school.example").consumerTierShutDown, false);
  assert.equal(parseCodeAssist({ ineligibleTiers: [{ reasonCode: "RESTRICTED_AGE" }] }, undefined).consumerTierShutDown, false);
  // The project is a plain id or an object carrying one, and anything else is dropped.
  assert.equal(parseCodeAssist({ cloudaicompanionProject: { id: "proj-object" } }, undefined).project, "proj-object");
  assert.equal(parseCodeAssist({ cloudaicompanionProject: 'bad project"id' }, undefined).project, undefined);
  assert.deepEqual(parseCodeAssist([], undefined), { consumerTierShutDown: false });
  assert.equal(quotaRequestBody(undefined), "{}");
  assert.equal(quotaRequestBody("proj-9"), '{"project":"proj-9"}');

  const quotaBodies: string[] = [];
  const clientFor = (codeAssist: unknown): GeminiUsageClient => {
    mock.method(security, "requestJson", (async (url, options) => {
      if (String(url).includes("loadCodeAssist")) {
        return { status: 200, headers: {}, data: codeAssist };
      }
      quotaBodies.push(options.body ?? "");
      return { status: 403, headers: {}, data: { error: { status: "PERMISSION_DENIED" } } };
    }) as typeof security.requestJson);
    return new GeminiUsageClient({
      fetchAntigravity: async () => undefined,
      fetchAgy: async () => undefined,
      hasAgyCli: async () => true,
      loadCredentials: async () => ({ accessToken: "gemini-access-token", expiresAt: Date.now() + 3_600_000, sourcePath: "fixture" }),
    });
  };
  try {
    // The shutdown reads as such, never as an expired sign-in, and the project went along.
    await assert.rejects(clientFor(shutDown).getUsage(), (error: unknown) => {
      assert.equal(
        (error as Error).message,
        "Google Gemini usage is unavailable. Check that `agy` is signed in, then refresh. Antigravity (IDE or editor extension) and `agy` returned no usage, and Google no longer serves Gemini CLI quota for this plan.",
      );
      return true;
    });
    assert.equal(quotaBodies.at(-1), '{"project":"proj-9"}');
    mock.restoreAll();

    // The same 403 for an account Google did not flag stays a sign-in problem.
    await assert.rejects(clientFor({ currentTier: { id: "free-tier" } }).getUsage(), /the Gemini CLI sign-in has expired\.$/);
    assert.equal(quotaBodies.at(-1), "{}");
  } finally {
    mock.restoreAll();
  }
  assert.match(combinedGeminiError(false, consumerTierShutdownMessage), /^Google Gemini usage is unavailable\. Sign in to Antigravity or install `agy`/);
});

test("Gemini retries agy when a cached cold-start failure and CLI fallback are both stale", async () => {
  const recovered = {
    plan: "Antigravity",
    metrics: [{ name: "Gemini Models", kind: "session" as const, usedPercent: 23 }],
    fetchedAt: new Date().toISOString(),
    providerId: "gemini",
    providerName: "Google Gemini",
  };
  let agyCalls = 0;
  const client = new GeminiUsageClient({
    fetchAntigravity: async () => undefined,
    fetchAgy: async () => ++agyCalls === 1 ? recovered : undefined,
    loadCredentials: async () => { throw new Error("No Gemini CLI credentials."); },
  });
  (client as unknown as { agyRetryAfter: number }).agyRetryAfter = Date.now() + 20 * 60_000;

  assert.equal(await client.getUsage(), recovered);
  assert.equal(agyCalls, 1);

  const disconnected = new GeminiUsageClient({
    fetchAntigravity: async () => undefined,
    fetchAgy: async () => undefined,
    loadCredentials: async () => { throw new Error("No Gemini CLI credentials."); },
  });
  await assert.rejects(
    disconnected.getUsage(),
    (error: unknown) => error instanceof Error && /`agy`/.test(error.message) && !/`gemini`/.test(error.message),
  );
});

test("Gemini names every failed path once the CLI fallback fails too", async () => {
  // The actionable sentence comes first, because the desktop card shows only that much. It blames
  // no sign-in: agy may be signed in but slow, and the IDE may be installed without agy.
  assert.equal(
    combinedGeminiError(true, "Google Gemini is not signed in. Run `agy` to sign in, then refresh."),
    "Google Gemini usage is unavailable. Check that `agy` is signed in, then refresh. Antigravity (IDE or editor extension) and `agy` returned no usage, and the Gemini CLI is not signed in.",
  );
  assert.equal(
    combinedGeminiError(false, "Google Gemini sign-in has expired. Run `agy` to sign in again, then refresh."),
    "Google Gemini usage is unavailable. Sign in to Antigravity or install `agy`, then refresh. Antigravity (IDE or editor extension) returned no usage and `agy` is not installed, and the Gemini CLI sign-in has expired.",
  );
  // A failure that is not about sign-in keeps its own message and gets no sign-in hint.
  assert.equal(
    combinedGeminiError(true, "Google Cloud API rate-limited the quota request."),
    "Google Cloud API rate-limited the quota request. Antigravity (IDE or editor extension) and `agy` returned no usage before the Gemini CLI fallback.",
  );
  assert.match(combinedGeminiError(true, "Google Gemini API returned HTTP 500"), /HTTP 500\. Antigravity \(IDE or editor extension\)/);
  assert.doesNotMatch(combinedGeminiError(true, "Gemini usage request timed out."), /sign in|not found/i);

  const throttled = new UsageProviderError("Google Cloud API rate-limited the quota request.", 90_000);
  const client = new GeminiUsageClient({
    fetchAntigravity: async () => undefined,
    fetchAgy: async () => undefined,
    loadCredentials: async () => ({ accessToken: "token", sourcePath: "fixture" }),
    hasAgyCli: async () => false,
  });
  (client as unknown as { getGeminiCliUsage: () => Promise<never> }).getGeminiCliUsage = async () => { throw throttled; };
  await assert.rejects(client.getUsage(), (error: unknown) => error instanceof UsageProviderError
    && error.retryAfterMs === 90_000
    && error.cause === throttled
    && /^Google Cloud API rate-limited the quota request. .*`agy` is not installed/.test(error.message));

  const unexpected = new TypeError("internal detail");
  const crashing = new GeminiUsageClient({
    fetchAntigravity: async () => undefined,
    fetchAgy: async () => undefined,
    loadCredentials: async () => ({ accessToken: "token", sourcePath: "fixture" }),
  });
  (crashing as unknown as { getGeminiCliUsage: () => Promise<never> }).getGeminiCliUsage = async () => { throw unexpected; };
  await assert.rejects(crashing.getUsage(), (error: unknown) => error === unexpected);

  // A cancelled refresh keeps the CLI's own error and never looks for agy.
  let agyLookups = 0;
  const wrapped = new UsageProviderError("Could not read Gemini usage because the provider returned invalid or unavailable data.");
  const cancelled = new GeminiUsageClient({
    fetchAntigravity: async () => undefined,
    fetchAgy: async () => undefined,
    loadCredentials: async () => ({ accessToken: "token", sourcePath: "fixture" }),
    hasAgyCli: async () => { agyLookups++; return true; },
  });
  (cancelled as unknown as { getGeminiCliUsage: () => Promise<never> }).getGeminiCliUsage = async () => { throw wrapped; };
  const controller = new AbortController();
  controller.abort();
  await assert.rejects(cancelled.getUsage(controller.signal), (error: unknown) => error === wrapped);
  assert.equal(agyLookups, 0);
});

test("netstat listening ports stay bound to the owning process", () => {
  const stdout = [
    "",
    "Active Connections",
    "",
    "  Proto  Local Address          Foreign Address        State           PID",
    "  TCP    127.0.0.1:5001         0.0.0.0:0              LISTENING       42",
    "  TCP    [::1]:5003             [::]:0                 LISTENING       42",
    "  TCP    127.0.0.1:5001         0.0.0.0:0              LISTENING       42",
    "  TCP    127.0.0.1:5002         0.0.0.0:0              LISTENING       99",
    "  TCP    127.0.0.1:5010         127.0.0.1:5011         ESTABLISHED     42",
    "  UDP    127.0.0.1:5004         *:*                                    42",
    "  TCP    127.0.0.1:0            0.0.0.0:0              LISTENING       42",
    "garbage",
  ].join("\r\n");

  // Only this process's listeners, de-duplicated, IPv6 included, and never a port that is
  // merely connected or owned by someone else.
  assert.deepEqual(parseNetstatListeningPorts(stdout, 42), [5001, 5003]);
  assert.deepEqual(parseNetstatListeningPorts(stdout, 99), [5002]);
  assert.deepEqual(parseNetstatListeningPorts(stdout, 7), []);
  assert.deepEqual(parseNetstatListeningPorts("", 42), []);

  // netstat localizes the state word, so discovery must not depend on reading "LISTENING".
  const german = [
    "  Proto  Lokale Adresse         Remoteadresse          Status          PID",
    "  TCP    127.0.0.1:5005         0.0.0.0:0              ABHֳ–REN         42",
    "  TCP    127.0.0.1:5006         127.0.0.1:5007         HERGESTELLT     42",
  ].join("\r\n");
  assert.deepEqual(parseNetstatListeningPorts(german, 42), [5005]);
});

test("Antigravity discovery binds tokens to revalidated process-owned ports", async () => {
  const processLines = [
    "not relevant",
    "42 language_server_windows.exe --csrf_token=primary --extension_server_csrf_token secondary --extension_server_port 5002",
    "0 language_server --csrf_token invalid",
    "43 language_server --extension_server_port=70000 --csrf_token=other",
  ].join("\n");
  assert.deepEqual(parseAntigravityProcessLines(processLines), [
    { pid: 42, tokens: ["secondary", "primary"], hintedPort: 5002 },
    // An out-of-range hint is dropped rather than kept for a port no process can own.
    { pid: 43, tokens: ["other"] },
  ]);

  const expected = {
    plan: "Antigravity",
    metrics: [{ name: "Gemini Models", kind: "rolling" as const, usedPercent: 20 }],
    fetchedAt: new Date().toISOString(),
    providerId: "gemini",
    providerName: "Google Gemini",
  };
  const portChecks: number[] = [];
  const queries: Array<[number, string]> = [];
  const result = await tryFetchAntigravitySnapshot(undefined, {
    detectProcesses: async () => [{ pid: 42, tokens: ["bad", "good"], hintedPort: 5002 }],
    listeningPorts: async (pid) => {
      assert.equal(pid, 42);
      portChecks.push(pid);
      return portChecks.length === 1 ? [5001, 5002] : portChecks.length === 2 ? [5001] : [5001];
    },
    query: async (port, token) => {
      queries.push([port, token]);
      return port === 5001 && token === "good" ? expected : undefined;
    },
  });
  assert.equal(result, expected);
  assert.deepEqual(queries, [[5001, "bad"], [5001, "good"]]);

  const unavailable = await tryFetchAntigravitySnapshot(undefined, {
    detectProcesses: async () => { throw new Error("process listing unavailable"); },
    listeningPorts: async () => { throw new Error("must not run"); },
    query: async () => { throw new Error("must not run"); },
  });
  assert.equal(unavailable, undefined);
});

test("Antigravity discovery finds editor-extension hubs and only the current user's processes", () => {
  const lines = [
    String.raw`7 C:\Users\me\.gemini\bin\agy.exe --hub --hub-port=49193 --app_data_dir=antigravity --csrf_token=hub-token --add-dir=d:\work`,
    "8 /home/me/.gemini/bin/agy --hub --hub-port 50001 --csrf_token second",
    String.raw`9 "C:\Program Files\agy\agy.exe" --hub --hub-port=50002 --csrf_token=third`,
    "10 agy --hub --csrf_token=no-port",
    "11 agy --hub --hub-port=70000 --csrf_token=bad-port",
    "12 agy -p /usage --csrf_token=one-off",
    "13 agy --hub-port=50003 --csrf_token=no-hub-flag",
    "14 language_server_linux_x64 --csrf_token=ide --extension_server_port=4100",
  ].join("\n");
  assert.deepEqual(parseAntigravityProcessLines(lines), [
    // Only the editor extension passes --app_data_dir; UsageAI's own hubs never do.
    { pid: 7, tokens: ["hub-token"], hubPort: 49193, editorHub: true },
    { pid: 8, tokens: ["second"], hubPort: 50001, editorHub: false },
    { pid: 9, tokens: ["third"], hubPort: 50002, editorHub: false },
    { pid: 14, tokens: ["ide"], hintedPort: 4100 },
  ]);

  // ps -o uid=,pid=,command=: another user's look-alike never becomes a candidate.
  assert.equal(
    ownProcessLines(" 1000  42 agy --hub --hub-port=1 --csrf_token=a\n0 43 agy --hub\n1000 44 language_server --csrf_token=b\nnoise", 1000),
    "42 agy --hub --hub-port=1 --csrf_token=a\n44 language_server --csrf_token=b",
  );

  // One provider-side CIM filter covers both kinds; Where-Object listed every process first.
  assert.match(antigravityProcessQuery, /-Filter "Name LIKE '%language_server%' OR Name = 'agy\.exe'"/);
  assert.doesNotMatch(antigravityProcessQuery, /Where-Object/);
});

test("Antigravity probe asks IDE language servers before editor hubs and never its own hub", async () => {
  const snapshot = {
    plan: "Google AI Pro",
    metrics: [{ name: "Gemini Models", kind: "session" as const, usedPercent: 4 }],
    fetchedAt: new Date().toISOString(),
    providerId: "gemini",
    providerName: "Google Gemini",
  };
  const asked: string[] = [];
  const memory: { last?: { pid: number; port: number; token: string; hub: boolean } | undefined } = {};
  const result = await tryFetchAntigravitySnapshot(undefined, {
    detectProcesses: async () => [
      { pid: 6, tokens: ["other-usageai"], hubPort: 902, editorHub: false },
      { pid: 7, tokens: ["hub"], hubPort: 900, editorHub: true },
      { pid: 8, tokens: ["own"], hubPort: 901, editorHub: false },
      { pid: 9, tokens: ["ide"] },
    ],
    listeningPorts: async (pid) => ({ 6: [902], 7: [900, 950], 8: [901], 9: [1200] } as Record<number, number[]>)[pid] ?? [],
    query: async (port, token) => { asked.push(`ide ${port} ${token}`); return undefined; },
    queryHub: async (port, token) => { asked.push(`hub ${port} ${token}`); return port === 900 ? snapshot : undefined; },
    ownHubPid: () => 8,
    memory,
  });
  assert.equal(result, snapshot);
  // The language server goes first, then the editor's hub, asked only on its --hub-port. Another
  // UsageAI instance's hub would come last, and this instance's own hub (already tried by the
  // serving-hub short cut) is never a candidate.
  assert.deepEqual(asked, ["ide 1200 ide", "hub 900 hub"]);
  assert.deepEqual(memory.last, { pid: 7, port: 900, token: "hub", hub: true });

  // Without a hub client, hub candidates are skipped rather than sent to the language-server query.
  const skipped: string[] = [];
  assert.equal(await tryFetchAntigravitySnapshot(undefined, {
    detectProcesses: async () => [{ pid: 7, tokens: ["hub"], hubPort: 900 }],
    listeningPorts: async () => [900],
    query: async (port) => { skipped.push(String(port)); return snapshot; },
  }), undefined);
  assert.deepEqual(skipped, []);
});

test("the remembered Antigravity candidate skips the listing and is dropped once it stops serving", async () => {
  const snapshot = {
    plan: "Google AI Pro",
    metrics: [{ name: "Gemini Models", kind: "session" as const, usedPercent: 4 }],
    fetchedAt: new Date().toISOString(),
    providerId: "gemini",
    providerName: "Google Gemini",
  };
  const memory: { last?: { pid: number; port: number; token: string; hub: boolean } | undefined } = {
    last: { pid: 7, port: 900, token: "hub", hub: true },
  };
  let listings = 0;
  let owned = [900];
  const sent: string[] = [];
  const dependencies = {
    detectProcesses: async () => { listings += 1; return []; },
    listeningPorts: async () => owned,
    query: async () => undefined,
    queryHub: async (port: number, token: string) => { sent.push(`${port} ${token}`); return snapshot; },
    memory,
  };

  assert.equal(await tryFetchAntigravitySnapshot(undefined, dependencies), snapshot);
  assert.equal(listings, 0);

  // The editor closed: its port is gone, so the token is not sent again and discovery runs instead.
  owned = [];
  sent.length = 0;
  assert.equal(await tryFetchAntigravitySnapshot(undefined, dependencies), undefined);
  assert.deepEqual(sent, []);
  assert.equal(listings, 1);
  assert.equal(memory.last, undefined);
});

test("an editor hub needs GetUserStatus, and gets longer for the quota summary", async () => {
  const status = {
    userStatus: {
      email: "dev@example.com",
      cascadeModelConfigData: { clientModelConfigs: [{ label: "Gemini Pro", quotaInfo: { remainingFraction: 0.8 } }] },
    },
  };
  const summary = {
    response: { groups: [{ displayName: "Gemini Models", buckets: [{ bucketId: "gemini_5h", remainingFraction: 0.7 }] }] },
  };
  const calls: Array<[string, number | undefined, string | undefined]> = [];
  let summaryAnswers = true;
  try {
    mock.method(security, "requestJson", (async (url, options) => {
      const method = String(url).split("/").at(-1) ?? "";
      calls.push([method, options?.timeoutMs, options?.body as string | undefined]);
      assert.ok(String(url).startsWith("http://127.0.0.1:900/"));
      if (method === "GetUserStatus") return { status: 200, headers: {}, data: status };
      if (summaryAnswers) return { status: 200, headers: {}, data: summary };
      throw new Error("timed out");
    }) as typeof security.requestJson);

    const full = await queryDiscoveredHub(900, "hub-token");
    assert.ok(full);
    assert.ok(full.metrics.some((metric) => metric.name === "Gemini Models (5-hour)"));
    assert.deepEqual(calls, [["GetUserStatus", 5_000, "{}"], ["RetrieveUserQuotaSummary", hubQuotaSummaryTimeoutMs, "{}"]]);

    // A cold hub that cannot produce the summary in time still yields the status reading.
    summaryAnswers = false;
    const statusOnly = await queryDiscoveredHub(900, "hub-token");
    assert.ok(statusOnly);
    assert.deepEqual(statusOnly.metrics.map((metric) => metric.name), ["Gemini Models"]);

    mock.restoreAll();
    mock.method(security, "requestJson", (async () => ({ status: 401, headers: {}, data: {} })) as typeof security.requestJson);
    assert.equal(await queryDiscoveredHub(900, "wrong-token"), undefined);
  } finally {
    mock.restoreAll();
  }
});

test("Linux /proc reads join socket inodes to the pid and fall back only when the tables are unreadable", async () => {
  const tcp = [
    "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode",
    "   0: 0100007F:C031 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 41001 1",
    "   1: 0100007F:C032 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 41003 1",
  ].join("\n");
  const links: Record<string, string> = {
    "/proc/7/fd/0": "/dev/null",
    "/proc/7/fd/3": "socket:[41001]",
    "/proc/7/fd/4": "socket:[99999]",
    "/proc/7/fd/5": "pipe:[123]",
  };
  const proc = {
    readFile: async (file: string) => {
      if (file === "/proc/net/tcp") return tcp;
      throw Object.assign(new Error("no IPv6"), { code: "ENOENT" });
    },
    readdir: async (directory: string) => {
      if (directory === "/proc/7/fd") return ["0", "3", "4", "5", "6"];
      throw Object.assign(new Error("not ours"), { code: "EACCES" });
    },
    readlink: async (link: string) => {
      const target = links[link];
      if (target === undefined) throw Object.assign(new Error("closed"), { code: "ENOENT" });
      return target;
    },
  };
  // Only fd 3 is a listening socket; 41003 belongs to some other process, fd 6 closed mid-read.
  assert.deepEqual(await procListeningPorts(7, proc), [49201]);
  // Another user's pid owns nothing; lsof could not see more.
  assert.deepEqual(await procListeningPorts(8, proc), []);
  // With neither socket table readable the caller must try lsof instead.
  await assert.rejects(procListeningPorts(7, { ...proc, readFile: async () => { throw new Error("masked"); } }), /unreadable/);
});

test("Linux listening ports are read from /proc without lsof", () => {
  const table = [
    "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode",
    "   0: 0100007F:C031 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 41001 1 0000000000000000 100 0 0 10 0",
    "   1: 0100007F:1F90 0100007F:D2F0 01 00000000:00000000 00:00000000 00000000  1000        0 41002 1 0000000000000000 20 4 30 10 -1",
    "   2: 00000000:0016 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 0 1",
    "   3: short row",
  ].join("\n");
  // 0xC031 = 49201 is LISTEN; the 01 row is an established connection; inode 0 is unusable.
  assert.deepEqual([...parseProcNetListeners(table)], [["41001", 49201]]);
});

test("Gemini prefers a running agy hub over the Antigravity process probe", async () => {
  const hubbed = {
    plan: "Google AI Pro",
    metrics: [{ name: "Gemini Models (Weekly)", kind: "rolling" as const, usedPercent: 1 }],
    fetchedAt: new Date().toISOString(),
    providerId: "gemini",
    providerName: "Google Gemini",
  };
  let antigravityCalls = 0;
  const withHub = new GeminiUsageClient({
    fetchAntigravity: async () => { antigravityCalls += 1; return undefined; },
    fetchAgy: async () => hubbed,
    hasAgyHub: () => true,
    loadCredentials: async () => { throw new Error("No Gemini CLI credentials."); },
  });

  assert.equal(await withHub.getUsage(), hubbed);
  // The probe costs a PowerShell child process, so a live hub must skip it entirely.
  assert.equal(antigravityCalls, 0);

  const withoutHub = new GeminiUsageClient({
    fetchAntigravity: async () => { antigravityCalls += 1; return undefined; },
    fetchAgy: async () => hubbed,
    hasAgyHub: () => false,
    loadCredentials: async () => { throw new Error("No Gemini CLI credentials."); },
  });
  assert.equal(await withoutHub.getUsage(), hubbed);
  assert.equal(antigravityCalls, 1);
});

test("Claude environment overrides surface bounded provider failures without recovery", async () => {
  const names = [
    "USAGEAI_CLAUDE_OAUTH_TOKEN",
    "USAGEAI_CLAUDE_OAUTH_SCOPES",
    "USAGEAI_CLAUDE_SESSION_KEY",
  ] as const;
  const previous = new Map(names.map((name) => [name, process.env[name]]));
  process.env.USAGEAI_CLAUDE_OAUTH_TOKEN = "environment-token";
  process.env.USAGEAI_CLAUDE_OAUTH_SCOPES = "user:profile";
  delete process.env.USAGEAI_CLAUDE_SESSION_KEY;
  let probes = 0;
  const client = () => new ClaudeUsageClient(async () => {
    probes += 1;
    return true;
  });
  try {
    for (const [status, pattern] of [
      [401, /login has expired/i],
      [403, /cannot read account usage/i],
      [500, /HTTP 500/i],
    ] as const) {
      mock.restoreAll();
      mock.method(security, "requestJson", (async () => ({ status, headers: {}, data: {} })) as typeof security.requestJson);
      await assert.rejects(() => client().getUsage(), pattern);
    }

    mock.restoreAll();
    mock.method(security, "requestJson", (async () => { throw new SyntaxError("invalid JSON"); }) as typeof security.requestJson);
    await assert.rejects(() => client().getUsage(), /invalid or unavailable data/i);
    assert.equal(probes, 0);

    process.env.USAGEAI_CLAUDE_OAUTH_SCOPES = "org:create_api_key";
    await assert.rejects(() => client().getUsage(), /missing the user:profile scope/i);
  } finally {
    mock.restoreAll();
    for (const name of names) restoreEnvironment(name, previous.get(name));
  }
});

test("Claude auth-status probe accepts valid CLI output and contains failures", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "usageai-claude-auth-"));
  const oldClaudePath = process.env.CLAUDE_PATH;
  const oldPath = process.env.PATH;
  let scriptPath: string;
  try {
    if (process.platform === "win32") {
      const commandPath = path.join(directory, "claude.cmd");
      scriptPath = path.join(directory, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
      await mkdir(path.dirname(scriptPath), { recursive: true });
      await writeFile(commandPath, "@echo off\r\n", "utf8");
      process.env.CLAUDE_PATH = commandPath;
      process.env.PATH = `${path.dirname(process.execPath)}${path.delimiter}${oldPath ?? ""}`;
    } else {
      scriptPath = path.join(directory, "claude");
      process.env.CLAUDE_PATH = scriptPath;
    }

    const writeFixture = async (source: string): Promise<void> => {
      await writeFile(
        scriptPath,
        process.platform === "win32" ? source : `#!${process.execPath}\n${source}`,
        "utf8",
      );
      if (process.platform !== "win32") await chmod(scriptPath, 0o755);
    };

    await writeFixture(`process.stdout.write(JSON.stringify({ loggedIn: true }));`);
    assert.equal(await runClaudeAuthStatus(), true);

    await writeFixture(`process.stdout.write("not-json");`);
    assert.equal(await runClaudeAuthStatus(), false);

    await writeFixture(`process.exitCode = 2;`);
    assert.equal(await runClaudeAuthStatus(), false);

    await writeFixture(`process.stdout.write(JSON.stringify({ loggedIn: true }));`);
    const controller = new AbortController();
    const reason = new Error("cancelled fixture");
    controller.abort(reason);
    await assert.rejects(() => runClaudeAuthStatus(controller.signal), reason);
  } finally {
    restoreEnvironment("CLAUDE_PATH", oldClaudePath);
    restoreEnvironment("PATH", oldPath);
    await rm(directory, { recursive: true, force: true });
  }
});

test("Claude recovery makes the CLI refresh an expired login that auth status only reports", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "usageai-claude-refresh-"));
  const configDirectory = path.join(directory, "config");
  const credentialPath = path.join(configDirectory, ".credentials.json");
  const mcpMarker = path.join(directory, "mcp-ran");
  const names = ["CLAUDE_PATH", "PATH", "CLAUDE_CONFIG_DIR"];
  const previous = new Map(names.map((name) => [name, process.env[name]]));
  const credentials = (accessToken: string, expiresAt: number): string => JSON.stringify({
    claudeAiOauth: { accessToken, expiresAt, scopes: ["user:profile"], subscriptionType: "pro" },
  });
  try {
    await mkdir(configDirectory, { recursive: true });
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
    process.env.CLAUDE_CONFIG_DIR = configDirectory;
    // Mirrors the real CLI: `auth status` never refreshes, `mcp list` needs a live token and does.
    const source = `
      const fs = require("node:fs");
      const args = process.argv.slice(2);
      if (args[0] === "auth") {
        process.stdout.write(JSON.stringify({ loggedIn: true }));
      } else if (args[0] === "mcp") {
        fs.writeFileSync(${JSON.stringify(mcpMarker)}, "1");
        fs.writeFileSync(${JSON.stringify(credentialPath)}, ${JSON.stringify(
          credentials("cli-refreshed-access", Date.now() + 8 * 3_600_000),
        )});
      }
    `;
    await writeFile(scriptPath, process.platform === "win32" ? source : `#!${process.execPath}\n${source}`, "utf8");
    if (process.platform !== "win32") await chmod(scriptPath, 0o755);

    const fresh = credentials("still-fresh-access", Date.now() + 3_600_000);
    await writeFile(credentialPath, fresh, "utf8");
    assert.equal(await runClaudeAuthRecovery(), true);
    assert.equal(await readFile(credentialPath, "utf8"), fresh);
    await assert.rejects(() => readFile(mcpMarker, "utf8"));

    await writeFile(credentialPath, credentials("expired-access", Date.now() - 60_000), "utf8");
    assert.equal(await runClaudeAuthRecovery(), true);
    assert.match(await readFile(credentialPath, "utf8"), /cli-refreshed-access/);
  } finally {
    for (const name of names) restoreEnvironment(name, previous.get(name));
    await rm(directory, { recursive: true, force: true });
  }
});

test("Gemini cloud and refresh failures retain safe provider errors", async () => {
  const oldClientId = process.env.GEMINI_CLIENT_ID;
  const oldClientSecret = process.env.GEMINI_CLIENT_SECRET;
  process.env.GEMINI_CLIENT_ID = "fixture-client";
  process.env.GEMINI_CLIENT_SECRET = "fixture-secret";
  const createClient = (credentials: {
    accessToken?: string;
    refreshToken?: string;
    idToken?: string;
    expiresAt?: number;
    sourcePath: string;
  } = {
    accessToken: "access-token",
    expiresAt: Date.now() + 3_600_000,
    sourcePath: "fixture",
  }) => new GeminiUsageClient({
    fetchAntigravity: async () => undefined,
    fetchAgy: async () => undefined,
    loadCredentials: async () => credentials,
  });
  try {
    for (const [status, pattern] of [
      [401, /sign-in has expired/i],
      [403, /sign-in has expired/i],
      [500, /HTTP 500/i],
    ] as const) {
      mock.restoreAll();
      mock.method(security, "requestJson", (async (url) => String(url).includes("loadCodeAssist")
        ? { status: 503, headers: {}, data: {} }
        : { status, headers: {}, data: {} }) as typeof security.requestJson);
      await assert.rejects(() => createClient().getUsage(), pattern);
    }

    mock.restoreAll();
    mock.method(security, "requestJson", (async (url) => String(url).includes("loadCodeAssist")
      ? { status: 200, headers: {}, data: { currentTier: { id: "free-tier" } } }
      : { status: 429, headers: { "retry-after": "3" }, data: {} }) as typeof security.requestJson);
    await assert.rejects(
      () => createClient().getUsage(),
      (error: unknown) => error instanceof UsageProviderError && error.retryAfterMs === 3_000,
    );

    mock.restoreAll();
    mock.method(security, "requestJson", (async (url) => {
      if (String(url).includes("loadCodeAssist")) return { status: 200, headers: {}, data: {} };
      throw new SyntaxError("invalid quota JSON");
    }) as typeof security.requestJson);
    await assert.rejects(() => createClient().getUsage(), /invalid or unavailable data/i);

    for (const refreshResult of [
      { status: 500, headers: {}, data: {} },
      { status: 200, headers: {}, data: { access_token: " " } },
    ]) {
      mock.restoreAll();
      mock.method(security, "requestJson", (async () => refreshResult) as typeof security.requestJson);
      await assert.rejects(
        () => createClient({
          accessToken: "expired-token",
          refreshToken: "refresh-token",
          expiresAt: Date.now() - 1,
          sourcePath: "fixture",
        }).getUsage(),
        /sign-in has expired|empty access token/i,
      );
    }

    mock.restoreAll();
    mock.method(security, "requestJson", (async () => { throw new Error("network down"); }) as typeof security.requestJson);
    await assert.rejects(
      () => createClient({
        accessToken: "expired-token",
        refreshToken: "refresh-token",
        expiresAt: Date.now() - 1,
        sourcePath: "fixture",
      }).getUsage(),
      /sign-in has expired/i,
    );
  } finally {
    mock.restoreAll();
    restoreEnvironment("GEMINI_CLIENT_ID", oldClientId);
    restoreEnvironment("GEMINI_CLIENT_SECRET", oldClientSecret);
  }
});

test("a failed agy hub start is negatively cached instead of retried every refresh", () => {
  // CLI builds from 2026-09 onwards reject the environment-supplied CSRF token, so every hub call
  // fails and the probe falls back to `agy -p /usage` ג€” a child process per refresh, which flashes a
  // console on Windows. Retrying the dead hub each poll pays a 20s stall on top of that.
  clearHubStartBackoff();
  const failedAt = Date.now();
  assert.equal(shouldSkipHubStart(failedAt), false);

  recordHubStartFailure(failedAt);
  assert.equal(shouldSkipHubStart(failedAt), true);
  assert.equal(shouldSkipHubStart(failedAt + hubRetryIntervalMs - 60_000), true);
  assert.equal(shouldSkipHubStart(failedAt + hubRetryIntervalMs), false);

  clearHubStartBackoff();
  assert.equal(shouldSkipHubStart(failedAt), false);
});

test("the agy hub is started with the CSRF token on its command line", () => {
  const args = agyHubArguments(51234, "TOKEN");
  assert.deepEqual([...args], ["--hub", "--hub-port=51234", "--csrf_token=TOKEN"]);
  // The sign-in lives in the CLI's default data directory, so the hub must not be redirected.
  assert.equal(args.some((argument) => argument.startsWith("--app_data_dir")), false);
  // An agy the extension starts never runs its self-updater; only the lowercase value is honoured.
  assert.deepEqual(agyOwnedEnvironment, { CI: "1", AGY_CLI_DISABLE_AUTO_UPDATE: "true" });
});

test("a refresh the user asked for clears both agy backoffs", async () => {
  // Without this, one missed hub start keeps the per-refresh `agy -p /usage` fallback ג€” and its
  // console flash ג€” in place for the whole 30-minute window, however often the user hits Refresh.
  let cleared = 0;
  const client = new GeminiUsageClient({
    fetchAntigravity: async () => undefined,
    fetchAgy: async () => undefined,
    hasAgyHub: () => false,
    resetAgyBackoff: () => { cleared += 1; },
    loadCredentials: async () => { throw new UsageProviderError("Google Gemini is not signed in."); },
  });

  await assert.rejects(client.getUsage());
  // The "both paths are stale" recovery exists to retry agy at once, so it must release the hub too.
  assert.equal(cleared, 1);

  client.onForcedRefresh();
  assert.equal(cleared, 2);
});
