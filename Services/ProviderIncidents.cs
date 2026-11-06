using System.Text.Json;
using UsageAI.Models;

namespace UsageAI.Services;

/// <summary>How badly a provider's own status page says its service is affected, mildest first.</summary>
internal enum IncidentSeverity
{
    Maintenance,
    Degraded,
    PartialOutage,
    MajorOutage,
}

/// <summary>An ongoing incident a provider reports on its public status page.</summary>
internal sealed record ProviderIncident(string ProviderId, IncidentSeverity Severity, string Title, Uri Page)
{
    public string SeverityLabel => Severity switch
    {
        IncidentSeverity.Maintenance => "Maintenance",
        IncidentSeverity.Degraded => "Degraded",
        IncidentSeverity.PartialOutage => "Partial outage",
        _ => "Outage",
    };

    /// <summary>An outage, partial or full, as opposed to degraded service or maintenance.</summary>
    public bool IsOutage => Severity >= IncidentSeverity.PartialOutage;
}

/// <summary>
/// Reads providers' public status pages, so a card that goes stale can be told apart from a
/// provider that is down. No credential is involved: only the published status JSON is fetched,
/// through the hardened client, at most every five minutes, and only the components that serve the
/// usage UsageAI shows are counted. A failed check keeps the last answer and never marks a provider
/// stale. Gemini has no status page that covers its quota, so it has no source.
/// </summary>
internal sealed class ProviderIncidentService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private const int MaxTitleCharacters = 120;
    private static readonly HttpClient SharedClient = SecureHttp.CreateClient(TimeSpan.FromSeconds(10));

    /// <summary>A provider's status page: a Statuspage page with a component allowlist, or an incident.io page with a component group.</summary>
    internal sealed record Source(string ProviderId, Uri Page, Uri Feed, IReadOnlyList<string>? Components, string? Group);

    internal static readonly IReadOnlyList<Source> Sources =
    [
        new("claude", new Uri("https://status.claude.com/"), new Uri("https://status.claude.com/api/v2/summary.json"), ["claude.ai", "Claude API", "Claude Code"], null),
        new("copilot", new Uri("https://www.githubstatus.com/"), new Uri("https://www.githubstatus.com/api/v2/summary.json"), ["Copilot"], null),
        new("codex", new Uri("https://status.openai.com/"), new Uri("https://status.openai.com/proxy/status.openai.com"), null, "Codex"),
    ];

    private readonly HttpClient _client;
    private readonly IReadOnlyList<Source> _sources;
    private Dictionary<string, ProviderIncident> _current = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextCheck;
    private bool _running;
    private int _generation;

    public ProviderIncidentService()
        : this(SharedClient, Sources)
    {
    }

    internal ProviderIncidentService(HttpClient client, IReadOnlyList<Source> sources)
    {
        _client = client;
        _sources = sources;
    }

    /// <summary>Raised on the calling context when the set of incidents changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The ongoing incident of each provider that has one.</summary>
    public IReadOnlyDictionary<string, ProviderIncident> Current => _current;

    public ProviderIncident? For(string providerId) =>
        _current.TryGetValue(ProviderKey.ProviderOf(providerId), out var incident) ? incident : null;

    /// <summary>
    /// Checks the status pages when due. With <paramref name="enabled"/> false nothing is fetched
    /// and any known incident is cleared.
    /// </summary>
    public async Task RefreshIfDueAsync(bool enabled, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            // A check still in flight sees the new generation and drops its result, so turning the
            // setting off cannot be undone by an answer that arrives a moment later.
            _generation++;
            _nextCheck = default;
            Publish(new Dictionary<string, ProviderIncident>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        if (_running || now < _nextCheck)
        {
            return;
        }

        _running = true;
        _nextCheck = now + CheckInterval;
        var generation = _generation;
        try
        {
            var results = await Task.WhenAll(_sources.Select(source => CheckAsync(source, cancellationToken)));
            if (generation != _generation)
            {
                return;
            }

            var next = new Dictionary<string, ProviderIncident>(_current, StringComparer.OrdinalIgnoreCase);
            foreach (var (source, answered, incident) in results)
            {
                if (!answered)
                {
                    continue;
                }

                if (incident is null)
                {
                    next.Remove(source.ProviderId);
                }
                else
                {
                    next[source.ProviderId] = incident;
                }
            }

            Publish(next);
        }
        finally
        {
            _running = false;
        }
    }

    private async Task<(Source Source, bool Answered, ProviderIncident? Incident)> CheckAsync(Source source, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Feed);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (source, false, null);
            }

            using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
            var incident = source.Group is { } group
                ? ParseIncidentIo(document.RootElement, source.ProviderId, source.Page, group)
                : ParseStatuspage(document.RootElement, source.ProviderId, source.Page, source.Components ?? []);
            return (source, true, incident);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or InvalidDataException or InvalidOperationException ||
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (source, false, null);
        }
    }

    private void Publish(Dictionary<string, ProviderIncident> next)
    {
        var changed = next.Count != _current.Count ||
                      next.Any(entry => !_current.TryGetValue(entry.Key, out var known) || known != entry.Value);
        _current = next;
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static IncidentSeverity? ComponentSeverity(string? status) => status switch
    {
        "degraded_performance" => IncidentSeverity.Degraded,
        "partial_outage" => IncidentSeverity.PartialOutage,
        "major_outage" or "full_outage" => IncidentSeverity.MajorOutage,
        "under_maintenance" => IncidentSeverity.Maintenance,
        _ => null,
    };

    internal static IncidentSeverity? ImpactSeverity(string? impact) => impact switch
    {
        "minor" => IncidentSeverity.Degraded,
        "major" => IncidentSeverity.PartialOutage,
        "critical" => IncidentSeverity.MajorOutage,
        "maintenance" => IncidentSeverity.Maintenance,
        _ => null,
    };

    /// <summary>
    /// A Statuspage <c>summary.json</c>: the worst of the allowlisted components' own status and of
    /// the unresolved incidents (and in-progress maintenance) that touch them, titled after the
    /// first such incident.
    /// </summary>
    internal static ProviderIncident? ParseStatuspage(JsonElement root, string providerId, Uri page, IReadOnlyList<string> allowlist)
    {
        bool Counts(string? name) => name is not null && allowlist.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        IncidentSeverity? worst = null;
        string? title = null;
        Uri? link = null;
        void Consider(IncidentSeverity severity, string? candidateTitle, string? id)
        {
            if (worst is null || severity > worst)
            {
                worst = severity;
            }

            if (title is null && CleanTitle(candidateTitle) is { } cleaned)
            {
                title = cleaned;
                link = id is { Length: > 0 and <= 64 } && id.All(char.IsAsciiLetterOrDigit)
                    ? new Uri(page, $"incidents/{id}")
                    : null;
            }
        }

        foreach (var (property, planned) in new[] { ("incidents", false), ("scheduled_maintenances", true) })
        {
            if (!root.TryGetProperty(property, out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                var status = GetString(entry, "status");
                if (status is null || status == "resolved" || (planned && status != "in_progress"))
                {
                    continue;
                }

                var listed = entry.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array
                    ? components.EnumerateArray().ToArray()
                    : [];
                // Only an incident that names an allowlisted component counts. GitHub and Anthropic
                // often open an incident before attaching components, and such an incident may be
                // about any of their products; the component's own status still reports it once the
                // page attributes it.
                var affected = listed.Where(component => Counts(GetString(component, "name"))).ToArray();
                if (affected.Length == 0)
                {
                    continue;
                }

                var severity = planned
                    ? IncidentSeverity.Maintenance
                    : affected.Select(component => ComponentSeverity(GetString(component, "status"))).Max() ??
                      ImpactSeverity(GetString(entry, "impact"));
                if (severity is { } known)
                {
                    Consider(known, GetString(entry, "name"), GetString(entry, "id"));
                }
            }
        }

        if (root.TryGetProperty("components", out var all) && all.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in all.EnumerateArray())
            {
                var name = GetString(component, "name");
                if (Counts(name) && ComponentSeverity(GetString(component, "status")) is { } severity)
                {
                    Consider(severity, null, null);
                    title ??= CleanTitle($"{name}: {Label(severity).ToLowerInvariant()}");
                }
            }
        }

        return worst is { } level
            ? new ProviderIncident(providerId, level, title ?? Label(level), link ?? page)
            : null;
    }

    /// <summary>
    /// An incident.io page's proxy feed: the components of one group, such as "Codex", whose
    /// affected status is not operational, titled after the ongoing incident that names the group.
    /// </summary>
    internal static ProviderIncident? ParseIncidentIo(JsonElement root, string providerId, Uri page, string group)
    {
        if (!root.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var members = new HashSet<string>(StringComparer.Ordinal);
        if (summary.TryGetProperty("structure", out var structure) &&
            structure.ValueKind == JsonValueKind.Object &&
            structure.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("group", out var entry) &&
                    entry.ValueKind == JsonValueKind.Object &&
                    string.Equals(GetString(entry, "name"), group, StringComparison.Ordinal) &&
                    entry.TryGetProperty("components", out var components) &&
                    components.ValueKind == JsonValueKind.Array)
                {
                    foreach (var component in components.EnumerateArray())
                    {
                        if (GetString(component, "component_id") is { Length: > 0 } id)
                        {
                            members.Add(id);
                        }
                    }
                }
            }
        }

        IncidentSeverity? worst = null;
        if (summary.TryGetProperty("affected_components", out var affected) && affected.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in affected.EnumerateArray())
            {
                if (GetString(component, "component_id") is { } id &&
                    members.Contains(id) &&
                    ComponentSeverity(GetString(component, "status")) is { } severity &&
                    (worst is null || severity > worst))
                {
                    worst = severity;
                }
            }
        }

        if (worst is not { } level)
        {
            return null;
        }

        string? title = null;
        if (summary.TryGetProperty("ongoing_incidents", out var ongoing) && ongoing.ValueKind == JsonValueKind.Array)
        {
            var names = ongoing.EnumerateArray().Select(incident => GetString(incident, "name")).OfType<string>().ToArray();
            title = CleanTitle(names.FirstOrDefault(name => name.Contains(group, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault());
        }

        return new ProviderIncident(providerId, level, title ?? $"{group}: {Label(level).ToLowerInvariant()}", page);
    }

    private static string Label(IncidentSeverity severity) => new ProviderIncident(string.Empty, severity, string.Empty, new Uri("https://example.invalid")).SeverityLabel;

    /// <summary>A status page title made safe to show: control characters dropped, length capped.</summary>
    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var cleaned = new string(title.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned.Length > MaxTitleCharacters ? cleaned[..MaxTitleCharacters].TrimEnd() + "…" : cleaned;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
