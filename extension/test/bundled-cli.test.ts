import assert from "node:assert/strict";
import { constants as fsConstants } from "node:fs";
import { chmod, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import test from "node:test";
import {
  claudeBundledPaths,
  codexBundledPaths,
  findBundledExecutable,
  localExtensionPath,
  type ExtensionPathResolver,
} from "../src/bundled-cli";
import { findClaudeLaunch } from "../src/providers/claude";
import { executableAccessMode } from "../src/security";
import { findCodexLaunch } from "../src/providers/codex";

function restoreEnvironment(name: string, value: string | undefined): void {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}

async function touch(file: string): Promise<void> {
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, "", "utf8");
  // The lookup requires the execute bit outside Windows, as a real unpacked binary has.
  if (process.platform !== "win32") await chmod(file, 0o755);
}

test("bundled CLI paths follow each official extension's own lookup", () => {
  assert.deepEqual(codexBundledPaths("win32", "x64"), [path.join("bin", "windows-x86_64", "codex.exe")]);
  assert.deepEqual(codexBundledPaths("linux", "x64"), [path.join("bin", "linux-x86_64", "codex")]);
  assert.deepEqual(codexBundledPaths("linux", "arm64"), [path.join("bin", "linux-aarch64", "codex")]);
  assert.deepEqual(codexBundledPaths("darwin", "arm64"), [path.join("bin", "macos-aarch64", "codex")]);
  assert.deepEqual(codexBundledPaths("linux", "ia32"), []);
  assert.deepEqual(codexBundledPaths("freebsd", "x64"), []);
  assert.deepEqual(claudeBundledPaths("linux", "x64"), [
    path.join("resources", "native-binaries", "linux-x64", "claude"),
    path.join("resources", "native-binary", "claude"),
  ]);
  assert.deepEqual(claudeBundledPaths("win32", "arm64"), [
    path.join("resources", "native-binaries", "win32-arm64", "claude.exe"),
    path.join("resources", "native-binaries", "win32-x64", "claude.exe"),
    path.join("resources", "native-binary", "claude.exe"),
  ]);
  assert.equal(executableAccessMode("win32"), fsConstants.F_OK);
  assert.equal(executableAccessMode("linux"), fsConstants.X_OK);
});

test("bundled CLI lookup only launches from a local, absolute extension folder", () => {
  const root = path.resolve(os.tmpdir(), "provider-extension");
  const local = { extensionPath: root, extensionUri: { scheme: "file" } };
  assert.equal(localExtensionPath(local), root);
  assert.equal(localExtensionPath(undefined), undefined);
  assert.equal(localExtensionPath({ ...local, extensionUri: { scheme: "vscode-remote" } }), undefined);
  assert.equal(localExtensionPath({ ...local, extensionPath: "relative/folder" }), undefined);
});

test("bundled CLI lookup tries its fixed paths in order and only returns existing files", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "usageai-bundled-"));
  const preferred = path.join("multi", "tool.exe");
  const fallback = path.join("single", "tool.exe");
  try {
    const resolve: ExtensionPathResolver = (id) => (id === "publisher.tool" ? root : undefined);
    assert.equal(await findBundledExecutable(resolve, "publisher.tool", [preferred, fallback]), undefined);
    await touch(path.join(root, fallback));
    assert.equal(await findBundledExecutable(resolve, "publisher.tool", [preferred, fallback]), path.join(root, fallback));
    await touch(path.join(root, preferred));
    assert.equal(await findBundledExecutable(resolve, "publisher.tool", [preferred, fallback]), path.join(root, preferred));
    assert.equal(await findBundledExecutable(resolve, "publisher.other", [preferred]), undefined);
    assert.equal(await findBundledExecutable(resolve, "publisher.tool", []), undefined);
    await touch(path.join(path.dirname(root), "outside.exe"));
    assert.equal(await findBundledExecutable(resolve, "publisher.tool", [path.join("..", "outside.exe")]), undefined);
    assert.equal(await findBundledExecutable(() => "relative/root", "publisher.tool", [preferred]), undefined);
    if (process.platform !== "win32") {
      await chmod(path.join(root, preferred), 0o644);
      assert.equal(await findBundledExecutable(resolve, "publisher.tool", [preferred]), undefined);
    }
  } finally {
    await rm(path.join(path.dirname(root), "outside.exe"), { force: true });
    await rm(root, { recursive: true, force: true });
  }
});

test("Codex and Claude use the extension-bundled CLI only after standalone installs", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "usageai-bundled-launch-"));
  const names = ["PATH", "CODEX_PATH", "CLAUDE_PATH", "APPDATA", "LOCALAPPDATA", "USERPROFILE", "HOME"];
  const previous = new Map(names.map((name) => [name, process.env[name]]));
  const pathDirectory = path.join(directory, "path");
  const codexRoot = path.join(directory, "openai.chatgpt");
  const claudeRoot = path.join(directory, "anthropic.claude-code");
  const bundledCodex = path.join(codexRoot, codexBundledPaths()[0] ?? "unsupported");
  const bundledClaude = path.join(claudeRoot, claudeBundledPaths().at(-1)!);
  try {
    await mkdir(pathDirectory, { recursive: true });
    for (const name of ["CODEX_PATH", "CLAUDE_PATH"]) delete process.env[name];
    process.env.PATH = pathDirectory;
    for (const name of ["APPDATA", "LOCALAPPDATA", "USERPROFILE", "HOME"]) process.env[name] = path.join(directory, "home");
    const resolve: ExtensionPathResolver = (id) => ({ "openai.chatgpt": codexRoot, "anthropic.claude-code": claudeRoot })[id];

    await assert.rejects(() => findCodexLaunch(resolve), /Install Codex or the Codex extension/);
    assert.equal(await findClaudeLaunch(resolve), undefined);
    // Running locally in a remote window, UsageAI cannot see the remote host: point at running there.
    const remoteWindow: ExtensionPathResolver = Object.assign(() => undefined, { runsLocallyInRemoteWindow: true });
    await assert.rejects(() => findCodexLaunch(remoteWindow), /not found on this computer.*remote.extensionKind/);

    await touch(bundledCodex);
    await touch(bundledClaude);
    assert.deepEqual(await findCodexLaunch(resolve), { executable: bundledCodex, args: [] });
    assert.deepEqual(await findClaudeLaunch(resolve), { executable: bundledClaude, argsPrefix: [] });
    // Without a resolver, as outside VS Code, the bundled binaries are never considered.
    await assert.rejects(() => findCodexLaunch(), /Codex CLI was not found/);
    assert.equal(await findClaudeLaunch(), undefined);

    const windows = process.platform === "win32";
    const standaloneCodex = path.join(pathDirectory, windows ? "codex.exe" : "codex");
    const standaloneClaude = path.join(pathDirectory, windows ? "claude.exe" : "claude");
    await touch(standaloneCodex);
    await touch(standaloneClaude);
    assert.deepEqual(await findCodexLaunch(resolve), { executable: standaloneCodex, args: [] });
    assert.deepEqual(await findClaudeLaunch(resolve), { executable: standaloneClaude, argsPrefix: [] });
  } finally {
    for (const name of names) restoreEnvironment(name, previous.get(name));
    await rm(directory, { recursive: true, force: true });
  }
});
