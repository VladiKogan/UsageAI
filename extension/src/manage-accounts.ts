import * as path from "node:path";
import type { AccountDecision, ManagedAccount } from "./account-service";
import {
  changeAccountSetting,
  cleanLabel,
  exceedsConfiguredAccounts,
  hasAccountSetting,
  maxConfiguredAccounts,
  type AccountProviderId,
  type AccountSettingChange,
} from "./accounts";
import { saveAccountSettings, type AddAccountUi } from "./add-account";

/** The editor surfaces Manage Accounts uses, so the flow itself needs no VS Code host. */
export interface ManageAccountsUi extends Pick<AddAccountUi, "pick" | "input" | "info" | "error" | "offerReload" | "copy"> {
  /** A modal question with one action button; true when that button was chosen. */
  confirm(message: string, action: string): PromiseLike<boolean>;
  /**
   * Shows the folder in the system file manager. Left out where that manager is on another
   * computer: UsageAI running on the remote side of a remote window.
   */
  readonly reveal?: (folder: string) => PromiseLike<unknown> | void;
}

export interface ManageAccountsDependencies {
  readonly ui: ManageAccountsUi;
  /** The provider's card name, "Claude Code" or "Codex", from its usage client. */
  readonly providerName: (provider: AccountProviderId) => string;
  /** Every login UsageAI knows about, shown or not. */
  readonly catalog: () => readonly ManagedAccount[];
  /** The raw `usageai.accounts` value. Saving it re-reads the accounts through the change event. */
  readonly configuredAccounts: () => unknown;
  readonly saveConfiguredAccounts: (value: readonly unknown[]) => PromiseLike<void>;
  /** What the user answered for a found login. */
  readonly decisionFor: (id: string) => AccountDecision;
  /** Shows, ignores, or (undefined) forgets the answer for a found login. */
  readonly answer: (id: string, decision: AccountDecision) => PromiseLike<void>;
  readonly folderExists: (folder: string) => PromiseLike<boolean>;
  readonly addAccount: () => PromiseLike<unknown>;
}

export type ManageAccountsResult = "changed" | "unchanged" | "cancelled" | "failed";

const addAccountItem = "$(add) Add Account…";

function statusText(managed: ManagedAccount): string {
  switch (managed.status) {
    case "shown":
      return managed.account.source === "default" ? "main login" : "";
    case "hidden":
      return "hidden";
    case "pending":
      return "found, waiting for Show or Ignore";
    case "ignored":
      return "ignored";
    case "duplicate":
      return `same login as ${managed.shownAs ?? "another account"}`;
    case "unavailable":
      return "unavailable";
  }
}

type Action = "rename" | "hide" | "show" | "remove" | "reveal" | "copy";

function actionsFor(managed: ManagedAccount, canReveal: boolean): Array<{ readonly label: string; readonly detail?: string; readonly action: Action }> {
  const { account, status } = managed;
  const actions: Array<{ label: string; detail?: string; action: Action }> = [];
  if (status === "shown" || status === "hidden") {
    actions.push({ label: "Rename…", detail: "The name on its card. Without one, its email or folder name is used.", action: "rename" });
  }
  if (status === "hidden" || status === "pending" || status === "ignored") {
    actions.push({ label: "Show", detail: "Show it on a card of its own.", action: "show" });
  }
  if (status === "shown" || status === "pending") {
    actions.push({ label: "Hide", detail: "Keep it out of the dashboard and Status Bar. Its folder is left as it is.", action: "hide" });
  }
  if (account.source === "added") {
    actions.push({
      label: "Remove",
      detail: status === "unavailable"
        ? "Take this entry out of usageai.accounts."
        : managed.found
          ? "Forget its name and stop showing it. Its login folder stays on disk."
          : "Take it out of usageai.accounts. Its login folder stays on disk.",
      action: "remove",
    });
  }
  if (canReveal) {
    actions.push({ label: "Open Folder", detail: account.path, action: "reveal" });
  }
  actions.push({ label: "Copy Folder Path", action: "copy" });
  return actions;
}

/**
 * `UsageAI: Manage Accounts…`: pick any Claude Code or Codex login, shown or not, then rename,
 * hide, show or remove it, or open its folder. A login folder is never deleted or written.
 */
export async function manageAccounts(dependencies: ManageAccountsDependencies): Promise<ManageAccountsResult> {
  const { ui } = dependencies;
  const catalog = dependencies.catalog();
  const items = [
    ...catalog.map((managed) => {
      const status = statusText(managed);
      const email = managed.account.identity.email;
      const where = managed.problem ? `${managed.account.path} — ${managed.problem}` : managed.account.path;
      return {
        label: managed.account.label,
        description: [dependencies.providerName(managed.account.provider), status].filter(Boolean).join(" · "),
        detail: email && email !== managed.account.label ? `${email} — ${where}` : where,
        managed,
      };
    }),
    { label: addAccountItem, description: "", detail: "Sign in to another account, or add a login folder you already have.", managed: undefined },
  ];
  const picked = await ui.pick(items, "Manage Accounts", catalog.length > 0 ? "Choose an account" : "No Claude Code or Codex logins yet");
  if (!picked) {
    return "cancelled";
  }
  if (!picked.managed) {
    await dependencies.addAccount();
    return "unchanged";
  }
  const managed = picked.managed;
  const { account } = managed;
  const title = `${dependencies.providerName(account.provider)} · ${account.label}`;
  const canReveal = ui.reveal !== undefined && await dependencies.folderExists(account.path);
  const chosen = await ui.pick(actionsFor(managed, canReveal), title, "What should UsageAI do with this account?");
  if (!chosen) {
    return "cancelled";
  }

  /**
   * Writes the changed entry. A found folder whose entry goes away falls back to being a found
   * login, so it is answered with Show first or the next read would hold it back as new.
   */
  const save = async (change: AccountSettingChange): Promise<boolean> => {
    const current = dependencies.configuredAccounts();
    const next = changeAccountSetting(current, account.provider, account.path, change);
    if (exceedsConfiguredAccounts(current, next)) {
      ui.error(`usageai.accounts already lists ${maxConfiguredAccounts} folders. Remove one, then try again.`);
      return false;
    }
    const dropsFoundEntry = managed.found && change !== "remove"
      && hasAccountSetting(current, account.provider, account.path)
      && !hasAccountSetting(next, account.provider, account.path);
    const before = dependencies.decisionFor(account.id);
    if (dropsFoundEntry) {
      await dependencies.answer(account.id, "show");
    }
    if (await saveAccountSettings(ui, dependencies.saveConfiguredAccounts, next)) {
      return true;
    }
    if (dropsFoundEntry) {
      await dependencies.answer(account.id, before);
    }
    return false;
  };
  // The main login, and a found folder, need no entry once it says nothing.
  const dropWhenPlain = account.source === "default" || managed.found;

  switch (chosen.action) {
    case "rename": {
      const name = await ui.input({
        title: `Rename ${title}`,
        prompt: "The name shown on the account's card. Leave it empty to use its email or folder name.",
        value: account.userLabel ?? "",
        placeHolder: account.identity.email ?? path.basename(account.path),
      });
      if (name === undefined) {
        return "cancelled";
      }
      if ((cleanLabel(name) ?? "") === (account.userLabel ?? "")) {
        return "unchanged";
      }
      return await save({ label: name, dropWhenPlain }) ? "changed" : "failed";
    }
    case "hide":
      if (account.source === "found") {
        await dependencies.answer(account.id, "ignore");
        return "changed";
      }
      return await save({ hidden: true, dropWhenPlain }) ? "changed" : "failed";
    case "show":
      if (managed.status === "pending" || managed.status === "ignored") {
        await dependencies.answer(account.id, "show");
        return "changed";
      }
      return await save({ hidden: false, dropWhenPlain }) ? "changed" : "failed";
    case "remove": {
      const question = managed.status === "unavailable"
        ? `Remove ${title} from usageai.accounts?`
        : `Remove ${title} from UsageAI? Its login folder stays on disk, and Add Account brings it back.`;
      if (!await ui.confirm(question, "Remove")) {
        return "cancelled";
      }
      // A folder the home listing finds would come straight back as a found login, so it is
      // ignored before its entry goes, never after: in between it would be shown again.
      const before = dependencies.decisionFor(account.id);
      if (managed.found) {
        await dependencies.answer(account.id, "ignore");
      }
      if (await save("remove")) {
        return "changed";
      }
      if (managed.found) {
        await dependencies.answer(account.id, before);
      }
      return "failed";
    }
    case "reveal":
      await ui.reveal?.(account.path);
      return "unchanged";
    case "copy":
      await ui.copy(account.path);
      void ui.info(`Copied ${account.path}`);
      return "unchanged";
  }
}
