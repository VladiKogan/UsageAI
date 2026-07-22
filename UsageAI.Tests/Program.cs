using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using UsageAI.Services;

namespace UsageAI.Tests;

internal static class Program
{
    private static readonly string[] ClaudeProfileScope = { "user:profile" };

    private static readonly (string Name, Func<Task> Run)[] Tests =
    {
        ("credential input validation", TestCredentialInputAsync),
        ("minimal child environment", TestMinimalChildEnvironmentAsync),
        ("bounded CLI output", TestBoundedCliOutputAsync),
        ("bounded HTTP JSON", TestBoundedHttpJsonAsync),
        ("bounded credential files", TestBoundedCredentialFileAsync),
        ("Claude session-key normalization", TestClaudeSessionKeyAsync),
        ("Claude organization parsing", TestClaudeOrganizationParsingAsync),
        ("Claude credential ACL preservation", TestClaudeCredentialAclPreservationAsync),
        ("Claude credential lost-update protection", TestClaudeCredentialLostUpdateAsync),
        ("cross-process refresh lock", TestCrossProcessLockAsync),
    };

    private static async Task<int> Main()
    {
        var failures = 0;
        foreach (var (name, run) in Tests)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} security tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestCredentialInputAsync()
    {
        Equal("safe-token", CredentialInput.NormalizeToken("  safe-token  "));
        Null(CredentialInput.NormalizeToken("token\r\nInjected: value"));
        Null(CredentialInput.NormalizeToken("token\tvalue"));
        Null(CredentialInput.NormalizeToken("token value"));
        Null(CredentialInput.NormalizeToken("token\u007fvalue"));
        Null(CredentialInput.NormalizeToken(new string('x', CredentialInput.MaxTokenCharacters + 1)));
        return Task.CompletedTask;
    }

    private static Task TestMinimalChildEnvironmentAsync()
    {
        const string secretName = "USAGEAI_SECURITY_TEST_SECRET";
        var original = Environment.GetEnvironmentVariable(secretName);
        try
        {
            Environment.SetEnvironmentVariable(secretName, "must-not-be-inherited");
            var startInfo = new ProcessStartInfo { UseShellExecute = false };
            ProcessSecurity.ApplyMinimalEnvironment(startInfo);

            False(startInfo.Environment.ContainsKey(secretName));
            Equal("1", startInfo.Environment["NO_COLOR"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, original);
        }

        return Task.CompletedTask;
    }

    private static async Task TestBoundedCliOutputAsync()
    {
        using var validReader = new StringReader("message\r\nnext");
        Equal("message", await ProcessSecurity.ReadBoundedLineAsync(validReader, 16, CancellationToken.None));

        using var oversizedReader = new StringReader(new string('x', 17));
        await ThrowsAsync<InvalidDataException>(() =>
            ProcessSecurity.ReadBoundedLineAsync(oversizedReader, 16, CancellationToken.None));
    }

    private static async Task TestBoundedHttpJsonAsync()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[SecureHttp.MaxJsonResponseBytes + 1]),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        await ThrowsAsync<InvalidDataException>(() =>
            SecureHttp.ReadJsonDocumentAsync(response, CancellationToken.None));
    }

    private static Task TestBoundedCredentialFileAsync()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "large.json");
            File.WriteAllText(path, new string('x', 64));
            Throws<InvalidDataException>(() => SecureLocalFile.ReadAllText(path, maxCharacters: 32));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }

        return Task.CompletedTask;
    }

    private static Task TestClaudeSessionKeyAsync()
    {
        Equal("sk-ant-session", ClaudeWebUsageClient.NormalizeSessionKey("sessionKey=sk-ant-session"));
        Equal("sk-ant-session", ClaudeWebUsageClient.NormalizeSessionKey(" sk-ant-session "));
        Null(ClaudeWebUsageClient.NormalizeSessionKey("sk-ant-session; other=value"));
        Null(ClaudeWebUsageClient.NormalizeSessionKey("sk-ant-session\r\nInjected: value"));
        return Task.CompletedTask;
    }

    private static Task TestClaudeOrganizationParsingAsync()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "memberships": [
                { "uuid": "membership-id", "organization": { "uuid": "organization-id" } }
              ]
            }
            """);
        Equal("organization-id", ClaudeWebUsageClient.FindOrganizationId(document.RootElement));
        return Task.CompletedTask;
    }

    private static Task TestClaudeCredentialAclPreservationAsync()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, ".credentials.json");
            File.WriteAllText(path, CredentialJson("old-access", "old-refresh"));

            var sid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows SID is unavailable.");
            var security = new FileSecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            const AccessControlSections comparedSections =
                AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
            var before = new FileInfo(path).GetAccessControl(comparedSections)
                .GetSecurityDescriptorSddlForm(comparedSections);

            ClaudeCodeUsageClient.TryPersistRefreshedCredentials(new ClaudeCodeUsageClient.ClaudeCredentials(
                "new-access",
                "new-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                ClaudeProfileScope,
                "Claude",
                path,
                "old-refresh"));

            var after = new FileInfo(path).GetAccessControl(comparedSections)
                .GetSecurityDescriptorSddlForm(comparedSections);
            Equal(before, after);
            var updated = File.ReadAllText(path);
            True(updated.Contains("new-access", StringComparison.Ordinal));
            True(updated.Contains("new-refresh", StringComparison.Ordinal));
            True(updated.Contains("keep-this-value", StringComparison.Ordinal));
            Equal(0, Directory.GetFiles(directory, ".credentials.json.usageai-tmp.*").Length);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }

        return Task.CompletedTask;
    }

    private static Task TestClaudeCredentialLostUpdateAsync()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, ".credentials.json");
            var original = CredentialJson("other-access", "other-refresh");
            File.WriteAllText(path, original);

            ClaudeCodeUsageClient.TryPersistRefreshedCredentials(new ClaudeCodeUsageClient.ClaudeCredentials(
                "new-access",
                "new-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                ClaudeProfileScope,
                "Claude",
                path,
                "stale-refresh"));

            Equal(original, File.ReadAllText(path));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }

        return Task.CompletedTask;
    }

    private static async Task TestCrossProcessLockAsync()
    {
        var directory = CreateTestDirectory();
        try
        {
            var name = $"security-test-{Guid.NewGuid():N}";
            await using (var first = await CrossProcessFileLock.AcquireAsync(
                             name,
                             TimeSpan.FromSeconds(1),
                             CancellationToken.None,
                             directory))
            {
                await ThrowsAsync<TimeoutException>(() => CrossProcessFileLock.AcquireAsync(
                    name,
                    TimeSpan.FromMilliseconds(250),
                    CancellationToken.None,
                    directory));
            }
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static string CredentialJson(string accessToken, string refreshToken) =>
        $$"""
        {
          "mcpOAuth": { "server": { "accessToken": "keep-this-value" } },
          "claudeAiOauth": {
            "accessToken": "{{accessToken}}",
            "refreshToken": "{{refreshToken}}",
            "expiresAt": 1000,
            "scopes": ["user:profile"],
            "subscriptionType": "max"
          }
        }
        """;

    private static string CreateTestDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageAI.SecurityTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UsageAI.SecurityTests"));
        if (!fullPath.StartsWith(expectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove an unexpected test path.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool value) => True(!value);

    private static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, got '{value}'.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
