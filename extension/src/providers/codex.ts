import { access } from "node:fs/promises";
import * as path from "node:path";
import * as readline from "node:readline";
import { UsageProviderError } from "../errors";
import { clampPercent, type UsageClient, type UsageMetric, type UsageSnapshot } from "../model";
import { findExecutable, spawnSecure } from "../security";
import { getNumber, getObject, getString, isObject } from "./json";

const requestTimeoutMs = 20_000;
const maxProtocolLineCharacters = 131_072;
const maxProtocolMessages = 512;

interface CodexLaunch {
  readonly executable: string;
  readonly args: readonly string[];
}

export class CodexUsageClient implements UsageClient {
  public readonly id = "codex";
  public readonly displayName = "Codex";
  public readonly signInCommand = "codex login";
  public readonly accountUrl = "https://chatgpt.com/codex/settings/usage";

  public async getUsage(signal?: AbortSignal): Promise<UsageSnapshot> {
    const launch = await findCodexLaunch();
    const child = spawnSecure(
      launch.executable,
      [...launch.args, "app-server", "--listen", "stdio://"],
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
          clientInfo: { name: "usage-ai-vscode", title: "UsageAI", version: "0.1.13" },
          capabilities: { experimentalApi: true },
        },
      });
      await readResponse(iterator, 1);
      writeProtocolMessage(child.stdin, { method: "initialized" });
      writeProtocolMessage(child.stdin, { id: 2, method: "account/rateLimits/read", params: null });
      const result = await readResponse(iterator, 2);
      return parseCodexSnapshot(result);
    } catch (error) {
      if (signal?.aborted) {
        throw signal.reason;
      }
      if (/login|auth/i.test(stderr)) {
        throw new UsageProviderError("Codex is not signed in. Run `codex login`, then refresh.", 0, { cause: error });
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
          ? "Codex is not signed in. Run `codex login`, then refresh."
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

export function parseCodexSnapshot(result: unknown): UsageSnapshot {
  const rateLimits = selectCodexBucket(result);
  const primary = parseWindow(rateLimits, "primary", "Primary");
  const secondary = parseWindow(rateLimits, "secondary", "Secondary");
  const { session, weekly } = classifyWindows(primary, secondary);
  const metrics: UsageMetric[] = [];
  if (session) {
    metrics.push(session);
  }
  if (weekly) {
    metrics.push(weekly);
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

  return {
    plan: formatPlan(getString(rateLimits, "planType") ?? "Codex"),
    metrics,
    fetchedAt: new Date().toISOString(),
    providerId: "codex",
    providerName: "Codex",
  };
}

function selectCodexBucket(result: unknown): Record<string, unknown> {
  const buckets = getObject(result, "rateLimitsByLimitId");
  if (buckets) {
    const codex = getObject(buckets, "codex");
    if (codex) {
      return codex;
    }
    const first = Object.values(buckets).find(isObject);
    if (first) {
      return first;
    }
  }
  const historical = getObject(result, "rateLimits");
  if (historical) {
    return historical;
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

async function findCodexLaunch(): Promise<CodexLaunch> {
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
  }
  if (!command) {
    throw new UsageProviderError("Codex CLI was not found. Install Codex or set CODEX_PATH, then refresh.");
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
