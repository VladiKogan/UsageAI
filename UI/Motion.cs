using System.Runtime.InteropServices;

namespace UsageAI.UI;

/// <summary>
/// Whether the app animates, and the one easing curve it animates with. Motion follows Windows'
/// "Animation effects" setting (Settings ▸ Accessibility ▸ Visual effects), and High Contrast
/// never animates: both ask for a still screen, and values then change in place.
/// </summary>
internal static class Motion
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] ref bool value, uint winIni);

    // Painting asks for every gauge on every frame, so Windows' setting is read at most once a
    // second rather than once per gauge per frame.
    private const long SettingLifetimeMs = 1000;
    private static bool _systemAnimations = true;
    private static long _systemAnimationsReadAt = long.MinValue;

    /// <summary>Lets tests decide instead of the machine's setting.</summary>
    internal static Func<bool>? EnabledOverride { get; set; }

    public static bool Enabled => EnabledOverride?.Invoke() ?? (!Theme.IsHighContrast && CachedSystemAnimations());

    /// <summary>
    /// cubic-bezier(0.23, 1, 0.32, 1): a quick start that settles gently, so a changed value
    /// lands where it is going rather than drifting there.
    /// </summary>
    public static double Ease(double progress)
    {
        if (progress <= 0)
        {
            return 0;
        }

        if (progress >= 1)
        {
            return 1;
        }

        const double X1 = 0.23, Y1 = 1, X2 = 0.32, Y2 = 1;
        // Solve x(t) = progress for the curve parameter, then read y(t). Bisection is exact
        // enough in 24 steps and cannot diverge the way Newton's method can near flat spots.
        double low = 0, high = 1, t = progress;
        for (var step = 0; step < 24; step++)
        {
            t = (low + high) / 2;
            if (Bezier(t, X1, X2) < progress)
            {
                low = t;
            }
            else
            {
                high = t;
            }
        }

        return Bezier(t, Y1, Y2);
    }

    private static double Bezier(double t, double p1, double p2)
    {
        var inverse = 1 - t;
        return 3 * inverse * inverse * t * p1 + 3 * inverse * t * t * p2 + t * t * t;
    }

    private static bool CachedSystemAnimations()
    {
        var now = Environment.TickCount64;
        if (now - _systemAnimationsReadAt >= SettingLifetimeMs)
        {
            _systemAnimations = SystemAnimationsOn();
            _systemAnimationsReadAt = now;
        }

        return _systemAnimations;
    }

    private static bool SystemAnimationsOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var enabled = true;
            return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0) || enabled;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }
}

/// <summary>
/// Eases each gauge from the value it showed to the value it should show. Controls ask for the
/// value to draw while painting, keyed by provider and limit, and the tracker repaints them until
/// they arrive. It outlives the controls — the popup rebuilds its tiles on every update — so a
/// rebuilt tile carries on from where the old one was. A key seen for the first time, and every
/// key while motion is off, draws its target at once.
/// </summary>
internal sealed class MotionTracker : IDisposable
{
    internal const int ValueDurationMs = 400;
    internal const int ColorDurationMs = 200;

    private readonly Dictionary<string, Tween<float>> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tween<Color>> _colors = new(StringComparer.Ordinal);
    private readonly Dictionary<Control, Rectangle> _animating = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly Func<long> _clock;
    private bool _disposed;

    public MotionTracker(Func<long>? clock = null)
    {
        _clock = clock ?? (static () => Environment.TickCount64);
        _timer.Tick += OnTick;
    }

    /// <summary>Whether any control is still being repainted toward a target.</summary>
    internal bool IsAnimating => _timer.Enabled;

    /// <summary>
    /// The value <paramref name="owner"/> should draw now for a gauge heading to
    /// <paramref name="target"/>. Only <paramref name="area"/> is repainted while it moves, so a
    /// bar easing in a long table does not redraw every row; an empty area repaints the owner.
    /// </summary>
    public float Value(Control owner, string key, float target, Rectangle area = default) =>
        Step(_values, owner, key, target, ValueDurationMs, area, static (from, to, amount) => from + (to - from) * (float)amount);

    /// <summary>The colour to draw now, crossfading when a limit moves between status colours.</summary>
    public Color Color(Control owner, string key, Color target, Rectangle area = default) =>
        Step(_colors, owner, key, target, ColorDurationMs, area, static (from, to, amount) => Theme.Blend(to, from, amount));

    /// <summary>
    /// Forgets every gauge, so each is drawn at its value the next time it appears. A window
    /// calls this when it hides: values that changed while nobody was looking are not news,
    /// and opening the window must not replay them.
    /// </summary>
    public void Reset()
    {
        _values.Clear();
        _colors.Clear();
        _animating.Clear();
        _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Tick -= OnTick;
        _timer.Dispose();
        _animating.Clear();
    }

    private T Step<T>(
        Dictionary<string, Tween<T>> tweens,
        Control owner,
        string key,
        T target,
        int duration,
        Rectangle area,
        Func<T, T, double, T> mix)
        where T : struct
    {
        var now = _clock();
        if (!Motion.Enabled || !tweens.TryGetValue(key, out var tween))
        {
            tweens[key] = new Tween<T>(target, target, now - duration);
            return target;
        }

        var progress = Math.Clamp((now - tween.Start) / (double)duration, 0, 1);
        var current = progress >= 1 ? tween.To : mix(tween.From, tween.To, Motion.Ease(progress));
        if (!EqualityComparer<T>.Default.Equals(tween.To, target))
        {
            // Retargeted mid-flight: carry on from what is on screen now, never jump back.
            tweens[key] = new Tween<T>(current, target, now);
            progress = 0;
        }

        if (progress < 1 && !_disposed)
        {
            // An empty rectangle stands for the whole control, and absorbs any area added to it.
            _animating[owner] = _animating.TryGetValue(owner, out var dirty)
                ? (dirty.IsEmpty || area.IsEmpty ? Rectangle.Empty : Rectangle.Union(dirty, area))
                : area;
            _timer.Start();
        }

        return current;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Each control re-registers from its own paint while it is still moving, so a control
        // that has arrived, or has been disposed, simply drops out.
        var owners = _animating.ToArray();
        _animating.Clear();
        foreach (var (owner, area) in owners)
        {
            if (owner.IsDisposed || owner.Disposing)
            {
                continue;
            }

            if (area.IsEmpty)
            {
                owner.Invalidate();
            }
            else
            {
                owner.Invalidate(area);
            }
        }

        if (owners.Length == 0)
        {
            _timer.Stop();
        }
    }

    private readonly record struct Tween<T>(T From, T To, long Start);
}
