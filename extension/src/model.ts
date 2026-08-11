export type UsageMetricKind = "session" | "rolling" | "monthly" | "balance";

export interface UsageMetric {
  readonly name: string;
  readonly kind: UsageMetricKind;
  readonly usedPercent: number | null;
  readonly resetsAt?: string;
  readonly durationMinutes?: number;
  readonly remainingText?: string;
  readonly usageText?: string;
  readonly isUnlimited?: boolean;
}

export interface UsageSnapshot {
  readonly plan: string;
  readonly metrics: readonly UsageMetric[];
  readonly fetchedAt: string;
  readonly providerId: string;
  readonly providerName: string;
  readonly accountName?: string;
}

export interface ProviderState {
  readonly id: string;
  readonly displayName: string;
  readonly signInCommand: string;
  readonly accountUrl: string;
  readonly snapshot?: UsageSnapshot;
  readonly error?: string;
  readonly stale: boolean;
  readonly refreshing: boolean;
  readonly nextRefreshAt?: string;
}

export interface UsageClient {
  readonly id: string;
  readonly displayName: string;
  readonly signInCommand: string;
  readonly accountUrl: string;
  getUsage(signal?: AbortSignal): Promise<UsageSnapshot>;
}

export type StatusBarProviderId = "hottest" | "codex" | "claude" | "copilot" | "gemini";

const statusBarProviderIds = new Set<StatusBarProviderId>([
  "hottest",
  "codex",
  "claude",
  "copilot",
  "gemini",
]);

export function normalizeStatusBarProviders(value: unknown): StatusBarProviderId[] {
  const values = typeof value === "string" ? [value] : Array.isArray(value) ? value : [];
  const selected = [...new Set(values.filter(isStatusBarProviderId))];
  return selected.length > 1 ? selected.filter((providerId) => providerId !== "hottest") : selected;
}

export interface StatusBarProviderCheckboxes {
  readonly hottestWhenNoneSelected: boolean;
  readonly codex: boolean;
  readonly claude: boolean;
  readonly copilot: boolean;
  readonly gemini: boolean;
}

export function statusBarProvidersFromCheckboxes(
  checkboxes: StatusBarProviderCheckboxes,
  providerOrder: readonly Exclude<StatusBarProviderId, "hottest">[] = ["codex", "claude", "copilot", "gemini"],
): StatusBarProviderId[] {
  const explicitProviders = providerOrder.filter((providerId) => checkboxes[providerId]);
  if (explicitProviders.length > 0) {
    return explicitProviders;
  }
  return checkboxes.hottestWhenNoneSelected ? ["hottest"] : [];
}

function isStatusBarProviderId(value: unknown): value is StatusBarProviderId {
  return typeof value === "string" && statusBarProviderIds.has(value as StatusBarProviderId);
}

export function clampPercent(value: number): number {
  return Math.max(0, Math.min(100, Math.round(value)));
}

export function hasQuota(metric: UsageMetric): boolean {
  return metric.usedPercent !== null && !metric.isUnlimited;
}

export function highestUsedPercent(snapshot: UsageSnapshot): number {
  return snapshot.metrics
    .filter(hasQuota)
    .reduce((highest, metric) => Math.max(highest, metric.usedPercent ?? 0), 0);
}

export function statusBarMetrics(snapshot: UsageSnapshot): UsageMetric[] {
  const quotaMetrics = snapshot.metrics.filter(hasQuota);
  const session = quotaMetrics.find((metric) => metric.kind === "session");
  const rolling = quotaMetrics.find((metric) => metric.kind === "rolling");
  const preferred = [session, rolling].filter((metric): metric is UsageMetric => Boolean(metric));

  for (const metric of quotaMetrics) {
    if (!preferred.includes(metric)) {
      preferred.push(metric);
    }
  }

  return preferred.slice(0, 2);
}

export function segmentedUsageBar(usedPercent: number, segmentCount = 10): string {
  const safeSegmentCount = Math.max(1, Math.round(segmentCount));
  const filledCount = Math.round((clampPercent(usedPercent) / 100) * safeSegmentCount);
  return [
    ...Array.from({ length: filledCount }, () => "■"),
    ...Array.from({ length: safeSegmentCount - filledCount }, () => "□"),
  ].join(" ");
}

export function primaryMetric(snapshot: UsageSnapshot): UsageMetric | undefined {
  return snapshot.metrics.find(hasQuota) ?? snapshot.metrics[0];
}

export function metricKey(metric: UsageMetric): string {
  return `${metric.kind}:${metric.name}`;
}

export function formatResetCountdown(iso: string | undefined, now = Date.now()): string {
  if (!iso) {
    return "Reset not reported";
  }

  const milliseconds = new Date(iso).getTime() - now;
  if (milliseconds <= 0) {
    return "Reset due";
  }

  const minutes = Math.ceil(milliseconds / 60_000);
  if (minutes < 60) {
    return `Resets in ${minutes}m`;
  }
  if (minutes < 1_440) {
    const hours = Math.floor(minutes / 60);
    const remainingMinutes = minutes % 60;
    return `Resets in ${hours}h${remainingMinutes ? ` ${remainingMinutes}m` : ""}`;
  }

  const days = Math.floor(minutes / 1_440);
  const remainingHours = Math.floor((minutes % 1_440) / 60);
  return `Resets in ${days}d${remainingHours ? ` ${remainingHours}h` : ""}`;
}
