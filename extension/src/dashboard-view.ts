import * as vscode from "vscode";
import { formatResetCountdown, type ProviderState } from "./model";
import type { UsageRefreshService } from "./refresh-service";

export class UsageDashboardViewProvider implements vscode.WebviewViewProvider, vscode.Disposable {
  public static readonly viewType = "usageai.dashboard";
  private view: vscode.WebviewView | undefined;
  private readonly disposables: vscode.Disposable[] = [];
  private readonly removeUpdateListener: () => void;

  public constructor(
    private readonly refreshService: UsageRefreshService,
    private readonly warningPercent: () => number,
    private readonly criticalPercent: () => number,
  ) {
    this.removeUpdateListener = refreshService.onDidUpdate((states) => this.postStates(states));
  }

  public resolveWebviewView(view: vscode.WebviewView): void {
    this.view = view;
    view.webview.options = {
      enableScripts: true,
      localResourceRoots: [],
    };
    view.webview.html = dashboardHtml(view.webview);
    this.disposables.push(
      view.onDidChangeVisibility(() => {
        this.refreshService.setVisible(view.visible);
        if (view.visible) {
          this.postStates(this.refreshService.getStates());
        }
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

  public dispose(): void {
    this.removeUpdateListener();
    for (const disposable of this.disposables) {
      disposable.dispose();
    }
  }

  private postStates(states: readonly ProviderState[]): void {
    void this.view?.webview.postMessage({
      type: "states",
      states,
      warningPercent: this.warningPercent(),
      criticalPercent: this.criticalPercent(),
    });
  }

  private async handleMessage(message: unknown): Promise<void> {
    if (!isMessage(message)) {
      return;
    }
    if (message.type === "refresh") {
      await this.refreshService.refresh(true);
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
    const state = this.refreshService.getStates().find((candidate) => candidate.id === message.providerId);
    if (!state) {
      return;
    }
    if (message.type === "openAccount") {
      await vscode.env.openExternal(vscode.Uri.parse(state.accountUrl));
    } else if (message.type === "copySignIn") {
      await vscode.env.clipboard.writeText(state.signInCommand);
      void vscode.window.showInformationMessage(`Copied: ${state.signInCommand}`);
    }
  }
}

function isMessage(value: unknown): value is { readonly type: string; readonly providerId?: string } {
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
    body {
      margin: 0;
      padding: 10px;
      color: var(--vscode-foreground);
      background: var(--vscode-sideBar-background);
      font: 13px/1.4 var(--vscode-font-family);
    }
    button { font: inherit; }
    .masthead {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: 8px;
      padding: 2px 2px 10px;
    }
    .masthead h1 { margin: 0; font-size: 12px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
    .masthead span { color: var(--vscode-descriptionForeground); font-size: 11px; }
    .providers { display: grid; gap: 9px; }
    .card {
      position: relative;
      overflow: hidden;
      border: 1px solid var(--vscode-widget-border, transparent);
      border-radius: 6px;
      background: var(--vscode-editorWidget-background);
      box-shadow: 0 1px 2px color-mix(in srgb, var(--vscode-widget-shadow) 20%, transparent);
    }
    .card::before {
      content: "";
      position: absolute;
      inset: 0 auto 0 0;
      width: 3px;
      background: var(--vscode-progressBar-background);
    }
    .card[data-level="warning"]::before { background: var(--vscode-editorWarning-foreground); }
    .card[data-level="critical"]::before { background: var(--vscode-errorForeground); }
    .card[data-stale="true"] { opacity: .76; }
    .card-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 8px; padding: 10px 10px 8px 12px; }
    .provider { min-width: 0; }
    .provider-name { display: block; font-weight: 650; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .plan { color: var(--vscode-descriptionForeground); font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .headline { font-family: var(--vscode-editor-font-family); font-size: 18px; font-weight: 650; letter-spacing: -.04em; white-space: nowrap; }
    .metrics { display: grid; gap: 9px; padding: 0 10px 10px 12px; }
    .metric-head, .metric-foot { display: flex; justify-content: space-between; gap: 8px; }
    .metric-head { margin-bottom: 4px; font-size: 11px; }
    .metric-name { font-weight: 600; }
    .metric-value, .metric-foot { color: var(--vscode-descriptionForeground); }
    .metric-foot { margin-top: 4px; font-size: 10px; }
    .rail { appearance: none; display: block; width: 100%; height: 5px; overflow: hidden; border: 0; border-radius: 999px; background: transparent; }
    .rail::-webkit-progress-bar { border-radius: inherit; background: color-mix(in srgb, var(--vscode-progressBar-background) 26%, transparent); }
    .rail::-webkit-progress-value { border-radius: inherit; background: var(--vscode-progressBar-background); }
    .metric[data-level="warning"] .rail::-webkit-progress-value { background: var(--vscode-editorWarning-foreground); }
    .metric[data-level="critical"] .rail::-webkit-progress-value { background: var(--vscode-errorForeground); }
    .balance { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; padding: 7px 8px; border-radius: 4px; background: var(--vscode-textCodeBlock-background); }
    .balance strong { font-family: var(--vscode-editor-font-family); }
    .error { margin: 0 10px 10px 12px; padding: 7px 8px; border-left: 2px solid var(--vscode-errorForeground); color: var(--vscode-errorForeground); background: var(--vscode-inputValidation-errorBackground); font-size: 11px; }
    .actions { display: flex; gap: 6px; margin-top: 6px; }
    .card-actions { padding: 0 10px 10px 12px; }
    .action {
      border: 0;
      border-radius: 3px;
      padding: 3px 7px;
      color: var(--vscode-button-secondaryForeground);
      background: var(--vscode-button-secondaryBackground);
      cursor: pointer;
    }
    .action:hover { background: var(--vscode-button-secondaryHoverBackground); }
    .action:focus-visible { outline: 1px solid var(--vscode-focusBorder); outline-offset: 2px; }
    .empty { padding: 24px 12px; border: 1px dashed var(--vscode-widget-border); border-radius: 6px; color: var(--vscode-descriptionForeground); text-align: center; }
    .empty strong { display: block; margin-bottom: 5px; color: var(--vscode-foreground); }
  </style>
</head>
<body>
  <header class="masthead"><h1>Quota instruments</h1><span id="stamp">Waiting for first reading</span></header>
  <main id="providers" class="providers" aria-live="polite"></main>
  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    const providers = document.getElementById('providers');
    const stamp = document.getElementById('stamp');
    const el = (tag, className, text) => {
      const node = document.createElement(tag);
      if (className) node.className = className;
      if (text !== undefined) node.textContent = text;
      return node;
    };
    const button = (label, type, providerId) => {
      const node = el('button', 'action', label);
      node.type = 'button';
      node.addEventListener('click', () => vscode.postMessage({ type, providerId }));
      return node;
    };
    const level = (used, warning, critical) => used >= critical ? 'critical' : used >= warning ? 'warning' : 'normal';
    const countdown = ${formatResetCountdown.toString()};
    const renderMetric = (metric, warning, critical) => {
      if (metric.usedPercent === null || metric.isUnlimited) {
        const row = el('div', 'balance');
        row.append(el('span', '', metric.name), el('strong', '', metric.remainingText || (metric.isUnlimited ? 'UNLIMITED' : metric.usageText || 'Not reported')));
        return row;
      }
      const metricLevel = level(metric.usedPercent, warning, critical);
      const root = el('section', 'metric');
      root.dataset.level = metricLevel;
      const head = el('div', 'metric-head');
      head.append(el('span', 'metric-name', metric.name), el('span', 'metric-value', metric.usedPercent + '% used'));
      const rail = el('progress', 'rail');
      rail.setAttribute('aria-label', metric.name + ' usage');
      rail.max = 100;
      rail.value = metric.usedPercent;
      const foot = el('div', 'metric-foot');
      foot.append(el('span', '', metric.usageText || (100 - metric.usedPercent) + '% left'), el('span', '', countdown(metric.resetsAt)));
      root.append(head, rail, foot);
      return root;
    };
    const render = (payload) => {
      providers.replaceChildren();
      const warning = payload.warningPercent ?? 72;
      const critical = payload.criticalPercent ?? 90;
      if (!payload.states.length) {
        const empty = el('div', 'empty');
        empty.append(el('strong', '', 'No providers selected'), el('span', '', 'Choose providers in UsageAI settings.'));
        empty.append(button('Open settings', 'settings'));
        providers.append(empty);
        return;
      }
      let newest = 0;
      for (const state of payload.states) {
        const card = el('article', 'card');
        card.dataset.stale = String(state.stale);
        const highest = state.snapshot ? Math.max(0, ...state.snapshot.metrics.filter(m => m.usedPercent !== null && !m.isUnlimited).map(m => m.usedPercent)) : 0;
        card.dataset.level = level(highest, warning, critical);
        const head = el('div', 'card-head');
        const provider = el('div', 'provider');
        provider.append(el('span', 'provider-name', state.displayName), el('span', 'plan', state.snapshot ? [state.snapshot.plan, state.snapshot.accountName].filter(Boolean).join(' · ') : 'Not connected'));
        const headline = el('div', 'headline', state.refreshing ? '•••' : state.snapshot ? highest + '%' : '—');
        head.append(provider, headline);
        card.append(head);
        if (state.snapshot) {
          newest = Math.max(newest, new Date(state.snapshot.fetchedAt).getTime());
          const metrics = el('div', 'metrics');
          for (const metric of state.snapshot.metrics) metrics.append(renderMetric(metric, warning, critical));
          card.append(metrics);
        }
        if (state.error) {
          const error = el('div', 'error');
          error.append(el('div', '', state.error));
          const actions = el('div', 'actions');
          actions.append(button('Copy sign-in', 'copySignIn', state.id), button('Refresh', 'refresh', state.id));
          error.append(actions);
          card.append(error);
        } else if (state.snapshot) {
          const actions = el('div', 'actions card-actions');
          actions.append(button('Account', 'openAccount', state.id));
          card.append(actions);
        }
        providers.append(card);
      }
      stamp.textContent = newest ? 'Updated ' + new Date(newest).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : 'Waiting for first reading';
    };
    window.addEventListener('message', (event) => {
      if (event.data?.type === 'states') render(event.data);
    });
    vscode.postMessage({ type: 'ready' });
  </script>
</body>
</html>`;
}

function randomNonce(): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  let result = "";
  for (let index = 0; index < 32; index += 1) {
    result += alphabet.charAt(Math.floor(Math.random() * alphabet.length));
  }
  return result;
}
