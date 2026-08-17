import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test, { mock } from "node:test";
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
};

const vscodeStub = {
  StatusBarAlignment: { Left: 1 },
  ProgressLocation: { Window: 1 },
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
const { UsageDashboardViewProvider } = localRequire("../src/dashboard-view") as typeof import("../src/dashboard-view");
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

  const dashboard = new UsageDashboardViewProvider(service, () => 72, () => 90);
  dashboard.resolveWebviewView(view as never);
  assert.deepEqual(webview.options, { enableScripts: true, localResourceRoots: [] });
  assert.match(webview.html, /default-src 'none'/);
  assert.match(webview.html, /Last good/);
  assert.match(webview.html, /Retry at/);
  assert.doesNotMatch(webview.html, /innerHTML/);
  assert.equal(visibility.at(-1), true);
  assert.ok(posted.length >= 1);

  await messageHandler?.({ type: "ready" });
  await messageHandler?.({ type: "refresh", providerId: "codex" });
  await messageHandler?.({ type: "settings" });
  await messageHandler?.({ type: "openAccount", providerId: "codex" });
  await messageHandler?.({ type: "copySignIn", providerId: "codex" });
  await messageHandler?.({ type: "openAccount", providerId: "unknown" });
  await messageHandler?.({ unsafe: true });
  assert.deepEqual(executedCommands.map((entry) => entry.command), ["usageai.refresh", "usageai.openSettings"]);
  assert.deepEqual(openedUris, ["https://example.com/codex"]);
  assert.deepEqual(clipboardWrites, ["codex login"]);
  assert.deepEqual(informationMessages, ["Copied: codex login"]);

  updateListener?.(states);
  view.visible = false;
  visibilityHandler?.();
  disposeHandler?.();
  assert.deepEqual(visibility.slice(-2), [false, false]);
  dashboard.dispose();
  assert.equal(updateListener, undefined);
});

test("activation helpers sanitize configuration and activation registers its surface", () => {
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
      get: (_key: string, fallback: unknown) => fallback,
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
  } finally {
    mock.restoreAll();
    for (const subscription of subscriptions) subscription.dispose();
  }
});
