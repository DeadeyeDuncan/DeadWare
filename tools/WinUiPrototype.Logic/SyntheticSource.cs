namespace WinUiPrototype.Logic;

/// <summary>A repeatable wandering value in 0..100; every 97th call is a missing reading.</summary>
public sealed class SyntheticSource(int seed)
{
    private readonly Random random = new(seed);
    private double value = 50;
    private int calls;

    public double? Next()
    {
        calls++;
        value = Math.Clamp(value + (random.NextDouble() - 0.5) * 12, 0, 100);
        return calls % 97 == 0 ? null : value;
    }
}
