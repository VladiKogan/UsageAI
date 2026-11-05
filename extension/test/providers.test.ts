import assert from "node:assert/strict";
import * as os from "node:os";
import * as path from "node:path";
import test from "node:test";
import {
  assertClaudeCredentialsUsable,
  formatClaudePlan,
  parseClaudeCredentials,
  parseClaudeSnapshot,
  refreshExpiredClaudeCredentials,
} from "../src/providers/claude";
import { parseCodexSnapshot } from "../src/providers/codex";
import { parseCopilotSnapshot, resolveCopilotPlan } from "../src/providers/copilot";
import {
  agyExecutableCandidates,
  mergeQuotaSummary,
  parseAntigravityQuotaSummary,
  parseAntigravityUserStatus,
  parseAgyUsageOutput,
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
  assert.equal(snapshot.accountName, undefined);

  // Model-specific buckets such as Spark are shown too, named after the label Codex gives them, while
  // the main limits keep their plain names. Mirrors the desktop check.
  const grouped = parseCodexSnapshot(
    {
      rateLimitsByLimitId: {
        codex_bengalfox: {
          limitId: "codex_bengalfox",
          limitName: "GPT-5.3-Codex-Spark",
          primary: { usedPercent: 91, windowDurationMins: 300 },
          secondary: { usedPercent: 12, windowDurationMins: 10_080 },
        },
        codex: {
          limitId: "codex",
          planType: "pro",
          primary: { usedPercent: 5, windowDurationMins: 300 },
          secondary: { usedPercent: 40, windowDurationMins: 10_080 },
        },
        codex_other: { normalModelSlug: "gpt-mini (preview)", secondary: { usedPercent: 3, windowDurationMins: 10_080 } },
        codex_unnamed: { primary: { usedPercent: 1, windowDurationMins: 300 } },
        codex_empty: { limitName: "No windows" },
      },
    },
    { account: { type: "chatgpt", email: "dev@example.com", planType: "plus" } },
  );
  assert.deepEqual(grouped.metrics.map((metric) => metric.name), [
    "5-hour",
    "Weekly",
    "GPT-5.3-Codex-Spark 5-hour",
    "GPT-5.3-Codex-Spark Weekly",
    "gpt-mini preview Weekly",
    "Codex Unnamed 5-hour",
  ]);
  assert.equal(grouped.metrics[2]?.usedPercent, 91);
  assert.equal(grouped.plan, "Pro");
  assert.equal(grouped.accountName, "dev@example.com");
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
  assert.equal("refreshToken" in credentials, false);
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

test("Claude credential parsing never exposes the shared refresh token", () => {
  const credentials = parseClaudeCredentials(JSON.stringify({
    claudeAiOauth: {
      accessToken: "expired-access-token",
      refreshToken: "shared-refresh-token",
      expiresAt: Date.now() - 1,
      scopes: ["user:profile"],
    },
  }));

  assert.equal("refreshToken" in credentials, false);
  assert.throws(
    () => assertClaudeCredentialsUsable(credentials),
    /expired.*CLI could not refresh/i,
  );
});

test("Claude delegates expired access-token recovery to the official CLI", async () => {
  const expired = parseClaudeCredentials(JSON.stringify({
    claudeAiOauth: {
      accessToken: "expired-access-token",
      refreshToken: "shared-refresh-token",
      expiresAt: Date.now() - 1,
      scopes: ["user:profile"],
    },
  }));
  const refreshed = {
    ...expired,
    accessToken: "owner-refreshed-access-token",
    expiresAt: Date.now() + 60_000,
  };
  let probeCalls = 0;
  let reloadCalls = 0;

  const recovered = await refreshExpiredClaudeCredentials(
    expired,
    async () => {
      reloadCalls++;
      return refreshed;
    },
    async () => {
      probeCalls++;
      return true;
    },
  );

  assert.equal(probeCalls, 1);
  assert.equal(reloadCalls, 1);
  assert.equal(recovered.accessToken, "owner-refreshed-access-token");
  assert.equal("refreshToken" in recovered, false);
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

test("Copilot names an individual plan from its SKU", () => {
  // copilot_plan is "individual" for every personal plan; the SKU says which one it is.
  const faculty = parseCopilotSnapshot({
    copilot_plan: "individual",
    access_type_sku: "free_faculty_quota",
    quota_snapshots: { chat: { unlimited: true } },
  });
  assert.equal(faculty.plan, "Pro");

  const cases: Array<[string | undefined, string | undefined, string]> = [
    ["individual", "monthly_subscriber_quota", "Pro"],
    ["individual", "yearly_subscriber_quota", "Pro"],
    ["individual", "free_educational_quota", "Pro"],
    ["individual", "free_engaged_oss_quota", "Pro"],
    ["individual", "plus_monthly_subscriber_quota", "Pro+"],
    ["Individual", " PLUS_YEARLY_SUBSCRIBER_QUOTA ", "Pro+"],
    ["individual", "free_limited_copilot", "Free"],
    [undefined, "free_faculty_quota", "Pro"],
    // An unknown SKU keeps the reported plan, and only a personal plan is ever renamed.
    ["individual", "some_future_quota", "Individual"],
    ["individual", undefined, "Individual"],
    ["business", "monthly_subscriber_quota", "Business"],
    ["individual_pro", "free_limited_copilot", "Pro"],
    [undefined, "some_future_quota", "Some Future Quota"],
    [undefined, undefined, "Copilot"],
  ];
  for (const [plan, sku, expected] of cases) {
    assert.equal(resolveCopilotPlan(plan, sku), expected, `${plan} / ${sku}`);
  }
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
          // An omitted fraction with a reset time is an exhausted limit (protobuf omits zero)...
          { label: "Claude Sonnet", quotaInfo: { resetTime: "2026-07-29T18:00:00Z" } },
          // ...while a block with neither belongs to a model with no quota and is skipped.
          { label: "Gemini 4 Argon", quotaInfo: {} },
        ],
      },
    },
  });
  assert.ok(antigravity);
  assert.deepEqual(antigravity.metrics.map((metric) => metric.usedPercent), [20, 100]);
  assert.equal(antigravity.metrics.length, 2);

  // A Google AI Plus account's real answers: the summary names only weekly windows, so no "5-hour"
  // row is borrowed from the status call, which repeats the weekly fractions without a window name.
  const plusStatus = parseAntigravityUserStatus({
    userStatus: { cascadeModelConfigData: { clientModelConfigs: [
      { label: "Gemini 3.8 Flash (High)", quotaInfo: { remainingFraction: 1, resetTime: "2026-11-12T06:22:21Z" } },
      { label: "Claude Sonnet 4.6 (Thinking)", quotaInfo: { remainingFraction: 0.060413, resetTime: "2026-11-10T06:16:45Z" } },
    ] } },
  })!;
  const plusSummary = parseAntigravityQuotaSummary({ response: { groups: [
    { displayName: "Gemini Models", buckets: [{ bucketId: "gemini-weekly", window: "weekly", remainingFraction: 1, resetTime: "2026-11-12T06:22:21Z" }] },
    { displayName: "Claude and GPT models", buckets: [{ bucketId: "3p-weekly", window: "weekly", remainingFraction: 0.060413, resetTime: "2026-11-10T06:16:45Z" }] },
  ] } });
  const plus = mergeQuotaSummary(plusStatus.metrics, plusSummary);
  assert.deepEqual(plus.map((metric) => metric.name), ["Gemini Models (Weekly)", "Claude and GPT models (Weekly)"]);
  assert.equal(plus[1]?.usedPercent, 94);

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

  const agy = parseAgyUsageOutput(JSON.stringify({
    type: "result",
    result: {
      planName: "Google AI Pro",
      accountEmail: "agy@example.com",
      quotaSummary: {
        groups: [{
          displayName: "Gemini Models",
          buckets: [
            { bucketId: "gemini_5h", remaining: { remainingFraction: 0.72 } },
            { bucketId: "gemini_weekly", remainingFraction: 0.44 },
          ],
        }],
      },
    },
  }));
  assert.ok(agy);
  assert.equal(agy.plan, "Google AI Pro");
  assert.equal(agy.accountName, "agy@example.com");
  assert.deepEqual(agy.metrics.map((metric) => metric.usedPercent), [28, 56]);

  const currentAgy = parseAgyUsageOutput(JSON.stringify({
    status: "SUCCESS",
    response: "Gemini Models: 81% remaining",
    command: {
      name: "usage",
      data: {
        groups: [
          {
            name: "Gemini Models",
            buckets: [
              {
                id: "gemini_5h",
                name: "5-hour limit",
                window: "5h",
                remaining_fraction: 0.81,
                reset_time: "2026-08-26T14:00:00Z",
              },
              {
                id: "gemini_weekly",
                name: "Weekly limit",
                window: "7d",
                remaining_fraction: 0.62,
                reset_time: "2026-09-01T00:00:00Z",
              },
            ],
          },
          {
            name: "Claude and GPT models",
            buckets: [
              { id: "other_5h", window: "5h", remaining_fraction: 1.2 },
              { id: "other_weekly", window: "weekly", remaining_fraction: -0.2 },
            ],
          },
        ],
      },
    },
  }));
  assert.ok(currentAgy);
  assert.equal(currentAgy.plan, "Antigravity");
  assert.deepEqual(currentAgy.metrics.map((metric) => [metric.name, metric.usedPercent]), [
    ["Gemini Models (5-hour)", 19],
    ["Gemini Models (Weekly)", 38],
    ["Claude and GPT models (5-hour)", 0],
    ["Claude and GPT models (Weekly)", 100],
  ]);
  assert.equal(currentAgy.metrics[1]?.resetsAt, "2026-09-01T00:00:00.000Z");
});

test("Antigravity CLI discovery includes the VS Code extension backend", () => {
  const executable = process.platform === "win32" ? "agy.exe" : "agy";
  assert.ok(agyExecutableCandidates().includes(path.join(os.homedir(), ".gemini", "bin", executable)));
});

test("provider parsers reject malformed data and preserve fallback semantics", () => {
  const fallbackBucket = parseCodexSnapshot({
    rateLimitsByLimitId: {
      other: {
        planType: "prolite",
        primary: { usedPercent: 140 },
        secondary: { usedPercent: -20, windowDurationMins: 20_160 },
      },
    },
    rateLimitResetCredits: { availableCount: 1 },
  });
  assert.equal(fallbackBucket.plan, "Pro Lite");
  assert.deepEqual(fallbackBucket.metrics.map((metric) => [metric.name, metric.usedPercent]), [
    ["Session", 100],
    ["2-week", 0],
    ["Reset credits", null],
  ]);
  assert.equal(fallbackBucket.metrics[2]?.usageText, "Full reset available");

  const historical = parseCodexSnapshot({
    rateLimits: {
      planType: "custom_plan",
      primary: { usedPercent: 12, windowDurationMins: 90, resetsAt: 1_900_000_000 },
    },
  });
  assert.equal(historical.plan, "Custom Plan");
  assert.equal(historical.metrics[0]?.name, "Session");
  assert.equal(historical.metrics[0]?.resetsAt, "2030-03-17T17:46:40.000Z");
  assert.throws(() => parseCodexSnapshot({}), /no rate-limit windows/i);

  const flatCredentials = parseClaudeCredentials(JSON.stringify({
    accessToken: " flat-access-token ",
    rateLimitTier: "team",
    scopes: [null, " ", "user:profile", "x".repeat(129)],
  }));
  assert.equal(flatCredentials.accessToken, "flat-access-token");
  assert.equal(flatCredentials.plan, "Claude Team");
  assert.deepEqual(flatCredentials.scopes, ["user:profile"]);
  assert.doesNotThrow(() => assertClaudeCredentialsUsable({}));
  assert.throws(() => parseClaudeCredentials("{}"), /no OAuth access token/i);

  const camelCase = parseClaudeSnapshot({
    // Utilization is a percentage: 1.0 is 1% used, not a fraction meaning 100%.
    fiveHour: { utilization: 1.0, resetsAt: "2030-01-01T00:00:00Z" },
    limits: [
      null,
      { kind: "weekly_all", group: "monthly", percent: 90 },
      { kind: "weekly_models" },
      { kind: "weekly_models", group: "weekly", percent: 25, resetsAt: "2030-01-02T00:00:00Z" },
    ],
    extraUsage: { isEnabled: true },
  }, "Claude Team");
  assert.deepEqual(camelCase.metrics.map((metric) => [metric.name, metric.usedPercent]), [
    ["5-hour", 1],
    ["Weekly", 25],
    ["Extra usage", null],
  ]);
  assert.equal(camelCase.metrics[2]?.remainingText, "ENABLED");

  const invalidCurrency = parseClaudeSnapshot({
    sevenDay: { utilization: 17 },
    extraUsage: { isEnabled: true, usedCredits: 125, monthlyLimit: 500, currency: "not-a-currency" },
  });
  assert.equal(invalidCurrency.metrics[1]?.remainingText, "1.25 NOT-A-CURRENCY / 5.00 NOT-A-CURRENCY");
  assert.throws(() => parseClaudeSnapshot({}), /without five-hour or weekly/i);
  assert.deepEqual(
    [undefined, "free", "pro", "max", "enterprise", "claude_max_5x_plan", "custom_tier"]
      .map(formatClaudePlan),
    ["Claude", "Claude Free", "Claude Pro", "Claude Max", "Claude Enterprise", "Claude Max 5x", "Custom Tier"],
  );

  assert.throws(() => parseGeminiQuotaResponse({}), /without quota buckets/i);
  assert.throws(
    () => parseGeminiQuotaResponse({ buckets: [{ remainingFraction: 0.5 }] }),
    /no valid model quota buckets/i,
  );
  const duplicateQuota = parseGeminiQuotaResponse({
    buckets: [
      { modelId: "custom_model", remainingFraction: 0.8 },
      { modelId: "custom_model", remainingFraction: 0.2, resetTime: "2030-01-01T00:00:00Z" },
    ],
  });
  assert.equal(duplicateQuota.plan, "Gemini");
  assert.equal(duplicateQuota.metrics[0]?.name, "Custom Model");
  assert.equal(duplicateQuota.metrics[0]?.usedPercent, 80);

  assert.equal(parseAntigravityUserStatus({}), undefined);
  assert.equal(parseAntigravityUserStatus({ userStatus: {} }), undefined);
  const groupedStatus = parseAntigravityUserStatus({
    userStatus: {
      planStatus: { planInfo: { planDisplayName: "Workspace" } },
      cascadeModelConfigData: {
        clientModelConfigs: [
          null,
          { label: "Other model", quotaInfo: { remainingFraction: 0.5 } },
          { label: "Other model", quotaInfo: { remainingFraction: 0.5 } },
          { label: "Other model", quotaInfo: { remainingFraction: 0.25, resetTime: "2030-01-01T00:00:00Z" } },
        ],
      },
    },
  });
  assert.ok(groupedStatus);
  assert.equal(groupedStatus.plan, "Workspace");
  assert.deepEqual(groupedStatus.metrics.map((metric) => metric.name), [
    "Other Models (Limit 1)",
    "Other Models (Limit 2)",
  ]);

  assert.deepEqual(parseAntigravityQuotaSummary({}), []);
  const assortedSummary = parseAntigravityQuotaSummary({
    quotaSummary: {
      groups: [
        { name: "Broken" },
        { name: "Other", buckets: [{ name: "Daily", remaining_fraction: 0.6 }] },
        { name: "Gemini Models", buckets: [{ id: "weekly", remaining: { remaining_fraction: 0.3 } }] },
      ],
    },
  });
  assert.deepEqual(assortedSummary.map((metric) => [metric.name, metric.kind, metric.usedPercent]), [
    ["Gemini Models (Weekly)", "rolling", 70],
    ["Other (Daily)", "rolling", 40],
  ]);

  assert.equal(parseAgyUsageOutput(""), undefined);
  assert.equal(parseAgyUsageOutput("not json"), undefined);
  const wrapped = parseAgyUsageOutput(`noise before ${JSON.stringify({
    accountEmail: "wrapped@example.com",
    nested: JSON.stringify({
      groups: [{ displayName: "Gemini Models", buckets: [{ bucketId: "5h", remainingFraction: 0.9 }] }],
    }),
  })} noise after`);
  assert.ok(wrapped);
  assert.equal(wrapped.accountName, "wrapped@example.com");
  assert.equal(wrapped.metrics[0]?.usedPercent, 10);
});
