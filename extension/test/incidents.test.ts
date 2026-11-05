import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test, { mock } from "node:test";
import {
  incidentSources,
  incidentSummary,
  isOutage,
  parseIncidentIo,
  parseStatuspage,
  ProviderIncidentService,
} from "../src/incidents";
import { UsageRefreshService } from "../src/refresh-service";

const localRequire = createRequire(__filename);
const security = localRequire("../src/security") as typeof import("../src/security");

const claudeComponents = ["claude.ai", "Claude API", "Claude Code"];
const claudePage = "https://status.claude.com/";

test("Statuspage parsing counts only the allowlisted components", () => {
  assert.equal(parseStatuspage({
    components: [{ name: "claude.ai", status: "operational" }],
    incidents: [],
    scheduled_maintenances: [],
  }, "claude", claudePage, claudeComponents), undefined);

  const incident = parseStatuspage({
    components: [{ name: "Claude Code", status: "degraded_performance" }, { name: "Claude Cowork", status: "major_outage" }],
    incidents: [
      { id: "abc123", name: "Cowork is down", status: "investigating", impact: "critical", components: [{ name: "Claude Cowork", status: "major_outage" }] },
      { id: "def456", name: "Elevated errors on Claude Code", status: "identified", impact: "minor", components: [{ name: "Claude Code", status: "degraded_performance" }] },
      { id: "old", name: "Resolved yesterday", status: "resolved", impact: "critical", components: [] },
    ],
    scheduled_maintenances: [],
  }, "claude", claudePage, claudeComponents);
  assert.deepEqual(incident, {
    providerId: "claude",
    severity: "degraded",
    title: "Elevated errors on Claude Code",
    page: "https://status.claude.com/incidents/def456",
  });
  assert.equal(incidentSummary(incident!), "Degraded: Elevated errors on Claude Code");
  assert.equal(isOutage(incident!), false);

  const componentOnly = parseStatuspage({
    components: [{ name: "Claude API (api.anthropic.com)", status: "partial_outage" }],
    incidents: [],
    scheduled_maintenances: [],
  }, "claude", claudePage, claudeComponents)!;
  assert.equal(isOutage(componentOnly), true);
  assert.equal(componentOnly.title, "Claude API (api.anthropic.com): partial outage");

  // An incident opened before components are attached may be about any product, so it is not pinned
  // on Copilot ("Incident with Actions" usually starts like this).
  assert.equal(parseStatuspage({
    components: [{ name: "Copilot", status: "operational" }],
    incidents: [{ id: "x1", name: "Incident with Actions", status: "investigating", impact: "minor", components: [] }],
    scheduled_maintenances: [],
  }, "copilot", "https://www.githubstatus.com/", ["Copilot"]), undefined);

  assert.equal(parseStatuspage({
    components: [],
    incidents: [],
    scheduled_maintenances: [{ id: "m1", name: "Copilot maintenance", status: "in_progress", components: [{ name: "Copilot", status: "under_maintenance" }] }],
  }, "copilot", "https://www.githubstatus.com/", ["Copilot"])?.severity, "maintenance");
  assert.equal(parseStatuspage({
    components: [],
    incidents: [],
    scheduled_maintenances: [{ name: "Later", status: "scheduled", components: [{ name: "Copilot", status: "operational" }] }],
  }, "copilot", "https://www.githubstatus.com/", ["Copilot"]), undefined);
});

test("incident.io parsing counts only the named component group", () => {
  const feed = {
    summary: {
      structure: { items: [
        { group: { name: "APIs", components: [{ component_id: "api-1" }] } },
        { group: { name: "Codex", components: [{ component_id: "codex-1" }, { component_id: "codex-2" }] } },
      ] },
      affected_components: [{ component_id: "api-1", status: "full_outage" }, { component_id: "codex-2", status: "degraded_performance" }],
      ongoing_incidents: [{ name: "Responses API outage" }, { name: "Codex tasks are slow" }],
    },
  };
  const codex = parseIncidentIo(feed, "codex", "https://status.openai.com/", "Codex")!;
  assert.equal(codex.severity, "degraded");
  assert.equal(codex.title, "Codex tasks are slow");
  assert.equal(parseIncidentIo({ summary: { structure: { items: [] }, affected_components: [] } }, "codex", "https://status.openai.com/", "Codex"), undefined);
  assert.equal(parseIncidentIo({}, "codex", "https://status.openai.com/", "Codex"), undefined);
});

test("the incident service polls rarely, keeps the last answer, and clears when turned off", async () => {
  const copilot = incidentSources.filter((source) => source.providerId === "copilot");
  let requests = 0;
  let answer: { status: number; data: unknown } = {
    status: 200,
    data: { components: [{ name: "Copilot", status: "degraded_performance" }], incidents: [], scheduled_maintenances: [] },
  };
  mock.method(security, "requestJson", (async (url, options) => {
    requests += 1;
    assert.equal(new URL(String(url)).host, "www.githubstatus.com");
    assert.deepEqual(options.allowedHosts, ["www.githubstatus.com"]);
    assert.equal(options.headers?.Authorization, undefined);
    return { status: answer.status, headers: {}, data: answer.data };
  }) as typeof security.requestJson);
  try {
    const service = new ProviderIncidentService(copilot);
    let changes = 0;
    service.onDidChange(() => { changes += 1; });
    const now = Date.now();
    await service.refreshIfDue(true, now);
    assert.equal(service.get("copilot")?.severity, "degraded");
    assert.equal(changes, 1);
    await service.refreshIfDue(true, now + 60_000);
    assert.equal(requests, 1);
    answer = { status: 503, data: {} };
    await service.refreshIfDue(true, now + 6 * 60_000);
    assert.equal(requests, 2);
    assert.equal(service.get("copilot")?.severity, "degraded");
    assert.equal(changes, 1);
    await service.refreshIfDue(false, now + 7 * 60_000);
    assert.equal(service.get("copilot"), undefined);
    assert.equal(changes, 2);
  } finally {
    mock.restoreAll();
  }
});

test("turning incidents off while a check is in flight is not undone by its late answer", async () => {
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  mock.method(security, "requestJson", (async () => {
    await gate;
    return { status: 200, headers: {}, data: { components: [{ name: "Copilot", status: "degraded_performance" }], incidents: [], scheduled_maintenances: [] } };
  }) as typeof security.requestJson);
  try {
    const service = new ProviderIncidentService(incidentSources.filter((source) => source.providerId === "copilot"));
    const inFlight = service.refreshIfDue(true, Date.now());
    await service.refreshIfDue(false, Date.now());
    release();
    await inFlight;
    assert.equal(service.get("copilot"), undefined);
  } finally {
    mock.restoreAll();
  }
});

test("the refresh service attaches each provider's incident to its published state", () => {
  const service = new UsageRefreshService({
    clients: [{ id: "claude", displayName: "Claude Code", signInCommand: "claude", accountUrl: "https://claude.ai", getUsage: async () => { throw new Error("unused"); } }],
    enabledProviderIds: () => ["claude"],
    visibleIntervalMs: () => 60_000,
    backgroundIntervalMs: () => 60_000,
    incidentFor: (id) => id === "claude" ? { summary: "Outage: API down", isOutage: true } : undefined,
  });
  let published = 0;
  service.onDidUpdate(() => { published += 1; });
  assert.equal(service.getStates()[0]?.incident, "Outage: API down");
  assert.equal(service.getStates()[0]?.incidentIsOutage, true);
  service.incidentsChanged();
  assert.equal(published, 1);
  service.dispose();
});
