using System.Runtime.InteropServices;
using System.Text;

namespace UsageAI.Services;

internal static class WindowsCredentialReader
{
    private const uint GenericCredentialType = 1;

    public static IReadOnlyList<string> FindGenericPasswords(string servicePrefix)
    {
        return FindGenericPasswords($"{servicePrefix}*", targetName =>
            targetName.StartsWith($"{servicePrefix}/", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> FindKeyringPasswords(string serviceName)
    {
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.UserName,
        };
        foreach (var environmentName in new[] { "USER", "USERNAME" })
        {
            var account = Environment.GetEnvironmentVariable(environmentName)?.Trim();
            if (!string.IsNullOrWhiteSpace(account))
            {
                accounts.Add(account);
            }
        }

        return FindGenericPasswords(null, targetName =>
            targetName.Equals(serviceName, StringComparison.OrdinalIgnoreCase) ||
            accounts.Any(account =>
                targetName.Equals($"{serviceName}/{account}", StringComparison.OrdinalIgnoreCase) ||
                targetName.Equals($"{serviceName}.{account}", StringComparison.OrdinalIgnoreCase) ||
                targetName.Equals($"{account}.{serviceName}", StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<string> FindGenericPasswords(
        string? filter,
        Func<string, bool> matchesTarget)
    {
        var passwords = new List<string>();
        if (!CredEnumerate(filter, 0, out var count, out var credentials))
        {
            return passwords;
        }

        try
        {
            for (var index = 0; index < count; index++)
            {
                var credentialPointer = Marshal.ReadIntPtr(credentials, index * IntPtr.Size);
                if (credentialPointer == IntPtr.Zero)
                {
                    continue;
                }

                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                var targetName = Marshal.PtrToStringUni(credential.TargetName);
                if (credential.Type != GenericCredentialType ||
                    targetName is null ||
                    !matchesTarget(targetName) ||
                    credential.CredentialBlob == IntPtr.Zero ||
                    credential.CredentialBlobSize == 0 ||
                    credential.CredentialBlobSize > int.MaxValue)
                {
                    continue;
                }

                var bytes = new byte[(int)credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                var password = DecodePassword(bytes);
                if (!string.IsNullOrWhiteSpace(password))
                {
                    passwords.Add(password);
                }
            }
        }
        finally
        {
            CredFree(credentials);
        }

        return passwords;
    }

    private static string DecodePassword(byte[] bytes)
    {
        var looksLikeUtf16 = bytes.Length >= 2 && bytes.Length % 2 == 0 &&
                             (bytes[0] == 0xff && bytes[1] == 0xfe ||
                              Enumerable.Range(1, Math.Min(bytes.Length, 16) / 2)
                                  .Count(index => bytes[index * 2 - 1] == 0) >= 3);
        return (looksLikeUtf16 ? Encoding.Unicode : Encoding.UTF8)
            .GetString(bytes)
            .TrimEnd('\0');
    }

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(
        string? filter,
        uint flags,
        out uint count,
        out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
