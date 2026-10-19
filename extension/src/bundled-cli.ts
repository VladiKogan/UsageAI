import { constants as fsConstants } from "node:fs";
import { access } from "node:fs/promises";
import * as path from "node:path";

/** Returns the install folder of another extension running in this extension host, or nothing. */
export interface ExtensionPathResolver {
  (extensionId: string): string | undefined;
  /** Whether the extension is installed but runs in the other host of a remote window. */
  readonly onOtherHost?: (extensionId: string) => boolean;
}

/** The default outside VS Code, and in tests that don't exercise bundled CLIs: no extensions. */
export const noExtensions: ExtensionPathResolver = () => undefined;

/** The parts of a `vscode.Extension` this module reads, so tests need no VS Code host. */
export interface ExtensionLocation {
  readonly extensionPath: string;
  readonly extensionKind: number;
  readonly extensionUri: { readonly scheme: string };
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
  if (ownKind === undefined || extension.extensionKind !== ownKind) {
    return undefined;
  }
  return path.isAbsolute(extension.extensionPath) ? extension.extensionPath : undefined;
}

/** True for an installed extension that `sameHostExtensionPath` turns down for running elsewhere. */
export function otherHostExtension(extension: ExtensionLocation | undefined, ownKind: number | undefined): boolean {
  return extension !== undefined && ownKind !== undefined && sameHostExtensionPath(extension, ownKind) === undefined;
}

/**
 * Windows has no execute bit, so existence is the check there; elsewhere a binary unpacked without
 * its execute bit would only fail later, at spawn, with a less useful error than "not found".
 */
export function executableAccessMode(platform: NodeJS.Platform = process.platform): number {
  return platform === "win32" ? fsConstants.F_OK : fsConstants.X_OK;
}

/**
 * A short, fixed list of paths inside an official provider extension, tried in order: never a
 * search, so nothing outside the folder VS Code reports for that extension id can be launched.
 */
export async function findBundledExecutable(
  resolveExtensionPath: ExtensionPathResolver,
  extensionId: string,
  relativePaths: readonly string[],
): Promise<string | undefined> {
  if (relativePaths.length === 0) {
    return undefined;
  }
  const root = resolveExtensionPath(extensionId);
  if (!root || !path.isAbsolute(root)) {
    return undefined;
  }
  for (const relativePath of relativePaths) {
    const candidate = path.resolve(root, relativePath);
    const relative = path.relative(root, candidate);
    if (!relative || relative.startsWith("..") || path.isAbsolute(relative)) {
      continue;
    }
    try {
      await access(candidate, executableAccessMode());
      return candidate;
    } catch {
      // Try the next layout.
    }
  }
  return undefined;
}

/**
 * `openai.chatgpt` ships `bin/<os>-<arch>/codex`. The names mirror the extension's own lookup:
 * win32 is `windows`, darwin is `macos`, x64 is `x86_64`, and arm64 is `aarch64`.
 */
export function codexBundledPaths(
  platform: NodeJS.Platform = process.platform,
  arch: string = process.arch,
): readonly string[] {
  const os = ({ win32: "windows", linux: "linux", darwin: "macos" } as Partial<Record<NodeJS.Platform, string>>)[platform];
  const cpu = ({ x64: "x86_64", arm64: "aarch64" } as Record<string, string>)[arch];
  if (!os || !cpu) {
    return [];
  }
  return [path.join("bin", `${os}-${cpu}`, platform === "win32" ? "codex.exe" : "codex")];
}

/**
 * `anthropic.claude-code` mirrors the extension's own lookup: a multi-platform package keeps
 * `resources/native-binaries/<platform>-<arch>/claude`, where Windows on ARM falls back to the x64
 * build, and a platform-specific package keeps a single `resources/native-binary/claude`.
 */
export function claudeBundledPaths(
  platform: NodeJS.Platform = process.platform,
  arch: string = process.arch,
): readonly string[] {
  const executable = platform === "win32" ? "claude.exe" : "claude";
  return [
    path.join("resources", "native-binaries", `${platform}-${arch}`, executable),
    ...(platform === "win32" && arch === "arm64"
      ? [path.join("resources", "native-binaries", "win32-x64", executable)]
      : []),
    path.join("resources", "native-binary", executable),
  ];
}
