using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace UsageAI.Services;

internal sealed class UpdateInstallException(string message) : Exception(message);

/// <summary>
/// Downloads the exact installer assets advertised by the pinned UsageAI GitHub repository,
/// follows only GitHub's known release-asset redirects, and verifies the published SHA-256 before
/// allowing Windows to execute the installer.
/// </summary>
internal static class UpdateInstaller
{
    internal const long MaximumInstallerBytes = 128L * 1024 * 1024;
    internal const int MaximumChecksumBytes = 4_096;

    private const int MaximumRedirects = 5;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly HttpClient SharedClient = SecureHttp.CreateClient(DownloadTimeout);
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    public static async Task<string> DownloadAndVerifyAsync(
        UpdateRelease release,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        return await DownloadAndVerifyAsync(
            release,
            SharedClient,
            AppPaths.UpdateDirectory,
            timeout.Token);
    }

    internal static async Task<string> DownloadAndVerifyAsync(
        UpdateRelease release,
        HttpClient client,
        string updateDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(client);

        var installer = release.Installer;
        var checksum = release.Checksum;
        if (installer is null || checksum is null)
        {
            throw new UpdateInstallException("This release does not include a verifiable Windows installer.");
        }

        if (installer.Size is <= 0 or > MaximumInstallerBytes ||
            checksum.Size is <= 0 or > MaximumChecksumBytes)
        {
            throw new UpdateInstallException("The published update has an unexpected size.");
        }

        Directory.CreateDirectory(updateDirectory);
        var finalPath = Path.Combine(
            Path.GetFullPath(updateDirectory),
            $"UsageAI-{release.Version}-Setup-{Guid.NewGuid():N}.exe");
        var partialPath = $"{finalPath}.partial";

        try
        {
            var checksumBytes = await DownloadBytesAsync(
                client,
                checksum,
                MaximumChecksumBytes,
                cancellationToken);
            var expectedHash = ParseChecksum(checksumBytes, installer.Name);

            await DownloadFileAsync(client, installer, partialPath, cancellationToken);
            var actualHash = await ComputeSha256Async(partialPath, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                throw new UpdateInstallException("The downloaded installer failed its SHA-256 verification.");
            }

            File.Move(partialPath, finalPath);
            return finalPath;
        }
        catch (UpdateInstallException)
        {
            DeleteBestEffort(partialPath);
            DeleteBestEffort(finalPath);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or OperationCanceledException
                                              or CryptographicException)
        {
            DeleteBestEffort(partialPath);
            DeleteBestEffort(finalPath);
            throw new UpdateInstallException("The update could not be downloaded and verified.");
        }
    }

    public static void Launch(string installerPath) => Launch(installerPath, Process.Start);

    internal static void Launch(
        string installerPath,
        Func<ProcessStartInfo, Process?> startProcess)
    {
        var fullPath = Path.GetFullPath(installerPath);
        if (!Path.IsPathFullyQualified(fullPath) ||
            !File.Exists(fullPath) ||
            !Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateInstallException("The verified update installer is no longer available.");
        }

        try
        {
            using var process = startProcess(new ProcessStartInfo
            {
                FileName = fullPath,
                Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null)
            {
                throw new UpdateInstallException("Windows could not start the update installer.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new UpdateInstallException(
                exception.NativeErrorCode == 1223
                    ? "The update installation was cancelled."
                    : "Windows could not start the update installer.");
        }
    }

    private static async Task<byte[]> DownloadBytesAsync(
        HttpClient client,
        UpdateAsset asset,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SendFollowingSafeRedirectsAsync(client, asset.DownloadUrl, cancellationToken);
        EnsureSuccessfulResponse(response, asset.Size, maximumBytes);
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream((int)Math.Min(asset.Size, maximumBytes));
        var buffer = new byte[4096];
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > maximumBytes)
            {
                throw new UpdateInstallException("The published checksum is unexpectedly large.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (memory.Length != asset.Size)
        {
            throw new UpdateInstallException("The published checksum download was incomplete.");
        }

        return memory.ToArray();
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        UpdateAsset asset,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await SendFollowingSafeRedirectsAsync(client, asset.DownloadUrl, cancellationToken);
        EnsureSuccessfulResponse(response, asset.Size, MaximumInstallerBytes);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[81_920];
        long written = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            written += read;
            if (written > asset.Size || written > MaximumInstallerBytes)
            {
                throw new UpdateInstallException("The downloaded installer is larger than advertised.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (written != asset.Size)
        {
            throw new UpdateInstallException("The installer download was incomplete.");
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendFollowingSafeRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirects = 0; redirects <= MaximumRedirects; redirects++)
        {
            ValidateDownloadUri(currentUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            request.Headers.UserAgent.ParseAdd(AppIdentity.UserAgent);
            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects == MaximumRedirects)
            {
                throw new UpdateInstallException("GitHub returned an invalid update download redirect.");
            }

            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        }

        throw new UpdateInstallException("GitHub returned too many update download redirects.");
    }

    private static void ValidateDownloadUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !AllowedDownloadHosts.Contains(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new UpdateInstallException("GitHub returned an unsafe update download address.");
        }
    }

    private static void EnsureSuccessfulResponse(
        HttpResponseMessage response,
        long expectedBytes,
        long maximumBytes)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateInstallException("GitHub did not return the requested update file.");
        }

        if (response.Content.Headers.ContentLength is { } length &&
            (length != expectedBytes || length > maximumBytes))
        {
            throw new UpdateInstallException("The update download size does not match the release metadata.");
        }
    }

    private static byte[] ParseChecksum(byte[] contents, string expectedFileName)
    {
        if (contents.Any(value => value > 0x7f))
        {
            throw new UpdateInstallException("The published checksum is invalid.");
        }

        var text = Encoding.ASCII.GetString(contents).Trim();
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !parts[1].TrimStart('*').Equals(expectedFileName, StringComparison.Ordinal) ||
            parts[0].Length != 64)
        {
            throw new UpdateInstallException("The published checksum is invalid.");
        }

        try
        {
            return Convert.FromHexString(parts[0]);
        }
        catch (FormatException)
        {
            throw new UpdateInstallException("The published checksum is invalid.");
        }
    }

    private static async Task<byte[]> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
