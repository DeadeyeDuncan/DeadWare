using System.Globalization;
namespace DeadWare.Core;

// Every owner-facing text of the Corsair hub, and the rules that pick between them.
public static class HubTexts
{
    public const string Heading="Corsair iCUE LINK hub",SwitchLabel="DeadWare drives the hub",
        About="When this is on, DeadWare lights the hub itself and reads its coolant temperature and its fan and pump speeds. DeadWare never sets fan or pump speeds.",
        OffLine="Off. The hub's lights go through OpenRGB, as they do today.",SwitchingLine="Switching… the lights blink once.",
        LetGoLine="On, but DeadWare has let go for now.",
        TakesLine="On. DeadWare takes the hub in a moment.",
        GaveBackLine="Off. The hub is on its own.",GaveBackTitle="DeadWare gave the hub back.",
        GaveBackTail="The hub is now on its own built-in lights and fan settings, and stays that way until you choose.",
        BackButton="Back to OpenRGB, as before",RetryButton="Try the take-over again",
        NotBack="The hub has not come back through OpenRGB yet. Click the switch when its lights are back.",
        DialogTitle="Let DeadWare drive the Corsair hub?",DialogAsks="This changes which program is in charge of a cooling device, so DeadWare asks first.",
        DialogBlink="OpenRGB restarts without the hub, so all lights blink once. DeadWare then takes the hub and lights it.",
        DialogBoxHead="Read from the hub just now",
        DialogNoReading="DeadWare could not read the hub just now, so it cannot compare speeds afterwards. It will still give the hub back if the pump stops.",
        DialogCheck="After taking over, DeadWare reads the speeds again. If the pump or a fan runs more than 10% slower than above, DeadWare gives the hub back to its own built-in settings and tells you.",
        Never="DeadWare never sets fan or pump speeds.",TakeOver="Take over the hub",Cancel="Cancel",Coolant="Coolant",Pump="Pump",ReadingsHead="Readings",
        ReadingsNote="Read from the hub every few seconds. DeadWare shows these speeds; it never sets them.",
        ReadingsOff="Readings appear here once DeadWare drives the hub. Switch that on in Settings → Lighting.",
        ReadingsOwn="No readings while the hub is on its own.",OwnCaption="· own effect",
        CoolantOption="Show coolant temperature on CPU temperature graphs",
        CoolantHelp="Adds the Corsair hub's coolant temperature on the same °C scale, dashed and teal by default. It is drawn only while DeadWare drives the hub.";
    const string Dash="—";
    static readonly CultureInfo Inv=CultureInfo.InvariantCulture;

    public static string Rpm(int? rpm)=>rpm is{} v?v.ToString("N0",Inv)+" RPM":Dash;
    public static string Celsius(double? c)=>c is{} v?v.ToString("F1",Inv)+" °C":Dash;
    public static string Fans(IEnumerable<int?> rpm)
    {
        var list=rpm.ToList();
        return list.Count==0?Dash:string.Join(" · ",list.Select(v=>v is{} n?n.ToString("N0",Inv):Dash))+" RPM";
    }

    // Phrase, then the "when" for the Settings line and for the Lighting page. OpenRgbHasIt reads its "when" from the detail (ruling P32).
    static (string? Phrase,string? Settings,string? Lighting) Row(string? reason,string? detail,bool blocked)=>reason switch
    {
        HubReasons.ICue=>("iCUE is running","once iCUE has quit","iCUE has quit"),
        HubReasons.Service=>("the Corsair service is running","once the service has stopped","the service has stopped"),
        HubReasons.DeadLight=>("DeadLight is running","once DeadLight has quit","DeadLight has quit"),
        HubReasons.OtherOpenRgb=>("another OpenRGB is running","once it has quit","it has quit"),
        HubReasons.NoLighting=>(blocked?"lighting is not running":"lighting has stopped","when lighting runs again","lighting runs again"),
        HubReasons.NoHub=>("no iCUE LINK hub was found","when it is plugged in again","it is plugged in again"),
        HubReasons.Silent=>("the hub stopped answering","when DeadWare tries again in a moment","DeadWare tries again in a moment"),
        HubReasons.FallsBack=>("the hub keeps falling back to its own mode","when DeadWare tries again in a moment","DeadWare tries again in a moment"),
        HubReasons.UnknownPart=>(string.IsNullOrEmpty(detail)?"the hub has a part DeadWare doesn't know":$"the hub has a part DeadWare doesn't know ({detail})",
            "when DeadWare is restarted","DeadWare is restarted"),
        HubReasons.TooManyParts=>("the hub has more parts than DeadWare can read","when DeadWare is restarted","DeadWare is restarted"),
        HubReasons.TwoHubs=>("this PC has more than one iCUE LINK hub","when only one is plugged in","only one is plugged in"),
        HubReasons.OpenRgbHasIt=>detail==HubReasons.RestartSpent
            ?("OpenRGB still has the hub","when DeadWare is restarted","DeadWare is restarted")
            :("OpenRGB still has the hub","once OpenRGB has restarted without it","OpenRGB has restarted without it"),
        HubReasons.NoLock=>("DeadWare can't use the lock Corsair programs share","when DeadWare is restarted","DeadWare is restarted"),
        HubReasons.CantSave=>("DeadWare can't save its lighting settings",null,null),
        HubReasons.Asleep=>("the PC went to sleep",null,null),
        _=>(null,null,null),
    };
    public static string? Reason(string? reason,string? detail,bool blocked=false)=>Row(reason,detail,blocked).Phrase;
    // settings: the Settings phrase ("once iCUE has quit"), else the Lighting page's ("iCUE has quit"). detail matters for OpenRgbHasIt only.
    public static string? When(string? reason,string? detail,bool settings){var r=Row(reason,detail,false);return settings?r.Settings:r.Lighting;}

    // Starting and Waking are a moment's wait, not a fault: no caution line and no Lighting-page line, and the Settings state line is TakesLine.
    public static bool TakesInAMoment(string? reason)=>reason is HubReasons.Starting or HubReasons.Waking;

    static IEnumerable<HubPartReading> Of(HubReadings? r,string kind)=>(r?.Parts??[]).Where(p=>p.Kind==kind).OrderBy(p=>p.Channel);
    public static string Driving(HubReadings? now,TimeSpan? age)
    {
        const string head="On. DeadWare is driving the hub.";
        if(now is null) return head;
        if(!Fresh(now,age)) return $"{head}  {Coolant} {Dash} · {Pump} {Dash} · Fans {Dash}";
        var coolant=Of(now,HubPartKinds.Pump).FirstOrDefault()?.Celsius;
        return $"{head}  {Coolant} {Celsius(coolant)} · {Pump} {Rpm(Of(now,HubPartKinds.Pump).FirstOrDefault()?.Rpm)} · Fans {Fans(Of(now,HubPartKinds.Fan).Select(p=>p.Rpm))}";
    }
    public static string? TakenLine(HubReadings? before,DateTimeOffset? at,DateTimeOffset now)
    {
        if(before is null||at is null) return null;
        var when=at.Value.ToOffset(now.Offset);
        string day=when.Date==now.Date?"today":when.ToString("d MMMM",Inv);
        return $"Taken over {day} at {when.ToString("HH:mm",Inv)}. Before: pump {Rpm(Of(before,HubPartKinds.Pump).FirstOrDefault()?.Rpm)}, fans {Fans(Of(before,HubPartKinds.Fan).Select(p=>p.Rpm))}.";
    }
    public static string? LetGoCaution(string? reason,string? detail)
    {
        var r=Row(reason,detail,false);
        return r.Phrase is null||r.Settings is null?null
            :$"DeadWare has let go of the hub because {r.Phrase}. The hub is on its own built-in lights and fan settings. DeadWare takes it again {r.Settings}.";
    }
    static string PartName(HubStop s,bool picture)=>s.Part==HubPartKinds.Pump
        ?(s.Number<=1?"the pump":$"pump {s.Number}"):(picture?$"radiator fan {s.Number}":$"fan {s.Number}");
    public static string WhatHappened(HubStop stop,bool picture)
    {
        var part=PartName(stop,picture);
        return stop.Kind switch
        {
            HubStops.NoReading=>$"After the take-over DeadWare got no speed reading for {part} ({Rpm(stop.Before)} before).",
            HubStops.PumpStopped=>$"While DeadWare was driving the hub {part} read 0 RPM for 10 seconds.",
            _=>$"After the take-over {part} ran at {Rpm(stop.After)}, down from {Rpm(stop.Before)} before.",
        };
    }
    static readonly string[] BlockedOrder=[HubReasons.ICue,HubReasons.Service,HubReasons.DeadLight,HubReasons.OtherOpenRgb,HubReasons.NoHub,HubReasons.UnknownPart,
        HubReasons.TooManyParts,HubReasons.TwoHubs,HubReasons.NoLighting,HubReasons.CantSave,HubReasons.NoLock];
    public static string Blocked(IEnumerable<(string Reason,string? Detail)> reasons)
    {
        var phrases=reasons.Select((r,i)=>(i,rank:Array.IndexOf(BlockedOrder,r.Reason),phrase:Reason(r.Reason,r.Detail,true))).Where(x=>x.phrase is not null)
            .OrderBy(x=>x.rank<0?int.MaxValue:x.rank).ThenBy(x=>x.i).Select(x=>x.phrase!).ToList();
        if(phrases.Count==0) phrases.Add(Reason(HubReasons.NoLighting,null,true)!);
        return $"DeadWare can't take the hub yet: {string.Join(", and ",phrases)}. See below for what to do, then try again.";
    }
    // The unknown part's detail stays out of this line; the blocked line shows it (ruling P34).
    public static string Interrupted(string? reason,string? detail=null)
    {
        var phrase=Reason(reason,null)??"something went wrong";
        return $"The take-over did not finish ({phrase}). The switch is off and the hub's lights stay with OpenRGB.";
    }
    public static string UnknownPart(int type,int model)=>$"type 0x{type:X2}, model 0x{model:X2}";
    // LightingPage.RunningText, with the hub: a hub DeadWare drives, or one on its own, is not counted among the OpenRGB devices.
    public static string Running(HostState state)
    {
        int count=(state.Devices??[]).Where(d=>d is{Key:not null}&&d.Hub is not(HubRoutes.Driven or HubRoutes.Own)&&(d.Mouse is null||d.Mouse.Connection==MouseConnections.Cable))
            .Select(d=>d.Key).Distinct(StringComparer.Ordinal).Count();
        string devices=count==1?"1 device through OpenRGB":$"{count} devices through OpenRGB";
        devices+=state.Hub?.State switch
        {
            HubStates.Driving=>" · the Corsair hub is driven by DeadWare",
            HubStates.LetGo or HubStates.GaveBack=>" · the Corsair hub is on its own",
            _=>"",
        };
        if(!EverydayTexts.Lit(state)) return $"{devices} · lights off";
        var kind=LightingEffects.KindOf(state.Settings.Effect?.Kind);
        return kind==EffectKind.Static?$"{devices} · Static is on":$"{devices} · {kind} is running";
    }
    // The Lighting page's line about a hub that is on its own; Starting and Waking give none (ruling P33).
    public static string? ReasonLine(HubView? hub,bool picture)
    {
        if(hub is null) return null;
        if(hub.State==HubStates.LetGo)
        {
            var phrase=Reason(hub.Reason,hub.Detail);var when=When(hub.Reason,hub.Detail,false);
            return phrase is null||when is null?null:$"DeadWare has let go of the Corsair hub because {phrase}. The hub shows its own built-in effect until {when}.";
        }
        if(hub.State!=HubStates.GaveBack||hub.Stop is null) return null;
        string because=hub.Stop.Kind switch
        {
            HubStops.NoReading=>$"it got no speed reading for {PartName(hub.Stop,picture)} after the take-over",
            HubStops.PumpStopped=>"the pump stopped",
            _=>$"{PartName(hub.Stop,picture)} ran slower after the take-over",
        };
        return $"DeadWare gave the Corsair hub back because {because}. Choose what to do in Settings → Lighting.";
    }
    public static string CardSub(string? route)=>route switch
    {
        HubRoutes.Driven=>"driven by DeadWare",
        HubRoutes.Own=>"on its own · DeadWare is not lighting it",
        _=>"lit through OpenRGB",
    };
    // The hub card's sub-line: the route first, then the picture's selection (marked); a hub on its own shows its route's line alone.
    public static string CardSubLine(string? route,string? marked)=>marked is null||route==HubRoutes.Own?CardSub(route):CardSub(route)+" · "+marked;
    public static string FanName(int number,bool picture)=>picture?$"Radiator fan {number}":$"Fan {number}";
    public static string FansLabel(bool picture)=>picture?"Radiator fans":"Fans";
    public static string CoolantLegend(double? celsius,string? hubState,string pattern)=>celsius is{} c?$"Coolant · {Celsius(c)} · {pattern}":hubState switch
    {
        HubStates.Off=>"Coolant · no reading (DeadWare is not driving the hub)",
        HubStates.LetGo or HubStates.GaveBack=>"Coolant · no reading (the hub is on its own)",
        _=>"Coolant · no reading",
    };
    // A reading older than 10 s, or none, shows as —.
    public static bool Fresh(HubReadings? readings,TimeSpan? age)=>readings is not null&&age is{} a&&a<=TimeSpan.FromSeconds(10);
    // The hub card's Readings section. An entry without a value is a line of text, not a row.
    public static IReadOnlyList<(string Label,string? Value)> ReadingRows(string? route,HubReadings? readings,TimeSpan? age,bool picture)
    {
        switch(route)
        {
            case HubRoutes.OpenRgb: return [(ReadingsOff,null)];
            case HubRoutes.Own: return [(ReadingsOwn,null)];
            case HubRoutes.Driven:
                if(!Fresh(readings,age)) return [(Coolant,Dash),(Pump,Dash),(FansLabel(picture),Dash)];
                return [(Coolant,Celsius(Of(readings,HubPartKinds.Pump).FirstOrDefault()?.Celsius)),(Pump,Rpm(Of(readings,HubPartKinds.Pump).FirstOrDefault()?.Rpm)),
                    (FansLabel(picture),Fans(Of(readings,HubPartKinds.Fan).Select(p=>p.Rpm)))];
            default: return [];
        }
    }
}
