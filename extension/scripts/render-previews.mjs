// Renders the README previews (media/dashboard-preview.png, media/status-bar-preview.png) from the
// compiled extension and sample providers, the way the desktop app's --render-preview does.
//
// The dashboard is the real webview HTML from dist/src/dashboard-view.js, and the Status Bar text and
// tooltip come from the real StatusBarController, both loaded with a stub `vscode` module. They are
// dressed in VS Code's Dark Modern colours and captured by headless Edge (or Chrome) over the
// DevTools protocol, cropped to the content.
//
//   npm.cmd run previews                     # compiles, then renders into media/
//   node ./scripts/render-previews.mjs --out-dir <dir> --scale 2
//
// Browser: EDGE_PATH / CHROME_PATH, else the standard Edge and Chrome install folders.
// Codicons (view-title icons): VSCODE_CODICON_PATH, else found next to `code` on PATH.

import { spawn, execFileSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const extensionRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const args = process.argv.slice(2);
const option = (name, fallback) => {
  const index = args.indexOf(name);
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback;
};
const outDir = resolve(option("--out-dir", join(extensionRoot, "media")));
const scale = Number(option("--scale", "2"));

// ---------------------------------------------------------------------------------------------
// Sample data: the same providers and readings as the desktop's PreviewRenderer.SampleData, at a
// fixed clock so every run renders the same times. Keep the two in step.
// ---------------------------------------------------------------------------------------------

const now = new Date();
now.setHours(10, 42, 0, 0);
const at = (hours, minutes = 0) => new Date(now.getTime() + (hours * 60 + minutes) * 60_000).toISOString();
const fetchedAt = at(0, -7);
const state = (id, displayName, snapshot) => ({
  id, displayName, signInCommand: id, accountUrl: "https://example.com", snapshot, stale: false, refreshing: false,
  lastAttemptedAt: fetchedAt,
});
const states = [
  state("codex", "Codex", {
    plan: "Plus", fetchedAt, providerId: "codex", providerName: "Codex",
    metrics: [
      { name: "5-hour", kind: "session", usedPercent: 41, resetsAt: at(1, 48), durationMinutes: 300 },
      { name: "Weekly", kind: "rolling", usedPercent: 92, resetsAt: at(2 * 24 + 18), durationMinutes: 10_080 },
      { name: "Credits", kind: "balance", usedPercent: null, remainingText: "$12.50", usageText: "Available account balance" },
      { name: "Reset credits", kind: "balance", usedPercent: null, remainingText: "1", usageText: "Full reset available" },
    ],
  }),
  state("claude", "Claude Code", {
    plan: "Claude Pro", fetchedAt, providerId: "claude", providerName: "Claude Code",
    metrics: [
      { name: "5-hour", kind: "session", usedPercent: 34, resetsAt: at(3, 12), durationMinutes: 300 },
      { name: "Weekly", kind: "rolling", usedPercent: 78, resetsAt: at(4 * 24 + 8), durationMinutes: 10_080 },
    ],
  }),
  state("copilot", "GitHub Copilot", {
    plan: "Pro", fetchedAt, providerId: "copilot", providerName: "GitHub Copilot", accountName: "octocat",
    metrics: [
      { name: "AI credits", kind: "monthly", usedPercent: 64, resetsAt: at(18 * 24 + 4), usageText: "540 of 1,500 left" },
      { name: "Chat", kind: "monthly", usedPercent: 0, resetsAt: at(18 * 24 + 4), remainingText: "UNLIMITED", usageText: "No monthly limit", isUnlimited: true },
      { name: "Completions", kind: "monthly", usedPercent: 0, resetsAt: at(18 * 24 + 4), remainingText: "UNLIMITED", usageText: "No monthly limit", isUnlimited: true },
    ],
  }),
  state("gemini", "Google Gemini", {
    plan: "Google AI Pro", fetchedAt, providerId: "gemini", providerName: "Google Gemini", accountName: "user@example.com",
    metrics: [
      { name: "Gemini Models (5-hour)", kind: "session", usedPercent: 14, resetsAt: at(4, 10), durationMinutes: 300 },
      { name: "Gemini Models (Weekly)", kind: "rolling", usedPercent: 48, resetsAt: at(5 * 24 + 14), durationMinutes: 10_080 },
      { name: "Claude and GPT models (5-hour)", kind: "session", usedPercent: 94, resetsAt: at(2, 30), durationMinutes: 300 },
      { name: "Claude and GPT models (Weekly)", kind: "rolling", usedPercent: 83, resetsAt: at(4 * 24 + 18), durationMinutes: 10_080 },
    ],
  }),
];
// Copilot starts collapsed so the preview also shows a collapsed card's one-line summary.
const expandedProviders = ["codex", "claude", "gemini"];

// ---------------------------------------------------------------------------------------------
// Load the compiled extension modules with a stub `vscode`.
// ---------------------------------------------------------------------------------------------

class FakeMarkdownString {
  constructor(value) { this.parts = value ? [{ kind: "markdown", value }] : []; }
  appendMarkdown(value) { this.parts.push({ kind: "markdown", value }); return this; }
  appendText(value) { this.parts.push({ kind: "text", value }); return this; }
  appendCodeblock(value) { this.parts.push({ kind: "code", value }); return this; }
}
const statusItems = [];
const disposable = { dispose() {} };
const fakeVscode = {
  MarkdownString: FakeMarkdownString,
  StatusBarAlignment: { Left: 1, Right: 2 },
  window: {
    createStatusBarItem(alignment, priority) {
      const item = { alignment, priority, shown: false, show() { this.shown = true; }, dispose() {} };
      statusItems.push(item);
      return item;
    },
  },
  commands: { executeCommand: async () => undefined },
  env: {},
  Uri: { parse: (value) => value },
};

const require = createRequire(import.meta.url);
const moduleLoader = require("node:module");
const originalLoad = moduleLoader._load;
moduleLoader._load = function load(request, parent, isMain) {
  return request === "vscode" ? fakeVscode : originalLoad.call(this, request, parent, isMain);
};
const distFile = (name) => {
  const path = join(extensionRoot, "dist", "src", name);
  if (!existsSync(path)) throw new Error(`${path} is missing. Run npm.cmd run compile first.`);
  return path;
};
const { UsageDashboardViewProvider } = require(distFile("dashboard-view.js"));
const { StatusBarController } = require(distFile("status-bar.js"));

// Times in the tooltip come from toLocaleTimeString in the extension host; pin them to en-US.
const toLocaleTimeString = Date.prototype.toLocaleTimeString;
Date.prototype.toLocaleTimeString = function localTime(locales, options) {
  return toLocaleTimeString.call(this, locales ?? "en-US", options);
};

function dashboardDocument() {
  let html = "";
  let payload;
  const view = {
    visible: true,
    webview: {
      options: {},
      set html(value) { html = value; },
      get html() { return html; },
      postMessage: async (message) => { payload = message; return true; },
      onDidReceiveMessage: () => disposable,
    },
    onDidChangeVisibility: () => disposable,
    onDidDispose: () => disposable,
  };
  const refreshService = { onDidUpdate: () => () => undefined, getStates: () => states, setVisible() {} };
  const provider = new UsageDashboardViewProvider(refreshService, () => 72, () => 90, {
    loadExpandedProviders: () => expandedProviders,
    metricDisplayMode: () => "all",
    quotaDisplay: () => "used",
  });
  provider.resolveWebviewView(view);
  provider.dispose();
  if (!html || !payload) throw new Error("The dashboard did not produce its HTML and first payload.");
  return { html, payload };
}

function statusBarItems() {
  const controller = new StatusBarController();
  controller.update(states, ["codex", "claude", "copilot", "gemini"], "used");
  return [...statusItems].filter((item) => item.shown).sort((left, right) => right.priority - left.priority);
}

// ---------------------------------------------------------------------------------------------
// VS Code chrome: Dark Modern colours (theme-defaults/themes/dark_modern.json, with the core
// defaults it leaves unset) and the default webview fonts on Windows.
// ---------------------------------------------------------------------------------------------

const themeVariables = {
  "--vscode-font-family": "\"Segoe WPC\", \"Segoe UI\", sans-serif",
  "--vscode-font-size": "13px",
  "--vscode-editor-font-family": "Consolas, \"Courier New\", monospace",
  "--vscode-foreground": "#CCCCCC",
  "--vscode-descriptionForeground": "#9D9D9D",
  "--vscode-sideBar-background": "#181818",
  "--vscode-editorWidget-background": "#202020",
  "--vscode-widget-border": "#313131",
  "--vscode-widget-shadow": "rgba(0, 0, 0, 0.36)",
  "--vscode-progressBar-background": "#0078D4",
  "--vscode-editorWarning-foreground": "#CCA700",
  "--vscode-errorForeground": "#F85149",
  "--vscode-list-hoverBackground": "#2A2D2E",
  "--vscode-focusBorder": "#0078D4",
  "--vscode-textCodeBlock-background": "#2B2B2B",
  "--vscode-button-secondaryForeground": "#CCCCCC",
  "--vscode-button-secondaryBackground": "#00000000",
  "--vscode-button-secondaryHoverBackground": "#2B2B2B",
  "--vscode-dropdown-background": "#313131",
  "--vscode-dropdown-foreground": "#CCCCCC",
  "--vscode-dropdown-border": "#3C3C3C",
  "--vscode-inputValidation-errorBackground": "#5A1D1D",
};
const rootStyle = `:root { ${Object.entries(themeVariables).map(([key, value]) => `${key}: ${value};`).join(" ")} }`;
const fontUrl = (path) => pathToFileURL(path).href;
const providerFont = `@font-face { font-family: "usageai-providers"; src: url("${fontUrl(join(extensionRoot, "media", "usageai-providers.woff"))}") format("woff"); }`;
const providerGlyphs = { "usageai-codex": "\uE001", "usageai-claude": "\uE002", "usageai-copilot": "\uE003", "usageai-gemini": "\uE004" };

function findCodiconFont() {
  const explicit = process.env.VSCODE_CODICON_PATH;
  if (explicit && existsSync(explicit)) return explicit;
  try {
    const command = process.platform === "win32" ? "where.exe" : "which";
    const codeCommand = execFileSync(command, ["code"], { encoding: "utf8" }).split(/\r?\n/).find(Boolean);
    if (!codeCommand) return undefined;
    const installRoot = resolve(dirname(codeCommand), "..");
    const candidates = [join(installRoot, "resources", "app", "out", "media", "codicon.ttf")];
    // Newer Windows installs keep the app in one commit-named folder beside bin\.
    for (const entry of readdirSync(installRoot, { withFileTypes: true })) {
      if (entry.isDirectory()) candidates.push(join(installRoot, entry.name, "resources", "app", "out", "media", "codicon.ttf"));
    }
    return candidates.find((candidate) => existsSync(candidate));
  } catch {
    return undefined;
  }
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" })[character]);
}

// The icon references the extension writes ($(usageai-claude)) as glyphs from its own icon font.
function withIcons(escaped) {
  return escaped.replace(/\$\(([a-z-]+)\)/g, (match, name) =>
    providerGlyphs[name] ? `<span class="glyph">${providerGlyphs[name]}</span>` : match);
}

// The tooltip markdown is a small, known subset: **bold**, *italic*, [links](command:…), paragraphs,
// icons, and one code block. Text parts were appended with appendText, so they are never markdown.
function renderTooltip(markdown) {
  const blocks = [[]];
  for (const part of markdown.parts) {
    if (part.kind === "code") {
      blocks.push({ code: part.value }, []);
      continue;
    }
    const pieces = part.value.split("\n\n");
    pieces.forEach((piece, index) => {
      if (index > 0) blocks.push([]);
      if (piece) blocks.at(-1).push({ kind: part.kind, value: piece });
    });
  }
  return blocks.filter((block) => block.code !== undefined || block.length).map((block) => {
    if (block.code !== undefined) return `<pre>${escapeHtml(block.code.replace(/^\n+|\n+$/g, ""))}</pre>`;
    const html = block.map(({ kind, value }) => {
      const escaped = escapeHtml(value);
      if (kind === "text") return escaped;
      return withIcons(escaped)
        .replace(/\[([^\]]+)\]\([^)]+\)/g, "<a>$1</a>")
        .replace(/\*\*/g, "\u0001")
        .replace(/\*([^*]+)\*/g, "<em>$1</em>");
    }).join("");
    let bold = false;
    return `<p>${html.replace(/\u0001/g, () => ((bold = !bold) ? "<strong>" : "</strong>"))}</p>`;
  }).join("");
}

function dashboardPage(codiconFont) {
  const { html, payload } = dashboardDocument();
  const fixedNow = now.getTime();
  const bootstrap = `<script>
    Date.now = () => ${fixedNow};
    window.acquireVsCodeApi = () => ({
      postMessage(message) {
        if (message && message.type === "ready") window.dispatchEvent(new MessageEvent("message", { data: ${JSON.stringify(payload)} }));
      },
      getState() { return undefined; },
      setState() {},
    });
  </script>`;
  const chrome = `<style>
    ${rootStyle}
    ${codiconFont ? `@font-face { font-family: "codicon"; src: url("${fontUrl(codiconFont)}") format("truetype"); }` : ""}
    html { background: #181818; }
    .pane-title { display: flex; align-items: center; height: 35px; margin: -10px -10px 8px; padding: 0 8px 0 20px; color: #CCCCCC; font-size: 11px; font-weight: 400; letter-spacing: 0; }
    .pane-title span { flex: 1; text-transform: uppercase; }
    .pane-title i { width: 22px; height: 22px; display: inline-flex; align-items: center; justify-content: center; font: normal 16px/1 codicon; font-style: normal; color: #CCCCCC; }
  </style>`;
  // Codicon code points: refresh, gear, ellipsis (Views and More Actions).
  const titleIcons = codiconFont ? "<i>\uEB37</i><i>\uEB51</i><i>\uEA7C</i>" : "";
  return html
    .replace(/<meta http-equiv="Content-Security-Policy"[^>]*>/, "")
    .replace("<head>", `<head>${bootstrap}${chrome}`)
    .replace("<body>", `<body><div class="pane-title"><span>UsageAI</span>${titleIcons}</div>`);
}

function statusBarPage() {
  const items = statusBarItems();
  const hovered = items.find((item) => item.name === "UsageAI: Claude");
  if (!hovered) throw new Error("The Claude Status Bar item was not created.");
  const itemHtml = items.map((item) =>
    `<span class="item${item === hovered ? " hovered" : ""}">${withIcons(escapeHtml(item.text))}</span>`).join("");
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><style>
    ${rootStyle}
    ${providerFont}
    html, body { margin: 0; background: #1F1F1F; }
    body { font: 12px/22px var(--vscode-font-family); color: #CCCCCC; }
    #stage { position: relative; width: 520px; }
    .statusbar { position: absolute; left: 0; right: 0; bottom: 0; height: 22px; display: flex; padding-left: 7px; border-top: 1px solid #2B2B2B; background: #181818; }
    .item { display: inline-flex; align-items: center; gap: 4px; padding: 0 5px; margin-right: 3px; white-space: pre; }
    .item.hovered { background: rgba(241, 241, 241, 0.2); border-radius: 0; }
    .glyph { font: 14px/1 "usageai-providers"; position: relative; top: 1px; }
    .hover { position: absolute; bottom: 27px; padding: 4px 8px; border: 1px solid #454545; border-radius: 3px; background: #202020; color: #CCCCCC; box-shadow: 0 2px 8px var(--vscode-widget-shadow); font: 13px/19px var(--vscode-font-family); white-space: nowrap; }
    .hover p { margin: 8px 0; }
    .hover p:first-child { margin-top: 0; }
    .hover p:last-child { margin-bottom: 0; }
    .hover strong { font-weight: 600; }
    .hover .glyph { font-size: 14px; margin-right: 4px; }
    .hover pre { margin: 8px 0; font: 14px/19px var(--vscode-editor-font-family); white-space: pre; }
    .hover a { color: #4DAAFC; text-decoration: none; }
  </style></head><body><div id="stage">
    <div class="hover" id="hover">${renderTooltip(hovered.tooltip)}</div>
    <div class="statusbar">${itemHtml}</div>
  </div><script>
    const item = document.querySelector(".item.hovered"); const hover = document.getElementById("hover");
    const left = item.offsetLeft + item.offsetWidth / 2 - hover.offsetWidth / 2;
    hover.style.left = Math.max(6, left) + "px";
    // Room for the hover, the 5px gap above the item, the bar, and a little editor above it all.
    document.getElementById("stage").style.height = (hover.offsetHeight + 27 + 14) + "px";
  </script></body></html>`;
}

// ---------------------------------------------------------------------------------------------
// Headless browser over the DevTools protocol.
// ---------------------------------------------------------------------------------------------

function findBrowser() {
  const candidates = [
    process.env.EDGE_PATH,
    process.env.CHROME_PATH,
    "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
    "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
    "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
    "/usr/bin/microsoft-edge",
    "/usr/bin/google-chrome",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
  ];
  const found = candidates.find((candidate) => candidate && existsSync(candidate));
  if (!found) throw new Error("No Edge or Chrome found. Set EDGE_PATH or CHROME_PATH.");
  return found;
}

async function waitFor(check, what, timeoutMs = 15_000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await check();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`Timed out waiting for ${what}.`);
    await new Promise((done) => setTimeout(done, 100));
  }
}

async function withBrowser(work) {
  const profile = mkdtempSync(join(tmpdir(), "usageai-preview-"));
  const browser = spawn(findBrowser(), [
    "--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profile}`, "--lang=en-US",
    "--hide-scrollbars", "--disable-lcd-text", "--allow-file-access-from-files", "--no-first-run", "--no-default-browser-check",
    "about:blank",
  ], { stdio: "ignore" });
  try {
    const portFile = join(profile, "DevToolsActivePort");
    const port = await waitFor(() => existsSync(portFile) && readFileSync(portFile, "utf8").split(/\r?\n/)[0], "the DevTools port");
    const target = await waitFor(async () => {
      const list = await fetch(`http://127.0.0.1:${port}/json/list`).then((response) => response.json()).catch(() => []);
      return list.find((entry) => entry.type === "page");
    }, "a page target");
    const socket = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((done, fail) => { socket.onopen = done; socket.onerror = fail; });
    let nextId = 0;
    const pending = new Map();
    const listeners = new Set();
    socket.onmessage = (event) => {
      const message = JSON.parse(event.data);
      if (message.id && pending.has(message.id)) {
        const { done, fail } = pending.get(message.id);
        pending.delete(message.id);
        if (message.error) fail(new Error(message.error.message)); else done(message.result);
      } else {
        for (const listener of listeners) listener(message);
      }
    };
    const send = (method, params = {}) => new Promise((done, fail) => {
      const id = ++nextId;
      pending.set(id, { done, fail });
      socket.send(JSON.stringify({ id, method, params }));
    });
    const loaded = () => new Promise((done) => {
      const listener = (message) => { if (message.method === "Page.loadEventFired") { listeners.delete(listener); done(); } };
      listeners.add(listener);
    });
    await send("Page.enable");
    await send("Emulation.setLocaleOverride", { locale: "en-US" });
    try {
      return await work({ send, loaded });
    } finally {
      socket.close();
    }
  } finally {
    browser.kill();
    await new Promise((done) => setTimeout(done, 300));
    rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
}

async function capture(session, file, width, html, measureExpression) {
  const page = join(tmpdir(), `usageai-preview-${process.pid}-${Date.now()}.html`);
  writeFileSync(page, html, "utf8");
  try {
    await session.send("Emulation.setDeviceMetricsOverride", { width, height: 2000, deviceScaleFactor: scale, mobile: false });
    const loaded = session.loaded();
    await session.send("Page.navigate", { url: pathToFileURL(page).href });
    await loaded;
    await session.send("Runtime.evaluate", { expression: "document.fonts.ready.then(() => true)", awaitPromise: true });
    const { result } = await session.send("Runtime.evaluate", { expression: measureExpression, returnByValue: true });
    const height = Math.ceil(result.value);
    const shot = await session.send("Page.captureScreenshot", {
      format: "png",
      clip: { x: 0, y: 0, width, height, scale: 1 },
      captureBeyondViewport: true,
    });
    writeFileSync(file, Buffer.from(shot.data, "base64"));
    console.log(`${file} (${width * scale}x${height * scale})`);
  } finally {
    rmSync(page, { force: true });
  }
}

mkdirSync(outDir, { recursive: true });
const codiconFont = findCodiconFont();
if (!codiconFont) console.warn("Codicon font not found; the view title renders without its icons. Set VSCODE_CODICON_PATH.");
await withBrowser(async (session) => {
  await capture(session, join(outDir, "dashboard-preview.png"), 400, dashboardPage(codiconFont),
    "document.getElementById('providers').getBoundingClientRect().bottom + 10");
  await capture(session, join(outDir, "status-bar-preview.png"), 520, statusBarPage(),
    "document.getElementById('stage').offsetHeight");
});
moduleLoader._load = originalLoad;
