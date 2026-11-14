import { isAccountUnitKey, isKnownUnitKey } from "./accounts";
import { statusBarLimitChoices, selectStatusBarLimits, type ProviderState } from "./model";

/**
 * Which limits each Status Bar item shows, keyed by unit key (`claude`, `claude@1a2b3c4d`): a
 * model group, or for a provider without groups one of its weekly limits. The desktop keeps the
 * same choice per account as `PopupMetricGroups`, for its popup rings and tray icon.
 */
export const statusBarLimitsKey = "usageai.statusBar.limits.v1";

const maxEntries = 32;
const maxChoiceLength = 128;
const providerIds: readonly string[] = ["codex", "claude", "copilot", "gemini"];

/** The saved choices, keeping only usable entries, as the desktop's `AppSettings.Validate` does. */
export function sanitizeStatusBarLimits(value: unknown): Record<string, string> {
  const result: Record<string, string> = {};
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return result;
  }
  for (const [key, choice] of Object.entries(value as Record<string, unknown>)) {
    const unit = key.trim().toLowerCase();
    if (Object.keys(result).length >= maxEntries || !isKnownUnitKey(unit, providerIds) || typeof choice !== "string") {
      continue;
    }
    const trimmed = choice.trim();
    if (trimmed.length > 0 && trimmed.length <= maxChoiceLength) {
      result[unit] = trimmed;
    }
  }
  return result;
}

/** The choice saved for one Status Bar item, or undefined for automatic. */
export function statusBarLimitFor(choices: Readonly<Record<string, string>>, unit: string): string | undefined {
  return choices[unit.trim().toLowerCase()];
}

/**
 * The choices without those of accounts that no longer exist, so a forgotten account's choice
 * cannot fill the cap and push a new one out. A provider's own choice is always kept.
 */
export function pruneStatusBarLimits(
  choices: Readonly<Record<string, string>>,
  knownAccounts: readonly string[],
): Record<string, string> {
  const known = new Set(knownAccounts.map((unit) => unit.trim().toLowerCase()));
  return Object.fromEntries(Object.entries(choices).filter(([unit]) => !isAccountUnitKey(unit) || known.has(unit)));
}

/** The choices with one item pinned to `choice`, or returned to automatic when it is undefined. */
export function withStatusBarLimit(
  choices: Readonly<Record<string, string>>,
  unit: string,
  choice: string | undefined,
): Record<string, string> {
  const key = unit.trim().toLowerCase();
  const next = { ...choices };
  if (choice?.trim()) {
    next[key] = choice.trim();
  } else {
    delete next[key];
  }
  return next;
}

export const automaticLimitLabel = "Closest to its limit";

export interface StatusBarLimitItem {
  readonly label: string;
  readonly description?: string;
  readonly detail?: string;
  /** The unit key when choosing a provider; the pinned choice, or undefined for automatic. */
  readonly value: string | undefined;
}

export interface StatusBarLimitsUi {
  pick(items: readonly StatusBarLimitItem[], title: string, placeHolder: string): PromiseLike<StatusBarLimitItem | undefined>;
  info(message: string): void;
  error(message: string): void;
}

export interface StatusBarLimitsDependencies {
  readonly ui: StatusBarLimitsUi;
  /** The latest readings, in Status Bar order. */
  readonly states: () => readonly ProviderState[];
  readonly choices: () => Readonly<Record<string, string>>;
  /** Every account still known, shown, hidden or waiting, by unit key; others' choices are dropped. */
  readonly knownAccounts: () => readonly string[];
  readonly save: (choices: Record<string, string>) => PromiseLike<void>;
}

export type StatusBarLimitsResult = "changed" | "cancelled" | "failed" | "none";

/** The providers and accounts with something to choose, from their last reading. */
export function statusBarLimitCandidates(states: readonly ProviderState[]): ProviderState[] {
  return states.filter((state) => state.snapshot && statusBarLimitChoices(state.snapshot.metrics).length > 0);
}

/** The choices for one item: automatic first, then each group or weekly limit with its reading. */
export function statusBarLimitItems(state: ProviderState, saved: string | undefined): StatusBarLimitItem[] {
  const metrics = state.snapshot?.metrics ?? [];
  const automatic = selectStatusBarLimits(metrics);
  const current = selectStatusBarLimits(metrics, saved).pinned;
  const shown = (choice: string | undefined) => selectStatusBarLimits(metrics, choice).metrics
    .map((metric) => `${metric.name} ${metric.usedPercent ?? 0}%`)
    .join(" · ");
  const items: StatusBarLimitItem[] = [{
    label: current === undefined ? `$(check) ${automaticLimitLabel}` : automaticLimitLabel,
    description: automatic.group ?? automatic.metrics.find((metric) => metric.kind === "rolling")?.name ?? "",
    detail: shown(undefined),
    value: undefined,
  }];
  for (const choice of automatic.choices) {
    items.push({
      label: current !== undefined && current.toLowerCase() === choice.toLowerCase() ? `$(check) ${choice}` : choice,
      detail: shown(choice),
      value: choice,
    });
  }
  return items;
}

/**
 * `UsageAI: Choose Status Bar Limits…`: which model group (Gemini) or weekly limit (Claude) an item
 * shows, or the one closest to its limit. With `unit`, as from an item's tooltip, it skips straight
 * to that item's choices.
 */
export async function chooseStatusBarLimits(
  dependencies: StatusBarLimitsDependencies,
  unit?: string,
): Promise<StatusBarLimitsResult> {
  const { ui } = dependencies;
  const candidates = statusBarLimitCandidates(dependencies.states());
  let state = typeof unit === "string"
    ? candidates.find((candidate) => candidate.id.toLowerCase() === unit.trim().toLowerCase())
    : undefined;
  if (!state) {
    if (candidates.length === 0) {
      ui.info("No provider reports more than one model group or weekly limit yet, so there is nothing to choose.");
      return "none";
    }
    if (candidates.length === 1) {
      state = candidates[0];
    } else {
      const saved = dependencies.choices();
      const chosen = await ui.pick(
        candidates.map((candidate) => ({
          label: candidate.displayName,
          description: statusBarLimitFor(saved, candidate.id) ?? automaticLimitLabel,
          value: candidate.id,
        })),
        "Status Bar Limits",
        "Choose the provider or account whose limits to set",
      );
      state = chosen ? candidates.find((candidate) => candidate.id === chosen.value) : undefined;
    }
  }
  if (!state) {
    return "cancelled";
  }

  const saved = dependencies.choices();
  const chosen = await ui.pick(
    statusBarLimitItems(state, statusBarLimitFor(saved, state.id)),
    `Status Bar Limits: ${state.displayName}`,
    state.snapshot && selectStatusBarLimits(state.snapshot.metrics).group
      ? "Choose which model group's limits the Status Bar shows"
      : "Choose which weekly limit the Status Bar shows",
  );
  if (!chosen) {
    return "cancelled";
  }
  try {
    await dependencies.save(pruneStatusBarLimits(withStatusBarLimit(saved, state.id, chosen.value), dependencies.knownAccounts()));
    return "changed";
  } catch (error) {
    ui.error(`UsageAI could not save the Status Bar limits: ${error instanceof Error ? error.message : String(error)}`);
    return "failed";
  }
}
