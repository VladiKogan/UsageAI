import * as vscode from "vscode";
import {
  formatResetCountdown,
  hasQuota,
  normalizeMetricDisplayMode,
  selectDashboardMetrics,
  type MetricDisplayMode,
  type ProviderState,
  type UsageMetric,
} from "./model";
import type { UsageRefreshService } from "./refresh-service";

export const dashboardExpandedProvidersKey = "usageai.dashboard.expandedProviders.v1";
const knownProviderIds = ["codex", "claude", "copilot", "gemini"] as const;

export interface DashboardViewOptions {
  readonly loadExpandedProviders?: () => unknown;
  readonly saveExpandedProviders?: (providerIds: readonly string[]) => void | PromiseLike<void>;
  readonly metricDisplayMode?: () => unknown;
  readonly setMetricDisplayMode?: (mode: MetricDisplayMode) => void | PromiseLike<void>;
}

export function sanitizeExpandedProviderIds(
  value: unknown,
  knownIds: readonly string[] = knownProviderIds,
): string[] {
  if (!Array.isArray(value)) {
    return [...knownIds];
  }

  const allowed = new Set(knownIds);
  return [...new Set(value.filter((candidate): candidate is string =>
    typeof candidate === "string" && allowed.has(candidate)))];
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

  public constructor(
    private readonly refreshService: UsageRefreshService,
    private readonly warningPercent: () => number,
    private readonly criticalPercent: () => number,
    private readonly options: DashboardViewOptions = {},
  ) {
    this.expandedProviders = new Set(sanitizeExpandedProviderIds(options.loadExpandedProviders?.()));
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
      metricDisplayMode: normalizeMetricDisplayMode(this.options.metricDisplayMode?.()),
      expandedProviders: [...this.expandedProviders],
    });
  }

  private async persistExpansion(): Promise<void> {
    await this.options.saveExpandedProviders?.(
      knownProviderIds.filter((providerId) => this.expandedProviders.has(providerId)),
    );
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
      await this.options.setMetricDisplayMode?.(mode);
      this.postStates(this.refreshService.getStates());
      return;
    }
    if (message.type === "setAllExpanded") {
      if (typeof message.expanded !== "boolean") return;
      this.expandedProviders.clear();
      if (message.expanded) for (const providerId of knownProviderIds) this.expandedProviders.add(providerId);
      await this.persistExpansion();
      return;
    }
    if (message.type === "setProviderExpanded") {
      if (!isKnownProviderId(message.providerId) || typeof message.expanded !== "boolean") return;
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
}

function isMessage(value: unknown): value is DashboardMessage {
  return typeof value === "object" && value !== null && "type" in value && typeof value.type === "string";
}

function isKnownProviderId(value: unknown): value is typeof knownProviderIds[number] {
  return typeof value === "string" && (knownProviderIds as readonly string[]).includes(value);
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
    .card-toggle { appearance: none; display: grid; grid-template-columns: auto minmax(0, 1fr) auto; align-items: center; gap: 7px; width: 100%; border: 0; padding: 10px 10px 8px 10px; color: inherit; background: transparent; text-align: left; cursor: pointer; }
    .card-toggle:hover { background: var(--vscode-list-hoverBackground); }
    .card-toggle:focus-visible, .action:focus-visible, select:focus-visible { outline: 2px solid var(--vscode-focusBorder); outline-offset: -2px; }
    .chevron { width: 12px; color: var(--vscode-descriptionForeground); transform: rotate(-90deg); transition: transform .12s ease; }
    .card-toggle[aria-expanded="true"] .chevron { transform: rotate(0); }
    .provider { min-width: 0; }
    .provider-name { display: block; font-weight: 650; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .plan, .collapsed-summary { display: block; color: var(--vscode-descriptionForeground); font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .collapsed-summary { margin-top: 3px; }
    .headline { display: inline-flex; min-height: 25px; align-items: center; gap: 6px; font-family: var(--vscode-editor-font-family); font-size: 18px; font-weight: 650; letter-spacing: -.04em; white-space: nowrap; }
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
  <header class="masthead"><h1>Quota instruments</h1><span id="stamp" class="stamp">Waiting for first reading</span></header>
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
    metricMode.addEventListener('change', () => vscode.postMessage({ type: 'setMetricDisplayMode', mode: metricMode.value }));
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
    const renderMetric = (metric, warning, critical) => {
      if (!hasQuota(metric)) { const row = el('div', 'balance'); row.append(el('span', '', metric.name), el('strong', '', metric.remainingText || (metric.isUnlimited ? 'UNLIMITED' : metric.usageText || 'Not reported'))); return row; }
      const metricLevel = level(metric.usedPercent, warning, critical); const root = el('section', 'metric'); root.dataset.level = metricLevel;
      const head = el('div', 'metric-head'); head.append(el('span', 'metric-name', metric.name), el('span', 'metric-value', metric.usedPercent + '% used'));
      const rail = el('progress', 'rail'); rail.setAttribute('aria-label', metric.name + ' usage'); rail.max = 100; rail.value = metric.usedPercent;
      const foot = el('div', 'metric-foot'); foot.append(el('span', '', metric.usageText || (100 - metric.usedPercent) + '% left'), el('span', '', countdown(metric.resetsAt))); root.append(head, rail, foot); return root;
    };
    const render = (payload) => {
      providers.replaceChildren(); const warning = payload.warningPercent ?? 72; const critical = payload.criticalPercent ?? 90;
      const mode = ['metered', 'important'].includes(payload.metricDisplayMode) ? payload.metricDisplayMode : 'all'; metricMode.value = mode;
      const remembered = new Set(Array.isArray(payload.expandedProviders) ? payload.expandedProviders : []);
      if (!payload.states.length) { const empty = el('div', 'empty'); empty.append(el('strong', '', 'No providers selected'), el('span', '', 'Choose providers in UsageAI settings.')); empty.append(button('Open settings', 'settings')); providers.append(empty); return; }
      let newest = 0;
      for (const state of payload.states) {
        const card = el('article', 'card'); card.dataset.stale = String(state.stale);
        const highestMetric = collapsedDashboardMetric(state);
        const highest = highestMetric?.usedPercent ?? 0; card.dataset.level = level(highest, warning, critical);
        const expanded = effectiveDashboardExpansion(state, remembered.has(state.id)); const contentId = 'provider-content-' + state.id;
        const head = el('button', 'card-toggle'); head.type = 'button'; head.setAttribute('aria-expanded', String(expanded)); head.setAttribute('aria-controls', contentId); head.setAttribute('aria-label', (expanded ? 'Collapse ' : 'Expand ') + state.displayName + ' usage');
        head.addEventListener('click', () => vscode.postMessage({ type: 'setProviderExpanded', providerId: state.id, expanded: !expanded }));
        const chevron = el('span', 'chevron', '⌄'); chevron.setAttribute('aria-hidden', 'true'); const provider = el('div', 'provider');
        const planParts = state.snapshot ? [state.snapshot.plan, state.snapshot.accountName] : ['Not connected']; if (state.stale) planParts.push('Stale'); provider.append(el('span', 'provider-name', state.displayName), el('span', 'plan', planParts.filter(Boolean).join(' · ')));
        if (!expanded) provider.append(el('span', 'collapsed-summary', highestMetric ? highestMetric.name + ' · ' + highestMetric.usedPercent + '% · ' + countdown(highestMetric.resetsAt) : 'No metered limit'));
        const headline = el('div', 'headline'); if (state.refreshing) { const spinner = el('span', 'spinner'); spinner.setAttribute('aria-hidden', 'true'); headline.append(spinner); }
        if (state.snapshot) headline.append(el('span', '', highest + '%')); else if (!state.refreshing) headline.append(el('span', '', '—')); head.append(chevron, provider, headline); card.append(head);
        const body = el('div', 'card-body'); body.id = contentId; body.hidden = !expanded;
        if (state.snapshot) {
          newest = Math.max(newest, new Date(state.snapshot.fetchedAt).getTime()); const metrics = el('div', 'metrics'); const visibleMetrics = selectedMetrics(state.snapshot.metrics, mode);
          if (!visibleMetrics.length) metrics.append(el('div', 'filtered-empty', 'No metered limits reported')); else for (const metric of visibleMetrics) metrics.append(renderMetric(metric, warning, critical)); body.append(metrics);
          if (state.stale) { const details = []; const fetchedAt = localTime(state.snapshot.fetchedAt); const checkedAt = localTime(state.lastAttemptedAt); const retryAt = localTime(state.nextRefreshAt); if (fetchedAt) details.push('Last good ' + fetchedAt); if (checkedAt) details.push('Checked ' + checkedAt); if (retryAt) details.push('Retry at ' + retryAt); if (details.length) body.append(el('div', 'state-meta', details.join(' · '))); }
        }
        if (state.error) { const error = el('div', 'error'); error.append(el('div', '', state.error)); const actions = el('div', 'actions'); actions.append(button('Copy sign-in', 'copySignIn', state.id), button('Refresh', 'refresh', state.id)); error.append(actions); body.append(error); }
        else if (state.snapshot) { const actions = el('div', 'actions card-actions'); actions.append(button('Account', 'openAccount', state.id)); body.append(actions); }
        card.append(body); providers.append(card);
      }
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
