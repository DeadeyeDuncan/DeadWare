namespace DeadWare.Core;

public static class HubChecks
{
    public const int StalledRounds=4;
    // null: the check passes. The pump parts of before first, then its fans, each in channel order; the first failure is returned.
    public static HubStop? Compare(HubReadings before,HubReadings after)
    {
        foreach(var kind in new[]{HubPartKinds.Pump,HubPartKinds.Fan})
        {
            int number=0;
            foreach(var part in before.Parts.Where(p=>p.Kind==kind).OrderBy(p=>p.Channel))
            {
                number++;
                if(part.Rpm is not{} was) continue;
                var now=after.Parts.FirstOrDefault(p=>p.Channel==part.Channel)?.Rpm;
                if(now is null) return new HubStop{Kind=HubStops.NoReading,Part=kind,Number=number,Before=was,After=null};
                if((long)now.Value*10<(long)was*9) return new HubStop{Kind=HubStops.Slower,Part=kind,Number=number,Before=was,After=now};
            }
        }
        return null;
    }
    // A pump part reads 0 RPM or has no reading; fans never count.
    public static bool PumpStalled(HubReadings r)=>r.Parts.Any(p=>p.Kind==HubPartKinds.Pump&&(p.Rpm is null||p.Rpm==0));
    public static bool Stale(DateTimeOffset readAt,DateTimeOffset now)=>now-readAt>TimeSpan.FromMinutes(2);
}
