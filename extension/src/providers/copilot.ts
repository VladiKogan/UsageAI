import * as os from "node:os";
import * as path from "node:path";
import { UsageProviderError } from "../errors";
import { clampPercent, type UsageClient, type UsageMetric, type UsageSnapshot } from "../model";
import {
  collectProcessOutput,
  findExecutable,
  normalizeToken,
  readBoundedText,
  requestJson,
  spawnSecure,
  userHome,
} from "../security";
import { getBoolean, getNumber, getObject, getString, isObject, parseDate } from "./json";

const userEndpoint = "https://api.github.com/copilot_internal/user";

/** The message for an account whose Copilot subscription GitHub reports as ended. */
export const subscriptionEndedMessage = "Your GitHub Copilot subscription has ended. Renew it on GitHub, then refresh.";

/**
 * The host each discovered token belongs to, for tokens of a GitHub Enterprise Cloud (`*.ghe.com`)
 * login. Every other token is a github.com token. Mirrors the desktop `TokenHosts`.
 */
const tokenHosts = new Map<string, string>();

/** A bare lower-case DNS host from a value such as `https://octocorp.ghe.com/`. */
export function normalizeHost(value: string | undefined): string | undefined {
  let host = value?.trim().toLowerCase();
  if (!host) {
    return undefined;
  }
  host = host.replace(/^https?:\/\//, "");
  host = host.split(/[/:?#]/, 1)[0] ?? "";
  return host.length > 3 && host.length <= 253 && host.includes(".") && /^[a-z0-9.-]+$/.test(host) &&
    !host.startsWith(".") && !host.endsWith(".")
    ? host
    : undefined;
}

/**
 * Where a token's usage is read: api.github.com for github.com, `api.<host>` for a GitHub Enterprise
 * Cloud host, and nowhere for any other host, so a token only ever goes to the service that issued it.
 */
export function userEndpointFor(host: string | undefined): string | undefined {
  const normalized = normalizeHost(host);
  if (!normalized) {
    return undefined;
  }
  if (normalized === "github.com") {
    return userEndpoint;
  }
  return normalized.endsWith(".ghe.com") && normalized.length > ".ghe.com".length
    ? `https://api.${normalized}/copilot_internal/user`
    : undefined;
}

/**
 * The host a credential key names, or undefined when the key is no host: a URL, `host:appId`
 * (apps.json), or a bare host whose object holds the token itself (hosts.json).
 */
export function hostFromKey(key: string, holdsToken: boolean): string | undefined {
  return /^https?:\/\//i.test(key) || key.includes(":") || holdsToken ? normalizeHost(key) : undefined;
}
export class CopilotUsageClient implements UsageClient {
  public readonly id = "copilot";
  public readonly displayName = "GitHub Copilot";
  public readonly signInCommand = "copilot";
  public readonly accountUrl = "https://github.com/settings/copilot/features";
  private readonly rejectedTokens = new Set<string>();
  private workingToken: string | undefined;

  public constructor(private readonly enableGitHubCliFallback: () => boolean) {}

  public async getUsage(signal?: AbortSignal): Promise<UsageSnapshot> {
    const tokens = await this.findTokens();
    if (tokens.length === 0) {
      throw new UsageProviderError(
        "GitHub Copilot is not signed in. Sign in through Copilot, provide COPILOT_GITHUB_TOKEN, or enable the GitHub CLI fallback.",
      );
    }

    let rejected = 0;
    let lastFailure: unknown;
    for (const token of this.prioritizeTokens(tokens)) {
      try {
        const endpoint = userEndpointFor(tokenHosts.get(token) ?? "github.com") ?? userEndpoint;
        const response = await requestJson(endpoint, {
          allowedHosts: [new URL(endpoint).host],
          headers: { Authorization: `Bearer ${token}` },
          ...(signal ? { signal } : {}),
        });
        if (response.status === 401 || response.status === 403) {
          rejected += 1;
          this.rejectedTokens.add(token);
          if (this.workingToken === token) {
            this.workingToken = undefined;
          }
          continue;
        }
        if (response.status < 200 || response.status >= 300) {
          throw new UsageProviderError(`GitHub Copilot returned HTTP ${response.status} while reading usage.`);
        }
        this.workingToken = token;
        return parseCopilotSnapshot(response.data);
      } catch (error) {
        // GitHub answered for this account; another token would not make the subscription active.
        if (error instanceof UsageProviderError && error.message === subscriptionEndedMessage) {
          throw error;
        }
        lastFailure = error;
      }
    }
    if (rejected === tokens.length) {
      throw new UsageProviderError(
        "The saved GitHub credentials cannot access Copilot. Sign in to GitHub Copilot again, then refresh.",
      );
    }
    throw new UsageProviderError(
      "Could not read GitHub Copilot usage because the provider returned invalid or unavailable data.",
      0,
      { cause: lastFailure },
    );
  }

  private prioritizeTokens(tokens: readonly string[]): string[] {
    let candidates = tokens.filter((token) => !this.rejectedTokens.has(token));
    if (candidates.length === 0) {
      this.rejectedTokens.clear();
      candidates = [...tokens];
    }
    if (this.workingToken) {
      candidates = [this.workingToken, ...candidates.filter((token) => token !== this.workingToken)];
    }
    return candidates;
  }

  private async findTokens(): Promise<string[]> {
    const tokens = new Set<string>();
    addToken(tokens, process.env.COPILOT_GITHUB_TOKEN);
    for (const candidate of credentialFileCandidates()) {
      try {
        addTokensFromValue(tokens, JSON.parse(await readBoundedText(candidate)) as unknown, 0);
      } catch {
        // Optional credential sources can be absent, locked, or use another format.
      }
    }
    if (this.enableGitHubCliFallback()) {
      addToken(tokens, await readGitHubCliToken());
    }
    return [...tokens].slice(0, 16);
  }
}

export function parseCopilotSnapshot(root: unknown): UsageSnapshot {
  // GitHub answers an ended subscription with a normal reply and this SKU.
  if (getString(root, "access_type_sku")?.toLowerCase() === "subscription_ended") {
    throw new UsageProviderError(subscriptionEndedMessage);
  }
  const fallbackReset = parseDate(
    getString(root, "quota_reset_date_utc")
      ?? getString(root, "quota_reset_date")
      ?? getString(root, "limited_user_reset_date"),
  );
  const snapshots = getObject(root, "quota_snapshots");
  const windows: Array<{ readonly order: number; readonly metric: UsageMetric }> = [];
  if (snapshots) {
    addQuotaWindow(windows, snapshots, "premium_interactions", 0, fallbackReset);
    addQuotaWindow(windows, snapshots, "chat", 1, fallbackReset);
    addQuotaWindow(windows, snapshots, "completions", 2, fallbackReset);
  }
  const metrics = windows
    .sort((left, right) => Number(Boolean(left.metric.isUnlimited)) - Number(Boolean(right.metric.isUnlimited)) || left.order - right.order)
    .map(({ metric }) => metric);
  if (metrics.length === 0) {
    throw new UsageProviderError("GitHub Copilot returned account details without any quota information.");
  }
  const accountName = getString(root, "login");
  return {
    plan: resolveCopilotPlan(getString(root, "copilot_plan"), getString(root, "access_type_sku")),
    metrics,
    fetchedAt: new Date().toISOString(),
    providerId: "copilot",
    providerName: "GitHub Copilot",
    ...(accountName ? { accountName } : {}),
  };
}

function addQuotaWindow(
  windows: Array<{ readonly order: number; readonly metric: UsageMetric }>,
  snapshots: Record<string, unknown>,
  propertyName: string,
  order: number,
  fallbackReset: string | undefined,
): void {
  const quota = getObject(snapshots, propertyName);
  if (!quota || quota.has_quota === false) {
    return;
  }
  const unlimited = getBoolean(quota, "unlimited");
  const entitlement = getNumber(quota, "entitlement");
  const remaining = getNumber(quota, "quota_remaining") ?? getNumber(quota, "remaining");
  let remainingPercent = getNumber(quota, "percent_remaining");
  if (remainingPercent === undefined && entitlement && entitlement > 0 && remaining !== undefined) {
    remainingPercent = remaining / entitlement * 100;
  }
  remainingPercent = Math.max(0, Math.min(100, remainingPercent ?? (unlimited ? 100 : 0)));
  const usedPercent = clampPercent(100 - remainingPercent);
  const resetSeconds = getNumber(quota, "quota_reset_at");
  const resetsAt = resetSeconds && resetSeconds > 0
    ? new Date(resetSeconds * 1000).toISOString()
    : fallbackReset;
  const name = propertyName === "premium_interactions"
    ? (getBoolean(quota, "token_based_billing") ? "AI credits" : "Premium requests")
    : propertyName === "chat" ? "Chat" : propertyName === "completions" ? "Completions" : propertyName;
  const usageText = unlimited
    ? "No monthly limit"
    : entitlement && entitlement > 0 && remaining !== undefined
      ? `${formatNumber(remaining)} of ${formatNumber(entitlement)} left`
      : `${usedPercent}% used`;
  windows.push({
    order,
    metric: {
      name,
      kind: "monthly",
      usedPercent,
      ...(resetsAt ? { resetsAt } : {}),
      remainingText: unlimited ? "UNLIMITED" : `${Math.round(remainingPercent)}% LEFT`,
      usageText,
      isUnlimited: unlimited,
    },
  });
}

function credentialFileCandidates(): string[] {
  const home = userHome();
  const candidates = [
    path.join(home, ".copilot", "config.json"),
    path.join(home, ".copilot", "settings.json"),
    path.join(home, ".config", "github-copilot", "apps.json"),
  ];
  if (process.platform === "win32") {
    if (process.env.LOCALAPPDATA) {
      candidates.push(path.join(process.env.LOCALAPPDATA, "github-copilot", "apps.json"));
    }
    if (process.env.APPDATA) {
      candidates.push(path.join(process.env.APPDATA, "GitHub Copilot", "apps.json"));
    }
  } else if (process.platform === "darwin") {
    candidates.push(path.join(home, "Library", "Application Support", "GitHub Copilot", "apps.json"));
  }
  return candidates;
}

/**
 * Collects tokens, remembering the host each belongs to where the file says so. A token without a
 * host is github.com's. Mirrors the desktop `AddTokensFromElement`.
 */
export function addTokensFromValue(tokens: Set<string>, value: unknown, depth: number, host?: string): void {
  if (!isObject(value) || depth > 8 || tokens.size >= 16) {
    return;
  }
  for (const [key, child] of Object.entries(value)) {
    if (key === "oauth_token" && typeof child === "string") {
      addToken(tokens, child, host);
    } else if (key === "copilotTokens" && isObject(child)) {
      for (const [tokenKey, token] of Object.entries(child)) {
        if (typeof token === "string") {
          addToken(tokens, token, hostFromKey(tokenKey, true) ?? host);
        }
      }
    } else if (isObject(child)) {
      addTokensFromValue(tokens, child, depth + 1, hostFromKey(key, typeof child.oauth_token === "string") ?? host);
    }
  }
}

function addToken(tokens: Set<string>, value: string | undefined, host?: string): void {
  if (tokens.size >= 16) {
    return;
  }
  const token = normalizeToken(value);
  if (!token) {
    return;
  }
  // A token for a host UsageAI does not read from (such as a self-hosted GitHub Enterprise Server)
  // is left out entirely rather than sent to github.com.
  const tokenHost = normalizeHost(host) ?? "github.com";
  if (!userEndpointFor(tokenHost)) {
    return;
  }
  tokens.add(token);
  if (tokenHost !== "github.com") {
    tokenHosts.set(token, tokenHost);
  }
}

async function readGitHubCliToken(): Promise<string | undefined> {
  const executable = await findExecutable(
    process.platform === "win32" ? ["gh.exe"] : ["gh"],
  );
  if (!executable) {
    return undefined;
  }
  try {
    const child = spawnSecure(
      executable,
      ["auth", "token", "--hostname", "github.com"],
      ["GH_CONFIG_DIR", "NODE_EXTRA_CA_CERTS", "SSL_CERT_DIR", "SSL_CERT_FILE"],
    );
    const result = await collectProcessOutput(child, 5_000, 16_384);
    return result.exitCode === 0 ? normalizeToken(result.stdout) : undefined;
  } catch {
    return undefined;
  }
}

function formatNumber(value: number): string {
  return new Intl.NumberFormat(undefined, { maximumFractionDigits: Math.abs(value % 1) < 0.001 ? 0 : 1 }).format(value);
}

/**
 * GitHub reports `copilot_plan` as "individual" for Free, Pro and Pro+ alike, so a personal plan is
 * named from `access_type_sku` when that SKU is one we recognise. Every other plan, and an
 * unrecognised SKU, keeps the reported plan name. Mirrors the desktop's `ResolvePlan`.
 */
export function resolveCopilotPlan(copilotPlan: string | undefined, accessTypeSku: string | undefined): string {
  const individual = copilotPlan === undefined || copilotPlan.trim().toLowerCase() === "individual";
  const fromSku = individual ? individualPlanFromSku(accessTypeSku) : undefined;
  return fromSku ?? formatPlan(copilotPlan ?? accessTypeSku ?? "Copilot");
}

function individualPlanFromSku(accessTypeSku: string | undefined): string | undefined {
  const sku = accessTypeSku?.trim().toLowerCase();
  if (!sku) {
    return undefined;
  }
  if (sku.startsWith("plus_")) {
    return "Pro+";
  }
  if (sku === "free_limited_copilot") {
    return "Free";
  }
  // Paid monthly or yearly, or granted free to verified teachers, students and maintainers.
  const pro = ["monthly_subscriber_quota", "yearly_subscriber_quota", "free_faculty_quota",
    "free_educational_quota", "free_engaged_oss_quota"];
  return pro.includes(sku) ? "Pro" : undefined;
}

function formatPlan(plan: string): string {
  const normalized = plan.replaceAll("_", " ").trim().toLowerCase();
  const known: Record<string, string> = {
    individual: "Individual", "individual pro": "Pro", business: "Business",
    enterprise: "Enterprise", free: "Free",
  };
  return known[normalized] ?? normalized.replace(/\b\w/g, (value) => value.toUpperCase());
}
