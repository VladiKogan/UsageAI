import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { createRequire } from "node:module";
import * as os from "node:os";
import * as path from "node:path";
import test, { mock } from "node:test";
import vm from "node:vm";
import type { AccountEntry } from "../src/accounts";
import type { ProviderState, UsageClient, UsageSnapshot } from "../src/model";

const localRequire = createRequire(__filename);
const moduleLoader = localRequire("node:module") as {
  _load(request: string, parent: unknown, isMain: boolean): unknown;
};
const originalLoad = moduleLoader._load;

class FakeMarkdownString {
  public value = "";
  public isTrusted: unknown;

  public constructor(value?: string) {
    this.value = value ?? "";
  }

  public appendMarkdown(value: string): this {
    this.value += value;
    return this;
  }

  public appendText(value: string): this {
    this.value += value;
    return this;
  }

  public appendCodeblock(value: string, language?: string): this {
    this.value += `\n\n\`\`\`${language ?? ""}\n${value}\n\`\`\``;
    return this;
  }
}

interface FakeStatusBarItem {
  name?: string;
  command?: string;
  text?: string;
  tooltip?: string | FakeMarkdownString;
  readonly priority: number;
  shown: boolean;
  disposed: boolean;
  show(): void;
  dispose(): void;
}

const statusItems: FakeStatusBarItem[] = [];
const executedCommands: Array<{ readonly command: string; readonly args: readonly unknown[] }> = [];
const registeredCommands = new Map<string, (...args: readonly unknown[]) => unknown>();
const informationMessages: string[] = [];
const openedUris: string[] = [];
const clipboardWrites: string[] = [];
const configurationUpdates: Array<{ readonly key: string; readonly value: unknown; readonly target: unknown }> = [];
/** A setting whose update the fake window refuses, as one it has not registered. */
let unregisteredConfigurationKey: string | undefined;
let quickPickAnswer: ((items: ReadonlyArray<{ readonly label: string; readonly picked?: boolean }>) => unknown) | undefined;
let configurationListener: ((event: { affectsConfiguration(section: string): boolean }) => void) | undefined;
let registeredDashboard: unknown;

const configurationValues = new Map<string, unknown>();
const inspectedConfiguration = new Set<string>();
const configuration = {
  get<T>(key: string, fallback?: T): T {
    return (configurationValues.has(key) ? configurationValues.get(key) : fallback) as T;
  },
  inspect<T>(key: string): { readonly globalValue?: T } | undefined {
    return inspectedConfiguration.has(key) ? { globalValue: configurationValues.get(key) as T } : undefined;
  },
  async update(key: string, value: unknown, target: unknown): Promise<void> {
    configurationValues.set(key, value);
    if (key === unregisteredConfigurationKey) {
      throw new Error(`Unable to write to User Settings because usageai.${key} is not a registered configuration.`);
    }
    configurationUpdates.push({ key, value, target });
  },
};

class FakeDomElement {
  public className = "";
  public textContent = "";
  public value = "";
  public hidden = false;
  public id = "";
  public max = 0;
  public dataset: Record<string, string> = {};
  public readonly children: FakeDomElement[] = [];
  private readonly listeners = new Map<string, Array<() => void>>();

  public constructor(public readonly tagName: string) {}

  public append(...children: FakeDomElement[]): void { this.children.push(...children); }
  public replaceChildren(...children: FakeDomElement[]): void {
    this.children.length = 0;
    this.children.push(...children);
  }
  public setAttribute(_name: string, _value: string): void {}
  public addEventListener(type: string, listener: () => void): void {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }
  public dispatch(type: string): void {
    for (const listener of this.listeners.get(type) ?? []) listener();
  }
}

const installedExtensions = new Map<string, unknown>();
let remoteName: string | undefined;

const vscodeStub = {
  StatusBarAlignment: { Left: 1 },
  ProgressLocation: { Window: 1 },
  ConfigurationTarget: { Global: 1, Workspace: 2 },
  QuickPickItemKind: { Separator: -1 },
  MarkdownString: FakeMarkdownString,
  Uri: { parse: (value: string) => ({ value, toString: () => value }) },
  commands: {
    async executeCommand(command: string, ...args: readonly unknown[]) {
      executedCommands.push({ command, args });
    },
    registerCommand(command: string, handler: (...args: readonly unknown[]) => unknown) {
      registeredCommands.set(command, handler);
      return { dispose() { registeredCommands.delete(command); } };
    },
  },
  ExtensionKind: { UI: 1, Workspace: 2 },
  env: {
    get remoteName() { return remoteName; },
    async openExternal(uri: { toString(): string }) { openedUris.push(uri.toString()); },
    clipboard: { async writeText(value: string) { clipboardWrites.push(value); } },
  },
  window: {
    createStatusBarItem(_alignment: number, priority: number): FakeStatusBarItem {
      const item: FakeStatusBarItem = {
        priority,
        shown: false,
        disposed: false,
        show() { this.shown = true; },
        dispose() { this.disposed = true; },
      };
      statusItems.push(item);
      return item;
    },
    registerWebviewViewProvider(_viewType: string, provider: unknown) {
      registeredDashboard = provider;
      return { dispose() {} };
    },
    withProgress<T>(_options: unknown, task: () => T): T { return task(); },
    showInformationMessage(message: string) { informationMessages.push(message); return Promise.resolve(undefined); },
    showWarningMessage(message: string) { informationMessages.push(message); return Promise.resolve(undefined); },
    showErrorMessage(message: string) { informationMessages.push(message); return Promise.resolve(undefined); },
    async showQuickPick(items: ReadonlyArray<{ readonly label: string; readonly picked?: boolean }>) {
      return quickPickAnswer?.(items);
    },
  },
  extensions: {
    getExtension: (id: string) => installedExtensions.get(id),
  },
  workspace: {
    getConfiguration: () => configuration,
    onDidChangeConfiguration(listener: typeof configurationListener) {
      configurationListener = listener;
      return { dispose() { configurationListener = undefined; } };
    },
  },
};

moduleLoader._load = (request, parent, isMain) => request === "vscode"
  ? vscodeStub
  : originalLoad.call(moduleLoader, request, parent, isMain);

const { StatusBarController } = localRequire("../src/status-bar") as typeof import("../src/status-bar");
const dashboardModule = localRequire("../src/dashboard-view") as typeof import("../src/dashboard-view");
const { UsageDashboardViewProvider } = dashboardModule;
const extensionModule = localRequire("../src/extension") as typeof import("../src/extension");
const { UsageRefreshService } = localRequire("../src/refresh-service") as typeof import("../src/refresh-service");
moduleLoader._load = originalLoad;

const snapshot = (providerId: string, usedPercent: number): UsageSnapshot => ({
  plan: "Pro",
  metrics: [
    { name: "5-hour", kind: "session", usedPercent },
    { name: "Weekly", kind: "rolling", usedPercent: Math.max(0, usedPercent - 10) },
  ],
  fetchedAt: "2026-08-17T12:00:00Z",
  providerId,
  providerName: providerId,
});

test("status bar renders hottest, stale, error, and selection lifecycle states", () => {
  statusItems.length = 0;
  const states: ProviderState[] = [
    {
      id: "codex",
      displayName: "Codex",
      signInCommand: "codex login",
      accountUrl: "https://example.com/codex",
      snapshot: snapshot("codex", 45),
      stale: false,
      refreshing: false,
    },
    {
      id: "claude",
      displayName: "Claude Code",
      signInCommand: "claude",
      accountUrl: "https://example.com/claude",
      snapshot: snapshot("claude", 82),
      stale: true,
      refreshing: false,
      error: "Temporary failure.",
      lastAttemptedAt: "2026-08-17T12:05:00Z",
      nextRefreshAt: "2026-08-17T12:10:00Z",
    },
  ];
  const controller = new StatusBarController();
  controller.update(states, ["hottest"]);
  assert.equal(statusItems.length, 1);
  assert.match(statusItems[0]?.text ?? "", /Claude 82%/);
  const tooltip = statusItems[0]?.tooltip;
  assert.ok(tooltip instanceof FakeMarkdownString);
  assert.match(tooltip.value, /stale/i);
  assert.match(tooltip.value, /Last successful update/i);
  assert.match(tooltip.value, /Automatic retry/i);

  const { snapshot: _snapshot, ...disconnectedClaude } = states[1]!;
  controller.update([
    states[0]!,
    { ...disconnectedClaude, stale: false, error: "Sign in required." },
  ], ["codex", "claude"]);
  assert.equal(statusItems[0]?.disposed, true);
  assert.equal(statusItems.length, 3);
  assert.equal(statusItems[1]?.priority, 25);
  assert.equal(statusItems[2]?.priority, 24);
  assert.match(statusItems[2]?.text ?? "", /warning/);
  controller.dispose();
  assert.equal(statusItems[1]?.disposed, true);
  assert.equal(statusItems[2]?.disposed, true);
});

test("status bar marks a provider's status-page incident and names it in the tooltip", () => {
  statusItems.length = 0;
  const controller = new StatusBarController();
  controller.update([{
    id: "claude",
    displayName: "Claude Code",
    signInCommand: "claude",
    accountUrl: "https://example.com/claude",
    snapshot: snapshot("claude", 40),
    stale: false,
    refreshing: false,
    incident: "Partial outage: Elevated errors",
    incidentIsOutage: true,
  }], ["claude"]);
  assert.match(statusItems[0]?.text ?? "", /\$\(error\)$/);
  const tooltip = statusItems[0]?.tooltip;
  assert.ok(tooltip instanceof FakeMarkdownString);
  assert.match(tooltip.value, /Status page:.*Partial outage: Elevated errors/s);
  controller.update([{
    id: "claude",
    displayName: "Claude Code",
    signInCommand: "claude",
    accountUrl: "https://example.com/claude",
    snapshot: snapshot("claude", 40),
    stale: false,
    refreshing: false,
  }], ["claude"]);
  assert.doesNotMatch(statusItems.at(-1)?.text ?? "", /\$\((error|warning)\)/);
  controller.dispose();
});

test("status bar shows remaining quota with one trailing unit when asked", () => {
  statusItems.length = 0;
  const provider = (id: string, displayName: string, metrics: UsageSnapshot["metrics"]): ProviderState => ({
    id,
    displayName,
    signInCommand: id,
    accountUrl: `https://example.com/${id}`,
    snapshot: { ...snapshot(id, 0), metrics },
    stale: false,
    refreshing: false,
  });
  const states: ProviderState[] = [
    provider("codex", "Codex", snapshot("codex", 45).metrics),
    provider("claude", "Claude Code", snapshot("claude", 82).metrics),
    provider("gemini", "Gemini", [{ name: "Credits", kind: "balance", usedPercent: null, remainingText: "$5" }]),
  ];
  const controller = new StatusBarController();

  controller.update(states, ["hottest"]);
  assert.match(statusItems[0]?.text ?? "", /Claude 82% \| 72%$/);

  // Hottest still means most used, which is the provider with the least left.
  controller.update(states, ["hottest"], "remaining");
  assert.match(statusItems[0]?.text ?? "", /Claude 18% \| 28% left$/);
  const tooltip = statusItems[0]?.tooltip;
  assert.ok(tooltip instanceof FakeMarkdownString);
  assert.match(tooltip.value, /5-hour {2}■ ■ □ □ □ □ □ □ □ □ {2}18% left/);
  assert.match(tooltip.value, /Weekly {2}■ ■ ■ □ □ □ □ □ □ □ {2}28% left/);

  controller.update(states, ["codex", "gemini"], "remaining");
  assert.match(statusItems[1]?.text ?? "", /Codex 55% \| 65% left$/);
  assert.match(statusItems[2]?.text ?? "", /Gemini —$/);
  controller.update(states, ["codex", "gemini"], "used");
  assert.match(statusItems[2]?.text ?? "", /Gemini 0%$/);
  controller.dispose();
});

test("dashboard wires visibility, safe messages, state posting, and CSP", async () => {
  executedCommands.length = 0;
  openedUris.length = 0;
  clipboardWrites.length = 0;
  informationMessages.length = 0;
  const states: ProviderState[] = [{
    id: "codex",
    displayName: "Codex",
    signInCommand: "codex login",
    accountUrl: "https://example.com/codex",
    snapshot: snapshot("codex", 45),
    stale: false,
    refreshing: false,
  }];
  let updateListener: ((states: readonly ProviderState[]) => void) | undefined;
  const visibility: boolean[] = [];
  const service = {
    onDidUpdate(listener: (next: readonly ProviderState[]) => void) {
      updateListener = listener;
      return () => { updateListener = undefined; };
    },
    getStates: () => states,
    setVisible(value: boolean) { visibility.push(value); },
  } as unknown as InstanceType<typeof UsageRefreshService>;
  const posted: unknown[] = [];
  let messageHandler: ((message: unknown) => Promise<void>) | undefined;
  let visibilityHandler: (() => void) | undefined;
  let disposeHandler: (() => void) | undefined;
  const webview = {
    options: {} as unknown,
    html: "",
    postMessage(message: unknown) { posted.push(message); return Promise.resolve(true); },
    onDidReceiveMessage(handler: (message: unknown) => Promise<void>) {
      messageHandler = handler;
      return { dispose() {} };
    },
  };
  const view = {
    visible: true,
    webview,
    onDidChangeVisibility(handler: () => void) {
      visibilityHandler = handler;
      return { dispose() {} };
    },
    onDidDispose(handler: () => void) {
      disposeHandler = handler;
      return { dispose() {} };
    },
  };

  const savedExpanded: string[][] = [];
  const changedModes: string[] = [];
  const dashboard = new UsageDashboardViewProvider(service, () => 72, () => 90, {
    loadExpandedProviders: () => ["codex", "codex", "unknown", 42],
    saveExpandedProviders: (ids) => { savedExpanded.push([...ids]); },
    metricDisplayMode: () => "important",
    setMetricDisplayMode: (mode) => { changedModes.push(mode); },
  });
  dashboard.resolveWebviewView(view as never);
  assert.deepEqual(webview.options, { enableScripts: true, localResourceRoots: [] });
  assert.match(webview.html, /default-src 'none'/);
  assert.match(webview.html, /Last good/);
  assert.match(webview.html, /Retry at/);
  assert.match(webview.html, /aria-expanded/);
  assert.match(webview.html, /el\('button', 'card-toggle'\)/);
  assert.match(webview.html, /body\.hidden = !expanded/);
  assert.match(webview.html, /head\.type = 'button'/);
  assert.match(webview.html, /textContent = text/);
  assert.match(webview.html, /Collapse all/);
  assert.match(webview.html, /No metered limits reported/);
  assert.match(webview.html, /prefers-reduced-motion/);
  assert.doesNotMatch(webview.html, /innerHTML/);
  const inlineScript = webview.html.match(/<script[^>]*>([\s\S]*)<\/script>/)?.[1];
  assert.ok(inlineScript);
  assert.doesNotThrow(() => new vm.Script(inlineScript));
  const runtimeElements = new Map<string, FakeDomElement>([
    ["providers", new FakeDomElement("main")],
    ["stamp", new FakeDomElement("span")],
    ["metric-mode", new FakeDomElement("select")],
    ["collapse-all", new FakeDomElement("button")],
    ["expand-all", new FakeDomElement("button")],
  ]);
  const runtimeMessages: unknown[] = [];
  let stateMessage: ((event: { readonly data: unknown }) => void) | undefined;
  const runtime = vm.createContext({
    acquireVsCodeApi: () => ({ postMessage: (message: unknown) => runtimeMessages.push(message) }),
    document: {
      getElementById: (id: string) => runtimeElements.get(id),
      createElement: (tagName: string) => new FakeDomElement(tagName),
    },
    window: {
      addEventListener: (type: string, listener: (event: { readonly data: unknown }) => void) => {
        if (type === "message") stateMessage = listener;
      },
    },
  });
  new vm.Script(inlineScript).runInContext(runtime);
  assert.ok(stateMessage);
  const filterSnapshot: UsageSnapshot = {
    ...snapshot("codex", 45),
    metrics: [
      { name: "Balance", kind: "balance", usedPercent: null },
      { name: "Session lower", kind: "session", usedPercent: 20 },
      { name: "Session higher", kind: "session", usedPercent: 80 },
      { name: "Weekly", kind: "rolling", usedPercent: 50 },
      { name: "Monthly", kind: "monthly", usedPercent: 30 },
      { name: "Unlimited", kind: "monthly", usedPercent: 100, isUnlimited: true },
    ],
  };
  const filterState: ProviderState = {
    ...states[0]!,
    snapshot: filterSnapshot,
  };
  const metricCount = (mode: string): number => {
    stateMessage?.({
      data: {
        type: "states",
        states: [filterState],
        warningPercent: 72,
        criticalPercent: 90,
        metricDisplayMode: mode,
        expandedProviders: ["codex"],
      },
    });
    const runtimeProviders = runtimeElements.get("providers");
    const card = runtimeProviders?.children[0];
    const body = card?.children[1];
    return body?.children[0]?.children.length ?? -1;
  };
  assert.equal(metricCount("all"), 6);
  assert.equal(metricCount("metered"), 4);
  assert.equal(metricCount("important"), 3);
  const runtimeMode = runtimeElements.get("metric-mode");
  assert.ok(runtimeMode);
  runtimeMode.value = "metered";
  runtimeMode.dispatch("change");
  assert.equal(JSON.stringify(runtimeMessages.at(-1)), JSON.stringify({
    type: "setMetricDisplayMode",
    mode: "metered",
  }));
  const runtimeProviders = runtimeElements.get("providers");
  assert.equal(runtimeProviders?.children[0]?.children[1]?.children[0]?.children.length, 4);

  const renderCard = (quotaDisplay: unknown, expanded: boolean, state: ProviderState = filterState) => {
    stateMessage?.({
      data: {
        type: "states",
        states: [state],
        warningPercent: 72,
        criticalPercent: 90,
        metricDisplayMode: "all",
        quotaDisplay,
        expandedProviders: expanded ? ["codex"] : [],
      },
    });
    const card = runtimeElements.get("providers")?.children[0];
    const [, providerColumn, headline] = card?.children[0]?.children ?? [];
    const metric = card?.children[1]?.children[0]?.children[1];
    const [metricHead, rail, foot] = metric?.children ?? [];
    return {
      headline: headline?.children.map((child) => child.textContent),
      summary: providerColumn?.children[2]?.textContent,
      level: card?.dataset.level,
      metricValue: metricHead?.children[1]?.textContent,
      rail: rail?.value as unknown,
      foot: foot?.children[0]?.textContent,
    };
  };
  // Session higher is 80% used: the card headline, summary, and level follow it in both modes.
  const usedCard = renderCard(undefined, true);
  assert.deepEqual(usedCard.headline, ["80%"]);
  assert.equal(usedCard.level, "warning");
  assert.equal(usedCard.metricValue, "20% used");
  assert.equal(usedCard.rail, 20);
  assert.equal(usedCard.foot, "80% left");
  assert.equal(renderCard("used", false).summary, "Session higher · 80% · Reset not reported");

  const remainingCard = renderCard("remaining", true);
  assert.deepEqual(remainingCard.headline, ["20%", "left"]);
  assert.equal(remainingCard.level, "warning");
  assert.equal(remainingCard.metricValue, "80% left");
  assert.equal(remainingCard.rail, 80);
  assert.equal(remainingCard.foot, "20% used");
  assert.equal(renderCard("remaining", false).summary, "Session higher · 20% left · Reset not reported");
  assert.deepEqual(renderCard("unexpected", true).headline, ["80%"]);
  const unmetered: ProviderState = {
    ...filterState,
    snapshot: { ...filterSnapshot, metrics: [{ name: "Balance", kind: "balance", usedPercent: null }] },
  };
  assert.deepEqual(renderCard("remaining", true, unmetered).headline, ["—"]);
  assert.deepEqual(renderCard("used", true, unmetered).headline, ["0%"]);
  assert.equal(visibility.at(-1), true);
  assert.ok(posted.length >= 1);
  assert.deepEqual(posted[0], {
    type: "states",
    states,
    warningPercent: 72,
    criticalPercent: 90,
    metricDisplayMode: "important",
    quotaDisplay: "used",
    expandedProviders: ["codex"],
  });

  await messageHandler?.({ type: "ready" });
  await messageHandler?.({ type: "refresh", providerId: "codex" });
  await messageHandler?.({ type: "settings" });
  await messageHandler?.({ type: "openAccount", providerId: "codex" });
  await messageHandler?.({ type: "copySignIn", providerId: "codex" });
  await messageHandler?.({ type: "setProviderExpanded", providerId: "codex", expanded: false });
  await messageHandler?.({ type: "setProviderExpanded", providerId: "unknown", expanded: true });
  await messageHandler?.({ type: "setProviderExpanded", providerId: "claude", expanded: "yes" });
  await messageHandler?.({ type: "setAllExpanded", expanded: true });
  await messageHandler?.({ type: "setAllExpanded", expanded: "no" });
  await messageHandler?.({ type: "setAllExpanded", expanded: false });
  await messageHandler?.({ type: "setProviderExpanded", providerId: "gemini", expanded: true });
  await messageHandler?.({ type: "setMetricDisplayMode", mode: "metered" });
  await messageHandler?.({ type: "setMetricDisplayMode", mode: "invalid" });
  await messageHandler?.({ type: "openAccount", providerId: "unknown" });
  await messageHandler?.({ unsafe: true });
  assert.deepEqual(executedCommands.map((entry) => entry.command), ["usageai.refresh", "usageai.openSettings"]);
  assert.deepEqual(openedUris, ["https://example.com/codex"]);
  assert.deepEqual(clipboardWrites, ["codex login"]);
  assert.deepEqual(informationMessages, ["Copied: codex login"]);
  assert.deepEqual(savedExpanded, [
    [],
    ["codex", "claude", "copilot", "gemini"],
    [],
    ["gemini"],
  ]);
  assert.deepEqual(changedModes, ["metered"]);
  assert.equal((posted.at(-1) as { metricDisplayMode?: string }).metricDisplayMode, "metered");

  updateListener?.(states);
  const postedBeforeConfiguration = posted.length;
  dashboard.configurationChanged();
  assert.equal(posted.length, postedBeforeConfiguration + 1);
  view.visible = false;
  visibilityHandler?.();
  disposeHandler?.();
  assert.deepEqual(visibility.slice(-2), [false, false]);
  dashboard.dispose();
  assert.equal(updateListener, undefined);
});

test("expanded provider state defaults open and sanitizes restored ids", () => {
  assert.deepEqual(dashboardModule.sanitizeExpandedProviderIds(undefined), ["codex", "claude", "copilot", "gemini"]);
  assert.deepEqual(dashboardModule.sanitizeExpandedProviderIds(["gemini", "bad", "gemini", null]), ["gemini"]);
  assert.deepEqual(dashboardModule.sanitizeExpandedProviderIds([], ["codex"]), []);
});

test("dashboard expansion keeps attention temporary and summaries unfiltered", () => {
  const connected: ProviderState = {
    id: "gemini",
    displayName: "Gemini",
    signInCommand: "agy",
    accountUrl: "https://example.com/gemini",
    snapshot: {
      ...snapshot("gemini", 30),
      metrics: [
        { name: "Balance", kind: "balance", usedPercent: null },
        { name: "Gemini session", kind: "session", usedPercent: 88 },
        { name: "GPT session tie", kind: "session", usedPercent: 88 },
        { name: "Unlimited", kind: "monthly", usedPercent: 100, isUnlimited: true },
      ],
    },
    stale: false,
    refreshing: false,
  };
  assert.equal(dashboardModule.dashboardNeedsAttention(connected), false);
  assert.equal(dashboardModule.effectiveDashboardExpansion(connected, false), false);
  assert.equal(dashboardModule.effectiveDashboardExpansion(connected, true), true);
  assert.equal(dashboardModule.collapsedDashboardMetric(connected)?.name, "Gemini session");

  const stale = { ...connected, stale: true, error: "Temporary failure" };
  assert.equal(dashboardModule.dashboardNeedsAttention(stale), true);
  assert.equal(dashboardModule.effectiveDashboardExpansion(stale, false), true);
  const { error: _error, ...recovered } = stale;
  assert.equal(dashboardModule.effectiveDashboardExpansion({ ...recovered, stale: false }, false), false);

  const { snapshot: _connectedSnapshot, ...withoutSnapshot } = connected;
  const disconnected = { ...withoutSnapshot, error: "Sign in required" };
  assert.equal(dashboardModule.effectiveDashboardExpansion(disconnected, false), true);
  assert.equal(dashboardModule.collapsedDashboardMetric({}), undefined);
  assert.equal(dashboardModule.collapsedDashboardMetric({
    snapshot: { ...connected.snapshot!, metrics: [{ name: "Credits", kind: "balance", usedPercent: null }] },
  }), undefined);
});

test("activation helpers sanitize configuration and activation registers its surface", async () => {
  assert.deepEqual(extensionModule.sanitizeProviderIds(["gemini", "bad", "codex", "gemini"]), ["gemini", "codex"]);
  assert.equal(extensionModule.clampMinutes(Number.NaN, 1, 120), 1);
  assert.equal(extensionModule.clampMinutes(500, 1, 120), 120);
  assert.deepEqual(extensionModule.sanitizeCachedSnapshots(null), {});
  assert.deepEqual(extensionModule.sanitizeCachedSnapshots({
    codex: snapshot("codex", 45),
    malformed: { metrics: "not-an-array" },
    gemini: { ...snapshot("gemini", 20), fetchedAt: "invalid" },
  }), { codex: snapshot("codex", 45) });

  configurationValues.clear();
  configurationUpdates.length = 0;
  inspectedConfiguration.clear();
  configurationValues.set("providers", ["codex", "unknown"]);
  configurationValues.set("statusBarProvider", ["gemini"]);
  assert.deepEqual(extensionModule.getStatusBarProviderSelection(configuration as never, ["codex"]), ["gemini"]);
  configurationValues.set("statusBarProviders.codex", true);
  inspectedConfiguration.add("statusBarProviders.codex");
  assert.deepEqual(extensionModule.getStatusBarProviderSelection(configuration as never, ["codex"]), ["codex"]);
  configurationValues.set("quotaDisplay", "remaining");

  const subscriptions: Array<{ dispose(): unknown }> = [];
  const updates: unknown[] = [];
  const stateUpdates: Array<[string, unknown]> = [];
  // Activation lists the home folder for other logins; point it at an empty one.
  const previousHome = { USERPROFILE: process.env.USERPROFILE, HOME: process.env.HOME };
  const emptyHome = mkdtempSync(path.join(os.tmpdir(), "usageai-activation-home-"));
  process.env.USERPROFILE = emptyHome;
  process.env.HOME = emptyHome;
  const context = {
    subscriptions,
    globalState: {
      get: (key: string, fallback: unknown) => key === dashboardModule.dashboardExpandedProvidersKey
        ? ["claude"]
        : fallback,
      update: async (key: string, value: unknown) => { updates.push(value); stateUpdates.push([key, value]); },
    },
  };
  mock.method(UsageRefreshService.prototype, "start", () => {});
  try {
    extensionModule.activate(context as never);
    assert.deepEqual([...registeredCommands.keys()].sort(), [
      "usageai.addAccount",
      "usageai.chooseStatusBarItems",
      "usageai.manageAccounts",
      "usageai.openSettings",
      "usageai.refresh",
      "usageai.rescanAccounts",
      "usageai.show",
    ]);
    assert.ok(registeredDashboard instanceof UsageDashboardViewProvider);
    assert.ok(subscriptions.length >= 8);
    assert.ok(updates.length >= 1);
    configurationListener?.({ affectsConfiguration: (section) => section === "usageai" });
    const activatedDashboard = registeredDashboard as InstanceType<typeof UsageDashboardViewProvider>;
    const posted: unknown[] = [];
    let dashboardMessage: ((message: unknown) => Promise<void>) | undefined;
    activatedDashboard.resolveWebviewView({
      visible: true,
      webview: {
        options: {},
        html: "",
        postMessage(message: unknown) { posted.push(message); return Promise.resolve(true); },
        onDidReceiveMessage(handler: (message: unknown) => Promise<void>) {
          dashboardMessage = handler;
          return { dispose() {} };
        },
      },
      onDidChangeVisibility() { return { dispose() {} }; },
      onDidDispose() { return { dispose() {} }; },
    } as never);
    assert.deepEqual((posted[0] as { expandedProviders: string[] }).expandedProviders, ["claude"]);
    assert.equal((posted[0] as { quotaDisplay?: string }).quotaDisplay, "remaining");
    configurationValues.set("quotaDisplay", "inverted");
    configurationListener?.({ affectsConfiguration: (section) => section === "usageai" });
    assert.equal((posted.at(-1) as { quotaDisplay?: string }).quotaDisplay, "used");
    await dashboardMessage?.({ type: "setProviderExpanded", providerId: "claude", expanded: false });
    await dashboardMessage?.({ type: "setMetricDisplayMode", mode: "important" });
    // A dragged card is saved only for two shown cards; anything else is ignored.
    await dashboardMessage?.({ type: "moveCard", providerId: "codex", targetId: "nope", after: true });
    await dashboardMessage?.({ type: "moveCard", providerId: "codex", targetId: "claude", after: "yes" });
    assert.equal(stateUpdates.some(([key]) => key === dashboardModule.dashboardUnitOrderKey), false);
    assert.ok(updates.some((value) => Array.isArray(value) && value.length === 0));
    assert.deepEqual(configurationUpdates, [{ key: "metricDisplayMode", value: "important", target: 1 }]);
    // A card dropped on another provider's card moves the provider: only the provider order is
    // written, and the accounts' order, which did not change, is left alone.
    configurationValues.set("providers", ["codex", "claude"]);
    await dashboardMessage?.({ type: "moveCard", providerId: "codex", targetId: "claude", after: true });
    assert.deepEqual(configurationUpdates.at(-1), { key: "providers", value: ["claude", "codex"], target: 1 });
    assert.equal(stateUpdates.some(([key]) => key === dashboardModule.dashboardUnitOrderKey), false);
    configurationValues.set("providers", ["codex", "unknown"]);
    await registeredCommands.get("usageai.rescanAccounts")?.();
    assert.ok(informationMessages.includes("UsageAI found no new accounts."));

    // The Status Bar picker ticks from what is shown, skips a checkbox already holding its value
    // (Codex), sets the fallback so the checkboxes take over from the legacy setting, and keeps
    // the extra accounts in the extension's own state, never in a setting.
    configurationUpdates.length = 0;
    const offered: string[] = [];
    quickPickAnswer = (items) => {
      offered.push(...items.map((item) => `${item.label}${item.picked ? " ✓" : ""}`));
      return items.filter((item) => item.label === "Codex");
    };
    await registeredCommands.get("usageai.chooseStatusBarItems")?.();
    assert.deepEqual(offered, ["Codex ✓", "When nothing above is chosen", "Show the account with the highest usage ✓"]);
    assert.deepEqual(configurationUpdates.map((update) => [update.key, update.value]), [
      ["statusBarProviders.hottestWhenNoneSelected", false],
    ]);
    assert.deepEqual(stateUpdates.filter(([key]) => key === extensionModule.statusBarAccountsKey), [[extensionModule.statusBarAccountsKey, []]]);
    // A setting the window has not registered is refused, and the accounts are then not saved either.
    configurationUpdates.length = 0;
    stateUpdates.length = 0;
    unregisteredConfigurationKey = "statusBarProviders.hottestWhenNoneSelected";
    quickPickAnswer = (items) => items.filter((item) => item.label !== "Codex");
    await registeredCommands.get("usageai.chooseStatusBarItems")?.();
    assert.deepEqual(configurationUpdates.filter((update) => update.key === "statusBarProviders.hottestWhenNoneSelected"), []);
    assert.deepEqual(stateUpdates.filter(([key]) => key === extensionModule.statusBarAccountsKey), []);
    assert.ok(informationMessages.some((message) => message.startsWith("VS Code has not loaded UsageAI's newest settings")));
    unregisteredConfigurationKey = undefined;
    quickPickAnswer = undefined;
  } finally {
    mock.restoreAll();
    for (const subscription of subscriptions) subscription.dispose();
    for (const [name, value] of Object.entries(previousHome)) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
    rmSync(emptyHome, { recursive: true, force: true });
  }
});

test("a metric-mode change that cannot be saved reverts the dashboard's cached mode", async () => {
  // The mode is applied optimistically so the dashboard rerenders without waiting for configuration
  // propagation. A read-only or policy-locked setting must not leave that cache ahead of what the
  // Status Bar and the next reload read, and the rejection is never awaited by the message listener.
  const service = {
    onDidUpdate() { return () => {}; },
    getStates: () => [],
    setVisible() {},
  } as unknown as InstanceType<typeof UsageRefreshService>;

  const posted: { metricDisplayMode?: string }[] = [];
  let handler: ((message: unknown) => Promise<void>) | undefined;
  const dashboard = new UsageDashboardViewProvider(service, () => 72, () => 90, {
    metricDisplayMode: () => "all",
    setMetricDisplayMode: () => Promise.reject(new Error("setting is read-only")),
  });
  dashboard.resolveWebviewView({
    visible: true,
    webview: {
      options: {},
      html: "",
      postMessage(message: { metricDisplayMode?: string }) { posted.push(message); return Promise.resolve(true); },
      onDidReceiveMessage(next: (message: unknown) => Promise<void>) {
        handler = next;
        return { dispose() {} };
      },
    },
    onDidChangeVisibility() { return { dispose() {} }; },
    onDidDispose() { return { dispose() {} }; },
  } as never);

  await assert.doesNotReject(async () => handler?.({ type: "setMetricDisplayMode", mode: "important" }));
  // Applied immediately, then rolled back once the write failed.
  assert.equal(posted.at(-2)?.metricDisplayMode, "important");
  assert.equal(posted.at(-1)?.metricDisplayMode, "all");
});

test("the bundled-CLI resolver lends local extension folders and knows a remote window", () => {
  const root = process.cwd();
  installedExtensions.set("openai.chatgpt", { extensionPath: root, extensionUri: { scheme: "file" } });
  try {
    const local = extensionModule.bundledCliResolver({ extension: { extensionKind: 1 } } as never);
    assert.equal(local("openai.chatgpt"), root);
    assert.equal(local("anthropic.claude-code"), undefined);
    assert.equal(local.runsLocallyInRemoteWindow, false);

    // Connected to a remote host: running locally, UsageAI cannot see what is installed there.
    remoteName = "ssh-remote";
    assert.equal(extensionModule.bundledCliResolver({ extension: { extensionKind: 1 } } as never).runsLocallyInRemoteWindow, true);
    // Already running on the remote host, it sees that side's extensions itself.
    assert.equal(extensionModule.bundledCliResolver({ extension: { extensionKind: 2 } } as never).runsLocallyInRemoteWindow, false);
  } finally {
    remoteName = undefined;
    installedExtensions.clear();
  }
});

test("expansion state moves from v1 provider ids to v2 unit keys once", () => {
  const stored = new Map<string, unknown>([[dashboardModule.legacyDashboardExpandedProvidersKey, ["claude", "bogus", "codex"]]]);
  const writes: Array<[string, readonly string[]]> = [];
  const get = (key: string) => stored.get(key);
  const update = (key: string, value: readonly string[]) => { writes.push([key, value]); stored.set(key, value); };
  assert.equal(dashboardModule.dashboardExpandedProvidersKey, "usageai.dashboard.expandedProviders.v2");
  assert.deepEqual(dashboardModule.loadExpandedProviders(get, update), ["claude", "codex"]);
  assert.deepEqual(writes, [[dashboardModule.dashboardExpandedProvidersKey, ["claude", "codex"]]]);
  // Once migrated, the v2 value is read as it is and v1 is never consulted again.
  stored.set(dashboardModule.dashboardExpandedProvidersKey, ["claude@1a2b3c4d"]);
  assert.deepEqual(dashboardModule.loadExpandedProviders(get, update), ["claude@1a2b3c4d"]);
  assert.equal(writes.length, 1);
  assert.equal(dashboardModule.loadExpandedProviders(() => undefined, update), undefined);
  assert.deepEqual(
    dashboardModule.sanitizeExpandedProviderIds(["claude@1a2b3c4d", "gemini@1a2b3c4d", "claude@nothex!", "codex"]),
    ["claude@1a2b3c4d", "codex"],
  );
});

test("account cards keep their own expansion and Status Bar entries name the account", async () => {
  const state = (id: string, usedPercent: number, accountLabel?: string): ProviderState => ({
    id,
    displayName: accountLabel ? `Claude Code · ${accountLabel}` : "Claude Code",
    ...(accountLabel ? { accountLabel } : {}),
    signInCommand: "claude",
    accountUrl: "https://example.com/claude",
    snapshot: snapshot("claude", usedPercent),
    stale: false,
    refreshing: false,
  });
  let states = [state("claude", 30, "me@example.com"), state("claude@1a2b3c4d", 64, "Team")];
  const service = {
    onDidUpdate() { return () => {}; },
    getStates: () => states,
    setVisible() {},
  } as unknown as InstanceType<typeof UsageRefreshService>;
  const saved: Array<readonly string[]> = [];
  const posted: Array<{ expandedProviders: string[] }> = [];
  let handler: ((message: unknown) => Promise<void>) | undefined;
  const dashboard = new UsageDashboardViewProvider(service, () => 72, () => 90, {
    loadExpandedProviders: () => undefined,
    saveExpandedProviders: (ids) => { saved.push(ids); },
  });
  dashboard.resolveWebviewView({
    visible: true,
    webview: {
      options: {},
      html: "",
      postMessage(message: { expandedProviders: string[] }) { posted.push(message); return Promise.resolve(true); },
      onDidReceiveMessage(next: (message: unknown) => Promise<void>) { handler = next; return { dispose() {} }; },
    },
    onDidChangeVisibility() { return { dispose() {} }; },
    onDidDispose() { return { dispose() {} }; },
  } as never);
  // Nothing saved yet: every card, accounts included, starts expanded.
  assert.ok(posted.at(-1)?.expandedProviders.includes("claude@1a2b3c4d"));
  await handler?.({ type: "setProviderExpanded", providerId: "claude@1a2b3c4d", expanded: false });
  assert.ok(saved.at(-1)?.includes("claude"));
  assert.ok(!saved.at(-1)?.includes("claude@1a2b3c4d"));
  await handler?.({ type: "setProviderExpanded", providerId: "gemini@1a2b3c4d", expanded: true });
  await handler?.({ type: "setProviderExpanded", providerId: "<img>", expanded: true });
  assert.equal(saved.length, 1);
  await handler?.({ type: "setAllExpanded", expanded: true });
  assert.ok(saved.at(-1)?.includes("claude@1a2b3c4d"));
  dashboard.dispose();

  statusItems.length = 0;
  const controller = new StatusBarController();
  controller.update(states, ["claude", "claude@1a2b3c4d"]);
  assert.match(statusItems[0]?.text ?? "", /usageai-claude\) Claude · me@example\.com 30%/);
  assert.match(statusItems[1]?.text ?? "", /usageai-claude\) Claude · Team 64%/);
  assert.equal(statusItems[1]?.name, "UsageAI: Claude account");
  controller.update(states, ["hottest"]);
  assert.match(statusItems.at(-1)?.text ?? "", /Claude · Team 64%/);
  states = [state("claude", 30)];
  controller.update(states, ["claude@1a2b3c4d"]);
  assert.match(statusItems.at(-1)?.text ?? "", /Claude —/);
  controller.dispose();
});

test("account clients, Status Bar account names, and cached readings are keyed by unit", () => {
  const client = (id: string): UsageClient => ({
    id,
    displayName: id,
    signInCommand: id,
    accountUrl: "https://example.com",
    async getUsage() { return snapshot(id, 1); },
  });
  const base = { codex: client("codex"), claude: client("claude"), copilot: client("copilot"), gemini: client("gemini") };
  const account = (provider: "claude" | "codex", id: string, source: AccountEntry["source"], label: string): AccountEntry => ({
    provider,
    id,
    unitKey: id === "default" ? provider : `${provider}@${id}`,
    path: `/home/dev/.${provider}-${id}`,
    source,
    identity: { email: `${label}@example.com` },
    label,
    hidden: false,
  });
  const accounts = [
    account("claude", "default", "default", "me"),
    account("claude", "1a2b3c4d", "found", "team"),
    account("codex", "5e6f7a8b", "added", "work"),
  ];
  let created = 0;
  const cache = new Map<string, UsageClient>();
  const create = (entry: AccountEntry) => { created += 1; return client(entry.unitKey); };
  assert.deepEqual(extensionModule.buildUsageClients(base, undefined, create, cache).map((entry) => entry.id), ["codex", "claude", "copilot", "gemini"]);
  // A hidden default Codex login leaves only its other account.
  const built = extensionModule.buildUsageClients(base, accounts, create, cache);
  assert.deepEqual(built.map((entry) => entry.id), ["codex@5e6f7a8b", "claude", "claude@1a2b3c4d", "copilot", "gemini"]);
  extensionModule.buildUsageClients(base, accounts, create, cache);
  assert.equal(created, 2);
  extensionModule.buildUsageClients(base, accounts.slice(0, 1), create, cache);
  assert.equal(cache.size, 0);
  // Cards dragged into an order keep it within their provider; a unit the order does not name follows.
  assert.deepEqual(
    extensionModule.buildUsageClients(base, [...accounts, account("claude", "0a0b0c0d", "found", "lab")], create, new Map(), ["claude@1a2b3c4d", "claude", "gemini"])
      .map((entry) => entry.id),
    ["codex@5e6f7a8b", "claude@1a2b3c4d", "claude", "claude@0a0b0c0d", "copilot", "gemini"],
  );

  // Dropping a card on one of the same provider reorders that provider's accounts; dropping it on
  // another provider's card moves the whole provider before or after that provider.
  const shownUnits = ["codex", "codex@5e6f7a8b", "claude", "claude@1a2b3c4d", "gemini"];
  const shownProviders = ["codex", "claude", "gemini"];
  assert.deepEqual(dashboardModule.moveDashboardCard(shownProviders, shownUnits, "codex@5e6f7a8b", "codex", false), {
    providers: shownProviders,
    units: ["codex@5e6f7a8b", "codex", "claude", "claude@1a2b3c4d", "gemini"],
  });
  assert.deepEqual(dashboardModule.moveDashboardCard(shownProviders, shownUnits, "gemini", "codex@5e6f7a8b", false)?.providers, ["gemini", "codex", "claude"]);
  assert.deepEqual(dashboardModule.moveDashboardCard(shownProviders, shownUnits, "codex@5e6f7a8b", "claude@1a2b3c4d", true)?.providers, ["claude", "codex", "gemini"]);
  assert.equal(dashboardModule.moveDashboardCard(shownProviders, shownUnits, "codex", "codex@5e6f7a8b", false), undefined);
  assert.equal(dashboardModule.moveDashboardCard(shownProviders, shownUnits, "claude", "codex@5e6f7a8b", true), undefined);
  assert.equal(dashboardModule.moveDashboardCard(shownProviders, shownUnits, "copilot", "codex", true), undefined);
  assert.equal(dashboardModule.moveDashboardCard(shownProviders, shownUnits, "codex", "codex", true), undefined);
  assert.deepEqual(dashboardModule.sanitizeUnitOrder(["claude", "claude@1a2b3c4d", "claude", "nope", "gemini@1a2b3c4d", 4]), ["claude", "claude@1a2b3c4d"]);
  assert.deepEqual(dashboardModule.sanitizeUnitOrder("claude"), []);

  assert.deepEqual(extensionModule.resolveStatusBarAccounts(["TEAM", "work@example.com", "claude@0a0b0c0d", "nobody", 4, "TEAM"], accounts), [
    "claude@1a2b3c4d",
    "codex@5e6f7a8b",
    "claude@0a0b0c0d",
  ]);
  assert.deepEqual(extensionModule.resolveStatusBarAccounts("team", accounts), []);

  configurationValues.clear();
  inspectedConfiguration.clear();
  configurationValues.set("statusBarProvider", "claude");
  configurationValues.set("statusBarProviders.accounts", ["team", "work"]);
  assert.deepEqual(extensionModule.getStatusBarProviderSelection(configuration as never, ["claude"], accounts), ["claude", "claude@1a2b3c4d"]);
  // The extension's own saved list, once the picker has written one, replaces that former setting.
  assert.deepEqual(extensionModule.getStatusBarProviderSelection(configuration as never, ["claude"], accounts, []), "claude");
  assert.deepEqual(extensionModule.getStatusBarProviderSelection(configuration as never, ["claude"], accounts, ["claude@1a2b3c4d"]), ["claude", "claude@1a2b3c4d"]);
  // Items follow the dashboard: providers in their configured order, each main login followed by
  // its other accounts, instead of every extra account after the last provider.
  configurationValues.clear();
  for (const key of ["statusBarProviders.codex", "statusBarProviders.claude", "statusBarProviders.gemini"]) {
    configurationValues.set(key, true);
    inspectedConfiguration.add(key);
  }
  assert.deepEqual(
    extensionModule.getStatusBarProviderSelection(configuration as never, ["gemini", "codex", "claude"], accounts, ["claude@1a2b3c4d", "codex@5e6f7a8b"]),
    ["gemini", "codex", "codex@5e6f7a8b", "claude", "claude@1a2b3c4d"],
  );
  assert.deepEqual(
    extensionModule.getStatusBarProviderSelection(configuration as never, ["gemini", "codex", "claude"], accounts, ["claude@1a2b3c4d", "codex@5e6f7a8b"], ["claude@1a2b3c4d", "claude"]),
    ["gemini", "codex", "codex@5e6f7a8b", "claude@1a2b3c4d", "claude"],
  );
  configurationValues.clear();
  inspectedConfiguration.clear();

  assert.deepEqual(Object.keys(extensionModule.sanitizeCachedSnapshots({
    "claude@1a2b3c4d": snapshot("claude", 5),
    "claude@nothex!": snapshot("claude", 5),
    "gemini@1a2b3c4d": snapshot("gemini", 5),
    "codex@1a2b3c4d": snapshot("claude", 5),
  })), ["claude@1a2b3c4d"]);
});
