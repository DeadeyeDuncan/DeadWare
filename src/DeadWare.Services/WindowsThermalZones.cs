using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>
/// Windows' ACPI thermal zones, read from the "Thermal Zone Information" counters: the CPU temperature fallback for when the
/// hardware library has none. They need neither administrator rights nor a driver, so they work without PawnIO and on ARM.
/// One PDH query holds both wildcard counters, so a read is one snapshot of every zone. One caller at a time; never throws.
/// </summary>
internal sealed class WindowsThermalZones
{
    // English paths, so the counters are found in any display language. High Precision Temperature (tenths of a kelvin) is
    // the newer counter; Temperature is in whole kelvins.
    internal const string PrecisePath=@"\Thermal Zone Information(*)\High Precision Temperature",KelvinPath=@"\Thermal Zone Information(*)\Temperature";
    internal const string Hardware="Windows thermal zones",HardwareId="/windows/thermalzones",HardwareType="ThermalZone";
    /// <summary>The fewest seconds between opens of the query. Machines without zones (most desktops) so pay one read a minute, not one a poll.</summary>
    internal const double RetrySeconds=60;
    private const uint FormatDouble=0x00000200,NoScale=0x00001000,MoreData=0x800007D2;
    // x64 PDH_FMT_COUNTERVALUE_ITEM_W, read as PdhGpuApi reads it: the name pointer, then CStatus and the 8-byte-aligned value.
    private const int ItemBytes=24,StatusOffset=8,ValueOffset=16;
    private IntPtr query,precise,kelvin,buffer;
    private uint bufferBytes;
    private long openAfter;

    /// <summary>Every zone's temperature in °C (NaN where a zone reports none), or an empty list: no counters, no zones or a failed read.</summary>
    public IReadOnlyList<(string Name,double Celsius)> Read(Dictionary<string,double>? timings=null)
    {
        long started=Stopwatch.GetTimestamp();
        try
        {
            if(query==IntPtr.Zero)
            {
                if(started<openAfter)return [];
                openAfter=started+(long)(RetrySeconds*Stopwatch.Frequency);
                if(!Open())return [];
            }
            if(Snapshot() is {Count:>0} zones)return zones;
            // No zones, or a failed read: closed, and opened again on a later poll, at most once a minute.
            Close();return [];
        }
        catch(Exception) {Close();return [];}
        finally {if(timings is not null)timings["windows/thermal"]=Stopwatch.GetElapsedTime(started).TotalMilliseconds;}
    }

    /// <summary>Idempotent: closes the query and frees the buffer; a later read opens it again.</summary>
    public void Close()
    {
        if(query!=IntPtr.Zero)PdhCloseQuery(query);
        query=precise=kelvin=IntPtr.Zero;
        Marshal.FreeHGlobal(buffer);
        buffer=IntPtr.Zero;bufferBytes=0;
    }

    /// <summary>One Sensors-page row per zone. Ids start with /windows/, which no LibreHardwareMonitor identifier does.</summary>
    internal static IEnumerable<SensorReading> Sensors(IReadOnlyList<(string Name,double Celsius)> zones)=>zones.Select(zone=>
    {
        bool plausible=ThermalZones.IsPlausible(zone.Celsius);
        // An implausible reading keeps its value in the validity text, so a diagnostic report shows what the zone said.
        string validity=plausible?"Available":double.IsFinite(zone.Celsius)?$"Implausible reading ({zone.Celsius.ToString("0.0",CultureInfo.InvariantCulture)} °C)":"Missing or invalid provider value";
        return new SensorReading(HardwareId+"/"+zone.Name,Hardware,zone.Name,"°C",plausible?zone.Celsius:null,HardwareId,HardwareType,"Temperature",validity);
    });

    private bool Open()
    {
        if(PdhOpenQueryW(null,IntPtr.Zero,out query)!=0){query=IntPtr.Zero;return false;}
        // Either counter may be missing (High Precision Temperature on older Windows); with neither there is nothing to read.
        if(PdhAddEnglishCounterW(query,PrecisePath,IntPtr.Zero,out precise)!=0)precise=IntPtr.Zero;
        if(PdhAddEnglishCounterW(query,KelvinPath,IntPtr.Zero,out kelvin)!=0)kelvin=IntPtr.Zero;
        if(precise!=IntPtr.Zero||kelvin!=IntPtr.Zero)return true;
        Close();return false;
    }

    /// <summary>One collection of both counters, merged by zone in PDH's order. Null when the read fails, as on a machine without zones.</summary>
    private List<(string Name,double Celsius)>? Snapshot()
    {
        if(PdhCollectQueryData(query)!=0)return null;
        var tenths=Values(precise);var whole=Values(kelvin);
        if(tenths is null&&whole is null)return null;
        var zones=new List<(string Name,double Celsius)>();
        var index=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var (name,value) in tenths??[])
            if(index.TryAdd(name,zones.Count))zones.Add((name,Celsius(value/10)));
        // Temperature fills in for a zone without a plausible High Precision Temperature.
        foreach(var (name,value) in whole??[])
        {
            if(!index.TryGetValue(name,out int i)){index[name]=zones.Count;zones.Add((name,Celsius(value)));}
            else if(!ThermalZones.IsPlausible(zones[i].Celsius)&&ThermalZones.IsPlausible(Celsius(value)))zones[i]=(name,Celsius(value));
        }
        return zones;
    }

    private static double Celsius(double? kelvins)=>kelvins is double k&&k>0?Math.Round(k-273.15,2):double.NaN;

    /// <summary>A wildcard counter's value per zone in PDH's order, null where a zone has none; null when the read fails.</summary>
    private List<(string Name,double? Value)>? Values(IntPtr counter)
    {
        if(counter==IntPtr.Zero)return null;
        // As in PdhGpuApi: the size a size-0 call returns is trusted; after a too-small buffer the returned size is not.
        uint size=bufferBytes;
        for(var call=1;;call++)
        {
            uint bytes=size;
            uint status=PdhGetFormattedCounterArrayW(counter,FormatDouble|NoScale,ref bytes,out uint count,size==0?IntPtr.Zero:buffer);
            if(status==MoreData)
            {
                if(size==0&&bytes==0)return [];
                if(call==3)return null;
                if(size!=0){size=0;continue;}
                // Zeroed before allocating, so a failed allocation leaves nothing for Close to free twice.
                Marshal.FreeHGlobal(buffer);buffer=IntPtr.Zero;bufferBytes=0;
                buffer=Marshal.AllocHGlobal((nint)bytes);bufferBytes=size=bytes;
                continue;
            }
            if(status!=0)return null;
            var values=new List<(string,double?)>((int)count);
            for(var n=0;n<count;n++)
            {
                var item=buffer+n*ItemBytes;
                // CStatus 0 and 1 are valid and new data; any other status means no reading for that zone.
                bool valid=(uint)Marshal.ReadInt32(item,StatusOffset)<=1;
                values.Add((Marshal.PtrToStringUni(Marshal.ReadIntPtr(item))??"",valid?BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item,ValueOffset)):null));
            }
            return values;
        }
    }

    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhOpenQueryW(string? source,IntPtr user,out IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhAddEnglishCounterW(IntPtr query,string path,IntPtr user,out IntPtr counter);
    [DllImport("pdh.dll",ExactSpelling=true)] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter,uint format,ref uint bytes,out uint count,IntPtr buffer);
    [DllImport("pdh.dll",ExactSpelling=true)] private static extern uint PdhCloseQuery(IntPtr query);
}
