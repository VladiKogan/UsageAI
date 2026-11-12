import * as vscode from "vscode";
import { isKnownUnitKey, providerKindOf } from "./accounts";
import {
  displayedPercent,
  formatResetCountdown,
  hasQuota,
  normalizeMetricDisplayMode,
  paceText,
  normalizeQuotaDisplay,
  selectDashboardMetrics,
  type MetricDisplayMode,
  type ProviderState,
  type QuotaDisplay,
  type UsageMetric,
} from "./model";
import type { UsageRefreshService } from "./refresh-service";

/** Expanded cards by unit key (`claude`, `claude@1a2b3c4d`). */
export const dashboardExpandedProvidersKey = "usageai.dashboard.expandedProviders.v2";
/** The provider-id list used before accounts; migrated once into the `.v2` key. */
export const legacyDashboardExpandedProvidersKey = "usageai.dashboard.expandedProviders.v1";
const knownProviderIds = ["codex", "claude", "copilot", "gemini"] as const;
const maxRememberedCards = 64;
/** The cards' order within each provider, as unit keys, after the user dragged one. */
export const dashboardUnitOrderKey = "usageai.dashboard.unitOrder.v1";

/** Known unit keys only, once each. */
export function sanitizeUnitOrder(value: unknown): string[] {
  return Array.isArray(value) ? sanitizeExpandedProviderIds(value) : [];
}

/**
 * Where everything goes after one card is dropped before or after another. A provider's cards stay
 * together: within one provider the card changes place among that provider's accounts, and onto
 * another provider's card the whole provider moves before or after that provider. Undefined when
 * nothing would change or an id is not shown.
 */
export function moveDashboardCard(
  providers: readonly string[],
  units: readonly string[],
  moved: string,
  target: string,
  after: boolean,
): { readonly providers: string[]; readonly units: string[] } | undefined {
  if (moved === target || !units.includes(moved) || !units.includes(target)) {
    return undefined;
  }
  const movedKind = providerKindOf(moved);
  const targetKind = providerKindOf(target);
  let nextProviders = [...providers];
  let nextUnits = [...units];
  if (movedKind === targetKind) {
    nextUnits = units.filter((unit) => unit !== moved);
    nextUnits.splice(nextUnits.indexOf(target) + (after ? 1 : 0), 0, moved);
  } else {
    if (!providers.includes(movedKind) || !providers.includes(targetKind)) {
      return undefined;
    }
    nextProviders = providers.filter((provider) => provider !== movedKind);
    nextProviders.splice(nextProviders.indexOf(targetKind) + (after ? 1 : 0), 0, movedKind);
  }
  return nextProviders.join() === providers.join() && nextUnits.join() === units.join()
    ? undefined
    : { providers: nextProviders, units: nextUnits };
}

export interface DashboardViewOptions {
  readonly loadExpandedProviders?: () => unknown;
  readonly saveExpandedProviders?: (providerIds: readonly string[]) => void | PromiseLike<void>;
  readonly metricDisplayMode?: () => unknown;
  readonly setMetricDisplayMode?: (mode: MetricDisplayMode) => void | PromiseLike<void>;
  readonly quotaDisplay?: () => unknown;
  /** A card was dropped before or after another; both are shown unit keys. */
  readonly moveCard?: (moved: string, target: string, after: boolean) => void | PromiseLike<void>;
}

/** Known unit keys only: a provider id, or an account unit key of a provider that has accounts. */
export function sanitizeExpandedProviderIds(
  value: unknown,
  knownIds: readonly string[] = knownProviderIds,
): string[] {
  if (!Array.isArray(value)) {
    return [...knownIds];
  }

  return [...new Set(value.filter((candidate): candidate is string => isKnownUnitKey(candidate, knownIds)))]
    .slice(0, maxRememberedCards);
}

/**
 * The saved expansion state, moving the provider-id list of `.v1` into `.v2` once. A provider id is
 * the unit key of its default account, so the old list carries over unchanged.
 */
export function loadExpandedProviders(
  get: (key: string) => unknown,
  update: (key: string, value: readonly string[]) => unknown,
): unknown {
  const current = get(dashboardExpandedProvidersKey);
  if (current !== undefined) {
    return current;
  }
  const legacy = get(legacyDashboardExpandedProvidersKey);
  if (!Array.isArray(legacy)) {
    return undefined;
  }
  const migrated = sanitizeExpandedProviderIds(legacy);
  void Promise.resolve(update(dashboardExpandedProvidersKey, migrated)).catch(() => undefined);
  return migrated;
}

export function dashboardNeedsAttention(
  state: Pick<ProviderState, "snapshot" | "stale" | "error">,
): boolean {
  return !state.snapshot || state.stale || Boolean(state.error);
}

export function effectiveDashboardExpansion(
  state: Pick<ProviderState, "snapshot" | "stale" | "error">,
  rememberedExpanded: boolean,
): boolean {
  return dashboardNeedsAttention(state) || rememberedExpanded;
}

export function collapsedDashboardMetric(
  state: Pick<ProviderState, "snapshot">,
): UsageMetric | undefined {
  let selected: UsageMetric | undefined;
  for (const metric of state.snapshot?.metrics ?? []) {
    if (metric.usedPercent !== null && !metric.isUnlimited
      && (!selected || metric.usedPercent > (selected.usedPercent ?? 0))) {
      selected = metric;
    }
  }
  return selected;
}

export class UsageDashboardViewProvider implements vscode.WebviewViewProvider, vscode.Disposable {
  public static readonly viewType = "usageai.dashboard";
  private view: vscode.WebviewView | undefined;
  private readonly disposables: vscode.Disposable[] = [];
  private readonly removeUpdateListener: () => void;
  private readonly expandedProviders: Set<string>;
  /** Nothing saved yet: every card starts expanded, including accounts that appear later. */
  private expandEverything: boolean;
  private metricDisplayMode: MetricDisplayMode;
  private quotaDisplay: QuotaDisplay;

  public constructor(
    private readonly refreshService: UsageRefreshService,
    private readonly warningPercent: () => number,
    private readonly criticalPercent: () => number,
    private readonly options: DashboardViewOptions = {},
  ) {
    const saved = options.loadExpandedProviders?.();
    this.expandEverything = !Array.isArray(saved);
    this.expandedProviders = new Set(sanitizeExpandedProviderIds(saved));
    this.metricDisplayMode = normalizeMetricDisplayMode(options.metricDisplayMode?.());
    this.quotaDisplay = normalizeQuotaDisplay(options.quotaDisplay?.());
    this.removeUpdateListener = refreshService.onDidUpdate((states) => this.postStates(states));
  }

  public resolveWebviewView(view: vscode.WebviewView): void {
    this.view = view;
    view.webview.options = { enableScripts: true, localResourceRoots: [] };
    view.webview.html = dashboardHtml(view.webview);
    this.disposables.push(
      view.onDidChangeVisibility(() => {
        this.refreshService.setVisible(view.visible);
        if (view.visible) this.postStates(this.refreshService.getStates());
      }),
      view.webview.onDidReceiveMessage((message: unknown) => this.handleMessage(message)),
      view.onDidDispose(() => {
        this.view = undefined;
        this.refreshService.setVisible(false);
      }),
    );
    this.refreshService.setVisible(view.visible);
    this.postStates(this.refreshService.getStates());
  }

  public configurationChanged(): void {
    this.metricDisplayMode = normalizeMetricDisplayMode(this.options.metricDisplayMode?.());
    this.quotaDisplay = normalizeQuotaDisplay(this.options.quotaDisplay?.());
    this.postStates(this.refreshService.getStates());
  }

  public dispose(): void {
    this.removeUpdateListener();
    for (const disposable of this.disposables) disposable.dispose();
  }

  private postStates(states: readonly ProviderState[]): void {
    void this.view?.webview.postMessage({
      type: "states",
      states,
      warningPercent: this.warningPercent(),
      criticalPercent: this.criticalPercent(),
      metricDisplayMode: this.metricDisplayMode,
      quotaDisplay: this.quotaDisplay,
      expandedProviders: this.expandEverything
        ? [...new Set([...this.expandedProviders, ...states.map((state) => state.id)])]
        : [...this.expandedProviders],
    });
  }

  private async persistExpansion(): Promise<void> {
    this.expandEverything = false;
    await this.options.saveExpandedProviders?.(sanitizeExpandedProviderIds([...this.expandedProviders]));
    this.postStates(this.refreshService.getStates());
  }

  private async handleMessage(message: unknown): Promise<void> {
    if (!isMessage(message)) return;
    if (message.type === "refresh") {
      await vscode.commands.executeCommand("usageai.refresh");
      return;
    }
    if (message.type === "ready") {
      this.postStates(this.refreshService.getStates());
      return;
    }
    if (message.type === "settings") {
      await vscode.commands.executeCommand("usageai.openSettings");
      return;
    }
    if (message.type === "setMetricDisplayMode") {
      const mode = normalizeMetricDisplayMode(message.mode);
      if (mode !== message.mode) return;
      // Render immediately rather than waiting for configuration-change propagation, but a setting
      // that cannot be written must not leave this cache ahead of what the Status Bar and the next
      // reload will read.
      const previousMode = this.metricDisplayMode;
      this.metricDisplayMode = mode;
      this.postStates(this.refreshService.getStates());
      try {
        await this.options.setMetricDisplayMode?.(mode);
      } catch {
        this.metricDisplayMode = previousMode;
        this.postStates(this.refreshService.getStates());
      }
      return;
    }
    if (message.type === "moveCard") {
      if (!isKnownUnitKey(message.providerId, knownProviderIds) || !isKnownUnitKey(message.targetId, knownProviderIds)
        || typeof message.after !== "boolean") return;
      await this.options.moveCard?.(message.providerId, message.targetId, message.after);
      return;
    }
    if (message.type === "setAllExpanded") {
      if (typeof message.expanded !== "boolean") return;
      this.expandedProviders.clear();
      if (message.expanded) {
        for (const unit of [...knownProviderIds, ...this.refreshService.getStates().map((state) => state.id)]) {
          this.expandedProviders.add(unit);
        }
      }
      await this.persistExpansion();
      return;
    }
    if (message.type === "setProviderExpanded") {
      if (!isKnownUnitKey(message.providerId, knownProviderIds) || typeof message.expanded !== "boolean") return;
      if (this.expandEverything) {
        // The first change keeps every other card as it was shown: expanded.
        for (const state of this.refreshService.getStates()) this.expandedProviders.add(state.id);
      }
      if (message.expanded) this.expandedProviders.add(message.providerId);
      else this.expandedProviders.delete(message.providerId);
      await this.persistExpansion();
      return;
    }

    const state = this.refreshService.getStates().find((candidate) => candidate.id === message.providerId);
    if (!state) return;
    if (message.type === "openAccount") {
      await vscode.env.openExternal(vscode.Uri.parse(state.accountUrl));
    } else if (message.type === "copySignIn") {
      await vscode.env.clipboard.writeText(state.signInCommand);
      void vscode.window.showInformationMessage(`Copied: ${state.signInCommand}`);
    }
  }
}

interface DashboardMessage {
  readonly type: string;
  readonly providerId?: string;
  readonly expanded?: boolean;
  readonly mode?: unknown;
  readonly targetId?: unknown;
  readonly after?: unknown;
}

function isMessage(value: unknown): value is DashboardMessage {
  return typeof value === "object" && value !== null && "type" in value && typeof value.type === "string";
}

function dashboardHtml(webview: vscode.Webview): string {
  const nonce = randomNonce();
  return /* html */ `<!doctype html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
  <style nonce="${nonce}">
    :root { color-scheme: light dark; }
    * { box-sizing: border-box; }
    [hidden] { display: none !important; }
    body { margin: 0; padding: 10px; color: var(--vscode-foreground); background: var(--vscode-sideBar-background); font: 13px/1.4 var(--vscode-font-family); }
    button, select { font: inherit; }
    .masthead { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; padding: 2px 2px 7px; }
    .masthead h1 { margin: 0; font-size: 12px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
    .stamp { display: inline-flex; align-items: center; gap: 5px; color: var(--vscode-descriptionForeground); font-size: 11px; }
    .toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 5px; padding: 0 2px 9px; }
    .toolbar select { min-width: 112px; margin-right: auto; border: 1px solid var(--vscode-dropdown-border); padding: 3px 5px; color: var(--vscode-dropdown-foreground); background: var(--vscode-dropdown-background); }
    .providers { display: grid; gap: 9px; }
    .card { position: relative; overflow: hidden; border: 1px solid var(--vscode-widget-border, transparent); border-radius: 6px; background: var(--vscode-editorWidget-background); box-shadow: 0 1px 2px color-mix(in srgb, var(--vscode-widget-shadow) 20%, transparent); }
    .card::before { content: ""; position: absolute; inset: 0 auto 0 0; width: 3px; background: var(--vscode-progressBar-background); }
    .card[data-level="warning"]::before { background: var(--vscode-editorWarning-foreground); }
    .card[data-level="critical"]::before { background: var(--vscode-errorForeground); }
    .card[data-stale="true"] { opacity: .82; }
    .card.dragging { opacity: .45; }
    .card[data-drop="before"] { box-shadow: 0 -2px 0 var(--vscode-focusBorder); }
    .card[data-drop="after"] { box-shadow: 0 2px 0 var(--vscode-focusBorder); }
    .card-toggle { appearance: none; display: grid; grid-template-columns: auto minmax(0, 1fr) auto; align-items: center; gap: 7px; width: 100%; border: 0; padding: 10px 10px 8px 10px; color: inherit; background: transparent; text-align: left; cursor: pointer; }
    .card-toggle:hover { background: var(--vscode-list-hoverBackground); }
    .card-toggle:focus-visible, .action:focus-visible, select:focus-visible { outline: 2px solid var(--vscode-focusBorder); outline-offset: -2px; }
    .chevron { width: 12px; color: var(--vscode-descriptionForeground); transform: rotate(-90deg); transition: transform .12s ease; }
    .card-toggle[aria-expanded="true"] .chevron { transform: rotate(0); }
    .provider { min-width: 0; }
    .provider-name { display: block; font-weight: 650; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .plan, .collapsed-summary { display: block; color: var(--vscode-descriptionForeground); font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .collapsed-summary { margin-top: 3px; }
    .incident { display: block; margin-top: 3px; font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; color: var(--vscode-editorWarning-foreground); }
    .incident[data-outage="true"] { color: var(--vscode-errorForeground); }
    .headline { display: inline-flex; min-height: 25px; align-items: center; gap: 6px; font-family: var(--vscode-editor-font-family); font-size: 18px; font-weight: 650; letter-spacing: -.04em; white-space: nowrap; }
    .headline-unit { color: var(--vscode-descriptionForeground); font-size: 11px; font-weight: 600; letter-spacing: 0; }
    .spinner { display: inline-block; width: 13px; height: 13px; flex: 0 0 auto; border: 2px solid color-mix(in srgb, var(--vscode-progressBar-background) 26%, transparent); border-top-color: var(--vscode-progressBar-background); border-radius: 50%; animation: refresh-spin .8s linear infinite; }
    .stamp .spinner { width: 10px; height: 10px; border-width: 1.5px; }
    @keyframes refresh-spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) { .spinner { animation: none; border-style: dotted; } .chevron { transition: none; } }
    .card-body { padding-top: 1px; }
    .metrics { display: grid; gap: 9px; padding: 0 10px 10px 12px; }
    .metric-head, .metric-foot { display: flex; justify-content: space-between; gap: 8px; }
    .metric-head { margin-bottom: 4px; font-size: 11px; }
    .metric-name { font-weight: 600; }
    .metric-value, .metric-foot { color: var(--vscode-descriptionForeground); }
    .metric-foot { margin-top: 4px; font-size: 10px; }
    .state-meta { margin: 0 10px 9px 12px; color: var(--vscode-descriptionForeground); font-size: 10px; }
    .rail { appearance: none; display: block; width: 100%; height: 5px; overflow: hidden; border: 0; border-radius: 999px; background: transparent; }
    .rail::-webkit-progress-bar { border-radius: inherit; background: color-mix(in srgb, var(--vscode-progressBar-background) 26%, transparent); }
    .rail::-webkit-progress-value { border-radius: inherit; background: var(--vscode-progressBar-background); }
    .metric[data-level="warning"] .rail::-webkit-progress-value { background: var(--vscode-editorWarning-foreground); }
    .metric[data-level="critical"] .rail::-webkit-progress-value { background: var(--vscode-errorForeground); }
    .balance { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; padding: 7px 8px; border-radius: 4px; background: var(--vscode-textCodeBlock-background); }
    .balance strong { font-family: var(--vscode-editor-font-family); }
    .filtered-empty { padding: 8px; border: 1px dashed var(--vscode-widget-border); border-radius: 4px; color: var(--vscode-descriptionForeground); text-align: center; }
    .error { margin: 0 10px 10px 12px; padding: 7px 8px; border-left: 2px solid var(--vscode-errorForeground); color: var(--vscode-errorForeground); background: var(--vscode-inputValidation-errorBackground); font-size: 11px; }
    .actions { display: flex; gap: 6px; margin-top: 6px; }
    .card-actions { padding: 0 10px 10px 12px; }
    .action { border: 0; border-radius: 3px; padding: 3px 7px; color: var(--vscode-button-secondaryForeground); background: var(--vscode-button-secondaryBackground); cursor: pointer; }
    .action:hover { background: var(--vscode-button-secondaryHoverBackground); }
    .empty { padding: 24px 12px; border: 1px dashed var(--vscode-widget-border); border-radius: 6px; color: var(--vscode-descriptionForeground); text-align: center; }
    .empty strong { display: block; margin-bottom: 5px; color: var(--vscode-foreground); }
  </style>
</head>
<body>
  <header class="masthead"><h1>Usage limits</h1><span id="stamp" class="stamp">Waiting for first reading</span></header>
  <div class="toolbar" aria-label="Dashboard controls">
    <select id="metric-mode" aria-label="Dashboard metrics"><option value="all">All metrics</option><option value="metered">Metered only</option><option value="important">Important only</option></select>
    <button id="collapse-all" class="action" type="button">Collapse all</button><button id="expand-all" class="action" type="button">Expand all</button>
  </div>
  <main id="providers" class="providers" aria-live="polite"></main>
  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    const providers = document.getElementById('providers'); const stamp = document.getElementById('stamp'); const metricMode = document.getElementById('metric-mode');
    document.getElementById('collapse-all').addEventListener('click', () => vscode.postMessage({ type: 'setAllExpanded', expanded: false }));
    document.getElementById('expand-all').addEventListener('click', () => vscode.postMessage({ type: 'setAllExpanded', expanded: true }));
    let latestPayload; let dragging; let deferred;
    // The card whose header holds the keyboard focus. A redraw rebuilds every card, and one move sends several
    // (the new order, then the changed setting), so the focus is put back on each instead of once.
    const focusedUnit = () => { const active = document.activeElement; return document.hasFocus?.() && active?.classList?.contains('card-toggle') ? active.closest('.card')?.dataset.unit : undefined; };
    const clearDrop = () => { for (const node of providers.querySelectorAll('[data-drop]')) delete node.dataset.drop; };
    const moveCard = (providerId, targetId, after) => vscode.postMessage({ type: 'moveCard', providerId, targetId, after });
    // A reading that lands mid-drag is drawn once the drag ends: redrawing would remove the card being dragged.
    const endDrag = () => { dragging = undefined; clearDrop(); for (const node of providers.querySelectorAll('.dragging')) { node.classList.remove('dragging'); node.draggable = false; } if (deferred) { const next = deferred; deferred = undefined; render(next); } };
    metricMode.addEventListener('change', () => { const mode = metricMode.value; if (latestPayload) render({ ...latestPayload, metricDisplayMode: mode }); vscode.postMessage({ type: 'setMetricDisplayMode', mode }); });
    const el = (tag, className, text) => { const node = document.createElement(tag); if (className) node.className = className; if (text !== undefined) node.textContent = text; return node; };
    const button = (label, type, providerId) => { const node = el('button', 'action', label); node.type = 'button'; node.addEventListener('click', () => vscode.postMessage({ type, providerId })); return node; };
    const localTime = (value) => { if (!value) return ''; const date = new Date(value); return Number.isNaN(date.getTime()) ? '' : date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); };
    const hasQuota = ${hasQuota.toString()};
    const level = (used, warning, critical) => used >= critical ? 'critical' : used >= warning ? 'warning' : 'normal';
    const countdown = ${formatResetCountdown.toString()};
    const selectedMetrics = ${selectDashboardMetrics.toString()};
    const dashboardNeedsAttention = ${dashboardNeedsAttention.toString()};
    const effectiveDashboardExpansion = ${effectiveDashboardExpansion.toString()};
    const collapsedDashboardMetric = ${collapsedDashboardMetric.toString()};
    const displayed = ${displayedPercent.toString()};
    const pace = ${paceText.toString()};
    const renderMetric = (metric, warning, critical, quota) => {
      if (!hasQuota(metric)) { const row = el('div', 'balance'); row.append(el('span', '', metric.name), el('strong', '', metric.remainingText || (metric.isUnlimited ? 'UNLIMITED' : metric.usageText || 'Not reported'))); return row; }
      const metricLevel = level(metric.usedPercent, warning, critical); const root = el('section', 'metric'); root.dataset.level = metricLevel;
      const remaining = quota === 'remaining'; const used = displayed(metric.usedPercent, 'used'); const left = displayed(metric.usedPercent, 'remaining');
      const head = el('div', 'metric-head'); head.append(el('span', 'metric-name', metric.name), el('span', 'metric-value', remaining ? left + '% left' : used + '% used'));
      const rail = el('progress', 'rail'); rail.setAttribute('aria-label', metric.name + (remaining ? ' remaining' : ' usage')); rail.max = 100; rail.value = remaining ? left : used;
      const foot = el('div', 'metric-foot'); foot.append(el('span', '', metric.usageText || (remaining ? used + '% used' : left + '% left')), el('span', '', [countdown(metric.resetsAt), pace(metric)].filter(Boolean).join(' · '))); root.append(head, rail, foot); return root;
    };
    const render = (payload) => {
      if (dragging) { deferred = payload; return; }
      latestPayload = payload; const focused = focusedUnit(); let focusedHead;
      providers.replaceChildren(); const warning = payload.warningPercent ?? 72; const critical = payload.criticalPercent ?? 90;
      const mode = ['metered', 'important'].includes(payload.metricDisplayMode) ? payload.metricDisplayMode : 'all'; metricMode.value = mode;
      const quota = payload.quotaDisplay === 'remaining' ? 'remaining' : 'used';
      const remembered = new Set(Array.isArray(payload.expandedProviders) ? payload.expandedProviders : []);
      if (!payload.states.length) { const empty = el('div', 'empty'); empty.append(el('strong', '', 'No providers selected'), el('span', '', 'Choose providers in UsageAI settings.')); empty.append(button('Open settings', 'settings')); providers.append(empty); return; }
      let newest = 0;
      for (const state of payload.states) {
        const card = el('article', 'card'); card.dataset.stale = String(state.stale); card.dataset.unit = state.id;
        // Only the card itself: a drag of selected text or a link inside it bubbles here too, and must not move the card.
        card.addEventListener('dragstart', (event) => { if (event.target !== card) return; dragging = state.id; card.classList.add('dragging'); event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/plain', state.displayName); });
        card.addEventListener('dragover', (event) => { if (!dragging || dragging === state.id) return; event.preventDefault(); event.dataTransfer.dropEffect = 'move'; const box = card.getBoundingClientRect(); clearDrop(); card.dataset.drop = event.clientY > box.top + box.height / 2 ? 'after' : 'before'; });
        card.addEventListener('drop', (event) => { if (!dragging || dragging === state.id) return; event.preventDefault(); moveCard(dragging, state.id, card.dataset.drop === 'after'); });
        card.addEventListener('dragend', endDrag);
        const highestMetric = collapsedDashboardMetric(state);
        const highest = highestMetric?.usedPercent ?? 0; card.dataset.level = level(highest, warning, critical);
        const expanded = effectiveDashboardExpansion(state, remembered.has(state.id)); const contentId = 'provider-content-' + state.id;
        const head = el('button', 'card-toggle'); head.type = 'button'; head.setAttribute('aria-expanded', String(expanded)); head.setAttribute('aria-controls', contentId); head.setAttribute('aria-label', (expanded ? 'Collapse ' : 'Expand ') + state.displayName + ' usage');
        head.addEventListener('click', () => vscode.postMessage({ type: 'setProviderExpanded', providerId: state.id, expanded: !expanded }));
        // Dragging starts from the header only, so text in an open card can still be selected.
        head.title = 'Drag to reorder, or press Alt+Up / Alt+Down'; head.setAttribute('aria-keyshortcuts', 'Alt+ArrowUp Alt+ArrowDown');
        head.addEventListener('pointerdown', () => { card.draggable = true; });
        head.addEventListener('keydown', (event) => { if (!event.altKey || (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) return; event.preventDefault(); const down = event.key === 'ArrowDown'; const ids = payload.states.map(other => other.id); const neighbour = ids[ids.indexOf(state.id) + (down ? 1 : -1)]; if (neighbour) moveCard(state.id, neighbour, down); });
        if (state.id === focused) focusedHead = head;
        const chevron = el('span', 'chevron', '⌄'); chevron.setAttribute('aria-hidden', 'true'); const provider = el('div', 'provider');
        const planParts = state.snapshot ? [state.snapshot.plan, state.snapshot.accountName] : ['Not connected']; if (state.stale) planParts.push('Stale'); provider.append(el('span', 'provider-name', state.displayName), el('span', 'plan', planParts.filter(Boolean).join(' · ')));
        if (state.incident) { const incident = el('span', 'incident', 'Status page: ' + state.incident); incident.dataset.outage = String(Boolean(state.incidentIsOutage)); provider.append(incident); }
        if (!expanded) provider.append(el('span', 'collapsed-summary', highestMetric ? highestMetric.name + ' · ' + displayed(highest, quota) + (quota === 'remaining' ? '% left · ' : '% · ') + countdown(highestMetric.resetsAt) : 'No metered limit'));
        const headline = el('div', 'headline'); if (state.refreshing) { const spinner = el('span', 'spinner'); spinner.setAttribute('aria-hidden', 'true'); headline.append(spinner); }
        // Remaining mode never shows a provider without a metered limit as "100% left".
        if (state.snapshot && quota === 'remaining') { if (highestMetric) headline.append(el('span', '', displayed(highest, quota) + '%'), el('span', 'headline-unit', 'left')); else headline.append(el('span', '', '—')); }
        else if (state.snapshot) headline.append(el('span', '', highest + '%')); else if (!state.refreshing) headline.append(el('span', '', '—')); head.append(chevron, provider, headline); card.append(head);
        const body = el('div', 'card-body'); body.id = contentId; body.hidden = !expanded; body.addEventListener('pointerdown', () => { card.draggable = false; });
        if (state.snapshot) {
          newest = Math.max(newest, new Date(state.snapshot.fetchedAt).getTime()); const metrics = el('div', 'metrics'); const visibleMetrics = selectedMetrics(state.snapshot.metrics, mode);
          if (!visibleMetrics.length) metrics.append(el('div', 'filtered-empty', 'No metered limits reported')); else for (const metric of visibleMetrics) metrics.append(renderMetric(metric, warning, critical, quota)); body.append(metrics);
          if (state.stale) { const details = []; const fetchedAt = localTime(state.snapshot.fetchedAt); const checkedAt = localTime(state.lastAttemptedAt); const retryAt = localTime(state.nextRefreshAt); if (fetchedAt) details.push('Last good ' + fetchedAt); if (checkedAt) details.push('Checked ' + checkedAt); if (retryAt) details.push('Retry at ' + retryAt); if (details.length) body.append(el('div', 'state-meta', details.join(' · '))); }
        }
        if (state.error) { const error = el('div', 'error'); error.append(el('div', '', state.error)); const actions = el('div', 'actions'); actions.append(button('Copy sign-in', 'copySignIn', state.id), button('Refresh', 'refresh', state.id)); error.append(actions); body.append(error); }
        else if (state.snapshot) { const actions = el('div', 'actions card-actions'); actions.append(button('Account', 'openAccount', state.id)); body.append(actions); }
        card.append(body); providers.append(card);
      }
      focusedHead?.focus();
      const refreshing = payload.states.some(state => state.refreshing); const staleStates = payload.states.filter(state => state.stale); stamp.replaceChildren();
      if (refreshing) { const spinner = el('span', 'spinner'); spinner.setAttribute('aria-hidden', 'true'); stamp.append(spinner, el('span', '', 'Refreshing…')); }
      else if (staleStates.length) { const noun = staleStates.length === 1 ? 'provider' : 'providers'; const latestCheck = Math.max(0, ...staleStates.map(state => new Date(state.lastAttemptedAt || '').getTime()).filter(Number.isFinite)); stamp.textContent = staleStates.length + ' ' + noun + ' stale' + (latestCheck ? ' · checked ' + localTime(latestCheck) : ''); }
      else stamp.textContent = newest ? 'Updated ' + new Date(newest).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : 'Waiting for first reading';
    };
    window.addEventListener('message', (event) => { if (event.data?.type === 'states') render(event.data); }); vscode.postMessage({ type: 'ready' });
  </script>
</body>
</html>`;
}

function randomNonce(): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  let result = "";
  for (let index = 0; index < 32; index += 1) result += alphabet.charAt(Math.floor(Math.random() * alphabet.length));
  return result;
}
