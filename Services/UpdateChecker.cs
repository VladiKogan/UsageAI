using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageAI.Services;

/// <summary>
/// Opt-in check for a newer published release. Releases are cut and uploaded by hand, so
/// without this there is no way to learn that one exists. It is off by default and contacts
/// GitHub only when the user turns it on.
/// </summary>
internal static class UpdateChecker
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly Uri LatestReleaseEndpoint =
        new("https://api.github.com/repos/VladiKogan/UsageAI/releases/latest");

    private static readonly HttpClient Client = SecureHttp.CreateClient(RequestTimeout);
    private static readonly char[] PrereleaseSeparators = { '-', '+' };

    /// <summary>The newer release tag, or null when up to date or the check cannot be made.</summary>
    public static async Task<string?> FindNewerReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
            if (!document.RootElement.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 32)
            {
                return null;
            }

            return IsNewer(tag, AppIdentity.Version) ? tag : null;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or JsonException
                                              or InvalidDataException
                                              or OperationCanceledException)
        {
            return null;
        }
    }

    internal static bool IsNewer(string releaseTag, string currentVersion)
    {
        var candidate = ParseVersion(releaseTag);
        var current = ParseVersion(currentVersion);
        return candidate is not null && current is not null && candidate > current;
    }

    private static Version? ParseVersion(string value)
    {
        var trimmed = value.Trim().TrimStart('v', 'V');
        var cut = trimmed.IndexOfAny(PrereleaseSeparators);
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        return Version.TryParse(trimmed, out var parsed)
            ? parsed
            : int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)
                ? new Version(major, 0)
                : null;
    }
}
