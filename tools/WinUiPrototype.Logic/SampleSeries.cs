namespace WinUiPrototype.Logic;

/// <summary>The newest <paramref name="capacity"/> samples; a null sample is a missing reading.</summary>
public sealed class SampleSeries(int capacity)
{
    private readonly double?[] items = new double?[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private int start;

    public int Capacity => items.Length;
    public int Count { get; private set; }

    /// <summary>Index 0 is the oldest kept sample.</summary>
    public double? this[int index] =>
        (uint)index < (uint)Count ? items[(start + index) % items.Length] : throw new ArgumentOutOfRangeException(nameof(index));

    public double? Latest => Count == 0 ? null : this[Count - 1];

    public void Add(double? value)
    {
        if (Count < items.Length) { items[(start + Count) % items.Length] = value; Count++; }
        else { items[start] = value; start = (start + 1) % items.Length; }
    }

    /// <summary>Tops the series up to capacity, so a chart starts with a full window as a running app would have.</summary>
    public void Fill(SyntheticSource source)
    {
        while (Count < Capacity) Add(source.Next());
    }
}
