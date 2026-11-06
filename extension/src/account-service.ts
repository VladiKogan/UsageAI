import { EventEmitter } from "node:events";
import * as path from "node:path";
import {
  accountFolderProblem,
  accountIdForPath,
  accountLabel,
  accountProviderIds,
  claudeProfilePath,
  credentialFileName,
  defaultAccountId,
  defaultLoginFolder,
  discoverAccountFolders,
  distinctAccountLabels,
  identityKey,
  nodeAccountFileSystem,
  providerTitle,
  readClaudeIdentity,
  readCodexIdentity,
  sanitizeAccountSettings,
  unitKey,
  type AccountEntry,
  type AccountFileSystem,
  type AccountIdentity,
  type AccountProviderId,
  type AccountSetting,
  type DiscoveredFolder,
} from "./accounts";

/** globalState key for scan time and the found accounts the user has answered. */
export const accountsStateKey = "usageai.accounts.state.v1";
/** Found logins are looked for at activation, on demand, and otherwise once a day. */
export const accountScanIntervalMs = 24 * 60 * 60_000;
const maxRememberedAnswers = 256;

export interface AccountState {
  readonly lastScanAt?: number;
  /** Found accounts the user chose to show (or that were shown on the first run). */
  readonly shown: readonly string[];
  readonly ignored: readonly string[];
}

/** The stored state, or nothing when it is absent: a fresh install or an upgrade. */
export function sanitizeAccountState(value: unknown): AccountState | undefined {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return undefined;
  }
  const record = value as Record<string, unknown>;
  const ids = (candidate: unknown): string[] => Array.isArray(candidate)
    ? [...new Set(candidate.filter((id): id is string => typeof id === "string" && /^[0-9a-f]{8}$/.test(id)))].slice(-maxRememberedAnswers)
    : [];
  const lastScanAt = typeof record.lastScanAt === "number" && Number.isFinite(record.lastScanAt) && record.lastScanAt > 0
    ? record.lastScanAt
    : undefined;
  return { ...(lastScanAt !== undefined ? { lastScanAt } : {}), shown: ids(record.shown), ignored: ids(record.ignored) };
}

export interface AccountCandidate<T> {
  readonly source: T;
  readonly identity: AccountIdentity;
}

export interface ResolveAccountsInput {
  /** One per provider: the primary login, with the folder its CLI reads today. */
  readonly defaults: readonly AccountCandidate<{ readonly provider: AccountProviderId; readonly path: string }>[];
  /** `usageai.accounts` entries whose folder holds a login. */
  readonly configured: readonly AccountCandidate<AccountSetting>[];
  readonly discovered: readonly AccountCandidate<DiscoveredFolder>[];
  readonly state: AccountState;
}

export interface ResolvedAccounts {
  /** Shown accounts, grouped by provider: the default first, then added folders, then found ones. */
  readonly shown: readonly AccountEntry[];
  /** Found accounts waiting for Show / Ignore. */
  readonly pending: readonly AccountEntry[];
  /** Found or added logins of someone already shown, left out. */
  readonly duplicates: readonly { readonly account: AccountEntry; readonly shownAs: string }[];
}

/**
 * Decides which logins are shown. A folder the user listed in `usageai.accounts` is shown unless
 * hidden; a found folder is shown once answered with Show; a login whose email and organization
 * match an account already shown is never added twice.
 */
export function resolveAccounts(input: ResolveAccountsInput): ResolvedAccounts {
  const shown: AccountEntry[] = [];
  const pending: AccountEntry[] = [];
  const duplicates: { account: AccountEntry; shownAs: string }[] = [];
  const shownIdentities = new Map<string, string>();
  const shownIds = new Set(input.state.shown);
  const ignoredIds = new Set(input.state.ignored);

  const claim = (entry: AccountEntry): boolean => {
    const key = identityKey(entry.identity);
    const existing = key ? shownIdentities.get(`${entry.provider}|${key}`) : undefined;
    if (existing !== undefined) {
      duplicates.push({ account: entry, shownAs: existing });
      return false;
    }
    if (key) {
      shownIdentities.set(`${entry.provider}|${key}`, entry.label);
    }
    return true;
  };

  for (const provider of accountProviderIds) {
    const defaultCandidate = input.defaults.find((candidate) => candidate.source.provider === provider);
    const defaultPathId = defaultCandidate ? accountIdForPath(defaultCandidate.source.path) : undefined;
    const handled = new Set<string>(defaultPathId ? [defaultPathId] : []);
    const group: AccountEntry[] = [];

    if (defaultCandidate) {
      const override = input.configured.find((candidate) =>
        candidate.source.provider === provider && accountIdForPath(candidate.source.path) === defaultPathId);
      const userLabel = override?.source.label;
      const entry: AccountEntry = {
        provider,
        id: defaultAccountId,
        unitKey: unitKey(provider, defaultAccountId),
        path: defaultCandidate.source.path,
        source: "default",
        identity: defaultCandidate.identity,
        ...(userLabel ? { userLabel } : {}),
        label: accountLabel(userLabel, defaultCandidate.identity, defaultCandidate.source.path),
        hidden: override?.source.hidden ?? false,
      };
      if (!entry.hidden) {
        claim(entry);
        group.push(entry);
      }
    }

    for (const candidate of input.configured) {
      const id = accountIdForPath(candidate.source.path);
      if (candidate.source.provider !== provider || handled.has(id)) {
        continue;
      }
      handled.add(id);
      const entry: AccountEntry = {
        provider,
        id,
        unitKey: unitKey(provider, id),
        path: candidate.source.path,
        source: "added",
        identity: candidate.identity,
        ...(candidate.source.label ? { userLabel: candidate.source.label } : {}),
        label: accountLabel(candidate.source.label, candidate.identity, candidate.source.path),
        hidden: candidate.source.hidden,
      };
      if (!entry.hidden && claim(entry)) {
        group.push(entry);
      }
    }

    for (const candidate of input.discovered) {
      if (candidate.source.provider !== provider || handled.has(candidate.source.id)) {
        continue;
      }
      handled.add(candidate.source.id);
      const entry: AccountEntry = {
        provider,
        id: candidate.source.id,
        unitKey: unitKey(provider, candidate.source.id),
        path: candidate.source.path,
        source: "found",
        identity: candidate.identity,
        label: accountLabel(undefined, candidate.identity, candidate.source.path),
        hidden: false,
      };
      if (ignoredIds.has(entry.id)) {
        continue;
      }
      if (!claim(entry)) {
        continue;
      }
      if (shownIds.has(entry.id)) {
        group.push(entry);
      } else {
        // Not claimed while pending, so answering Show cannot turn it into a duplicate of itself.
        const key = identityKey(entry.identity);
        if (key) shownIdentities.delete(`${provider}|${key}`);
        pending.push(entry);
      }
    }
    shown.push(...distinctAccountLabels(group));
  }
  return { shown, pending, duplicates };
}

export interface AccountServiceDependencies {
  readonly home: () => string;
  readonly environment?: () => NodeJS.ProcessEnv;
  /** The raw `usageai.accounts` value. */
  readonly settings: () => unknown;
  readonly loadState: () => unknown;
  readonly saveState: (state: AccountState) => PromiseLike<void> | void;
  readonly now?: () => number;
  readonly fileSystem?: AccountFileSystem;
  /** The one-time message on the first run that found extra logins. */
  readonly announce?: (message: string) => void;
  /** "New Claude account found: x@… — Show / Ignore". Nothing appears until answered. */
  readonly askToShow?: (account: AccountEntry, message: string) => PromiseLike<"show" | "ignore" | undefined>;
  readonly warn?: (message: string) => void;
}

/**
 * Keeps the list of logins: the default ones, folders from `usageai.accounts`, and folders found by
 * one listing of the home folder at activation, on demand and once a day — never on a refresh tick.
 */
export class AccountService {
  private readonly emitter = new EventEmitter();
  private discovered: readonly DiscoveredFolder[] = [];
  private input: Omit<ResolveAccountsInput, "state"> = { defaults: [], configured: [], discovered: [] };
  private state: AccountState | undefined;
  private resolved: ResolvedAccounts = { shown: [], pending: [], duplicates: [] };
  private readonly asking = new Set<string>();
  private lastWarning = "";
  private signature = "";
  /** Providers whose default folder holds a login, so the first-run message never names an empty one. */
  private defaultLogins = new Set<AccountProviderId>();
  private disposed = false;

  public constructor(private readonly dependencies: AccountServiceDependencies) {
    this.state = sanitizeAccountState(dependencies.loadState());
  }

  /** Shown accounts, the default logins included. */
  public get accounts(): readonly AccountEntry[] {
    return this.resolved.shown;
  }

  public get pending(): readonly AccountEntry[] {
    return this.resolved.pending;
  }

  public get duplicates(): ResolvedAccounts["duplicates"] {
    return this.resolved.duplicates;
  }

  public get lastScanAt(): number | undefined {
    return this.state?.lastScanAt;
  }

  public onDidChange(listener: (accounts: readonly AccountEntry[]) => void): () => void {
    this.emitter.on("change", listener);
    return () => this.emitter.off("change", listener);
  }

  public dispose(): void {
    this.disposed = true;
    this.emitter.removeAllListeners();
  }

  /** Scans when the last scan is a day old. */
  public async scanIfDue(): Promise<boolean> {
    const last = this.state?.lastScanAt ?? 0;
    if (this.now() - last < accountScanIntervalMs && this.now() >= last) {
      return false;
    }
    await this.scan();
    return true;
  }

  /** One listing of the home folder, then the found accounts not answered yet are offered. */
  public async scan(): Promise<{ readonly pending: number }> {
    const defaults = this.defaultFolders();
    this.discovered = await discoverAccountFolders(this.dependencies.home(), Object.fromEntries(
      defaults.map((entry) => [entry.provider, entry.path]),
    ), this.fileSystem());
    await this.readInputs();
    const firstRun = this.state === undefined;
    if (firstRun) {
      // A fresh install or an upgrade: the logins already here are the user's own, so they show at
      // once with one message instead of a question each.
      const preview = resolveAccounts({ ...this.input, state: { shown: [], ignored: [] } });
      this.state = { shown: preview.pending.map((entry) => entry.id), ignored: [], lastScanAt: this.now() };
    } else {
      this.state = { ...this.currentState(), lastScanAt: this.now() };
    }
    await this.dependencies.saveState(this.currentState());
    this.apply();
    if (firstRun) {
      this.announceFirstRun();
    }
    this.askAboutPending();
    return { pending: this.resolved.pending.length };
  }

  /** Re-reads `usageai.accounts` and the known folders, without listing the home folder again. */
  public async reload(): Promise<void> {
    await this.readInputs();
    this.apply();
  }

  /** Shows a pending found account, as its Show button does. */
  public async answer(id: string, decision: "show" | "ignore"): Promise<void> {
    const state = this.currentState();
    this.state = {
      ...state,
      shown: decision === "show" ? [...state.shown.filter((value) => value !== id), id] : state.shown.filter((value) => value !== id),
      ignored: decision === "ignore" ? [...state.ignored.filter((value) => value !== id), id] : state.ignored.filter((value) => value !== id),
    };
    await this.dependencies.saveState(this.currentState());
    this.apply();
  }

  private currentState(): AccountState {
    return this.state ?? { shown: [], ignored: [] };
  }

  private now(): number {
    return this.dependencies.now?.() ?? Date.now();
  }

  private fileSystem(): AccountFileSystem {
    return this.dependencies.fileSystem ?? nodeAccountFileSystem;
  }

  private environment(): NodeJS.ProcessEnv {
    return this.dependencies.environment?.() ?? process.env;
  }

  private defaultFolders(): Array<{ readonly provider: AccountProviderId; readonly path: string }> {
    const home = this.dependencies.home();
    return accountProviderIds.map((provider) => ({
      provider,
      path: defaultLoginFolder(provider, this.environment(), home)
        ?? path.join(home, provider === "claude" ? ".claude" : ".codex"),
    }));
  }

  private async identityOf(provider: AccountProviderId, folder: string, isDefault: boolean): Promise<AccountIdentity> {
    if (provider === "codex") {
      return readCodexIdentity(folder);
    }
    const withoutConfigDirectory = isDefault && !this.environment().CLAUDE_CONFIG_DIR?.trim();
    return readClaudeIdentity(claudeProfilePath(folder, withoutConfigDirectory, this.dependencies.home()));
  }

  private async readInputs(): Promise<void> {
    const defaults = this.defaultFolders();
    const defaultIds = new Set(defaults.map((entry) => `${entry.provider}:${accountIdForPath(entry.path)}`));
    const settings = sanitizeAccountSettings(this.dependencies.settings());
    const rejected = [...settings.rejected];
    const configured: AccountCandidate<AccountSetting>[] = [];
    for (const setting of settings.entries) {
      const isDefault = defaultIds.has(`${setting.provider}:${accountIdForPath(setting.path)}`);
      // A label or hidden flag for the default folder needs no login check of its own.
      const problem = isDefault ? undefined : await accountFolderProblem(setting.provider, setting.path, this.fileSystem());
      if (problem) {
        rejected.push(`${setting.path}: ${problem}`);
        continue;
      }
      configured.push({ source: setting, identity: await this.identityOf(setting.provider, setting.path, isDefault) });
    }
    const defaultLogins = new Set<AccountProviderId>();
    for (const entry of defaults) {
      if (await this.fileSystem().isFile(path.join(entry.path, credentialFileName(entry.provider)))) {
        defaultLogins.add(entry.provider);
      }
    }
    this.defaultLogins = defaultLogins;
    this.input = {
      defaults: await Promise.all(defaults.map(async (entry) => ({
        source: entry,
        identity: await this.identityOf(entry.provider, entry.path, true),
      }))),
      configured,
      discovered: await Promise.all(this.discovered.map(async (folder) => ({
        source: folder,
        identity: await this.identityOf(folder.provider, folder.path, false),
      }))),
    };
    const warning = rejected.length > 0
      ? `UsageAI ignored ${rejected.length === 1 ? "an entry" : `${rejected.length} entries`} in usageai.accounts: ${rejected.join("; ")}.`
      : "";
    if (warning && warning !== this.lastWarning) {
      this.dependencies.warn?.(warning);
    }
    this.lastWarning = warning;
  }

  private apply(): void {
    this.resolved = resolveAccounts({ ...this.input, state: this.currentState() });
    const signature = JSON.stringify(this.resolved.shown.map((entry) => [entry.unitKey, entry.path, entry.label]));
    if (signature !== this.signature) {
      this.signature = signature;
      if (!this.disposed) {
        this.emitter.emit("change", this.resolved.shown);
      }
    }
  }

  private announceFirstRun(): void {
    const parts: string[] = [];
    for (const provider of accountProviderIds) {
      const accounts = this.resolved.shown.filter((entry) =>
        entry.provider === provider && (entry.source !== "default" || this.defaultLogins.has(provider)));
      if (!accounts.some((entry) => entry.source === "found")) {
        continue;
      }
      parts.push(`Found ${accounts.length} ${providerTitle(provider)} accounts: ${accounts.map((entry) => entry.label).join(", ")}.`);
    }
    if (parts.length > 0) {
      this.dependencies.announce?.(parts.join(" "));
    }
  }

  private askAboutPending(): void {
    for (const account of this.resolved.pending) {
      if (this.asking.has(account.id) || !this.dependencies.askToShow) {
        continue;
      }
      this.asking.add(account.id);
      void Promise.resolve(this.dependencies.askToShow(account, `New ${providerTitle(account.provider)} account found: ${account.label}`))
        .then(async (decision) => {
          if (decision && !this.disposed) {
            await this.answer(account.id, decision);
          }
        }, () => undefined)
        .finally(() => this.asking.delete(account.id));
    }
  }
}
