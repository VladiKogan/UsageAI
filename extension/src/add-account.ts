import { mkdir, readdir, rmdir, stat } from "node:fs/promises";
import * as path from "node:path";
import {
  accountIdForPath,
  accountPathProblem,
  cleanLabel,
  commandWithEnvironment,
  configDirectoryVariable,
  credentialFileName,
  identityKey,
  proposeLoginFolder,
  providerTitle,
  readClaudeIdentity,
  readCodexIdentity,
  type AccountEntry,
  type AccountProviderId,
} from "./accounts";

/** The editor surfaces the Add Account flow uses, so the flow itself needs no VS Code host. */
export interface AddAccountUi {
  pick<T extends { readonly label: string }>(items: readonly T[], title: string, placeHolder: string): PromiseLike<T | undefined>;
  input(options: {
    readonly title: string;
    readonly prompt: string;
    readonly value?: string;
    readonly placeHolder?: string;
    readonly validate?: (value: string) => PromiseLike<string | undefined> | string | undefined;
  }): PromiseLike<string | undefined>;
  pickFolder(title: string): PromiseLike<string | undefined>;
  info(message: string, ...actions: string[]): PromiseLike<string | undefined>;
  error(message: string): void;
  copy(text: string): PromiseLike<void>;
  /** Starts the CLI login in an editor terminal; `closed` settles when that terminal closes. */
  runLogin(options: {
    readonly name: string;
    readonly environment: Readonly<Record<string, string>>;
    readonly executable: string;
    readonly args: readonly string[];
  }): { readonly closed: PromiseLike<void> };
  /** A cancellable progress notification around `task`. */
  waitWithProgress<T>(title: string, task: (cancelled: () => boolean) => Promise<T>): PromiseLike<T>;
}

export interface LoginLaunch {
  readonly executable: string;
  readonly args: readonly string[];
}

export interface AddAccountFileSystem {
  /** "missing", "empty", "login" (holds the credential file) or "other" (holds something else). */
  folderState(provider: AccountProviderId, folder: string): Promise<"missing" | "empty" | "login" | "other">;
  hasCredentialFile(provider: AccountProviderId, folder: string): Promise<boolean>;
  exists(folder: string): Promise<boolean>;
  createFolder(folder: string): Promise<void>;
  /** Removes the folder only while it is still empty. */
  removeIfEmpty(folder: string): Promise<void>;
}

export const nodeAddAccountFileSystem: AddAccountFileSystem = {
  async folderState(provider, folder) {
    let entries: string[];
    try {
      entries = await readdir(folder);
    } catch (error) {
      return (error as NodeJS.ErrnoException).code === "ENOENT" ? "missing" : "other";
    }
    if (entries.length === 0) {
      return "empty";
    }
    return entries.includes(credentialFileName(provider)) ? "login" : "other";
  },
  async hasCredentialFile(provider, folder) {
    try {
      return (await stat(path.join(folder, credentialFileName(provider)))).isFile();
    } catch {
      return false;
    }
  },
  async exists(folder) {
    try {
      await stat(folder);
      return true;
    } catch {
      return false;
    }
  },
  async createFolder(folder) {
    await mkdir(folder, { recursive: true });
  },
  async removeIfEmpty(folder) {
    try {
      await rmdir(folder);
    } catch {
      // Not empty, or already gone: either way the folder is left as it is.
    }
  },
};

export interface NewAccountSetting {
  readonly provider: AccountProviderId;
  readonly path: string;
  readonly label?: string;
}

export interface AddAccountDependencies {
  readonly ui: AddAccountUi;
  readonly home: () => string;
  /** The accounts shown now, the default logins included. */
  readonly accounts: () => readonly AccountEntry[];
  /** The raw `usageai.accounts` value. */
  readonly configuredAccounts: () => unknown;
  readonly saveConfiguredAccounts: (value: readonly unknown[]) => PromiseLike<void>;
  /** The provider's official CLI, found the way the provider clients find it. */
  readonly findLogin: (provider: AccountProviderId) => Promise<LoginLaunch | undefined>;
  /** False when UsageAI runs on this computer in a remote window, where a terminal opens on the remote host. */
  readonly canRunLoginInTerminal?: () => boolean;
  /** Re-reads the accounts and refreshes the new one. */
  readonly afterAdd: (account: NewAccountSetting) => PromiseLike<void>;
  readonly fileSystem?: AddAccountFileSystem;
  readonly pollIntervalMs?: number;
  readonly timeoutMs?: number;
}

export type AddAccountResult = "added" | "duplicate" | "cancelled" | "failed";

const signInTimeoutMs = 120_000;

/** `UsageAI: Add Account…`: a provider, then a new sign-in or a login folder the user already has. */
export async function addAccount(dependencies: AddAccountDependencies): Promise<AddAccountResult> {
  const { ui } = dependencies;
  const provider = await ui.pick(
    [
      { label: "Claude Code", provider: "claude" as const },
      { label: "Codex", provider: "codex" as const },
    ],
    "Add Account",
    "Which provider is the account for?",
  );
  if (!provider) {
    return "cancelled";
  }
  const way = await ui.pick(
    [
      { label: "Sign in to another account", detail: "Runs the official CLI login into a new folder of its own.", way: "signIn" as const },
      { label: "Use a login folder I already have", detail: `A folder with a ${credentialFileName(provider.provider)} in it.`, way: "existing" as const },
    ],
    `Add ${providerTitle(provider.provider)} Account`,
    "How should UsageAI find the account?",
  );
  if (!way) {
    return "cancelled";
  }
  if (way.way === "signIn") {
    return signIn(dependencies, provider.provider);
  }
  const folder = await ui.pickFolder(`Choose a ${providerTitle(provider.provider)} login folder`);
  return folder ? useExistingFolder(dependencies, provider.provider, folder) : "cancelled";
}

function fileSystemOf(dependencies: AddAccountDependencies): AddAccountFileSystem {
  return dependencies.fileSystem ?? nodeAddAccountFileSystem;
}

async function signIn(dependencies: AddAccountDependencies, provider: AccountProviderId): Promise<AddAccountResult> {
  const { ui } = dependencies;
  const fileSystem = fileSystemOf(dependencies);
  const title = `Sign in to another ${providerTitle(provider)} account`;
  const name = await ui.input({ title, prompt: "A name for this account (optional), such as \"team\".", placeHolder: "team" });
  if (name === undefined) {
    return "cancelled";
  }
  const proposed = await proposeLoginFolder(provider, name, dependencies.home(), (folder) => fileSystem.exists(folder));
  const typed = await ui.input({
    title,
    prompt: `The folder the new login is saved in. Your own CLI can use it with ${configDirectoryVariable(provider)}.`,
    value: proposed,
    validate: async (value) => {
      const candidate = value.trim();
      const problem = candidate ? accountPathProblem(candidate) : "the path is missing";
      if (problem) {
        return `Choose another folder: ${problem}.`;
      }
      return await fileSystem.folderState(provider, path.resolve(candidate)) === "other"
        ? "The folder must be missing or empty."
        : undefined;
    },
  });
  if (typed === undefined) {
    return "cancelled";
  }
  const problem = accountPathProblem(typed.trim());
  if (problem) {
    ui.error(`UsageAI cannot sign in to ${typed.trim()}: ${problem}.`);
    return "failed";
  }
  const folder = path.resolve(typed.trim());
  const state = await fileSystem.folderState(provider, folder);
  if (state === "login") {
    // Never sign in over an existing login: offer to show the one already there.
    const choice = await ui.info(`${folder} already holds a ${providerTitle(provider)} login.`, "Use this login instead");
    return choice ? useExistingFolder(dependencies, provider, folder, name) : "cancelled";
  }
  if (state === "other") {
    ui.error(`UsageAI cannot sign in to ${folder}: the folder must be missing or empty.`);
    return "failed";
  }

  const launch = await dependencies.findLogin(provider);
  if (!launch) {
    ui.error(provider === "claude"
      ? "Claude Code's CLI was not found. Install Claude Code or its extension, or set CLAUDE_PATH, then try again."
      : "Codex CLI was not found. Install Codex or the Codex extension, or set CODEX_PATH, then try again.");
    return "failed";
  }

  const created = state === "missing";
  await fileSystem.createFolder(folder);
  const variable = configDirectoryVariable(provider);
  const loginArgs = provider === "claude" ? ["auth", "login", "--claudeai"] : ["login"];
  let closed: PromiseLike<void> | undefined;
  if (dependencies.canRunLoginInTerminal?.() ?? true) {
    closed = ui.runLogin({
      name: `UsageAI: ${providerTitle(provider)} sign-in`,
      environment: { [variable]: folder },
      executable: launch.executable,
      args: [...launch.args, ...loginArgs],
    }).closed;
  } else {
    // A terminal would open on the remote host, which cannot write this computer's folder.
    const command = commandWithEnvironment(variable, folder, `${provider} ${loginArgs.join(" ")}`);
    await ui.copy(command);
    void ui.info(`Run this in a terminal on this computer to sign in (copied): ${command}`);
  }

  const signedIn = await ui.waitWithProgress(
    "Waiting for you to finish signing in in the browser…",
    (cancelled) => waitForLogin(dependencies, provider, folder, cancelled, closed),
  );
  if (!signedIn) {
    if (created) {
      await fileSystem.removeIfEmpty(folder);
    }
    ui.error(`The ${providerTitle(provider)} sign-in did not finish, so no account was added.`);
    return "failed";
  }
  return useExistingFolder(dependencies, provider, folder, name);
}

/** Polls for the credential file, bounded, until it appears, the user cancels, or the terminal closes. */
async function waitForLogin(
  dependencies: AddAccountDependencies,
  provider: AccountProviderId,
  folder: string,
  cancelled: () => boolean,
  closed: PromiseLike<void> | undefined,
): Promise<boolean> {
  const fileSystem = fileSystemOf(dependencies);
  const interval = dependencies.pollIntervalMs ?? 1_000;
  const deadline = Date.now() + (dependencies.timeoutMs ?? signInTimeoutMs);
  let terminalClosed = false;
  void closed?.then(() => {
    terminalClosed = true;
  });
  while (!cancelled()) {
    if (await fileSystem.hasCredentialFile(provider, folder)) {
      return true;
    }
    if (terminalClosed || Date.now() >= deadline) {
      // The CLI may write its file just as it exits.
      return fileSystem.hasCredentialFile(provider, folder);
    }
    await new Promise((resolve) => setTimeout(resolve, interval));
  }
  return false;
}

/** "Use a login folder I already have": validated, checked for a duplicate, optionally named, added. */
export async function useExistingFolder(
  dependencies: AddAccountDependencies,
  provider: AccountProviderId,
  rawFolder: string,
  presetName?: string,
): Promise<AddAccountResult> {
  const { ui } = dependencies;
  const folder = path.isAbsolute(rawFolder.trim()) ? path.resolve(rawFolder.trim()) : rawFolder.trim();
  const problem = accountPathProblem(folder || ".")
    ?? (await fileSystemOf(dependencies).hasCredentialFile(provider, folder) ? undefined : `it holds no ${credentialFileName(provider)}`);
  if (problem) {
    ui.error(`UsageAI cannot use ${folder || "that folder"}: ${problem}.`);
    return "failed";
  }

  const id = accountIdForPath(folder);
  const shown = dependencies.accounts().filter((account) => account.provider === provider);
  const sameFolder = shown.find((account) => account.id === id || accountIdForPath(account.path) === id);
  const identity = provider === "claude"
    ? await readClaudeIdentity(path.join(folder, ".claude.json"))
    : await readCodexIdentity(folder);
  const key = identityKey(identity);
  const samePerson = key ? shown.find((account) => identityKey(account.identity) === key) : undefined;
  const duplicate = sameFolder ?? samePerson;
  if (duplicate) {
    void ui.info(`Already shown as ${duplicate.label}.`);
    return "duplicate";
  }

  const name = presetName ?? await ui.input({
    title: `Add ${providerTitle(provider)} Account`,
    prompt: "A name for this account (optional). Without one, it is shown by its email or folder name.",
    ...(identity.email ? { placeHolder: identity.email } : {}),
  });
  if (name === undefined) {
    return "cancelled";
  }
  const label = cleanLabel(name);
  const current = dependencies.configuredAccounts();
  const kept = (Array.isArray(current) ? current : []).filter((entry: unknown) => {
    const record = entry as { provider?: unknown; path?: unknown } | null;
    return !(record && record.provider === provider && typeof record.path === "string"
      && path.isAbsolute(record.path) && accountIdForPath(record.path) === id);
  });
  const setting: NewAccountSetting = { provider, path: folder, ...(label ? { label } : {}) };
  await dependencies.saveConfiguredAccounts([...kept, setting]);
  await dependencies.afterAdd(setting);
  void ui.info(`Added ${providerTitle(provider)} account ${label ?? identity.email ?? path.basename(folder)}.`);
  return "added";
}
