using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using UsageAI.Services;

namespace UsageAI.Tests;

internal static class WindowsIntegrationTests
{
    public static Task TestStartupRegistrationAsync()
    {
        var registryPath = $@"Software\UsageAI-Test-Startup-{Guid.NewGuid():N}";
        const string valueName = "UsageAI";
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "UsageAI Test",
            "UsageAI.exe");
        try
        {
            False(StartupManager.IsEnabledAt(registryPath, valueName));
            StartupManager.SetEnabledAt(registryPath, valueName, enabled: true, executable);
            True(StartupManager.IsEnabledAt(registryPath, valueName));
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: false))
            {
                Equal($"\"{executable}\"", key?.GetValue(valueName) as string);
            }

            StartupManager.SetEnabledAt(registryPath, valueName, enabled: false, executablePath: null);
            False(StartupManager.IsEnabledAt(registryPath, valueName));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
        }

        return Task.CompletedTask;
    }

    public static Task TestSingleInstanceMessageAsync()
    {
        var expectedHandle = new IntPtr(0x1234);
        IntPtr actualHandle = IntPtr.Zero;
        var actualMessage = 0;
        True(SingleInstance.PostShow(expectedHandle, (windowHandle, message, wParam, lParam) =>
        {
            actualHandle = windowHandle;
            actualMessage = message;
            Equal(IntPtr.Zero, wParam);
            Equal(IntPtr.Zero, lParam);
            return true;
        }));
        Equal(expectedHandle, actualHandle);
        Equal(SingleInstance.ShowMessage, actualMessage);
        return Task.CompletedTask;
    }

    public static Task TestCredentialManagerAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var prefix = $"UsageAI-Test-Generic-{suffix}";
        var service = $"UsageAI-Test-Keyring-{suffix}";
        const string genericSecret = "synthetic-generic-secret";
        const string keyringSecret = "synthetic-keyring-secret";
        using var generic = TemporaryGenericCredential.Create($"{prefix}/account", genericSecret);
        using var keyring = TemporaryGenericCredential.Create(service, keyringSecret);

        True(WindowsCredentialReader.FindGenericPasswords(prefix).Contains(genericSecret));
        True(WindowsCredentialReader.FindKeyringPasswords(service).Contains(keyringSecret));
        return Task.CompletedTask;
    }

    public static Task TestInstallerLaunchAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "UsageAI.InstallerLaunchTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var installerPath = Path.Combine(directory, "UsageAI-Test-Setup.exe");
            File.WriteAllText(installerPath, "synthetic installer fixture");
            ProcessStartInfo? captured = null;
            UpdateInstaller.Launch(installerPath, startInfo =>
            {
                captured = startInfo;
                return new Process();
            });
            NotNull(captured);
            Equal(Path.GetFullPath(installerPath), captured!.FileName);
            Equal("/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS", captured.Arguments);
            Equal("runas", captured.Verb);
            True(captured.UseShellExecute);

            var missingProcess = Throws<UpdateInstallException>(() =>
                UpdateInstaller.Launch(installerPath, _ => null));
            Equal("Windows could not start the update installer.", missingProcess.Message);

            var cancelled = Throws<UpdateInstallException>(() =>
                UpdateInstaller.Launch(installerPath, _ => throw new Win32Exception(1223)));
            Equal("The update installation was cancelled.", cancelled.Message);

            var failed = Throws<UpdateInstallException>(() =>
                UpdateInstaller.Launch(installerPath, _ => throw new Win32Exception(5)));
            Equal("Windows could not start the update installer.", failed.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
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

    private static void NotNull(object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a value.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private sealed class TemporaryGenericCredential : IDisposable
    {
        private const uint GenericCredentialType = 1;
        private const uint PersistSession = 1;
        private readonly string _targetName;
        private bool _disposed;

        private TemporaryGenericCredential(string targetName) => _targetName = targetName;

        public static TemporaryGenericCredential Create(string targetName, string secret)
        {
            var bytes = Encoding.Unicode.GetBytes(secret);
            var blob = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new NativeCredential
                {
                    Flags = 0,
                    Type = GenericCredentialType,
                    TargetName = targetName,
                    Comment = IntPtr.Zero,
                    LastWritten = default,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = PersistSession,
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    TargetAlias = IntPtr.Zero,
                    UserName = IntPtr.Zero,
                };
                if (!CredWrite(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                for (var index = 0; index < bytes.Length; index++)
                {
                    Marshal.WriteByte(blob, index, 0);
                }
                Marshal.FreeCoTaskMem(blob);
            }

            return new TemporaryGenericCredential(targetName);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _ = CredDelete(_targetName, GenericCredentialType, 0);
            _disposed = true;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string targetName, uint type, uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string TargetName;
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
}
