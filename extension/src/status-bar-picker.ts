import { isAccountProviderId, isAccountUnitKey, type AccountEntry } from "./accounts";
import { reportSettingsWriteError, type AddAccountUi } from "./add-account";
import type { StatusBarProviderId, StatusBarSelectionId } from "./model";

export type UsageProviderId = Exclude<StatusBarProviderId, "hottest">;

/** How many extra accounts the Status Bar may show. */
export const maxStatusBarAccounts = 16;

/**
 * Extra Status Bar entries: each an account unit key (`claude@1a2b3c4d`), as the picker saves
 * them, or a shown account's label or email, as the former setting allowed by hand.
 */
export function resolveStatusBarAccounts(value: unknown, accounts: readonly AccountEntry[]): string[] {
  const names = Array.isArray(value) ? value.filter((entry): entry is string => typeof entry === "string") : [];
  const resolved: string[] = [];
  for (const name of names.slice(0, maxStatusBarAccounts)) {
    const match = resolveStatusBarAccount(name, accounts);
    if (match && !resolved.includes(match)) {
      resolved.push(match);
    }
  }
  return resolved;
}

function resolveStatusBarAccount(name: string, accounts: readonly AccountEntry[]): string | undefined {
  const trimmed = name.trim();
  const lower = trimmed.toLowerCase();
  return isAccountUnitKey(trimmed)
    ? trimmed
    : accounts.find((account) => account.label.toLowerCase() === lower || account.identity.email?.toLowerCase() === lower)?.unitKey;
}

/** One checkbox in the picker: a provider's main login, another account, or the fallback. */
export interface StatusBarPickItem {
  readonly label: string;
  readonly description?: string;
  readonly picked: boolean;
  /** A provider id (its main login), an account unit key, or "hottest". */
  readonly unit: string;
}

export interface StatusBarPickSeparator {
  readonly separator: string;
}

export interface StatusBarPickerUi extends Pick<AddAccountUi, "error" | "offerReload"> {
  /** A multi-select list; the chosen items, or nothing when dismissed. */
  pickMany(
    items: readonly (StatusBarPickItem | StatusBarPickSeparator)[],
    title: string,
    placeHolder: string,
  ): PromiseLike<readonly StatusBarPickItem[] | undefined>;
}

/** What the picker writes: the main-login checkboxes it listed, the fallback, and the extra accounts. */
export interface StatusBarChange {
  readonly providers: Readonly<Partial<Record<UsageProviderId, boolean>>>;
  readonly hottestWhenNoneSelected: boolean;
  readonly accounts: readonly string[];
}

export interface StatusBarPickerDependencies {
  readonly ui: StatusBarPickerUi;
  /** Enabled providers, in Status Bar order. */
  readonly providers: () => readonly UsageProviderId[];
  /** Shown accounts, grouped by provider, main logins included. */
  readonly accounts: () => readonly AccountEntry[];
  readonly providerName: (provider: UsageProviderId) => string;
  /** The Status Bar items shown now, from whichever settings are in use. */
  readonly selection: () => readonly StatusBarSelectionId[];
  readonly hottestWhenNoneSelected: () => boolean;
  /** The saved extra accounts: the extension's own state, else the former setting's value. */
  readonly configuredAccounts: () => unknown;
  readonly save: (change: StatusBarChange) => PromiseLike<void>;
}

export type StatusBarPickerResult = "changed" | "cancelled" | "failed";

/** The picker's checkboxes, ticked from what the Status Bar shows now. */
export function statusBarPickItems(dependencies: Omit<StatusBarPickerDependencies, "ui" | "save" | "configuredAccounts">): Array<StatusBarPickItem | StatusBarPickSeparator> {
  const selection = new Set<string>(dependencies.selection());
  const accounts = dependencies.accounts();
  const items: Array<StatusBarPickItem | StatusBarPickSeparator> = [];
  for (const provider of dependencies.providers()) {
    const name = dependencies.providerName(provider);
    if (!isAccountProviderId(provider)) {
      items.push({ label: name, picked: selection.has(provider), unit: provider });
      continue;
    }
    const own = accounts.filter((account) => account.provider === provider);
    if (own.length === 0) {
      // The accounts are not read yet (the picker opened as the editor started): the provider's
      // own checkbox still stands for its main login.
      items.push({ label: name, picked: selection.has(provider), unit: provider });
    } else if (selection.has(provider) && !own.some((account) => account.source === "default")) {
      // The main login is hidden or signed out but its checkbox is still on: list it so it can be
      // unticked here rather than only in Settings.
      items.push({ label: name, description: "main login", picked: true, unit: provider });
    }
    for (const account of own) {
      const main = account.source === "default";
      const email = account.identity.email && account.identity.email !== account.label ? account.identity.email : undefined;
      items.push({
        label: main ? name : `${name} · ${account.label}`,
        description: main
          ? [own.length > 1 ? "main login" : undefined, account.label].filter(Boolean).join(" · ")
          : [email, account.unitKey].filter(Boolean).join(" · "),
        picked: selection.has(account.unitKey),
        unit: account.unitKey,
      });
    }
  }
  items.push({ separator: "When nothing above is chosen" });
  items.push({ label: "Show the account with the highest usage", picked: dependencies.hottestWhenNoneSelected(), unit: "hottest" });
  return items;
}

/**
 * The settings after the user's choice. Main logins become the provider checkboxes. The extra
 * accounts keep each entry the user wrote that still names a chosen account, or an account not
 * listed (hidden, or of a provider turned off), and new choices are added by their stable id.
 */
export function statusBarChange(
  items: readonly (StatusBarPickItem | StatusBarPickSeparator)[],
  chosen: readonly StatusBarPickItem[],
  configured: unknown,
  accounts: readonly AccountEntry[],
): StatusBarChange {
  const listed = items.filter((item): item is StatusBarPickItem => "unit" in item);
  const picked = new Set(chosen.map((item) => item.unit));
  const providers: Partial<Record<UsageProviderId, boolean>> = {};
  for (const item of listed) {
    if (item.unit !== "hottest" && !isAccountUnitKey(item.unit)) {
      providers[item.unit as UsageProviderId] = picked.has(item.unit);
    }
  }
  const listedUnits = new Set(listed.map((item) => item.unit).filter((unit) => unit !== "hottest"));
  const kept: string[] = [];
  const covered = new Set<string>();
  for (const name of Array.isArray(configured) ? configured : []) {
    if (typeof name !== "string") {
      continue;
    }
    const unit = resolveStatusBarAccount(name, accounts);
    // A name nothing resolves is kept once, so the list stays free of repeats.
    const key = unit ?? `name:${name.trim()}`;
    if (covered.has(key)) {
      continue;
    }
    // A name for a listed main login (its label or email) is dropped: the provider checkbox above
    // now says whether it shows, and keeping the name would show it after it was unticked.
    if (unit && listedUnits.has(unit) && !isAccountUnitKey(unit)) {
      continue;
    }
    if (!unit || !listedUnits.has(unit) || picked.has(unit)) {
      kept.push(name);
      covered.add(key);
    }
  }
  const added = [...listedUnits].filter((unit) => isAccountUnitKey(unit) && picked.has(unit) && !covered.has(unit));
  return {
    providers,
    hottestWhenNoneSelected: picked.has("hottest"),
    accounts: [...kept, ...added],
  };
}

/** `UsageAI: Choose Status Bar Items…`: one checkbox per account, main logins included. */
export async function chooseStatusBarItems(dependencies: StatusBarPickerDependencies): Promise<StatusBarPickerResult> {
  const { ui } = dependencies;
  const items = statusBarPickItems(dependencies);
  const chosen = await ui.pickMany(items, "Status Bar", "Choose the accounts to show in the Status Bar");
  if (!chosen) {
    return "cancelled";
  }
  const change = statusBarChange(items, chosen, dependencies.configuredAccounts(), dependencies.accounts());
  if (change.accounts.length > maxStatusBarAccounts) {
    // Cutting the list would drop a ticked account silently while reporting success.
    ui.error(`The Status Bar can show at most ${maxStatusBarAccounts} extra accounts. Untick some, then try again.`);
    return "failed";
  }
  try {
    await dependencies.save(change);
    return "changed";
  } catch (error) {
    reportSettingsWriteError(ui, error, "the Status Bar settings");
    return "failed";
  }
}
