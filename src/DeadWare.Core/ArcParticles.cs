namespace DeadWare.Core;

/// <summary>Where the moving dots sit along a connection arc. Pure placement; the arc itself is unchanged.</summary>
public static class ArcParticles
{
    /// <summary><paramref name="count"/> fractions spaced 1/count apart, advancing with <c>time * speed</c>;
    /// reversed (1 - f) when the traffic flows toward the origin.</summary>
    public static IReadOnlyList<double> Fractions(double time, double speed, int count, bool towardOrigin)
    {
        if (count <= 0 || !double.IsFinite(time) || !double.IsFinite(speed)) return [];
        var result = new double[count];
        double phase = time * speed; phase -= Math.Floor(phase);
        for (int i = 0; i < count; i++)
        {
            double fraction = (double)i / count + phase; fraction -= Math.Floor(fraction);
            result[i] = towardOrigin ? 1 - fraction : fraction;
        }
        return result;
    }

    /// <summary>0 for no activity, then 1, 2 or 3 dots as the level rises through a third and two thirds.</summary>
    public static int Count(double level) => !double.IsFinite(level) || level <= 0 ? 0 : level < .34 ? 1 : level < .67 ? 2 : 3;
}
