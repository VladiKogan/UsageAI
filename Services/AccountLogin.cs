using System.Diagnostics;
using System.Text.RegularExpressions;

namespace UsageAI.Services;

internal enum AccountLoginOutcome
{
    SignedIn,
    Cancelled,
    TimedOut,
    Failed,
}

internal sealed record AccountLoginResult(AccountLoginOutcome Outcome, string? Message = null);

/// <summary>
/// "Sign in to another account": runs the provider's official CLI login with its folder variable
/// pointed at a new folder. The CLI does the whole sign-in and writes its own credential file
/// there; UsageAI only starts it, shows the sign-in link it prints, and waits for the file.
/// </summary>
internal static partial class AccountLogin
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private const int MaxRetainedOutputCharacters = 16_384;

    [GeneratedRegex(@"https://[^\s""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    /// <summary>
    /// Signs <paramref name="folder"/> in. A folder this attempt created is deleted again when the
    /// sign-in does not finish, since everything in it came from this attempt.
    /// </summary>
    public static async Task<AccountLoginResult> RunAsync(
        string providerId,
        string folder,
        Action<Uri>? signInLinkFound,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        Func<string, ProcessStartInfo?>? createStartInfo = null)
    {
        var created = !Directory.Exists(folder);
        ProcessStartInfo? startInfo;
        try
        {
            startInfo = (createStartInfo ?? CreateStartInfo(providerId))(folder);
        }
        catch (CodexUsageException exception)
        {
            return new AccountLoginResult(AccountLoginOutcome.Failed, exception.Message);
        }

        if (startInfo is null)
        {
            return new AccountLoginResult(
                AccountLoginOutcome.Failed,
                $"The {AccountDiscovery.ProviderName(providerId)} CLI was not found. Install it, then try again.");
        }

        Directory.CreateDirectory(folder);
        var credentialFile = Path.Combine(folder, AccountDiscovery.CredentialFileName(providerId));

        // Re-authenticating a folder that already holds a login waits for the CLI to write it anew.
        DateTime? writtenBefore = File.Exists(credentialFile) ? File.GetLastWriteTimeUtc(credentialFile) : null;
        bool SignedIn() => File.Exists(credentialFile) && File.GetLastWriteTimeUtc(credentialFile) != writtenBefore;
        Process? process = null;
        var outcome = AccountLoginOutcome.Failed;
        string? message = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return new AccountLoginResult(AccountLoginOutcome.Failed, "Windows could not start the sign-in.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? Timeout);
            var linkSeen = 0;
            void Watch(string? line)
            {
                if (line is null || UrlPattern().Match(line) is not { Success: true } match)
                {
                    return;
                }

                if (Uri.TryCreate(match.Value.TrimEnd('.', ',', ')'), UriKind.Absolute, out var link) &&
                    link.Scheme == Uri.UriSchemeHttps &&
                    Interlocked.Exchange(ref linkSeen, 1) == 0)
                {
                    signInLinkFound?.Invoke(link);
                }
            }

            var stdout = WatchLinesAsync(process.StandardOutput, Watch, deadline.Token);
            var stderr = WatchLinesAsync(process.StandardError, Watch, deadline.Token);
            try
            {
                while (!SignedIn())
                {
                    if (process.HasExited)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
                }

                if (SignedIn())
                {
                    // The CLI may still be writing the rest of its state; give it a moment to finish.
                    using var settle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    settle.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await process.WaitForExitAsync(settle.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Signed in; a CLI that lingers is stopped below.
                    }

                    outcome = AccountLoginOutcome.SignedIn;
                }
                else
                {
                    message = $"The sign-in ended without a login (exit code {process.ExitCode}).";
                }
            }
            catch (OperationCanceledException)
            {
                outcome = cancellationToken.IsCancellationRequested ? AccountLoginOutcome.Cancelled : AccountLoginOutcome.TimedOut;
                message = outcome == AccountLoginOutcome.TimedOut ? "The sign-in did not finish within two minutes." : null;
            }

            ProcessSecurity.TryKill(process);
            await Task.WhenAll(Quietly(stdout), Quietly(stderr));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            message = "Windows could not start the sign-in.";
        }
        finally
        {
            ProcessSecurity.TryKill(process);
            process?.Dispose();
            if (outcome != AccountLoginOutcome.SignedIn && created)
            {
                TryDelete(folder);
            }
        }

        return new AccountLoginResult(outcome, message);
    }

    private static Func<string, ProcessStartInfo?> CreateStartInfo(string providerId) =>
        providerId.Equals(AccountDiscovery.ClaudeProviderId, StringComparison.OrdinalIgnoreCase)
            ? ClaudeCodeUsageClient.CreateLoginStartInfo
            : CodexUsageClient.CreateLoginStartInfo;

    private static async Task WatchLinesAsync(StreamReader reader, Action<string?> onLine, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < MaxRetainedOutputCharacters * 16)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            read += line.Length;
            onLine(line.Length > 4096 ? line[..4096] : line);
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or TimeoutException or ObjectDisposedException)
        {
            // The streams close with the process.
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder still held open stays; it holds no finished login.
        }
    }
}
