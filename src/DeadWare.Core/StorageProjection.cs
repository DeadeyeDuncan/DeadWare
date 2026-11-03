namespace DeadWare.Core;

public static class StorageProjection
{
    public static string? Normalize(string? serial)
    {
        var t=serial?.Trim();
        return string.IsNullOrEmpty(t)?null:t;
    }

    public static string DriveId(string? serial,string model,int index)=>Normalize(serial)??$"{model}#{index}";

    public static IReadOnlyList<StorageDrive> Persisted(IReadOnlyList<StorageDrive> drives)
        =>drives.Where(d=>d.Persistable).OrderBy(d=>d.Index).Take(StorageMetrics.MaximumPersistedDrives).ToList();

    public static string Badge(SmartSummary s)
    {
        if(!s.Readable) return "Unknown";
        if(s.CriticalWarning||s.LifePercent<=10) return "Failing";
        if(s.Reallocated+s.Pending+s.Uncorrectable>0||s.LifePercent<=25||(s.Temperature is{} t&&s.MaxTemperature is{} max&&t>max)) return "Warning";
        return "OK";
    }

    public static StorageHealth Health(string driveId,SmartSummary? s,string? unavailable)
    {
        if(s is null) return new(driveId,"Unknown",null,null,null,null,null,unavailable);
        if(s.NeedsAdministrator) return new(driveId,"Unknown",null,null,null,null,null,"Unknown · needs administrator");
        return new(driveId,Badge(s),s.Temperature,s.LifePercent,s.UsedPercent,s.PowerOnHours,s.MaxTemperature,unavailable);
    }
}
