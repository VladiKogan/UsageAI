import * as vscode from "vscode";
import { AccountService, accountsStateKey } from "./account-service";
import { isAccountUnitKey, providerKindOf, type AccountEntry, type AccountProviderId } from "./accounts";
import { addAccount, type AddAccountUi, type LoginLaunch } from "./add-account";
import { localExtensionPath, type ExtensionPathResolver } from "./bundled-cli";
import { dashboardExpandedProvidersKey, loadExpandedProviders, UsageDashboardViewProvider } from "./dashboard-view";
import { incidentSummary, isOutage, ProviderIncidentService } from "./incidents";
import {
  normalizeQuotaDisplay,
  statusBarProvidersFromCheckboxes,
  type ProviderState,
  type StatusBarProviderId,
  type UsageClient,
  type UsageSnapshot,
} from "./model";
import { ClaudeUsageClient, findClaudeLaunch, installedClaudeCliVersion, runClaudeAuthRecovery } from "./providers/claude";
import { CodexUsageClient, findCodexLaunch } from "./providers/codex";
import { CopilotUsageClient } from "./providers/copilot";
import { disposeAgyHub, GeminiUsageClient } from "./providers/gemini";
import { UsageRefreshService } from "./refresh-service";
import { userHome } from "./security";
import { StatusBarController } from "./status-bar";

const cacheKey = "usageai.cachedSnapshots.v1";
type UsageProviderId = Exclude<StatusBarProviderId, "hottest">;
const providerOrder: readonly UsageProviderId[] = ["codex", "claude", "copilot", "gemini"];
/** How often the once-a-day account scan asks whether it is due. */
const accountScanCheckMs = 60 * 60_000;

/**
 * Finds provider extensions for their bundled CLIs. Each lookup asks VS Code again, because provider
 * extensions can be installed or updated while UsageAI keeps running.
 */
export function bundledCliResolver(context: Pick<vscode.ExtensionContext, "extension">): ExtensionPathResolver {
  const ownKind = (context.extension as vscode.Extension<unknown> | undefined)?.extensionKind;
  return Object.assign(
    (extensionId: string) => localExtensionPath(vscode.extensions.getExtension(extensionId)),
    { runsLocallyInRemoteWindow: vscode.env.remoteName !== undefined && ownKind === vscode.ExtensionKind.UI },
  );
}

/**
 * The refresh units for the current accounts: each provider's default client (unless its default
 * login is hidden), then one client per other shown account. Account clients are kept while their
 * folder stays the same, so a unit's client state survives an unrelated account change.
 */
export function buildUsageClients(
  baseClients: Readonly<Record<UsageProviderId, UsageClient>>,
  accounts: readonly AccountEntry[] | undefined,
  createAccountClient: (account: AccountEntry) => UsageClient,
  cache: Map<string, UsageClient> = new Map(),
): UsageClient[] {
  const clients: UsageClient[] = [];
  const used = new Set<string>();
  for (const providerId of providerOrder) {
    if (providerId !== "claude" && providerId !== "codex") {
      clients.push(baseClients[providerId]);
      continue;
    }
    // Before the first account read, the default login shows exactly as before.
    const entries = accounts?.filter((account) => account.provider === providerId);
    if (!entries || entries.some((account) => account.source === "default")) {
      clients.push(baseClients[providerId]);
    }
    for (const account of entries ?? []) {
      if (account.source === "default") {
        continue;
      }
      const key = `${account.unitKey}|${account.path}`;
      const client = cache.get(key) ?? createAccountClient(account);
      cache.set(key, client);
      used.add(key);
      clients.push(client);
    }
  }
  for (const key of [...cache.keys()]) {
    if (!used.has(key)) {
      cache.delete(key);
    }
  }
  return clients;
}

/**
 * Extra Status Bar entries from `usageai.statusBarProviders.accounts`: each an account unit key
 * (`claude@1a2b3c4d`) or a shown account's label or email.
 */
export function resolveStatusBarAccounts(value: unknown, accounts: readonly AccountEntry[]): string[] {
  const names = Array.isArray(value) ? value.filter((entry): entry is string => typeof entry === "string") : [];
  const resolved: string[] = [];
  for (const name of names.slice(0, 16)) {
    const trimmed = name.trim();
    const lower = trimmed.toLowerCase();
    const match = isAccountUnitKey(trimmed)
      ? trimmed
      : accounts.find((account) => account.label.toLowerCase() === lower || account.identity.email?.toLowerCase() === lower)?.unitKey;
    if (match && !resolved.includes(match)) {
      resolved.push(match);
    }
  }
  return resolved;
}

export function activate(context: vscode.ExtensionContext): void {
  const extensionPath = bundledCliResolver(context);
  const configuration = () => vscode.workspace.getConfiguration("usageai");
  const claudeCliVersion = () => installedClaudeCliVersion(extensionPath);
  const baseClients: Record<UsageProviderId, UsageClient> = {
    codex: new CodexUsageClient(extensionPath),
    claude: new ClaudeUsageClient((signal) => runClaudeAuthRecovery(signal, extensionPath), undefined, claudeCliVersion),
    copilot: new CopilotUsageClient(() => configuration().get("enableGitHubCliFallback", false)),
    gemini: new GeminiUsageClient(),
  };
  // Another account's client reads only its own folder and gives that folder to every CLI child.
  const createAccountClient = (account: AccountEntry): UsageClient => account.provider === "claude"
    ? new ClaudeUsageClient(
      (signal) => runClaudeAuthRecovery(signal, extensionPath, account.path),
      { id: account.id, configDirectory: account.path },
      claudeCliVersion,
    )
    : new CodexUsageClient(extensionPath, {}, { id: account.id, home: account.path });
  const accountClients = new Map<string, UsageClient>();
  const accounts = new AccountService({
    home: userHome,
    settings: () => configuration().get<unknown>("accounts", []),
    loadState: () => context.globalState.get<unknown>(accountsStateKey),
    saveState: (state) => context.globalState.update(accountsStateKey, state),
    announce: (message) => void vscode.window.showInformationMessage(message),
    askToShow: (_account, message) => vscode.window.showInformationMessage(message, "Show", "Ignore")
      .then((choice) => choice === "Show" ? "show" : choice === "Ignore" ? "ignore" : undefined),
    warn: (message) => void vscode.window.showWarningMessage(message),
  });
  let accountsLoaded = false;
  const enabledProviderIds = () => sanitizeProviderIds(
    configuration().get<readonly string[]>("providers", [...providerOrder]),
  );
  const cached = sanitizeCachedSnapshots(context.globalState.get<unknown>(cacheKey, {}));
  const incidents = new ProviderIncidentService();
  const showIncidents = () => configuration().get("showProviderIncidents", true) === true;
  const refreshService = new UsageRefreshService({
    clients: buildUsageClients(baseClients, undefined, createAccountClient, accountClients),
    enabledProviderIds,
    visibleIntervalMs: () => clampMinutes(configuration().get("refreshIntervalMinutes", 5), 1, 120) * 60_000,
    backgroundIntervalMs: () => clampMinutes(configuration().get("backgroundRefreshIntervalMinutes", 15), 5, 240) * 60_000,
    initialSnapshots: cached,
    incidentFor: (providerId) => {
      const incident = showIncidents() ? incidents.get(providerId) : undefined;
      return incident ? { summary: incidentSummary(incident), isOutage: isOutage(incident) } : undefined;
    },
    accountLabelFor: (unit) => accounts.accounts.find((account) => account.unitKey === unit)?.label,
  });
  // The status pages are checked at most every five minutes; this only asks whether a check is due.
  const checkIncidents = () => void incidents.refreshIfDue(showIncidents());
  const incidentTimer = setInterval(checkIncidents, 60_000);
  incidentTimer.unref();
  const removeIncidentListener = incidents.onDidChange(() => refreshService.incidentsChanged());
  const dashboard = new UsageDashboardViewProvider(
    refreshService,
    () => clampMinutes(configuration().get("warningPercent", 72), 1, 100),
    () => clampMinutes(configuration().get("criticalPercent", 90), 1, 100),
    {
      loadExpandedProviders: () => loadExpandedProviders(
        (key) => context.globalState.get<unknown>(key),
        (key, value) => context.globalState.update(key, [...value]),
      ),
      saveExpandedProviders: (providerIds) => context.globalState.update(
        dashboardExpandedProvidersKey,
        [...providerIds],
      ),
      metricDisplayMode: () => configuration().get<unknown>("metricDisplayMode", "all"),
      quotaDisplay: () => configuration().get<unknown>("quotaDisplay", "used"),
      setMetricDisplayMode: (mode) => configuration().update(
        "metricDisplayMode",
        mode,
        vscode.ConfigurationTarget.Global,
      ),
    },
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
    statusBars.update(
      states,
      getStatusBarProviderSelection(configuration(), enabledProviderIds(), accounts.accounts),
      normalizeQuotaDisplay(configuration().get<unknown>("quotaDisplay", "used")),
    );
    // Until the accounts are read, the cached readings of the other accounts are kept, not dropped.
    const snapshots = {
      ...(accountsLoaded ? {} : cached),
      ...Object.fromEntries(states.filter((state) => state.snapshot).map((state) => [state.id, state.snapshot])),
    } as Record<string, UsageSnapshot>;
    void context.globalState.update(cacheKey, snapshots);
    void dashboard;
  };
  const removeListener = refreshService.onDidUpdate(updateUi);
  updateUi(refreshService.getStates());

  const removeAccountListener = accounts.onDidChange((shown) => {
    accountsLoaded = true;
    refreshService.setClients(buildUsageClients(baseClients, shown, createAccountClient, accountClients));
  });
  // Found logins are looked for at activation and once a day, never on a refresh tick.
  void accounts.scan().catch(() => undefined);
  const accountScanTimer = setInterval(() => void accounts.scanIfDue().catch(() => undefined), accountScanCheckMs);
  accountScanTimer.unref();
  const findLogin = async (provider: AccountProviderId): Promise<LoginLaunch | undefined> => {
    if (provider === "claude") {
      const launch = await findClaudeLaunch(extensionPath);
      return launch ? { executable: launch.executable, args: launch.argsPrefix } : undefined;
    }
    try {
      return await findCodexLaunch(extensionPath);
    } catch {
      return undefined;
    }
  };

  context.subscriptions.push(
    dashboard,
    statusBars,
    { dispose: removeListener },
    { dispose: removeAccountListener },
    { dispose: () => clearInterval(accountScanTimer) },
    { dispose: () => accounts.dispose() },
    { dispose: removeIncidentListener },
    { dispose: () => clearInterval(incidentTimer) },
    { dispose: () => refreshService.dispose() },
    vscode.window.registerWebviewViewProvider(UsageDashboardViewProvider.viewType, dashboard),
    vscode.commands.registerCommand("usageai.show", async () => {
      await vscode.commands.executeCommand("workbench.view.extension.usageai");
      await vscode.commands.executeCommand("usageai.dashboard.focus");
    }),
    vscode.commands.registerCommand("usageai.refresh", refreshManually),
    vscode.commands.registerCommand("usageai.openSettings", () =>
      vscode.commands.executeCommand("workbench.action.openSettings", "@ext:vladikogan.usageai")),
    vscode.commands.registerCommand("usageai.addAccount", () => addAccount({
      ui: vscodeAddAccountUi(),
      home: userHome,
      accounts: () => accounts.accounts,
      configuredAccounts: () => configuration().get<unknown>("accounts", []),
      saveConfiguredAccounts: (value) => configuration().update("accounts", value, vscode.ConfigurationTarget.Global),
      findLogin,
      canRunLoginInTerminal: () => !extensionPath.runsLocallyInRemoteWindow,
      afterAdd: async () => {
        await accounts.reload();
        await accounts.scan();
      },
    })),
    vscode.commands.registerCommand("usageai.rescanAccounts", async () => {
      const result = await accounts.scan();
      if (result.pending === 0) {
        void vscode.window.showInformationMessage("UsageAI found no new accounts.");
      }
    }),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration("usageai.accounts")) {
        void accounts.reload().catch(() => undefined);
      }
      if (event.affectsConfiguration("usageai")) {
        refreshService.configurationChanged();
        updateUi(refreshService.getStates());
        dashboard.configurationChanged();
        checkIncidents();
      }
    }),
  );
  refreshService.start();
  checkIncidents();
}

export function deactivate(): void {
  // VS Code disposes the subscriptions registered by activate, but the Antigravity CLI hub is a child
  // process this extension owns for the whole session.
  disposeAgyHub();
}

/** The Add Account flow's editor surfaces. */
function vscodeAddAccountUi(): AddAccountUi {
  return {
    pick: (items, title, placeHolder) => vscode.window.showQuickPick([...items], { title, placeHolder, ignoreFocusOut: true }),
    input: (options) => vscode.window.showInputBox({
      title: options.title,
      prompt: options.prompt,
      ignoreFocusOut: true,
      ...(options.value !== undefined ? { value: options.value } : {}),
      ...(options.placeHolder !== undefined ? { placeHolder: options.placeHolder } : {}),
      ...(options.validate ? { validateInput: options.validate } : {}),
    }),
    pickFolder: async (title) => (await vscode.window.showOpenDialog({
      title,
      canSelectFiles: false,
      canSelectFolders: true,
      canSelectMany: false,
      openLabel: "Use this folder",
      defaultUri: vscode.Uri.file(userHome()),
    }))?.[0]?.fsPath,
    info: (message, ...actions) => vscode.window.showInformationMessage(message, ...actions),
    error: (message) => void vscode.window.showErrorMessage(message),
    copy: (text) => vscode.env.clipboard.writeText(text),
    runLogin: (options) => {
      const terminal = vscode.window.createTerminal({
        name: options.name,
        shellPath: options.executable,
        shellArgs: [...options.args],
        env: { ...options.environment },
      });
      terminal.show();
      const closed = new Promise<void>((resolve) => {
        const listener = vscode.window.onDidCloseTerminal((candidate) => {
          if (candidate === terminal) {
            listener.dispose();
            resolve();
          }
        });
      });
      return { closed };
    },
    waitWithProgress: (title, task) => vscode.window.withProgress(
      { location: vscode.ProgressLocation.Notification, title, cancellable: true },
      (_progress, token) => task(() => token.isCancellationRequested),
    ),
  };
}

export function sanitizeProviderIds(values: readonly string[]): UsageProviderId[] {
  const allowed = new Set<UsageProviderId>(providerOrder);
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
  providerIds: readonly UsageProviderId[],
  accounts: readonly AccountEntry[] = [],
): unknown {
  const selection = statusBarProviderSelection(configuration, providerIds);
  // Accounts named in their own setting join whatever the checkboxes chose; a provider's own entry
  // keeps following its primary account.
  const extra = resolveStatusBarAccounts(configuration.get<unknown>("statusBarProviders.accounts", []), accounts)
    .filter((unit) => providerIds.includes(providerKindOf(unit) as UsageProviderId));
  if (extra.length === 0) {
    return selection;
  }
  const base = typeof selection === "string" ? [selection] : Array.isArray(selection) ? selection : [];
  return [...base, ...extra];
}

function statusBarProviderSelection(
  configuration: vscode.WorkspaceConfiguration,
  providerIds: readonly UsageProviderId[],
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
  }, providerIds);
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
  for (const [unit, candidate] of Object.entries(value)) {
    if (isCachedSnapshot(candidate, unit)) {
      snapshots[unit] = candidate;
    }
  }
  return snapshots;
}

/** Keyed by unit: the provider id, or `claude@1a2b3c4d` for another account of that provider. */
function isCachedSnapshot(value: unknown, unit: string): value is UsageSnapshot {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return false;
  }
  const snapshot = value as Partial<UsageSnapshot>;
  const providerId = providerKindOf(unit);
  return snapshot.providerId === providerId
    && ["codex", "claude", "copilot", "gemini"].includes(providerId)
    && (unit === providerId || isAccountUnitKey(unit))
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
