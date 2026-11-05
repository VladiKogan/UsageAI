using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

internal sealed class ClaudeCodeUsageClient : IUsageClient
{
    private const string CredentialManagerService = "Claude Code-credentials";
    private const string OAuthBeta = "oauth-2025-04-20";
    private const int MaxAuthProbeOutputCharacters = 16_384;
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");

    /// <summary>The usage endpoint asked to include saved limit-reset credits, as claude.ai asks.</summary>
    internal static readonly Uri UsageWithResetCreditsEndpoint = new("https://api.anthropic.com/api/oauth/usage?cedar_ember=1");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AuthProbeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan CredentialObservationWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshNudgeTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient SharedClient = SecureHttp.CreateClient(RequestTimeout);
    private readonly HttpClient _client;
    private readonly Func<IReadOnlyList<string>> _keyringPasswords;
    private readonly Func<CancellationToken, Task<bool>> _claudeAuthProbe;

    /// <summary>Until when the opt-in web path is skipped after claude.ai asked for a browser check.</summary>
    private DateTimeOffset _webRetryAfterUtc;

    public ClaudeCodeUsageClient()
        : this(SharedClient, null, RunClaudeAuthRecoveryAsync)
    {
    }

    internal ClaudeCodeUsageClient(
        HttpClient client,
        Func<IReadOnlyList<string>>? keyringPasswords = null,
        Func<CancellationToken, Task<bool>>? claudeAuthProbe = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _keyringPasswords = keyringPasswords ??
            (() => WindowsCredentialReader.FindKeyringPasswords(CredentialManagerService));
        _claudeAuthProbe = claudeAuthProbe ?? (_ => Task.FromResult(false));
    }

    public string Id => "claude";

    public string DisplayName => "Claude Code";

    public string SignInCommand => "claude";

    public Uri AccountUrl { get; } = new("https://claude.ai/settings/usage");

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        if (ClaudeWebUsageClient.IsConfigured && DateTimeOffset.UtcNow >= _webRetryAfterUtc)
        {
            try
            {
                return await ClaudeWebUsageClient.GetUsageAsync(cancellationToken);
            }
            catch (ClaudeWebUsageException exception)
            {
                // An expired or unavailable opt-in web session falls back to Claude Code OAuth. After a
                // Cloudflare browser check claude.ai is left alone for a while rather than challenged
                // on every refresh; the session key itself is kept.
                if (exception.RetryAfter > TimeSpan.Zero)
                {
                    _webRetryAfterUtc = DateTimeOffset.UtcNow + exception.RetryAfter;
                }
            }
        }

        var credentials = await RefreshExpiredCredentialsThroughClaudeAsync(
            LoadCredentials(),
            cancellationToken);
        ValidateCredentials(credentials);

        try
        {
            return await RequestUsageAsync(credentials, cancellationToken);
        }
        catch (ClaudeCodeUsageException exception) when (
            exception.CanRetryAfterAuthProbe && !credentials.IsEnvironmentOverride)
        {
            var recovered = await RecoverCredentialsThroughClaudeAsync(cancellationToken);
            if (recovered is null)
            {
                throw;
            }

            ValidateCredentials(recovered);
            return await RequestUsageAsync(recovered, cancellationToken);
        }
    }

    private static void ValidateCredentials(ClaudeCredentials credentials)
    {
        if (credentials.IsExpired)
        {
            throw new ClaudeCodeUsageException(
                "Claude Code's access token expired and its CLI could not refresh the login. Open `claude`, then refresh UsageAI.");
        }

        if (credentials.Scopes.Count > 0 &&
            !credentials.Scopes.Contains("user:profile", StringComparer.Ordinal))
        {
            throw new ClaudeCodeUsageException(
                "Claude Code's OAuth token is missing the user:profile scope. Run `claude` or use the Claude Code extension to sign in again.");
        }
    }

    private async Task<UsageSnapshot> RequestUsageAsync(
        ClaudeCredentials credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            // Saved limit-reset credits are reported only when asked for. An account or API that
            // rejects the parameter is asked once more without it, so usage never depends on it.
            var response = await SendUsageRequestAsync(UsageWithResetCreditsEndpoint, credentials, cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                response = await SendUsageRequestAsync(UsageEndpoint, credentials, cancellationToken);
            }

            using var _ = response;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ClaudeCodeUsageException(
                    "Claude Code's login has expired. Run `claude` or use the Claude Code extension to sign in again, then refresh.")
                {
                    CanRetryAfterAuthProbe = true,
                };
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new ClaudeCodeUsageException(
                    "Claude Code's login cannot read account usage. Run `claude` or use the Claude Code extension to sign in again.")
                {
                    CanRetryAfterAuthProbe = true,
                };
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                var retryText = retryAfter is { } delta
                    ? $" Try again in about {Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds))} seconds."
                    : string.Empty;
                throw new ClaudeCodeUsageException($"Anthropic rate-limited the usage request.{retryText}")
                {
                    RetryAfter = retryAfter ?? TimeSpan.Zero,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ClaudeCodeUsageException(
                    $"Claude Code returned HTTP {(int)response.StatusCode} while reading usage.");
            }

            using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
            return ParseSnapshot(document.RootElement, credentials.Plan);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClaudeCodeUsageException("Claude Code did not return usage data within 15 seconds.");
        }
        catch (ClaudeCodeUsageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ClaudeCodeUsageException(
                "Could not read Claude Code usage because the provider returned invalid or unavailable data.",
                exception)
            {
                CanRetryAfterAuthProbe = true,
            };
        }
    }

    private async Task<HttpResponseMessage> SendUsageRequestAsync(
        Uri endpoint,
        ClaudeCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("anthropic-beta", OAuthBeta);
        request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>The headline shown when Claude reports no metered limit for the account's plan.</summary>
    internal const string NoLimitsReportedText = "Claude reports no usage limits for this plan";

    /// <summary>At most this many model-specific weekly limits are shown.</summary>
    private const int MaxModelWeeklyLimits = 6;

    /// <summary>A grant total above this is treated as malformed rather than shown.</summary>
    private const int MaxResetGrants = 50;

    internal static UsageSnapshot ParseSnapshot(JsonElement root, string plan = "Claude") =>
        ParseSnapshot(root, plan, DateTimeOffset.UtcNow);

    internal static UsageSnapshot ParseSnapshot(JsonElement root, string plan, DateTimeOffset now)
    {
        var session = ParseWindow(root, "five_hour", "fiveHour", "5-hour", UsageMetricKind.Session, 300);
        var weekly = ParseWeeklyAllLimit(root) ??
                     ParseWindow(root, "seven_day", "sevenDay", "Weekly", UsageMetricKind.Rolling, 10_080);

        var metrics = new List<UsageMetric>();
        if (session is null && weekly is null)
        {
            // Free and some education plans answer with both windows set to null: there is no limit
            // to meter, which is not a failure and must not read as 0% used.
            if (!IsExplicitNull(root, "five_hour") || !IsExplicitNull(root, "seven_day"))
            {
                throw new ClaudeCodeUsageException(
                    "Claude Code returned account details without five-hour or weekly usage data.");
            }

            metrics.Add(new UsageMetric(
                "Usage limits",
                UsageMetricKind.Balance,
                null,
                RemainingText: "Not reported",
                UsageText: NoLimitsReportedText));
        }

        if (session is not null)
        {
            metrics.Add(session);
        }

        if (weekly is not null)
        {
            metrics.Add(weekly);
        }

        metrics.AddRange(ParseModelWeeklyLimits(root));

        var extraUsage = ParseExtraUsage(root);
        if (extraUsage is not null)
        {
            metrics.Add(extraUsage);
        }

        var resetGrants = CountResetGrants(root, now);
        if (resetGrants > 0)
        {
            metrics.Add(new UsageMetric(
                "Reset credits",
                UsageMetricKind.Balance,
                null,
                RemainingText: resetGrants.ToString(CultureInfo.CurrentCulture),
                UsageText: $"Free limit reset{(resetGrants == 1 ? string.Empty : "s")} available"));
        }

        return new UsageSnapshot(
            plan,
            metrics,
            DateTimeOffset.Now,
            "claude",
            "Claude Code");
    }

    private ClaudeCredentials LoadCredentials()
    {
        var environmentToken = CredentialInput.NormalizeToken(
            Environment.GetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_TOKEN"));
        if (environmentToken is not null)
        {
            var scopes = (Environment.GetEnvironmentVariable("USAGEAI_CLAUDE_OAUTH_SCOPES") ?? "user:profile")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(scope => scope.Length <= 128)
                .Take(32)
                .ToArray();
            return new ClaudeCredentials(
                environmentToken,
                null,
                scopes,
                "Claude (OAuth)",
                IsEnvironmentOverride: true);
        }

        Exception? fileError = null;
        var credentialPath = GetCredentialPath();
        if (File.Exists(credentialPath))
        {
            try
            {
                return ParseCredentials(SecureLocalFile.ReadAllText(credentialPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ClaudeCodeUsageException)
            {
                fileError = exception;
            }
        }

        foreach (var savedCredential in _keyringPasswords())
        {
            try
            {
                return ParseCredentials(savedCredential);
            }
            catch (Exception exception) when (exception is JsonException or ClaudeCodeUsageException)
            {
                fileError ??= exception;
            }
        }

        if (fileError is not null)
        {
            throw new ClaudeCodeUsageException(
                "Claude Code's saved login could not be read. Run `claude` or use the Claude Code extension to sign in again.",
                fileError);
        }

        throw new ClaudeCodeUsageException(
            "Claude Code is not signed in. Run `claude` or sign in through the Claude Code extension, then refresh.");
    }

    private async Task<ClaudeCredentials> RefreshExpiredCredentialsThroughClaudeAsync(
        ClaudeCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (!credentials.IsExpired)
        {
            return credentials;
        }

        return await RecoverCredentialsThroughClaudeAsync(cancellationToken) ?? credentials;
    }

    private async Task<ClaudeCredentials?> RecoverCredentialsThroughClaudeAsync(
        CancellationToken cancellationToken)
    {
        bool claudeOwnsFreshLogin;
        try
        {
            claudeOwnsFreshLogin = await _claudeAuthProbe(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (!claudeOwnsFreshLogin)
        {
            return null;
        }

        var startedAt = Stopwatch.GetTimestamp();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var refreshed = LoadCredentials();
                if (!refreshed.IsExpired)
                {
                    return refreshed;
                }
            }
            catch (ClaudeCodeUsageException)
            {
                // Claude may be replacing its credential store; retry only inside the bounded window.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        while (Stopwatch.GetElapsedTime(startedAt) < CredentialObservationWindow);

        return null;
    }

    /// <summary>
    /// Asks the official CLI to validate, and if needed refresh, Claude Code's login. `auth status`
    /// only reports whether a login exists and never refreshes an expired access token, so while the
    /// stored token is still expired a command that needs a live token (`mcp list`) makes the CLI
    /// refresh it through its own credential store. UsageAI never touches the refresh token itself.
    /// </summary>
    internal static async Task<bool> RunClaudeAuthRecoveryAsync(CancellationToken cancellationToken)
    {
        var launch = FindClaudeLaunch();
        if (launch is null || !await RunClaudeAuthStatusAsync(launch, cancellationToken))
        {
            return false;
        }

        if (!HasFreshStoredCredential())
        {
            await NudgeClaudeCredentialRefreshAsync(launch, cancellationToken);
        }

        return true;
    }

    private static async Task<bool> RunClaudeAuthStatusAsync(
        ClaudeLaunch launch,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AuthProbeTimeout);
        using var process = StartClaudeCli(launch, "auth", "status", "--json");
        var stdoutTask = ProcessSecurity.DrainTextAsync(
            process.StandardOutput,
            MaxAuthProbeOutputCharacters,
            timeout.Token);
        var stderrTask = ProcessSecurity.DrainTextAsync(
            process.StandardError,
            MaxAuthProbeOutputCharacters,
            timeout.Token);

        try
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            await TryDrainProbeErrorAsync(stderrTask);
            if (process.ExitCode != 0)
            {
                return false;
            }

            using var document = JsonDocument.Parse(stdout, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.TryGetProperty("loggedIn", out var loggedIn) &&
                   loggedIn.ValueKind == JsonValueKind.True;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await TryDrainProbeErrorAsync(stderrTask);
            return false;
        }
        finally
        {
            ProcessSecurity.TryKill(process);
        }
    }

    private static async Task NudgeClaudeCredentialRefreshAsync(
        ClaudeLaunch launch,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RefreshNudgeTimeout);
        using var process = StartClaudeCli(launch, "mcp", "list");
        var stdoutTask = ProcessSecurity.DrainTextAsync(
            process.StandardOutput,
            MaxAuthProbeOutputCharacters,
            timeout.Token);
        var stderrTask = ProcessSecurity.DrainTextAsync(
            process.StandardError,
            MaxAuthProbeOutputCharacters,
            timeout.Token);

        try
        {
            process.StandardInput.Close();
            var exited = process.WaitForExitAsync(timeout.Token);

            // The refresh lands in the first seconds; the MCP health checks after it are not needed.
            while (!exited.IsCompleted && !HasFreshStoredCredential())
            {
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Out of time; the caller's observation window decides whether the refresh landed.
        }
        finally
        {
            ProcessSecurity.TryKill(process);
            await TryDrainProbeErrorAsync(stdoutTask);
            await TryDrainProbeErrorAsync(stderrTask);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool HasFreshStoredCredential()
    {
        try
        {
            var credentialPath = GetCredentialPath();
            return File.Exists(credentialPath) &&
                   !ParseCredentials(SecureLocalFile.ReadAllText(credentialPath)).IsExpired;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ClaudeCodeUsageException)
        {
            return false;
        }
    }

    private static Process StartClaudeCli(ClaudeLaunch launch, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        ProcessSecurity.ApplyMinimalEnvironment(
            startInfo,
            "CLAUDE_CONFIG_DIR",
            "CLAUDE_CODE_GIT_BASH_PATH",
            "NODE_EXTRA_CA_CERTS",
            "SSL_CERT_DIR",
            "SSL_CERT_FILE");
        startInfo.Environment["DISABLE_AUTOUPDATER"] = "1";
        if (launch.Script is not null)
        {
            startInfo.ArgumentList.Add(launch.Script);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new ClaudeCodeUsageException("Windows could not start the Claude CLI.");
    }

    private static ClaudeLaunch? FindClaudeLaunch()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_PATH");
        if (IsUsableClaudeCommand(configured))
        {
            return CreateClaudeLaunch(Path.GetFullPath(configured!));
        }

        var candidates = new List<string>();
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(cleanDirectory))
            {
                continue;
            }

            candidates.Add(Path.GetFullPath(Path.Combine(cleanDirectory, "claude.exe")));
            candidates.Add(Path.GetFullPath(Path.Combine(cleanDirectory, "claude.cmd")));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(userProfile, ".local", "bin", "claude.exe"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "claude.cmd"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "claude",
            "claude.exe"));

        foreach (var command in candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var launch = CreateClaudeLaunch(command);
            if (launch is not null)
            {
                return launch;
            }
        }

        // Last, so a standalone install keeps priority: the binary the Claude Code editor extension
        // ships for itself, as the editor extension's own recovery uses.
        var bundled = EditorExtensionBinaries.FindClaude(userProfile, RuntimeInformation.OSArchitecture);
        return bundled is null ? null : new ClaudeLaunch(bundled, null);
    }

    private static ClaudeLaunch? CreateClaudeLaunch(string command)
    {
        if (!command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return new ClaudeLaunch(command, null);
        }

        var npmDirectory = Path.GetDirectoryName(command);
        if (npmDirectory is null)
        {
            return null;
        }

        var script = Path.Combine(
            npmDirectory,
            "node_modules",
            "@anthropic-ai",
            "claude-code",
            "cli.js");
        if (!File.Exists(script))
        {
            return null;
        }

        var localNode = Path.Combine(npmDirectory, "node.exe");
        var node = File.Exists(localNode)
            ? localNode
            : ProcessSecurity.FindAbsoluteExecutableOnPath("node.exe");
        return node is null ? null : new ClaudeLaunch(Path.GetFullPath(node), Path.GetFullPath(script));
    }

    private static bool IsUsableClaudeCommand(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.Contains('"') &&
        Path.IsPathFullyQualified(path) &&
        (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) &&
        File.Exists(path);

    private static async Task TryDrainProbeErrorAsync(Task<string> stderrTask)
    {
        try
        {
            await stderrTask.WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // Probe diagnostics are intentionally discarded and never shown to the user.
        }
    }

    private static ClaudeCredentials ParseCredentials(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 32,
        });
        var root = document.RootElement;
        var oauth = TryGetObject(root, "claudeAiOauth", out var nested) ? nested : root;
        var accessToken = CredentialInput.NormalizeToken(GetString(oauth, "accessToken"));
        if (accessToken is null)
        {
            throw new ClaudeCodeUsageException(
                "Claude Code's saved login has no OAuth access token. Run `claude` or use the Claude Code extension to sign in again.");
        }

        var scopes = Array.Empty<string>();
        if (oauth.TryGetProperty("scopes", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.Array)
        {
            scopes = scopeElement.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Where(value => value.Length <= 128)
                .Take(32)
                .ToArray();
        }

        DateTimeOffset? expiresAt = null;
        if (GetDouble(oauth, "expiresAt") is { } expiresAtMilliseconds)
        {
            try
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAtMilliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                expiresAt = DateTimeOffset.MinValue;
            }
        }

        var planSource = GetString(oauth, "subscriptionType") ?? GetString(oauth, "rateLimitTier");
        return new ClaudeCredentials(
            accessToken,
            expiresAt,
            scopes,
            FormatPlan(planSource));
    }

    private static UsageMetric? ParseWindow(
        JsonElement root,
        string snakeCaseName,
        string camelCaseName,
        string displayName,
        UsageMetricKind kind,
        long durationMinutes)
    {
        if (!TryGetObject(root, snakeCaseName, out var window) &&
            !TryGetObject(root, camelCaseName, out window))
        {
            return null;
        }

        var utilization = GetDouble(window, "utilization");
        if (utilization is null || !double.IsFinite(utilization.Value))
        {
            return null;
        }

        var usedPercent = NormalizeUtilization(utilization.Value);
        return new UsageMetric(
            displayName,
            kind,
            usedPercent,
            ParseTimestamp(GetString(window, "resets_at") ?? GetString(window, "resetsAt")),
            durationMinutes);
    }

    private static UsageMetric? ParseWeeklyAllLimit(JsonElement root) =>
        FindLimit(
            root,
            kind => kind is "weekly_all" or "all_models" or "weekly_models",
            "Weekly",
            requireWeeklyGroup: true);

    private static UsageMetric? ParseNamedLimit(JsonElement root, string limitKind, string displayName) =>
        FindLimit(
            root,
            kind => string.Equals(kind, limitKind, StringComparison.Ordinal),
            displayName,
            requireWeeklyGroup: false);

    private static bool IsExplicitNull(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Null;

    /// <summary>
    /// Weekly limits that apply to one model, named "Weekly Opus", "Weekly Sonnet" and so on. The
    /// newer <c>limits</c> entries carry the model in <c>scope.model</c>; older answers report
    /// <c>seven_day_opus</c> / <c>seven_day_sonnet</c> and a <c>weekly_opus</c> limit kind, which
    /// are read only when no scoped entry names that model. The all-models limit stays the main
    /// weekly row.
    /// </summary>
    internal static IReadOnlyList<UsageMetric> ParseModelWeeklyLimits(JsonElement root)
    {
        var found = new List<UsageMetric>();
        void Add(UsageMetric? metric)
        {
            if (metric is not null &&
                found.Count < MaxModelWeeklyLimits &&
                !found.Any(existing => string.Equals(existing.Name, metric.Name, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(metric);
            }
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("limits", out var limits) &&
            limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.ValueKind != JsonValueKind.Object ||
                    GetString(limit, "kind") is "weekly_all" or "all_models" or "weekly_models" or "session" ||
                    !string.Equals(GetString(limit, "group") ?? "weekly", "weekly", StringComparison.OrdinalIgnoreCase) ||
                    !TryGetObject(limit, "scope", out var scope) ||
                    !TryGetObject(scope, "model", out var model) ||
                    ModelLabel(GetString(model, "display_name") ?? GetString(model, "displayName") ?? GetString(model, "id")) is not { } label ||
                    GetDouble(limit, "percent") is not { } percent ||
                    !double.IsFinite(percent))
                {
                    continue;
                }

                Add(new UsageMetric(
                    $"Weekly {label}",
                    UsageMetricKind.Rolling,
                    NormalizeUtilization(percent),
                    ParseTimestamp(GetString(limit, "resets_at") ?? GetString(limit, "resetsAt")),
                    10_080));
            }
        }

        Add(ParseNamedLimit(root, "weekly_opus", "Weekly Opus"));
        Add(ParseWindow(root, "seven_day_opus", "sevenDayOpus", "Weekly Opus", UsageMetricKind.Rolling, 10_080));
        Add(ParseWindow(root, "seven_day_sonnet", "sevenDaySonnet", "Weekly Sonnet", UsageMetricKind.Rolling, 10_080));
        return found;
    }

    /// <summary>A model name made short and safe for a metric name: "Claude Opus 4.5" reads "Opus 4.5".</summary>
    private static string? ModelLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cleaned = new string(name.Where(character => !char.IsControl(character) && character is not '(' and not ')').ToArray()).Trim();
        if (cleaned.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["Claude ".Length..].Trim();
        }

        return cleaned.Length switch
        {
            0 => null,
            > 40 => cleaned[..40].TrimEnd(),
            _ => cleaned,
        };
    }

    /// <summary>
    /// Saved "reset for free" grants that can be used now: each grant counts once per reset it has
    /// left, a paused or not-yet-started or expired grant not at all. Grant ids are redemption handles
    /// and are never read. A grant without its <c>paused</c> flag is skipped, and an implausible total
    /// is treated as malformed.
    /// </summary>
    internal static int CountResetGrants(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetObject(root, "cedar_ember", out var resets) ||
            !resets.TryGetProperty("grants", out var grants) ||
            grants.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var total = 0;
        foreach (var grant in grants.EnumerateArray())
        {
            if (grant.ValueKind != JsonValueKind.Object ||
                !grant.TryGetProperty("paused", out var paused) ||
                paused.ValueKind != JsonValueKind.False ||
                GetDouble(grant, "resets_left") is not { } left ||
                left < 1 ||
                left > MaxResetGrants)
            {
                continue;
            }

            if ((ParseTimestamp(GetString(grant, "starts_at")) is { } startsAt && startsAt > now) ||
                (ParseTimestamp(GetString(grant, "ends_at")) is { } endsAt && endsAt <= now))
            {
                continue;
            }

            total += (int)left;
            if (total > MaxResetGrants)
            {
                return 0;
            }
        }

        return total;
    }

    private static UsageMetric? FindLimit(
        JsonElement root,
        Func<string, bool> matchesKind,
        string displayName,
        bool requireWeeklyGroup)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var kind = GetString(limit, "kind");
            if (kind is null || !matchesKind(kind))
            {
                continue;
            }

            var group = GetString(limit, "group");
            if (requireWeeklyGroup && group is not null &&
                !group.Equals("weekly", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var percent = GetDouble(limit, "percent");
            if (percent is null || !double.IsFinite(percent.Value))
            {
                continue;
            }

            return new UsageMetric(
                displayName,
                UsageMetricKind.Rolling,
                (int)Math.Round(Math.Clamp(percent.Value, 0, 100), MidpointRounding.AwayFromZero),
                ParseTimestamp(GetString(limit, "resets_at") ?? GetString(limit, "resetsAt")),
                10_080);
        }

        return null;
    }

    private static UsageMetric? ParseExtraUsage(JsonElement root)
    {
        if (!TryGetObject(root, "extra_usage", out var extraUsage) &&
            !TryGetObject(root, "extraUsage", out extraUsage))
        {
            return null;
        }

        return CreateExtraUsageMetric(extraUsage);
    }

    /// <summary>Turns an extra-usage or overage-spend object into a balance metric.</summary>
    internal static UsageMetric? CreateExtraUsageMetric(JsonElement extraUsage)
    {
        var value = FormatExtraUsageObject(extraUsage);
        return value is null
            ? null
            : new UsageMetric(
                "Extra usage",
                UsageMetricKind.Balance,
                null,
                RemainingText: value,
                UsageText: "Charged beyond the plan limit");
    }

    internal static string? FormatExtraUsageObject(JsonElement extraUsage)
    {
        if (extraUsage.ValueKind != JsonValueKind.Object ||
            !GetBoolean(extraUsage, "is_enabled", "isEnabled"))
        {
            return null;
        }

        var usedCents = GetDouble(extraUsage, "used_credits") ?? GetDouble(extraUsage, "usedCredits");
        var limitCents = GetDouble(extraUsage, "monthly_credit_limit") ??
                         GetDouble(extraUsage, "monthly_limit") ??
                         GetDouble(extraUsage, "monthlyLimit");
        if (usedCents is null)
        {
            return "ENABLED";
        }

        var currency = GetString(extraUsage, "currency") ?? "USD";
        var used = FormatCurrency(usedCents.Value / 100, currency);
        return limitCents is null
            ? used
            : $"{used} / {FormatCurrency(limitCents.Value / 100, currency)}";
    }

    // Anthropic reports utilization as a percentage (0-100), never a fraction: 1.0 means 1% used.
    private static int NormalizeUtilization(double utilization) =>
        (int)Math.Round(Math.Clamp(utilization, 0, 100), MidpointRounding.AwayFromZero);

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToLocalTime()
            : null;

    internal static string FormatPlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return "Claude";
        }

        var normalized = plan.Trim().ToLowerInvariant();
        if (normalized.Contains("claude_max_5x") || normalized.Contains("claude_max_5"))
        {
            return "Claude Max 5x";
        }

        if (normalized.Contains("claude_max_20x") || normalized.Contains("claude_max_20"))
        {
            return "Claude Max 20x";
        }

        return normalized switch
        {
            "free" => "Claude Free",
            "pro" or "claude_pro" => "Claude Pro",
            "max" => "Claude Max",
            "team" => "Claude Team",
            "enterprise" => "Claude Enterprise",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan.Replace('_', ' ').Trim()),
        };
    }

    private static string FormatCurrency(double value, string currency) =>
        currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
            ? value.ToString("C2", CultureInfo.GetCultureInfo("en-US"))
            : $"{value:N2} {currency.ToUpperInvariant()}";

    private static string GetCredentialPath()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            configuredDirectory = configuredDirectory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(configuredDirectory))
            {
                throw new ClaudeCodeUsageException("CLAUDE_CONFIG_DIR must be an absolute path.");
            }

            return Path.Combine(Path.GetFullPath(configuredDirectory), ".credentials.json");
        }

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.GetFullPath(Path.Combine(userProfile, ".claude", ".credentials.json"));
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    // TryGetDouble throws on anything but a number, so the kind is checked first: one unexpected
    // string must skip that field, not fail the whole reading.
    private static double? GetDouble(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetDouble(out var value)
            ? value
            : null;

    private static bool GetBoolean(JsonElement element, string snakeCaseName, string camelCaseName) =>
        element.TryGetProperty(snakeCaseName, out var property) && property.ValueKind == JsonValueKind.True ||
        element.TryGetProperty(camelCaseName, out property) && property.ValueKind == JsonValueKind.True;

    internal sealed record ClaudeCredentials(
        string AccessToken,
        DateTimeOffset? ExpiresAt,
        IReadOnlyList<string> Scopes,
        string Plan,
        bool IsEnvironmentOverride = false)
    {
        public bool IsExpired => ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow;
    }

    private sealed record ClaudeLaunch(string Executable, string? Script);
}

internal sealed class ClaudeCodeUsageException : Exception, IThrottledUsageException
{
    public ClaudeCodeUsageException(string message)
        : base(message)
    {
    }

    public ClaudeCodeUsageException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Set from the provider's Retry-After header; zero when the provider did not say.</summary>
    public TimeSpan RetryAfter { get; init; }

    /// <summary>True when one bounded official CLI validation may repair this failure.</summary>
    internal bool CanRetryAfterAuthProbe { get; init; }
}
