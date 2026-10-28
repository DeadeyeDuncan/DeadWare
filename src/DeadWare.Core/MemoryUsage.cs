namespace DeadWare.Core;

public static class MemoryUsage
{
    public static double? Percentage(double? used, double? total)
    {
        if (used is not double usedBytes || total is not double totalBytes ||
            !double.IsFinite(usedBytes) || !double.IsFinite(totalBytes) ||
            usedBytes < 0 || totalBytes <= 0 || usedBytes > totalBytes) return null;
        return usedBytes / totalBytes * 100;
    }
}
