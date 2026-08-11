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
        const response = await requestJson(userEndpoint, {
          allowedHosts: ["api.github.com"],
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
    plan: formatPlan(getString(root, "copilot_plan") ?? getString(root, "access_type_sku") ?? "Copilot"),
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

function addTokensFromValue(tokens: Set<string>, value: unknown, depth: number): void {
  if (!isObject(value) || depth > 8 || tokens.size >= 16) {
    return;
  }
  for (const [key, child] of Object.entries(value)) {
    if (key === "oauth_token" && typeof child === "string") {
      addToken(tokens, child);
    } else if (key === "copilotTokens" && isObject(child)) {
      for (const token of Object.values(child)) {
        if (typeof token === "string") {
          addToken(tokens, token);
        }
      }
    } else if (isObject(child)) {
      addTokensFromValue(tokens, child, depth + 1);
    }
  }
}

function addToken(tokens: Set<string>, value: string | undefined): void {
  if (tokens.size >= 16) {
    return;
  }
  const token = normalizeToken(value);
  if (token) {
    tokens.add(token);
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

function formatPlan(plan: string): string {
  const normalized = plan.replaceAll("_", " ").trim().toLowerCase();
  const known: Record<string, string> = {
    individual: "Individual", "individual pro": "Pro", business: "Business",
    enterprise: "Enterprise", free: "Free",
  };
  return known[normalized] ?? normalized.replace(/\b\w/g, (value) => value.toUpperCase());
}
