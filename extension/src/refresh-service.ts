import { EventEmitter } from "node:events";
import { accountCardTitle, orderUnits, providerKindOf } from "./accounts";
import { UsageProviderError, safeErrorMessage } from "./errors";
import type { ProviderState, UsageClient, UsageSnapshot } from "./model";

interface BackoffState {
  failures: number;
  retryAt: number;
}

interface RefreshServiceOptions {
  /** One client per refresh unit: a provider's default login, or one of its other accounts. */
  readonly clients: readonly UsageClient[];
  /** Provider ids (not unit keys) to show, in order; every account of a provider follows it. */
  readonly enabledProviderIds: () => readonly string[];
  /** The order the user dragged the cards into; empty keeps each provider's accounts together. */
  readonly unitOrder?: () => readonly string[];
  readonly visibleIntervalMs: () => number;
  readonly backgroundIntervalMs: () => number;
  readonly initialSnapshots?: Readonly<Record<string, UsageSnapshot>>;
  /** The provider's ongoing status-page incident, attached to each published state. */
  readonly incidentFor?: (providerId: string) => { readonly summary: string; readonly isOutage: boolean } | undefined;
  /** A unit's account label; it titles the card only while its provider shows several accounts. */
  readonly accountLabelFor?: (unitKey: string) => string | undefined;
}

export class UsageRefreshService {
  private readonly emitter = new EventEmitter();
  private readonly states = new Map<string, ProviderState>();
  private readonly backoff = new Map<string, BackoffState>();
  /** Units added after start that have not been fetched yet; the next refresh takes them at once. */
  private readonly unfetched = new Set<string>();
  private clients: readonly UsageClient[];
  private timer: NodeJS.Timeout | undefined;
  private activeRefresh: Promise<void> | undefined;
  private visible = false;
  private disposed = false;
  private nextRegularRefreshAt = 0;

  public constructor(private readonly options: RefreshServiceOptions) {
    this.clients = options.clients;
    for (const client of options.clients) {
      this.addState(client);
    }
  }

  /**
   * Replaces the refresh units after the account list changed. A unit that stays keeps its state and
   * backoff; a new one starts from its cached snapshot and is fetched at once; a removed one is gone.
   */
  public setClients(clients: readonly UsageClient[]): void {
    const ids = new Set(clients.map((client) => client.id));
    for (const id of [...this.states.keys()]) {
      if (!ids.has(id)) {
        this.states.delete(id);
        this.backoff.delete(id);
        this.unfetched.delete(id);
      }
    }
    let added = false;
    for (const client of clients) {
      if (!this.states.has(client.id)) {
        this.addState(client);
        this.unfetched.add(client.id);
        added = true;
      }
    }
    this.clients = clients;
    this.emit();
    if (added && !this.disposed) {
      void this.refresh(false);
    }
  }

  private addState(client: UsageClient): void {
    const snapshot = this.options.initialSnapshots?.[client.id];
    this.states.set(client.id, {
      id: client.id,
      displayName: client.displayName,
      signInCommand: client.signInCommand,
      accountUrl: client.accountUrl,
      ...(snapshot ? { snapshot, stale: true } : { stale: false }),
      refreshing: false,
    });
  }

  public onDidUpdate(listener: (states: readonly ProviderState[]) => void): () => void {
    this.emitter.on("update", listener);
    return () => this.emitter.off("update", listener);
  }

  public getStates(): readonly ProviderState[] {
    const order = this.options.enabledProviderIds();
    const grouped = order.flatMap((providerId) => {
      const units = this.clients.filter((client) => providerKindOf(client.id) === providerId);
      return units.flatMap((client) => {
        const state = this.states.get(client.id);
        if (!state) {
          return [];
        }
        const label = units.length > 1 ? this.options.accountLabelFor?.(client.id) : undefined;
        const { accountLabel: _previous, ...base } = state;
        const incident = this.options.incidentFor?.(providerId);
        return [{
          ...base,
          displayName: accountCardTitle(client.displayName, label, units.length),
          signInCommand: client.signInCommand,
          accountUrl: client.accountUrl,
          ...(label ? { accountLabel: label } : {}),
          ...(incident ? { incident: incident.summary, incidentIsOutage: incident.isOutage } : {}),
        }];
      });
    });
    const saved = this.options.unitOrder?.() ?? [];
    return saved.length === 0
      ? grouped
      : orderUnits(grouped.map((state) => state.id), saved).flatMap((unit) => grouped.filter((state) => state.id === unit));
  }

  /** The cards were dragged into a new order. */
  public orderChanged(): void {
    this.emit();
  }

  /** Republishes the current states, for example after the providers' status pages changed. */
  public incidentsChanged(): void {
    this.emit();
  }

  public start(): void {
    void this.refresh(true);
  }

  public setVisible(visible: boolean): void {
    this.visible = visible;
    this.nextRegularRefreshAt = Date.now() + this.regularIntervalMs();
    this.scheduleNext();
  }

  public configurationChanged(): void {
    this.emit();
    this.nextRegularRefreshAt = Date.now() + this.regularIntervalMs();
    this.scheduleNext();
  }

  public refresh(manual = false): Promise<void> {
    if (this.disposed) {
      return Promise.resolve();
    }
    if (this.activeRefresh) {
      return this.activeRefresh;
    }
    this.activeRefresh = this.runRefresh(manual).finally(() => {
      this.activeRefresh = undefined;
      this.scheduleNext();
    });
    return this.activeRefresh;
  }

  public dispose(): void {
    this.disposed = true;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = undefined;
    }
    this.emitter.removeAllListeners();
  }

  private async runRefresh(manual: boolean): Promise<void> {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = undefined;
    }
    const enabled = new Set(this.options.enabledProviderIds());
    const now = Date.now();
    const attemptedAt = new Date(now).toISOString();
    const regularRefresh = manual || now >= this.nextRegularRefreshAt;
    const clients = this.clients.filter((client) => {
      if (!enabled.has(providerKindOf(client.id))) {
        return false;
      }
      if (manual || this.unfetched.has(client.id)) {
        return true;
      }
      const backoff = this.backoff.get(client.id);
      return backoff ? backoff.retryAt <= now : regularRefresh;
    });
    for (const client of clients) {
      this.unfetched.delete(client.id);
      if (manual) {
        // The user asked for fresh numbers, so a provider sitting out its own recovery backoff
        // should try that path again rather than repeat a degraded reading.
        client.onForcedRefresh?.();
      }
      const current = this.states.get(client.id);
      if (current) {
        this.states.set(client.id, { ...current, refreshing: true });
      }
    }
    this.emit();

    const results = await Promise.all(clients.map(async (client) => {
      try {
        return { client, snapshot: await client.getUsage() } as const;
      } catch (error) {
        return { client, error } as const;
      }
    }));

    for (const result of results) {
      const current = this.states.get(result.client.id);
      if (!current) {
        continue;
      }
      if ("snapshot" in result) {
        this.backoff.delete(result.client.id);
        const { error: _error, nextRefreshAt: _nextRefreshAt, ...base } = current;
        this.states.set(result.client.id, {
          ...base,
          snapshot: result.snapshot,
          stale: false,
          refreshing: false,
          lastAttemptedAt: attemptedAt,
        });
      } else {
        const previous = this.backoff.get(result.client.id)?.failures ?? 0;
        const failures = Math.min(previous + 1, 7);
        const hintedMs = result.error instanceof UsageProviderError ? result.error.retryAfterMs : 0;
        const backoffMs = hintedMs > 0 ? hintedMs : Math.min(60, 2 ** (failures - 1)) * 60_000;
        const retryAt = Date.now() + backoffMs;
        this.backoff.set(result.client.id, { failures, retryAt });
        this.states.set(result.client.id, {
          ...current,
          stale: Boolean(current.snapshot),
          refreshing: false,
          error: safeErrorMessage(result.error),
          lastAttemptedAt: attemptedAt,
          nextRefreshAt: new Date(retryAt).toISOString(),
        });
      }
    }
    if (regularRefresh) {
      this.nextRegularRefreshAt = Date.now() + this.regularIntervalMs();
    }
    this.emit();
  }

  private emit(): void {
    this.emitter.emit("update", this.getStates());
  }

  private scheduleNext(): void {
    if (this.disposed) {
      return;
    }
    if (this.timer) {
      clearTimeout(this.timer);
    }
    const now = Date.now();
    const interval = this.regularIntervalMs();
    const regularDelay = this.nextRegularRefreshAt > 0
      ? Math.max(0, this.nextRegularRefreshAt - now)
      : interval;
    const enabled = new Set(this.options.enabledProviderIds());
    const nextBackoff = [...this.backoff.entries()]
      .filter(([unit]) => enabled.has(providerKindOf(unit)))
      .map(([, entry]) => Math.max(0, entry.retryAt - now))
      .sort((left, right) => left - right)[0];
    // A unit added while a refresh was running is fetched right after it rather than at the next interval.
    const waiting = [...this.unfetched].some((unit) => enabled.has(providerKindOf(unit)));
    const delay = waiting ? 1_000 : Math.max(1_000, Math.min(regularDelay, nextBackoff ?? regularDelay));
    this.timer = setTimeout(() => void this.refresh(false), delay);
    this.timer.unref();
  }

  private regularIntervalMs(): number {
    return this.visible
      ? this.options.visibleIntervalMs()
      : this.options.backgroundIntervalMs();
  }
}
