import { access } from "node:fs/promises";
import * as path from "node:path";
import * as readline from "node:readline";
import { commandWithEnvironment, defaultLoginFolder, mainSignInCommand } from "../accounts";
import { codexBundledPaths, findBundledExecutable, noExtensions, type ExtensionPathResolver } from "../bundled-cli";
import { UsageProviderError } from "../errors";
import { clampPercent, type UsageClient, type UsageMetric, type UsageSnapshot } from "../model";
import { findExecutable, normalizeToken, parseRetryAfter, readBoundedText, requestJson, spawnSecure } from "../security";
import { getArray, getNumber, getObject, getString, isObject } from "./json";

/** The endpoint the Codex CLI itself reads usage from. */
export const codexUsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

/** The Codex login's access token, read from its `auth.json` for the usage request alone. */
export interface CodexHttpCredentials {
  /** Sent only to chatgpt.com, never logged or stored. */
  readonly accessToken: string;
  readonly accountId?: string;
}

export interface CodexDependencies {
  readonly loadCredentials?: () => Promise<CodexHttpCredentials | undefined>;
}

/** A login is renewed by the CLI this long before it expires, so a token this close is left to it. */
const expirySkewMs = 60_000;

const requestTimeoutMs = 20_000;
const maxProtocolLineCharacters = 131_072;
const maxProtocolMessages = 512;
const codexExtensionId = "openai.chatgpt";

export interface CodexLaunch {
  readonly executable: string;
  readonly args: readonly string[];
}

/** A Codex login other than the default one: its own `CODEX_HOME`, read directly. */
export interface CodexAccount {
  /** The account id; the client's id becomes the unit key `codex@<id>`. */
  readonly id: string;
  readonly home: string;
}

export class CodexUsageClient implements UsageClient {
  public readonly id: string;
  public readonly displayName = "Codex";
  public readonly signInCommand: string;
  public readonly accountUrl = "https://chatgpt.com/codex/settings/usage";

  /**
   * `resolveExtensionPath` lets the CLI bundled in OpenAI's Codex extension serve as a last resort.
   * With `account`, that folder's `auth.json` is read for the HTTP path and the app-server is started
   * with exactly that `CODEX_HOME`, set explicitly rather than inherited.
   */
  public constructor(
    private readonly resolveExtensionPath: ExtensionPathResolver = noExtensions,
    private readonly dependencies: CodexDependencies = {},
    private readonly account?: CodexAccount,
  ) {
    this.id = account ? `codex@${account.id}` : "codex";
    this.signInCommand = account ? commandWithEnvironment("CODEX_HOME", account.home, "codex login") : mainSignInCommand("codex");
  }

  /**
   * Reads usage over HTTPS with the Codex login's own token when that login is a ChatGPT login that
   * has not expired, which spares starting the CLI on every refresh. Everything else — no login file,
   * an API-key login, an expired token, a custom backend, a rejected token, an outage, an unexpected
   * answer — goes to `codex app-server`, which refreshes the login itself. UsageAI never refreshes or
   * writes the token. Mirrors the desktop `CodexUsageClient`.
   */
  public async getUsage(signal?: AbortSignal): Promise<UsageSnapshot> {
    const account = this.account;
    const load = this.dependencies.loadCredentials
      ?? (account ? () => readCodexHttpCredentials(account.home) : () => loadCodexHttpCredentials());
    const credentials = await load();
    if (credentials) {
      const viaHttp = await tryHttpUsage(credentials, signal);
      if (viaHttp) {
        return viaHttp;
      }
    }
    return this.getAppServerUsage(signal);
  }

  private async getAppServerUsage(signal?: AbortSignal): Promise<UsageSnapshot> {
    const launch = await findCodexLaunch(this.resolveExtensionPath);
    const environment = codexAppServerEnvironment(this.account?.home);
    const child = spawnSecure(
      launch.executable,
      [...launch.args, ...codexAppServerArguments],
      environment.inherited,
      environment.overrides,
    );
    let stderr = "";
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (chunk: string) => {
      stderr = (stderr + chunk).slice(0, 16_384);
    });
    const lines = readline.createInterface({ input: child.stdout, crlfDelay: Infinity });
    const iterator = lines[Symbol.asyncIterator]();

    const abort = () => child.kill();
    signal?.addEventListener("abort", abort, { once: true });
    const timer = setTimeout(() => child.kill(), requestTimeoutMs);
    timer.unref();

    try {
      writeProtocolMessage(child.stdin, {
        id: 1,
        method: "initialize",
        params: {
          clientInfo: { name: "usage-ai-vscode", title: "UsageAI", version: "0.1.24" },
          capabilities: { experimentalApi: true },
        },
      });
      await readResponse(iterator, 1);
      writeProtocolMessage(child.stdin, { method: "initialized" });
      writeProtocolMessage(child.stdin, { id: 2, method: "account/rateLimits/read", params: null });
      const result = await readResponse(iterator, 2);
      return parseCodexSnapshot(result, await tryReadAccount(child.stdin, iterator));
    } catch (error) {
      if (signal?.aborted) {
        throw signal.reason;
      }
      if (/login|auth/i.test(stderr)) {
        throw new UsageProviderError("Codex is not signed in. Run `codex login` or sign in through the Codex extension, then refresh.", 0, { cause: error });
      }
      if (error instanceof UsageProviderError) {
        throw error;
      }
      throw new UsageProviderError(
        "Could not read Codex usage. Verify the Codex CLI installation, then refresh.",
        0,
        { cause: error },
      );
    } finally {
      clearTimeout(timer);
      signal?.removeEventListener("abort", abort);
      lines.close();
      child.stdin.end();
      child.kill();
    }
  }
}

/**
 * The environment the app-server gets: an account's folder set explicitly, and for the main login
 * no `CODEX_HOME`, so it uses the standard `~/.codex` UsageAI reads. Never inherited.
 */
export function codexAppServerEnvironment(home?: string): {
  readonly inherited: readonly string[];
  readonly overrides: NodeJS.ProcessEnv;
} {
  return {
    inherited: ["NODE_EXTRA_CA_CERTS", "SSL_CERT_DIR", "SSL_CERT_FILE"],
    overrides: home ? { CODEX_HOME: home } : {},
  };
}

async function tryHttpUsage(credentials: CodexHttpCredentials, signal?: AbortSignal): Promise<UsageSnapshot | undefined> {
  let response;
  try {
    response = await requestJson(codexUsageEndpoint, {
      method: "GET",
      allowedHosts: ["chatgpt.com"],
      headers: {
        Authorization: `Bearer ${credentials.accessToken}`,
        Accept: "application/json",
        ...(credentials.accountId ? { "ChatGPT-Account-Id": credentials.accountId } : {}),
      },
      timeoutMs: 15_000,
      ...(signal ? { signal } : {}),
    });
  } catch (error) {
    if (signal?.aborted) {
      throw signal.reason;
    }
    return undefined;
  }
  // The app-server would ask the same service, so a rate limit is reported, not retried.
  if (response.status === 429) {
    throw new UsageProviderError("ChatGPT rate-limited the Codex usage request.", parseRetryAfter(response.headers) || 300_000);
  }
  if (response.status < 200 || response.status >= 300) {
    return undefined;
  }
  try {
    return parseCodexHttpSnapshot(response.data);
  } catch {
    return undefined;
  }
}

/**
 * The main login's token when the HTTP path may use it: a ChatGPT login in the standard `~/.codex`,
 * never one pointed at a custom `chatgpt_base_url`, since the token goes to chatgpt.com alone. An
 * inherited `CODEX_HOME` never moves the main login; that folder is found as an account.
 */
export async function loadCodexHttpCredentials(now = Date.now()): Promise<CodexHttpCredentials | undefined> {
  return readCodexHttpCredentials(defaultLoginFolder("codex"), now);
}

export async function readCodexHttpCredentials(home: string, now = Date.now()): Promise<CodexHttpCredentials | undefined> {
  try {
    if (await hasCustomBackend(path.join(home, "config.toml"))) {
      return undefined;
    }
    const auth = JSON.parse(await readBoundedText(path.join(home, "auth.json"))) as unknown;
    // An API-key login has no ChatGPT tokens and no usage windows to read.
    const tokens = getObject(auth, "tokens");
    const accessToken = normalizeToken(getString(tokens, "access_token"));
    if (!accessToken) {
      return undefined;
    }
    const expiresAt = jwtExpiryMs(accessToken);
    if (expiresAt !== undefined && expiresAt <= now + expirySkewMs) {
      return undefined;
    }
    const rawAccountId = getString(tokens, "account_id");
    const accountId = rawAccountId && rawAccountId.length <= 128 && /^[A-Za-z0-9_-]+$/.test(rawAccountId) ? rawAccountId : undefined;
    return { accessToken, ...(accountId ? { accountId } : {}) };
  } catch {
    return undefined;
  }
}

/** True when `config.toml` points Codex at another backend, which only the app-server honours. */
async function hasCustomBackend(configPath: string): Promise<boolean> {
  let text: string;
  try {
    text = await readBoundedText(configPath);
  } catch (error) {
    // A missing file sets nothing; an unreadable one cannot be ruled out, so HTTP steps aside.
    return (error as NodeJS.ErrnoException).code !== "ENOENT";
  }
  return text.split("\n").some((line) => line.trim().startsWith("chatgpt_base_url"));
}

/** The `exp` claim of a JWT in milliseconds, or undefined when the token is not a readable JWT. */
export function jwtExpiryMs(token: string): number | undefined {
  const parts = token.split(".");
  if (parts.length !== 3 || !parts[1] || parts[1].length > 8_192) {
    return undefined;
  }
  try {
    const payload = JSON.parse(Buffer.from(parts[1], "base64url").toString("utf8")) as unknown;
    const exp = getNumber(payload, "exp");
    return exp !== undefined && exp > 0 && exp < 253_402_300_800 ? exp * 1000 : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Reads a `wham/usage` answer, the same data the app-server relays in its own field names. Extra
 * limits come from `additional_rate_limits`, named by `limit_name`.
 */
export function parseCodexHttpSnapshot(data: unknown): UsageSnapshot {
  const rateLimit = getObject(data, "rate_limit");
  if (!rateLimit || !hasWindow(rateLimit, httpFields)) {
    throw new UsageProviderError("Codex returned no rate-limit windows. Sign in with `codex login`, then refresh.");
  }
  const extras: Array<{ readonly group: string; readonly bucket: Record<string, unknown> }> = [];
  for (const entry of getArray(data, "additional_rate_limits") ?? []) {
    if (extras.length >= maxExtraBuckets) {
      break;
    }
    const bucket = getObject(entry, "rate_limit");
    if (!bucket || !hasWindow(bucket, httpFields)) {
      continue;
    }
    const group = cleanLabel(getString(entry, "limit_name") ?? getString(entry, "metered_feature"), "Other limit");
    if (!extras.some((existing) => existing.group.toLowerCase() === group.toLowerCase())) {
      extras.push({ group, bucket });
    }
  }

  const metrics: UsageMetric[] = [];
  addWindows(metrics, rateLimit, undefined, httpFields);
  for (const { group, bucket } of extras) {
    addWindows(metrics, bucket, group, httpFields);
  }
  addBalances(metrics, getString(getObject(data, "credits"), "balance"), getNumber(getObject(data, "rate_limit_reset_credits"), "available_count") ?? 0);

  const email = getString(data, "email");
  return {
    plan: formatPlan(getString(data, "plan_type") ?? "Codex"),
    metrics,
    fetchedAt: new Date().toISOString(),
    providerId: "codex",
    providerName: "Codex",
    ...(email && email.length <= 320 ? { accountName: email } : {}),
  };
}

/**
 * The `app-server` command with a read-only sandbox and no approval prompts, so the server UsageAI
 * starts can never be asked to run or approve anything. They are `-c` config overrides on the
 * subcommand itself: `-s` and `-a` belong to the interactive CLI and are not passed on to
 * `app-server`. Mirrors the desktop.
 */
export const codexAppServerArguments: readonly string[] = ["app-server", "-c", "sandbox_mode=\"read-only\"", "-c", "approval_policy=\"never\"", "--listen", "stdio://"];

/** How long the optional account lookup may take after usage has already arrived. */
const accountReadTimeoutMs = 5_000;

/**
 * Asks which account is signed in. Usage has already arrived, so an older CLI without
 * `account/read`, an error, or a slow answer leaves the account unnamed instead of failing the
 * refresh. `refreshToken: false` keeps the lookup from refreshing the login.
 */
async function tryReadAccount(
  stdin: NodeJS.WritableStream,
  iterator: AsyncIterator<string>,
): Promise<Record<string, unknown> | undefined> {
  let timer: NodeJS.Timeout | undefined;
  try {
    writeProtocolMessage(stdin, { id: 3, method: "account/read", params: { refreshToken: false } });
    return await Promise.race([
      readResponse(iterator, 3),
      new Promise<undefined>((resolve) => {
        timer = setTimeout(() => resolve(undefined), accountReadTimeoutMs);
        timer.unref();
      }),
    ]);
  } catch {
    return undefined;
  } finally {
    clearTimeout(timer);
  }
}

function writeProtocolMessage(stream: NodeJS.WritableStream, message: unknown): void {
  stream.write(`${JSON.stringify(message)}\n`);
}

async function readResponse(
  iterator: AsyncIterator<string>,
  expectedId: number,
): Promise<Record<string, unknown>> {
  for (let count = 0; count < maxProtocolMessages; count += 1) {
    const next = await iterator.next();
    if (next.done) {
      throw new UsageProviderError("The Codex app-server stopped before it returned usage data.");
    }
    if (next.value.length > maxProtocolLineCharacters) {
      throw new UsageProviderError("The Codex app-server returned an oversized message.");
    }

    let root: unknown;
    try {
      root = JSON.parse(next.value) as unknown;
    } catch {
      continue;
    }
    if (!isObject(root) || !matchesId(root.id, expectedId)) {
      continue;
    }
    if (isObject(root.error)) {
      const message = getString(root.error, "message");
      throw new UsageProviderError(
        message && /login|auth/i.test(message)
          ? "Codex is not signed in. Run `codex login` or sign in through the Codex extension, then refresh."
          : "Codex could not read the account rate limits.",
      );
    }
    if (!isObject(root.result)) {
      throw new UsageProviderError("Codex returned a response without usage data.");
    }
    return root.result;
  }
  throw new UsageProviderError("The Codex app-server returned too many unrelated messages.");
}

function matchesId(value: unknown, expectedId: number): boolean {
  return value === expectedId || value === String(expectedId);
}

/** At most this many extra rate-limit buckets (model-specific limits such as Spark) are shown. */
const maxExtraBuckets = 6;

export function parseCodexSnapshot(result: unknown, account?: unknown): UsageSnapshot {
  const { key: mainKey, bucket: rateLimits } = selectCodexBucket(result);
  const extras = extraBuckets(result, mainKey);
  // The main bucket always keeps the plain "5-hour" / "Weekly" names, so its history and alert keys
  // never change when a model-specific bucket such as Spark appears or goes. Extra buckets read
  // "<label> 5-hour". Mirrors the desktop.
  const metrics: UsageMetric[] = [];
  addWindows(metrics, rateLimits, undefined, appServerFields);
  for (const { group, bucket } of extras) {
    addWindows(metrics, bucket, group, appServerFields);
  }
  addBalances(
    metrics,
    getString(getObject(rateLimits, "credits"), "balance"),
    getNumber(getObject(result, "rateLimitResetCredits"), "availableCount") ?? 0,
  );

  // The fresh rate-limit response knows the current plan best; the account only names who is signed in.
  const accountDetails = getObject(account, "account");
  const email = getString(accountDetails, "email");
  return {
    plan: formatPlan(getString(rateLimits, "planType") ?? getString(accountDetails, "planType") ?? "Codex"),
    metrics,
    fetchedAt: new Date().toISOString(),
    providerId: "codex",
    providerName: "Codex",
    ...(email && email.length <= 320 ? { accountName: email } : {}),
  };
}

function addBalances(metrics: UsageMetric[], balance: string | undefined, resetCredits: number): void {
  if (balance && balance.length <= 64) {
    metrics.push({
      name: "Credits",
      kind: "balance",
      usedPercent: null,
      remainingText: balance,
      usageText: "Available account balance",
    });
  }
  const resets = Math.trunc(resetCredits);
  if (resets > 0) {
    metrics.push({
      name: "Reset credits",
      kind: "balance",
      usedPercent: null,
      remainingText: String(resets),
      usageText: `Full reset${resets === 1 ? "" : "s"} available`,
    });
  }
}

/** Where a window's figures live: the app-server's names, or `wham/usage`'s. */
interface WindowFields {
  readonly primary: string;
  readonly secondary: string;
  readonly used: string;
  readonly reset: string;
  readonly duration: string;
  readonly durationUnitsPerMinute: number;
}

const appServerFields: WindowFields = {
  primary: "primary", secondary: "secondary", used: "usedPercent", reset: "resetsAt", duration: "windowDurationMins", durationUnitsPerMinute: 1,
};

const httpFields: WindowFields = {
  primary: "primary_window", secondary: "secondary_window", used: "used_percent", reset: "reset_at", duration: "limit_window_seconds", durationUnitsPerMinute: 60,
};

function hasWindow(bucket: Record<string, unknown>, fields: WindowFields): boolean {
  return Boolean(parseWindow(bucket, fields.primary, "Primary", fields) ?? parseWindow(bucket, fields.secondary, "Secondary", fields));
}

function addWindows(metrics: UsageMetric[], bucket: Record<string, unknown>, group: string | undefined, fields: WindowFields): void {
  const primary = parseWindow(bucket, fields.primary, "Primary", fields);
  const secondary = parseWindow(bucket, fields.secondary, "Secondary", fields);
  const { session, weekly } = classifyWindows(primary, secondary);
  for (const window of [session, weekly]) {
    if (window) {
      metrics.push(group ? { ...window, name: `${group} ${window.name}` } : window);
    }
  }
}

/**
 * The buckets besides the main one that report at least one window, each named by the label the
 * server gives it (`limitName`), else its model slug or a readable form of its id.
 */
function extraBuckets(
  result: unknown,
  mainKey: string | undefined,
): Array<{ readonly group: string; readonly bucket: Record<string, unknown> }> {
  const extras: Array<{ readonly group: string; readonly bucket: Record<string, unknown> }> = [];
  const buckets = getObject(result, "rateLimitsByLimitId");
  if (!buckets || mainKey === undefined) {
    return extras;
  }
  for (const [key, bucket] of Object.entries(buckets)) {
    if (extras.length >= maxExtraBuckets) {
      break;
    }
    if (key === mainKey || !isObject(bucket) || !hasWindow(bucket, appServerFields)) {
      continue;
    }
    const group = codexBucketLabel(bucket, key);
    if (!extras.some((existing) => existing.group.toLowerCase() === group.toLowerCase())) {
      extras.push({ group, bucket });
    }
  }
  return extras;
}

export function codexBucketLabel(bucket: unknown, limitId: string): string {
  return cleanLabel(
    getString(bucket, "limitName") ?? getString(bucket, "normalModelSlug"),
    limitId.replace(/[_-]/g, " ").trim().replace(/\b\w/g, (value) => value.toUpperCase()),
  );
}

/** A server-given label made safe for a metric name, where a parenthesis would read as the window. */
function cleanLabel(label: string | undefined, fallback: string): string {
  const clean = (value: string | undefined): string => (value ?? "").replace(/[\u0000-\u001f\u007f()]/g, "").trim();
  const cleaned = clean(label) || clean(fallback);
  if (!cleaned) {
    return "Other limit";
  }
  return cleaned.length > 60 ? cleaned.slice(0, 60).trimEnd() : cleaned;
}

function selectCodexBucket(result: unknown): { readonly key: string | undefined; readonly bucket: Record<string, unknown> } {
  const buckets = getObject(result, "rateLimitsByLimitId");
  if (buckets) {
    const codex = getObject(buckets, "codex");
    if (codex) {
      return { key: "codex", bucket: codex };
    }
    const first = Object.entries(buckets).find(([, value]) => isObject(value));
    if (first && isObject(first[1])) {
      return { key: first[0], bucket: first[1] };
    }
  }
  const historical = getObject(result, "rateLimits");
  if (historical) {
    return { key: undefined, bucket: historical };
  }
  throw new UsageProviderError("Codex returned no rate-limit windows. Sign in with `codex login`, then refresh.");
}

function parseWindow(
  rateLimits: Record<string, unknown>,
  propertyName: string,
  name: string,
  fields: WindowFields,
): UsageMetric | undefined {
  const window = getObject(rateLimits, propertyName);
  const used = getNumber(window, fields.used);
  if (!window || used === undefined || !Number.isFinite(used)) {
    return undefined;
  }
  const resetSeconds = getNumber(window, fields.reset);
  const durationUnits = getNumber(window, fields.duration);
  const durationMinutes = durationUnits !== undefined && durationUnits > 0
    ? Math.trunc(durationUnits / fields.durationUnitsPerMinute)
    : undefined;
  return {
    name,
    kind: "session",
    usedPercent: clampPercent(used),
    ...(resetSeconds !== undefined && resetSeconds > 0 && resetSeconds < 253_402_300_800
      ? { resetsAt: new Date(resetSeconds * 1000).toISOString() }
      : {}),
    ...(durationMinutes !== undefined ? { durationMinutes } : {}),
  };
}

function classifyWindows(
  primary: UsageMetric | undefined,
  secondary: UsageMetric | undefined,
): { readonly session?: UsageMetric; readonly weekly?: UsageMetric } {
  let session: UsageMetric | undefined;
  let weekly: UsageMetric | undefined;
  for (const window of [primary, secondary]) {
    if (!window) {
      continue;
    }
    if ((window.durationMinutes ?? 0) >= 1_440) {
      weekly ??= { ...window, name: formatWindowName(window.durationMinutes, "Weekly"), kind: "rolling" };
    } else {
      session ??= { ...window, name: formatWindowName(window.durationMinutes, "Session"), kind: "session" };
    }
  }
  if (!session && !weekly && primary) {
    session = { ...primary, name: "Session", kind: "session" };
    weekly = secondary ? { ...secondary, name: "Weekly", kind: "rolling" } : undefined;
  }
  return { ...(session ? { session } : {}), ...(weekly ? { weekly } : {}) };
}

function formatWindowName(durationMinutes: number | undefined, fallback: string): string {
  if (durationMinutes === undefined) {
    return fallback;
  }
  if (durationMinutes % 10_080 === 0) {
    const weeks = durationMinutes / 10_080;
    return weeks === 1 ? "Weekly" : `${weeks}-week`;
  }
  return durationMinutes % 60 === 0 ? `${durationMinutes / 60}-hour` : fallback;
}

function formatPlan(plan: string): string {
  const normalized = plan.replaceAll("_", " ").toLowerCase();
  const known: Record<string, string> = {
    plus: "Plus", pro: "Pro", prolite: "Pro Lite", team: "Team", business: "Business",
    enterprise: "Enterprise", edu: "Education", free: "Free",
  };
  return known[normalized] ?? normalized.replace(/\b\w/g, (value) => value.toUpperCase());
}

export async function findCodexLaunch(resolveExtensionPath: ExtensionPathResolver = noExtensions): Promise<CodexLaunch> {
  const configured = process.env.CODEX_PATH?.trim();
  let command: string | undefined;
  if (configured) {
    const validName = process.platform !== "win32" || /\.(?:exe|cmd)$/i.test(configured);
    if (!path.isAbsolute(configured) || !validName) {
      throw new UsageProviderError(
        process.platform === "win32"
          ? "CODEX_PATH must be an absolute path to codex.exe or codex.cmd."
          : "CODEX_PATH must be an absolute path to the Codex executable.",
      );
    }
    try {
      await access(configured);
      command = path.resolve(configured);
    } catch {
      throw new UsageProviderError("CODEX_PATH does not point to an existing Codex executable.");
    }
  } else {
    command = await findExecutable(process.platform === "win32" ? ["codex.exe", "codex.cmd"] : ["codex"]);
    if (!command && process.platform === "win32" && process.env.APPDATA) {
      const npmCommand = path.join(process.env.APPDATA, "npm", "codex.cmd");
      try {
        await access(npmCommand);
        command = npmCommand;
      } catch {
        // Report the normal not-found error below.
      }
    }
    // Last, so a standalone install keeps priority: the binary the Codex extension ships for itself.
    command ??= await findBundledExecutable(resolveExtensionPath, codexExtensionId, codexBundledPaths());
  }
  if (!command) {
    throw new UsageProviderError(resolveExtensionPath.runsLocallyInRemoteWindow
      // Codex may well be installed on the remote host, where this copy of UsageAI cannot look.
      ? "Codex CLI was not found on this computer. If Codex is installed on the remote host, set `remote.extensionKind` to run UsageAI there, then reload."
      : "Codex CLI was not found. Install Codex or the Codex extension, or set CODEX_PATH, then refresh.");
  }
  if (!command.toLowerCase().endsWith(".cmd")) {
    return { executable: command, args: [] };
  }

  const npmDirectory = path.dirname(command);
  const script = path.join(npmDirectory, "node_modules", "@openai", "codex", "bin", "codex.js");
  try {
    await access(script);
  } catch {
    throw new UsageProviderError("The Codex npm launcher is incomplete. Reinstall the Codex CLI, then refresh.");
  }
  const localNode = path.join(npmDirectory, "node.exe");
  let node: string | undefined;
  try {
    await access(localNode);
    node = localNode;
  } catch {
    node = await findExecutable(["node.exe"]);
  }
  if (!node) {
    throw new UsageProviderError("Node.js was not found, so the Codex npm launcher cannot start.");
  }
  return { executable: node, args: [script] };
}
