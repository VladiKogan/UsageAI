import { access } from "node:fs/promises";
import * as path from "node:path";
import { executableAccessMode } from "./security";

/** Returns the install folder of another extension running in this extension host, or nothing. */
export interface ExtensionPathResolver {
  (extensionId: string): string | undefined;
  /**
   * UsageAI runs on this computer while the window is connected to a remote host. VS Code's stable
   * API only reports extensions in UsageAI's own host, so a provider extension installed on the
   * remote side cannot be seen; this is what lets an error point there anyway.
   */
  readonly runsLocallyInRemoteWindow?: boolean;
}

/** The default outside VS Code, and in tests that don't exercise bundled CLIs: no extensions. */
export const noExtensions: ExtensionPathResolver = () => undefined;

/** The parts of a `vscode.Extension` this module reads, so tests need no VS Code host. */
export interface ExtensionLocation {
  readonly extensionPath: string;
  readonly extensionUri: { readonly scheme: string };
}

/**
 * The stable `getExtension` only reports extensions in UsageAI's own extension host, so the folder
 * is on this machine already; a file-scheme absolute path is what remains to check before launching
 * anything from it.
 */
export function localExtensionPath(extension: ExtensionLocation | undefined): string | undefined {
  if (!extension || extension.extensionUri.scheme !== "file") {
    return undefined;
  }
  return path.isAbsolute(extension.extensionPath) ? extension.extensionPath : undefined;
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
