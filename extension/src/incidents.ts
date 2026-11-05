import { requestJson } from "./security";
import { getArray, getObject, getString, isObject } from "./providers/json";

/** How badly a provider's own status page says its service is affected, mildest first. */
export type IncidentSeverity = "maintenance" | "degraded" | "partial_outage" | "major_outage";

const severityOrder: readonly IncidentSeverity[] = ["maintenance", "degraded", "partial_outage", "major_outage"];
const severityLabel: Readonly<Record<IncidentSeverity, string>> = {
  maintenance: "Maintenance",
  degraded: "Degraded",
  partial_outage: "Partial outage",
  major_outage: "Outage",
};

/** An ongoing incident a provider reports on its public status page. Mirrors the desktop `ProviderIncident`. */
export interface ProviderIncident {
  readonly providerId: string;
  readonly severity: IncidentSeverity;
  readonly title: string;
  readonly page: string;
}

export function incidentSummary(incident: ProviderIncident): string {
  return `${severityLabel[incident.severity]}: ${incident.title}`;
}

export function isOutage(incident: ProviderIncident): boolean {
  return incident.severity === "partial_outage" || incident.severity === "major_outage";
}

/** A provider's status page: a Statuspage page with a component allowlist, or an incident.io page with a group. */
export interface IncidentSource {
  readonly providerId: string;
  readonly page: string;
  readonly feed: string;
  readonly components?: readonly string[];
  readonly group?: string;
}

export const incidentSources: readonly IncidentSource[] = [
  { providerId: "claude", page: "https://status.claude.com/", feed: "https://status.claude.com/api/v2/summary.json", components: ["claude.ai", "Claude API", "Claude Code"] },
  { providerId: "copilot", page: "https://www.githubstatus.com/", feed: "https://www.githubstatus.com/api/v2/summary.json", components: ["Copilot"] },
  { providerId: "codex", page: "https://status.openai.com/", feed: "https://status.openai.com/proxy/status.openai.com", group: "Codex" },
];

export const incidentCheckIntervalMs = 5 * 60_000;
const maxTitleCharacters = 120;

function worse(left: IncidentSeverity | undefined, right: IncidentSeverity | undefined): IncidentSeverity | undefined {
  if (!left) return right;
  if (!right) return left;
  return severityOrder.indexOf(right) > severityOrder.indexOf(left) ? right : left;
}

export function componentSeverity(status: string | undefined): IncidentSeverity | undefined {
  switch (status) {
    case "degraded_performance": return "degraded";
    case "partial_outage": return "partial_outage";
    case "major_outage": case "full_outage": return "major_outage";
    case "under_maintenance": return "maintenance";
    default: return undefined;
  }
}

export function impactSeverity(impact: string | undefined): IncidentSeverity | undefined {
  switch (impact) {
    case "minor": return "degraded";
    case "major": return "partial_outage";
    case "critical": return "major_outage";
    case "maintenance": return "maintenance";
    default: return undefined;
  }
}

function cleanTitle(title: string | undefined): string | undefined {
  const cleaned = (title ?? "").replace(/[\u0000-\u001f\u007f]/g, "").trim();
  if (!cleaned) return undefined;
  return cleaned.length > maxTitleCharacters ? `${cleaned.slice(0, maxTitleCharacters).trimEnd()}…` : cleaned;
}

/**
 * A Statuspage `summary.json`: the worst of the allowlisted components' own status and of the
 * unresolved incidents (and in-progress maintenance) that touch them, titled after the first such
 * incident. Mirrors the desktop `ParseStatuspage`.
 */
export function parseStatuspage(root: unknown, providerId: string, page: string, allowlist: readonly string[]): ProviderIncident | undefined {
  const counts = (name: string | undefined): boolean =>
    name !== undefined && allowlist.some((prefix) => name.toLowerCase().startsWith(prefix.toLowerCase()));
  let worst: IncidentSeverity | undefined;
  let title: string | undefined;
  let link: string | undefined;
  const consider = (severity: IncidentSeverity, candidate: string | undefined, id: string | undefined): void => {
    worst = worse(worst, severity);
    if (!title && cleanTitle(candidate)) {
      title = cleanTitle(candidate);
      link = id && id.length <= 64 && /^[A-Za-z0-9]+$/.test(id) ? new URL(`incidents/${id}`, page).toString() : undefined;
    }
  };

  for (const [property, planned] of [["incidents", false], ["scheduled_maintenances", true]] as const) {
    for (const entry of getArray(root, property) ?? []) {
      const status = getString(entry, "status");
      if (!status || status === "resolved" || (planned && status !== "in_progress")) {
        continue;
      }
      const listed = getArray(entry, "components") ?? [];
      const affected = listed.filter((component) => counts(getString(component, "name")));
      // Only an incident that names an allowlisted component counts: one opened before components are
      // attached may be about any product, and the component status reports it once attributed.
      if (affected.length === 0) {
        continue;
      }
      const severity = planned
        ? "maintenance"
        : affected.map((component) => componentSeverity(getString(component, "status"))).reduce(worse, undefined)
          ?? impactSeverity(getString(entry, "impact"));
      if (severity) {
        consider(severity, getString(entry, "name"), getString(entry, "id"));
      }
    }
  }

  for (const component of getArray(root, "components") ?? []) {
    const name = getString(component, "name");
    const severity = componentSeverity(getString(component, "status"));
    if (counts(name) && severity) {
      consider(severity, undefined, undefined);
      title ??= cleanTitle(`${name}: ${severityLabel[severity].toLowerCase()}`);
    }
  }

  return worst ? { providerId, severity: worst, title: title ?? severityLabel[worst], page: link ?? page } : undefined;
}

/**
 * An incident.io proxy feed: the components of one group, such as "Codex", whose affected status is
 * not operational, titled after the ongoing incident that names the group. Mirrors the desktop.
 */
export function parseIncidentIo(root: unknown, providerId: string, page: string, group: string): ProviderIncident | undefined {
  const summary = getObject(root, "summary");
  if (!summary) {
    return undefined;
  }
  const members = new Set<string>();
  for (const item of getArray(getObject(summary, "structure"), "items") ?? []) {
    const entry = getObject(item, "group");
    if (getString(entry, "name") !== group) {
      continue;
    }
    for (const component of getArray(entry, "components") ?? []) {
      const id = getString(component, "component_id");
      if (id) members.add(id);
    }
  }
  let worst: IncidentSeverity | undefined;
  for (const component of getArray(summary, "affected_components") ?? []) {
    const id = getString(component, "component_id");
    if (id && members.has(id)) {
      worst = worse(worst, componentSeverity(getString(component, "status")));
    }
  }
  if (!worst) {
    return undefined;
  }
  const names = (getArray(summary, "ongoing_incidents") ?? [])
    .map((incident) => getString(incident, "name"))
    .filter((name): name is string => Boolean(name));
  const title = cleanTitle(names.find((name) => name.toLowerCase().includes(group.toLowerCase())) ?? names[0]);
  return { providerId, severity: worst, title: title ?? `${group}: ${severityLabel[worst].toLowerCase()}`, page };
}

/**
 * Reads providers' public status pages, at most every five minutes and with no credential, so a
 * card that went stale can be told apart from a provider that is down. A failed check keeps the last
 * answer. Mirrors the desktop `ProviderIncidentService`.
 */
export class ProviderIncidentService {
  private current = new Map<string, ProviderIncident>();
  private nextCheckAt = 0;
  private running = false;
  private generation = 0;
  private readonly listeners = new Set<() => void>();

  public constructor(private readonly sources: readonly IncidentSource[] = incidentSources) {}

  public onDidChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  public get(providerId: string): ProviderIncident | undefined {
    return this.current.get(providerId);
  }

  public async refreshIfDue(enabled: boolean, now = Date.now()): Promise<void> {
    if (!enabled) {
      // A check still in flight sees the new generation and drops its result, so turning the setting
      // off cannot be undone by an answer that arrives a moment later.
      this.generation += 1;
      this.nextCheckAt = 0;
      this.publish(new Map());
      return;
    }
    if (this.running || now < this.nextCheckAt) {
      return;
    }
    this.running = true;
    this.nextCheckAt = now + incidentCheckIntervalMs;
    const generation = this.generation;
    try {
      const results = await Promise.all(this.sources.map(async (source) => ({ source, ...(await this.check(source)) })));
      if (generation !== this.generation) {
        return;
      }
      const next = new Map(this.current);
      for (const { source, answered, incident } of results) {
        if (!answered) continue;
        if (incident) next.set(source.providerId, incident);
        else next.delete(source.providerId);
      }
      this.publish(next);
    } finally {
      this.running = false;
    }
  }

  private async check(source: IncidentSource): Promise<{ readonly answered: boolean; readonly incident?: ProviderIncident }> {
    try {
      const response = await requestJson(source.feed, { allowedHosts: [new URL(source.feed).host], timeoutMs: 10_000 });
      if (response.status < 200 || response.status >= 300 || !isObject(response.data)) {
        return { answered: false };
      }
      const incident = source.group
        ? parseIncidentIo(response.data, source.providerId, source.page, source.group)
        : parseStatuspage(response.data, source.providerId, source.page, source.components ?? []);
      return incident ? { answered: true, incident } : { answered: true };
    } catch {
      return { answered: false };
    }
  }

  private publish(next: Map<string, ProviderIncident>): void {
    const changed = next.size !== this.current.size ||
      [...next].some(([id, incident]) => JSON.stringify(this.current.get(id)) !== JSON.stringify(incident));
    this.current = next;
    if (changed) {
      for (const listener of this.listeners) listener();
    }
  }
}
