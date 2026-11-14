import assert from "node:assert/strict";
import test from "node:test";
import {
  metricGroups,
  selectStatusBarLimits,
  splitMetricName,
  statusBarLimitChoices,
  statusBarMetricLabel,
  weeklyLimits,
  weekMinutes,
  type ProviderState,
  type UsageMetric,
  type UsageMetricKind,
} from "../src/model";
import {
  chooseStatusBarLimits,
  sanitizeStatusBarLimits,
  statusBarLimitCandidates,
  statusBarLimitFor,
  statusBarLimitItems,
  pruneStatusBarLimits,
  withStatusBarLimit,
  type StatusBarLimitItem,
  type StatusBarLimitsDependencies,
} from "../src/status-bar-limits";

const metric = (name: string, kind: UsageMetricKind, usedPercent: number | null): UsageMetric => ({ name, kind, usedPercent });
const names = (metrics: readonly UsageMetric[]) => metrics.map((entry) => entry.name).join("|");

const gemini = [
  metric("Gemini Models (5-hour)", "session", 14),
  metric("Gemini Models (Weekly)", "rolling", 48),
  metric("Claude and GPT models (5-hour)", "session", 94),
  metric("Claude and GPT models (Weekly)", "rolling", 83),
];
const weekly = (name: string, usedPercent: number): UsageMetric => ({ name, kind: "rolling", usedPercent, durationMinutes: weekMinutes });
const claude = [
  metric("5-hour", "session", 30),
  weekly("Weekly", 40),
  weekly("Weekly Fable", 70),
  { name: "Fable reset", kind: "balance", usedPercent: null, remainingText: "1" } satisfies UsageMetric,
];

const state = (id: string, displayName: string, metrics: readonly UsageMetric[] | undefined): ProviderState => ({
  id,
  displayName,
  signInCommand: "",
  accountUrl: "",
  stale: false,
  refreshing: false,
  ...(metrics ? { snapshot: { plan: "Pro", metrics, fetchedAt: new Date(0).toISOString(), providerId: id, providerName: displayName } } : {}),
});

test("status bar limits split names into a model group and window, as the desktop does", () => {
  assert.deepEqual(splitMetricName("Claude and GPT models (Weekly)"), { group: "Claude and GPT models", window: "Weekly" });
  assert.deepEqual(splitMetricName(" Gemini Models (Limit 1) "), { group: "Gemini Models", window: "Limit 1" });
  assert.deepEqual(splitMetricName("Weekly"), { window: "Weekly" });
  assert.deepEqual(splitMetricName("(5-hour)"), { window: "(5-hour)" });
  assert.deepEqual(splitMetricName("Odd ()"), { window: "Odd ()" });
  assert.equal(statusBarMetricLabel(metric("Claude and GPT models (Weekly)", "rolling", 1)), "Weekly");
});

test("status bar limits pick one Gemini model group, busiest or pinned, shortest window first", () => {
  assert.deepEqual(metricGroups(gemini), ["Gemini Models", "Claude and GPT models"]);
  assert.deepEqual(statusBarLimitChoices(gemini), ["Gemini Models", "Claude and GPT models"]);
  assert.deepEqual(weeklyLimits(gemini), []);

  const automatic = selectStatusBarLimits(gemini);
  assert.equal(automatic.group, "Claude and GPT models");
  assert.equal(automatic.pinned, undefined);
  assert.equal(names(automatic.metrics), "Claude and GPT models (5-hour)|Claude and GPT models (Weekly)");

  // A pinned group wins, matched without regard to case or stray spaces.
  const pinned = selectStatusBarLimits(gemini, "  gemini models ");
  assert.equal(pinned.group, "Gemini Models");
  assert.equal(pinned.pinned, "Gemini Models");
  assert.equal(names(pinned.metrics), "Gemini Models (5-hour)|Gemini Models (Weekly)");

  // A group the provider stopped reporting, or a weekly-limit name, falls back to automatic.
  assert.equal(selectStatusBarLimits(gemini, "Retired models").pinned, undefined);
  assert.equal(selectStatusBarLimits(gemini, "Claude and GPT models (Weekly)").group, "Claude and GPT models");

  // One group is no choice: the older "(Limit n)" names all share a group.
  const legacy = [metric("Gemini Models (Limit 1)", "rolling", 10), metric("Gemini Models (Limit 2)", "rolling", 60)];
  assert.deepEqual(metricGroups(legacy), []);
  const legacyLimits = selectStatusBarLimits(legacy);
  assert.equal(legacyLimits.group, undefined);
  assert.equal(names(legacyLimits.metrics), "Gemini Models (Limit 2)");
});

test("status bar limits pick Claude's weekly limit, busiest or pinned", () => {
  assert.deepEqual(weeklyLimits(claude), ["Weekly", "Weekly Fable"]);
  assert.deepEqual(statusBarLimitChoices(claude), ["Weekly", "Weekly Fable"]);
  assert.equal(names(selectStatusBarLimits(claude).metrics), "5-hour|Weekly Fable");
  assert.equal(names(selectStatusBarLimits(claude, " weekly ").metrics), "5-hour|Weekly");
  assert.equal(selectStatusBarLimits(claude, "weekly").pinned, "Weekly");
  assert.equal(names(selectStatusBarLimits(claude, "Weekly Fable").metrics), "5-hour|Weekly Fable");
  assert.equal(selectStatusBarLimits(claude, "Weekly").group, undefined);
  assert.equal(names(selectStatusBarLimits(claude, "Weekly Opus").metrics), "5-hour|Weekly Fable");
  assert.equal(names(selectStatusBarLimits(claude, "Gemini Models").metrics), "5-hour|Weekly Fable");

  // Only week-long limits are a weekly choice: the Gemini CLI's daily per-model quotas are not.
  const daily: UsageMetric[] = [
    { name: "Gemini Pro", kind: "rolling", usedPercent: 30, durationMinutes: 1_440 },
    { name: "Gemini Flash", kind: "rolling", usedPercent: 60, durationMinutes: 1_440 },
  ];
  assert.deepEqual(statusBarLimitChoices(daily), []);
  assert.equal(names(selectStatusBarLimits(daily, "Gemini Pro").metrics), "Gemini Flash");

  // Without groups or several weekly limits there is nothing to choose; balances and unlimited
  // allowances never show, and other metered limits still do, busiest first.
  const mixed = [
    { name: "Credits", kind: "balance", usedPercent: null, remainingText: "$7" } satisfies UsageMetric,
    metric("Monthly", "monthly", 99),
    metric("Weekly", "rolling", 20),
    metric("Session low", "session", 5),
    metric("Session high", "session", 50),
    { name: "Chat", kind: "monthly", usedPercent: 0, isUnlimited: true } satisfies UsageMetric,
  ];
  assert.deepEqual(statusBarLimitChoices(mixed), []);
  assert.equal(names(selectStatusBarLimits(mixed).metrics), "Session high|Weekly");
  const pools = [metric("Pool A", "balance", 10), metric("Pool B", "balance", 70), metric("Pool C", "balance", 40)];
  assert.equal(names(selectStatusBarLimits(pools).metrics), "Pool B|Pool C");
  assert.deepEqual(selectStatusBarLimits([], "x").metrics, []);
});

test("saved status bar limits keep only usable entries, keyed by unit", () => {
  assert.deepEqual(sanitizeStatusBarLimits(undefined), {});
  assert.deepEqual(sanitizeStatusBarLimits(["Weekly"]), {});
  assert.deepEqual(sanitizeStatusBarLimits({
    GEMINI: " Gemini Models ",
    "claude@1a2b3c4d": "Weekly Fable",
    claude: "",
    copilot: 3,
    "gemini@1a2b3c4d": "x",
    "   ": "x",
    codex: "x".repeat(129),
  }), { gemini: "Gemini Models", "claude@1a2b3c4d": "Weekly Fable" });
  const many = Object.fromEntries(Array.from({ length: 40 }, () => ["claude", "Weekly"]));
  assert.equal(Object.keys(sanitizeStatusBarLimits(many)).length, 1);

  const saved = withStatusBarLimit({}, "Claude@1a2b3c4d", " Weekly ");
  assert.deepEqual(saved, { "claude@1a2b3c4d": "Weekly" });
  assert.equal(statusBarLimitFor(saved, "claude@1a2b3c4d"), "Weekly");
  assert.deepEqual(withStatusBarLimit(saved, "claude@1a2b3c4d", undefined), {});
  assert.deepEqual(withStatusBarLimit(saved, "claude@1a2b3c4d", "  "), {});

  // A forgotten account's choice is dropped on the next save; a provider's own never is.
  assert.deepEqual(
    pruneStatusBarLimits({ gemini: "Gemini Models", claude: "Weekly", "claude@1a2b3c4d": "Weekly", "codex@0a0b0c0d": "Weekly" }, ["Claude@1a2b3c4d"]),
    { gemini: "Gemini Models", claude: "Weekly", "claude@1a2b3c4d": "Weekly" },
  );
});

test("choosing status bar limits offers each choice with its readings and saves one per account", async () => {
  const states = [
    state("codex", "Codex", [metric("5-hour", "session", 10), metric("Weekly", "rolling", 20)]),
    state("claude", "Claude Code", claude),
    state("claude@1a2b3c4d", "Claude Code · team", claude),
    state("gemini", "Google Gemini", gemini),
    state("copilot", "GitHub Copilot", undefined),
  ];
  assert.deepEqual(statusBarLimitCandidates(states).map((candidate) => candidate.id), ["claude", "claude@1a2b3c4d", "gemini"]);

  // Automatic first and ticked, naming what it shows now; each choice says what it would show.
  const items = statusBarLimitItems(states[3]!, undefined);
  assert.deepEqual(items.map((item) => item.label), ["$(check) Closest to its limit", "Gemini Models", "Claude and GPT models"]);
  assert.equal(items[0]!.description, "Claude and GPT models");
  assert.equal(items[1]!.detail, "Gemini Models (5-hour) 14% · Gemini Models (Weekly) 48%");
  assert.deepEqual(statusBarLimitItems(states[1]!, "weekly").map((item) => item.label), ["Closest to its limit", "$(check) Weekly", "Weekly Fable"]);
  assert.equal(statusBarLimitItems(states[1]!, undefined)[0]!.description, "Weekly Fable");

  let saved: Record<string, string> = { gemini: "Gemini Models", "claude@0f0f0f0f": "Weekly" };
  const asked: string[] = [];
  const answers: Array<(items: readonly StatusBarLimitItem[]) => StatusBarLimitItem | undefined> = [];
  const info: string[] = [];
  const errors: string[] = [];
  let failSave = false;
  const dependencies = (shown: readonly ProviderState[]): StatusBarLimitsDependencies => ({
    ui: {
      pick: async (offered, title) => {
        asked.push(`${title}: ${offered.map((item) => `${item.label}${item.description ? ` (${item.description})` : ""}`).join(", ")}`);
        return answers.shift()?.(offered);
      },
      info: (message) => info.push(message),
      error: (message) => errors.push(message),
    },
    states: () => shown,
    choices: () => saved,
    knownAccounts: () => ["claude@1a2b3c4d"],
    save: async (next) => {
      if (failSave) {
        throw new Error("read-only");
      }
      saved = next;
    },
  });

  // Several candidates: the account first, then its limits.
  answers.push((offered) => offered.find((item) => item.value === "claude@1a2b3c4d"));
  answers.push((offered) => offered.find((item) => item.value === "Weekly"));
  assert.equal(await chooseStatusBarLimits(dependencies(states)), "changed");
  assert.deepEqual(saved, { gemini: "Gemini Models", "claude@1a2b3c4d": "Weekly" });
  assert.equal(asked[0], "Status Bar Limits: Claude Code (Closest to its limit), Claude Code · team (Closest to its limit), Google Gemini (Gemini Models)");
  assert.match(asked[1] ?? "", /^Status Bar Limits: Claude Code · team: /);

  // From an item's tooltip the account is known; choosing automatic clears the pin.
  asked.length = 0;
  answers.push((offered) => offered[0]);
  assert.equal(await chooseStatusBarLimits(dependencies(states), "GEMINI"), "changed");
  assert.equal(asked.length, 1);
  assert.deepEqual(saved, { "claude@1a2b3c4d": "Weekly" });

  // One candidate skips the first list; dismissing either list saves nothing.
  asked.length = 0;
  assert.equal(await chooseStatusBarLimits(dependencies([states[3]!])), "cancelled");
  assert.equal(asked.length, 1);
  assert.equal(await chooseStatusBarLimits(dependencies(states)), "cancelled");
  assert.deepEqual(saved, { "claude@1a2b3c4d": "Weekly" });

  // Nothing to choose says so; a refused save is reported, not thrown.
  assert.equal(await chooseStatusBarLimits(dependencies([states[0]!, states[4]!])), "none");
  assert.match(info[0] ?? "", /^No provider reports more than one model group or weekly limit/);
  failSave = true;
  answers.push((offered) => offered[1]);
  assert.equal(await chooseStatusBarLimits(dependencies([states[3]!])), "failed");
  assert.match(errors[0] ?? "", /read-only/);
});
