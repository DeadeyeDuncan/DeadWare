using System.Text.RegularExpressions;

namespace DeadWare.Core;

public static partial class LightingKeys
{
    [GeneratedRegex(@"#\d+$")] private static partial Regex ZoneSuffix();
    public static string Device(string serial,string location)=>$"{serial}|{location}";
    public static string Zone(string deviceKey,int zoneIndex)=>$"{deviceKey}#{zoneIndex}";
    // Split on the FIRST '|': a serial may contain anything, a greedier split would truncate the location.
    public static string LocationOf(string deviceKey){int i=deviceKey.IndexOf('|');return i<0?deviceKey:deviceKey[(i+1)..];}
    public static bool TrySplitZone(string zoneKey,out string deviceKey,out int zoneIndex)
    {
        deviceKey=zoneKey;zoneIndex=0;
        var m=ZoneSuffix().Match(zoneKey);
        if(!m.Success||!int.TryParse(m.Value.AsSpan(1),out zoneIndex)) return false;
        deviceKey=zoneKey[..m.Index];
        return true;
    }
    // Re-keys entries whose device lost/gained a serial at the same location. Conservative: refuses an absent or
    // ambiguous location, an already-stored target, and two stale keys racing for one target.
    public static bool Remap(LightingSettings settings,IReadOnlyCollection<string> liveDeviceKeys)
    {
        var live=new HashSet<string>(liveDeviceKeys);
        var byLocation=new Dictionary<string,string?>();
        foreach(var k in liveDeviceKeys){var loc=LocationOf(k);byLocation[loc]=byLocation.ContainsKey(loc)?null:k;}
        var stored=new HashSet<string>(settings.HardwareModes.Keys);
        foreach(var k in settings.ZoneColors.Keys) stored.Add(TrySplitZone(k,out var d,out _)?d:k);
        var renames=new Dictionary<string,string>();
        foreach(var s in stored)
        {
            if(live.Contains(s)) continue;
            if(!byLocation.TryGetValue(LocationOf(s),out var target)||target is null||target==s) continue;
            if(stored.Contains(target)) continue;
            renames[s]=target;
        }
        foreach(var g in renames.GroupBy(r=>r.Value).Where(g=>g.Count()>1).ToList()) foreach(var r in g) renames.Remove(r.Key);
        if(renames.Count==0) return false;
        string Rekey(string k)
        {
            if(TrySplitZone(k,out var d,out var i)) return renames.TryGetValue(d,out var to)?Zone(to,i):k;
            return renames.TryGetValue(k,out var t)?t:k;
        }
        settings.ZoneColors=settings.ZoneColors.ToDictionary(e=>Rekey(e.Key),e=>e.Value);
        settings.HardwareModes=settings.HardwareModes.ToDictionary(e=>Rekey(e.Key),e=>e.Value);
        return true;
    }
}
