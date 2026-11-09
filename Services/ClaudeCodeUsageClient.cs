using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

internal sealed class ClaudeCodeUsageClient : IAccountUsageClient
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
    private readonly Func<string?> _claudeCliVersion;

    /// <summary>Until when the opt-in web path is skipped after claude.ai asked for a browser check.</summary>
    private DateTimeOffset _webRetryAfterUtc;

    /// <summary>
    /// The login folder of an extra account, or null for the default login. An extra account reads
    /// only its own folder's credential file: never the environment token, the web session or the
    /// shared Credential Manager item, which all belong to the default login.
    /// </summary>
    private readonly string? _configDirectory;

    public ClaudeCodeUsageClient()
        : this(SharedClient, null, RunClaudeAuthRecoveryAsync)
    {
    }

    /// <summary>A client for the login in <paramref name="configDirectory"/>, an extra account.</summary>
    public ClaudeCodeUsageClient(string configDirectory)
        : this(
            SharedClient,
            static () => Array.Empty<string>(),
            cancellationToken => RunClaudeAuthRecoveryAsync(configDirectory, cancellationToken),
            configDirectory)
    {
    }

    internal ClaudeCodeUsageClient(
        HttpClient client,
        Func<IReadOnlyList<string>>? keyringPasswords = null,
        Func<CancellationToken, Task<bool>>? claudeAuthProbe = null,
        string? configDirectory = null,
        Func<string?>? claudeCliVersion = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _claudeCliVersion = claudeCliVersion ?? InstalledClaudeCliVersion;
        _configDirectory = configDirectory is null ? null : ProviderKey.NormalizeFolder(configDirectory);
        _keyringPasswords = _configDirectory is not null
            ? static () => Array.Empty<string>()
            : keyringPasswords ?? (() => WindowsCredentialReader.FindKeyringPasswords(CredentialManagerService));
        _claudeAuthProbe = claudeAuthProbe ?? (_ => Task.FromResult(false));
        AccountId = _configDirectory is null ? ProviderKey.DefaultAccountId : ProviderKey.AccountIdFor(_configDirectory);
    }

    public string Id => ProviderKey.Compose(ProviderId, AccountId);

    public string ProviderId => "claude";

    public string AccountId { get; }

    public string LoginFolder => _configDirectory ?? AccountDiscovery.DefaultFolder(ProviderId);

    public string DisplayName => "Claude Code";

    public string SignInCommand => _configDirectory is null ? AccountCommands.SignInMain(ProviderId) : AccountCommands.SignIn(ProviderId, _configDirectory);

    public Uri AccountUrl { get; } = new("https://claude.ai/settings/usage");

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetUsageCoreAsync(cancellationToken);
        return _configDirectory is null ? snapshot : snapshot with { ProviderId = Id };
    }

    private async Task<UsageSnapshot> GetUsageCoreAsync(CancellationToken cancellationToken)
    {
        if (_configDirectory is null && ClaudeWebUsageClient.IsConfigured && DateTimeOffset.UtcNow >= _webRetryAfterUtc)
        {
            var webTimer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var viaWeb = await ClaudeWebUsageClient.GetUsageAsync(cancellationToken);
                ProviderAttempts.Record("claude-web", "answered", webTimer);
                return viaWeb;
            }
            catch (ClaudeWebUsageException exception)
            {
                // An expired or unavailable opt-in web session falls back to Claude Code OAuth. After a
                // Cloudflare browser check claude.ai is left alone for a while rather than challenged
                // on every refresh; the session key itself is kept.
                ProviderAttempts.Record(
                    "claude-web",
                    exception.RetryAfter > TimeSpan.Zero ? "browser-check"
                        : exception.Message.Contains("no longer valid", StringComparison.Ordinal) ? "session-rejected"
                        : "failed",
                    webTimer);
                if (exception.RetryAfter > TimeSpan.Zero)
                {
                    _webRetryAfterUtc = DateTimeOffset.UtcNow + exception.RetryAfter;
                }
            }
        }
        else if (_configDirectory is null && ClaudeWebUsageClient.IsConfigured)
        {
            ProviderAttempts.Record("claude-web", "skipped-browser-check");
        }

        var credentials = await RefreshExpiredCredentialsThroughClaudeAsync(
            LoadCredentials(),
            cancellationToken);
        ValidateCredentials(credentials);

        var oauthTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var snapshot = await RequestUsageAsync(credentials, cancellationToken);
            ProviderAttempts.Record("claude-oauth", "answered", oauthTimer);
            return snapshot;
        }
        catch (ClaudeCodeUsageException exception) when (
            exception.CanRetryAfterAuthProbe && !credentials.IsEnvironmentOverride)
        {
            ProviderAttempts.Record("claude-oauth", OAuthOutcome(exception), oauthTimer);
            var recoveryTimer = System.Diagnostics.Stopwatch.StartNew();
            var recovered = await RecoverCredentialsThroughClaudeAsync(cancellationToken);
            if (recovered is null)
            {
                ProviderAttempts.Record("claude-cli-recovery", "no-fresh-login", recoveryTimer);
                throw;
            }

            ProviderAttempts.Record("claude-cli-recovery", "refreshed", recoveryTimer);
            ValidateCredentials(recovered);
            var retryTimer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var retried = await RequestUsageAsync(recovered, cancellationToken);
                ProviderAttempts.Record("claude-oauth-retry", "answered", retryTimer);
                return retried;
            }
            catch (ClaudeCodeUsageException retryException)
            {
                ProviderAttempts.Record("claude-oauth-retry", OAuthOutcome(retryException), retryTimer);
                throw;
            }
        }
        catch (ClaudeCodeUsageException exception)
        {
            ProviderAttempts.Record("claude-oauth", OAuthOutcome(exception), oauthTimer);
            throw;
        }
    }

    /// <summary>The fixed-vocabulary outcome <c>--diagnose</c> reports for a failed OAuth request.</summary>
    private static string OAuthOutcome(ClaudeCodeUsageException exception) =>
        exception.RetryAfter > TimeSpan.Zero || exception.Message.Contains("rate-limited", StringComparison.Ordinal) ? "rate-limited"
        : exception.Message.Contains("has expired", StringComparison.Ordinal) ? "login-expired"
        : exception.Message.Contains("cannot read", StringComparison.Ordinal) ? "forbidden"
        : exception.Message.Contains("within", StringComparison.Ordinal) ? "timeout"
        : "failed";

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
            // Saved limit resets are reported only when asked for, and only to the Claude CLI, so
            // this one request carries the CLI's agent. A refusal of it (400, 401 or 403) is asked
            // once more without the parameter, as UsageAI, so usage never depends on either; an
            // expired login is refused again there and handled below as before.
            var response = await SendUsageRequestAsync(
                UsageWithResetCreditsEndpoint,
                credentials,
                ClaudeCliUserAgent(_claudeCliVersion()),
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                response = await SendUsageRequestAsync(UsageEndpoint, credentials, AppIdentity.UserAgent, cancellationToken);
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
        string userAgent,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("anthropic-beta", OAuthBeta);
        request.Headers.UserAgent.ParseAdd(userAgent);
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

        metrics.AddRange(ParseResetGrants(root, now));

        return new UsageSnapshot(
            plan,
            metrics,
            DateTimeOffset.Now,
            "claude",
            "Claude Code");
    }

    private ClaudeCredentials LoadCredentials()
    {
        if (_configDirectory is not null && !Directory.Exists(_configDirectory))
        {
            throw new ClaudeCodeUsageException(
                $"Claude login not found at {_configDirectory}. Forget it or locate it in Settings → Accounts.");
        }

        // An explicit token stands for the default login only; an extra account is its own folder.
        var environmentToken = _configDirectory is not null ? null : CredentialInput.NormalizeToken(
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
        var credentialPath = GetCredentialPath(_configDirectory);
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

        if (_configDirectory is not null)
        {
            throw new ClaudeCodeUsageException(
                $"Claude Code is not signed in at {_configDirectory}. Re-authenticate it in Settings → Accounts, then refresh.");
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
    internal static Task<bool> RunClaudeAuthRecoveryAsync(CancellationToken cancellationToken) =>
        RunClaudeAuthRecoveryAsync(configDirectory: null, cancellationToken);

    /// <summary>
    /// The recovery for one login folder: both CLI children run with <c>CLAUDE_CONFIG_DIR</c> set to
    /// it explicitly, so an extra account is never refreshed through the default login's folder.
    /// </summary>
    internal static async Task<bool> RunClaudeAuthRecoveryAsync(string? configDirectory, CancellationToken cancellationToken)
    {
        var launch = FindClaudeLaunch();
        if (launch is null || !await RunClaudeAuthStatusAsync(launch, configDirectory, cancellationToken))
        {
            return false;
        }

        if (!HasFreshStoredCredential(configDirectory))
        {
            await NudgeClaudeCredentialRefreshAsync(launch, configDirectory, cancellationToken);
        }

        return true;
    }

    private static async Task<bool> RunClaudeAuthStatusAsync(
        ClaudeLaunch launch,
        string? configDirectory,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AuthProbeTimeout);
        using var process = StartClaudeCli(launch, configDirectory, "auth", "status", "--json");
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
        string? configDirectory,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RefreshNudgeTimeout);
        using var process = StartClaudeCli(launch, configDirectory, "mcp", "list");
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
            while (!exited.IsCompleted && !HasFreshStoredCredential(configDirectory))
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

    private static bool HasFreshStoredCredential(string? configDirectory)
    {
        try
        {
            var credentialPath = GetCredentialPath(configDirectory);
            return File.Exists(credentialPath) &&
                   !ParseCredentials(SecureLocalFile.ReadAllText(credentialPath)).IsExpired;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ClaudeCodeUsageException)
        {
            return false;
        }
    }

    private static Process StartClaudeCli(ClaudeLaunch launch, string? configDirectory, params string[] arguments) =>
        Process.Start(CreateCliStartInfo(launch, configDirectory, arguments))
            ?? throw new ClaudeCodeUsageException("Windows could not start the Claude CLI.");

    /// <summary>
    /// How every Claude CLI child starts — the recovery probes and the sign-in alike: no window,
    /// redirected streams, the minimal environment, and a given login folder set explicitly rather
    /// than inherited, so the child reads exactly that login. The main login's children get no
    /// <c>CLAUDE_CONFIG_DIR</c> at all: an inherited one would point them at another folder than
    /// the one UsageAI reads, and setting it to <c>~\.claude</c> would move the CLI's profile from
    /// <c>~\.claude.json</c> into the folder.
    /// </summary>
    private static ProcessStartInfo CreateCliStartInfo(ClaudeLaunch launch, string? configDirectory, IEnumerable<string> arguments)
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
            "CLAUDE_CODE_GIT_BASH_PATH",
            "NODE_EXTRA_CA_CERTS",
            "SSL_CERT_DIR",
            "SSL_CERT_FILE");
        startInfo.Environment["DISABLE_AUTOUPDATER"] = "1";
        if (configDirectory is not null)
        {
            startInfo.Environment["CLAUDE_CONFIG_DIR"] = configDirectory;
        }

        if (launch.Script is not null)
        {
            startInfo.ArgumentList.Add(launch.Script);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>
    /// The official login, <c>claude auth login --claudeai</c>, pointed at <paramref name="configDirectory"/>.
    /// The CLI writes its own credential file there; UsageAI only starts it. Null when no Claude
    /// CLI is installed.
    /// </summary>
    internal static ProcessStartInfo? CreateLoginStartInfo(string configDirectory) =>
        FindClaudeLaunch() is { } launch
            ? CreateCliStartInfo(launch, configDirectory, LoginArguments)
            : null;

    private static readonly string[] LoginArguments = ["auth", "login", "--claudeai"];

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

    /// <summary>The CLI version sent when the installed one cannot be read.</summary>
    internal const string FallbackClaudeCliVersion = "2.1.0";

    private static readonly TimeSpan CliVersionLifetime = TimeSpan.FromMinutes(30);
    private static readonly object CliVersionLock = new();
    private static (DateTimeOffset ReadAt, string? Version)? _cliVersion;

    /// <summary>
    /// The User-Agent of the request that asks for saved resets. Claude reports them only to its
    /// own CLI (<c>ineligible_reason: "surface"</c> otherwise), so that one request names itself as
    /// the CLI does, with the installed version; every other request keeps UsageAI's own agent.
    /// </summary>
    internal static string ClaudeCliUserAgent(string? version) =>
        $"claude-cli/{(IsCliVersion(version) ? version : FallbackClaudeCliVersion)} (external, cli)";

    private static bool IsCliVersion(string? version) =>
        version is { Length: > 0 and <= 32 } &&
        version.Split('.') is { Length: 3 } parts &&
        parts.All(part => part.Length is > 0 and <= 9 && part.All(char.IsAsciiDigit));

    internal static void ForgetInstalledClaudeCliVersion()
    {
        lock (CliVersionLock)
        {
            _cliVersion = null;
        }
    }

    /// <summary>
    /// The installed Claude CLI's version, read from its files and never by running it: npm's
    /// <c>package.json</c>, the version in the Claude Code editor extension's folder name, or the
    /// executable's file version. Kept for 30 minutes; the lock makes concurrent account refreshes
    /// share one read. It never throws: the version only decorates a header, so no unreadable or
    /// malformed file may cost the usage request.
    /// </summary>
    internal static string? InstalledClaudeCliVersion()
    {
        lock (CliVersionLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cliVersion is { } cached && now - cached.ReadAt < CliVersionLifetime)
            {
                return cached.Version;
            }

            string? version = null;
            try
            {
                version = FindClaudeLaunch() is { } launch ? ReadClaudeCliVersion(launch) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                                  InvalidDataException or InvalidOperationException or ArgumentException or
                                                  NotSupportedException or System.Security.SecurityException)
            {
            }

            _cliVersion = (now, version);
            return version;
        }
    }

    private static string? ReadClaudeCliVersion(ClaudeLaunch launch)
    {
        if (launch.Script is { } script)
        {
            var package = Path.Combine(Path.GetDirectoryName(script) ?? string.Empty, "package.json");
            if (!File.Exists(package))
            {
                return null;
            }

            using var document = JsonDocument.Parse(SecureLocalFile.ReadAllText(package));
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? CliVersionOf(GetString(document.RootElement, "version"))
                : null;
        }

        // The editor extension's copy sits in a folder named for its version, as the extension reads it.
        var folder = System.Text.RegularExpressions.Regex.Match(
            launch.Executable,
            @"anthropic\.claude-code-(\d+\.\d+\.\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (folder.Success)
        {
            return CliVersionOf(folder.Groups[1].Value);
        }

        var info = FileVersionInfo.GetVersionInfo(launch.Executable);
        return info.FileMajorPart + info.FileMinorPart + info.FileBuildPart > 0
            ? CliVersionOf($"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}")
            : null;
    }

    /// <summary>"2.1.321" from "2.1.321", "2.1.321-beta.1" or a four-part version; null for anything else.</summary>
    private static string? CliVersionOf(string? raw) =>
        raw is not null &&
        Version.TryParse(raw.Split('-', '+')[0], out var parsed) &&
        parsed.Build >= 0 &&
        $"{parsed.Major}.{parsed.Minor}.{parsed.Build}" is var version &&
        IsCliVersion(version)
            ? version
            : null;

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

        // The legacy fields describe the same limits as the scoped entries, so they fill in only a
        // model family no scoped entry has named ("Weekly Opus 4.5" already covers "Weekly Opus").
        bool Named(string family) => found.Any(metric => metric.Name.Contains(family, StringComparison.OrdinalIgnoreCase));
        if (!Named("Opus"))
        {
            Add(ParseNamedLimit(root, "weekly_opus", "Weekly Opus"));
            Add(ParseWindow(root, "seven_day_opus", "sevenDayOpus", "Weekly Opus", UsageMetricKind.Rolling, 10_080));
        }

        if (!Named("Sonnet"))
        {
            Add(ParseWindow(root, "seven_day_sonnet", "sevenDaySonnet", "Weekly Sonnet", UsageMetricKind.Rolling, 10_080));
        }

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

    /// <summary>The kinds of saved reset, named as the Claude CLI names them, in display order.</summary>
    private static readonly (string Kind, string Name, string Effect)[] ResetKinds =
    [
        ("fable", "Fable reset", "Gives back what Fable used this week"),
        ("session", "5-hour reset", "Clears the 5-hour limit"),
        ("weekly", "Weekly reset", "Clears the weekly limit"),
        ("full", "Full reset", "Clears the 5-hour and weekly limits"),
        (string.Empty, "Reset credits", "Free limit reset"),
    ];

    /// <summary>
    /// Saved "reset for free" grants that can be used now, one balance row per kind of reset: each
    /// grant counts once per reset it has left, a paused or not-yet-started or expired grant not at
    /// all. Grant ids are redemption handles and are never read. A grant without its <c>paused</c>
    /// flag is skipped, and an implausible total is treated as malformed.
    /// </summary>
    internal static IReadOnlyList<UsageMetric> ParseResetGrants(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetObject(root, "cedar_ember", out var resets) ||
            !resets.TryGetProperty("grants", out var grants) ||
            grants.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<UsageMetric>();
        }

        var total = 0;
        var byKind = new Dictionary<string, (int Count, DateTimeOffset? UseBy)>(StringComparer.Ordinal);
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

            var endsAt = ParseTimestamp(GetString(grant, "ends_at"));
            if ((ParseTimestamp(GetString(grant, "starts_at")) is { } startsAt && startsAt > now) ||
                endsAt <= now)
            {
                continue;
            }

            total += (int)left;
            if (total > MaxResetGrants)
            {
                return Array.Empty<UsageMetric>();
            }

            var kind = ResetGrantKind(grant);
            var (count, useBy) = byKind.GetValueOrDefault(kind);
            byKind[kind] = (count + (int)left, endsAt is { } end && (useBy is null || end < useBy) ? end : useBy);
        }

        return ResetKinds
            .Where(entry => byKind.ContainsKey(entry.Kind))
            .Select(entry =>
            {
                var (count, useBy) = byKind[entry.Kind];
                var effect = entry.Kind.Length == 0 && count != 1 ? entry.Effect + "s" : entry.Effect;
                return new UsageMetric(
                    entry.Name,
                    UsageMetricKind.Balance,
                    null,
                    RemainingText: count.ToString(CultureInfo.CurrentCulture),
                    UsageText: useBy is { } date ? $"{effect} · use by {UseByText(date, now)}" : effect);
            })
            .ToArray();
    }

    /// <summary>
    /// Which reset a grant is, by the Claude CLI's rule: one that gives back weekly use is a Fable
    /// reset; otherwise its <c>type</c>, else what its <c>clears</c> list names. Empty when unknown.
    /// </summary>
    private static string ResetGrantKind(JsonElement grant)
    {
        if (grant.TryGetProperty("gives_back_weekly", out var givesBack) && givesBack.ValueKind == JsonValueKind.True)
        {
            return "fable";
        }

        if (GetString(grant, "type") is { } type && type is "session" or "weekly" or "full")
        {
            return type;
        }

        if (!grant.TryGetProperty("clears", out var clears) || clears.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var limits = clears.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .ToArray();
        var session = limits.Contains("five_hour", StringComparer.Ordinal);
        var other = limits.Any(limit => !string.Equals(limit, "five_hour", StringComparison.Ordinal));
        return (session, other) switch
        {
            (true, true) => "full",
            (true, false) => "session",
            (false, true) => "weekly",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// A use-by date as "17 Nov" in local time, with the year ("5 Jan 2027") only when it is not the
    /// current one. English month names, like every other label; the same in the editor extension.
    /// </summary>
    internal static string UseByText(DateTimeOffset date, DateTimeOffset now)
    {
        var local = date.ToLocalTime();
        return local.ToString(local.Year == now.ToLocalTime().Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
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

    /// <summary>
    /// The credential file of <paramref name="configDirectory"/>, else of the main login in the
    /// standard <c>~\.claude</c>. An inherited <c>CLAUDE_CONFIG_DIR</c> never moves the main login:
    /// that folder is found as an account of its own.
    /// </summary>
    private static string GetCredentialPath(string? configDirectory) =>
        Path.Combine(configDirectory ?? AccountDiscovery.DefaultFolder(AccountDiscovery.ClaudeProviderId), ".credentials.json");

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
