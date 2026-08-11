import assert from "node:assert/strict";
import test from "node:test";
import { parseClaudeCredentials, parseClaudeSnapshot } from "../src/providers/claude";
import { parseCodexSnapshot } from "../src/providers/codex";
import { parseCopilotSnapshot } from "../src/providers/copilot";
import {
  parseAntigravityQuotaSummary,
  parseAntigravityUserStatus,
  parseGeminiQuotaResponse,
} from "../src/providers/gemini";

test("Codex keeps rolling windows and balances", () => {
  const snapshot = parseCodexSnapshot({
    rateLimitsByLimitId: {
      codex: {
        planType: "plus",
        primary: { usedPercent: 43, windowDurationMins: 300 },
        secondary: { usedPercent: 63, windowDurationMins: 10_080 },
        credits: { balance: "$12.50" },
      },
    },
    rateLimitResetCredits: { availableCount: 3 },
  });
  assert.equal(snapshot.plan, "Plus");
  assert.deepEqual(snapshot.metrics.map((metric) => metric.name), ["5-hour", "Weekly", "Credits", "Reset credits"]);
  assert.equal(snapshot.metrics[1]?.usedPercent, 63);
});

test("Claude parses OAuth credentials and every reported limit", () => {
  const credentials = parseClaudeCredentials(JSON.stringify({
    claudeAiOauth: {
      accessToken: "access-token",
      refreshToken: "refresh-token",
      expiresAt: 1_900_000_000_000,
      scopes: ["user:profile"],
      subscriptionType: "claude_max_20x",
    },
  }));
  assert.equal(credentials.plan, "Claude Max 20x");
  const snapshot = parseClaudeSnapshot({
    five_hour: { utilization: 22, resets_at: "2026-07-27T21:00:00Z" },
    seven_day: { utilization: 0.49 },
    limits: [
      { kind: "weekly_all", group: "weekly", percent: 51 },
      { kind: "weekly_opus", percent: 12 },
    ],
    extra_usage: { is_enabled: true, used_credits: 410, monthly_credit_limit: 5_000, currency: "USD" },
  }, credentials.plan);
  assert.deepEqual(snapshot.metrics.map((metric) => metric.usedPercent), [22, 51, 12, null]);
  assert.equal(snapshot.metrics[3]?.remainingText, "$4.10 / $50.00");
});

test("Copilot retains metered and unlimited quotas", () => {
  const snapshot = parseCopilotSnapshot({
    copilot_plan: "individual_pro",
    login: "octocat",
    quota_reset_date_utc: "2026-08-01T00:00:00Z",
    quota_snapshots: {
      premium_interactions: {
        entitlement: 300,
        remaining: 72,
        percent_remaining: 24,
        token_based_billing: true,
      },
      chat: { unlimited: true },
      completions: { unlimited: true },
    },
  });
  assert.equal(snapshot.plan, "Pro");
  assert.equal(snapshot.accountName, "octocat");
  assert.equal(snapshot.metrics.length, 3);
  assert.equal(snapshot.metrics[0]?.name, "AI credits");
  assert.equal(snapshot.metrics[0]?.usedPercent, 76);
  assert.equal(snapshot.metrics[1]?.isUnlimited, true);
});

test("Gemini parses CLI and Antigravity quota shapes", () => {
  const cli = parseGeminiQuotaResponse({
    buckets: [
      { modelId: "gemini-1.5-pro", remainingFraction: 0.75, resetTime: "2026-07-28T20:00:00Z" },
      { modelId: "gemini-1.5-flash", remainingFraction: 0.9 },
    ],
  }, "Google AI Pro", "user@example.com");
  assert.deepEqual(cli.metrics.map((metric) => metric.usedPercent), [25, 10]);
  assert.equal(cli.accountName, "user@example.com");

  const antigravity = parseAntigravityUserStatus({
    userStatus: {
      email: "dev@example.com",
      cascadeModelConfigData: {
        clientModelConfigs: [
          { label: "Gemini Pro", quotaInfo: { remainingFraction: 0.8 } },
          { label: "Claude Sonnet", quotaInfo: {} },
        ],
      },
    },
  });
  assert.ok(antigravity);
  assert.deepEqual(antigravity.metrics.map((metric) => metric.usedPercent), [20, 100]);

  const summary = parseAntigravityQuotaSummary({
    response: {
      groups: [{
        displayName: "Gemini Models",
        buckets: [
          { bucketId: "gemini_5h", remainingFraction: 0.7 },
          { bucketId: "gemini_weekly", remainingFraction: 0.55 },
        ],
      }],
    },
  });
  assert.deepEqual(summary.map((metric) => [metric.name, metric.usedPercent]), [
    ["Gemini Models (5-hour)", 30],
    ["Gemini Models (Weekly)", 45],
  ]);
});
