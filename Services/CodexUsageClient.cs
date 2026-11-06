using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

/// <summary>The Codex login's access token, read from its <c>auth.json</c> for the usage request alone.</summary>
/// <param name="AccessToken">Sent only to <c>chatgpt.com</c>, never logged or stored.</param>
/// <param name="AccountId">The ChatGPT account the token belongs to, sent as <c>ChatGPT-Account-Id</c>.</param>
internal sealed record CodexHttpCredentials(string AccessToken, string? AccountId)
{
    public override string ToString() => "CodexHttpCredentials { AccessToken = <redacted> }";
}

internal sealed class CodexUsageClient : IAccountUsageClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The endpoint the Codex CLI itself reads usage from.</summary>
    internal static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");

    private static readonly HttpClient SharedClient = SecureHttp.CreateClient(TimeSpan.FromSeconds(15));

    /// <summary>A login is renewed by the CLI this long before it expires, so a token this close is left to it.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly Func<CodexHttpCredentials?> _loadCredentials;
    private readonly Func<CancellationToken, Task<(JsonElement RateLimits, JsonElement? Account)>> _appServer;

    /// <summary>The <c>CODEX_HOME</c> of an extra account, or null for the default login.</summary>
    private readonly string? _codexHome;

    public CodexUsageClient()
        : this(SharedClient, LoadHttpCredentials, GetRawUsageAsync)
    {
    }

    /// <summary>
    /// A client for the login in <paramref name="codexHome"/>, an extra account. Its HTTP path reads
    /// that folder's <c>auth.json</c>, and its <c>app-server</c> runs with <c>CODEX_HOME</c> set to it.
    /// </summary>
    public CodexUsageClient(string codexHome)
        : this(
            SharedClient,
            () => ReadHttpCredentials(ProviderKey.NormalizeFolder(codexHome), DateTimeOffset.UtcNow),
            cancellationToken => GetRawUsageAsync(ProviderKey.NormalizeFolder(codexHome), cancellationToken),
            codexHome)
    {
    }

    internal CodexUsageClient(
        HttpClient http,
        Func<CodexHttpCredentials?> loadCredentials,
        Func<CancellationToken, Task<(JsonElement RateLimits, JsonElement? Account)>>? appServer = null,
        string? codexHome = null)
    {
        _http = http;
        _loadCredentials = loadCredentials;
        _appServer = appServer ?? (cancellationToken => GetRawUsageAsync(cancellationToken));
        _codexHome = codexHome is null ? null : ProviderKey.NormalizeFolder(codexHome);
        AccountId = _codexHome is null ? ProviderKey.DefaultAccountId : ProviderKey.AccountIdFor(_codexHome);
    }

    public string ProviderId => "codex";

    public string AccountId { get; }

    public string LoginFolder => _codexHome ?? AccountDiscovery.DefaultFolder(ProviderId);
    private const int MaxProtocolLineCharacters = 131_072;
    private const int MaxProtocolMessagesPerResponse = 512;
    private const int MaxRetainedStandardErrorCharacters = 16_384;

    public string Id => ProviderKey.Compose(ProviderId, AccountId);

    public string DisplayName => "Codex";

    public string SignInCommand => _codexHome is null ? "codex login" : AccountCommands.SignIn(ProviderId, _codexHome);

    public Uri AccountUrl { get; } = new("https://chatgpt.com/codex/settings/usage");

    /// <summary>How long the optional account lookup may take after usage has already arrived.</summary>
    private static readonly TimeSpan AccountReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>At most this many extra rate-limit buckets (model-specific limits such as Spark) are shown.</summary>
    private const int MaxExtraBuckets = 6;

    /// <summary>
    /// Reads usage over HTTPS with the Codex login's own token when that login is a ChatGPT login
    /// that has not expired, which spares starting the Node-based CLI on every refresh. Everything
    /// else — no login file, an API-key login, an expired token, a custom backend, a rejected
    /// token, an outage, an unexpected answer — goes to <c>codex app-server</c>, which refreshes
    /// the login itself. UsageAI never refreshes or writes the token.
    /// </summary>
    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        if (_codexHome is not null && !Directory.Exists(_codexHome))
        {
            throw new CodexUsageException(
                $"Codex login not found at {_codexHome}. Forget it or locate it in Settings → Accounts.");
        }

        var snapshot = await GetUsageCoreAsync(cancellationToken);
        return _codexHome is null ? snapshot : snapshot with { ProviderId = Id };
    }

    private async Task<UsageSnapshot> GetUsageCoreAsync(CancellationToken cancellationToken)
    {
        if (_loadCredentials() is { } credentials)
        {
            var httpTimer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (await TryGetHttpUsageAsync(credentials, cancellationToken) is { } viaHttp)
                {
                    ProviderAttempts.Record("chatgpt-http", "answered", httpTimer);
                    return viaHttp;
                }

                ProviderAttempts.Record("chatgpt-http", "no-answer", httpTimer);
            }
            catch (CodexUsageException exception) when (exception.RetryAfter > TimeSpan.Zero)
            {
                ProviderAttempts.Record("chatgpt-http", "rate-limited", httpTimer);
                throw;
            }
        }
        else
        {
            ProviderAttempts.Record("chatgpt-http", "no-usable-login");
        }

        var appServerTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (rateLimits, account) = await _appServer(cancellationToken);
            var snapshot = ParseSnapshot(rateLimits, account) with { Source = "codex-app-server" };
            ProviderAttempts.Record("codex-app-server", "answered", appServerTimer);
            return snapshot;
        }
        catch (CodexUsageException exception)
        {
            ProviderAttempts.Record(
                "codex-app-server",
                exception.Message.Contains("not signed in", StringComparison.OrdinalIgnoreCase) ? "not-signed-in"
                    : exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ? "cli-not-found"
                    : exception.Message.Contains("within", StringComparison.OrdinalIgnoreCase) ? "timeout"
                    : "failed",
                appServerTimer);
            throw;
        }
    }

    private async Task<UsageSnapshot?> TryGetHttpUsageAsync(
        CodexHttpCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        if (credentials.AccountId is not null)
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException ||
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }

        using (response)
        {
            // The app-server would ask the same service, so a rate limit is reported, not retried.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new CodexUsageException("ChatGPT rate-limited the Codex usage request.")
                {
                    RetryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5),
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            try
            {
                using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
                return ParseHttpSnapshot(document.RootElement) with { Source = "chatgpt-http" };
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or IOException or HttpRequestException ||
                (exception is CodexUsageException { RetryAfter.Ticks: 0 }) ||
                (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The Codex login's token when the HTTP path may use it, else null. Only a ChatGPT login in a
    /// <c>CODEX_HOME</c> given as an absolute path (or the default <c>~\.codex</c>) qualifies, and
    /// never one pointed at a custom <c>chatgpt_base_url</c>: the token goes to chatgpt.com alone.
    /// </summary>
    internal static CodexHttpCredentials? LoadHttpCredentials()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim().Trim('"');
        if (string.IsNullOrEmpty(home))
        {
            var profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(profile))
            {
                profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            home = Path.Combine(profile, ".codex");
        }
        else if (!Path.IsPathFullyQualified(home))
        {
            return null;
        }

        return ReadHttpCredentials(Path.GetFullPath(home), DateTimeOffset.UtcNow);
    }

    internal static CodexHttpCredentials? ReadHttpCredentials(string codexHome, DateTimeOffset now)
    {
        try
        {
            if (HasCustomBackend(Path.Combine(codexHome, "config.toml")))
            {
                return null;
            }

            var path = Path.Combine(codexHome, "auth.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(
                SecureLocalFile.ReadAllText(path),
                new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            {
                // An API-key login has no ChatGPT tokens and no usage windows to read.
                return null;
            }

            var accessToken = CredentialInput.NormalizeToken(GetString(tokens, "access_token"));
            if (accessToken is null ||
                (JwtExpiry(accessToken) is { } expiresAt && expiresAt <= now + ExpirySkew))
            {
                return null;
            }

            var accountId = GetString(tokens, "account_id");
            accountId = accountId is { Length: > 0 and <= 128 } &&
                        accountId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                ? accountId
                : null;
            return new CodexHttpCredentials(accessToken, accountId);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <c>config.toml</c> points Codex at another backend. The app-server honours that
    /// setting; the HTTP path would not, so it steps aside rather than send the token elsewhere.
    /// </summary>
    private static bool HasCustomBackend(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return false;
        }

        return SecureLocalFile.ReadAllText(configPath)
            .Split('\n')
            .Select(line => line.Trim())
            .Any(line => line.StartsWith("chatgpt_base_url", StringComparison.Ordinal));
    }

    /// <summary>The <c>exp</c> claim of a JWT, or null when the token is not a readable JWT.</summary>
    internal static DateTimeOffset? JwtExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[1].Length > 8_192)
        {
            return null;
        }

        try
        {
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight(segment.Length + ((4 - (segment.Length % 4)) % 4), '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(segment));
            return payload.RootElement.ValueKind == JsonValueKind.Object &&
                   payload.RootElement.TryGetProperty("exp", out var exp) &&
                   exp.TryGetInt64(out var seconds) &&
                   seconds is > 0 and < 253_402_300_800
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static Task<(JsonElement RateLimits, JsonElement? Account)> GetRawUsageAsync(CancellationToken cancellationToken = default) =>
        GetRawUsageAsync(codexHome: null, cancellationToken);

    private static async Task<(JsonElement RateLimits, JsonElement? Account)> GetRawUsageAsync(
        string? codexHome,
        CancellationToken cancellationToken)
    {
        var codexLaunch = FindCodexLaunch();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var process = StartAppServer(codexLaunch, codexHome);
        var stderrTask = ProcessSecurity.DrainTextAsync(
            process.StandardError,
            MaxRetainedStandardErrorCharacters,
            timeout.Token);

        try
        {
            await WriteMessageAsync(process, new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "usage-ai", title = "UsageAI", version = AppIdentity.Version },
                    capabilities = new { experimentalApi = true },
                },
            }, timeout.Token);

            await ReadResponseAsync(process, 1, timeout.Token);

            await WriteMessageAsync(process, new { method = "initialized" }, timeout.Token);
            await WriteMessageAsync(process, new { id = 2, method = "account/rateLimits/read", @params = (object?)null }, timeout.Token);

            var rateLimits = await ReadResponseAsync(process, 2, timeout.Token);
            return (rateLimits, await TryReadAccountAsync(process, timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexUsageException("Codex did not return usage data within 20 seconds.");
        }
        catch (CodexUsageException exception)
        {
            var stderr = await TryReadErrorAsync(stderrTask);
            if (stderr.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("auth", StringComparison.OrdinalIgnoreCase))
            {
                throw new CodexUsageException(
                    "Codex is not signed in. Run `codex login` or sign in through the Codex extension, then refresh.",
                    exception);
            }

            throw;
        }
        catch (Exception exception)
        {
            await TryReadErrorAsync(stderrTask);
            throw new CodexUsageException(
                "Could not read Codex usage. Verify the Codex CLI installation, then refresh.",
                exception);
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    /// <summary>
    /// Asks which account is signed in, for the card's account line. It is optional: usage has
    /// already arrived, so an older CLI without <c>account/read</c>, an error, or a slow answer
    /// leaves the account unnamed instead of failing the refresh. <c>refreshToken: false</c>
    /// keeps the lookup from refreshing the login.
    /// </summary>
    private static async Task<JsonElement?> TryReadAccountAsync(Process process, CancellationToken cancellationToken)
    {
        using var accountTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        accountTimeout.CancelAfter(AccountReadTimeout);
        try
        {
            await WriteMessageAsync(
                process,
                new { id = 3, method = "account/read", @params = new { refreshToken = false } },
                accountTimeout.Token);
            return await ReadResponseAsync(process, 3, accountTimeout.Token);
        }
        catch (Exception exception) when (
            exception is CodexUsageException or IOException ||
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>app-server</c> command with a read-only sandbox and no approval prompts, so the server
    /// UsageAI starts can never be asked to run or approve anything. They are <c>-c</c> config
    /// overrides on the subcommand itself: <c>-s</c> and <c>-a</c> belong to the interactive CLI and
    /// are not passed on to <c>app-server</c>, so they would leave the user's own configured sandbox
    /// and approval policy in force.
    /// </summary>
    internal static readonly string[] AppServerArguments =
        ["app-server", "-c", "sandbox_mode=\"read-only\"", "-c", "approval_policy=\"never\"", "--listen", "stdio://"];

    private static Process StartAppServer(CodexLaunch launch, string? codexHome) =>
        Process.Start(CreateCliStartInfo(launch, codexHome, AppServerArguments))
            ?? throw new CodexUsageException("Windows could not start the Codex CLI.");

    /// <summary>
    /// How every Codex CLI child starts — the app-server and the sign-in alike: no window, redirected
    /// streams, the minimal environment, and a given <c>CODEX_HOME</c> set explicitly rather than
    /// inherited, so the child reads exactly that login.
    /// </summary>
    private static ProcessStartInfo CreateCliStartInfo(CodexLaunch launch, string? codexHome, IEnumerable<string> arguments)
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
            "CODEX_HOME",
            "NODE_EXTRA_CA_CERTS",
            "SSL_CERT_DIR",
            "SSL_CERT_FILE");
        if (codexHome is not null)
        {
            startInfo.Environment["CODEX_HOME"] = codexHome;
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

    private static async Task WriteMessageAsync(Process process, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        for (var messageCount = 0; messageCount < MaxProtocolMessagesPerResponse; messageCount++)
        {
            var line = await ProcessSecurity.ReadBoundedLineAsync(
                process.StandardOutput,
                MaxProtocolLineCharacters,
                cancellationToken);
            if (line is null)
            {
                throw new CodexUsageException("The Codex app-server stopped before it returned usage data.");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || !MatchesId(id, expectedId))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var errorMessage)
                        ? errorMessage.GetString()
                        : error.GetRawText();

                    throw new CodexUsageException(ToFriendlyError(message));
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    throw new CodexUsageException("Codex returned a response without usage data.");
                }

                return result.Clone();
            }
        }

        throw new CodexUsageException("The Codex app-server returned too many unrelated messages.");
    }

    private static bool MatchesId(JsonElement id, int expectedId) =>
        (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number == expectedId) ||
        (id.ValueKind == JsonValueKind.String &&
         id.GetString() == expectedId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static UsageSnapshot ParseSnapshot(JsonElement result, JsonElement? account = null)
    {
        var (mainKey, rateLimits) = SelectCodexBucket(result);
        var extraBuckets = ExtraBuckets(result, mainKey);

        // The main bucket always keeps the plain "5-hour" / "Weekly" names, so its history, alert memory
        // and pinned choices never change when a model-specific bucket such as Spark appears or goes.
        // Extra buckets read "<label> 5-hour": distinct, with no parenthesised group that would split
        // the popup into groups.
        var metrics = new List<UsageMetric>();
        AddWindows(metrics, rateLimits, null, AppServerFields);
        foreach (var (group, bucket) in extraBuckets)
        {
            AddWindows(metrics, bucket, group, AppServerFields);
        }

        AddBalances(
            metrics,
            rateLimits.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object
                ? GetString(credits, "balance")
                : null,
            GetCount(result, "rateLimitResetCredits", "availableCount"));

        // The fresh rate-limit response knows the current plan best; the account lookup only names
        // who is signed in.
        var accountDetails = account is { ValueKind: JsonValueKind.Object } accountRoot &&
                             accountRoot.TryGetProperty("account", out var accountElement) &&
                             accountElement.ValueKind == JsonValueKind.Object
            ? accountElement
            : (JsonElement?)null;
        var plan = GetString(rateLimits, "planType") ??
                   (accountDetails is { } details ? GetString(details, "planType") : null) ??
                   "Codex";
        var email = accountDetails is { } signedIn ? GetString(signedIn, "email") : null;
        return new UsageSnapshot(
            FormatPlan(plan),
            metrics,
            DateTimeOffset.Now,
            "codex",
            "Codex",
            AccountName: email is { Length: > 0 and <= 320 } ? email : null);
    }

    /// <summary>
    /// Reads a <c>wham/usage</c> answer, the same data the app-server relays in its own field names.
    /// Extra limits come from <c>additional_rate_limits</c>, named by <c>limit_name</c>.
    /// </summary>
    internal static UsageSnapshot ParseHttpSnapshot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("rate_limit", out var rateLimit) ||
            rateLimit.ValueKind != JsonValueKind.Object ||
            !HasWindow(rateLimit, HttpFields))
        {
            throw new CodexUsageException("Codex returned no rate-limit windows. Sign in with `codex login`, then refresh.");
        }

        var extras = new List<(string Group, JsonElement Bucket)>();
        if (root.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in additional.EnumerateArray())
            {
                if (extras.Count >= MaxExtraBuckets)
                {
                    break;
                }

                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("rate_limit", out var bucket) ||
                    bucket.ValueKind != JsonValueKind.Object ||
                    !HasWindow(bucket, HttpFields))
                {
                    continue;
                }

                var group = CleanLabel(GetString(entry, "limit_name") ?? GetString(entry, "metered_feature"), "Other limit");
                if (!extras.Any(existing => string.Equals(existing.Group, group, StringComparison.OrdinalIgnoreCase)))
                {
                    extras.Add((group, bucket));
                }
            }
        }

        var metrics = new List<UsageMetric>();
        AddWindows(metrics, rateLimit, null, HttpFields);
        foreach (var (group, bucket) in extras)
        {
            AddWindows(metrics, bucket, group, HttpFields);
        }

        AddBalances(
            metrics,
            root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object
                ? GetString(credits, "balance")
                : null,
            GetCount(root, "rate_limit_reset_credits", "available_count"));

        var email = GetString(root, "email");
        return new UsageSnapshot(
            FormatPlan(GetString(root, "plan_type") ?? "Codex"),
            metrics,
            DateTimeOffset.Now,
            "codex",
            "Codex",
            AccountName: email is { Length: > 0 and <= 320 } ? email : null);
    }

    private static int GetCount(JsonElement root, string objectName, string countName) =>
        root.TryGetProperty(objectName, out var summary) &&
        summary.ValueKind == JsonValueKind.Object &&
        summary.TryGetProperty(countName, out var count) &&
        count.TryGetInt32(out var value)
            ? value
            : 0;

    private static void AddBalances(List<UsageMetric> metrics, string? balance, int resetCredits)
    {
        if (balance is { Length: > 0 and <= 64 })
        {
            metrics.Add(new UsageMetric(
                "Credits",
                UsageMetricKind.Balance,
                null,
                RemainingText: balance,
                UsageText: "Available account balance"));
        }

        if (resetCredits > 0)
        {
            metrics.Add(new UsageMetric(
                "Reset credits",
                UsageMetricKind.Balance,
                null,
                RemainingText: resetCredits.ToString(System.Globalization.CultureInfo.CurrentCulture),
                UsageText: $"Full reset{(resetCredits == 1 ? string.Empty : "s")} available"));
        }
    }

    /// <summary>Where a window's figures live: the app-server's names, or <c>wham/usage</c>'s.</summary>
    private sealed record WindowFields(
        string Primary,
        string Secondary,
        string Used,
        string Reset,
        string Duration,
        int DurationUnitsPerMinute);

    private static readonly WindowFields AppServerFields =
        new("primary", "secondary", "usedPercent", "resetsAt", "windowDurationMins", 1);

    private static readonly WindowFields HttpFields =
        new("primary_window", "secondary_window", "used_percent", "reset_at", "limit_window_seconds", 60);

    private static bool HasWindow(JsonElement bucket, WindowFields fields) =>
        ParseWindow(bucket, fields.Primary, "Primary", fields) is not null ||
        ParseWindow(bucket, fields.Secondary, "Secondary", fields) is not null;

    private static void AddWindows(List<UsageMetric> metrics, JsonElement bucket, string? group, WindowFields fields)
    {
        var primary = ParseWindow(bucket, fields.Primary, "Primary", fields);
        var secondary = ParseWindow(bucket, fields.Secondary, "Secondary", fields);
        var (session, weekly) = ClassifyWindows(primary, secondary);
        foreach (var window in new[] { session, weekly })
        {
            if (window is not null)
            {
                metrics.Add(group is null ? window : window with { Name = $"{group} {window.Name}" });
            }
        }
    }

    /// <summary>
    /// The buckets besides the main one that report at least one window, each named by the label the
    /// server gives it (<c>limitName</c>), else its model slug or a readable form of its id.
    /// </summary>
    private static List<(string Group, JsonElement Bucket)> ExtraBuckets(JsonElement result, string? mainKey)
    {
        var extras = new List<(string Group, JsonElement Bucket)>();
        if (mainKey is null ||
            !result.TryGetProperty("rateLimitsByLimitId", out var buckets) ||
            buckets.ValueKind != JsonValueKind.Object)
        {
            return extras;
        }

        foreach (var bucket in buckets.EnumerateObject())
        {
            if (extras.Count >= MaxExtraBuckets)
            {
                break;
            }

            if (bucket.Name == mainKey ||
                bucket.Value.ValueKind != JsonValueKind.Object ||
                !HasWindow(bucket.Value, AppServerFields))
            {
                continue;
            }

            var group = BucketLabel(bucket.Value, bucket.Name);
            if (!extras.Any(existing => string.Equals(existing.Group, group, StringComparison.OrdinalIgnoreCase)))
            {
                extras.Add((group, bucket.Value));
            }
        }

        return extras;
    }

    internal static string BucketLabel(JsonElement bucket, string limitId) =>
        CleanLabel(
            GetString(bucket, "limitName") ?? GetString(bucket, "normalModelSlug"),
            System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                limitId.Replace('_', ' ').Replace('-', ' ').Trim()));

    /// <summary>
    /// A server-given label made safe for a metric name, where a parenthesis would read as the
    /// window and control characters have no place.
    /// </summary>
    private static string CleanLabel(string? label, string fallback)
    {
        static string Clean(string? value) => value is null
            ? string.Empty
            : new string(value.Where(character => !char.IsControl(character) && character is not '(' and not ')').ToArray()).Trim();

        var cleaned = Clean(label);
        if (cleaned.Length == 0)
        {
            cleaned = Clean(fallback);
        }

        return cleaned.Length switch
        {
            0 => "Other limit",
            > 60 => cleaned[..60].TrimEnd(),
            _ => cleaned,
        };
    }

    private static (UsageMetric? Session, UsageMetric? Weekly) ClassifyWindows(
        UsageMetric? primary,
        UsageMetric? secondary)
    {
        var windows = new[] { primary, secondary }.Where(window => window is not null).Cast<UsageMetric>().ToList();
        UsageMetric? session = null;
        UsageMetric? weekly = null;

        foreach (var window in windows)
        {
            if (window.DurationMinutes is >= 1_440)
            {
                weekly ??= window with
                {
                    Name = FormatWindowName(window.DurationMinutes.Value, "Weekly"),
                    Kind = UsageMetricKind.Rolling,
                };
            }
            else
            {
                session ??= window with
                {
                    Name = FormatWindowName(window.DurationMinutes, "Session"),
                    Kind = UsageMetricKind.Session,
                };
            }
        }

        if (session is null && weekly is null && primary is not null)
        {
            session = primary with { Name = "Session", Kind = UsageMetricKind.Session };
            weekly = secondary is null
                ? null
                : secondary with { Name = "Weekly", Kind = UsageMetricKind.Rolling };
        }

        return (session, weekly);
    }

    private static string FormatWindowName(long? durationMinutes, string fallback)
    {
        if (durationMinutes is null)
        {
            return fallback;
        }

        if (durationMinutes.Value % 10_080 == 0)
        {
            var weeks = durationMinutes.Value / 10_080;
            return weeks == 1 ? "Weekly" : $"{weeks}-week";
        }

        if (durationMinutes.Value % 60 == 0)
        {
            return $"{durationMinutes.Value / 60}-hour";
        }

        return fallback;
    }

    /// <summary>The main bucket and its key; the key is null for the older single-bucket shape.</summary>
    private static (string? Key, JsonElement Bucket) SelectCodexBucket(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Object)
        {
            if (buckets.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object)
            {
                return ("codex", codex);
            }

            foreach (var bucket in buckets.EnumerateObject())
            {
                if (bucket.Value.ValueKind == JsonValueKind.Object)
                {
                    return (bucket.Name, bucket.Value);
                }
            }
        }

        if (result.TryGetProperty("rateLimits", out var historical) &&
            historical.ValueKind == JsonValueKind.Object)
        {
            return (null, historical);
        }

        throw new CodexUsageException("Codex returned no rate-limit windows. Sign in with `codex login`, then refresh.");
    }

    private static UsageMetric? ParseWindow(JsonElement rateLimits, string propertyName, string name, WindowFields fields)
    {
        if (rateLimits.ValueKind != JsonValueKind.Object ||
            !rateLimits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // A percentage may arrive with a fraction; it is shown rounded.
        if (!window.TryGetProperty(fields.Used, out var usedElement) ||
            usedElement.ValueKind != JsonValueKind.Number ||
            !usedElement.TryGetDouble(out var used) ||
            !double.IsFinite(used))
        {
            return null;
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty(fields.Reset, out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.Number &&
            resetElement.TryGetInt64(out var resetSeconds) &&
            resetSeconds is > 0 and < 253_402_300_800)
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime();
        }

        long? duration = null;
        if (window.TryGetProperty(fields.Duration, out var durationElement) &&
            durationElement.ValueKind == JsonValueKind.Number &&
            durationElement.TryGetInt64(out var durationUnits) &&
            durationUnits > 0)
        {
            duration = durationUnits / fields.DurationUnitsPerMinute;
        }

        return new UsageMetric(
            name,
            UsageMetricKind.Session,
            (int)Math.Clamp(Math.Round(used, MidpointRounding.AwayFromZero), 0, 100),
            resetsAt,
            duration);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static string FormatPlan(string plan) => plan.Replace('_', ' ') switch
    {
        "plus" => "Plus",
        "pro" => "Pro",
        "prolite" => "Pro Lite",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Education",
        "free" => "Free",
        var other => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(other),
    };

    private static string ToFriendlyError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Codex could not read the account rate limits.";
        }

        return message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("auth", StringComparison.OrdinalIgnoreCase)
            ? "Codex is not signed in. Run `codex login` or sign in through the Codex extension, then refresh."
            : "Codex could not read the account rate limits.";
    }

    /// <summary>
    /// The official login, <c>codex login</c>, with <c>CODEX_HOME</c> pointed at <paramref name="codexHome"/>.
    /// The CLI writes its own <c>auth.json</c> there; UsageAI only starts it.
    /// </summary>
    internal static ProcessStartInfo CreateLoginStartInfo(string codexHome) =>
        CreateCliStartInfo(FindCodexLaunch(), codexHome, LoginArguments);

    private static readonly string[] LoginArguments = ["login"];

    private static CodexLaunch FindCodexLaunch()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_PATH");
        if (IsUsableCommand(configured))
        {
            return CreateLaunch(Path.GetFullPath(configured!));
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            throw new CodexUsageException("CODEX_PATH must be an absolute path to codex.exe or codex.cmd.");
        }

        var candidates = new List<string>();
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(cleanDirectory))
            {
                continue;
            }

            if (!Path.IsPathFullyQualified(cleanDirectory))
            {
                continue;
            }

            candidates.Add(Path.GetFullPath(Path.Combine(cleanDirectory, "codex.exe")));
            candidates.Add(Path.GetFullPath(Path.Combine(cleanDirectory, "codex.cmd")));
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "codex.cmd"));

        // Last, so a standalone install keeps priority: the binary the Codex editor extension ships
        // for itself, as the editor extension uses.
        var command = candidates.FirstOrDefault(File.Exists)
            ?? EditorExtensionBinaries.FindCodex(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                RuntimeInformation.OSArchitecture)
            ?? throw new CodexUsageException(
                "Codex CLI was not found. Install Codex or the Codex extension, or set CODEX_PATH to codex.cmd, then refresh.");

        return CreateLaunch(command);
    }

    private static CodexLaunch CreateLaunch(string command)
    {
        if (!command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexLaunch(command, null);
        }

        var npmDirectory = Path.GetDirectoryName(command)
            ?? throw new CodexUsageException("The Codex CLI path is invalid.");
        var script = Path.Combine(npmDirectory, "node_modules", "@openai", "codex", "bin", "codex.js");
        if (!File.Exists(script))
        {
            throw new CodexUsageException("The Codex npm launcher is incomplete. Reinstall the Codex CLI, then refresh.");
        }

        var localNode = Path.Combine(npmDirectory, "node.exe");
        var node = File.Exists(localNode)
            ? Path.GetFullPath(localNode)
            : ProcessSecurity.FindAbsoluteExecutableOnPath("node.exe");
        if (node is null)
        {
            throw new CodexUsageException("Node.js was not found, so the Codex npm launcher cannot start.");
        }

        return new CodexLaunch(node, script);
    }

    private static bool IsUsableCommand(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.Contains('"') &&
        Path.IsPathFullyQualified(path) &&
        (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) &&
        File.Exists(path);

    private sealed record CodexLaunch(string Executable, string? Script);

    private static async Task<string> TryReadErrorAsync(Task<string> stderrTask)
    {
        try
        {
            return await stderrTask.WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            process.StandardInput.Close();
            ProcessSecurity.TryKill(process);

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The process may already have exited; there is nothing left to clean up.
        }
    }
}

internal sealed class CodexUsageException : Exception, IThrottledUsageException
{
    /// <summary>How long ChatGPT asked to wait after a rate limit; zero for every other failure.</summary>
    public TimeSpan RetryAfter { get; init; }

    public CodexUsageException(string message)
        : base(message)
    {
    }

    public CodexUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
