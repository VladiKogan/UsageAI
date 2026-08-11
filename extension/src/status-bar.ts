import * as vscode from "vscode";
import {
  highestUsedPercent,
  normalizeStatusBarProviders,
  type ProviderState,
  type StatusBarProviderId,
  type UsageSnapshot,
} from "./model";

const providerLabels: Record<Exclude<StatusBarProviderId, "hottest">, string> = {
  codex: "Codex",
  claude: "Claude",
  copilot: "Copilot",
  gemini: "Gemini",
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
        renderSnapshot(item, hottest, shortProviderName(hottest));
        return;
      }

      renderUnavailable(item, "UsageAI", states);
      return;
    }

    const state = states.find((candidate) => candidate.id === providerId);
    if (state?.snapshot) {
      renderSnapshot(item, state as ProviderState & { snapshot: UsageSnapshot }, providerLabels[providerId]);
      return;
    }

    renderUnavailable(item, providerLabels[providerId], state ? [state] : []);
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
): void {
  const staleLabel = state.stale ? " (stale)" : "";
  item.text = `$(pulse) ${label} ${highestUsedPercent(state.snapshot)}%`;
  item.tooltip = `${state.displayName} · ${state.snapshot.plan}${staleLabel}\nClick to open UsageAI`;
}

function renderUnavailable(
  item: vscode.StatusBarItem,
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
    item.text = `$(pulse) ${label} —`;
    item.tooltip = `${label} has no usage reading. Click to open UsageAI.`;
  }
}

function shortProviderName(state: ProviderState): string {
  return state.id in providerLabels
    ? providerLabels[state.id as keyof typeof providerLabels]
    : state.displayName;
}
