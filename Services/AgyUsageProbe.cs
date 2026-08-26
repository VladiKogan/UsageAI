using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

/// <summary>
/// Reads quota through Google's official Antigravity CLI without owning its credentials.
/// A single long-lived <c>--hub</c> server answers every refresh over loopback, so a running app
/// spawns no child process per poll; short-lived <c>agy -p /usage</c> reads remain the fallback for
/// CLI builds without a hub. Output is bounded and only processes created here are cleaned up.
/// </summary>
internal static class AgyUsageProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HubReadyTimeout = TimeSpan.FromSeconds(20);
    private const int MaxOutputCharacters = 262_144;
    private const int MaxErrorCharacters = 16_384;

    private static readonly SemaphoreSlim HubGate = new(1, 1);
    private static Process? _hubProcess;
    private static HttpClient? _hubClient;
    private static ushort _hubPort;
    private static string _hubToken = string.Empty;

    public static async Task<UsageSnapshot?> TryFetchAsync(CancellationToken cancellationToken)
    {
        var snapshot = await TryFetchFromHubAsync(cancellationToken);
        if (snapshot is not null)
        {
            return snapshot;
        }

        return await TryFetchFromPrintAsync(cancellationToken);
    }

    /// <summary>Stops the shared hub. Called when the application shuts down.</summary>
    public static void DisposeHub()
    {
        HubGate.Wait();
        try
        {
            ProcessSecurity.TryKill(_hubProcess);
            _hubProcess?.Dispose();
            _hubProcess = null;
            _hubClient?.Dispose();
            _hubClient = null;
            _hubPort = 0;
            _hubToken = string.Empty;
        }
        finally
        {
            HubGate.Release();
        }
    }

    private static async Task<UsageSnapshot?> TryFetchFromHubAsync(CancellationToken cancellationToken)
    {
        var client = await EnsureHubAsync(cancellationToken);
        if (client is null)
        {
            return null;
        }

        // This build of the service rejects unknown request fields, and both calls take no arguments.
        using var summary = await TryPostAsync(client, "RetrieveUserQuotaSummary", "{}", cancellationToken);
        if (summary is null)
        {
            return null;
        }

        var metrics = GeminiUsageClient.ParseQuotaSummaryResponse(summary.RootElement);
        if (metrics.Count == 0)
        {
            return null;
        }

        using var status = await TryPostAsync(client, "GetUserStatus", "{}", cancellationToken);
        var snapshot = status is null
            ? null
            : GeminiUsageClient.ParseAntigravityUserStatus(status.RootElement);
        return snapshot is null
            ? CreateSnapshot(metrics, null, null)
            : snapshot with
            {
                Metrics = GeminiUsageClient.MergeAntigravityQuotaSummaryMetrics(snapshot.Metrics, metrics),
            };
    }

    private static async Task<HttpClient?> EnsureHubAsync(CancellationToken cancellationToken)
    {
        await HubGate.WaitAsync(cancellationToken);
        try
        {
            if (_hubClient is not null && _hubProcess is { HasExited: false })
            {
                return _hubClient;
            }

            ProcessSecurity.TryKill(_hubProcess);
            _hubProcess?.Dispose();
            _hubProcess = null;
            _hubClient?.Dispose();
            _hubClient = null;

            var executable = FindExecutable();
            if (executable is null || !TryReserveLoopbackPort(out var port))
            {
                return null;
            }

            // The hub authenticates callers with a token read from its own environment, so minting one
            // here keeps the socket usable by this process alone.
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var process = Process.Start(CreateHubStartInfo(executable, port, token));
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            _ = ProcessSecurity.DrainTextAsync(process.StandardOutput, MaxErrorCharacters, CancellationToken.None);
            _ = ProcessSecurity.DrainTextAsync(process.StandardError, MaxErrorCharacters, CancellationToken.None);

            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _hubPort = port;
            _hubToken = token;
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ready.CancelAfter(HubReadyTimeout);
            try
            {
                while (!ready.IsCancellationRequested && !process.HasExited)
                {
                    using var probe = await TryPostAsync(client, "RetrieveUserQuotaSummary", "{}", ready.Token);
                    if (probe is not null)
                    {
                        _hubProcess = process;
                        _hubClient = client;
                        return client;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(400), ready.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall through to cleanup: the hub never became ready in time.
            }

            ProcessSecurity.TryKill(process);
            process.Dispose();
            client.Dispose();
            _hubPort = 0;
            _hubToken = string.Empty;
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            HubGate.Release();
        }
    }

    internal static ProcessStartInfo CreateHubStartInfo(string executable, ushort port, string token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        ProcessSecurity.ApplyMinimalEnvironment(
            startInfo,
            "ANTIGRAVITY_CLI_PATH",
            "GOOGLE_CLOUD_PROJECT",
            "NODE_EXTRA_CA_CERTS",
            "SSL_CERT_DIR",
            "SSL_CERT_FILE");
        startInfo.Environment["CI"] = "1";
        startInfo.Environment["ANTIGRAVITY_CSRF_TOKEN"] = token;
        startInfo.ArgumentList.Add("--hub");
        // `--app_data_dir` is deliberately omitted: the CLI's default data directory holds the sign-in.
        startInfo.ArgumentList.Add(
            string.Create(CultureInfo.InvariantCulture, $"--hub-port={port}"));
        return startInfo;
    }

    private static bool TryReserveLoopbackPort(out ushort port)
    {
        port = 0;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port != 0;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<UsageSnapshot?> TryFetchFromPrintAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable();
        if (executable is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        Process? process = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        var ownedProcessIds = new HashSet<int>();
        var startedAtUtc = DateTime.UtcNow;

        try
        {
            process = Start(executable);
            ownedProcessIds.Add(process.Id);
            process.StandardInput.Close();
            stdoutTask = ProcessSecurity.DrainTextAsync(
                process.StandardOutput,
                MaxOutputCharacters,
                CancellationToken.None);
            stderrTask = ProcessSecurity.DrainTextAsync(
                process.StandardError,
                MaxErrorCharacters,
                CancellationToken.None);

            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The official CLI is a best-effort source; Gemini CLI compatibility remains available.
        }
        finally
        {
            ProcessSecurity.TryKill(process);
            TryKillOwnedProcesses(ownedProcessIds, startedAtUtc);
        }

        var output = await TryReadCompletedOutputAsync(stdoutTask);
        await TryReadCompletedOutputAsync(stderrTask);
        return ParseOutput(output);
    }

    internal static UsageSnapshot? ParseOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            var start = output.IndexOf('{');
            var end = output.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            try
            {
                document = JsonDocument.Parse(
                    output.Substring(start, end - start + 1),
                    new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (JsonException)
            {
                return null;
            }
        }

        using (document)
        {
            if (!TryFindQuotaContainer(document.RootElement, 0, out var quotaContainer))
            {
                return null;
            }

            var metrics = GeminiUsageClient.ParseQuotaSummaryResponse(quotaContainer);
            if (metrics.Count == 0)
            {
                return null;
            }

            return CreateSnapshot(metrics, FindString(document.RootElement, "plan", "planName", "tier"),
                FindString(document.RootElement, "email", "accountEmail"));
        }
    }

    private static Process Start(string executable)
    {
        return Process.Start(CreateStartInfo(executable))
            ?? throw new InvalidOperationException("Windows could not start the Antigravity CLI.");
    }

    internal static ProcessStartInfo CreateStartInfo(string executable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        ProcessSecurity.ApplyMinimalEnvironment(
            startInfo,
            "ANTIGRAVITY_CLI_PATH",
            "GOOGLE_CLOUD_PROJECT",
            "NODE_EXTRA_CA_CERTS",
            "SSL_CERT_DIR",
            "SSL_CERT_FILE");
        startInfo.Environment["CI"] = "1";
        startInfo.Environment["AGY_CLI_HIDE_ACCOUNT_INFO"] = "1";
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("/usage");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--print-timeout=12s");

        return startInfo;
    }

    private static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLI_PATH")?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(configured) &&
            Path.IsPathFullyQualified(configured) &&
            File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wellKnown = GetWellKnownExecutablePaths(userProfile, localAppData);
        if (wellKnown[0] is { } extensionBackend &&
            Path.IsPathFullyQualified(extensionBackend) &&
            File.Exists(extensionBackend))
        {
            return extensionBackend;
        }

        var fromPath = ProcessSecurity.FindAbsoluteExecutableOnPath("agy.exe");
        if (fromPath is not null)
        {
            return fromPath;
        }

        return wellKnown
            .Skip(1)
            .FirstOrDefault(path => Path.IsPathFullyQualified(path) && File.Exists(path));
    }

    internal static IReadOnlyList<string> GetWellKnownExecutablePaths(
        string userProfile,
        string localAppData) =>
        new[]
        {
            Path.Combine(userProfile, ".gemini", "bin", "agy.exe"),
            Path.Combine(localAppData, "agy", "bin", "agy.exe"),
        };

    private static async Task<JsonDocument?> TryPostAsync(
        HttpClient client,
        string method,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            // The hub listens on loopback without TLS. It is reachable only from this machine and is
            // gated by the token minted for this process.
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"http://127.0.0.1:{_hubPort}/exa.language_server_pb.LanguageServerService/{method}"));
            request.Headers.Add("Connect-Protocol-Version", "1");
            request.Headers.Add("X-Codeium-Csrf-Token", _hubToken);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode
                ? await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static UsageSnapshot CreateSnapshot(
        IReadOnlyList<UsageMetric> metrics,
        string? plan,
        string? accountEmail) =>
        new(
            string.IsNullOrWhiteSpace(plan) ? "Antigravity" : plan,
            metrics,
            DateTimeOffset.Now,
            "gemini",
            "Google Gemini",
            accountEmail);

    private static bool TryFindQuotaContainer(
        JsonElement element,
        int depth,
        out JsonElement container)
    {
        if (depth > 12)
        {
            container = default;
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (GeminiUsageClient.ParseQuotaSummaryResponse(element).Count > 0)
            {
                container = element;
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindQuotaContainer(property.Value, depth + 1, out container))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindQuotaContainer(item, depth + 1, out container))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.String &&
                 element.GetString() is { } nested &&
                 nested.Length <= MaxOutputCharacters)
        {
            try
            {
                using var nestedDocument = JsonDocument.Parse(nested);
                if (TryFindQuotaContainer(nestedDocument.RootElement, depth + 1, out var nestedContainer))
                {
                    container = nestedContainer.Clone();
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        container = default;
        return false;
    }

    private static string? FindString(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                var nested = FindString(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static void TryKillOwnedProcesses(IEnumerable<int> processIds, DateTime startedAtUtc)
    {
        foreach (var processId in processIds.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                var name = process.ProcessName;
                if (process.StartTime.ToUniversalTime() >= startedAtUtc.AddSeconds(-2) &&
                    IsOwnedProcessName(name))
                {
                    ProcessSecurity.TryKill(process);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    internal static bool IsOwnedProcessName(string name) =>
        name.Equals("agy", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("antigravity", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("language_server", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> TryReadCompletedOutputAsync(Task<string>? task)
    {
        if (task is null)
        {
            return null;
        }

        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            return null;
        }
    }

    private sealed record OwnedPort(int Pid, ushort Port);
}
