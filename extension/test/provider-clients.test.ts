import assert from "node:assert/strict";
import { chmod, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import * as os from "node:os";
import * as path from "node:path";
import test, { mock } from "node:test";
import { UsageProviderError } from "../src/errors";
import { ClaudeUsageClient, runClaudeAuthStatus } from "../src/providers/claude";
import { CodexUsageClient } from "../src/providers/codex";
import { CopilotUsageClient } from "../src/providers/copilot";
import {
  GeminiUsageClient,
  parseAntigravityProcessLines,
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
    assert.equal(requests, 1);

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
  const oldPath = process.env.PATH;
  const fixtureSource = `
const readline = require("node:readline");
const input = readline.createInterface({ input: process.stdin });
input.on("line", (line) => {
  const message = JSON.parse(line);
  if (message.id === 1) {
    process.stdout.write(JSON.stringify({ id: 1, result: {} }) + "\\n");
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
  } finally {
    restoreEnvironment("CODEX_PATH", oldCodexPath);
    restoreEnvironment("PATH", oldPath);
    await rm(directory, { recursive: true, force: true });
  }
});

test("Codex client translates protocol and authentication failures", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "usageai-codex-errors-"));
  const oldCodexPath = process.env.CODEX_PATH;
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
    restoreEnvironment("PATH", oldPath);
    await rm(directory, { recursive: true, force: true });
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

test("Antigravity discovery binds tokens to revalidated process-owned ports", async () => {
  const processLines = [
    "not relevant",
    "42 language_server_windows.exe --csrf_token=primary --extension_server_csrf_token secondary --extension_server_port 5002",
    "0 language_server --csrf_token invalid",
    "43 language_server --extension_server_port=70000 --csrf_token=other",
  ].join("\n");
  assert.deepEqual(parseAntigravityProcessLines(processLines), [
    { pid: 42, tokens: ["secondary", "primary"], hintedPort: 5002 },
    { pid: 43, tokens: ["other"], hintedPort: 70000 },
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
