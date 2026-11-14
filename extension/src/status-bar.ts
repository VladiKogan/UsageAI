import * as vscode from "vscode";
import { providerKindOf } from "./accounts";
import {
  displayedPercent,
  highestUsedPercent,
  normalizeStatusBarProviders,
  segmentedUsageBar,
  selectStatusBarLimits,
  statusBarMetricLabel,
  type ProviderState,
  type QuotaDisplay,
  type StatusBarLimits,
  type StatusBarProviderId,
  type StatusBarSelectionId,
  type UsageMetric,
  type UsageSnapshot,
} from "./model";
import { statusBarLimitFor } from "./status-bar-limits";

const providerLabels: Record<Exclude<StatusBarProviderId, "hottest">, string> = {
  codex: "Codex",
  claude: "Claude",
  copilot: "Copilot",
  gemini: "Gemini",
};

const providerIcons: Record<Exclude<StatusBarProviderId, "hottest">, string> = {
  codex: "$(usageai-codex)",
  claude: "$(usageai-claude)",
  copilot: "$(usageai-copilot)",
  gemini: "$(usageai-gemini)",
};

export class StatusBarController implements vscode.Disposable {
  private readonly items = new Map<StatusBarSelectionId, vscode.StatusBarItem>();
  private selectionSignature = "";
  private limits: Readonly<Record<string, string>> = {};

  /** `limits` holds each item's chosen model group or weekly limit, by unit key. */
  update(
    states: readonly ProviderState[],
    rawSelection: unknown,
    quotaDisplay: QuotaDisplay = "used",
    limits: Readonly<Record<string, string>> = {},
  ): void {
    this.limits = limits;
    const selectedProviders = normalizeStatusBarProviders(rawSelection);
    const nextSignature = selectedProviders.join(",");
    if (nextSignature !== this.selectionSignature) {
      this.disposeItems();
      this.selectionSignature = nextSignature;
    }

    selectedProviders.forEach((providerId, index) => {
      const item = this.getOrCreateItem(providerId, index);
      this.updateItem(item, providerId, states, quotaDisplay);
      item.show();
    });
  }

  dispose(): void {
    this.disposeItems();
  }

  private getOrCreateItem(providerId: StatusBarSelectionId, index: number): vscode.StatusBarItem {
    const existing = this.items.get(providerId);
    if (existing) {
      return existing;
    }

    const item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 25 - index);
    item.name = providerId === "hottest"
      ? "UsageAI: Hottest provider"
      : `UsageAI: ${providerLabel(providerId)}${providerId.includes("@") ? " account" : ""}`;
    item.command = "usageai.show";
    this.items.set(providerId, item);
    return item;
  }

  private updateItem(
    item: vscode.StatusBarItem,
    providerId: StatusBarSelectionId,
    states: readonly ProviderState[],
    quotaDisplay: QuotaDisplay,
  ): void {
    if (providerId === "hottest") {
      const withSnapshots = states.filter(hasSnapshot);
      const hottest = [...withSnapshots].sort(
        (left, right) => highestUsedPercent(right.snapshot) - highestUsedPercent(left.snapshot),
      )[0];
      if (hottest) {
        renderSnapshot(
          item,
          hottest,
          shortProviderName(hottest),
          quotaDisplay,
          statusBarLimitFor(this.limits, hottest.id),
          states.some((state) => state.refreshing),
        );
        return;
      }

      renderUnavailable(item, providerId, "UsageAI", states);
      return;
    }

    // A provider id follows that provider's primary account; a unit key names one account.
    const state = states.find((candidate) => candidate.id === providerId);
    const label = state ? shortProviderName(state) : providerLabel(providerId);
    if (state?.snapshot) {
      renderSnapshot(
        item,
        state as ProviderState & { snapshot: UsageSnapshot },
        label,
        quotaDisplay,
        statusBarLimitFor(this.limits, state.id),
      );
      return;
    }

    renderUnavailable(item, providerId, label, state ? [state] : []);
  }

  private disposeItems(): void {
    for (const item of this.items.values()) {
      item.dispose();
    }
    this.items.clear();
  }
}

function hasSnapshot(state: ProviderState): state is ProviderState & { snapshot: UsageSnapshot } {
  return Boolean(state.snapshot);
}

function renderSnapshot(
  item: vscode.StatusBarItem,
  state: ProviderState & { snapshot: UsageSnapshot },
  label: string,
  quotaDisplay: QuotaDisplay,
  preferred: string | undefined,
  refreshing = state.refreshing,
): void {
  // The same limits the desktop popup's rings show for this account, from the same choice.
  const limits = selectStatusBarLimits(state.snapshot.metrics, preferred);
  const { metrics } = limits;
  const icon = refreshing ? "$(sync~spin)" : providerIcon(providerKindOf(state.id));
  // An incident on the provider's status page adds a marker, the Status Bar's counterpart of the
  // desktop tray dot; the tooltip names it.
  const incident = state.incident ? ` ${state.incidentIsOutage ? "$(error)" : "$(warning)"}` : "";
  item.text = `${icon} ${label} ${statusBarReadings(state.snapshot, metrics, quotaDisplay)}${incident}`;
  item.tooltip = snapshotTooltip(state, limits, quotaDisplay, refreshing);
}

function statusBarReadings(
  snapshot: UsageSnapshot,
  metrics: readonly UsageMetric[],
  quotaDisplay: QuotaDisplay,
): string {
  if (quotaDisplay === "used") {
    return metrics.length > 0
      ? metrics.map((metric) => `${metric.usedPercent ?? 0}%`).join(" | ")
      : `${highestUsedPercent(snapshot)}%`;
  }
  // A bare remaining percentage reads as usage at a glance, so it carries one trailing unit, and a
  // provider with no metered limit shows no reading rather than a full "100% left".
  return metrics.length > 0
    ? `${metrics.map((metric) => `${displayedPercent(metric.usedPercent ?? 0, quotaDisplay)}%`).join(" | ")} left`
    : "—";
}

function snapshotTooltip(
  state: ProviderState & { snapshot: UsageSnapshot },
  limits: StatusBarLimits,
  quotaDisplay: QuotaDisplay,
  refreshing: boolean,
): vscode.MarkdownString {
  const { metrics } = limits;
  const tooltip = new vscode.MarkdownString(undefined, true);
  tooltip.appendMarkdown(`${providerIcon(providerKindOf(state.id))} **`);
  tooltip.appendText(state.displayName);
  tooltip.appendMarkdown("**");
  tooltip.appendText(` · ${state.snapshot.plan}${state.stale ? " (stale)" : ""}`);
  if (state.incident) {
    // The provider's own status page explains a stale or slow card better than anything else.
    tooltip.appendMarkdown(`\n\n${state.incidentIsOutage ? "$(error)" : "$(warning)"} *Status page:* `);
    tooltip.appendText(state.incident);
  }

  if (refreshing) {
    tooltip.appendMarkdown("\n\n$(sync~spin) *Refreshing usage…*");
  }

  if (limits.group) {
    // Which of the provider's model groups the readings are, as the desktop tile says.
    tooltip.appendMarkdown("\n\n*");
    tooltip.appendText(limits.pinned ? limits.group : `${limits.group} (closest to its limit)`);
    tooltip.appendMarkdown("*");
  }

  if (metrics.length > 0) {
    tooltip.appendMarkdown("\n\n");
    tooltip.appendCodeblock(formatMeterRows(metrics, quotaDisplay, limits.group !== undefined), "text");
  }

  const fetchedAt = new Date(state.snapshot.fetchedAt);
  if (!Number.isNaN(fetchedAt.getTime())) {
    tooltip.appendMarkdown(state.stale ? "\n\n*Last successful update:* " : "\n\n*Last updated:* ");
    tooltip.appendText(fetchedAt.toLocaleTimeString());
  }

  if (state.stale) {
    appendTimestamp(tooltip, "Last check", state.lastAttemptedAt);
    appendTimestamp(tooltip, "Automatic retry", state.nextRefreshAt);
  }

  tooltip.appendMarkdown("\n\n[Open UsageAI](command:usageai.show) · [Refresh](command:usageai.refresh)");
  const commands = ["usageai.show", "usageai.refresh"];
  if (limits.choices.length > 0) {
    // Straight to this item's choices: a model group for Gemini, a weekly limit for Claude.
    const args = encodeURIComponent(JSON.stringify([state.id]));
    tooltip.appendMarkdown(` · [Choose limits](command:usageai.chooseStatusBarLimits?${args})`);
    commands.push("usageai.chooseStatusBarLimits");
  }
  tooltip.isTrusted = { enabledCommands: commands };
  return tooltip;
}

function appendTimestamp(tooltip: vscode.MarkdownString, label: string, value: string | undefined): void {
  if (!value) {
    return;
  }
  const timestamp = new Date(value);
  if (Number.isNaN(timestamp.getTime())) {
    return;
  }
  tooltip.appendMarkdown(`\n\n*${label}:* `);
  tooltip.appendText(timestamp.toLocaleTimeString());
}

/** One row per reading. Within a named model group a row carries only its window, as a ring does. */
function formatMeterRows(metrics: readonly UsageMetric[], quotaDisplay: QuotaDisplay, grouped = false): string {
  const name = (metric: UsageMetric) => grouped ? statusBarMetricLabel(metric) : metric.name;
  const labelWidth = Math.max(...metrics.map((metric) => name(metric).length));
  const unit = quotaDisplay === "remaining" ? " left" : "";
  return metrics.map((metric) => {
    const percent = displayedPercent(metric.usedPercent ?? 0, quotaDisplay);
    return `${name(metric).padEnd(labelWidth)}  ${segmentedUsageBar(percent)}  ${percent}%${unit}`;
  }).join("\n");
}

function renderUnavailable(
  item: vscode.StatusBarItem,
  providerId: StatusBarSelectionId,
  label: string,
  states: readonly ProviderState[],
): void {
  if (states.some((state) => state.refreshing)) {
    item.text = `$(sync~spin) ${label}`;
    item.tooltip = `Refreshing ${label} usage`;
  } else if (states.some((state) => state.error)) {
    item.text = `$(warning) ${label}`;
    item.tooltip = `${label} needs attention. Click to open UsageAI.`;
  } else {
    item.text = `${providerIcon(providerKindOf(providerId))} ${label} —`;
    item.tooltip = `${label} has no usage reading. Click to open UsageAI.`;
  }
}

function providerIcon(providerId: string): string {
  return providerId in providerIcons
    ? providerIcons[providerId as keyof typeof providerIcons]
    : "$(dashboard)";
}

function providerLabel(unit: string): string {
  const kind = providerKindOf(unit);
  return kind in providerLabels ? providerLabels[kind as keyof typeof providerLabels] : unit;
}

/** "Claude", or "Claude · team" while Claude shows more than one account. */
function shortProviderName(state: ProviderState): string {
  const kind = providerKindOf(state.id);
  const name = kind in providerLabels ? providerLabels[kind as keyof typeof providerLabels] : state.displayName;
  return state.accountLabel ? `${name} · ${state.accountLabel}` : name;
}
