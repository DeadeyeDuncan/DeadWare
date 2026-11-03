namespace DeadWare.Core;

public sealed record StorageVolume(string Letter,string Label,long SizeBytes,long FreeBytes);

public sealed record StorageDrive(string Id,string? Serial,string Model,int Index,string BusType,long SizeBytes,bool Removable,IReadOnlyList<StorageVolume> Volumes)
{
    public string IdentityNote{get;init;}="";
    public bool Persistable=>Serial is not null&&!Removable;
}

public sealed record SmartSummary(bool Readable,bool CriticalWarning,double? LifePercent,double? Temperature,double? MaxTemperature,long Reallocated,long Pending,long Uncorrectable,double? PowerOnHours,double? UsedPercent,bool NeedsAdministrator);

public sealed record StorageHealth(string DriveId,string Badge,double? Temperature,double? LifePercent,double? UsedPercent,double? PowerOnHours,double? MaxTemperature,string? Unavailable);

public static class StorageMetrics
{
    public const int MaximumPersistedDrives=8;
    public static string Key(string id,string metric)=>$"storage/{id}/{metric}";
    public static string VolumeKey(string id,string letter)=>$"storage/{id}/volume/{letter}/free";
}
