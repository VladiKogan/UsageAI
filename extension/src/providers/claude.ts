import { access, readdir, realpath, stat } from "node:fs/promises";
import * as path from "node:path";
import { commandWithEnvironment, defaultLoginFolder, hasClaudeApiKeyLogin, mainSignInCommand } from "../accounts";
import { claudeBundledPaths, findBundledExecutable, noExtensions, type ExtensionPathResolver } from "../bundled-cli";
import { UsageProviderError } from "../errors";
import { clampPercent, type UsageClient, type UsageMetric, type UsageSnapshot } from "../model";
import type * as http from "node:http";
import {
  collectProcessOutput,
  findExecutable,
  NonJsonResponseError,
  normalizeToken,
  parseRetryAfter,
  readBoundedText,
  requestJson,
  spawnSecure,
  userHome,
} from "../security";
import { getArray, getBoolean, getNumber, getObject, getString, isObject, parseDate } from "./json";

const usageEndpoint = "https://api.anthropic.com/api/oauth/usage";
/** The usage endpoint asked to include saved limit-reset credits, as claude.ai asks. */
export const usageWithResetCreditsEndpoint = `${usageEndpoint}?cedar_ember=1`;
/** The headline shown when Claude reports no metered limit for the account's plan. */
export const noLimitsReportedText = "Claude reports no usage limits for this plan";
/** How long to leave claude.ai alone after its Cloudflare browser check. */
const cloudflareRetryAfterMs = 15 * 60_000;
const maxModelWeeklyLimits = 6;
const maxResetGrants = 50;
const oauthBeta = "oauth-2025-04-20";
const authProbeTimeoutMs = 12_000;
const credentialObservationMs = 5_000;
const refreshNudgeTimeoutMs = 20_000;
const claudeExtensionId = "anthropic.claude-code";

export interface ClaudeCredentials {
  readonly accessToken: string;
  readonly expiresAt?: number;
  readonly scopes: readonly string[];
  readonly plan: string;
  readonly isEnvironmentOverride?: boolean;
}

export type ClaudeAuthProbe = (signal?: AbortSignal) => Promise<boolean>;

export interface ClaudeLaunch {
  readonly executable: string;
  readonly argsPrefix: readonly string[];
}

/** A Claude login other than the default one: its own config folder, read directly. */
export interface ClaudeAccount {
  /** The account id; the client's id becomes the unit key `claude@<id>`. */
  readonly id: string;
  /** The folder holding that login's `.credentials.json`, used as its `CLAUDE_CONFIG_DIR`. */
  readonly configDirectory: string;
}

export class ClaudeUsageClient implements UsageClient {
  public readonly id: string;
  public readonly displayName = "Claude Code";
  public readonly signInCommand: string;
  public readonly accountUrl = "https://claude.ai/settings/usage";

  /** Until when the opt-in web path is skipped after claude.ai asked for a browser check. */
  private webRetryAfter = 0;

  /**
   * Without `account` this is the default login, exactly as before. With one, only that folder's
   * `.credentials.json` is read: the environment token override, the web session key and any other
   * fallback belong to the default login alone, and the CLI recovery runs with that folder.
   */
  public constructor(
    private readonly claudeAuthProbe: ClaudeAuthProbe = runClaudeAuthRecovery,
    private readonly account?: ClaudeAccount,
    private readonly claudeCliVersion: () => Promise<string | undefined> = () => installedClaudeCliVersion(),
  ) {
    this.id = account ? `claude@${account.id}` : "claude";
    this.signInCommand = account
      ? commandWithEnvironment("CLAUDE_CONFIG_DIR", account.configDirectory, "claude auth login --claudeai")
      : mainSignInCommand("claude");
  }

  public async getUsage(signal?: AbortSignal): Promise<UsageSnapshot> {
    if (!this.account && getClaudeSessionKey() && Date.now() >= this.webRetryAfter) {
      try {
        return await getClaudeWebUsage(signal);
      } catch (error) {
        // The explicit web session is a fallback source; continue with Claude Code OAuth. After a
        // Cloudflare browser check claude.ai is left alone for a while; the session key is kept.
        if (error instanceof UsageProviderError && error.retryAfterMs > 0) {
          this.webRetryAfter = Date.now() + error.retryAfterMs;
        }
      }
    }

    let credentials = await this.loadCredentialsOrExplainApiKeyLogin();
    credentials = await refreshExpiredClaudeCredentials(
      credentials,
      () => this.loadCredentials(),
      this.claudeAuthProbe,
      signal,
    );
    assertClaudeCredentialsUsable(credentials);
    if (credentials.scopes.length > 0 && !credentials.scopes.includes("user:profile")) {
      throw new UsageProviderError(
        "Claude Code's OAuth token is missing the user:profile scope. Run `claude` or use the Claude Code extension to sign in again.",
      );
    }

    try {
      return await requestClaudeUsage(credentials, this.claudeCliVersion, signal);
    } catch (error) {
      if (!(error instanceof RecoverableClaudeUsageError) || credentials.isEnvironmentOverride) {
        throw error;
      }

      const recovered = await recoverClaudeCredentialsThroughCli(
        () => this.loadCredentials(),
        this.claudeAuthProbe,
        signal,
      );
      if (!recovered) {
        throw error;
      }

      assertClaudeCredentialsUsable(recovered);
      if (recovered.scopes.length > 0 && !recovered.scopes.includes("user:profile")) {
        throw new UsageProviderError(
          "Claude Code's OAuth token is missing the user:profile scope. Run `claude` or use the Claude Code extension to sign in again.",
        );
      }
      return requestClaudeUsage(recovered, this.claudeCliVersion, signal);
    }
  }

  /**
   * `loadCredentials`, except that a login switched to an API key (Console billing) says so: signing
   * in that way removes the subscription login UsageAI reads, which otherwise looks like an
   * unexplained sign-out. Not used by the recovery loop, which reloads every 100 ms.
   */
  private async loadCredentialsOrExplainApiKeyLogin(): Promise<ClaudeCredentials> {
    try {
      return await this.loadCredentials();
    } catch (error) {
      if (await hasClaudeApiKeyLogin(this.account?.configDirectory ?? defaultLoginFolder("claude"))) {
        throw new UsageProviderError(claudeApiKeyLoginMessage(this.account?.configDirectory), 0, { cause: error });
      }
      throw error;
    }
  }

  private async loadCredentials(): Promise<ClaudeCredentials> {
    if (this.account) {
      return readClaudeCredentialFile(claudeCredentialPath(this.account.configDirectory));
    }
    const environmentToken = normalizeToken(process.env.USAGEAI_CLAUDE_OAUTH_TOKEN);
    if (environmentToken) {
      return {
        accessToken: environmentToken,
        scopes: (process.env.USAGEAI_CLAUDE_OAUTH_SCOPES ?? "user:profile")
          .split(",")
          .map((scope) => scope.trim())
          .filter((scope) => scope.length > 0 && scope.length <= 128)
          .slice(0, 32),
        plan: "Claude (OAuth)",
        isEnvironmentOverride: true,
      };
    }

    return readClaudeCredentialFile(claudeCredentialPath());
  }
}

export function claudeApiKeyLoginMessage(configDirectory?: string): string {
  return configDirectory
    ? `Claude Code at ${configDirectory} is signed in with an API key (Console billing) instead of a Claude subscription, so there are no plan limits to show. Sign it in with your subscription (\`claude auth login --claudeai\` with its CLAUDE_CONFIG_DIR), then refresh.`
    : "Claude Code is signed in with an API key (Console billing) instead of a Claude subscription, so there are no plan limits to show. Sign in with your subscription (`claude auth login --claudeai`), then refresh.";
}

async function readClaudeCredentialFile(credentialPath: string): Promise<ClaudeCredentials> {
  try {
    return parseClaudeCredentials(await readBoundedText(credentialPath));
  } catch (error) {
    throw new UsageProviderError(
      "Claude Code is not signed in, or its saved login could not be read. Run `claude` or sign in through the Claude Code extension, then refresh.",
      0,
      { cause: error },
    );
  }
}

export async function refreshExpiredClaudeCredentials(
  credentials: ClaudeCredentials,
  reloadCredentials: () => Promise<ClaudeCredentials>,
  authProbe: ClaudeAuthProbe,
  signal?: AbortSignal,
): Promise<ClaudeCredentials> {
  if (credentials.expiresAt === undefined || credentials.expiresAt > Date.now()) {
    return credentials;
  }

  return await recoverClaudeCredentialsThroughCli(
    reloadCredentials,
    authProbe,
    signal,
  ) ?? credentials;
}

export async function recoverClaudeCredentialsThroughCli(
  reloadCredentials: () => Promise<ClaudeCredentials>,
  authProbe: ClaudeAuthProbe,
  signal?: AbortSignal,
): Promise<ClaudeCredentials | undefined> {
  let claudeOwnsFreshLogin = false;
  try {
    claudeOwnsFreshLogin = await authProbe(signal);
  } catch (error) {
    if (signal?.aborted) {
      throw error;
    }
    return undefined;
  }
  if (!claudeOwnsFreshLogin) {
    return undefined;
  }

  const deadline = Date.now() + credentialObservationMs;
  do {
    if (signal?.aborted) {
      throw signal.reason instanceof Error ? signal.reason : new Error("Operation cancelled.");
    }
    try {
      const refreshed = await reloadCredentials();
      if (refreshed.expiresAt === undefined || refreshed.expiresAt > Date.now()) {
        return refreshed;
      }
    } catch {
      // Claude may be replacing its credential store; retry only inside the bounded window.
    }
    await abortableDelay(100, signal);
  } while (Date.now() < deadline);

  return undefined;
}

async function requestClaudeUsage(
  credentials: ClaudeCredentials,
  claudeCliVersion: () => Promise<string | undefined>,
  signal?: AbortSignal,
): Promise<UsageSnapshot> {
  try {
    const request = (url: string, userAgent?: string) => requestJson(url, {
      allowedHosts: ["api.anthropic.com"],
      headers: {
        Authorization: `Bearer ${credentials.accessToken}`,
        "anthropic-beta": oauthBeta,
        ...(userAgent ? { "User-Agent": userAgent } : {}),
      },
      ...(signal ? { signal } : {}),
    });
    // Saved limit resets are reported only when asked for, and only to the Claude CLI, so this one
    // request carries the CLI's agent. A refusal of it (400, 401 or 403) is asked once more without
    // the parameter, as UsageAI, so usage never depends on either; an expired login is refused again
    // there and handled below as before. Mirrors the desktop.
    let response = await request(usageWithResetCreditsEndpoint, claudeCliUserAgent(await claudeCliVersion().catch(() => undefined)));
    if (response.status === 400 || response.status === 401 || response.status === 403) {
      response = await request(usageEndpoint);
    }
    if (response.status === 401) {
      throw new RecoverableClaudeUsageError(
        "Claude Code's login has expired. Run `claude` or use the Claude Code extension to sign in again, then refresh.",
      );
    }
    if (response.status === 403) {
      throw new RecoverableClaudeUsageError(
        "Claude Code's login cannot read account usage. Run `claude` or use the Claude Code extension to sign in again.",
      );
    }
    if (response.status === 429) {
      throw new UsageProviderError(
        "Anthropic rate-limited the usage request.",
        parseRetryAfter(response.headers),
      );
    }
    if (response.status < 200 || response.status >= 300) {
      throw new UsageProviderError(`Claude Code returned HTTP ${response.status} while reading usage.`);
    }
    return parseClaudeSnapshot(response.data, credentials.plan);
  } catch (error) {
    if (error instanceof UsageProviderError) {
      throw error;
    }
    throw new RecoverableClaudeUsageError(
      "Could not read Claude Code usage because the provider returned invalid or unavailable data.",
      { cause: error },
    );
  }
}

class RecoverableClaudeUsageError extends UsageProviderError {
  public constructor(message: string, options?: ErrorOptions) {
    super(message, 0, options);
    this.name = "RecoverableClaudeUsageError";
  }
}

/**
 * The credential file of `configDirectory`, else of the main login in the standard `~/.claude`. An
 * inherited `CLAUDE_CONFIG_DIR` never moves the main login; that folder is found as an account.
 */
function claudeCredentialPath(configDirectory?: string): string {
  return path.join(configDirectory || defaultLoginFolder("claude"), ".credentials.json");
}

async function hasFreshStoredClaudeCredential(configDirectory?: string): Promise<boolean> {
  try {
    const credentials = parseClaudeCredentials(await readBoundedText(claudeCredentialPath(configDirectory)));
    return credentials.expiresAt === undefined || credentials.expiresAt > Date.now();
  } catch {
    return false;
  }
}

/**
 * Asks the official CLI to validate, and if needed refresh, Claude Code's login. `auth status`
 * only reports whether a login exists and never refreshes an expired access token, so while the
 * stored token is still expired a command that needs a live token (`mcp list`) makes the CLI
 * refresh it through its own credential store. UsageAI never touches the refresh token itself.
 * With `configDirectory` (an account other than the default) both children get exactly that
 * `CLAUDE_CONFIG_DIR`, set explicitly rather than inherited.
 */
export async function runClaudeAuthRecovery(
  signal?: AbortSignal,
  resolveExtensionPath: ExtensionPathResolver = noExtensions,
  configDirectory?: string,
): Promise<boolean> {
  // Resolved once: both commands below run the same CLI.
  const launch = await findClaudeLaunch(resolveExtensionPath);
  if (!launch || !await claudeAuthStatus(launch, signal, configDirectory)) {
    return false;
  }
  if (!await hasFreshStoredClaudeCredential(configDirectory)) {
    await nudgeClaudeCredentialRefresh(launch, signal, configDirectory);
  }
  return true;
}

async function nudgeClaudeCredentialRefresh(
  launch: ClaudeLaunch,
  signal: AbortSignal | undefined,
  configDirectory: string | undefined,
): Promise<void> {
  const child = spawnClaudeCli(launch, ["mcp", "list"], configDirectory);
  child.stdin.end();
  // Output is discarded; the child keeps running its MCP health checks after the refresh lands
  // and exits on its own, with this bound as the backstop.
  const exited = collectProcessOutput(child, refreshNudgeTimeoutMs, 16_384).then(() => true, () => true);
  const abort = () => child.kill();
  signal?.addEventListener("abort", abort, { once: true });
  try {
    const deadline = Date.now() + refreshNudgeTimeoutMs;
    while (Date.now() < deadline && !await hasFreshStoredClaudeCredential(configDirectory)) {
      if (await Promise.race([exited, abortableDelay(100, signal).then(() => false)])) {
        return;
      }
    }
  } finally {
    signal?.removeEventListener("abort", abort);
  }
}

/**
 * The environment a Claude CLI child gets: an account's folder set explicitly, and for the main
 * login no `CLAUDE_CONFIG_DIR` at all, so the child uses the standard `~/.claude` UsageAI reads.
 * Never inherited: an inherited one would point the child at another folder, and setting it to
 * `~/.claude` would move the CLI's profile from `~/.claude.json` into the folder.
 */
export function claudeCliEnvironment(configDirectory?: string): {
  readonly inherited: readonly string[];
  readonly overrides: NodeJS.ProcessEnv;
} {
  return {
    inherited: [
      "CLAUDE_CODE_GIT_BASH_PATH",
      "NODE_EXTRA_CA_CERTS",
      "SSL_CERT_DIR",
      "SSL_CERT_FILE",
    ],
    overrides: { DISABLE_AUTOUPDATER: "1", ...(configDirectory ? { CLAUDE_CONFIG_DIR: configDirectory } : {}) },
  };
}

function spawnClaudeCli(launch: ClaudeLaunch, args: readonly string[], configDirectory?: string) {
  const environment = claudeCliEnvironment(configDirectory);
  return spawnSecure(launch.executable, [...launch.argsPrefix, ...args], environment.inherited, environment.overrides);
}

export async function runClaudeAuthStatus(
  signal?: AbortSignal,
  resolveExtensionPath: ExtensionPathResolver = noExtensions,
): Promise<boolean> {
  const launch = await findClaudeLaunch(resolveExtensionPath);
  return launch ? claudeAuthStatus(launch, signal) : false;
}

async function claudeAuthStatus(
  launch: ClaudeLaunch,
  signal: AbortSignal | undefined,
  configDirectory?: string,
): Promise<boolean> {
  const child = spawnClaudeCli(launch, ["auth", "status", "--json"], configDirectory);
  child.stdin.end();
  const abort = () => child.kill();
  signal?.addEventListener("abort", abort, { once: true });
  try {
    const result = await collectProcessOutput(child, authProbeTimeoutMs, 16_384);
    if (signal?.aborted) {
      throw signal.reason instanceof Error ? signal.reason : new Error("Operation cancelled.");
    }
    if (result.exitCode !== 0) {
      return false;
    }
    const status = JSON.parse(result.stdout) as unknown;
    return getBoolean(status, "loggedIn");
  } catch (error) {
    if (signal?.aborted) {
      throw error;
    }
    return false;
  } finally {
    signal?.removeEventListener("abort", abort);
    if (child.exitCode === null) {
      child.kill();
    }
  }
}

export async function findClaudeLaunch(
  resolveExtensionPath: ExtensionPathResolver = noExtensions,
): Promise<ClaudeLaunch | undefined> {
  const configured = process.env.CLAUDE_PATH?.trim().replace(/^"|"$/g, "");
  const candidates: string[] = [];
  if (configured && path.isAbsolute(configured)) {
    candidates.push(path.resolve(configured));
  }

  const fromPath = await findExecutable(
    process.platform === "win32" ? ["claude.exe", "claude.cmd"] : ["claude"],
  );
  if (fromPath) {
    candidates.push(fromPath);
  }

  if (process.platform === "win32") {
    candidates.push(
      path.join(userHome(), ".local", "bin", "claude.exe"),
      path.join(process.env.APPDATA ?? "", "npm", "claude.cmd"),
      path.join(process.env.LOCALAPPDATA ?? "", "Programs", "claude", "claude.exe"),
    );
  } else {
    candidates.push(
      path.join(userHome(), ".local", "bin", "claude"),
      "/opt/homebrew/bin/claude",
      "/usr/local/bin/claude",
    );
  }
  for (const candidate of [...new Set(candidates)]) {
    if (!candidate || !path.isAbsolute(candidate)) {
      continue;
    }
    try {
      await access(candidate);
    } catch {
      continue;
    }
    const launch = await createClaudeLaunch(candidate);
    if (launch) {
      return launch;
    }
  }
  // Last, and only when nothing above worked, so a standalone install keeps priority: the binary the
  // Claude Code extension ships for itself. findBundledExecutable has already checked that it exists.
  const bundled = await findBundledExecutable(resolveExtensionPath, claudeExtensionId, claudeBundledPaths());
  return bundled ? createClaudeLaunch(bundled) : undefined;
}

/** The CLI version sent when the installed one cannot be read. */
export const fallbackClaudeCliVersion = "2.1.0";
const cliVersionLifetimeMs = 30 * 60_000;
let cachedCliVersion: { readonly readAt: number; readonly version: Promise<string | undefined> } | undefined;

function isCliVersion(version: string | undefined): version is string {
  return version !== undefined && version.length <= 32 && /^\d{1,9}\.\d{1,9}\.\d{1,9}$/.test(version);
}

/**
 * The User-Agent of the request that asks for saved resets. Claude reports them only to its own CLI
 * (`ineligible_reason: "surface"` otherwise), so that one request names itself as the CLI does, with
 * the installed version; every other request keeps UsageAI's own agent. Mirrors the desktop.
 */
export function claudeCliUserAgent(version: string | undefined): string {
  return `claude-cli/${isCliVersion(version) ? version : fallbackClaudeCliVersion} (external, cli)`;
}

export function forgetInstalledClaudeCliVersion(): void {
  cachedCliVersion = undefined;
}

/**
 * The installed Claude CLI's version, read from its files and never by running it: npm's
 * `package.json`, the version folder a native install links to or copies, or the version in the
 * Claude Code editor extension's folder name. Kept for 30 minutes, and the read in progress is
 * shared, so accounts refreshing together look once. Never rejects.
 */
export function installedClaudeCliVersion(
  resolveExtensionPath: ExtensionPathResolver = noExtensions,
): Promise<string | undefined> {
  if (!cachedCliVersion || Date.now() - cachedCliVersion.readAt >= cliVersionLifetimeMs) {
    cachedCliVersion = {
      readAt: Date.now(),
      version: findClaudeLaunch(resolveExtensionPath)
        .then((launch) => launch ? readClaudeCliVersion(launch) : undefined)
        .catch(() => undefined),
    };
  }
  return cachedCliVersion.version;
}

/** "2.1.321" from "2.1.321" or "2.1.321-beta.1"; undefined for anything else. */
function cliVersionOf(raw: unknown): string | undefined {
  const core = typeof raw === "string" ? raw.split(/[-+]/)[0] : undefined;
  return isCliVersion(core) ? core : undefined;
}

async function packageVersion(folder: string): Promise<string | undefined> {
  return cliVersionOf(getString(JSON.parse(await readBoundedText(path.join(folder, "package.json"))), "version"));
}

async function readClaudeCliVersion(launch: ClaudeLaunch): Promise<string | undefined> {
  const script = launch.argsPrefix[0];
  if (script) {
    return packageVersion(path.dirname(script));
  }
  const resolved = await realpath(launch.executable).catch(() => launch.executable);
  // An npm install outside Windows is a link straight to the package's cli.js.
  if (path.extname(resolved).toLowerCase() === ".js") {
    return packageVersion(path.dirname(resolved));
  }
  const linked = /[\\/]versions[\\/](\d+\.\d+\.\d+)(?:[\\/]|$)/.exec(resolved)?.[1]
    ?? /anthropic\.claude-code-(\d+\.\d+\.\d+)/i.exec(resolved)?.[1];
  if (linked) {
    return cliVersionOf(linked);
  }
  // The Windows native install copies the version it runs into ~/.local/bin, keeping that copy and
  // earlier ones in ~/.local/share/claude/versions/<x.y.z>. The kept copy with the same size and
  // modification time is the one installed; with no such copy nothing is guessed.
  // Both sides resolved alike, so a home reached through a short (8.3) or linked path still matches.
  const localBin = await realpath(path.join(userHome(), ".local", "bin")).catch(() => path.join(userHome(), ".local", "bin"));
  if (path.dirname(resolved).toLowerCase() !== localBin.toLowerCase()) {
    return undefined;
  }
  const installed = await stat(resolved);
  const versions = path.join(userHome(), ".local", "share", "claude", "versions");
  for (const kept of (await readdir(versions).catch((): string[] => [])).filter(isCliVersion)) {
    const copy = await stat(path.join(versions, kept)).catch(() => undefined);
    if (copy?.isFile() && copy.size === installed.size && Math.abs(copy.mtimeMs - installed.mtimeMs) < 2_000) {
      return kept;
    }
  }
  return undefined;
}

async function createClaudeLaunch(command: string): Promise<ClaudeLaunch | undefined> {
  if (process.platform !== "win32" || path.extname(command).toLowerCase() !== ".cmd") {
    return { executable: command, argsPrefix: [] };
  }

  const npmDirectory = path.dirname(command);
  const script = path.join(npmDirectory, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
  try {
    await access(script);
  } catch {
    return undefined;
  }
  const localNode = path.join(npmDirectory, "node.exe");
  let node: string | undefined;
  try {
    await access(localNode);
    node = localNode;
  } catch {
    node = await findExecutable(["node.exe"]);
  }
  return node ? { executable: node, argsPrefix: [script] } : undefined;
}

function abortableDelay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason instanceof Error ? signal.reason : new Error("Operation cancelled."));
      return;
    }
    const abort = () => {
      clearTimeout(timer);
      reject(signal?.reason instanceof Error ? signal.reason : new Error("Operation cancelled."));
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", abort);
      resolve();
    }, milliseconds);
    timer.unref();
    signal?.addEventListener("abort", abort, { once: true });
  });
}

export function assertClaudeCredentialsUsable(credentials: { readonly expiresAt?: number }): void {
  if (credentials.expiresAt !== undefined && credentials.expiresAt <= Date.now()) {
    throw new UsageProviderError(
      "Claude Code's access token expired and its CLI could not refresh the login. Open `claude`, then refresh UsageAI.",
    );
  }
}

export function parseClaudeCredentials(rawJson: string): ClaudeCredentials {
  const root = JSON.parse(rawJson) as unknown;
  const oauth = getObject(root, "claudeAiOauth") ?? (isObject(root) ? root : undefined);
  const accessToken = normalizeToken(getString(oauth, "accessToken"));
  if (!accessToken) {
    throw new UsageProviderError("Claude Code's saved login has no OAuth access token.");
  }
  const scopes = (getArray(oauth, "scopes") ?? [])
    .filter((scope): scope is string => typeof scope === "string")
    .map((scope) => scope.trim())
    .filter((scope) => scope.length > 0 && scope.length <= 128)
    .slice(0, 32);
  const expiresAt = getNumber(oauth, "expiresAt");
  return {
    accessToken,
    ...(expiresAt !== undefined ? { expiresAt } : {}),
    scopes,
    plan: formatClaudePlan(getString(oauth, "subscriptionType") ?? getString(oauth, "rateLimitTier")),
  };
}

export function parseClaudeSnapshot(root: unknown, plan = "Claude", now = Date.now()): UsageSnapshot {
  const session = parseWindow(root, "five_hour", "fiveHour", "5-hour", "session", 300);
  const weekly = findLimit(root, ["weekly_all", "all_models", "weekly_models"], "Weekly", true)
    ?? parseWindow(root, "seven_day", "sevenDay", "Weekly", "rolling", 10_080);
  const metrics: UsageMetric[] = [];
  if (!session && !weekly) {
    // Free and some education plans answer with both windows set to null: there is no limit to
    // meter, which is not a failure and must not read as 0% used.
    if (!isObject(root) || root.five_hour !== null || root.seven_day !== null) {
      throw new UsageProviderError("Claude Code returned account details without five-hour or weekly usage data.");
    }
    metrics.push({ name: "Usage limits", kind: "balance", usedPercent: null, remainingText: "Not reported", usageText: noLimitsReportedText });
  }
  if (session) {
    metrics.push(session);
  }
  if (weekly) {
    metrics.push(weekly);
  }
  metrics.push(...parseModelWeeklyLimits(root));
  const extraUsage = getObject(root, "extra_usage") ?? getObject(root, "extraUsage");
  const extra = extraUsage ? createExtraUsageMetric(extraUsage) : undefined;
  if (extra) {
    metrics.push(extra);
  }
  metrics.push(...parseResetGrants(root, now));
  return {
    plan,
    metrics,
    fetchedAt: new Date().toISOString(),
    providerId: "claude",
    providerName: "Claude Code",
  };
}

function parseWindow(
  root: unknown,
  snakeCaseName: string,
  camelCaseName: string,
  displayName: string,
  kind: "session" | "rolling",
  durationMinutes: number,
): UsageMetric | undefined {
  const window = getObject(root, snakeCaseName) ?? getObject(root, camelCaseName);
  const utilization = getNumber(window, "utilization");
  if (!window || utilization === undefined) {
    return undefined;
  }
  const resetsAt = parseDate(getString(window, "resets_at") ?? getString(window, "resetsAt"));
  return {
    name: displayName,
    kind,
    // Anthropic reports utilization as a percentage (0-100), never a fraction: 1.0 means 1% used.
    usedPercent: clampPercent(utilization),
    ...(resetsAt ? { resetsAt } : {}),
    durationMinutes,
  };
}

function findLimit(
  root: unknown,
  kinds: readonly string[],
  displayName: string,
  requireWeeklyGroup: boolean,
): UsageMetric | undefined {
  for (const value of getArray(root, "limits") ?? []) {
    if (!isObject(value) || !kinds.includes(getString(value, "kind") ?? "")) {
      continue;
    }
    const group = getString(value, "group");
    if (requireWeeklyGroup && group && group.toLowerCase() !== "weekly") {
      continue;
    }
    const percent = getNumber(value, "percent");
    if (percent === undefined) {
      continue;
    }
    const resetsAt = parseDate(getString(value, "resets_at") ?? getString(value, "resetsAt"));
    return {
      name: displayName,
      kind: "rolling",
      usedPercent: clampPercent(percent),
      ...(resetsAt ? { resetsAt } : {}),
      durationMinutes: 10_080,
    };
  }
  return undefined;
}

/**
 * Weekly limits that apply to one model, named "Weekly Opus 5" and so on. The newer `limits` entries
 * carry the model in `scope.model`; older answers report `seven_day_opus` / `seven_day_sonnet` and a
 * `weekly_opus` limit kind, which are read only when no scoped entry has that name. The all-models
 * limit stays the main weekly row. Mirrors the desktop `ParseModelWeeklyLimits`.
 */
export function parseModelWeeklyLimits(root: unknown): UsageMetric[] {
  const found: UsageMetric[] = [];
  const add = (metric: UsageMetric | undefined): void => {
    if (metric && found.length < maxModelWeeklyLimits &&
        !found.some((existing) => existing.name.toLowerCase() === metric.name.toLowerCase())) {
      found.push(metric);
    }
  };
  for (const limit of getArray(root, "limits") ?? []) {
    const kind = getString(limit, "kind");
    if (!isObject(limit) || ["weekly_all", "all_models", "weekly_models", "session"].includes(kind ?? "") ||
        (getString(limit, "group") ?? "weekly").toLowerCase() !== "weekly") {
      continue;
    }
    const model = getObject(getObject(limit, "scope"), "model");
    const label = modelLabel(getString(model, "display_name") ?? getString(model, "displayName") ?? getString(model, "id"));
    const percent = getNumber(limit, "percent");
    if (!label || percent === undefined || !Number.isFinite(percent)) {
      continue;
    }
    const resetsAt = parseDate(getString(limit, "resets_at") ?? getString(limit, "resetsAt"));
    add({ name: `Weekly ${label}`, kind: "rolling", usedPercent: clampPercent(percent), ...(resetsAt ? { resetsAt } : {}), durationMinutes: 10_080 });
  }
  // The legacy fields describe the same limits as the scoped entries, so they fill in only a model
  // family no scoped entry has named ("Weekly Opus 4.5" already covers "Weekly Opus").
  const named = (family: string): boolean => found.some((metric) => metric.name.toLowerCase().includes(family));
  if (!named("opus")) {
    add(findLimit(root, ["weekly_opus"], "Weekly Opus", false));
    add(parseWindow(root, "seven_day_opus", "sevenDayOpus", "Weekly Opus", "rolling", 10_080));
  }
  if (!named("sonnet")) {
    add(parseWindow(root, "seven_day_sonnet", "sevenDaySonnet", "Weekly Sonnet", "rolling", 10_080));
  }
  return found;
}

/** A model name made short and safe for a metric name: "Claude Opus 5" reads "Opus 5". */
function modelLabel(name: string | undefined): string | undefined {
  let cleaned = (name ?? "").replace(/[\u0000-\u001f\u007f()]/g, "").trim();
  if (cleaned.toLowerCase().startsWith("claude ")) {
    cleaned = cleaned.slice("claude ".length).trim();
  }
  if (!cleaned) {
    return undefined;
  }
  return cleaned.length > 40 ? cleaned.slice(0, 40).trimEnd() : cleaned;
}

/** The kinds of saved reset, named as the Claude CLI names them, in display order. */
const resetKinds: readonly { readonly kind: string; readonly name: string; readonly effect: string }[] = [
  { kind: "fable", name: "Fable reset", effect: "Gives back what Fable used this week" },
  { kind: "session", name: "5-hour reset", effect: "Clears the 5-hour limit" },
  { kind: "weekly", name: "Weekly reset", effect: "Clears the weekly limit" },
  { kind: "full", name: "Full reset", effect: "Clears the 5-hour and weekly limits" },
  { kind: "", name: "Reset credits", effect: "Free limit reset" },
];

/**
 * Saved "reset for free" grants usable now, one balance row per kind of reset: each counts once per
 * reset it has left; a paused, not yet started, expired, or unflagged grant not at all. Grant ids are
 * redemption handles and are never read; an implausible total is treated as malformed. Mirrors the
 * desktop `ParseResetGrants`.
 */
export function parseResetGrants(root: unknown, now = Date.now()): UsageMetric[] {
  let total = 0;
  const byKind = new Map<string, { count: number; useBy: number | undefined }>();
  for (const grant of getArray(getObject(root, "cedar_ember"), "grants") ?? []) {
    const left = getNumber(grant, "resets_left");
    if (!isObject(grant) || grant.paused !== false || left === undefined || left < 1 || left > maxResetGrants) {
      continue;
    }
    const startsAt = Date.parse(getString(grant, "starts_at") ?? "");
    const endsAt = Date.parse(getString(grant, "ends_at") ?? "");
    if ((Number.isFinite(startsAt) && startsAt > now) || (Number.isFinite(endsAt) && endsAt <= now)) {
      continue;
    }
    total += Math.trunc(left);
    if (total > maxResetGrants) {
      return [];
    }
    const kind = resetGrantKind(grant);
    const current = byKind.get(kind) ?? { count: 0, useBy: undefined };
    byKind.set(kind, {
      count: current.count + Math.trunc(left),
      useBy: Number.isFinite(endsAt) && (current.useBy === undefined || endsAt < current.useBy) ? endsAt : current.useBy,
    });
  }
  return resetKinds.flatMap(({ kind, name, effect }) => {
    const found = byKind.get(kind);
    if (!found) {
      return [];
    }
    const text = kind === "" && found.count !== 1 ? `${effect}s` : effect;
    return [{
      name,
      kind: "balance" as const,
      usedPercent: null,
      remainingText: String(found.count),
      usageText: found.useBy === undefined ? text : `${text} · use by ${useByText(found.useBy, now)}`,
    }];
  });
}

/**
 * Which reset a grant is, by the Claude CLI's rule: one that gives back weekly use is a Fable reset;
 * otherwise its `type`, else what its `clears` list names. Empty when unknown.
 */
function resetGrantKind(grant: Record<string, unknown>): string {
  if (grant.gives_back_weekly === true) {
    return "fable";
  }
  const type = getString(grant, "type");
  if (type === "session" || type === "weekly" || type === "full") {
    return type;
  }
  const clears = (getArray(grant, "clears") ?? []).filter((item): item is string => typeof item === "string");
  const session = clears.includes("five_hour");
  const other = clears.some((limit) => limit !== "five_hour");
  return session && other ? "full" : session ? "session" : other ? "weekly" : "";
}

const shortMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/**
 * A use-by date as "17 Nov" in local time, with the year ("5 Jan 2027") only when it is not the
 * current one; English month names, like every other label. The same as the desktop app.
 */
export function useByText(time: number, now = Date.now()): string {
  const date = new Date(time);
  const day = `${date.getDate()} ${shortMonths[date.getMonth()]}`;
  return date.getFullYear() === new Date(now).getFullYear() ? day : `${day} ${date.getFullYear()}`;
}

/**
 * True when Cloudflare answered with its browser check: flagged by `cf-mitigated`, or a 403/429/503
 * HTML page served by Cloudflare instead of claude.ai's JSON. Mirrors the desktop check.
 */
export function isCloudflareChallenge(status: number, headers: http.IncomingHttpHeaders): boolean {
  const mitigated = headers["cf-mitigated"];
  if ((Array.isArray(mitigated) ? mitigated.join(",") : mitigated ?? "").toLowerCase().includes("challenge")) {
    return true;
  }
  return [403, 429, 503].includes(status) &&
    String(headers["content-type"] ?? "").toLowerCase().includes("text/html") &&
    String(headers.server ?? "").toLowerCase() === "cloudflare";
}

function createExtraUsageMetric(extraUsage: Record<string, unknown>): UsageMetric | undefined {
  if (!getBoolean(extraUsage, "is_enabled") && !getBoolean(extraUsage, "isEnabled")) {
    return undefined;
  }
  const usedCents = getNumber(extraUsage, "used_credits") ?? getNumber(extraUsage, "usedCredits");
  const limitCents = getNumber(extraUsage, "monthly_credit_limit")
    ?? getNumber(extraUsage, "monthly_limit")
    ?? getNumber(extraUsage, "monthlyLimit");
  const currency = getString(extraUsage, "currency") ?? "USD";
  let value = "ENABLED";
  if (usedCents !== undefined) {
    const used = formatCurrency(usedCents / 100, currency);
    value = limitCents === undefined ? used : `${used} / ${formatCurrency(limitCents / 100, currency)}`;
  }
  return {
    name: "Extra usage",
    kind: "balance",
    usedPercent: null,
    remainingText: value,
    usageText: "Charged beyond the plan limit",
  };
}

function formatCurrency(value: number, currency: string): string {
  try {
    return new Intl.NumberFormat(currency.toUpperCase() === "USD" ? "en-US" : undefined, {
      style: "currency",
      currency: currency.toUpperCase(),
    }).format(value);
  } catch {
    return `${value.toFixed(2)} ${currency.toUpperCase()}`;
  }
}

export function formatClaudePlan(plan: string | undefined): string {
  if (!plan?.trim()) {
    return "Claude";
  }
  const normalized = plan.trim().toLowerCase();
  if (normalized.includes("claude_max_5x") || normalized.includes("claude_max_5")) {
    return "Claude Max 5x";
  }
  if (normalized.includes("claude_max_20x") || normalized.includes("claude_max_20")) {
    return "Claude Max 20x";
  }
  const known: Record<string, string> = {
    free: "Claude Free", pro: "Claude Pro", claude_pro: "Claude Pro", max: "Claude Max",
    team: "Claude Team", enterprise: "Claude Enterprise",
  };
  return known[normalized] ?? plan.replaceAll("_", " ").replace(/\b\w/g, (value) => value.toUpperCase());
}

function getClaudeSessionKey(): string | undefined {
  for (const name of ["USAGEAI_CLAUDE_SESSION_KEY", "CLAUDE_AI_SESSION_KEY", "CLAUDE_WEB_SESSION_KEY"]) {
    let value = normalizeToken(process.env[name]);
    if (!value) {
      continue;
    }
    value = value.replace(/^sessionKey=/i, "").trim();
    if (value && !value.includes(";")) {
      return value;
    }
  }
  return undefined;
}

async function getClaudeWebUsage(signal?: AbortSignal): Promise<UsageSnapshot> {
  const sessionKey = getClaudeSessionKey();
  if (!sessionKey) {
    throw new UsageProviderError("Claude web session authentication is not configured.");
  }
  const headers = {
    Cookie: `sessionKey=${sessionKey}`,
    Origin: "https://claude.ai",
    Referer: "https://claude.ai/settings/usage",
    "anthropic-client-platform": "web_claude_ai",
  };
  const common = {
    allowedHosts: ["claude.ai"] as const,
    headers,
    ...(signal ? { signal } : {}),
  };
  // Cloudflare's browser check blocks the request before claude.ai sees the session, so it says
  // nothing about the session key: it pauses the web path instead of reading as a dead session.
  const request = async (url: string) => {
    let response;
    try {
      response = await requestJson(url, common);
    } catch (error) {
      if (error instanceof NonJsonResponseError && isCloudflareChallenge(error.status, error.headers)) {
        throw new UsageProviderError(
          "claude.ai asked for a browser check (Cloudflare) instead of answering. The session key is kept; UsageAI will try again later.",
          cloudflareRetryAfterMs,
        );
      }
      throw error;
    }
    if (isCloudflareChallenge(response.status, response.headers)) {
      throw new UsageProviderError(
        "claude.ai asked for a browser check (Cloudflare) instead of answering. The session key is kept; UsageAI will try again later.",
        cloudflareRetryAfterMs,
      );
    }
    return response;
  };
  const accountResponse = await request("https://claude.ai/api/account");
  if (accountResponse.status < 200 || accountResponse.status >= 300) {
    throw new UsageProviderError("Claude's saved web session is no longer valid.");
  }
  const account = accountResponse.data;
  let organizationId = findOrganizationId(account);
  if (!organizationId) {
    const organizations = await request("https://claude.ai/api/organizations");
    organizationId = Array.isArray(organizations.data)
      ? organizations.data.map((entry) => getString(entry, "uuid")).find(Boolean)
      : undefined;
  }
  if (!organizationId) {
    throw new UsageProviderError("Claude did not return an account organization.");
  }
  const escapedId = encodeURIComponent(organizationId);
  // Saved limit-reset credits are reported only when asked for; a rejection is retried without it.
  let usage = await request(`https://claude.ai/api/organizations/${escapedId}/usage?cedar_ember=1`);
  if (usage.status < 200 || usage.status >= 300) {
    usage = await request(`https://claude.ai/api/organizations/${escapedId}/usage`);
  }
  if (usage.status < 200 || usage.status >= 300) {
    throw new UsageProviderError(`Claude web usage returned HTTP ${usage.status}.`);
  }
  const snapshot = parseClaudeSnapshot(usage.data, formatClaudePlan(getString(account, "rate_limit_tier")));
  const accountName = getString(account, "email_address");
  return { ...snapshot, ...(accountName ? { accountName } : {}) };
}

function findOrganizationId(account: unknown): string | undefined {
  for (const membership of getArray(account, "memberships") ?? []) {
    const nested = getString(getObject(membership, "organization"), "uuid");
    const direct = getString(membership, "uuid");
    if (nested || direct) {
      return nested ?? direct;
    }
  }
  return undefined;
}
