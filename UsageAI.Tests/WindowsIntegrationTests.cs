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
        var root = $@"Software\UsageAI-Test-Startup-{Guid.NewGuid():N}";
        var runPath = $@"{root}\Run";
        var approvedPath = $@"{root}\StartupApproved\Run";
        const string valueName = "UsageAI";
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "UsageAI Test",
            "UsageAI.exe");
        var otherCopy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "UsageAI Portable",
            "UsageAI.exe");
        try
        {
            Equal(StartupStatus.NotRegistered, ReadStatus(runPath, approvedPath, valueName, executable));
            Throws<InvalidOperationException>(() => StartupManager.SetEnabledAt(
                runPath,
                approvedPath,
                valueName,
                enabled: true,
                executablePath: null));

            StartupManager.SetEnabledAt(runPath, approvedPath, valueName, enabled: true, executable);
            var enabled = StartupManager.ReadStateAt(runPath, approvedPath, valueName, executable);
            Equal(StartupStatus.Enabled, enabled.Status);
            True(enabled.WillRun);
            Equal(executable, enabled.RegisteredPath);
            using (var key = Registry.CurrentUser.OpenSubKey(runPath, writable: false))
            {
                Equal($"\"{executable}\"", key?.GetValue(valueName) as string);
            }

            // Task Manager and Settings leave the Run value in place and record the veto beside
            // it, which is the case a presence check keeps reporting as enabled.
            SetApproval(approvedPath, valueName, approved: false);
            var blocked = StartupManager.ReadStateAt(runPath, approvedPath, valueName, executable);
            Equal(StartupStatus.BlockedByWindows, blocked.Status);
            False(blocked.WillRun);

            // A flag Windows wrote as approved is not a veto.
            SetApproval(approvedPath, valueName, approved: true);
            Equal(StartupStatus.Enabled, ReadStatus(runPath, approvedPath, valueName, executable));

            // Re-enabling clears the veto, so ticking the box always ends in a state that runs.
            SetApproval(approvedPath, valueName, approved: false);
            StartupManager.SetEnabledAt(runPath, approvedPath, valueName, enabled: true, executable);
            Equal(StartupStatus.Enabled, ReadStatus(runPath, approvedPath, valueName, executable));
            using (var key = Registry.CurrentUser.OpenSubKey(approvedPath, writable: false))
            {
                Null(key?.GetValue(valueName));
            }

            // An entry outliving the copy that wrote it is the other way a presence check lies.
            var moved = StartupManager.ReadStateAt(runPath, approvedPath, valueName, otherCopy);
            Equal(StartupStatus.PathMismatch, moved.Status);
            Equal(executable, moved.RegisteredPath);

            // With no running executable to compare against, a registered entry is not accused of
            // pointing somewhere else.
            Equal(StartupStatus.Enabled, ReadStatus(runPath, approvedPath, valueName, null));

            // Unquoted, unterminated, and non-string entries come from other tools, never from
            // UsageAI, and none of them may throw out of a state read.
            using (var key = Registry.CurrentUser.CreateSubKey(runPath, writable: true))
            {
                key.SetValue(valueName, executable);
                Equal(StartupStatus.Enabled, ReadStatus(runPath, approvedPath, valueName, executable));
                key.SetValue(valueName, "\"" + executable);
                Equal(StartupStatus.Enabled, ReadStatus(runPath, approvedPath, valueName, executable));
                key.SetValue(valueName, "\"\"");
                Equal(StartupStatus.PathMismatch, ReadStatus(runPath, approvedPath, valueName, executable));
                key.SetValue(valueName, 1);
                Equal(StartupStatus.NotRegistered, ReadStatus(runPath, approvedPath, valueName, executable));
            }

            StartupManager.SetEnabledAt(runPath, approvedPath, valueName, enabled: true, executable);
            SetApproval(approvedPath, valueName, approved: false);
            StartupManager.SetEnabledAt(runPath, approvedPath, valueName, enabled: false, executablePath: null);
            Equal(StartupStatus.NotRegistered, ReadStatus(runPath, approvedPath, valueName, executable));
            using (var key = Registry.CurrentUser.OpenSubKey(approvedPath, writable: false))
            {
                Null(key?.GetValue(valueName));
            }

            // Disabling twice, and disabling on a machine that has never vetoed anything, are
            // both no-ops rather than errors.
            Registry.CurrentUser.DeleteSubKeyTree($@"{root}\StartupApproved", throwOnMissingSubKey: false);
            StartupManager.SetEnabledAt(runPath, approvedPath, valueName, enabled: false, executablePath: null);
            Equal(StartupStatus.NotRegistered, ReadStatus(runPath, approvedPath, valueName, executable));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }

        return Task.CompletedTask;
    }

    private static StartupStatus ReadStatus(
        string runPath,
        string approvedPath,
        string valueName,
        string? currentExecutable) =>
        StartupManager.ReadStateAt(runPath, approvedPath, valueName, currentExecutable).Status;

    /// <summary>
    /// Writes the approval flag the way the shell does: a leading byte whose low bit marks the
    /// veto, followed by the filetime stamped on when a person switches the entry off.
    /// </summary>
    private static void SetApproval(string approvedPath, string valueName, bool approved)
    {
        using var key = Registry.CurrentUser.CreateSubKey(approvedPath, writable: true);
        var flags = new byte[12];
        flags[0] = approved ? (byte)0x02 : (byte)0x03;
        if (!approved)
        {
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(flags, 4);
        }

        key.SetValue(valueName, flags, RegistryValueKind.Binary);
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
        False(SingleInstance.PostShow(expectedHandle, (_, _, _, _) => false));
        return Task.CompletedTask;
    }

    public static Task TestUpdateRestartRegistrationAsync()
    {
        True(ApplicationRestart.TryRegister());

        string? capturedCommandLine = null;
        var capturedFlags = 0;
        True(ApplicationRestart.TryRegister((commandLine, flags) =>
        {
            capturedCommandLine = commandLine;
            capturedFlags = flags;
            return 0;
        }));
        Equal(string.Empty, capturedCommandLine);
        Equal(ApplicationRestart.UpdateRestartFlags, capturedFlags);
        False(ApplicationRestart.TryRegister((_, _) => -1));
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

    private static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected no value, got '{value}'.");
        }
    }

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
