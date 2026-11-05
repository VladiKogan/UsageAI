using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using UsageAI.Models;
using UsageAI.Services;
using UsageAI.UI;

namespace UsageAI.Tests;

/// <summary>
/// The in-app update from the user's side: the progress window, Windows closing the app for the
/// installer, and the note that lets the reopened copy say whether the update arrived.
/// </summary>
internal static class UpdateFlowTests
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const int EndSessionCloseApp = 0x1;

    /// <summary>Reports at once, unlike <see cref="Progress{T}"/>, which posts to a message loop.</summary>
    internal sealed class ImmediateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public static Task TestUpdateHandoffAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UsageAI.HandoffTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "update-handoff.json");
            var now = DateTimeOffset.UtcNow;
            var handoff = new UpdateHandoff("0.15.0", "0.15.1", now);

            // The new version running means the update arrived; the note is read only once.
            UpdateHandoffStore.Save(path, handoff);
            var installed = UpdateHandoffStore.Take(path, "0.15.1", now.AddMinutes(1));
            Equal(UpdateOutcome.Installed, installed!.Outcome);
            Equal("0.15.1", installed.Handoff.ToVersion);
            False(File.Exists(path));
            Null(UpdateHandoffStore.Take(path, "0.15.1", now));

            // The old version back means the installer never finished.
            UpdateHandoffStore.Save(path, handoff);
            Equal(UpdateOutcome.NotInstalled, UpdateHandoffStore.Take(path, "0.15.0", now)!.Outcome);

            // Build metadata does not make a different version.
            UpdateHandoffStore.Save(path, handoff);
            Equal(UpdateOutcome.Installed, UpdateHandoffStore.Take(path, "0.15.1+abc123", now)!.Outcome);

            // Another version, an expired note, a note from the future, and a damaged note say nothing.
            UpdateHandoffStore.Save(path, handoff);
            Null(UpdateHandoffStore.Take(path, "0.16.0", now));
            UpdateHandoffStore.Save(path, handoff);
            Null(UpdateHandoffStore.Take(path, "0.15.1", now + UpdateHandoffStore.Lifetime + TimeSpan.FromMinutes(1)));
            UpdateHandoffStore.Save(path, handoff with { StartedAt = now.AddMinutes(10) });
            Null(UpdateHandoffStore.Take(path, "0.15.1", now));
            UpdateHandoffStore.Save(path, handoff with { ToVersion = "not a version" });
            Null(UpdateHandoffStore.Take(path, "0.15.1", now));
            File.WriteAllText(path, "{ not json");
            Null(UpdateHandoffStore.Take(path, "0.15.1", now));
            False(File.Exists(path));

            // Downloaded installers go; anything else in the folder stays.
            var updates = Path.Combine(directory, "updates");
            Directory.CreateDirectory(updates);
            File.WriteAllText(Path.Combine(updates, "UsageAI-0.14.0-Setup-0a1b.exe"), "old installer");
            File.WriteAllText(Path.Combine(updates, "UsageAI-0.15.0-Setup-2c3d.exe.partial"), "interrupted download");
            File.WriteAllText(Path.Combine(updates, "UsageAI-0.15.0-Setup-notes.txt"), "not an installer");
            File.WriteAllText(Path.Combine(updates, "readme.txt"), "not ours");
            Equal(2, UpdateHandoffStore.CleanUpInstallers(updates));
            Equal(2, Directory.GetFiles(updates).Length);
            Equal(0, UpdateHandoffStore.CleanUpInstallers(Path.Combine(directory, "missing")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public static Task TestUpdateProgressWindowAsync()
    {
        using var window = new UpdateProgressForm(dpiOverride: 96);
        window.ShowDownloading("9.9.9", 0.5);
        Equal(UpdateStage.Downloading, window.Stage);
        Equal("Updating to UsageAI 9.9.9", window.Title);
        Equal("Downloading… 50%", window.Status);
        Null(window.PrimaryText);
        var halfway = window.Progress;

        window.ShowDownloading("9.9.9", 1);
        True(window.Progress > halfway);
        window.ShowStarting("9.9.9");
        Equal(UpdateStage.Starting, window.Stage);
        True(window.Progress > halfway);

        // A late download report must not take the window back a stage.
        window.ShowDownloading("9.9.9", 0.2);
        Equal(UpdateStage.Starting, window.Stage);

        window.ShowInstalling("9.9.9");
        Equal(UpdateFlowHandoff, window.Progress);
        Null(window.PrimaryText);
        var progressHeight = window.ClientSize.Height;

        window.ShowFailed("9.9.9", UsageApplicationContext.InstallerFailure(4));
        Equal(UpdateStage.Failed, window.Stage);
        Equal("The update to 9.9.9 didn't finish", window.Title);
        Equal("Release page", window.PrimaryText);
        True(window.Status.Contains("error 4", StringComparison.Ordinal));
        False(window.ClosesItself);
        // A finished window has room for its buttons.
        True(window.ClientSize.Height > progressHeight);

        using var reopened = new UpdateProgressForm(dpiOverride: 96);
        reopened.ShowInstalled("9.9.9", offerWhatsNew: true);
        Equal(UpdateStage.Installed, reopened.Stage);
        Equal("UsageAI updated", reopened.Title);
        Equal(1F, reopened.Progress);
        Equal("What's new", reopened.PrimaryText);
        True(reopened.ClosesItself);
        reopened.ShowInstalled("9.9.9", offerWhatsNew: false);
        Null(reopened.PrimaryText);

        // Drawn at twice the DPI, everything doubles.
        using var large = new UpdateProgressForm(dpiOverride: 192);
        large.ShowInstalling("9.9.9");
        True(large.ClientSize.Width == window.ClientSize.Width * 2);
        using (var bitmap = new Bitmap(reopened.ClientSize.Width, reopened.ClientSize.Height))
        {
            reopened.DrawToBitmap(bitmap, reopened.ClientRectangle);
        }

        Equal("The installer was cancelled. This version is still installed.", UsageApplicationContext.InstallerFailure(2));
        Equal("The installer was cancelled. This version is still installed.", UsageApplicationContext.InstallerFailure(5));
        Equal("The installer could not prepare the update. This version is still installed.", UsageApplicationContext.InstallerFailure(7));

        // The corner away from the taskbar's far end: bottom right, top, and left taskbars.
        var size = new Size(360, 90);
        var screen = new Rectangle(0, 0, 1920, 1080);
        Equal(new Point(1548, 938), UpdateProgressForm.CornerFor(screen, new Rectangle(0, 0, 1920, 1040), size, 12));
        Equal(new Point(1548, 52), UpdateProgressForm.CornerFor(screen, new Rectangle(0, 40, 1920, 1040), size, 12));
        Equal(new Point(72, 978), UpdateProgressForm.CornerFor(screen, new Rectangle(60, 0, 1860, 1080), size, 12));
        return Task.CompletedTask;
    }

    public static Task TestEndSessionExitsAsync()
    {
        using var context = NewContext();
        var exited = false;
        context.ThreadExit += (_, _) => exited = true;
        var handle = MessageWindowHandle(context);

        // Agreeing to close is the default; a session end that was called off changes nothing.
        Equal(1, (int)SendMessage(handle, WmQueryEndSession, IntPtr.Zero, EndSessionCloseApp));
        SendMessage(handle, WmEndSession, IntPtr.Zero, EndSessionCloseApp);
        PumpFor(TimeSpan.FromMilliseconds(100));
        False(exited);

        // Restart Manager's close exits the app as the tray menu's Exit does, without waiting
        // out its 30-second timeout.
        SendMessage(handle, WmEndSession, new IntPtr(1), EndSessionCloseApp);
        PumpUntil(() => exited, "the app to exit");
        return Task.CompletedTask;
    }

    public static Task TestEndSessionExitsFromModalAsync()
    {
        // Settings is modal, and an update started from it is still open when Windows closes the
        // app; the close must end the whole message loop, not only the dialog.
        using var context = NewContext();
        var handle = MessageWindowHandle(context);
        var dialogShown = false;
        var timedOut = false;
        using var open = new System.Windows.Forms.Timer { Interval = 20 };
        using var watchdog = new System.Windows.Forms.Timer { Interval = 10_000 };
        open.Tick += (_, _) =>
        {
            open.Stop();
            using var dialog = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10_000, -10_000),
            };
            dialog.Shown += (_, _) =>
            {
                dialogShown = true;
                SendMessage(handle, WmQueryEndSession, IntPtr.Zero, EndSessionCloseApp);
                SendMessage(handle, WmEndSession, new IntPtr(1), EndSessionCloseApp);
            };
            dialog.ShowDialog();
        };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            timedOut = true;
            foreach (var form in Application.OpenForms.Cast<Form>().ToArray())
            {
                form.Close();
            }

            Application.ExitThread();
        };

        open.Start();
        watchdog.Start();
        Application.Run(context);
        True(dialogShown);
        False(timedOut);
        return Task.CompletedTask;
    }

    public static Task TestInstallUpdateFlowAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UsageAI.InstallFlowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var installerPath = Path.Combine(directory, "UsageAI-9.9.9-Setup-test.exe");
        File.WriteAllText(installerPath, "synthetic installer fixture");
        var release = new UpdateRelease(
            "v9.9.9",
            "9.9.9",
            new Uri("https://github.com/VladiKogan/UsageAI/releases/tag/v9.9.9"),
            new UpdateAsset("UsageAI-9.9.9-Setup.exe", new Uri("https://github.com/VladiKogan/UsageAI/releases/download/v9.9.9/UsageAI-9.9.9-Setup.exe"), 27),
            new UpdateAsset("UsageAI-9.9.9-Setup.exe.sha256", new Uri("https://github.com/VladiKogan/UsageAI/releases/download/v9.9.9/UsageAI-9.9.9-Setup.exe.sha256"), 90));
        var stages = new List<UpdateStage>();
        string? launched = null;
        string? noteAtLaunch = null;
        var exitCode = 1;
        UsageApplicationContext? current = null;

        async Task<string> Download(UpdateRelease _, IProgress<double>? progress, CancellationToken __)
        {
            progress?.Report(0.5);
            await Task.Yield();
            progress?.Report(1);
            await Task.Yield();
            stages.Add(current!.UpdateWindow!.Stage);
            return installerPath;
        }

        Process Launch(string path)
        {
            launched = path;
            stages.Add(current!.UpdateWindow!.Stage);
            noteAtLaunch = File.ReadAllText(AppPaths.UpdateHandoffFile);
            // A real child that exits with the code under test, standing in for Setup.
            return Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "where.exe"),
                exitCode == 0 ? "/Q cmd.exe" : "/Q usageai-no-such-file.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
        }

        try
        {
            using (var context = NewContext(downloadInstaller: Download, launchInstaller: Launch))
            {
                current = context;

                // Setup failing while this copy still runs: the window says so, the note goes.
                Run(context.InstallUpdateAsync(release));
                Equal(installerPath, launched);
                Equal(UpdateStage.Downloading, stages[0]);
                Equal(UpdateStage.Starting, stages[1]);
                True(noteAtLaunch!.Contains("\"ToVersion\":\"9.9.9\"", StringComparison.Ordinal));
                True(noteAtLaunch.Contains($"\"FromVersion\":\"{AppIdentity.Version}\"", StringComparison.Ordinal));
                Equal(UpdateStage.Failed, context.UpdateWindow!.Stage);
                True(context.UpdateWindow.Status.Contains("error 1", StringComparison.Ordinal));
                False(File.Exists(AppPaths.UpdateHandoffFile));

                // Setup succeeding without replacing this copy: installed, but not this copy.
                exitCode = 0;
                Run(context.InstallUpdateAsync(release));
                Equal(UpdateStage.InstalledNotRunning, context.UpdateWindow!.Stage);
                // This copy saw the outcome itself, so it must not report it again after a restart.
                False(File.Exists(AppPaths.UpdateHandoffFile));

                // A cancelled elevation prompt is said in the panel, and leaves no note behind.
                var cancel = true;
                current = null;
                using (var cancelling = NewContext(
                    downloadInstaller: (_, _, _) => Task.FromResult(installerPath),
                    launchInstaller: _ => cancel ? throw new UpdateInstallException("The update installation was cancelled.") : null!))
                {
                    Run(cancelling.InstallUpdateAsync(release));
                    Equal(UpdateStage.Failed, cancelling.UpdateWindow!.Stage);
                    True(cancelling.UpdateWindow.Status.StartsWith("The update installation was cancelled.", StringComparison.Ordinal));
                    Equal("Release page", cancelling.UpdateWindow.PrimaryText);
                    False(File.Exists(AppPaths.UpdateHandoffFile));
                    cancelling.UpdateWindow.Close();
                    PumpUntil(() => cancelling.UpdateWindow is null, "the window to close");
                }

                var window = context.UpdateWindow;
                window.Close();
                PumpUntil(() => context.UpdateWindow is null, "the window to close");
            }

            // The reopened copy finishes the bar and says the update arrived.
            var result = new UpdateHandoffResult(
                UpdateOutcome.Installed,
                new UpdateHandoff("0.15.0", AppIdentity.Version, DateTimeOffset.UtcNow));
            using (var reopened = NewContext(updateResult: result))
            {
                ShowUpdateResult(reopened);
                Equal(UpdateStage.Installed, reopened.UpdateWindow!.Stage);
                True(reopened.UpdateWindow.ClosesItself);
                reopened.UpdateWindow.Close();
                PumpUntil(() => reopened.UpdateWindow is null, "the window to close");

                // Shown once only.
                ShowUpdateResult(reopened);
                Null(reopened.UpdateWindow);
            }

            var failed = new UpdateHandoffResult(
                UpdateOutcome.NotInstalled,
                new UpdateHandoff(AppIdentity.Version, "9.9.9", DateTimeOffset.UtcNow));
            using (var oldCopy = NewContext(updateResult: failed))
            {
                ShowUpdateResult(oldCopy);
                Equal(UpdateStage.Failed, oldCopy.UpdateWindow!.Stage);
                True(oldCopy.UpdateWindow.Status.Contains($"UsageAI {AppIdentity.Version} is still installed", StringComparison.Ordinal));
                oldCopy.UpdateWindow.Close();
                PumpUntil(() => oldCopy.UpdateWindow is null, "the window to close");
            }
        }
        finally
        {
            UpdateHandoffStore.Delete();
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private const float UpdateFlowHandoff = UpdateProgressForm.HandoffProgress;

    private static UsageApplicationContext NewContext(
        UpdateHandoffResult? updateResult = null,
        Func<UpdateRelease, IProgress<double>?, CancellationToken, Task<string>>? downloadInstaller = null,
        Func<string, Process>? launchInstaller = null) =>
        new(
            new IUsageClient[] { new IdleClient() },
            new AppSettings { GlobalHotkeyEnabled = false },
            showTrayIcon: false,
            answerRelaunch: false,
            updateResult: updateResult,
            downloadInstaller: downloadInstaller,
            launchInstaller: launchInstaller);

    /// <summary>A provider that is never polled: these checks never let the app refresh.</summary>
    private sealed class IdleClient : IUsageClient
    {
        public string Id => "codex";

        public string DisplayName => "Codex";

        public string SignInCommand => "codex";

        public Uri AccountUrl => new("https://example.com/codex");

        public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The update checks never refresh providers.");
    }

    private static void ShowUpdateResult(UsageApplicationContext context) =>
        typeof(UsageApplicationContext)
            .GetMethod("ShowUpdateResult", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(context, null);

    private static IntPtr MessageWindowHandle(UsageApplicationContext context) =>
        ((MessageWindow)typeof(UsageApplicationContext)
            .GetField("_messageWindow", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(context)!).Handle;

    /// <summary>Waits for a task whose continuations run on this thread's message loop.</summary>
    private static void Run(Task task)
    {
        PumpUntil(() => task.IsCompleted, "the update flow");
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void PumpFor(TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, int lParam) =>
        SendMessage(window, message, wParam, new IntPtr(lParam));

    private static void True(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Expected true: {expression}");
        }
    }

    private static void False(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null) =>
        True(!condition, $"not ({expression})");

    private static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, got {value}.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}
