import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";
import { UsageProviderError } from "../src/errors";
import {
  highestUsedPercent,
  formatResetCountdown,
  normalizeStatusBarProviders,
  primaryMetric,
  segmentedUsageBar,
  statusBarMetrics,
  statusBarProvidersFromCheckboxes,
  type UsageClient,
  type UsageSnapshot,
} from "../src/model";
import { UsageRefreshService } from "../src/refresh-service";
import { normalizeToken } from "../src/security";

const snapshot: UsageSnapshot = {
  plan: "Pro",
  metrics: [
    { name: "Balance", kind: "balance", usedPercent: null, remainingText: "$5" },
    { name: "Weekly", kind: "rolling", usedPercent: 74 },
  ],
  fetchedAt: new Date().toISOString(),
  providerId: "fixture",
  providerName: "Fixture",
};

test("marketplace previews resolve from the extension directory", async () => {
  const packageJson = JSON.parse(
    await readFile(resolve(__dirname, "../../package.json"), "utf8"),
  ) as { vsce?: { baseImagesUrl?: string } };
  assert.equal(
    packageJson.vsce?.baseImagesUrl,
    "https://raw.githubusercontent.com/VladiKogan/UsageAI/main/extension",
  );

  const readme = await readFile(resolve(__dirname, "../../README.md"), "utf8");
  const previewPaths = [...readme.matchAll(/<img src="(media\/[^"]+-preview\.png)"/g)]
    .map((match) => match[1]!);
  assert.deepEqual(previewPaths, [
    "media/dashboard-preview.png",
    "media/status-bar-preview.png",
  ]);
  await Promise.all(previewPaths.map((previewPath) =>
    access(resolve(__dirname, "../..", previewPath))));
});

test("model selects real quota before balance", () => {
  assert.equal(primaryMetric(snapshot)?.name, "Weekly");
  assert.equal(highestUsedPercent(snapshot), 74);
});

test("status bar prioritizes session and weekly windows", () => {
  const windows: UsageSnapshot = {
    ...snapshot,
    metrics: [
      { name: "Credits", kind: "balance", usedPercent: null, remainingText: "$5" },
      { name: "Weekly", kind: "rolling", usedPercent: 39 },
      { name: "5-hour", kind: "session", usedPercent: 24 },
      { name: "Weekly Opus", kind: "rolling", usedPercent: 12 },
    ],
  };

  assert.deepEqual(statusBarMetrics(windows).map((metric) => metric.name), ["5-hour", "Weekly"]);
  assert.equal(segmentedUsageBar(24), "■ ■ □ □ □ □ □ □ □ □");
  assert.equal(segmentedUsageBar(39), "■ ■ ■ ■ □ □ □ □ □ □");
});

test("status bar provider selection supports legacy and multiple values", () => {
  assert.deepEqual(normalizeStatusBarProviders("claude"), ["claude"]);
  assert.deepEqual(normalizeStatusBarProviders(["codex", "gemini", "codex"]), ["codex", "gemini"]);
  assert.deepEqual(normalizeStatusBarProviders(["hottest", "claude"]), ["claude"]);
  assert.deepEqual(normalizeStatusBarProviders([]), []);
});

test("status bar checkboxes select providers and retain hottest as a fallback", () => {
  assert.deepEqual(statusBarProvidersFromCheckboxes({
    hottestWhenNoneSelected: true,
    codex: false,
    claude: false,
    copilot: false,
    gemini: false,
  }), ["hottest"]);
  assert.deepEqual(statusBarProvidersFromCheckboxes({
    hottestWhenNoneSelected: true,
    codex: true,
    claude: true,
    copilot: false,
    gemini: false,
  }, ["claude", "gemini", "codex", "copilot"]), ["claude", "codex"]);
  assert.deepEqual(statusBarProvidersFromCheckboxes({
    hottestWhenNoneSelected: false,
    codex: false,
    claude: false,
    copilot: false,
    gemini: false,
  }), []);
});

test("long reset countdowns use days and hours", () => {
  const now = Date.parse("2026-08-11T00:00:00Z");
  assert.equal(formatResetCountdown(undefined, now), "Reset not reported");
  assert.equal(formatResetCountdown("invalid", now), "Reset not reported");
  assert.equal(formatResetCountdown("2026-08-10T00:00:00Z", now), "Reset due");
  assert.equal(formatResetCountdown("2026-08-11T00:01:00Z", now), "Resets in 1m");
  assert.equal(formatResetCountdown("2026-08-17T12:49:00Z", now), "Resets in 6d 12h");
  assert.equal(formatResetCountdown("2026-08-11T23:49:00Z", now), "Resets in 23h 49m");
  assert.equal(formatResetCountdown("2026-08-12T00:00:00Z", now), "Resets in 1d");
});

test("credential normalization rejects whitespace and controls", () => {
  assert.equal(normalizeToken(" token-value "), "token-value");
  assert.equal(normalizeToken("token value"), undefined);
  assert.equal(normalizeToken("line\nbreak"), undefined);
});

test("refresh service keeps the previous snapshot stale after failure", async () => {
  let attempts = 0;
  const client: UsageClient = {
    id: "fixture",
    displayName: "Fixture",
    signInCommand: "fixture login",
    accountUrl: "https://example.com",
    async getUsage() {
      attempts += 1;
      if (attempts > 1) {
        throw new UsageProviderError("Fixture failed.");
      }
      return snapshot;
    },
  };
  const service = new UsageRefreshService({
    clients: [client],
    enabledProviderIds: () => ["fixture"],
    visibleIntervalMs: () => 60_000,
    backgroundIntervalMs: () => 60_000,
  });
  await service.refresh(true);
  await service.refresh(true);
  const state = service.getStates()[0];
  assert.equal(state?.snapshot, snapshot);
  assert.equal(state?.stale, true);
  assert.equal(state?.error, "Fixture failed.");
  assert.ok(state?.lastAttemptedAt);
  assert.ok(state?.nextRefreshAt);
  service.dispose();
});

test("retry-only refresh recovers stale providers without moving regular polling", async () => {
  let healthyAttempts = 0;
  let recoveringAttempts = 0;
  const healthy: UsageClient = {
    id: "healthy",
    displayName: "Healthy",
    signInCommand: "healthy login",
    accountUrl: "https://example.com/healthy",
    async getUsage() {
      healthyAttempts += 1;
      return { ...snapshot, providerId: "healthy", providerName: "Healthy" };
    },
  };
  const recovering: UsageClient = {
    id: "recovering",
    displayName: "Recovering",
    signInCommand: "recovering login",
    accountUrl: "https://example.com/recovering",
    async getUsage() {
      recoveringAttempts += 1;
      if (recoveringAttempts === 2) {
        throw new UsageProviderError("Temporary failure.", 1);
      }
      return { ...snapshot, providerId: "recovering", providerName: "Recovering" };
    },
  };
  const service = new UsageRefreshService({
    clients: [healthy, recovering],
    enabledProviderIds: () => ["healthy", "recovering"],
    visibleIntervalMs: () => 60_000,
    backgroundIntervalMs: () => 60_000,
  });

  await service.refresh(true);
  await service.refresh(true);
  const stale = service.getStates().find((state) => state.id === "recovering");
  assert.equal(stale?.stale, true);
  assert.ok(stale?.nextRefreshAt);
  const retryAt = new Date(stale.nextRefreshAt).getTime();
  while (Date.now() < retryAt) {
    await new Promise((resolve) => setTimeout(resolve, 1));
  }

  const regularDeadline = (service as unknown as { nextRegularRefreshAt: number }).nextRegularRefreshAt;
  await service.refresh(false);

  assert.equal(healthyAttempts, 2);
  assert.equal(recoveringAttempts, 3);
  const recovered = service.getStates().find((state) => state.id === "recovering");
  assert.equal(recovered?.stale, false);
  assert.equal(recovered?.error, undefined);
  assert.equal(recovered?.nextRefreshAt, undefined);
  assert.equal(
    (service as unknown as { nextRegularRefreshAt: number }).nextRegularRefreshAt,
    regularDeadline,
  );
  service.dispose();
});

test("disabled providers do not keep an expired retry timer hot", async () => {
  let enabledProviderIds = ["fixture"];
  let attempts = 0;
  const client: UsageClient = {
    id: "fixture",
    displayName: "Fixture",
    signInCommand: "fixture login",
    accountUrl: "https://example.com/fixture",
    async getUsage() {
      attempts += 1;
      throw new UsageProviderError("Temporary failure.", 1);
    },
  };
  const service = new UsageRefreshService({
    clients: [client],
    enabledProviderIds: () => enabledProviderIds,
    visibleIntervalMs: () => 60_000,
    backgroundIntervalMs: () => 60_000,
  });

  await service.refresh(true);
  await new Promise((resolve) => setTimeout(resolve, 5));
  enabledProviderIds = [];
  service.configurationChanged();

  const timer = (service as unknown as { timer?: NodeJS.Timeout }).timer;
  const scheduledDelay = (timer as unknown as { _idleTimeout?: number } | undefined)?._idleTimeout;
  assert.ok(scheduledDelay !== undefined && scheduledDelay > 50_000);
  await service.refresh(false);
  assert.equal(attempts, 1);
  service.dispose();
});

test("cached snapshots stay stale across generic failures and follow configured order", async () => {
  let enabledProviderIds = ["beta", "alpha"];
  const alpha: UsageClient = {
    id: "alpha",
    displayName: "Alpha",
    signInCommand: "alpha login",
    accountUrl: "https://example.com/alpha",
    async getUsage() {
      throw new Error("internal details must stay private");
    },
  };
  const beta: UsageClient = {
    id: "beta",
    displayName: "Beta",
    signInCommand: "beta login",
    accountUrl: "https://example.com/beta",
    async getUsage() {
      return { ...snapshot, providerId: "beta", providerName: "Beta" };
    },
  };
  const cachedAlpha = { ...snapshot, providerId: "alpha", providerName: "Alpha" };
  const service = new UsageRefreshService({
    clients: [alpha, beta],
    enabledProviderIds: () => enabledProviderIds,
    visibleIntervalMs: () => 60_000,
    backgroundIntervalMs: () => 60_000,
    initialSnapshots: { alpha: cachedAlpha },
  });

  assert.deepEqual(service.getStates().map((state) => state.id), ["beta", "alpha"]);
  assert.equal(service.getStates()[1]?.snapshot, cachedAlpha);
  assert.equal(service.getStates()[1]?.stale, true);
  await service.refresh(true);
  const failed = service.getStates().find((state) => state.id === "alpha");
  assert.equal(failed?.snapshot, cachedAlpha);
  assert.equal(failed?.stale, true);
  assert.equal(failed?.error, "UsageAI could not refresh this provider.");

  enabledProviderIds = ["alpha"];
  service.configurationChanged();
  assert.deepEqual(service.getStates().map((state) => state.id), ["alpha"]);
  service.dispose();
});

test("concurrent refresh calls coalesce and disposal prevents later work", async () => {
  let attempts = 0;
  let complete: ((value: UsageSnapshot) => void) | undefined;
  const pending = new Promise<UsageSnapshot>((resolve) => { complete = resolve; });
  const client: UsageClient = {
    id: "fixture",
    displayName: "Fixture",
    signInCommand: "fixture login",
    accountUrl: "https://example.com/fixture",
    async getUsage() {
      attempts += 1;
      return pending;
    },
  };
  const service = new UsageRefreshService({
    clients: [client],
    enabledProviderIds: () => ["fixture"],
    visibleIntervalMs: () => 60_000,
    backgroundIntervalMs: () => 60_000,
  });

  const first = service.refresh(true);
  const second = service.refresh(true);
  assert.equal(first, second);
  assert.equal(attempts, 1);
  complete?.(snapshot);
  await first;
  assert.equal(service.getStates()[0]?.refreshing, false);
  service.dispose();
  await service.refresh(true);
  assert.equal(attempts, 1);
});
