namespace DeadWare.Core;

/// <summary>
/// How often the connections map draws a frame while its arcs move. A map in use runs at about 30 frames a second, the rate the
/// motion was designed at; one nobody has touched for a minute drops to about 15, and after five minutes to about 10, because a map
/// left open all day (the desktop widget) pays for every frame whether or not anyone is watching. Any input on the map restores the
/// full rate at once. Motion always advances by the real time elapsed, so a slower rate shows the same speeds in fewer steps.
/// </summary>
public static class MapAnimation
{
    public const double IdleSeconds = 60, LongIdleSeconds = 300;

    /// <summary>The most one frame advances the motion, so a late frame or a stalled thread never makes the particles jump.</summary>
    public const double MaximumStepSeconds = .25;

    // Windows wakes timers on its clock tick, 15.6 ms unless a finer one was asked for, so each interval sits just under a whole
    // number of ticks: exactly 1/30 s can wait for the third tick (47 ms, 21 frames a second), while 31 ms is met on the second
    // (31.25 ms, 32 frames a second). With a finer tick the rates come out at about 32, 16 and 11.
    private static readonly TimeSpan Full = TimeSpan.FromMilliseconds(31), Idle = TimeSpan.FromMilliseconds(62), LongIdle = TimeSpan.FromMilliseconds(93);

    /// <summary>The time between frames for a map that has had no input for <paramref name="idleSeconds"/> seconds.</summary>
    public static TimeSpan Interval(double idleSeconds) =>
        !(idleSeconds >= IdleSeconds) ? Full : idleSeconds < LongIdleSeconds ? Idle : LongIdle;

    /// <summary>How far the motion advances for <paramref name="elapsed"/> seconds since the last frame: never back, never more than
    /// <see cref="MaximumStepSeconds"/>.</summary>
    public static double Step(double elapsed) => double.IsFinite(elapsed) ? Math.Clamp(elapsed, 0, MaximumStepSeconds) : 0;
}
