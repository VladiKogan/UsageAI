import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test, { mock } from "node:test";
import vm from "node:vm";
import type { ProviderState, UsageSnapshot } from "../src/model";

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

const vscodeStub = {
  StatusBarAlignment: { Left: 1 },
  ProgressLocation: { Window: 1 },
  ConfigurationTarget: { Global: 1 },
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
  env: {
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
    showInformationMessage(message: string) { informationMessages.push(message); },
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
  assert.equal(visibility.at(-1), true);
  assert.ok(posted.length >= 1);
  assert.deepEqual(posted[0], {
    type: "states",
    states,
    warningPercent: 72,
    criticalPercent: 90,
    metricDisplayMode: "important",
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

  const subscriptions: Array<{ dispose(): unknown }> = [];
  const updates: unknown[] = [];
  const context = {
    subscriptions,
    globalState: {
      get: (key: string, fallback: unknown) => key === dashboardModule.dashboardExpandedProvidersKey
        ? ["claude"]
        : fallback,
      update: async (_key: string, value: unknown) => { updates.push(value); },
    },
  };
  mock.method(UsageRefreshService.prototype, "start", () => {});
  try {
    extensionModule.activate(context as never);
    assert.deepEqual([...registeredCommands.keys()].sort(), [
      "usageai.openSettings",
      "usageai.refresh",
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
    await dashboardMessage?.({ type: "setProviderExpanded", providerId: "claude", expanded: false });
    await dashboardMessage?.({ type: "setMetricDisplayMode", mode: "important" });
    assert.ok(updates.some((value) => Array.isArray(value) && value.length === 0));
    assert.deepEqual(configurationUpdates, [{ key: "metricDisplayMode", value: "important", target: 1 }]);
  } finally {
    mock.restoreAll();
    for (const subscription of subscriptions) subscription.dispose();
  }
});
