using System.Diagnostics;

namespace UsageAI.Services;

/// <summary>
/// One source a provider tried while reading usage, as <c>--diagnose</c> reports it. Both fields
/// come from a fixed vocabulary in UsageAI's own code — never from exception text, responses,
/// ports, tokens or paths — so the list is safe to paste into an issue.
/// </summary>
/// <param name="Source">The path tried, such as <c>agy-hub</c> or <c>gemini-cli</c>.</param>
/// <param name="Outcome">What happened, such as <c>answered</c>, <c>no-answer</c> or <c>sign-in-expired</c>.</param>
/// <param name="ElapsedMs">How long the attempt took.</param>
internal sealed record ProviderAttempt(string Source, string Outcome, long ElapsedMs);

/// <summary>
/// Collects <see cref="ProviderAttempt"/>s for <c>--diagnose</c>. Recording is off unless a
/// diagnostic run turned it on for its own async flow, so the tray app pays nothing for it.
/// </summary>
internal static class ProviderAttempts
{
    private const int MaxAttempts = 32;

    private static readonly AsyncLocal<List<ProviderAttempt>?> Current = new();

    /// <summary>Starts recording for the calling async flow and returns the list being filled.</summary>
    public static IReadOnlyList<ProviderAttempt> Begin()
    {
        var attempts = new List<ProviderAttempt>();
        Current.Value = attempts;
        return attempts;
    }

    /// <summary>Stops recording for the calling async flow.</summary>
    public static void End() => Current.Value = null;

    public static bool IsRecording => Current.Value is not null;

    /// <summary>Records an attempt; ignored unless a diagnostic run is recording.</summary>
    public static void Record(string source, string outcome, Stopwatch? timer = null)
    {
        var attempts = Current.Value;
        if (attempts is null || attempts.Count >= MaxAttempts)
        {
            return;
        }

        lock (attempts)
        {
            attempts.Add(new ProviderAttempt(
                IsVocabulary(source) ? source : "unknown",
                IsVocabulary(outcome) ? outcome : "unknown",
                timer?.ElapsedMilliseconds ?? 0));
        }
    }

    /// <summary>
    /// Times <paramref name="attempt"/> and records <c>answered</c> or <c>no-answer</c> by whether it
    /// produced a result; an exception is recorded as <c>failed</c> and rethrown.
    /// </summary>
    public static async Task<T?> TrackAsync<T>(string source, Func<Task<T?>> attempt)
        where T : class
    {
        if (!IsRecording)
        {
            return await attempt();
        }

        var timer = Stopwatch.StartNew();
        try
        {
            var result = await attempt();
            Record(source, result is null ? "no-answer" : "answered", timer);
            return result;
        }
        catch (OperationCanceledException)
        {
            Record(source, "cancelled", timer);
            throw;
        }
        catch
        {
            Record(source, "failed", timer);
            throw;
        }
    }

    /// <summary>Lower-case words joined by hyphens: the only shape a source or outcome may take.</summary>
    private static bool IsVocabulary(string value) =>
        value.Length is > 0 and <= 40 &&
        value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
