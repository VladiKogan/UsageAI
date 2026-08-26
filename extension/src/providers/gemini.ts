import type { ChildProcessWithoutNullStreams } from "node:child_process";
import { randomUUID } from "node:crypto";
import { access, readdir } from "node:fs/promises";
import { createServer } from "node:net";
import * as path from "node:path";
import { UsageProviderError } from "../errors";
import { clampPercent, type UsageClient, type UsageMetric, type UsageSnapshot } from "../model";
import {
  collectProcessOutput,
  findExecutable,
  normalizeToken,
  parseRetryAfter,
  readBoundedText,
  requestJson,
  spawnSecure,
  userHome,
} from "../security";
import { getArray, getNumber, getObject, getString, isObject, parseDate } from "./json";

const quotaEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
const codeAssistEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
const tokenRefreshEndpoint = "https://oauth2.googleapis.com/token";
const notSignedInMessage = "Google Gemini is not signed in. Run `agy` to sign in, then refresh.";
const signInAgainMessage = "Google Gemini sign-in has expired. Run `agy` to sign in again, then refresh.";

interface GeminiCredentials {
  readonly accessToken?: string;
  readonly refreshToken?: string;
  readonly idToken?: string;
  readonly expiresAt?: number;
  readonly sourcePath: string;
}

interface GeminiUsageDependencies {
  readonly fetchAntigravity: (signal?: AbortSignal) => Promise<UsageSnapshot | undefined>;
  readonly fetchAgy: (signal?: AbortSignal) => Promise<UsageSnapshot | undefined>;
  readonly hasAgyHub?: () => boolean;
  readonly loadCredentials: () => Promise<GeminiCredentials>;
}

export interface AntigravityProcess {
  readonly pid: number;
  readonly tokens: readonly string[];
  readonly hintedPort?: number;
}

export interface AntigravityProbeDependencies {
  readonly detectProcesses: () => Promise<readonly AntigravityProcess[]>;
  readonly listeningPorts: (pid: number) => Promise<readonly number[]>;
  readonly query: (port: number, token: string, signal?: AbortSignal) => Promise<UsageSnapshot | undefined>;
}

export class GeminiUsageClient implements UsageClient {
  public readonly id = "gemini";
  public readonly displayName = "Google Gemini";
  public readonly signInCommand = "agy";
  public readonly accountUrl = "https://aistudio.google.com/";
  private refreshedCredentials: GeminiCredentials | undefined;
  private agyRetryAfter = 0;

  public constructor(private readonly dependencies: GeminiUsageDependencies = {
    fetchAntigravity: tryFetchAntigravitySnapshot,
    fetchAgy: tryFetchAgySnapshot,
    hasAgyHub: hasActiveAgyHub,
    loadCredentials,
  }) {}

  public async getUsage(signal?: AbortSignal): Promise<UsageSnapshot> {
    // Once a hub is serving this session, prefer it: the Antigravity IDE probe costs a PowerShell
    // child process on every refresh and reports the same quota buckets.
    if (this.dependencies.hasAgyHub?.() === true) {
      const hubbed = await this.dependencies.fetchAgy(signal);
      if (hubbed) {
        return hubbed;
      }
    }

    const antigravity = await this.dependencies.fetchAntigravity(signal);
    if (antigravity) {
      return antigravity;
    }

    const agyWasSkipped = Date.now() < this.agyRetryAfter;
    if (!agyWasSkipped) {
      const agy = await this.dependencies.fetchAgy(signal);
      if (agy) {
        return agy;
      }
      this.agyRetryAfter = Date.now() + 30 * 60_000;
    }

    try {
      return await this.getGeminiCliUsage(signal);
    } catch (error) {
      this.agyRetryAfter = 0;
      if (agyWasSkipped) {
        const recovered = await this.dependencies.fetchAgy(signal);
        if (recovered) {
          return recovered;
        }
      }
      throw error;
    }
  }

  private async getGeminiCliUsage(signal?: AbortSignal): Promise<UsageSnapshot> {
    let credentials: GeminiCredentials;
    try {
      credentials = await this.dependencies.loadCredentials();
    } catch (error) {
      throw new UsageProviderError(
        notSignedInMessage,
        0,
        { cause: error },
      );
    }
    credentials = await this.ensureFreshCredentials(credentials, signal);
    if (!credentials.accessToken) {
      throw new UsageProviderError("Gemini OAuth credentials do not contain a valid access token.");
    }
    return this.fetchCloudSnapshot(credentials, signal);
  }

  private async fetchCloudSnapshot(
    credentials: GeminiCredentials,
    signal?: AbortSignal,
  ): Promise<UsageSnapshot> {
    const plan = await loadCodeAssistPlan(credentials, signal);
    try {
      const response = await requestJson(quotaEndpoint, {
        method: "POST",
        allowedHosts: ["cloudcode-pa.googleapis.com"],
        headers: {
          Authorization: `Bearer ${credentials.accessToken ?? ""}`,
          "Content-Type": "application/json",
        },
        body: "{}",
        ...(signal ? { signal } : {}),
      });
      if (response.status === 401) {
        throw new UsageProviderError(signInAgainMessage);
      }
      if (response.status === 403) {
        throw new UsageProviderError(signInAgainMessage);
      }
      if (response.status === 429) {
        throw new UsageProviderError("Google Cloud API rate-limited the quota request.", parseRetryAfter(response.headers));
      }
      if (response.status < 200 || response.status >= 300) {
        throw new UsageProviderError(`Google Gemini API returned HTTP ${response.status} while reading usage.`);
      }
      return parseGeminiQuotaResponse(response.data, plan, extractJwtString(credentials.idToken, "email"));
    } catch (error) {
      if (error instanceof UsageProviderError) {
        throw error;
      }
      throw new UsageProviderError(
        "Could not read Gemini usage because the provider returned invalid or unavailable data.",
        0,
        { cause: error },
      );
    }
  }

  private async ensureFreshCredentials(
    credentials: GeminiCredentials,
    signal?: AbortSignal,
  ): Promise<GeminiCredentials> {
    if (this.refreshedCredentials?.sourcePath === credentials.sourcePath &&
        this.refreshedCredentials.accessToken &&
        (this.refreshedCredentials.expiresAt ?? 0) > Date.now() + 300_000) {
      return this.refreshedCredentials;
    }
    if (credentials.accessToken && (!credentials.expiresAt || credentials.expiresAt > Date.now() + 300_000)) {
      return credentials;
    }
    if (!credentials.refreshToken) {
      throw new UsageProviderError(signInAgainMessage);
    }
    const client = await resolveOAuthClientCredentials();
    const body = new URLSearchParams({
      client_id: client.id,
      client_secret: client.secret,
      refresh_token: credentials.refreshToken,
      grant_type: "refresh_token",
    }).toString();
    try {
      const response = await requestJson(tokenRefreshEndpoint, {
        method: "POST",
        allowedHosts: ["oauth2.googleapis.com"],
        headers: { "Content-Type": "application/x-www-form-urlencoded" },
        body,
        ...(signal ? { signal } : {}),
      });
      if (response.status < 200 || response.status >= 300) {
        throw new UsageProviderError(signInAgainMessage);
      }
      const accessToken = normalizeToken(getString(response.data, "access_token"));
      if (!accessToken) {
        throw new UsageProviderError("Google returned an empty access token upon refresh.");
      }
      const expiresIn = Math.max(60, Math.min(604_800, getNumber(response.data, "expires_in") ?? 3_600));
      const idToken = normalizeToken(getString(response.data, "id_token")) ?? credentials.idToken;
      const refreshed: GeminiCredentials = {
        ...credentials,
        accessToken,
        ...(idToken ? { idToken } : {}),
        expiresAt: Date.now() + expiresIn * 1000,
      };
      this.refreshedCredentials = refreshed;
      return refreshed;
    } catch (error) {
      if (error instanceof UsageProviderError) {
        throw error;
      }
      throw new UsageProviderError(signInAgainMessage, 0, { cause: error });
    }
  }
}

export function parseGeminiQuotaResponse(
  root: unknown,
  planFromCodeAssist?: string,
  accountEmail?: string,
): UsageSnapshot {
  const buckets = getArray(root, "buckets");
  if (!buckets) {
    throw new UsageProviderError("Gemini returned account details without quota buckets.");
  }
  const quotas = new Map<string, { readonly remaining: number; readonly resetsAt?: string }>();
  for (const bucket of buckets) {
    const modelId = getString(bucket, "modelId");
    const remaining = getNumber(bucket, "remainingFraction") ?? 0;
    if (!modelId) {
      continue;
    }
    const resetsAt = parseDate(getString(bucket, "resetTime"));
    const existing = quotas.get(modelId);
    if (!existing || remaining < existing.remaining) {
      quotas.set(modelId, { remaining, ...(resetsAt ? { resetsAt } : {}) });
    }
  }
  if (quotas.size === 0) {
    throw new UsageProviderError("Gemini quota response contained no valid model quota buckets.");
  }
  const entries = [...quotas.entries()];
  const pro = entries.filter(([id]) => /pro/i.test(id)).sort((a, b) => a[1].remaining - b[1].remaining)[0];
  const flash = entries.filter(([id]) => /flash/i.test(id)).sort((a, b) => a[1].remaining - b[1].remaining)[0];
  const primary = pro ?? flash ?? entries[0];
  if (!primary) {
    throw new UsageProviderError("Gemini quota response contained no valid model quota buckets.");
  }
  const metrics: UsageMetric[] = [geminiModelMetric(primary[0], primary[1])];
  if (pro && flash && pro[0].toLowerCase() !== flash[0].toLowerCase()) {
    metrics.push(geminiModelMetric(flash[0], flash[1]));
  }
  return {
    plan: planFromCodeAssist?.trim() || "Gemini",
    metrics,
    fetchedAt: new Date().toISOString(),
    providerId: "gemini",
    providerName: "Google Gemini",
    ...(accountEmail ? { accountName: accountEmail } : {}),
  };
}

export function parseAntigravityUserStatus(root: unknown): UsageSnapshot | undefined {
  const userStatus = getObject(root, "userStatus");
  if (!userStatus) {
    return undefined;
  }
  const accountName = getString(userStatus, "email");
  const userTier = getObject(userStatus, "userTier");
  const planInfo = getObject(getObject(userStatus, "planStatus"), "planInfo");
  const plan = getString(userTier, "name")
    ?? getString(userTier, "description")
    ?? getString(planInfo, "planDisplayName")
    ?? getString(planInfo, "planName")
    ?? "Google AI Pro";
  const configs = getArray(getObject(userStatus, "cascadeModelConfigData"), "clientModelConfigs") ?? [];
  const grouped = new Map<string, Array<{ readonly remaining: number; readonly resetsAt?: string }>>();
  for (const config of configs) {
    const label = getString(config, "label");
    const quotaInfo = getObject(config, "quotaInfo");
    if (!label || !quotaInfo) {
      continue;
    }
    const resetsAt = parseDate(getString(quotaInfo, "resetTime"));
    const entry: { readonly remaining: number; readonly resetsAt?: string } = {
      remaining: getNumber(quotaInfo, "remainingFraction") ?? 0,
      ...(resetsAt ? { resetsAt } : {}),
    };
    const group = modelGroupName(label);
    const values = grouped.get(group) ?? [];
    if (!values.some((value) => Math.abs(value.remaining - entry.remaining) < 0.001 && value.resetsAt === entry.resetsAt)) {
      values.push(entry);
      grouped.set(group, values);
    }
  }
  const order = ["Gemini Models", "Claude and GPT models"];
  const metrics: UsageMetric[] = [];
  for (const [group, values] of [...grouped].sort((a, b) => order.indexOf(a[0]) - order.indexOf(b[0]))) {
    values.forEach((value, index) => metrics.push({
      name: values.length > 1 ? `${group} (Limit ${index + 1})` : group,
      kind: "rolling",
      usedPercent: clampPercent((1 - value.remaining) * 100),
      ...(value.resetsAt ? { resetsAt: value.resetsAt } : {}),
      durationMinutes: 1_440,
    }));
  }
  if (metrics.length === 0) {
    return undefined;
  }
  return {
    plan,
    metrics,
    fetchedAt: new Date().toISOString(),
    providerId: "gemini",
    providerName: "Google Gemini",
    ...(accountName ? { accountName } : {}),
  };
}

export async function tryFetchAntigravitySnapshot(
  signal?: AbortSignal,
  dependencies: AntigravityProbeDependencies = {
    detectProcesses: detectAntigravityProcesses,
    listeningPorts,
    query: queryAntigravity,
  },
): Promise<UsageSnapshot | undefined> {
  try {
    for (const processInfo of await dependencies.detectProcesses()) {
      const ownedPorts = await dependencies.listeningPorts(processInfo.pid);
      const ports = processInfo.hintedPort && ownedPorts.includes(processInfo.hintedPort)
        ? [processInfo.hintedPort, ...ownedPorts.filter((port) => port !== processInfo.hintedPort)]
        : ownedPorts;
      for (const port of ports.slice(0, 32)) {
        if (!(await dependencies.listeningPorts(processInfo.pid)).includes(port)) {
          continue;
        }
        for (const token of processInfo.tokens) {
          const snapshot = await dependencies.query(port, token, signal);
          if (snapshot) {
            return snapshot;
          }
        }
      }
    }
  } catch {
    // Antigravity probing is best effort; Gemini CLI OAuth remains available.
  }
  return undefined;
}

interface AgyHub {
  readonly child: ChildProcessWithoutNullStreams;
  readonly port: number;
  readonly token: string;
}

const hubReadyTimeoutMs = 20_000;
let activeHub: AgyHub | undefined;
let pendingHub: Promise<AgyHub | undefined> | undefined;

/**
 * The Antigravity CLI serves the same quota summary from a long-lived `--hub` server. Reusing one hub
 * for the session keeps refreshes free of child processes, which matters on Windows because `agy -p`
 * boots every configured MCP server through `cmd.exe`, and those grandchildren can flash a console.
 */
async function ensureAgyHub(): Promise<AgyHub | undefined> {
  if (activeHub && activeHub.child.exitCode === null && !activeHub.child.killed) {
    return activeHub;
  }
  activeHub = undefined;
  pendingHub ??= startAgyHub().finally(() => {
    pendingHub = undefined;
  });
  return pendingHub;
}

async function startAgyHub(): Promise<AgyHub | undefined> {
  const executable = await findAgyExecutable();
  if (!executable) {
    return undefined;
  }
  const port = await reserveLoopbackPort();
  if (!port) {
    return undefined;
  }
  // The hub authenticates callers with a token it reads from its own environment, so minting one here
  // keeps the socket usable by this extension alone. `--app_data_dir` is deliberately omitted: the CLI's
  // default data directory is the one holding the Antigravity sign-in.
  const token = randomUUID();
  const child = spawnSecure(
    executable,
    ["--hub", `--hub-port=${port}`],
    ["ANTIGRAVITY_CLI_PATH", "GOOGLE_CLOUD_PROJECT", "NODE_EXTRA_CA_CERTS", "SSL_CERT_DIR", "SSL_CERT_FILE"],
    { CI: "1", ANTIGRAVITY_CSRF_TOKEN: token },
  );
  child.stdin.end();
  child.stdout.resume();
  child.stderr.resume();
  child.on("error", () => undefined);
  child.once("exit", () => {
    if (activeHub?.child === child) {
      activeHub = undefined;
    }
  });

  const deadline = Date.now() + hubReadyTimeoutMs;
  while (Date.now() < deadline) {
    if (child.exitCode !== null || child.signalCode !== null) {
      return undefined;
    }
    if (await queryHubQuotaSummary(port, token) !== undefined) {
      activeHub = { child, port, token };
      return activeHub;
    }
    await abortableDelay(400);
  }
  try { child.kill(); } catch { /* It already exited. */ }
  return undefined;
}

/** True while a hub started by this session is still running. */
export function hasActiveAgyHub(): boolean {
  return activeHub !== undefined && activeHub.child.exitCode === null && !activeHub.child.killed;
}

/** Frees the session's hub. Called from the extension's deactivate hook. */
export function disposeAgyHub(): void {
  const hub = activeHub;
  activeHub = undefined;
  if (!hub) {
    return;
  }
  try { hub.child.kill(); } catch { /* It already exited. */ }
}

async function reserveLoopbackPort(): Promise<number | undefined> {
  return new Promise<number | undefined>((resolve) => {
    const server = createServer();
    server.unref();
    server.once("error", () => resolve(undefined));
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : undefined;
      server.close(() => resolve(port !== undefined && validPort(port) ? port : undefined));
    });
  });
}

async function requestHub<T = unknown>(
  port: number,
  token: string,
  method: string,
  signal?: AbortSignal,
): Promise<T | undefined> {
  try {
    const response = await requestJson<T>(
      `http://127.0.0.1:${port}/exa.language_server_pb.LanguageServerService/${method}`,
      {
        method: "POST",
        allowedHosts: ["127.0.0.1"],
        allowLoopbackPlaintext: true,
        timeoutMs: 5_000,
        headers: {
          "Connect-Protocol-Version": "1",
          "X-Codeium-Csrf-Token": token,
          "Content-Type": "application/json",
        },
        // This service rejects unknown request fields, and both calls take no arguments.
        body: "{}",
        ...(signal ? { signal } : {}),
      },
    );
    return response.status >= 200 && response.status < 300 ? response.data : undefined;
  } catch {
    return undefined;
  }
}

async function queryHubQuotaSummary(
  port: number,
  token: string,
  signal?: AbortSignal,
): Promise<readonly UsageMetric[] | undefined> {
  const data = await requestHub(port, token, "RetrieveUserQuotaSummary", signal);
  if (data === undefined) {
    return undefined;
  }
  const metrics = parseAntigravityQuotaSummary(data);
  return metrics.length > 0 ? metrics : undefined;
}

async function tryFetchAgySnapshot(signal?: AbortSignal): Promise<UsageSnapshot | undefined> {
  const hub = await ensureAgyHub();
  if (hub) {
    const metrics = await queryHubQuotaSummary(hub.port, hub.token, signal);
    if (metrics) {
      const status = parseAntigravityUserStatus(await requestHub(hub.port, hub.token, "GetUserStatus", signal));
      return status
        ? { ...status, metrics: mergeQuotaSummary(status.metrics, metrics) }
        : {
          plan: "Antigravity",
          metrics,
          fetchedAt: new Date().toISOString(),
          providerId: "gemini",
          providerName: "Google Gemini",
        };
    }
  }
  return tryFetchAgyPrintSnapshot(signal);
}

/** Fallback for CLI builds without `--hub`: one short-lived `agy -p /usage` read. */
async function tryFetchAgyPrintSnapshot(signal?: AbortSignal): Promise<UsageSnapshot | undefined> {
  const executable = await findAgyExecutable();
  if (!executable) {
    return undefined;
  }

  const child = spawnSecure(
    executable,
    ["-p", "/usage", "--output-format", "json", "--print-timeout=12s"],
    ["ANTIGRAVITY_CLI_PATH", "GOOGLE_CLOUD_PROJECT", "NODE_EXTRA_CA_CERTS", "SSL_CERT_DIR", "SSL_CERT_FILE"],
    { CI: "1" },
  );
  child.stdin.end();
  let stdout = "";
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk: string) => {
    stdout = (stdout + chunk).slice(0, 262_144);
  });
  // Always drain stderr, but never retain or surface the CLI's account/login diagnostics.
  child.stderr.on("data", () => undefined);

  const probeController = new AbortController();
  const timeout = setTimeout(() => probeController.abort(new Error("The Antigravity CLI probe timed out.")), 15_000);
  timeout.unref();
  const abortFromCaller = () => probeController.abort(
    signal?.reason instanceof Error ? signal.reason : new Error("Operation cancelled."),
  );
  signal?.addEventListener("abort", abortFromCaller, { once: true });
  try {
    await new Promise<void>((resolve) => {
      child.once("exit", () => resolve());
      child.once("error", () => resolve());
      probeController.signal.addEventListener("abort", () => resolve(), { once: true });
    });
  } finally {
    clearTimeout(timeout);
    signal?.removeEventListener("abort", abortFromCaller);
    if (child.exitCode === null) {
      try { child.kill(); } catch { /* It already exited. */ }
    }
  }

  return parseAgyUsageOutput(stdout);
}

async function findAgyExecutable(): Promise<string | undefined> {
  const configured = process.env.ANTIGRAVITY_CLI_PATH?.trim().replace(/^"|"$/g, "");
  if (configured && path.isAbsolute(configured)) {
    try {
      await access(configured);
      return path.resolve(configured);
    } catch {
      // Continue through official install locations.
    }
  }

  const candidates = agyExecutableCandidates();
  for (const candidate of candidates.slice(0, 1)) {
    if (!candidate || !path.isAbsolute(candidate)) {
      continue;
    }
    try {
      await access(candidate);
      return candidate;
    } catch {
      // Try the next well-known location.
    }
  }

  const fromPath = await findExecutable(process.platform === "win32" ? ["agy.exe"] : ["agy"]);
  if (fromPath) {
    return fromPath;
  }

  for (const candidate of candidates.slice(1)) {
    if (!candidate || !path.isAbsolute(candidate)) {
      continue;
    }
    try {
      await access(candidate);
      return candidate;
    } catch {
      // Try the next well-known location.
    }
  }
  return undefined;
}

export function agyExecutableCandidates(): readonly string[] {
  const executable = process.platform === "win32" ? "agy.exe" : "agy";
  const extensionBackend = path.join(userHome(), ".gemini", "bin", executable);
  return process.platform === "win32"
    ? [extensionBackend, path.join(process.env.LOCALAPPDATA ?? "", "agy", "bin", executable)]
    : [extensionBackend, path.join(userHome(), ".local", "bin", executable), "/opt/homebrew/bin/agy", "/usr/local/bin/agy"];
}

export function parseAgyUsageOutput(output: string): UsageSnapshot | undefined {
  if (!output.trim()) {
    return undefined;
  }
  let root: unknown;
  try {
    root = JSON.parse(output) as unknown;
  } catch {
    const start = output.indexOf("{");
    const end = output.lastIndexOf("}");
    if (start < 0 || end <= start) return undefined;
    try {
      root = JSON.parse(output.slice(start, end + 1)) as unknown;
    } catch {
      return undefined;
    }
  }
  const quota = findQuotaContainer(root, 0);
  const metrics = quota ? parseAntigravityQuotaSummary(quota) : [];
  if (metrics.length === 0) return undefined;
  const accountName = findNestedString(root, ["email", "accountEmail"]);
  return {
    plan: findNestedString(root, ["plan", "planName", "tier"]) ?? "Antigravity",
    metrics,
    fetchedAt: new Date().toISOString(),
    providerId: "gemini",
    providerName: "Google Gemini",
    ...(accountName ? { accountName } : {}),
  };
}

function findQuotaContainer(value: unknown, depth: number): unknown | undefined {
  if (depth > 12) return undefined;
  if (parseAntigravityQuotaSummary(value).length > 0) return value;
  if (typeof value === "string" && value.length <= 262_144) {
    try {
      return findQuotaContainer(JSON.parse(value) as unknown, depth + 1);
    } catch {
      return undefined;
    }
  }
  if (Array.isArray(value)) {
    for (const child of value) {
      const found = findQuotaContainer(child, depth + 1);
      if (found) return found;
    }
  } else if (isObject(value)) {
    for (const child of Object.values(value)) {
      const found = findQuotaContainer(child, depth + 1);
      if (found) return found;
    }
  }
  return undefined;
}

function findNestedString(value: unknown, names: readonly string[]): string | undefined {
  if (Array.isArray(value)) {
    for (const child of value) {
      const found = findNestedString(child, names);
      if (found) return found;
    }
  } else if (isObject(value)) {
    for (const [name, child] of Object.entries(value)) {
      if (names.some((candidate) => candidate.toLowerCase() === name.toLowerCase()) && typeof child === "string") {
        return child;
      }
      const found = findNestedString(child, names);
      if (found) return found;
    }
  }
  return undefined;
}

function abortableDelay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(resolve, milliseconds);
    timer.unref();
    signal?.addEventListener("abort", () => {
      clearTimeout(timer);
      reject(signal.reason instanceof Error ? signal.reason : new Error("Operation cancelled."));
    }, { once: true });
  });
}

async function queryAntigravity(
  port: number,
  csrfToken: string,
  signal?: AbortSignal,
): Promise<UsageSnapshot | undefined> {
  try {
    const response = await requestJson(
      `https://127.0.0.1:${port}/exa.language_server_pb.LanguageServerService/GetUserStatus`,
      {
        method: "POST",
        allowedHosts: ["127.0.0.1"],
        allowLoopbackSelfSigned: true,
        timeoutMs: 5_000,
        headers: {
          "Connect-Protocol-Version": "1",
          "X-Codeium-Csrf-Token": csrfToken,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          metadata: { ideName: "antigravity", extensionName: "antigravity", ideVersion: "unknown", locale: "en" },
        }),
        ...(signal ? { signal } : {}),
      },
    );
    if (response.status < 200 || response.status >= 300) {
      return undefined;
    }
    const snapshot = parseAntigravityUserStatus(response.data);
    if (!snapshot) {
      return undefined;
    }
    const summary = await queryAntigravityQuotaSummary(port, csrfToken, signal);
    return summary.length > 0 ? { ...snapshot, metrics: mergeQuotaSummary(snapshot.metrics, summary) } : snapshot;
  } catch {
    return undefined;
  }
}

async function queryAntigravityQuotaSummary(
  port: number,
  csrfToken: string,
  signal?: AbortSignal,
): Promise<readonly UsageMetric[]> {
  try {
    const response = await requestJson(
      `https://127.0.0.1:${port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary`,
      {
        method: "POST",
        allowedHosts: ["127.0.0.1"],
        allowLoopbackSelfSigned: true,
        timeoutMs: 5_000,
        headers: {
          "Connect-Protocol-Version": "1",
          "X-Codeium-Csrf-Token": csrfToken,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ request: {}, forceRefresh: false }),
        ...(signal ? { signal } : {}),
      },
    );
    return response.status >= 200 && response.status < 300
      ? parseAntigravityQuotaSummary(response.data)
      : [];
  } catch {
    return [];
  }
}

export function parseAntigravityQuotaSummary(root: unknown): readonly UsageMetric[] {
  const groups = quotaSummaryGroups(root);
  if (!groups) {
    return [];
  }
  const parsed: Array<{ readonly groupOrder: number; readonly windowOrder: number; readonly sourceOrder: number; readonly metric: UsageMetric }> = [];
  let sourceOrder = 0;
  for (const group of groups) {
    const groupName = getString(group, "displayName") ?? getString(group, "name");
    const buckets = getArray(group, "buckets");
    if (!groupName || !buckets) {
      continue;
    }
    for (const bucket of buckets) {
      const remaining = getNumber(bucket, "remainingFraction")
        ?? getNumber(bucket, "remaining_fraction")
        ?? getNumber(getObject(bucket, "remaining"), "remainingFraction")
        ?? getNumber(getObject(bucket, "remaining"), "remaining_fraction");
      if (remaining === undefined) {
        continue;
      }
      const window = classifySummaryWindow(bucket);
      const resetsAt = parseDate(getString(bucket, "resetTime") ?? getString(bucket, "reset_time"));
      parsed.push({
        groupOrder: /^(Gemini Models)$/i.test(groupName) ? 0 : /Claude|GPT/i.test(groupName) ? 1 : 2,
        windowOrder: window.order,
        sourceOrder: sourceOrder++,
        metric: {
          name: `${groupName} (${window.name})`,
          kind: window.kind,
          usedPercent: clampPercent((1 - remaining) * 100),
          ...(resetsAt ? { resetsAt } : {}),
          ...(window.durationMinutes !== undefined ? { durationMinutes: window.durationMinutes } : {}),
        },
      });
    }
  }
  return parsed
    .sort((left, right) => left.groupOrder - right.groupOrder || left.windowOrder - right.windowOrder || left.sourceOrder - right.sourceOrder)
    .map(({ metric }) => metric);
}

function quotaSummaryGroups(root: unknown): readonly unknown[] | undefined {
  return getArray(root, "groups")
    ?? getArray(getObject(root, "response"), "groups")
    ?? getArray(getObject(getObject(root, "response"), "quotaSummary"), "groups")
    ?? getArray(getObject(root, "quotaSummary"), "groups");
}

function classifySummaryWindow(bucket: unknown): {
  readonly name: string;
  readonly kind: "session" | "rolling";
  readonly durationMinutes?: number;
  readonly order: number;
} {
  const displayName = getString(bucket, "displayName") ?? getString(bucket, "name");
  const descriptor = [getString(bucket, "bucketId") ?? getString(bucket, "id"), displayName, getString(bucket, "window")]
    .filter(Boolean)
    .join(" ")
    .toLowerCase();
  if (/week|7-day|7 day|7d/.test(descriptor)) {
    return { name: "Weekly", kind: "rolling", durationMinutes: 10_080, order: 1 };
  }
  if (/5-hour|5 hour|5h|five|session/.test(descriptor)) {
    return { name: "5-hour", kind: "session", durationMinutes: 300, order: 0 };
  }
  return { name: displayName?.trim() || "Quota", kind: "rolling", order: 2 };
}

function mergeQuotaSummary(
  currentMetrics: readonly UsageMetric[],
  summaryMetrics: readonly UsageMetric[],
): readonly UsageMetric[] {
  const groupName = (metricName: string) => {
    const suffix = metricName.lastIndexOf(" (");
    return suffix > 0 ? metricName.slice(0, suffix) : metricName;
  };
  const groups = [...new Set(summaryMetrics.map((metric) => groupName(metric.name)))];
  const merged: UsageMetric[] = [];
  for (const group of groups) {
    const summary = summaryMetrics.filter((metric) => groupName(metric.name).toLowerCase() === group.toLowerCase());
    if (!summary.some((metric) => metric.kind === "session")) {
      const existing = currentMetrics.find((metric) =>
        metric.name.toLowerCase() === group.toLowerCase() || metric.name.toLowerCase().startsWith(`${group.toLowerCase()} (`));
      if (existing) {
        merged.push({ ...existing, name: `${group} (5-hour)`, kind: "session", durationMinutes: 300 });
      }
    }
    merged.push(...summary);
  }
  merged.push(...currentMetrics.filter((metric) => !groups.some((group) =>
    metric.name.toLowerCase() === group.toLowerCase() || metric.name.toLowerCase().startsWith(`${group.toLowerCase()} (`))));
  return merged;
}

async function detectAntigravityProcesses(): Promise<AntigravityProcess[]> {
  if (process.platform === "win32") {
    const systemRoot = process.env.SystemRoot ?? "C:\\Windows";
    const preferred = path.join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    let powershell = preferred;
    try {
      await access(preferred);
    } catch {
      powershell = await findExecutable(["powershell.exe"]) ?? preferred;
    }
    const script = "Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*language_server_windows*' -or $_.Name -like 'language_server.exe' } | ForEach-Object { \"$($_.ProcessId)`t$($_.CommandLine)\" }";
    const result = await collectProcessOutput(spawnSecure(powershell, ["-NoProfile", "-NonInteractive", "-Command", script]), 3_000, 262_144);
    return parseAntigravityProcessLines(result.stdout);
  }

  const ps = await findExecutable(["ps"]);
  if (!ps) {
    return [];
  }
  const result = await collectProcessOutput(spawnSecure(ps, ["-ax", "-ww", "-o", "pid=,command="]), 3_000, 262_144);
  return parseAntigravityProcessLines(result.stdout);
}

export function parseAntigravityProcessLines(stdout: string): AntigravityProcess[] {
  const results: AntigravityProcess[] = [];
  for (const line of stdout.split(/\r?\n/)) {
    if (!/language_server/i.test(line) || !line.includes("--csrf_token")) {
      continue;
    }
    const match = line.match(/^\s*(\d+)\s+(.*)$/);
    if (!match?.[1] || !match[2]) {
      continue;
    }
    const pid = Number(match[1]);
    const command = match[2];
    const csrf = command.match(/--csrf_token(?:\s+|\s*=\s*)(\S+)/)?.[1];
    const extensionCsrf = command.match(/--extension_server_csrf_token(?:\s+|\s*=\s*)(\S+)/)?.[1];
    const hintedPort = Number(command.match(/--extension_server_port(?:\s+|\s*=\s*)(\d+)/)?.[1]);
    const tokens = [...new Set([extensionCsrf, csrf].filter((value): value is string => Boolean(value)))];
    if (Number.isInteger(pid) && pid > 0 && tokens.length > 0) {
      results.push({ pid, tokens, ...(Number.isInteger(hintedPort) && hintedPort > 0 ? { hintedPort } : {}) });
    }
    if (results.length >= 16) {
      break;
    }
  }
  return results;
}

async function listeningPorts(pid: number): Promise<number[]> {
  if (process.platform === "win32") {
    const systemRoot = process.env.SystemRoot ?? "C:\\Windows";
    const powershell = path.join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    try {
      const script = `Get-NetTCPConnection -OwningProcess ${pid} -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort`;
      const result = await collectProcessOutput(spawnSecure(powershell, ["-NoProfile", "-NonInteractive", "-Command", script]), 3_000, 32_768);
      return [...new Set(result.stdout.split(/\s+/).map(Number).filter(validPort))];
    } catch {
      return [];
    }
  }
  const lsof = await findExecutable(["lsof"]);
  if (!lsof) {
    return [];
  }
  try {
    const result = await collectProcessOutput(
      spawnSecure(lsof, ["-Pan", "-p", String(pid), "-iTCP", "-sTCP:LISTEN"]),
      3_000,
      65_536,
    );
    return [...new Set([...result.stdout.matchAll(/:(\d+)\s+\(LISTEN\)/g)].map((match) => Number(match[1])).filter(validPort))];
  } catch {
    return [];
  }
}

function validPort(value: number): boolean {
  return Number.isInteger(value) && value > 0 && value <= 65_535;
}

async function loadCredentials(): Promise<GeminiCredentials> {
  const sourcePath = path.join(geminiConfigDirectory(), "oauth_creds.json");
  const root = JSON.parse(await readBoundedText(sourcePath)) as unknown;
  const accessToken = normalizeToken(getString(root, "access_token") ?? getString(root, "accessToken"));
  const refreshToken = normalizeToken(getString(root, "refresh_token") ?? getString(root, "refreshToken"));
  const idToken = normalizeToken(getString(root, "id_token") ?? getString(root, "idToken"));
  const expiresAt = getNumber(root, "expiry_date");
  return {
    ...(accessToken ? { accessToken } : {}),
    ...(refreshToken ? { refreshToken } : {}),
    ...(idToken ? { idToken } : {}),
    ...(expiresAt !== undefined ? { expiresAt } : {}),
    sourcePath,
  };
}

function geminiConfigDirectory(): string {
  const configured = process.env.GEMINI_CONFIG_DIR?.trim().replace(/^"|"$/g, "");
  return configured && path.isAbsolute(configured) ? path.resolve(configured) : path.join(userHome(), ".gemini");
}

async function resolveOAuthClientCredentials(): Promise<{ readonly id: string; readonly secret: string }> {
  if (process.env.GEMINI_CLIENT_ID?.trim() && process.env.GEMINI_CLIENT_SECRET?.trim()) {
    return { id: process.env.GEMINI_CLIENT_ID.trim(), secret: process.env.GEMINI_CLIENT_SECRET.trim() };
  }
  try {
    const root = JSON.parse(await readBoundedText(path.join(geminiConfigDirectory(), "client_config.json"))) as unknown;
    const id = getString(root, "client_id")?.trim();
    const secret = getString(root, "client_secret")?.trim();
    if (id && secret) {
      return { id, secret };
    }
  } catch {
    // Use the official Gemini CLI client below.
  }
  return {
    id: Buffer.from([54,56,49,50,53,53,56,48,57,51,57,53,45,111,111,56,102,116,50,111,112,114,100,114,110,112,57,101,51,97,113,102,54,97,118,51,104,109,100,105,98,49,51,53,106,46,97,112,112,115,46,103,111,111,103,108,101,117,115,101,114,99,111,110,116,101,110,116,46,99,111,109]).toString("utf8"),
    secret: Buffer.from([71,79,67,83,80,88,45,52,117,72,103,77,80,109,45,49,111,55,83,107,45,103,101,86,54,67,117,53,99,108,88,70,115,120,108]).toString("utf8"),
  };
}

async function loadCodeAssistPlan(credentials: GeminiCredentials, signal?: AbortSignal): Promise<string | undefined> {
  if (!credentials.accessToken) {
    return undefined;
  }
  try {
    const response = await requestJson(codeAssistEndpoint, {
      method: "POST",
      allowedHosts: ["cloudcode-pa.googleapis.com"],
      headers: {
        Authorization: `Bearer ${credentials.accessToken}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ metadata: { ideType: "GEMINI_CLI", pluginType: "GEMINI" } }),
      ...(signal ? { signal } : {}),
    });
    if (response.status < 200 || response.status >= 300) {
      return undefined;
    }
    const paidTier = getString(getObject(response.data, "paidTier"), "name");
    if (paidTier) {
      return paidTier;
    }
    const tier = getString(getObject(response.data, "currentTier"), "id");
    const hostedDomain = extractJwtString(credentials.idToken, "hd");
    if (tier === "standard-tier") return "Paid";
    if (tier === "free-tier" && hostedDomain) return "Workspace";
    if (tier === "free-tier") return "Free";
    if (tier === "legacy-tier") return "Legacy";
  } catch {
    // Plan metadata is optional.
  }
  return undefined;
}

function extractJwtString(token: string | undefined, claim: string): string | undefined {
  const payload = token?.split(".")[1];
  if (!payload) {
    return undefined;
  }
  try {
    const root = JSON.parse(Buffer.from(payload, "base64url").toString("utf8")) as unknown;
    return getString(root, claim);
  } catch {
    return undefined;
  }
}

function geminiModelMetric(
  modelId: string,
  quota: { readonly remaining: number; readonly resetsAt?: string },
): UsageMetric {
  return {
    name: /pro/i.test(modelId) ? "Gemini Pro" : /flash/i.test(modelId) ? "Gemini Flash" : titleCase(modelId),
    kind: "rolling",
    usedPercent: clampPercent((1 - quota.remaining) * 100),
    ...(quota.resetsAt ? { resetsAt: quota.resetsAt } : {}),
    durationMinutes: 1_440,
  };
}

function modelGroupName(label: string): string {
  if (/claude|gpt|openai/i.test(label)) return "Claude and GPT models";
  if (/gemini|flash|pro/i.test(label)) return "Gemini Models";
  return "Other Models";
}

function titleCase(value: string): string {
  return value.replaceAll("-", " ").replaceAll("_", " ").replace(/\b\w/g, (letter) => letter.toUpperCase());
}
