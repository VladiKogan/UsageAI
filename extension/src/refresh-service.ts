import { EventEmitter } from "node:events";
import { UsageProviderError, safeErrorMessage } from "./errors";
import type { ProviderState, UsageClient, UsageSnapshot } from "./model";

interface BackoffState {
  failures: number;
  retryAt: number;
}

interface RefreshServiceOptions {
  readonly clients: readonly UsageClient[];
  readonly enabledProviderIds: () => readonly string[];
  readonly visibleIntervalMs: () => number;
  readonly backgroundIntervalMs: () => number;
  readonly initialSnapshots?: Readonly<Record<string, UsageSnapshot>>;
}

export class UsageRefreshService {
  private readonly emitter = new EventEmitter();
  private readonly states = new Map<string, ProviderState>();
  private readonly backoff = new Map<string, BackoffState>();
  private timer: NodeJS.Timeout | undefined;
  private activeRefresh: Promise<void> | undefined;
  private visible = false;
  private disposed = false;

  public constructor(private readonly options: RefreshServiceOptions) {
    for (const client of options.clients) {
      const snapshot = options.initialSnapshots?.[client.id];
      this.states.set(client.id, {
        id: client.id,
        displayName: client.displayName,
        signInCommand: client.signInCommand,
        accountUrl: client.accountUrl,
        ...(snapshot ? { snapshot, stale: true } : { stale: false }),
        refreshing: false,
      });
    }
  }

  public onDidUpdate(listener: (states: readonly ProviderState[]) => void): () => void {
    this.emitter.on("update", listener);
    return () => this.emitter.off("update", listener);
  }

  public getStates(): readonly ProviderState[] {
    const order = this.options.enabledProviderIds();
    return order
      .map((id) => this.states.get(id))
      .filter((state): state is ProviderState => Boolean(state));
  }

  public start(): void {
    void this.refresh(true);
  }

  public setVisible(visible: boolean): void {
    this.visible = visible;
    this.scheduleNext();
  }

  public configurationChanged(): void {
    this.emit();
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
    const clients = this.options.clients.filter((client) => {
      if (!enabled.has(client.id)) {
        return false;
      }
      const retryAt = this.backoff.get(client.id)?.retryAt ?? 0;
      return manual || retryAt <= now;
    });
    for (const client of clients) {
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
          nextRefreshAt: new Date(retryAt).toISOString(),
        });
      }
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
    const interval = this.visible
      ? this.options.visibleIntervalMs()
      : this.options.backgroundIntervalMs();
    const nextBackoff = [...this.backoff.values()]
      .map((entry) => entry.retryAt - Date.now())
      .filter((delay) => delay > 0)
      .sort((left, right) => left - right)[0];
    const delay = Math.max(1_000, Math.min(interval, nextBackoff ?? interval));
    this.timer = setTimeout(() => void this.refresh(false), delay);
    this.timer.unref();
  }
}
