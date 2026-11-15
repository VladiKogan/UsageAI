/**
 * The project's own pages, which the dashboard's footer links to: the repository (where a star is
 * given), a new issue, and the extension's reviews on the store this editor installs from. Nothing
 * is fetched and nothing is asked unprompted: each link only opens the browser, and the star,
 * issue or review is written there, by the person, signed in on that site. Mirrors the desktop's
 * `ProjectLinks`.
 */

export type ProjectLinkId = "star" | "report" | "review";
export const projectLinkIds: readonly ProjectLinkId[] = ["star", "report", "review"];

/** Where an editor installs extensions from, and so where a review of this one is written. */
export type ReviewStore = "marketplace" | "openvsx";
export const reviewStoreNames: Readonly<Record<ReviewStore, string>> = {
  marketplace: "Visual Studio Marketplace",
  openvsx: "Open VSX",
};

export const repositoryUrl = "https://github.com/VladiKogan/UsageAI";
const reviewUrls: Readonly<Record<ReviewStore, string>> = {
  // `ssr=false` is what the Marketplace's own Rating & Review tab links to; without it the page
  // opens on the overview.
  marketplace: "https://marketplace.visualstudio.com/items?itemName=vladikogan.usageai&ssr=false#review-details",
  openvsx: "https://open-vsx.org/extension/vladikogan/usageai/reviews",
};

export function isProjectLinkId(value: unknown): value is ProjectLinkId {
  return typeof value === "string" && (projectLinkIds as readonly string[]).includes(value);
}

/**
 * Only Microsoft's own builds install from the Visual Studio Marketplace; Antigravity, Cursor,
 * VSCodium and every other editor built on VS Code use Open VSX. The editor's name decides, and
 * its link scheme only when it reports no name, so a fork that kept the `vscode` scheme is still
 * sent to the store it installs from.
 */
export function reviewStoreFor(appName: unknown, uriScheme: unknown): ReviewStore {
  const name = typeof appName === "string" ? appName.trim() : "";
  if (name) {
    return /^Visual Studio Code\b/i.test(name) ? "marketplace" : "openvsx";
  }
  return typeof uriScheme === "string" && /^vscode(-insiders|-exploration)?$/i.test(uriScheme.trim())
    ? "marketplace"
    : "openvsx";
}

export interface IssueDetails {
  readonly extensionVersion?: unknown;
  /** The editor's name, such as "Antigravity". */
  readonly appName?: unknown;
  /** The VS Code version the editor is built on. */
  readonly vscodeVersion?: unknown;
  /** The operating system the extension runs on, such as "Windows_NT 10.0.26100 (x64)". */
  readonly os?: unknown;
  /** The kind of remote window ("wsl", "ssh-remote"), when there is one. */
  readonly remoteName?: unknown;
}

/**
 * One value of the issue's opening lines. VS Code reads a link into its own URI and writes it out
 * again before the browser gets it (`encodeURI(uri.toString(true))`), and `#`, `?`, `&` and `+` do
 * not survive that inside a query: the first two arrive escaped twice, the others end the value or
 * turn into a space. So a value keeps only letters, digits, spaces and plain punctuation, and the
 * fixed text of the body avoids the same characters.
 */
function issueValue(value: unknown): string {
  const text = typeof value === "string" ? value.replace(/[^A-Za-z0-9 ._()/:,-]/g, "").trim().slice(0, 80) : "";
  return text || "unknown";
}

/**
 * A new issue with the body started: the versions, two headings to fill in, and a warning before
 * pasting anything from a card. Nothing about providers, accounts or paths is added — the person
 * writes and sends it themselves.
 */
export function newIssueUrl(details: IssueDetails = {}): string {
  const lines = [
    `**UsageAI extension version:** ${issueValue(details.extensionVersion)}`,
    `**Editor:** ${issueValue(details.appName)} (VS Code ${issueValue(details.vscodeVersion)})`,
    `**OS:** ${issueValue(details.os)}`,
  ];
  if (typeof details.remoteName === "string" && details.remoteName.trim()) {
    lines.push(`**Remote window:** ${issueValue(details.remoteName)}`);
  }
  const body = `${lines.join("\n")}\n\n`
    + "**What happened**\n\n\n"
    + "**What you expected**\n\n\n"
    + "> If you paste a card's error text or a screenshot, check it first: it can include your account email.\n";
  return `${repositoryUrl}/issues/new?body=${encodeURIComponent(body)}`;
}

/** The three links for this editor, and the store its review link opens. */
export interface ProjectLinks {
  readonly reviewStore: ReviewStore;
  readonly urls: Readonly<Record<ProjectLinkId, string>>;
}

export function projectLinks(editor: IssueDetails & { readonly uriScheme?: unknown } = {}): ProjectLinks {
  const reviewStore = reviewStoreFor(editor.appName, editor.uriScheme);
  return {
    reviewStore,
    urls: { star: repositoryUrl, report: newIssueUrl(editor), review: reviewUrls[reviewStore] },
  };
}
