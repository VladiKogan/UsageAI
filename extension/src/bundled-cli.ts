import { access } from "node:fs/promises";
import * as path from "node:path";

/** Returns the install folder of another extension running in this extension host, or nothing. */
export type ExtensionPathResolver = (extensionId: string) => string | undefined;

/** The parts of a `vscode.Extension` this module reads, so tests need no VS Code host. */
export interface ExtensionLocation {
  readonly extensionPath: string;
  readonly extensionKind: number;
  readonly extensionUri: { readonly scheme: string };
}

let resolveExtensionPath: ExtensionPathResolver = () => undefined;

/** Installed by activation; providers stay free of the `vscode` module. */
export function setExtensionPathResolver(resolver: ExtensionPathResolver): void {
  resolveExtensionPath = resolver;
}

/**
 * `getExtension` also reports extensions running in the other host of a remote window, whose path
 * names a folder on the other machine. Only an extension in this host, on the local file system, can
 * lend its binary.
 */
export function sameHostExtensionPath(
  extension: ExtensionLocation | undefined,
  ownKind: number | undefined,
): string | undefined {
  if (!extension || extension.extensionUri.scheme !== "file") {
    return undefined;
  }
  if (ownKind !== undefined && extension.extensionKind !== ownKind) {
    return undefined;
  }
  return path.isAbsolute(extension.extensionPath) ? extension.extensionPath : undefined;
}

/**
 * One fixed path inside an official provider extension: never a search, so nothing outside the
 * folder VS Code reports for that extension id can be launched.
 */
export async function findBundledExecutable(
  extensionId: string,
  relativePath: string | undefined,
): Promise<string | undefined> {
  if (!relativePath) {
    return undefined;
  }
  const root = resolveExtensionPath(extensionId);
  if (!root || !path.isAbsolute(root)) {
    return undefined;
  }
  const candidate = path.resolve(root, relativePath);
  const relative = path.relative(root, candidate);
  if (!relative || relative.startsWith("..") || path.isAbsolute(relative)) {
    return undefined;
  }
  try {
    await access(candidate);
    return candidate;
  } catch {
    return undefined;
  }
}

/** `openai.chatgpt` ships `bin/<os>-<arch>/codex`, named after Rust's target triple parts. */
export function codexBundledPath(
  platform: NodeJS.Platform = process.platform,
  arch: string = process.arch,
): string | undefined {
  const os = ({ win32: "windows", linux: "linux", darwin: "macos" } as Partial<Record<NodeJS.Platform, string>>)[platform];
  const cpu = ({ x64: "x86_64", arm64: "aarch64" } as Record<string, string>)[arch];
  if (!os || !cpu) {
    return undefined;
  }
  return path.join("bin", `${os}-${cpu}`, platform === "win32" ? "codex.exe" : "codex");
}

/** `anthropic.claude-code` ships one native binary per platform-specific package. */
export function claudeBundledPath(platform: NodeJS.Platform = process.platform): string {
  return path.join("resources", "native-binary", platform === "win32" ? "claude.exe" : "claude");
}
