using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UsageAI.Services;

/// <summary>
/// The project's own pages, which About and What's New link to. Nothing is fetched: each link
/// only opens the browser, and the GitHub star itself is given on the page, by the person, signed
/// in there.
/// </summary>
internal static class ProjectLinks
{
    /// <summary>The repository page, where a star is given.</summary>
    internal static readonly Uri Repository = new("https://github.com/VladiKogan/UsageAI");

    /// <summary>The full changelog, for What's New's "View full changelog".</summary>
    internal static readonly Uri Changelog = new("https://github.com/VladiKogan/UsageAI/blob/main/Changelog.md");

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
}

/// <summary>
/// The one way the app opens a web page in the default browser — provider pages, sign-in links,
/// release notes, the project's links — so every caller handles a browser that does not answer
/// the same way, and checks can record a link instead of opening one.
/// </summary>
internal static class Browser
{
    /// <summary>Opens a link and says whether Windows took it. Replaceable for checks.</summary>
    internal static Func<Uri, bool> Opener { get; set; } = OpenInDefaultBrowser;

    /// <summary>Opens <paramref name="link"/>; false when no browser answered, for the caller to say so.</summary>
    internal static bool Open(Uri link) => Opener(link);

    private static bool OpenInDefaultBrowser(Uri link)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
