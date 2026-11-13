using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UsageAI.Services;

/// <summary>
/// The project's own pages, which About and What's New link to, and the one way they are
/// opened. Nothing is fetched: each link only opens the browser, and the GitHub star itself is
/// given on the page, by the person, signed in there.
/// </summary>
internal static class ProjectLinks
{
    /// <summary>The repository page, where a star is given.</summary>
    internal static readonly Uri Repository = new("https://github.com/VladiKogan/UsageAI");

    /// <summary>
    /// A new issue with the body started: the UsageAI and Windows versions, two headings to fill
    /// in, and a warning before pasting diagnostics. Nothing about providers, accounts or paths is
    /// added — the person writes and sends it themselves.
    /// </summary>
    internal static Uri NewIssue(string appVersion, string windows)
    {
        var body =
            $"**UsageAI version:** {appVersion}\n" +
            $"**Windows:** {windows}\n\n" +
            "### What happened\n\n\n" +
            "### What you expected\n\n\n" +
            "> If you attach `UsageAI.exe --diagnose <provider>` output, check it first: it can include your account email.\n";
        return new Uri($"{Repository}/issues/new?body={Uri.EscapeDataString(body)}");
    }

    /// <summary>The Windows version as the issue states it, such as "Microsoft Windows 10.0.26100 (X64)".</summary>
    internal static string WindowsVersion =>
        $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// Opens a link in the default browser. Replaceable, so checks record the link instead of
    /// opening a browser.
    /// </summary>
    internal static Action<Uri> Open { get; set; } = OpenInBrowser;

    private static void OpenInBrowser(Uri link)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser answered; there is nothing further to do from here.
        }
    }
}
