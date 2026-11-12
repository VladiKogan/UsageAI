import assert from "node:assert/strict";
import test from "node:test";
import { unitKey, type AccountEntry } from "../src/accounts";
import {
  chooseStatusBarItems,
  resolveStatusBarAccounts,
  statusBarChange,
  statusBarPickItems,
  type StatusBarPickerDependencies,
  type StatusBarPickItem,
  type StatusBarPickSeparator,
} from "../src/status-bar-picker";

const account = (overrides: Partial<AccountEntry> & Pick<AccountEntry, "provider" | "id">): AccountEntry => ({
  unitKey: unitKey(overrides.provider, overrides.id),
  path: `/home/dev/.${overrides.provider}-${overrides.id}`,
  source: overrides.id === "default" ? "default" : "found",
  identity: {},
  label: overrides.id,
  hidden: false,
  ...overrides,
});

const names = { codex: "Codex", claude: "Claude Code", copilot: "GitHub Copilot", gemini: "Gemini" } as const;
const mainClaude = account({ provider: "claude", id: "default", label: "me@example.com", identity: { email: "me@example.com" } });
const team = account({ provider: "claude", id: "1a2b3c4d", label: "Team", identity: { email: "team@example.com" } });
const lab = account({ provider: "claude", id: "0a0b0c0d", label: "lab@example.com", identity: { email: "lab@example.com" } });
const mainCodex = account({ provider: "codex", id: "default", label: ".codex" });
const accounts = [mainClaude, team, lab, mainCodex];

const choices = (items: readonly (StatusBarPickItem | StatusBarPickSeparator)[]): StatusBarPickItem[] =>
  items.filter((item): item is StatusBarPickItem => "unit" in item);

test("the Status Bar picker lists every account with a checkbox, ticked from what is shown", () => {
  const items = statusBarPickItems({
    providers: () => ["codex", "claude", "copilot"],
    accounts: () => accounts,
    providerName: (provider) => names[provider],
    selection: () => ["claude", "claude@1a2b3c4d", "copilot"],
    hottestWhenNoneSelected: () => false,
  });
  assert.deepEqual(items.map((item) => "separator" in item ? `-- ${item.separator}` : [item.label, item.description ?? "", item.picked, item.unit]), [
    // A provider with one login shows just its name; with several, the main one is called out.
    ["Codex", ".codex", false, "codex"],
    ["Claude Code", "main login · me@example.com", true, "claude"],
    ["Claude Code · Team", "team@example.com · claude@1a2b3c4d", true, "claude@1a2b3c4d"],
    ["Claude Code · lab@example.com", "claude@0a0b0c0d", false, "claude@0a0b0c0d"],
    ["GitHub Copilot", "", true, "copilot"],
    "-- When nothing above is chosen",
    ["Show the account with the highest usage", "", false, "hottest"],
  ]);
  // Before the accounts are read, Claude Code and Codex still get their main-login checkbox.
  const early = statusBarPickItems({
    providers: () => ["claude"],
    accounts: () => [],
    providerName: (provider) => names[provider],
    selection: () => ["claude"],
    hottestWhenNoneSelected: () => true,
  });
  assert.deepEqual(choices(early).map((item) => [item.label, item.picked, item.unit]), [
    ["Claude Code", true, "claude"],
    ["Show the account with the highest usage", true, "hottest"],
  ]);
  // A hidden or signed-out main login whose checkbox is on stays listed, so it can be unticked.
  const mainGone = statusBarPickItems({
    providers: () => ["claude"],
    accounts: () => [team],
    providerName: (provider) => names[provider],
    selection: () => ["claude"],
    hottestWhenNoneSelected: () => false,
  });
  assert.deepEqual(choices(mainGone).map((item) => [item.label, item.description ?? "", item.unit]), [
    ["Claude Code", "main login", "claude"],
    ["Claude Code · Team", "team@example.com · claude@1a2b3c4d", "claude@1a2b3c4d"],
    ["Show the account with the highest usage", "", "hottest"],
  ]);
});

test("the Status Bar picker writes main logins as checkboxes and keeps what the user wrote", () => {
  const items = statusBarPickItems({
    providers: () => ["codex", "claude"],
    accounts: () => accounts,
    providerName: (provider) => names[provider],
    selection: () => ["claude@1a2b3c4d"],
    hottestWhenNoneSelected: () => true,
  });
  const pick = (...units: string[]) => choices(items).filter((item) => units.includes(item.unit));
  // "team" stays as the user wrote it; an unknown name and an account of a provider not listed are
  // kept, since the user could not untick them; a new choice is added by its id.
  const configured = ["team", "nobody", "codex@5e6f7a8b", 4, "TEAM"];
  assert.deepEqual(statusBarChange(items, pick("claude", "claude@1a2b3c4d", "claude@0a0b0c0d", "hottest"), configured, accounts), {
    providers: { codex: false, claude: true },
    hottestWhenNoneSelected: true,
    accounts: ["team", "nobody", "codex@5e6f7a8b", "claude@0a0b0c0d"],
  });
  // Unticking an account drops every name for it.
  assert.deepEqual(statusBarChange(items, pick("codex"), configured, accounts), {
    providers: { codex: true, claude: false },
    hottestWhenNoneSelected: false,
    accounts: ["nobody", "codex@5e6f7a8b"],
  });
  assert.deepEqual(statusBarChange(items, [], undefined, accounts).accounts, []);
  // A name for the main login is left to its checkbox, so unticking it hides the main login; a
  // name nothing resolves is kept once.
  assert.deepEqual(statusBarChange(items, pick("codex"), ["me@example.com", "nobody", "nobody"], accounts), {
    providers: { codex: true, claude: false },
    hottestWhenNoneSelected: false,
    accounts: ["nobody"],
  });
  assert.deepEqual(resolveStatusBarAccounts(["lab@example.com", " claude@1a2b3c4d "], accounts), ["claude@0a0b0c0d", "claude@1a2b3c4d"]);
});

test("choosing Status Bar items saves once, and a refused save offers a reload", async () => {
  const saved: unknown[] = [];
  const log: string[] = [];
  const dependencies = (pickUnits: string[] | undefined, save?: StatusBarPickerDependencies["save"]): StatusBarPickerDependencies => ({
    ui: {
      async pickMany(items, title, placeHolder) {
        log.push(`${title}: ${placeHolder}`);
        return pickUnits ? choices(items).filter((item) => pickUnits.includes(item.unit)) : undefined;
      },
      error(message) { log.push(`error ${message}`); },
      offerReload(message) { log.push(`reload ${message}`); },
    },
    providers: () => ["claude", "gemini"],
    accounts: () => accounts,
    providerName: (provider) => names[provider],
    selection: () => ["hottest"],
    hottestWhenNoneSelected: () => true,
    configuredAccounts: () => [],
    save: save ?? (async (change) => { saved.push(change); }),
  });

  assert.equal(await chooseStatusBarItems(dependencies(undefined)), "cancelled");
  assert.deepEqual(saved, []);
  assert.equal(await chooseStatusBarItems(dependencies(["claude@1a2b3c4d", "gemini"])), "changed");
  assert.deepEqual(saved, [{ providers: { claude: false, gemini: true }, hottestWhenNoneSelected: false, accounts: ["claude@1a2b3c4d"] }]);
  assert.equal(log[0], "Status Bar: Choose the accounts to show in the Status Bar");

  log.length = 0;
  const unregistered = async () => {
    throw new Error("Unable to write to User Settings because usageai.statusBarProviders.accounts is not a registered configuration.");
  };
  assert.equal(await chooseStatusBarItems(dependencies(["claude"], unregistered)), "failed");
  assert.match(log.at(-1) ?? "", /^reload VS Code has not loaded/);
  assert.equal(await chooseStatusBarItems(dependencies(["claude"], async () => { throw new Error("read-only"); })), "failed");
  assert.equal(log.at(-1), "error UsageAI could not save the Status Bar settings: read-only");

  // A choice that would name more accounts than the setting holds is refused, never cut short.
  saved.length = 0;
  const full = Array.from({ length: 16 }, (_, index) => `old${index}`);
  assert.equal(await chooseStatusBarItems({ ...dependencies(["claude@1a2b3c4d"]), configuredAccounts: () => full }), "failed");
  assert.match(log.at(-1) ?? "", /^error The Status Bar can show at most 16 extra accounts/);
  assert.deepEqual(saved, []);
});
