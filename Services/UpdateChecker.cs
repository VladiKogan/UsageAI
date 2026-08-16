using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageAI.Services;

internal sealed record UpdateAsset(string Name, Uri DownloadUrl, long Size);

internal sealed record UpdateRelease(
    string Tag,
    string Version,
    Uri ReleasePageUrl,
    UpdateAsset? Installer,
    UpdateAsset? Checksum);

/// <summary>
/// Automatic discovery of newer published releases. The caller persists the time of each attempt so
/// a tray process that stays open does not contact GitHub more than once per day.
/// </summary>
internal static class UpdateChecker
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly Uri LatestReleaseEndpoint =
        new("https://api.github.com/repos/VladiKogan/UsageAI/releases/latest");
    private static readonly Uri ReleasesPage =
        new("https://github.com/VladiKogan/UsageAI/releases/latest");

    private static readonly HttpClient SharedClient = SecureHttp.CreateClient(RequestTimeout);
    private static readonly char[] PrereleaseSeparators = { '-', '+' };

    public static Task<UpdateRelease?> FindNewerReleaseAsync(CancellationToken cancellationToken) =>
        FindNewerReleaseAsync(SharedClient, cancellationToken);

    internal static async Task<UpdateRelease?> FindNewerReleaseAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await SecureHttp.ReadJsonDocumentAsync(response, cancellationToken);
            return ParseRelease(document.RootElement, AppIdentity.Version);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or JsonException
                                              or InvalidDataException
                                              or OperationCanceledException)
        {
            return null;
        }
    }

    internal static bool IsCheckDue(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc) =>
        lastCheckUtc is null ||
        nowUtc.ToUniversalTime() - lastCheckUtc.Value.ToUniversalTime() >= CheckInterval;

    internal static bool IsNewer(string releaseTag, string currentVersion)
    {
        var candidate = ParseVersion(releaseTag);
        var current = ParseVersion(currentVersion);
        return candidate is not null && current is not null && candidate > current;
    }

    private static UpdateRelease? ParseRelease(JsonElement root, string currentVersion)
    {
        if (!root.TryGetProperty("tag_name", out var tagElement) ||
            tagElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var tag = tagElement.GetString();
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 32 || !IsNewer(tag, currentVersion))
        {
            return null;
        }

        var parsedVersion = ParseVersion(tag);
        if (parsedVersion is null)
        {
            return null;
        }

        var version = tag.Trim().TrimStart('v', 'V');
        var prereleaseCut = version.IndexOfAny(PrereleaseSeparators);
        if (prereleaseCut >= 0)
        {
            version = version[..prereleaseCut];
        }

        var expectedInstallerName = $"UsageAI-{version}-Setup.exe";
        var expectedChecksumName = $"{expectedInstallerName}.sha256";
        UpdateAsset? installer = null;
        UpdateAsset? checksum = null;

        if (root.TryGetProperty("assets", out var assetsElement) &&
            assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetElement in assetsElement.EnumerateArray())
            {
                var asset = ParseAsset(assetElement);
                if (asset is null)
                {
                    continue;
                }

                if (asset.Name.Equals(expectedInstallerName, StringComparison.Ordinal))
                {
                    installer = asset;
                }
                else if (asset.Name.Equals(expectedChecksumName, StringComparison.Ordinal))
                {
                    checksum = asset;
                }
            }
        }

        var releasePage = ReleasesPage;
        if (root.TryGetProperty("html_url", out var pageElement) &&
            pageElement.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(pageElement.GetString(), UriKind.Absolute, out var candidatePage) &&
            IsExpectedGitHubReleaseUri(candidatePage))
        {
            releasePage = candidatePage;
        }

        return new UpdateRelease(tag, parsedVersion.ToString(), releasePage, installer, checksum);
    }

    private static UpdateAsset? ParseAsset(JsonElement element)
    {
        if (!element.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("browser_download_url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("size", out var sizeElement) ||
            !sizeElement.TryGetInt64(out var size))
        {
            return null;
        }

        var name = nameElement.GetString();
        return !string.IsNullOrWhiteSpace(name) &&
               name.Length <= 128 &&
               size > 0 &&
               Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var downloadUrl) &&
               IsExpectedGitHubReleaseUri(downloadUrl)
            ? new UpdateAsset(name, downloadUrl, size)
            : null;
    }

    private static bool IsExpectedGitHubReleaseUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith(
            "/VladiKogan/UsageAI/releases/",
            StringComparison.OrdinalIgnoreCase);

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
