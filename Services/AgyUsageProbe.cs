using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

/// <summary>
/// Reads quota through Google's official Antigravity CLI without owning its credentials.
/// The CLI is short-lived, stdin is closed, output is bounded, and only processes created
/// by this probe are eligible for cleanup.
/// </summary>
internal static class AgyUsageProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProcessDiscoveryTimeout = TimeSpan.FromSeconds(3);
    private const int MaxOutputCharacters = 262_144;
    private const int MaxErrorCharacters = 16_384;

    public static async Task<UsageSnapshot?> TryFetchAsync(CancellationToken cancellationToken)
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

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                    request.RequestUri?.Host is "127.0.0.1" or "localhost",
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };

            while (!timeout.IsCancellationRequested)
            {
                var candidates = await DiscoverOwnedPortsAsync(
                    process.Id,
                    timeout.Token);
                foreach (var candidate in candidates)
                {
                    ownedProcessIds.Add(candidate.Pid);
                }

                foreach (var port in candidates
                             .Select(candidate => candidate.Port)
                             .Distinct()
                             .Take(32))
                {
                    var snapshot = await TryQueryPortAsync(client, port, timeout.Token);
                    if (snapshot is not null)
                    {
                        return snapshot;
                    }
                }

                if (process.HasExited)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            }
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

    private static async Task<IReadOnlyList<OwnedPort>> DiscoverOwnedPortsAsync(
        int rootPid,
        CancellationToken cancellationToken)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powershell))
        {
            return Array.Empty<OwnedPort>();
        }

        // rootPid is an integer produced by Process.Start, so interpolating it cannot alter the script.
        var script =
            $"$root={rootPid};$all=Get-CimInstance Win32_Process;$ids=[System.Collections.Generic.HashSet[int]]::new();" +
            "$null=$ids.Add($root);do{$changed=$false;foreach($p in $all){if($ids.Contains([int]$p.ParentProcessId)-and$ids.Add([int]$p.ProcessId)){$changed=$true}}}while($changed);" +
            "foreach($owner in $ids){\"P`t$owner\";Get-NetTCPConnection -OwningProcess $owner -State Listen -ErrorAction SilentlyContinue|ForEach-Object{\"L`t$owner`t$($_.LocalPort)\"}}";
        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        ProcessSecurity.ApplyMinimalEnvironment(startInfo);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return Array.Empty<OwnedPort>();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProcessDiscoveryTimeout);
        var outputTask = ProcessSecurity.DrainTextAsync(
            process.StandardOutput,
            MaxOutputCharacters,
            timeout.Token);
        var errorTask = ProcessSecurity.DrainTextAsync(
            process.StandardError,
            MaxErrorCharacters,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            await errorTask;
            return ParseOwnedPorts(output);
        }
        finally
        {
            ProcessSecurity.TryKill(process);
        }
    }

    private static List<OwnedPort> ParseOwnedPorts(string output)
    {
        var ownedPids = new HashSet<int>();
        var ports = new List<OwnedPort>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length == 2 && parts[0] == "P" && int.TryParse(parts[1], out var pid))
            {
                ownedPids.Add(pid);
            }
            else if (parts.Length == 3 && parts[0] == "L" &&
                     int.TryParse(parts[1], out pid) &&
                     ushort.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                ownedPids.Add(pid);
                ports.Add(new OwnedPort(pid, port));
            }
        }

        // Preserve discovered child IDs for cleanup even before they bind a port.
        ports.AddRange(ownedPids
            .Where(pid => ports.All(candidate => candidate.Pid != pid))
            .Select(pid => new OwnedPort(pid, 0)));
        return ports;
    }

    private static async Task<UsageSnapshot?> TryQueryPortAsync(
        HttpClient client,
        ushort port,
        CancellationToken cancellationToken)
    {
        if (port == 0)
        {
            return null;
        }

        var status = await TryPostAsync(
            client,
            port,
            "GetUserStatus",
            "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}",
            cancellationToken);
        var snapshot = status is null
            ? null
            : GeminiUsageClient.ParseAntigravityUserStatus(status.RootElement);

        var summary = await TryPostAsync(
            client,
            port,
            "RetrieveUserQuotaSummary",
            "{\"request\":{},\"forceRefresh\":false}",
            cancellationToken);
        var metrics = summary is null
            ? Array.Empty<UsageMetric>()
            : GeminiUsageClient.ParseQuotaSummaryResponse(summary.RootElement);

        status?.Dispose();
        summary?.Dispose();

        if (snapshot is not null)
        {
            return metrics.Count == 0
                ? snapshot
                : snapshot with
                {
                    Metrics = GeminiUsageClient.MergeAntigravityQuotaSummaryMetrics(
                        snapshot.Metrics,
                        metrics),
                };
        }

        return metrics.Count == 0 ? null : CreateSnapshot(metrics, null, null);
    }

    private static async Task<JsonDocument?> TryPostAsync(
        HttpClient client,
        ushort port,
        string method,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/{method}");
            request.Headers.Add("Connect-Protocol-Version", "1");
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
