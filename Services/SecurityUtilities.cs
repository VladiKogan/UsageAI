using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace UsageAI.Services;

internal static class AppIdentity
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    public static string UserAgent => $"UsageAI/{Version}";
}

internal static class SecureHttp
{
    public const int MaxJsonResponseBytes = 1_048_576;

    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.Online,
            },
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout,
            MaxResponseContentBufferSize = MaxJsonResponseBytes,
        };
    }

    public static async Task<JsonDocument> ReadJsonDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        int maxBytes = MaxJsonResponseBytes)
    {
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException("The provider returned an oversized response.");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null &&
            !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The provider returned a non-JSON response.");
        }

        var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var limitedStream = new LimitedReadStream(contentStream, maxBytes);
        return await JsonDocument.ParseAsync(
            limitedStream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            },
            cancellationToken);
    }

    private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, BoundedCount(count));
            RecordRead(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..BoundedCount(buffer.Length)]);
            RecordRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(
                buffer[..BoundedCount(buffer.Length)],
                cancellationToken);
            RecordRead(read);
            return read;
        }

        private int BoundedCount(int requested)
        {
            var remainingWithSentinel = maxBytes - _bytesRead + 1;
            return (int)Math.Min(requested, Math.Max(1, remainingWithSentinel));
        }

        private void RecordRead(int read)
        {
            _bytesRead += read;
            if (_bytesRead > maxBytes)
            {
                throw new InvalidDataException("The provider returned an oversized response.");
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}

internal static class SecureLocalFile
{
    public const int MaxCredentialFileCharacters = 1_048_576;

    public static string ReadAllText(string path, int maxCharacters = MaxCredentialFileCharacters)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > maxCharacters * 4L)
        {
            throw new InvalidDataException("The credential file is unexpectedly large.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var result = new StringBuilder(Math.Min(maxCharacters, (int)Math.Min(stream.Length, 16_384)));
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var read = reader.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    return result.ToString();
                }

                if (result.Length + read > maxCharacters)
                {
                    throw new InvalidDataException("The credential file is unexpectedly large.");
                }

                result.Append(buffer, 0, read);
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}

internal static class ProcessSecurity
{
    private static readonly string[] CommonEnvironmentNames =
    {
        "APPDATA",
        "COMSPEC",
        "HOME",
        "HOMEDRIVE",
        "HOMEPATH",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "LANG",
        "LC_ALL",
        "LOCALAPPDATA",
        "NO_PROXY",
        "NUMBER_OF_PROCESSORS",
        "PATH",
        "PATHEXT",
        "PROCESSOR_ARCHITECTURE",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "SystemDrive",
        "SystemRoot",
        "TEMP",
        "TMP",
        "USERDOMAIN",
        "USERNAME",
        "USERPROFILE",
        "WINDIR",
    };

    public static void ApplyMinimalEnvironment(
        ProcessStartInfo startInfo,
        params string[] additionalEnvironmentNames)
    {
        var names = CommonEnvironmentNames.Concat(additionalEnvironmentNames).Distinct(
            StringComparer.OrdinalIgnoreCase);
        var allowed = names
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(item => !string.IsNullOrEmpty(item.Value))
            .ToArray();

        startInfo.Environment.Clear();
        foreach (var (name, value) in allowed)
        {
            startInfo.Environment[name] = value;
        }

        startInfo.Environment["NO_COLOR"] = "1";
    }

    public static string? FindAbsoluteExecutableOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(cleanDirectory))
            {
                continue;
            }

            var candidate = Path.GetFullPath(Path.Combine(cleanDirectory, fileName));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static async Task<string> DrainTextAsync(
        TextReader reader,
        int retainedCharacterLimit,
        CancellationToken cancellationToken)
    {
        var retained = new StringBuilder(Math.Min(retainedCharacterLimit, 4096));
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    return retained.ToString();
                }

                var remaining = retainedCharacterLimit - retained.Length;
                if (remaining > 0)
                {
                    retained.Append(buffer, 0, Math.Min(remaining, read));
                }
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    public static async Task<string?> ReadBoundedLineAsync(
        TextReader reader,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var line = new StringBuilder(Math.Min(maxCharacters, 4096));
        var singleCharacter = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(singleCharacter.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return line.Length == 0 ? null : line.ToString();
            }

            var value = singleCharacter[0];
            if (value == '\n')
            {
                return line.ToString();
            }

            if (value != '\r')
            {
                if (line.Length >= maxCharacters)
                {
                    throw new InvalidDataException("The provider CLI returned an oversized message.");
                }

                line.Append(value);
            }
        }
    }

    public static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

internal static class CredentialInput
{
    public const int MaxTokenCharacters = 16_384;

    public static string? NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value.Trim();
        return token.Length <= MaxTokenCharacters &&
               token.All(character => character is >= '!' and <= '~')
            ? token
            : null;
    }
}

internal sealed class CrossProcessFileLock : IAsyncDisposable
{
    private readonly FileStream _stream;

    private CrossProcessFileLock(FileStream stream)
    {
        _stream = stream;
    }

    public static async Task<CrossProcessFileLock> AcquireAsync(
        string lockName,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? lockDirectoryOverride = null)
    {
        var lockDirectory = lockDirectoryOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UsageAI",
                "locks");
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, $"{lockName}.lock");
        var startedAt = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
                return new CrossProcessFileLock(stream);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }

        throw new TimeoutException("Timed out waiting for another UsageAI credential refresh to finish.");
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
