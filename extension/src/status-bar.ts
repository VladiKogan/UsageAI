import * as vscode from "vscode";
import {
  highestUsedPercent,
  normalizeStatusBarProviders,
  segmentedUsageBar,
  statusBarMetrics,
  type ProviderState,
  type StatusBarProviderId,
  type UsageMetric,
  type UsageSnapshot,
} from "./model";

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
  private readonly items = new Map<StatusBarProviderId, vscode.StatusBarItem>();
  private selectionSignature = "";

  update(states: readonly ProviderState[], rawSelection: unknown): void {
    const selectedProviders = normalizeStatusBarProviders(rawSelection);
    const nextSignature = selectedProviders.join(",");
    if (nextSignature !== this.selectionSignature) {
      this.disposeItems();
      this.selectionSignature = nextSignature;
    }

    selectedProviders.forEach((providerId, index) => {
      const item = this.getOrCreateItem(providerId, index);
      this.updateItem(item, providerId, states);
      item.show();
    });
  }

  dispose(): void {
    this.disposeItems();
  }

  private getOrCreateItem(providerId: StatusBarProviderId, index: number): vscode.StatusBarItem {
    const existing = this.items.get(providerId);
    if (existing) {
      return existing;
    }

    const item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 25 - index);
    item.name = providerId === "hottest" ? "UsageAI: Hottest provider" : `UsageAI: ${providerLabels[providerId]}`;
    item.command = "usageai.show";
    this.items.set(providerId, item);
    return item;
  }

  private updateItem(
    item: vscode.StatusBarItem,
    providerId: StatusBarProviderId,
    states: readonly ProviderState[],
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
          states.some((state) => state.refreshing),
        );
        return;
      }

      renderUnavailable(item, providerId, "UsageAI", states);
      return;
    }

    const state = states.find((candidate) => candidate.id === providerId);
    if (state?.snapshot) {
      renderSnapshot(item, state as ProviderState & { snapshot: UsageSnapshot }, providerLabels[providerId]);
      return;
    }

    renderUnavailable(item, providerId, providerLabels[providerId], state ? [state] : []);
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
  refreshing = state.refreshing,
): void {
  const metrics = statusBarMetrics(state.snapshot);
  const readings = metrics.length > 0
    ? metrics.map((metric) => `${metric.usedPercent ?? 0}%`).join(" | ")
    : `${highestUsedPercent(state.snapshot)}%`;
  const icon = refreshing ? "$(sync~spin)" : providerIcon(state.id);
  item.text = `${icon} ${label} ${readings}`;
  item.tooltip = snapshotTooltip(state, metrics, refreshing);
}

function snapshotTooltip(
  state: ProviderState & { snapshot: UsageSnapshot },
  metrics: readonly UsageMetric[],
  refreshing: boolean,
): vscode.MarkdownString {
  const tooltip = new vscode.MarkdownString(undefined, true);
  tooltip.appendMarkdown(`${providerIcon(state.id)} **`);
  tooltip.appendText(state.displayName);
  tooltip.appendMarkdown("**");
  tooltip.appendText(` · ${state.snapshot.plan}${state.stale ? " (stale)" : ""}`);

  if (refreshing) {
    tooltip.appendMarkdown("\n\n$(sync~spin) *Refreshing usage…*");
  }

  if (metrics.length > 0) {
    tooltip.appendMarkdown("\n\n");
    tooltip.appendCodeblock(formatMeterRows(metrics), "text");
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
  tooltip.isTrusted = { enabledCommands: ["usageai.show", "usageai.refresh"] };
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

function formatMeterRows(metrics: readonly UsageMetric[]): string {
  const labelWidth = Math.max(...metrics.map((metric) => metric.name.length));
  return metrics.map((metric) => {
    const percent = metric.usedPercent ?? 0;
    return `${metric.name.padEnd(labelWidth)}  ${segmentedUsageBar(percent)}  ${percent}%`;
  }).join("\n");
}

function renderUnavailable(
  item: vscode.StatusBarItem,
  providerId: StatusBarProviderId,
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
    item.text = `${providerIcon(providerId)} ${label} —`;
    item.tooltip = `${label} has no usage reading. Click to open UsageAI.`;
  }
}

function providerIcon(providerId: string): string {
  return providerId in providerIcons
    ? providerIcons[providerId as keyof typeof providerIcons]
    : "$(dashboard)";
}

function shortProviderName(state: ProviderState): string {
  return state.id in providerLabels
    ? providerLabels[state.id as keyof typeof providerLabels]
    : state.displayName;
}
