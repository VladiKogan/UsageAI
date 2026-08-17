import * as vscode from "vscode";
import { UsageDashboardViewProvider } from "./dashboard-view";
import {
  statusBarProvidersFromCheckboxes,
  type ProviderState,
  type StatusBarProviderId,
  type UsageSnapshot,
} from "./model";
import { ClaudeUsageClient } from "./providers/claude";
import { CodexUsageClient } from "./providers/codex";
import { CopilotUsageClient } from "./providers/copilot";
import { GeminiUsageClient } from "./providers/gemini";
import { UsageRefreshService } from "./refresh-service";
import { StatusBarController } from "./status-bar";

const cacheKey = "usageai.cachedSnapshots.v1";
type UsageProviderId = Exclude<StatusBarProviderId, "hottest">;

export function activate(context: vscode.ExtensionContext): void {
  const configuration = () => vscode.workspace.getConfiguration("usageai");
  const clients = [
    new CodexUsageClient(),
    new ClaudeUsageClient(),
    new CopilotUsageClient(() => configuration().get("enableGitHubCliFallback", false)),
    new GeminiUsageClient(),
  ];
  const enabledProviderIds = () => sanitizeProviderIds(
    configuration().get<readonly string[]>("providers", clients.map((client) => client.id)),
  );
  const cached = sanitizeCachedSnapshots(context.globalState.get<unknown>(cacheKey, {}));
  const refreshService = new UsageRefreshService({
    clients,
    enabledProviderIds,
    visibleIntervalMs: () => clampMinutes(configuration().get("refreshIntervalMinutes", 5), 1, 120) * 60_000,
    backgroundIntervalMs: () => clampMinutes(configuration().get("backgroundRefreshIntervalMinutes", 15), 5, 240) * 60_000,
    initialSnapshots: cached,
  });
  const dashboard = new UsageDashboardViewProvider(
    refreshService,
    () => clampMinutes(configuration().get("warningPercent", 72), 1, 100),
    () => clampMinutes(configuration().get("criticalPercent", 90), 1, 100),
  );
  const statusBars = new StatusBarController();
  const refreshManually = () => vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Window,
      title: "Refreshing UsageAI",
    },
    () => refreshService.refresh(true),
  );

  const updateUi = (states: readonly ProviderState[]) => {
    statusBars.update(states, getStatusBarProviderSelection(configuration(), enabledProviderIds()));
    const snapshots = Object.fromEntries(
      states.filter((state) => state.snapshot).map((state) => [state.id, state.snapshot]),
    ) as Record<string, UsageSnapshot>;
    void context.globalState.update(cacheKey, snapshots);
    void dashboard;
  };
  const removeListener = refreshService.onDidUpdate(updateUi);
  updateUi(refreshService.getStates());

  context.subscriptions.push(
    dashboard,
    statusBars,
    { dispose: removeListener },
    { dispose: () => refreshService.dispose() },
    vscode.window.registerWebviewViewProvider(UsageDashboardViewProvider.viewType, dashboard),
    vscode.commands.registerCommand("usageai.show", async () => {
      await vscode.commands.executeCommand("workbench.view.extension.usageai");
      await vscode.commands.executeCommand("usageai.dashboard.focus");
    }),
    vscode.commands.registerCommand("usageai.refresh", refreshManually),
    vscode.commands.registerCommand("usageai.openSettings", () =>
      vscode.commands.executeCommand("workbench.action.openSettings", "@ext:vladikogan.usageai")),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration("usageai")) {
        refreshService.configurationChanged();
        updateUi(refreshService.getStates());
      }
    }),
  );
  refreshService.start();
}

export function deactivate(): void {
  // VS Code disposes the subscriptions registered by activate.
}

export function sanitizeProviderIds(values: readonly string[]): UsageProviderId[] {
  const allowed = new Set<UsageProviderId>(["codex", "claude", "copilot", "gemini"]);
  return [...new Set(values.filter((value): value is UsageProviderId => allowed.has(value as UsageProviderId)))];
}

const statusBarCheckboxKeys = [
  "statusBarProviders.hottestWhenNoneSelected",
  "statusBarProviders.codex",
  "statusBarProviders.claude",
  "statusBarProviders.copilot",
  "statusBarProviders.gemini",
] as const;

export function getStatusBarProviderSelection(
  configuration: vscode.WorkspaceConfiguration,
  providerOrder: readonly UsageProviderId[],
): unknown {
  const usesCheckboxes = statusBarCheckboxKeys.some((key) => isExplicitlyConfigured(configuration, key));
  if (!usesCheckboxes) {
    return configuration.get<unknown>("statusBarProvider", ["hottest"]);
  }

  return statusBarProvidersFromCheckboxes({
    hottestWhenNoneSelected: configuration.get(statusBarCheckboxKeys[0], true),
    codex: configuration.get(statusBarCheckboxKeys[1], false),
    claude: configuration.get(statusBarCheckboxKeys[2], false),
    copilot: configuration.get(statusBarCheckboxKeys[3], false),
    gemini: configuration.get(statusBarCheckboxKeys[4], false),
  }, providerOrder);
}

function isExplicitlyConfigured(configuration: vscode.WorkspaceConfiguration, key: string): boolean {
  const inspected = configuration.inspect<boolean>(key);
  return Boolean(inspected && (
    inspected.globalValue !== undefined
    || inspected.workspaceValue !== undefined
    || inspected.workspaceFolderValue !== undefined
  ));
}

export function clampMinutes(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, Number.isFinite(value) ? value : minimum));
}

export function sanitizeCachedSnapshots(value: unknown): Record<string, UsageSnapshot> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return {};
  }

  const snapshots: Record<string, UsageSnapshot> = {};
  for (const [providerId, candidate] of Object.entries(value)) {
    if (isCachedSnapshot(candidate, providerId)) {
      snapshots[providerId] = candidate;
    }
  }
  return snapshots;
}

function isCachedSnapshot(value: unknown, providerId: string): value is UsageSnapshot {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return false;
  }
  const snapshot = value as Partial<UsageSnapshot>;
  return snapshot.providerId === providerId
    && ["codex", "claude", "copilot", "gemini"].includes(providerId)
    && typeof snapshot.providerName === "string"
    && typeof snapshot.plan === "string"
    && typeof snapshot.fetchedAt === "string"
    && Number.isFinite(Date.parse(snapshot.fetchedAt))
    && Array.isArray(snapshot.metrics)
    && snapshot.metrics.every((metric) => {
      if (typeof metric !== "object" || metric === null || Array.isArray(metric)) {
        return false;
      }
      const candidate = metric as Partial<UsageSnapshot["metrics"][number]>;
      return typeof candidate.name === "string"
        && ["session", "rolling", "monthly", "balance"].includes(candidate.kind ?? "")
        && (candidate.usedPercent === null || (
          typeof candidate.usedPercent === "number"
          && Number.isFinite(candidate.usedPercent)
          && candidate.usedPercent >= 0
          && candidate.usedPercent <= 100
        ));
    });
}
