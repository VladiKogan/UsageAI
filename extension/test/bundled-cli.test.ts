import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import test from "node:test";
import {
  claudeBundledPath,
  codexBundledPath,
  findBundledExecutable,
  sameHostExtensionPath,
  type ExtensionPathResolver,
} from "../src/bundled-cli";
import { findClaudeLaunch } from "../src/providers/claude";
import { findCodexLaunch } from "../src/providers/codex";

function restoreEnvironment(name: string, value: string | undefined): void {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}

async function touch(file: string): Promise<void> {
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, "", "utf8");
}

test("bundled CLI paths follow each official extension's layout", () => {
  assert.equal(codexBundledPath("win32", "x64"), path.join("bin", "windows-x86_64", "codex.exe"));
  assert.equal(codexBundledPath("linux", "x64"), path.join("bin", "linux-x86_64", "codex"));
  assert.equal(codexBundledPath("linux", "arm64"), path.join("bin", "linux-aarch64", "codex"));
  assert.equal(codexBundledPath("darwin", "arm64"), path.join("bin", "macos-aarch64", "codex"));
  assert.equal(codexBundledPath("linux", "ia32"), undefined);
  assert.equal(codexBundledPath("freebsd", "x64"), undefined);
  assert.equal(claudeBundledPath("win32"), path.join("resources", "native-binary", "claude.exe"));
  assert.equal(claudeBundledPath("linux"), path.join("resources", "native-binary", "claude"));
});

test("bundled CLI lookup trusts only a local extension in this extension host", () => {
  const root = path.resolve(os.tmpdir(), "provider-extension");
  const local = { extensionPath: root, extensionKind: 2, extensionUri: { scheme: "file" } };
  assert.equal(sameHostExtensionPath(local, 2), root);
  assert.equal(sameHostExtensionPath(local, undefined), undefined);
  assert.equal(sameHostExtensionPath(undefined, 2), undefined);
  assert.equal(sameHostExtensionPath(local, 1), undefined);
  assert.equal(sameHostExtensionPath({ ...local, extensionUri: { scheme: "vscode-remote" } }, 2), undefined);
  assert.equal(sameHostExtensionPath({ ...local, extensionPath: "relative/folder" }, 2), undefined);
});

test("bundled CLI lookup returns one fixed path only when the file exists", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "usageai-bundled-"));
  const relative = path.join("bin", "tool.exe");
  try {
    const resolve: ExtensionPathResolver = (id) => (id === "publisher.tool" ? root : undefined);
    assert.equal(await findBundledExecutable(resolve, "publisher.tool", relative), undefined);
    await touch(path.join(root, relative));
    assert.equal(await findBundledExecutable(resolve, "publisher.tool", relative), path.join(root, relative));
    assert.equal(await findBundledExecutable(resolve, "publisher.other", relative), undefined);
    assert.equal(await findBundledExecutable(resolve, "publisher.tool", undefined), undefined);
    await touch(path.join(path.dirname(root), "outside.exe"));
    assert.equal(await findBundledExecutable(resolve, "publisher.tool", path.join("..", "outside.exe")), undefined);
    assert.equal(await findBundledExecutable(() => "relative/root", "publisher.tool", relative), undefined);
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
  const bundledCodex = path.join(codexRoot, codexBundledPath() ?? "unsupported");
  const bundledClaude = path.join(claudeRoot, claudeBundledPath());
  try {
    await mkdir(pathDirectory, { recursive: true });
    for (const name of ["CODEX_PATH", "CLAUDE_PATH"]) delete process.env[name];
    process.env.PATH = pathDirectory;
    for (const name of ["APPDATA", "LOCALAPPDATA", "USERPROFILE", "HOME"]) process.env[name] = path.join(directory, "home");
    const resolve: ExtensionPathResolver = (id) => ({ "openai.chatgpt": codexRoot, "anthropic.claude-code": claudeRoot })[id];

    await assert.rejects(() => findCodexLaunch(resolve), /Codex extension/);
    assert.equal(await findClaudeLaunch(resolve), undefined);

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
