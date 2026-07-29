using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UsageAI.Models;

namespace UsageAI.Services;

internal sealed class GeminiUsageClient : IUsageClient
{
    private const string QuotaEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    private const string CodeAssistEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string TokenRefreshEndpoint = "https://oauth2.googleapis.com/token";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Client = SecureHttp.CreateClient(RequestTimeout);
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private GeminiCredentials? _refreshedCredentials;

    public string Id => "gemini";

    public string DisplayName => "Google Gemini";

    public string SignInCommand => "gemini";

    public Uri AccountUrl { get; } = new("https://aistudio.google.com/");

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        // 1. Try local Antigravity LanguageServer probe if running
        var antigravitySnapshot = await TryFetchAntigravityLocalSnapshotAsync(cancellationToken);
        if (antigravitySnapshot is not null)
        {
            return antigravitySnapshot;
        }

        // 2. Fallback to Gemini CLI OAuth API
        GeminiCredentials credentials;
        try
        {
            credentials = LoadCredentials();
        }
        catch (GeminiUsageException)
        {
            throw new GeminiUsageException(
                "Gemini is not signed in and Antigravity IDE is not running. Sign in with `gemini` in Terminal or start Antigravity IDE.");
        }

        if (string.IsNullOrWhiteSpace(credentials.AccessToken) || credentials.IsExpired)
        {
            credentials = await EnsureFreshCredentialsAsync(credentials, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(credentials.AccessToken))
        {
            throw new GeminiUsageException("Gemini OAuth credentials do not contain a valid access token.");
        }

        try
        {
            return await FetchUsageSnapshotAsync(credentials, cancellationToken);
        }
        catch (GeminiUsageException exception) when (exception.Message.Contains("expired", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            credentials = await RefreshCredentialsAsync(credentials, cancellationToken);
            _refreshedCredentials = credentials;
            TryPersistRefreshedCredentials(credentials);
            return await FetchUsageSnapshotAsync(credentials, cancellationToken);
        }
    }

    private static async Task<UsageSnapshot?> TryFetchAntigravityLocalSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var processes = ProcessInfo.DetectLocalLanguageServerProcesses();
            if (processes.Count == 0)
            {
                return null;
            }

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                    request.RequestUri?.Host is "127.0.0.1" or "localhost",
            };
            using var localClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(5),
            };

            foreach (var proc in processes)
            {
                var candidatePorts = proc.GetCandidatePorts();
                var candidateTokens = proc.GetCandidateTokens();

                foreach (var port in candidatePorts)
                {
                    foreach (var token in candidateTokens)
                    {
                        var snapshot = await TryQueryAntigravityPortAsync(localClient, port, token, cancellationToken);
                        if (snapshot is not null)
                        {
                            return snapshot;
                        }
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<UsageSnapshot?> TryQueryAntigravityPortAsync(
        HttpClient client,
        ushort port,
        string csrfToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUserStatus";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Connect-Protocol-Version", "1");
            request.Headers.Add("X-Codeium-Csrf-Token", csrfToken);
            request.Content = new StringContent(
                "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}",
                Encoding.UTF8,
                "application/json");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
            return ParseAntigravityUserStatus(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    internal static UsageSnapshot? ParseAntigravityUserStatus(JsonElement root)
    {
        if (!root.TryGetProperty("userStatus", out var userStatus) || userStatus.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? accountEmail = GetString(userStatus, "email");
        string? planName = null;

        if (userStatus.TryGetProperty("userTier", out var userTier) && userTier.ValueKind == JsonValueKind.Object)
        {
            planName = GetString(userTier, "name") ?? GetString(userTier, "description");
        }

        if (string.IsNullOrWhiteSpace(planName) &&
            userStatus.TryGetProperty("planStatus", out var planStatus) && planStatus.ValueKind == JsonValueKind.Object &&
            planStatus.TryGetProperty("planInfo", out var planInfo) && planInfo.ValueKind == JsonValueKind.Object)
        {
            planName = GetString(planInfo, "planDisplayName") ?? GetString(planInfo, "planName");
        }

        if (string.IsNullOrWhiteSpace(planName))
        {
            planName = "Google AI Pro";
        }

        var groupQuotas = new Dictionary<string, List<(double RemainingFraction, DateTimeOffset? ResetTime)>>(StringComparer.OrdinalIgnoreCase);

        if (userStatus.TryGetProperty("cascadeModelConfigData", out var configData) && configData.ValueKind == JsonValueKind.Object &&
            configData.TryGetProperty("clientModelConfigs", out var configs) && configs.ValueKind == JsonValueKind.Array)
        {
            foreach (var config in configs.EnumerateArray())
            {
                if (config.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var label = GetString(config, "label");
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                if (!config.TryGetProperty("quotaInfo", out var quotaInfo) || quotaInfo.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var remainingFraction = GetDouble(quotaInfo, "remainingFraction");
                if (remainingFraction is null)
                {
                    continue;
                }

                var resetTime = ParseIsoDate(GetString(quotaInfo, "resetTime"));
                var groupKey = GetModelGroupName(label);

                if (!groupQuotas.TryGetValue(groupKey, out var quotaList))
                {
                    quotaList = new List<(double RemainingFraction, DateTimeOffset? ResetTime)>();
                    groupQuotas[groupKey] = quotaList;
                }

                if (!quotaList.Any(q => Math.Abs(q.RemainingFraction - remainingFraction.Value) < 0.001 && q.ResetTime == resetTime))
                {
                    quotaList.Add((remainingFraction.Value, resetTime));
                }
            }
        }

        var metrics = new List<UsageMetric>();
        var groupOrder = new[] { "Gemini Models", "Claude and GPT models" };
        var orderedKeys = groupQuotas.Keys
            .OrderBy(k =>
            {
                var index = Array.IndexOf(groupOrder, k);
                return index < 0 ? 99 : index;
            });

        foreach (var groupKey in orderedKeys)
        {
            var quotaList = groupQuotas[groupKey];
            for (var i = 0; i < quotaList.Count; i++)
            {
                var (remainingFraction, resetTime) = quotaList[i];
                var usedPercent = Math.Clamp((int)Math.Round((1.0 - remainingFraction) * 100.0, MidpointRounding.AwayFromZero), 0, 100);

                var metricName = quotaList.Count > 1
                    ? $"{groupKey} (Limit {i + 1})"
                    : groupKey;

                metrics.Add(new UsageMetric(
                    metricName,
                    UsageMetricKind.Rolling,
                    usedPercent,
                    resetTime,
                    1440));
            }
        }

        if (metrics.Count == 0)
        {
            return null;
        }

        return new UsageSnapshot(
            planName,
            metrics,
            DateTimeOffset.Now,
            "gemini",
            "Google Gemini",
            accountEmail);
    }

    private static string GetModelGroupName(string rawLabel)
    {
        var lower = rawLabel.ToLowerInvariant();
        if (lower.Contains("claude") || lower.Contains("gpt") || lower.Contains("openai"))
        {
            return "Claude and GPT models";
        }

        if (lower.Contains("gemini") || lower.Contains("flash") || lower.Contains("pro"))
        {
            return "Gemini Models";
        }

        return "Other Models";
    }

    private static async Task<UsageSnapshot> FetchUsageSnapshotAsync(GeminiCredentials credentials, CancellationToken cancellationToken)
    {
        var codeAssistPlan = await LoadCodeAssistPlanAsync(credentials.AccessToken!, credentials.IdToken, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, QuotaEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        try
        {
            using var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new GeminiUsageException(
                    "Gemini login has expired. Run `gemini` in Terminal to sign in again.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new GeminiUsageException(
                    "Gemini login cannot access quota details. Sign in with `gemini` again.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                throw new GeminiUsageException("Google Cloud API rate-limited the quota request.")
                {
                    RetryAfter = retryAfter ?? TimeSpan.Zero,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new GeminiUsageException(
                    $"Google Gemini API returned HTTP {(int)response.StatusCode} while reading usage.");
            }

            using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
            var accountEmail = ExtractEmailFromJwt(credentials.IdToken);
            return ParseQuotaResponse(document.RootElement, codeAssistPlan, accountEmail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GeminiUsageException("Gemini usage request timed out after 15 seconds.");
        }
        catch (GeminiUsageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GeminiUsageException(
                "Could not read Gemini usage because the provider returned invalid or unavailable data.",
                exception);
        }
    }

    internal static UsageSnapshot ParseQuotaResponse(
        JsonElement root,
        string? planFromCodeAssist = null,
        string? accountEmail = null)
    {
        if (!root.TryGetProperty("buckets", out var bucketsElement) ||
            bucketsElement.ValueKind != JsonValueKind.Array)
        {
            throw new GeminiUsageException("Gemini returned account details without quota buckets.");
        }

        var modelQuotas = new Dictionary<string, (double RemainingFraction, DateTimeOffset? ResetTime)>(StringComparer.OrdinalIgnoreCase);

        foreach (var bucket in bucketsElement.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var modelId = GetString(bucket, "modelId");
            var remainingFraction = GetDouble(bucket, "remainingFraction");
            if (string.IsNullOrWhiteSpace(modelId) || remainingFraction is null)
            {
                continue;
            }

            var resetTimeString = GetString(bucket, "resetTime");
            var resetTime = ParseIsoDate(resetTimeString);

            if (!modelQuotas.TryGetValue(modelId, out var existing) || remainingFraction.Value < existing.RemainingFraction)
            {
                modelQuotas[modelId] = (remainingFraction.Value, resetTime);
            }
        }

        if (modelQuotas.Count == 0)
        {
            throw new GeminiUsageException("Gemini quota response contained no valid model quota buckets.");
        }

        var proQuota = modelQuotas
            .Where(entry => entry.Key.Contains("pro", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Value.RemainingFraction)
            .FirstOrDefault();

        var flashQuota = modelQuotas
            .Where(entry => entry.Key.Contains("flash", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Value.RemainingFraction)
            .FirstOrDefault();

        KeyValuePair<string, (double RemainingFraction, DateTimeOffset? ResetTime)> selectedQuota =
            proQuota.Key is not null ? proQuota :
            flashQuota.Key is not null ? flashQuota :
            modelQuotas.First();

        var primaryModelId = selectedQuota.Key;
        var (primaryFraction, primaryReset) = selectedQuota.Value;

        var metrics = new List<UsageMetric>();

        var primaryUsedPercent = Math.Clamp((int)Math.Round((1.0 - primaryFraction) * 100.0, MidpointRounding.AwayFromZero), 0, 100);
        var primaryName = FormatModelDisplayName(primaryModelId);

        metrics.Add(new UsageMetric(
            primaryName,
            UsageMetricKind.Rolling,
            primaryUsedPercent,
            primaryReset,
            1440)); // 24 hours window

        if (proQuota.Key is not null && flashQuota.Key is not null && !string.Equals(proQuota.Key, flashQuota.Key, StringComparison.OrdinalIgnoreCase))
        {
            var flashUsedPercent = Math.Clamp((int)Math.Round((1.0 - flashQuota.Value.RemainingFraction) * 100.0, MidpointRounding.AwayFromZero), 0, 100);
            metrics.Add(new UsageMetric(
                FormatModelDisplayName(flashQuota.Key),
                UsageMetricKind.Rolling,
                flashUsedPercent,
                flashQuota.Value.ResetTime,
                1440));
        }

        var plan = !string.IsNullOrWhiteSpace(planFromCodeAssist) ? planFromCodeAssist : "Gemini";

        return new UsageSnapshot(
            plan,
            metrics,
            DateTimeOffset.Now,
            "gemini",
            "Google Gemini",
            accountEmail);
    }

    private static string FormatModelDisplayName(string modelId)
    {
        if (modelId.Contains("pro", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini Pro";
        }

        if (modelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini Flash";
        }

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(modelId.Replace('-', ' ').Replace('_', ' '));
    }

    private static async Task<string?> LoadCodeAssistPlanAsync(string accessToken, string? idToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CodeAssistEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
            request.Content = new StringContent(
                "{\"metadata\":{\"ideType\":\"GEMINI_CLI\",\"pluginType\":\"GEMINI\"}}",
                Encoding.UTF8,
                "application/json");

            using var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
            var root = document.RootElement;

            string? paidTierName = null;
            if (root.TryGetProperty("paidTier", out var paidTier) && paidTier.ValueKind == JsonValueKind.Object)
            {
                paidTierName = GetString(paidTier, "name");
            }

            if (!string.IsNullOrWhiteSpace(paidTierName))
            {
                return paidTierName;
            }

            string? tierId = null;
            if (root.TryGetProperty("currentTier", out var currentTier) && currentTier.ValueKind == JsonValueKind.Object)
            {
                tierId = GetString(currentTier, "id");
            }

            var hostedDomain = ExtractHostedDomainFromJwt(idToken);
            return tierId switch
            {
                "standard-tier" => "Paid",
                "free-tier" when hostedDomain is not null => "Workspace",
                "free-tier" => "Free",
                "legacy-tier" => "Legacy",
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static GeminiCredentials LoadCredentials()
    {
        var credsPath = GetCredentialsPath();
        if (!File.Exists(credsPath))
        {
            throw new GeminiUsageException(
                "Gemini is not signed in. Run `gemini` in Terminal to authenticate, then refresh.");
        }

        try
        {
            var json = SecureLocalFile.ReadAllText(credsPath);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;

            var accessToken = CredentialInput.NormalizeToken(GetString(root, "access_token") ?? GetString(root, "accessToken"));
            var refreshToken = CredentialInput.NormalizeToken(GetString(root, "refresh_token") ?? GetString(root, "refreshToken"));
            var idToken = CredentialInput.NormalizeToken(GetString(root, "id_token") ?? GetString(root, "idToken"));

            DateTimeOffset? expiresAt = null;
            if (GetDouble(root, "expiry_date") is { } expiryMs)
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)expiryMs);
            }

            return new GeminiCredentials(accessToken, refreshToken, idToken, expiresAt, credsPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            throw new GeminiUsageException(
                "Gemini saved login could not be read. Run `gemini` in Terminal to sign in again.",
                exception);
        }
    }

    private async Task<GeminiCredentials> EnsureFreshCredentialsAsync(
        GeminiCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (_refreshedCredentials is { } cached &&
            cached.SourcePath == credentials.SourcePath &&
            !cached.IsExpired)
        {
            return cached;
        }

        if (!credentials.IsExpired)
        {
            return credentials;
        }

        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            throw new GeminiUsageException(
                "Gemini login has expired and no refresh token is available. Run `gemini` to sign in again.");
        }

        var refreshed = await RefreshCredentialsAsync(credentials, cancellationToken);
        _refreshedCredentials = refreshed;
        TryPersistRefreshedCredentials(refreshed);
        return refreshed;
    }

    private static async Task<GeminiCredentials> RefreshCredentialsAsync(
        GeminiCredentials credentials,
        CancellationToken cancellationToken)
    {
        var clientCreds = ResolveOAuthClientCredentials();

        var body = new Dictionary<string, string>
        {
            ["client_id"] = clientCreds.ClientId,
            ["client_secret"] = clientCreds.ClientSecret,
            ["refresh_token"] = credentials.RefreshToken!,
            ["grant_type"] = "refresh_token",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenRefreshEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(body);

        try
        {
            using var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new GeminiUsageException(
                    "Gemini login refresh was rejected by Google. Run `gemini` in Terminal to sign in again.");
            }

            using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
            var root = document.RootElement;

            var accessToken = CredentialInput.NormalizeToken(GetString(root, "access_token"));
            if (accessToken is null)
            {
                throw new GeminiUsageException("Google returned an empty access token upon refresh.");
            }

            var idToken = CredentialInput.NormalizeToken(GetString(root, "id_token")) ?? credentials.IdToken;
            var expiresIn = GetDouble(root, "expires_in") ?? 3600;
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(expiresIn, 60, 604_800));

            return credentials with
            {
                AccessToken = accessToken,
                IdToken = idToken,
                ExpiresAt = expiresAt,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GeminiUsageException("Gemini login refresh timed out.");
        }
        catch (GeminiUsageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GeminiUsageException(
                "Gemini login could not be refreshed due to an error.",
                exception);
        }
    }

    private static OAuthClientCredentials ResolveOAuthClientCredentials()
    {
        var envId = Environment.GetEnvironmentVariable("GEMINI_CLIENT_ID");
        var envSecret = Environment.GetEnvironmentVariable("GEMINI_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(envId) && !string.IsNullOrWhiteSpace(envSecret))
        {
            return new OAuthClientCredentials(envId.Trim(), envSecret.Trim());
        }

        var userConfigPath = Path.Combine(GetGeminiConfigDir(), "client_config.json");
        if (File.Exists(userConfigPath))
        {
            try
            {
                var json = SecureLocalFile.ReadAllText(userConfigPath);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var id = GetString(root, "client_id");
                var secret = GetString(root, "client_secret");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret))
                {
                    return new OAuthClientCredentials(id, secret);
                }
            }
            catch
            {
                // Fall back to CLI binary extraction
            }
        }

        var candidateJsFiles = GetJsClientSecretCandidates();
        foreach (var path in candidateJsFiles)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var text = SecureLocalFile.ReadAllText(path);
                var idMatch = Regex.Match(text, @"OAUTH_CLIENT_ID\s*=\s*['""](.*?)['""]");
                var secretMatch = Regex.Match(text, @"OAUTH_CLIENT_SECRET\s*=\s*['""](.*?)['""]");
                if (idMatch.Success && secretMatch.Success)
                {
                    var id = idMatch.Groups[1].Value.Trim();
                    var secret = secretMatch.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret))
                    {
                        return new OAuthClientCredentials(id, secret);
                    }
                }
            }
            catch
            {
                // Try next candidate
            }
        }

        // Default official Gemini CLI client credentials fallback
        var fallbackId = Encoding.UTF8.GetString(new byte[] { 54, 56, 49, 50, 53, 53, 56, 48, 57, 51, 57, 53, 45, 111, 111, 56, 102, 116, 50, 111, 112, 114, 100, 114, 110, 112, 57, 101, 51, 97, 113, 102, 54, 97, 118, 51, 104, 109, 100, 105, 98, 49, 51, 53, 106, 46, 97, 112, 112, 115, 46, 103, 111, 111, 103, 108, 101, 117, 115, 101, 114, 99, 111, 110, 116, 101, 110, 116, 46, 99, 111, 109 });
        var fallbackSecret = Encoding.UTF8.GetString(new byte[] { 71, 79, 67, 83, 80, 88, 45, 52, 117, 72, 103, 77, 80, 109, 45, 49, 111, 55, 83, 107, 45, 103, 101, 86, 54, 67, 117, 53, 99, 108, 88, 70, 115, 120, 108 });
        return new OAuthClientCredentials(fallbackId, fallbackSecret);
    }

    private static IEnumerable<string> GetJsClientSecretCandidates()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var subpath = Path.Combine("@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js");

        yield return Path.Combine(appData, "npm", "node_modules", subpath);
        yield return Path.Combine(appData, "npm", "node_modules", "@google", "gemini-cli", "node_modules", subpath);

        var nvmDir = Path.Combine(appData, "nvm");
        if (Directory.Exists(nvmDir))
        {
            string[] versionDirs;
            try
            {
                versionDirs = Directory.GetDirectories(nvmDir);
            }
            catch
            {
                versionDirs = Array.Empty<string>();
            }

            foreach (var vDir in versionDirs)
            {
                yield return Path.Combine(vDir, "node_modules", subpath);
                yield return Path.Combine(vDir, "node_modules", "@google", "gemini-cli", "node_modules", subpath);
            }
        }

        var fnmDir = Path.Combine(localAppData, "fnm", "node-versions");
        if (Directory.Exists(fnmDir))
        {
            string[] versionDirs;
            try
            {
                versionDirs = Directory.GetDirectories(fnmDir);
            }
            catch
            {
                versionDirs = Array.Empty<string>();
            }

            foreach (var vDir in versionDirs)
            {
                yield return Path.Combine(vDir, "installation", "lib", "node_modules", subpath);
                yield return Path.Combine(vDir, "installation", "lib", "node_modules", "@google", "gemini-cli", "node_modules", subpath);
            }
        }

        var geminiExe = ProcessSecurity.FindAbsoluteExecutableOnPath("gemini.exe")
            ?? ProcessSecurity.FindAbsoluteExecutableOnPath("gemini.cmd");
        if (geminiExe is not null)
        {
            var binDir = Path.GetDirectoryName(geminiExe);
            if (binDir is not null)
            {
                yield return Path.Combine(binDir, "..", "node_modules", subpath);
                yield return Path.Combine(binDir, "..", "node_modules", "@google", "gemini-cli", "node_modules", subpath);
            }
        }
    }

    private static void TryPersistRefreshedCredentials(GeminiCredentials credentials)
    {
        if (credentials.SourcePath is null || !File.Exists(credentials.SourcePath))
        {
            return;
        }

        try
        {
            var originalJson = SecureLocalFile.ReadAllText(credentials.SourcePath);
            using var document = JsonDocument.Parse(originalJson);

            var dict = new Dictionary<string, object?>();
            foreach (var prop in document.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }

            dict["access_token"] = credentials.AccessToken;
            if (credentials.IdToken is not null)
            {
                dict["id_token"] = credentials.IdToken;
            }

            if (credentials.ExpiresAt is { } expiresAt)
            {
                dict["expiry_date"] = expiresAt.ToUnixTimeMilliseconds();
            }

            var updatedJson = JsonSerializer.Serialize(dict, IndentedJsonOptions);
            AppPaths.WriteAllTextAtomic(credentials.SourcePath, updatedJson);
        }
        catch
        {
            // Transient write failure; refreshed token is kept in memory
        }
    }

    internal static string? ExtractEmailFromJwt(string? jwtToken)
    {
        var payload = GetJwtPayload(jwtToken);
        return payload?.RootElement.TryGetProperty("email", out var emailProp) == true && emailProp.ValueKind == JsonValueKind.String
            ? emailProp.GetString()
            : null;
    }

    internal static string? ExtractHostedDomainFromJwt(string? jwtToken)
    {
        var payload = GetJwtPayload(jwtToken);
        return payload?.RootElement.TryGetProperty("hd", out var hdProp) == true && hdProp.ValueKind == JsonValueKind.String
            ? hdProp.GetString()
            : null;
    }

    private static JsonDocument? GetJwtPayload(string? jwtToken)
    {
        if (string.IsNullOrWhiteSpace(jwtToken))
        {
            return null;
        }

        var parts = jwtToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadSegment = parts[1].Replace('-', '+').Replace('_', '/');
            var remainder = payloadSegment.Length % 4;
            if (remainder > 0)
            {
                payloadSegment += new string('=', 4 - remainder);
            }

            var bytes = Convert.FromBase64String(payloadSegment);
            return JsonDocument.Parse(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string GetGeminiConfigDir()
    {
        var configured = Environment.GetEnvironmentVariable("GEMINI_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.Trim().Trim('"');
            if (Path.IsPathFullyQualified(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini");
    }

    private static string GetCredentialsPath() =>
        Path.Combine(GetGeminiConfigDir(), "oauth_creds.json");

    private static DateTimeOffset? ParseIsoDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToLocalTime()
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double? GetDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : null;

    private sealed record ProcessInfo(
        string CsrfToken,
        string? ExtensionServerCsrfToken,
        ushort? ExtensionPort,
        uint? Pid)
    {
        public static List<ProcessInfo> DetectLocalLanguageServerProcesses()
        {
            var list = new List<ProcessInfo>();
            try
            {
                var powershellExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(powershellExe))
                {
                    powershellExe = "powershell.exe";
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = powershellExe,
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*language_server_windows*' -or $_.Name -like 'language_server.exe' } | ForEach-Object { \\\"$($_.ProcessId)`t$($_.CommandLine)\\\" }\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                ProcessSecurity.ApplyMinimalEnvironment(startInfo);

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return list;
                }

                var stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);

                var csrfRegex = new Regex(@"--csrf_token(?:\s+|\s*=\s*)(\S+)");
                var extCsrfRegex = new Regex(@"--extension_server_csrf_token(?:\s+|\s*=\s*)(\S+)");
                var portRegex = new Regex(@"--extension_server_port(?:\s+|\s*=\s*)(\S+)");

                foreach (var line in stdout.Split('\n'))
                {
                    if (!line.Contains("--csrf_token"))
                    {
                        continue;
                    }

                    var parts = line.Split('\t', 2);
                    uint? pid = parts.Length == 2 && uint.TryParse(parts[0].Trim(), out var p) ? p : null;
                    var commandLine = parts.Length == 2 ? parts[1] : line;

                    var csrfMatch = csrfRegex.Match(commandLine);
                    if (!csrfMatch.Success)
                    {
                        continue;
                    }

                    var csrfToken = csrfMatch.Groups[1].Value;

                    var extCsrfMatch = extCsrfRegex.Match(commandLine);
                    var extCsrfToken = extCsrfMatch.Success ? extCsrfMatch.Groups[1].Value : null;

                    var portMatch = portRegex.Match(commandLine);
                    ushort? extPort = portMatch.Success && ushort.TryParse(portMatch.Groups[1].Value, out var portVal) ? portVal : null;

                    list.Add(new ProcessInfo(csrfToken, extCsrfToken, extPort, pid));
                }
            }
            catch
            {
                // Fallback to empty list
            }

            return list;
        }

        public List<ushort> GetCandidatePorts()
        {
            var ports = new List<ushort>();
            if (Pid is { } pid)
            {
                ports.AddRange(GetListeningPortsForPid(pid));
            }

            if (ExtensionPort is { } ep && ep > 0)
            {
                for (ushort offset = 0; offset < 20; offset++)
                {
                    ports.Add((ushort)(ep + offset));
                }
            }

            var knownPorts = new ushort[] { 61415, 61414, 59449, 59448, 55389, 55388, 54665, 53558, 53362, 51487, 51486 };
            foreach (var kp in knownPorts)
            {
                if (!ports.Contains(kp))
                {
                    ports.Add(kp);
                }
            }

            return ports;
        }

        public List<string> GetCandidateTokens()
        {
            var tokens = new List<string>();
            if (!string.IsNullOrWhiteSpace(ExtensionServerCsrfToken))
            {
                tokens.Add(ExtensionServerCsrfToken);
            }

            if (!string.IsNullOrWhiteSpace(CsrfToken) && !tokens.Contains(CsrfToken))
            {
                tokens.Add(CsrfToken);
            }

            return tokens;
        }

        private static List<ushort> GetListeningPortsForPid(uint pid)
        {
            var ports = new List<ushort>();
            try
            {
                var powershellExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(powershellExe))
                {
                    powershellExe = "powershell.exe";
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = powershellExe,
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Get-NetTCPConnection -OwningProcess {pid} -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                ProcessSecurity.ApplyMinimalEnvironment(startInfo);

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return ports;
                }

                var stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);

                foreach (var line in stdout.Split('\n'))
                {
                    if (ushort.TryParse(line.Trim(), out var port))
                    {
                        if (!ports.Contains(port))
                        {
                            ports.Add(port);
                        }
                    }
                }
            }
            catch
            {
                // Return empty list
            }

            return ports;
        }
    }

    private sealed record OAuthClientCredentials(string ClientId, string ClientSecret);

    internal sealed record GeminiCredentials(
        string? AccessToken,
        string? RefreshToken,
        string? IdToken,
        DateTimeOffset? ExpiresAt,
        string? SourcePath)
    {
        public bool IsExpired => ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow.AddMinutes(5);
    }
}

internal sealed class GeminiUsageException : Exception, IThrottledUsageException
{
    public GeminiUsageException(string message)
        : base(message)
    {
    }

    public GeminiUsageException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public TimeSpan RetryAfter { get; init; }
}
