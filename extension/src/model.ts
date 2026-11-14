export type UsageMetricKind = "session" | "rolling" | "monthly" | "balance";
export type MetricDisplayMode = "all" | "metered" | "important";
export type QuotaDisplay = "used" | "remaining";

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
  /** The unit key: the provider id for its default login, `claude@1a2b3c4d` for another account. */
  readonly id: string;
  /** The card title: "Claude Code", or "Claude Code · team" when the provider shows several accounts. */
  readonly displayName: string;
  /** The account's label, set only while its provider shows more than one account. */
  readonly accountLabel?: string;
  readonly signInCommand: string;
  readonly accountUrl: string;
  readonly snapshot?: UsageSnapshot;
  readonly error?: string;
  readonly stale: boolean;
  readonly refreshing: boolean;
  readonly lastAttemptedAt?: string;
  readonly nextRefreshAt?: string;
  /** The provider's own status-page incident, such as "Degraded: Elevated errors", when there is one. */
  readonly incident?: string;
  readonly incidentIsOutage?: boolean;
}

export interface UsageClient {
  readonly id: string;
  readonly displayName: string;
  readonly signInCommand: string;
  readonly accountUrl: string;
  getUsage(signal?: AbortSignal): Promise<UsageSnapshot>;
  /**
   * Called before a refresh the user asked for. Clients that negatively cache an expensive recovery
   * path clear it here instead of serving a degraded reading until the backoff expires.
   */
  onForcedRefresh?(): void;
}

export type StatusBarProviderId = "hottest" | "codex" | "claude" | "copilot" | "gemini";

const statusBarProviderIds = new Set<StatusBarProviderId>([
  "hottest",
  "codex",
  "claude",
  "copilot",
  "gemini",
]);

/** A Status Bar entry: a provider (its primary account), "hottest", or an account unit key. */
export type StatusBarSelectionId = StatusBarProviderId | `${"claude" | "codex"}@${string}`;

export function normalizeStatusBarProviders(value: unknown): StatusBarSelectionId[] {
  const values = typeof value === "string" ? [value] : Array.isArray(value) ? value : [];
  const selected = [...new Set(values.filter((candidate): candidate is StatusBarSelectionId =>
    isStatusBarProviderId(candidate) || isAccountSelection(candidate)))];
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

function isAccountSelection(value: unknown): boolean {
  return typeof value === "string" && /^(claude|codex)@[0-9a-f]{8}$/.test(value);
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

export function normalizeMetricDisplayMode(value: unknown): MetricDisplayMode {
  return value === "metered" || value === "important" ? value : "all";
}

export function normalizeQuotaDisplay(value: unknown): QuotaDisplay {
  return value === "remaining" ? "remaining" : "used";
}

/**
 * The percentage a meter shows and fills to. Selection, ordering, and warning levels keep working
 * from `usedPercent`; only the presented number and the bar flip, and always together. Kept free of
 * other helpers because the dashboard webview inlines it with `toString()`.
 */
export function displayedPercent(usedPercent: number, display: QuotaDisplay): number {
  const used = Math.max(0, Math.min(100, Math.round(usedPercent)));
  return display === "remaining" ? 100 - used : used;
}

/** Selects full-dashboard rows without changing the provider snapshot or Status Bar selection. */
export function selectDashboardMetrics(
  metrics: readonly UsageMetric[],
  mode: MetricDisplayMode,
): readonly UsageMetric[] {
  if (mode === "all") {
    return metrics;
  }
  if (mode === "metered") {
    return metrics.filter(hasQuota);
  }

  const kinds: readonly UsageMetricKind[] = ["session", "rolling", "monthly"];
  return kinds.flatMap((kind) => {
    let selected: UsageMetric | undefined;
    for (const metric of metrics) {
      if (metric.kind === kind && hasQuota(metric)
        && (!selected || (metric.usedPercent ?? 0) > (selected.usedPercent ?? 0))) {
        selected = metric;
      }
    }
    return selected ? [selected] : [];
  });
}

export function highestUsedPercent(snapshot: UsageSnapshot): number {
  return snapshot.metrics
    .filter(hasQuota)
    .reduce((highest, metric) => Math.max(highest, metric.usedPercent ?? 0), 0);
}

/** The most limits one Status Bar item shows, as the desktop popup tile has two rings. */
export const maxStatusBarMetrics = 2;

/**
 * Splits a name such as "Claude and GPT models (Weekly)" into its model group and window. A name
 * without a trailing parenthesis has no group and is its own window. Mirrors the desktop
 * `PopupMetricSelection.Split`.
 */
export function splitMetricName(name: string): { readonly group?: string; readonly window: string } {
  const trimmed = name.trim();
  const open = trimmed.lastIndexOf("(");
  if (open <= 0 || !trimmed.endsWith(")") || open + 2 >= trimmed.length) {
    return { window: trimmed };
  }
  const group = trimmed.slice(0, open).trim();
  const window = trimmed.slice(open + 1, -1).trim();
  return group.length === 0 || window.length === 0 ? { window: trimmed } : { group, window };
}

/** The model groups among a provider's metered limits, in reported order, when there are several. */
export function metricGroups(metrics: readonly UsageMetric[]): string[] {
  const groups: string[] = [];
  for (const metric of metrics) {
    const group = hasQuota(metric) ? splitMetricName(metric.name).group : undefined;
    if (group && !groups.some((known) => sameName(known, group))) {
      groups.push(group);
    }
  }
  return groups.length > 1 ? groups : [];
}

/** The length of a weekly window, which every limit offered as a weekly choice has. */
export const weekMinutes = 10_080;

/**
 * The weekly limits a provider without model groups reports more than one of, in reported order:
 * Claude's all-models "Weekly" and its model limits such as "Weekly Fable". Only week-long windows
 * count, so daily limits (the Gemini CLI's per-model quotas) are never offered. Mirrors the desktop.
 */
export function weeklyLimits(metrics: readonly UsageMetric[]): string[] {
  if (metricGroups(metrics).length > 0) {
    return [];
  }
  const limits: string[] = [];
  for (const metric of metrics) {
    const name = metric.name.trim();
    if (hasQuota(metric) && metric.kind === "rolling" && metric.durationMinutes === weekMinutes && !limits.some((known) => sameName(known, name))) {
      limits.push(name);
    }
  }
  return limits.length > 1 ? limits : [];
}

/** What a Status Bar item's limits can be pinned to: model groups, else weekly limits. */
export function statusBarLimitChoices(metrics: readonly UsageMetric[]): string[] {
  const groups = metricGroups(metrics);
  return groups.length > 0 ? groups : weeklyLimits(metrics);
}

/** The limits a Status Bar item shows, and where they came from. */
export interface StatusBarLimits {
  /** At most two metered limits, shortest window first. */
  readonly metrics: readonly UsageMetric[];
  /** The model group the limits belong to, when the provider reports several. */
  readonly group?: string;
  /** The pinned choice in use, or undefined when the busiest is shown. */
  readonly pinned?: string;
  /** Everything the item could be pinned to; empty when there is no choice. */
  readonly choices: readonly string[];
}

/**
 * Chooses what a Status Bar item shows, as the desktop popup tile chooses its rings
 * (`PopupMetricSelection.Select`). A provider reporting several model groups — Gemini's "Gemini
 * Models" and "Claude and GPT models" — shows one group: the pinned one, else whichever holds the
 * busiest limit. A provider with one group but several weekly limits — Claude's "Weekly" beside
 * "Weekly Fable" — shows the pinned weekly limit, else the busiest. Within that the limits are the
 * dashboard's Important selection, busiest session then rolling then monthly, two at most; with
 * none of those, the busiest metered limits. A pinned choice the provider no longer reports means
 * automatic.
 */
export function selectStatusBarLimits(metrics: readonly UsageMetric[], preferred?: string): StatusBarLimits {
  let metered = metrics.filter(hasQuota);
  const groups = metricGroups(metrics);
  const choices = groups.length > 0 ? groups : weeklyLimits(metrics);
  const wanted = preferred?.trim();
  const pinned = wanted ? choices.find((choice) => sameName(choice, wanted)) : undefined;
  let group: string | undefined;
  if (groups.length > 0) {
    group = pinned ?? [...metered]
      .filter((metric) => splitMetricName(metric.name).group !== undefined)
      .sort((left, right) => (right.usedPercent ?? 0) - (left.usedPercent ?? 0))
      .map((metric) => splitMetricName(metric.name).group)[0];
    metered = metered.filter((metric) => sameName(splitMetricName(metric.name).group ?? "", group ?? ""));
  } else if (pinned) {
    // The other weekly limits step aside, so the Important selection keeps the pinned one.
    metered = metered.filter((metric) => metric.kind !== "rolling" || sameName(metric.name.trim(), pinned));
  }

  let selected = selectDashboardMetrics(metered, "important");
  if (selected.length === 0) {
    // Metered limits that are not session, rolling or monthly windows still deserve a reading.
    selected = [...metered].sort((left, right) => (right.usedPercent ?? 0) - (left.usedPercent ?? 0));
  }
  return {
    metrics: selected.slice(0, maxStatusBarMetrics),
    ...(group ? { group } : {}),
    ...(pinned ? { pinned } : {}),
    choices,
  };
}

/** The limits a Status Bar item shows; see `selectStatusBarLimits`. */
export function statusBarMetrics(snapshot: UsageSnapshot, preferred?: string): UsageMetric[] {
  return [...selectStatusBarLimits(snapshot.metrics, preferred).metrics];
}

/** The short label a Status Bar reading carries: the window when the name has a group, else the name. */
export function statusBarMetricLabel(metric: UsageMetric): string {
  return splitMetricName(metric.name).window;
}

function sameName(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
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

/**
 * How a metered window's use compares with the time it has run: elapsed share minus used share,
 * as "On pace" (within 3 points), "12% under pace" or "8% over pace". Empty when unknown — no reset
 * or length, a full or unmetered limit, or before 5% of the window has passed. Mirrors the desktop
 * `UsagePace`. It is inlined into the webview with `toString()`, so it must stay self-contained.
 */
export function paceText(
  metric: { readonly usedPercent: number | null; readonly resetsAt?: string; readonly durationMinutes?: number; readonly isUnlimited?: boolean },
  now = Date.now(),
): string {
  const used = metric.usedPercent;
  if (used === null || metric.isUnlimited || used >= 100 || !metric.resetsAt || !metric.durationMinutes || metric.durationMinutes <= 0) {
    return "";
  }
  const duration = metric.durationMinutes * 60_000;
  const elapsed = duration - (Date.parse(metric.resetsAt) - now);
  if (!(elapsed > 0) || elapsed > duration || elapsed / duration < 0.05) {
    return "";
  }
  const reserve = Math.round(elapsed / duration * 100) - used;
  return reserve > 3 ? `${reserve}% under pace` : reserve < -3 ? `${-reserve}% over pace` : "On pace";
}

export function formatResetCountdown(iso: string | undefined, now = Date.now()): string {
  if (!iso) {
    return "Reset not reported";
  }

  const resetAt = new Date(iso).getTime();
  if (!Number.isFinite(resetAt)) {
    return "Reset not reported";
  }

  const milliseconds = resetAt - now;
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
