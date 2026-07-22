using System.Runtime.InteropServices;
using System.Text;

namespace UsageAI.Services;

internal static class WindowsCredentialReader
{
    private const uint GenericCredentialType = 1;

    public static IReadOnlyList<string> FindGenericPasswords(string servicePrefix)
    {
        var passwords = new List<string>();
        if (!CredEnumerate($"{servicePrefix}*", 0, out var count, out var credentials))
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
                    !targetName.StartsWith($"{servicePrefix}/", StringComparison.OrdinalIgnoreCase) ||
                    credential.CredentialBlob == IntPtr.Zero ||
                    credential.CredentialBlobSize == 0 ||
                    credential.CredentialBlobSize > int.MaxValue)
                {
                    continue;
                }

                var bytes = new byte[(int)credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                var password = Encoding.UTF8.GetString(bytes);
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

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(
        string filter,
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
