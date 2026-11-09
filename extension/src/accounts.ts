import { createHash } from "node:crypto";
import { readdir, stat } from "node:fs/promises";
import * as path from "node:path";
import { getObject, getString } from "./providers/json";
import { readBoundedText, userHome } from "./security";

/**
 * Multiple logins per provider. Phase 1 covers Claude Code and Codex: a login is a config folder
 * (`CLAUDE_CONFIG_DIR` / `CODEX_HOME`) holding the provider's own credential file. UsageAI only reads
 * those folders; it never writes or refreshes a token and never switches the folder a CLI uses.
 * Mirrors the desktop app's account model, so ids and unit keys are identical on both sides.
 */
export type AccountProviderId = "claude" | "codex";
export const accountProviderIds: readonly AccountProviderId[] = ["claude", "codex"];

/**
 * The main login, keyed by the bare provider id: always the standard `~/.claude` / `~/.codex`. An
 * inherited `CLAUDE_CONFIG_DIR` / `CODEX_HOME` never moves it; that folder is found as an account.
 */
export const defaultAccountId = "default";
/** How many folders `usageai.accounts` may name. */
export const maxConfiguredAccounts = 16;
/** How many found folders one listing may yield, so a crowded home folder stays cheap. */
export const maxDiscoveredAccounts = 32;
const maxLabelCharacters = 64;
/** `.claude.json` also holds Claude Code's project history, so it may be large; it is read rarely. */
const maxClaudeProfileCharacters = 8_388_608;
const maxCodexAuthCharacters = 262_144;

export interface AccountIdentity {
  readonly email?: string;
  /** Claude's organization UUID or Codex's ChatGPT account (workspace) id. */
  readonly organizationId?: string;
  readonly organizationName?: string;
}

/** One entry of the `usageai.accounts` setting, after shape validation. */
export interface AccountSetting {
  readonly provider: AccountProviderId;
  readonly path: string;
  readonly label?: string;
  readonly hidden: boolean;
}

export interface DiscoveredFolder {
  readonly provider: AccountProviderId;
  readonly path: string;
  readonly id: string;
}

export type AccountSource = "default" | "found" | "added";

/** A login UsageAI knows about, whether or not it is currently shown. */
export interface AccountEntry {
  readonly provider: AccountProviderId;
  readonly id: string;
  /** The key every per-provider thing uses: `claude` for the default login, else `claude@<id>`. */
  readonly unitKey: string;
  /** The login folder; for the default login, the folder its CLI reads today. */
  readonly path: string;
  readonly source: AccountSource;
  readonly identity: AccountIdentity;
  /** The user's own name for it, from `usageai.accounts`. */
  readonly userLabel?: string;
  /** User label, else email, else folder name. */
  readonly label: string;
  readonly hidden: boolean;
}

export function isAccountProviderId(value: unknown): value is AccountProviderId {
  return value === "claude" || value === "codex";
}

export function providerTitle(provider: AccountProviderId): string {
  return provider === "claude" ? "Claude" : "Codex";
}

export function credentialFileName(provider: AccountProviderId): string {
  return provider === "claude" ? ".credentials.json" : "auth.json";
}

export function configDirectoryVariable(provider: AccountProviderId): "CLAUDE_CONFIG_DIR" | "CODEX_HOME" {
  return provider === "claude" ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME";
}

function pathApi(platform: NodeJS.Platform): path.PlatformPath {
  return platform === "win32" ? path.win32 : path.posix;
}

/** `path.resolve`, trailing separators trimmed, lower-cased on Windows only. */
export function normalizeAccountPath(folder: string, platform: NodeJS.Platform = process.platform): string {
  const api = pathApi(platform);
  let resolved = api.resolve(folder);
  const root = api.parse(resolved).root;
  while (resolved.length > root.length && /[\\/]$/.test(resolved)) {
    resolved = resolved.slice(0, -1);
  }
  return platform === "win32" ? resolved.toLowerCase() : resolved;
}

/** The first 8 hex characters of SHA-256 over the UTF-8 normalised folder path. */
export function accountIdForPath(folder: string, platform: NodeJS.Platform = process.platform): string {
  return createHash("sha256").update(normalizeAccountPath(folder, platform), "utf8").digest("hex").slice(0, 8);
}

export function unitKey(providerId: string, accountId: string): string {
  return accountId === defaultAccountId ? providerId : `${providerId}@${accountId}`;
}

/** The provider part of a unit key: what picks the icon, colours, incidents and visibility. */
export function providerKindOf(key: string): string {
  const at = key.indexOf("@");
  return at < 0 ? key : key.slice(0, at);
}

const accountUnitKeyPattern = /^(claude|codex)@[0-9a-f]{8}$/;

/** A non-default account's unit key, such as `claude@1a2b3c4d`. */
export function isAccountUnitKey(value: unknown): value is string {
  return typeof value === "string" && accountUnitKeyPattern.test(value);
}

/** A provider id from `providerIds` or an account unit key of one of them. */
export function isKnownUnitKey(value: unknown, providerIds: readonly string[]): value is string {
  return typeof value === "string" && providerIds.includes(providerKindOf(value))
    && (!value.includes("@") || isAccountUnitKey(value));
}

export function isUncPath(folder: string): boolean {
  return /^[\\/]{2}/.test(folder.trim());
}

/**
 * The main login's folder: always the provider's standard `~/.claude` / `~/.codex`, the folder its
 * CLI uses when no variable is set, however the editor was started. Mirrors the desktop's
 * `AccountDiscovery.DefaultFolder`.
 */
export function defaultLoginFolder(provider: AccountProviderId, home: string = userHome()): string {
  return path.join(home, provider === "claude" ? ".claude" : ".codex");
}

/**
 * The folder an inherited `CLAUDE_CONFIG_DIR` / `CODEX_HOME` names, when it is an absolute local
 * path other than the standard folder. It is offered as a found account, never as the main login.
 */
export function inheritedLoginFolder(
  provider: AccountProviderId,
  environment: NodeJS.ProcessEnv = process.env,
  home: string = userHome(),
  platform: NodeJS.Platform = process.platform,
): string | undefined {
  const configured = environment[configDirectoryVariable(provider)]?.trim().replace(/^"|"$/g, "");
  if (!configured || !pathApi(platform).isAbsolute(configured) || isUncPath(configured)) {
    return undefined;
  }
  const folder = pathApi(platform).resolve(configured);
  return normalizeAccountPath(folder, platform) === normalizeAccountPath(defaultLoginFolder(provider, home), platform)
    ? undefined
    : folder;
}

/**
 * Where Claude keeps a folder's account profile, `.claude.json`: inside the folder when the CLI runs
 * with `CLAUDE_CONFIG_DIR`, but beside the standard `~/.claude` (as `~/.claude.json`) when no variable
 * is set, which is how the main login is read. A profile inside `~/.claude` only answers when it is
 * the only one there. Mirrors the desktop's `AccountDiscovery.ClaudeIdentityFile`.
 */
export async function claudeProfilePath(
  folder: string,
  home: string = userHome(),
  isFile: (file: string) => Promise<boolean> = nodeAccountFileSystem.isFile,
  platform: NodeJS.Platform = process.platform,
): Promise<string> {
  const inFolder = path.join(folder, ".claude.json");
  if (normalizeAccountPath(folder, platform) !== normalizeAccountPath(defaultLoginFolder("claude", home), platform)) {
    return inFolder;
  }
  const inProfile = path.join(home, ".claude.json");
  return await isFile(inProfile) || !await isFile(inFolder) ? inProfile : inFolder;
}

export interface AccountFileSystem {
  readonly listDirectory: (folder: string) => Promise<ReadonlyArray<{ readonly name: string; isDirectory(): boolean; isSymbolicLink(): boolean }>>;
  readonly isFile: (file: string) => Promise<boolean>;
}

export const nodeAccountFileSystem: AccountFileSystem = {
  listDirectory: (folder) => readdir(folder, { withFileTypes: true }),
  isFile: async (file) => {
    try {
      return (await stat(file)).isFile();
    } catch {
      return false;
    }
  },
};

/**
 * The provider a folder name belongs to: anything that starts with `.claude` or `.codex` and goes
 * on, so `.claude-work`, `.claude_work`, `.claude2` and `.codex.work` all count. The bare `.claude`
 * / `.codex` is the default login, never a found one. A wide rule is safe because only a folder
 * holding the provider's own credential file is taken. Mirrors the desktop's ProviderForFolderName.
 */
export function discoveredProvider(name: string): AccountProviderId | undefined {
  const lower = name.toLowerCase();
  if (lower.length > ".claude".length && lower.startsWith(".claude")) {
    return "claude";
  }
  if (lower.length > ".codex".length && lower.startsWith(".codex")) {
    return "codex";
  }
  return undefined;
}

/**
 * One non-recursive listing of the home folder: children whose name starts with `.claude` and that
 * hold `.credentials.json` are Claude logins, `.codex…` holding `auth.json` Codex logins. The
 * default login's own folder is skipped. Never a search beyond that one listing.
 */
export async function discoverAccountFolders(
  home: string,
  defaultFolders: Partial<Record<AccountProviderId, string | undefined>>,
  fileSystem: AccountFileSystem = nodeAccountFileSystem,
  platform: NodeJS.Platform = process.platform,
): Promise<DiscoveredFolder[]> {
  let entries: Awaited<ReturnType<AccountFileSystem["listDirectory"]>>;
  try {
    entries = await fileSystem.listDirectory(home);
  } catch {
    return [];
  }
  const defaults = new Set(Object.values(defaultFolders)
    .filter((folder): folder is string => Boolean(folder))
    .map((folder) => normalizeAccountPath(folder, platform)));
  const candidates = entries
    .filter((entry) => entry.isDirectory() || entry.isSymbolicLink())
    .map((entry) => ({ name: entry.name, provider: discoveredProvider(entry.name) }))
    .filter((entry): entry is { name: string; provider: AccountProviderId } => entry.provider !== undefined)
    .sort((left, right) => left.name.localeCompare(right.name));
  const found: DiscoveredFolder[] = [];
  for (const candidate of candidates) {
    if (found.length >= maxDiscoveredAccounts) {
      break;
    }
    const folder = pathApi(platform).join(home, candidate.name);
    if (defaults.has(normalizeAccountPath(folder, platform))) {
      continue;
    }
    if (await fileSystem.isFile(pathApi(platform).join(folder, credentialFileName(candidate.provider)))) {
      found.push({ provider: candidate.provider, path: folder, id: accountIdForPath(folder, platform) });
    }
  }
  return found;
}

export function cleanLabel(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const cleaned = value.replace(/[\u0000-\u001f\u007f]/g, "").trim();
  if (!cleaned) {
    return undefined;
  }
  return cleaned.length > maxLabelCharacters ? cleaned.slice(0, maxLabelCharacters).trimEnd() : cleaned;
}

export interface AccountSettingsResult {
  readonly entries: readonly AccountSetting[];
  /** Why each rejected entry was left out, for one warning. */
  readonly rejected: readonly string[];
}

/** Why a folder cannot be a login folder by its path alone, or nothing. */
export function accountPathProblem(folder: string): string | undefined {
  if (isUncPath(folder)) {
    return "a network (UNC) path is not allowed";
  }
  if (!path.isAbsolute(folder)) {
    return "the path must be absolute";
  }
  return undefined;
}

/**
 * Shape validation of `usageai.accounts`: provider, an absolute local path, an optional label and
 * hidden flag, at most `maxConfiguredAccounts` distinct folders. The credential file is checked
 * separately, because that needs the disk.
 */
export function sanitizeAccountSettings(value: unknown): AccountSettingsResult {
  if (!Array.isArray(value)) {
    return { entries: [], rejected: value === undefined || value === null ? [] : ["usageai.accounts must be a list"] };
  }
  const entries: AccountSetting[] = [];
  const rejected: string[] = [];
  const seen = new Set<string>();
  for (const candidate of value as unknown[]) {
    if (typeof candidate !== "object" || candidate === null || Array.isArray(candidate)) {
      rejected.push("an entry is not an object");
      continue;
    }
    const record = candidate as Record<string, unknown>;
    if (!isAccountProviderId(record.provider)) {
      rejected.push("an entry's provider must be \"claude\" or \"codex\"");
      continue;
    }
    const folder = typeof record.path === "string" ? record.path.trim().replace(/^"|"$/g, "") : "";
    const problem = folder ? accountPathProblem(folder) : "the path is missing";
    if (problem) {
      rejected.push(`${folder || "(no path)"}: ${problem}`);
      continue;
    }
    const resolved = path.resolve(folder);
    const key = `${record.provider}:${accountIdForPath(resolved)}`;
    if (seen.has(key)) {
      continue;
    }
    if (entries.length >= maxConfiguredAccounts) {
      rejected.push(`more than ${maxConfiguredAccounts} accounts are listed`);
      break;
    }
    seen.add(key);
    const label = cleanLabel(record.label);
    entries.push({ provider: record.provider, path: resolved, ...(label ? { label } : {}), hidden: record.hidden === true });
  }
  return { entries, rejected };
}

/** Why a folder cannot be used as a login folder, or nothing when it can. */
export async function accountFolderProblem(
  provider: AccountProviderId,
  folder: string,
  fileSystem: AccountFileSystem = nodeAccountFileSystem,
): Promise<string | undefined> {
  const problem = accountPathProblem(folder);
  if (problem) {
    return problem;
  }
  return await fileSystem.isFile(path.join(folder, credentialFileName(provider)))
    ? undefined
    : `it holds no ${credentialFileName(provider)}`;
}

function cleanIdentityValue(value: string | undefined, maxLength: number): string | undefined {
  const cleaned = value?.replace(/[\u0000-\u001f\u007f]/g, "").trim();
  return cleaned && cleaned.length <= maxLength ? cleaned : undefined;
}

/** Who a Claude folder is signed in as, from its `.claude.json`. Local files only. */
export async function readClaudeIdentity(profilePath: string): Promise<AccountIdentity> {
  try {
    const root = JSON.parse(await readBoundedText(profilePath, maxClaudeProfileCharacters)) as unknown;
    const account = getObject(root, "oauthAccount");
    const email = cleanIdentityValue(getString(account, "emailAddress"), 320);
    const organizationId = cleanIdentityValue(getString(account, "organizationUuid"), 128);
    const organizationName = cleanIdentityValue(getString(account, "organizationName"), 128);
    return {
      ...(email ? { email } : {}),
      ...(organizationId ? { organizationId } : {}),
      ...(organizationName ? { organizationName } : {}),
    };
  } catch {
    return {};
  }
}

/** The payload of a JWT, or nothing. The signature is not checked: this only names who signed in. */
export function jwtPayload(token: string | undefined): unknown {
  const parts = token?.split(".") ?? [];
  if (parts.length !== 3 || !parts[1] || parts[1].length > 16_384) {
    return undefined;
  }
  try {
    return JSON.parse(Buffer.from(parts[1], "base64url").toString("utf8")) as unknown;
  } catch {
    return undefined;
  }
}

/** Who a Codex folder is signed in as, from the `id_token` in its `auth.json`. Local files only. */
export async function readCodexIdentity(folder: string): Promise<AccountIdentity> {
  try {
    const auth = JSON.parse(await readBoundedText(path.join(folder, "auth.json"), maxCodexAuthCharacters)) as unknown;
    const tokens = getObject(auth, "tokens");
    const payload = jwtPayload(getString(tokens, "id_token"));
    const email = cleanIdentityValue(getString(payload, "email"), 320);
    const claims = getObject(payload, "https://api.openai.com/auth");
    const organizationId = cleanIdentityValue(getString(claims, "chatgpt_account_id") ?? getString(tokens, "account_id"), 128);
    return { ...(email ? { email } : {}), ...(organizationId ? { organizationId } : {}) };
  } catch {
    return {};
  }
}

/** Who is signed in, for duplicate checks: email plus organization, or nothing without an email. */
export function identityKey(identity: AccountIdentity): string | undefined {
  return identity.email ? `${identity.email.toLowerCase()}|${identity.organizationId?.toLowerCase() ?? ""}` : undefined;
}

/** User label, else email, else folder name. */
export function accountLabel(userLabel: string | undefined, identity: AccountIdentity, folder: string): string {
  return userLabel ?? identity.email ?? (path.basename(folder) || folder);
}

/**
 * Keeps one provider's account labels apart, as the desktop app does: when two read the same, each
 * adds its organisation if they all have different ones, else the second becomes "label · Account 2".
 */
export function distinctAccountLabels<T extends { readonly label: string; readonly identity: AccountIdentity }>(
  accounts: readonly T[],
): T[] {
  const groups = new Map<string, number[]>();
  accounts.forEach((account, index) => {
    const key = account.label.toLowerCase();
    groups.set(key, [...(groups.get(key) ?? []), index]);
  });
  const labels = accounts.map((account) => account.label);
  for (const members of groups.values()) {
    if (members.length < 2) continue;
    const organisations = members.map((index) => accounts[index]!.identity.organizationName);
    const byOrganisation = organisations.every((name) => !!name) &&
      new Set(organisations.map((name) => name!.toLowerCase())).size === members.length;
    members.forEach((index, position) => {
      labels[index] = byOrganisation
        ? `${accounts[index]!.label} · ${organisations[position]}`
        : position === 0 ? accounts[index]!.label : `${accounts[index]!.label} · Account ${position + 1}`;
    });
  }
  return accounts.map((account, index) => labels[index] === account.label ? account : { ...account, label: labels[index]! });
}

/** "Claude Code · team" when the provider shows more than one account; otherwise today's plain title. */
export function accountCardTitle(displayName: string, label: string | undefined, accountsShownForProvider: number): string {
  return accountsShownForProvider > 1 && label ? `${displayName} · ${label}` : displayName;
}

/** A command that runs `command` with one environment variable set, in the platform's usual shell. */
export function commandWithEnvironment(
  name: string,
  value: string,
  command: string,
  platform: NodeJS.Platform = process.platform,
): string {
  return platform === "win32"
    ? `$env:${name}='${value.replaceAll("'", "''")}'; ${command}`
    : `${name}='${value.replaceAll("'", "'\\''")}' ${command}`;
}

/**
 * The sign-in for the main login, which reads the standard folder. A terminal that inherits
 * `CLAUDE_CONFIG_DIR` / `CODEX_HOME` (as the editor did) would sign in to that folder instead, so
 * the line clears the variable first.
 */
export function mainSignInCommand(
  provider: AccountProviderId,
  environment: NodeJS.ProcessEnv = process.env,
  platform: NodeJS.Platform = process.platform,
): string {
  const command = provider === "codex" ? "codex login" : "claude";
  const variable = configDirectoryVariable(provider);
  if (!environment[variable]?.trim()) {
    return command;
  }
  return platform === "win32"
    ? `Remove-Item Env:${variable} -ErrorAction Ignore; ${command}`
    : `env -u ${variable} ${command}`;
}

/** Account names become folder suffixes: lower-case letters, digits and single dashes. */
export function sanitizeAccountName(name: string): string {
  return name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 32).replace(/-+$/, "");
}

/**
 * The folder "Sign in to another account" proposes: `~/.claude-<name>`, or `~/.claude-account-2`
 * without a name, with `-2`, `-3` … appended while the name is taken.
 */
export async function proposeLoginFolder(
  provider: AccountProviderId,
  name: string,
  home: string,
  exists: (folder: string) => Promise<boolean>,
): Promise<string> {
  const sanitized = sanitizeAccountName(name);
  const base = path.join(home, `.${provider}-${sanitized || "account"}`);
  for (let suffix = sanitized ? 1 : 2; suffix < 1_000; suffix += 1) {
    const candidate = suffix === 1 ? base : `${base}-${suffix}`;
    if (!await exists(candidate)) {
      return candidate;
    }
  }
  return `${base}-${Date.now()}`;
}
