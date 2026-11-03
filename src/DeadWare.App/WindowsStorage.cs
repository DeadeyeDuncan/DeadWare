using System.Diagnostics;
using System.Globalization;
using System.Management;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Per-poll storage fast path: drive enumeration plus throughput, activity and free space.</summary>
internal interface IStorageFastSource
{
    IReadOnlyList<StorageDrive> Drives(double now);
    void Read(IReadOnlyList<StorageDrive> drives,Dictionary<string,double?> values,out string? error);
}

internal sealed record WmiDisk(string? Serial,string Model,int Index,string BusType,long Size,bool Removable,IReadOnlyList<(string Letter,string Label,long Size,long Free)> Volumes);

/// <summary>WMI enumeration (refreshed every 60 s) and PhysicalDisk counters; never throws to the collector.</summary>
internal sealed class WindowsStorage : IStorageFastSource
{
    private const double EnumerationSeconds=60,ErrorAfterSeconds=60;
    private IReadOnlyList<StorageDrive> drives=[];
    private double? enumerated;
    private bool enumerationFailing;
    private double? failingSince;
    private string? lastError;
    private Dictionary<string,(PerformanceCounter Read,PerformanceCounter Write,PerformanceCounter Idle)>? counters;
    private readonly Stopwatch clock=Stopwatch.StartNew();

    /// <summary>"0 C: D:" → "0", "2" → "2", "_Total" → null.</summary>
    internal static string? InstanceDriveIndex(string instanceName)
    {
        var first=instanceName.Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null&&first.All(char.IsAsciiDigit)?first:null;
    }

    internal static IReadOnlyList<StorageDrive> Parse(IEnumerable<WmiDisk> disks)=>
        disks.OrderBy(d=>d.Index).Select(d=>new StorageDrive(StorageProjection.DriveId(d.Serial,d.Model,d.Index),StorageProjection.Normalize(d.Serial),d.Model,d.Index,d.BusType,d.Size,d.Removable,
            d.Volumes.Select(v=>new StorageVolume(v.Letter,v.Label,v.Size,v.Free)).ToList())).ToList();

    public IReadOnlyList<StorageDrive> Drives(double now)
    {
        if(enumerated is {} last&&now-last<EnumerationSeconds)return drives;
        try {drives=Parse(Enumerate());enumerated=now;enumerationFailing=false;DisposeCounters();}
        catch(Exception e) {enumerationFailing=true;lastError=$"Drive enumeration failed ({e.GetType().Name})";}
        return drives;
    }

    public void Read(IReadOnlyList<StorageDrive> list,Dictionary<string,double?> values,out string? error)
    {
        bool failed=enumerationFailing;
        foreach(var drive in list)
        {
            double? read=null,write=null,active=null;
            try
            {
                counters??=OpenCounters();
                if(counters.TryGetValue(drive.Index.ToString(CultureInfo.InvariantCulture),out var c))
                {
                    read=c.Read.NextValue();write=c.Write.NextValue();active=Math.Clamp(100-c.Idle.NextValue(),0,100);
                }
            }
            catch(Exception e) {failed=true;lastError=$"Disk counters unavailable ({e.GetType().Name})";read=write=active=null;DisposeCounters();}
            values[StorageMetrics.Key(drive.Id,"read")]=read;values[StorageMetrics.Key(drive.Id,"write")]=write;values[StorageMetrics.Key(drive.Id,"active")]=active;
            foreach(var volume in drive.Volumes)
            {
                double? free=null;
                try {free=new System.IO.DriveInfo(volume.Letter+":\\").TotalFreeSpace;} catch(Exception) {}
                values[StorageMetrics.VolumeKey(drive.Id,volume.Letter)]=free;
            }
        }
        double t=clock.Elapsed.TotalSeconds;
        if(!failed){failingSince=null;error=null;return;}
        failingSince??=t;
        error=t-failingSince.Value>=ErrorAfterSeconds?lastError:null;
    }

    private static Dictionary<string,(PerformanceCounter Read,PerformanceCounter Write,PerformanceCounter Idle)> OpenCounters()
    {
        var result=new Dictionary<string,(PerformanceCounter Read,PerformanceCounter Write,PerformanceCounter Idle)>(StringComparer.Ordinal);
        foreach(var instance in new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames())
        {
            // "_Total" has no index and is ignored; a letterless "2" still maps to drive index 2.
            if(InstanceDriveIndex(instance) is not {} index||result.ContainsKey(index))continue;
            var c=(Read:new PerformanceCounter("PhysicalDisk","Disk Read Bytes/sec",instance,true),Write:new PerformanceCounter("PhysicalDisk","Disk Write Bytes/sec",instance,true),Idle:new PerformanceCounter("PhysicalDisk","% Idle Time",instance,true));
            result[index]=c;
            c.Read.NextValue();c.Write.NextValue();c.Idle.NextValue();
        }
        return result;
    }

    private void DisposeCounters()
    {
        foreach(var c in counters?.Values??Enumerable.Empty<(PerformanceCounter Read,PerformanceCounter Write,PerformanceCounter Idle)>()) {c.Read.Dispose();c.Write.Dispose();c.Idle.Dispose();}
        counters=null;
    }

    private static List<WmiDisk> Enumerate()
    {
        var disks=new List<WmiDisk>();
        using var searcher=new ManagementObjectSearcher("SELECT DeviceID,Index,Model,SerialNumber,InterfaceType,MediaType,Size FROM Win32_DiskDrive");
        using var results=searcher.Get();
        foreach(ManagementObject disk in results)
        {
            using(disk)
            {
                var volumes=new List<(string Letter,string Label,long Size,long Free)>();
                foreach(ManagementObject partition in disk.GetRelated("Win32_DiskPartition"))
                {
                    using(partition)
                    foreach(ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                    {
                        using(logical)
                        {
                            var letter=(logical["DeviceID"] as string??"").TrimEnd(':');
                            if(letter.Length==0)continue;
                            volumes.Add((letter,logical["VolumeName"] as string??"",ToLong(logical["Size"]),ToLong(logical["FreeSpace"])));
                        }
                    }
                }
                string bus=disk["InterfaceType"] as string??"";
                string media=disk["MediaType"] as string??"";
                bool removable=bus.Equals("USB",StringComparison.OrdinalIgnoreCase)||media.Contains("Removable",StringComparison.OrdinalIgnoreCase)||media.Contains("External",StringComparison.OrdinalIgnoreCase);
                disks.Add(new(disk["SerialNumber"] as string,(disk["Model"] as string??"Disk").Trim(),Convert.ToInt32(disk["Index"]??0,CultureInfo.InvariantCulture),bus,ToLong(disk["Size"]),removable,
                    volumes.OrderBy(v=>v.Letter,StringComparer.Ordinal).ToList()));
            }
        }
        return disks;
    }

    private static long ToLong(object? value)=>value is null?0:Convert.ToInt64(value,CultureInfo.InvariantCulture);
}
