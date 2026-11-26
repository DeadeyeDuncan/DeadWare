using DeadWare.Core;

namespace DeadWare.App;

/// <summary>
/// The hub's readings as Sensors rows and the coolant graph value. The rows carry the hardware type "CorsairLink", which
/// <see cref="CpuPumpSpeed.Select"/> never accepts, so the CPU pump line and Cooling Health keep the sensor they use today.
/// </summary>
public static class HubSensors
{
    public const string Hardware="Corsair iCUE LINK hub",HardwareId="/corsairlink/hub",HardwareType="CorsairLink",CoolantKey="cpu/coolant";
    /// <summary>A reading older than this gives no rows and no coolant value.</summary>
    public static readonly TimeSpan MaxAge=TimeSpan.FromSeconds(10);

    /// <summary>One row per reading: coolant and pump for each pump part, then the fans in channel order. Nothing for no readings or an old one.</summary>
    public static IReadOnlyList<SensorReading> Sensors(HubReadings? readings,TimeSpan age,bool radiatorNames)
    {
        if(readings is null||age>MaxAge)return [];
        var pumps=readings.Parts.Where(p=>p.Kind==HubPartKinds.Pump).OrderBy(p=>p.Channel).ToArray();
        var fans=readings.Parts.Where(p=>p.Kind==HubPartKinds.Fan).OrderBy(p=>p.Channel).ToArray();
        var rows=new List<SensorReading>();
        SensorReading Row(string id,string name,string unit,double? value,string type)=>new(HardwareId+"/"+id,Hardware,name,unit,value,HardwareId,HardwareType,type,"Available");
        foreach(var part in pumps)
            rows.Add(Row($"{part.Channel}/coolant",pumps.Length>1?"Coolant · "+part.Name:"Coolant","°C",part.Celsius,"Temperature"));
        foreach(var part in pumps)
            rows.Add(Row($"{part.Channel}/pump",pumps.Length>1?"Pump · "+part.Name:"Pump","RPM",part.Rpm,"Fan"));
        for(int i=0;i<fans.Length;i++)
            rows.Add(Row($"{fans[i].Channel}/fan",(radiatorNames?"Radiator fan ":"Fan ")+(i+1),"RPM",fans[i].Rpm,"Fan"));
        return rows;
    }

    /// <summary>The first coolant reading, or null with no readings, an old one, or no coolant temperature in it.</summary>
    public static double? Coolant(HubReadings? readings,TimeSpan age)
    {
        if(readings is null||age>MaxAge)return null;
        foreach(var part in readings.Parts.Where(p=>p.Kind==HubPartKinds.Pump).OrderBy(p=>p.Channel))
            if(part.Celsius is {} c&&double.IsFinite(c))return c;
        return null;
    }
}
