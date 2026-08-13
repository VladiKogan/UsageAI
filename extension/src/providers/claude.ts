import * as path from "node:path";
import { UsageProviderError } from "../errors";
import { clampPercent, type UsageClient, type UsageMetric, type UsageSnapshot } from "../model";
import { normalizeToken, parseRetryAfter, readBoundedText, requestJson, userHome } from "../security";
import { getArray, getBoolean, getNumber, getObject, getString, isObject, parseDate } from "./json";

const usageEndpoint = "https://api.anthropic.com/api/oauth/usage";
const oauthBeta = "oauth-2025-04-20";

interface ClaudeCredentials {
  readonly accessToken: string;
  readonly expiresAt?: number;
  readonly scopes: readonly string[];
  readonly plan: string;
}

export class ClaudeUsageClient implements UsageClient {
  public readonly id = "claude";
  public readonly displayName = "Claude Code";
  public readonly signInCommand = "claude";
  public readonly accountUrl = "https://claude.ai/settings/usage";

  public async getUsage(signal?: AbortSignal): Promise<UsageSnapshot> {
    if (getClaudeSessionKey()) {
      try {
        return await getClaudeWebUsage(signal);
      } catch {
        // The explicit web session is a fallback source; continue with Claude Code OAuth.
      }
    }

    const credentials = await this.loadCredentials();
    assertClaudeCredentialsUsable(credentials);
    if (credentials.scopes.length > 0 && !credentials.scopes.includes("user:profile")) {
      throw new UsageProviderError(
        "Claude Code's OAuth token is missing the user:profile scope. Run `claude` to sign in again.",
      );
    }

    try {
      const response = await requestJson(usageEndpoint, {
        allowedHosts: ["api.anthropic.com"],
        headers: {
          Authorization: `Bearer ${credentials.accessToken}`,
          "anthropic-beta": oauthBeta,
        },
        ...(signal ? { signal } : {}),
      });
      if (response.status === 401) {
        throw new UsageProviderError("Claude Code's login has expired. Run `claude` to sign in again, then refresh.");
      }
      if (response.status === 403) {
        throw new UsageProviderError("Claude Code's login cannot read account usage. Run `claude` to sign in again.");
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
      throw new UsageProviderError(
        "Could not read Claude Code usage because the provider returned invalid or unavailable data.",
        0,
        { cause: error },
      );
    }
  }

  private async loadCredentials(): Promise<ClaudeCredentials> {
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
      };
    }

    const configuredDirectory = process.env.CLAUDE_CONFIG_DIR?.trim().replace(/^"|"$/g, "");
    if (configuredDirectory && !path.isAbsolute(configuredDirectory)) {
      throw new UsageProviderError("CLAUDE_CONFIG_DIR must be an absolute path.");
    }
    const credentialPath = path.join(configuredDirectory || path.join(userHome(), ".claude"), ".credentials.json");
    try {
      return parseClaudeCredentials(await readBoundedText(credentialPath));
    } catch (error) {
      throw new UsageProviderError(
        "Claude Code is not signed in, or its saved login could not be read. Run `claude` to sign in, then refresh.",
        0,
        { cause: error },
      );
    }
  }

}

export function assertClaudeCredentialsUsable(credentials: { readonly expiresAt?: number }): void {
  if (credentials.expiresAt !== undefined && credentials.expiresAt <= Date.now()) {
    throw new UsageProviderError(
      "Claude Code's login has expired. Open `claude` so Claude Code can refresh it, then refresh UsageAI.",
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

export function parseClaudeSnapshot(root: unknown, plan = "Claude"): UsageSnapshot {
  const session = parseWindow(root, "five_hour", "fiveHour", "5-hour", "session", 300);
  const weekly = findLimit(root, ["weekly_all", "all_models", "weekly_models"], "Weekly", true)
    ?? parseWindow(root, "seven_day", "sevenDay", "Weekly", "rolling", 10_080);
  if (!session && !weekly) {
    throw new UsageProviderError("Claude Code returned account details without five-hour or weekly usage data.");
  }
  const metrics: UsageMetric[] = [];
  if (session) {
    metrics.push(session);
  }
  if (weekly) {
    metrics.push(weekly);
  }
  const opus = findLimit(root, ["weekly_opus"], "Weekly Opus", false);
  if (opus) {
    metrics.push(opus);
  }
  const extraUsage = getObject(root, "extra_usage") ?? getObject(root, "extraUsage");
  const extra = extraUsage ? createExtraUsageMetric(extraUsage) : undefined;
  if (extra) {
    metrics.push(extra);
  }
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
  const percent = utilization > 0 && utilization <= 1 ? utilization * 100 : utilization;
  const resetsAt = parseDate(getString(window, "resets_at") ?? getString(window, "resetsAt"));
  return {
    name: displayName,
    kind,
    usedPercent: clampPercent(percent),
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
  const accountResponse = await requestJson("https://claude.ai/api/account", common);
  if (accountResponse.status < 200 || accountResponse.status >= 300) {
    throw new UsageProviderError("Claude's saved web session is no longer valid.");
  }
  const account = accountResponse.data;
  let organizationId = findOrganizationId(account);
  if (!organizationId) {
    const organizations = await requestJson("https://claude.ai/api/organizations", common);
    organizationId = Array.isArray(organizations.data)
      ? organizations.data.map((entry) => getString(entry, "uuid")).find(Boolean)
      : undefined;
  }
  if (!organizationId) {
    throw new UsageProviderError("Claude did not return an account organization.");
  }
  const escapedId = encodeURIComponent(organizationId);
  const usage = await requestJson(`https://claude.ai/api/organizations/${escapedId}/usage`, common);
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
