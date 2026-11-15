import assert from "node:assert/strict";
import test from "node:test";
import {
  isProjectLinkId,
  newIssueUrl,
  projectLinkIds,
  projectLinks,
  repositoryUrl,
  reviewStoreFor,
  reviewStoreNames,
} from "../src/project-links";

/**
 * What the browser is handed for a link opened through `vscode.env.openExternal`: VS Code reads the
 * link into its own URI, which decodes every component, then writes it out escaping only `#` and
 * `?` and passes the result through `encodeURI`.
 */
function throughVsCode(url: string): string {
  const [, head, query, fragment] = /^([^?#]*)(?:\?([^#]*))?(?:#(.*))?$/.exec(url) ?? [];
  const minimal = (part: string) => decodeURIComponent(part).replace(/#/g, "%23").replace(/\?/g, "%3F");
  return encodeURI(`${head ?? ""}${query === undefined ? "" : `?${minimal(query)}`}${fragment === undefined ? "" : `#${minimal(fragment)}`}`);
}

test("the review link opens the store the editor installs from", () => {
  // Microsoft's own builds use the Visual Studio Marketplace.
  assert.equal(reviewStoreFor("Visual Studio Code", "vscode"), "marketplace");
  assert.equal(reviewStoreFor("Visual Studio Code - Insiders", "vscode-insiders"), "marketplace");
  assert.equal(reviewStoreFor("  visual studio code  ", undefined), "marketplace");
  // Every other editor built on VS Code installs from Open VSX.
  assert.equal(reviewStoreFor("Antigravity", "antigravity"), "openvsx");
  assert.equal(reviewStoreFor("Cursor", "cursor"), "openvsx");
  assert.equal(reviewStoreFor("VSCodium", "vscodium"), "openvsx");
  assert.equal(reviewStoreFor("Code - OSS", "code-oss"), "openvsx");
  assert.equal(reviewStoreFor("Visual Studio Codex", "vscode"), "openvsx");
  // The name decides: a fork that kept the `vscode` scheme is still an Open VSX editor.
  assert.equal(reviewStoreFor("Some Editor", "vscode"), "openvsx");
  // The scheme is the answer only when the editor reports no name.
  assert.equal(reviewStoreFor(undefined, "vscode"), "marketplace");
  assert.equal(reviewStoreFor("", "VSCode-Insiders"), "marketplace");
  assert.equal(reviewStoreFor("  ", "vscode-exploration"), "marketplace");
  assert.equal(reviewStoreFor(undefined, "vscodium"), "openvsx");
  assert.equal(reviewStoreFor(undefined, undefined), "openvsx");
  assert.equal(reviewStoreFor(42, { scheme: "vscode" }), "openvsx");

  assert.deepEqual(reviewStoreNames, { marketplace: "Visual Studio Marketplace", openvsx: "Open VSX" });
  const code = projectLinks({ appName: "Visual Studio Code", uriScheme: "vscode" });
  assert.equal(code.reviewStore, "marketplace");
  assert.equal(code.urls.review, "https://marketplace.visualstudio.com/items?itemName=vladikogan.usageai&ssr=false#review-details");
  const antigravity = projectLinks({ appName: "Antigravity", uriScheme: "antigravity" });
  assert.equal(antigravity.reviewStore, "openvsx");
  assert.equal(antigravity.urls.review, "https://open-vsx.org/extension/vladikogan/usageai/reviews");
  assert.equal(antigravity.urls.star, repositoryUrl);
  assert.equal(repositoryUrl, "https://github.com/VladiKogan/UsageAI");
  assert.equal(projectLinks().reviewStore, "openvsx");
});

test("only the three named project links are accepted", () => {
  assert.deepEqual(projectLinkIds, ["star", "report", "review"]);
  for (const link of projectLinkIds) assert.equal(isProjectLinkId(link), true);
  for (const value of ["", "Star", "constructor", "toString", "https://example.com", 0, null, undefined, ["star"], { link: "star" }]) {
    assert.equal(isProjectLinkId(value), false);
  }
});

test("a new issue starts with the versions and nothing about the account", () => {
  const url = newIssueUrl({
    extensionVersion: "0.1.27",
    appName: "Antigravity",
    vscodeVersion: "1.104.0",
    os: "Windows_NT 10.0.26100 (x64)",
  });
  assert.ok(url.startsWith("https://github.com/VladiKogan/UsageAI/issues/new?body="));
  const body = new URL(url).searchParams.get("body");
  assert.equal(body, [
    "**UsageAI extension version:** 0.1.27",
    "**Editor:** Antigravity (VS Code 1.104.0)",
    "**OS:** Windows_NT 10.0.26100 (x64)",
    "",
    "**What happened**",
    "",
    "",
    "**What you expected**",
    "",
    "",
    "> If you paste a card's error text or a screenshot, check it first: it can include your account email.",
    "",
  ].join("\n"));

  // A remote window is named; a local one adds no line.
  const remote = new URL(newIssueUrl({ remoteName: "ssh-remote" })).searchParams.get("body") ?? "";
  assert.match(remote, /\n\*\*Remote window:\*\* ssh-remote\n\n/);
  assert.doesNotMatch(body ?? "", /Remote window/);
  assert.doesNotMatch(new URL(newIssueUrl({ remoteName: "  " })).searchParams.get("body") ?? "", /Remote window/);

  // Values that are missing or not text read "unknown" instead of failing.
  const empty = new URL(newIssueUrl()).searchParams.get("body") ?? "";
  assert.match(empty, /^\*\*UsageAI extension version:\*\* unknown\n\*\*Editor:\*\* unknown \(VS Code unknown\)\n\*\*OS:\*\* unknown\n/);
  assert.match(new URL(newIssueUrl({ extensionVersion: 7, os: "###" })).searchParams.get("body") ?? "", /version:\*\* unknown\n[^\n]*\n\*\*OS:\*\* unknown\n/);
});

test("the issue link survives VS Code rewriting it on the way to the browser", () => {
  // An editor's name is whatever its maker chose, so it can hold the very characters that end a
  // query or change its meaning; they are dropped, and a long value is cut.
  const url = newIssueUrl({
    extensionVersion: "0.1.27+build#3",
    appName: "Edit & Run? 100% C++ #1 ",
    vscodeVersion: "1.104.0",
    os: `Linux 6.8 (x64) ${"x".repeat(200)}`,
    remoteName: "wsl+Ubuntu-24.04",
  });
  const body = new URL(url).searchParams.get("body") ?? "";
  assert.doesNotMatch(body, /[#?&+]/);
  const lines = body.split("\n");
  assert.equal(lines[0], "**UsageAI extension version:** 0.1.27build3");
  assert.equal(lines[1], "**Editor:** Edit  Run 100 C 1 (VS Code 1.104.0)");
  assert.equal(lines[2]?.length, "**OS:** ".length + 80);
  assert.equal(lines[3], "**Remote window:** wslUbuntu-24.04");

  // The same body reaches GitHub whether the link is opened as written or as VS Code rewrites it.
  const rewritten = new URL(throughVsCode(url));
  assert.equal(rewritten.searchParams.get("body"), body);
  assert.deepEqual([...rewritten.searchParams.keys()], ["body"]);
  assert.equal(rewritten.hash, "");
  assert.equal(rewritten.pathname, "/VladiKogan/UsageAI/issues/new");

  // The store and repository links pass through unchanged.
  for (const editor of [{ appName: "Visual Studio Code" }, { appName: "Antigravity" }]) {
    const links = projectLinks(editor);
    assert.equal(throughVsCode(links.urls.review), links.urls.review);
    assert.equal(throughVsCode(links.urls.star), links.urls.star);
    assert.equal(new URL(throughVsCode(links.urls.report)).searchParams.get("body"), new URL(links.urls.report).searchParams.get("body"));
  }
});
