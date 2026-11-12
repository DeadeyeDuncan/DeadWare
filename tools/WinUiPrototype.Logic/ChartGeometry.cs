namespace WinUiPrototype.Logic;

public static class ChartGeometry
{
    /// <summary>
    /// The series as runs of points, split wherever a sample is missing. The newest sample sits at
    /// <paramref name="width"/> and older ones step left by width / (Capacity - 1).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(float X, float Y)>> Runs(SampleSeries series, float width, float height, double min, double max)
    {
        var runs = new List<IReadOnlyList<(float X, float Y)>>();
        if (!(width > 0) || !(height > 0)) return runs;
        float step = series.Capacity > 1 ? width / (series.Capacity - 1) : 0;
        bool flat = !(max > min);
        List<(float X, float Y)>? run = null;
        for (int i = 0; i < series.Count; i++)
        {
            if (series[i] is not { } value || double.IsNaN(value)) { run = null; continue; }
            float y = flat ? height / 2 : (float)Math.Clamp(height - (value - min) / (max - min) * height, 0, height);
            if (run is null) runs.Add(run = []);
            run.Add((width - (series.Count - 1 - i) * step, y));
        }
        return runs;
    }
}
