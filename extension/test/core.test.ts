import assert from "node:assert/strict";
import test from "node:test";
import { UsageProviderError } from "../src/errors";
import {
  highestUsedPercent,
  formatResetCountdown,
  normalizeStatusBarProviders,
  primaryMetric,
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

test("model selects real quota before balance", () => {
  assert.equal(primaryMetric(snapshot)?.name, "Weekly");
  assert.equal(highestUsedPercent(snapshot), 74);
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
  assert.ok(state?.nextRefreshAt);
  service.dispose();
});
