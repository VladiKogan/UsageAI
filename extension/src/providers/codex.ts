import { access } from "node:fs/promises";
import * as path from "node:path";
import * as readline from "node:readline";
import { codexBundledPaths, findBundledExecutable, noExtensions, type ExtensionPathResolver } from "../bundled-cli";
import { UsageProviderError } from "../errors";
import { clampPercent, type UsageClient, type UsageMetric, type UsageSnapshot } from "../model";
import { findExecutable, spawnSecure } from "../security";
import { getNumber, getObject, getString, isObject } from "./json";

const requestTimeoutMs = 20_000;
const maxProtocolLineCharacters = 131_072;
const maxProtocolMessages = 512;
const codexExtensionId = "openai.chatgpt";

export interface CodexLaunch {
  readonly executable: string;
  readonly args: readonly string[];
}

export class CodexUsageClient implements UsageClient {
  public readonly id = "codex";
  public readonly displayName = "Codex";
  public readonly signInCommand = "codex login";
  public readonly accountUrl = "https://chatgpt.com/codex/settings/usage";

  /** `resolveExtensionPath` lets the CLI bundled in OpenAI's Codex extension serve as a last resort. */
  public constructor(private readonly resolveExtensionPath: ExtensionPathResolver = noExtensions) {}

  public async getUsage(signal?: AbortSignal): Promise<UsageSnapshot> {
    const launch = await findCodexLaunch(this.resolveExtensionPath);
    const child = spawnSecure(
      launch.executable,
      [...launch.args, ...codexAppServerArguments],
      ["CODEX_HOME", "NODE_EXTRA_CA_CERTS", "SSL_CERT_DIR", "SSL_CERT_FILE"],
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
          clientInfo: { name: "usage-ai-vscode", title: "UsageAI", version: "0.1.18" },
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
 * The arguments ahead of the launch's own: a read-only sandbox and no approval prompts, so the
 * server UsageAI starts can never be asked to run or approve anything. Mirrors the desktop.
 */
export const codexAppServerArguments: readonly string[] = ["-s", "read-only", "-a", "never", "app-server", "--listen", "stdio://"];

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
  // With model-specific buckets such as Spark, every bucket becomes a named group, so the dashboard
  // shows Codex and Spark as separate groups. An account with only the main bucket keeps the plain
  // "5-hour" / "Weekly" names.
  const metrics: UsageMetric[] = [];
  addWindows(metrics, rateLimits, extras.length > 0 ? "Codex" : undefined);
  for (const { group, bucket } of extras) {
    addWindows(metrics, bucket, group);
  }

  const credits = getObject(rateLimits, "credits");
  const balance = getString(credits, "balance");
  if (balance) {
    metrics.push({
      name: "Credits",
      kind: "balance",
      usedPercent: null,
      remainingText: balance,
      usageText: "Available account balance",
    });
  }

  const resetCredits = getNumber(getObject(result, "rateLimitResetCredits"), "availableCount") ?? 0;
  if (resetCredits > 0) {
    metrics.push({
      name: "Reset credits",
      kind: "balance",
      usedPercent: null,
      remainingText: String(Math.trunc(resetCredits)),
      usageText: `Full reset${resetCredits === 1 ? "" : "s"} available`,
    });
  }

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

function addWindows(metrics: UsageMetric[], bucket: Record<string, unknown>, group: string | undefined): void {
  const primary = parseWindow(bucket, "primary", "Primary");
  const secondary = parseWindow(bucket, "secondary", "Secondary");
  const { session, weekly } = classifyWindows(primary, secondary);
  for (const window of [session, weekly]) {
    if (window) {
      metrics.push(group ? { ...window, name: `${group} (${window.name})` } : window);
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
    if (key === mainKey || !isObject(bucket) || (!parseWindow(bucket, "primary", "Primary") && !parseWindow(bucket, "secondary", "Secondary"))) {
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
  let label = getString(bucket, "limitName") ?? getString(bucket, "normalModelSlug");
  if (!label?.trim()) {
    label = limitId.replace(/[_-]/g, " ").trim().replace(/\b\w/g, (value) => value.toUpperCase());
  }
  // A label ends up in a metric name, where a parenthesis would read as the window.
  label =label.replace(/[\u0000-\u001f\u007f()]/g, "").trim();
  if (!label) {
    return "Other limit";
  }
  return label.length > 60 ? label.slice(0, 60).trimEnd() : label;
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
): UsageMetric | undefined {
  const window = getObject(rateLimits, propertyName);
  const used = getNumber(window, "usedPercent");
  if (!window || used === undefined) {
    return undefined;
  }
  const resetSeconds = getNumber(window, "resetsAt");
  const durationMinutes = getNumber(window, "windowDurationMins");
  return {
    name,
    kind: "session",
    usedPercent: clampPercent(used),
    ...(resetSeconds !== undefined ? { resetsAt: new Date(resetSeconds * 1000).toISOString() } : {}),
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
