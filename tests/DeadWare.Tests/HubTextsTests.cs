using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class HubTextsTests
{
    [Theory]
    [InlineData("icue","iCUE is running","once iCUE has quit","iCUE has quit")]
    [InlineData("service","the Corsair service is running","once the service has stopped","the service has stopped")]
    [InlineData("deadlight","DeadLight is running","once DeadLight has quit","DeadLight has quit")]
    [InlineData("otherOpenRgb","another OpenRGB is running","once it has quit","it has quit")]
    [InlineData("noLighting","lighting has stopped","when lighting runs again","lighting runs again")]
    [InlineData("noHub","no iCUE LINK hub was found","when it is plugged in again","it is plugged in again")]
    [InlineData("silent","the hub stopped answering","when DeadWare tries again in a moment","DeadWare tries again in a moment")]
    [InlineData("fallsBack","the hub keeps falling back to its own mode","when DeadWare tries again in a moment","DeadWare tries again in a moment")]
    [InlineData("unknownPart","the hub has a part DeadWare doesn't know","when DeadWare is restarted","DeadWare is restarted")]
    [InlineData("tooManyParts","the hub has more parts than DeadWare can read","when DeadWare is restarted","DeadWare is restarted")]
    [InlineData("twoHubs","this PC has more than one iCUE LINK hub","when only one is plugged in","only one is plugged in")]
    [InlineData("openRgbHasIt","OpenRGB still has the hub","once OpenRGB has restarted without it","OpenRGB has restarted without it")]
    [InlineData("noLock","DeadWare can't use the lock Corsair programs share","when DeadWare is restarted","DeadWare is restarted")]
    [InlineData("cantSave","DeadWare can't save its lighting settings",null,null)]
    [InlineData("asleep","the PC went to sleep",null,null)]
    public void Each_reason_has_its_phrase_and_its_two_whens(string code,string phrase,string? settings,string? lighting)
    {
        Assert.Equal(phrase,HubTexts.Reason(code,null));
        Assert.Equal(settings,HubTexts.When(code,null,true));
        Assert.Equal(lighting,HubTexts.When(code,null,false));
    }
    [Theory]
    [InlineData("waking")]
    [InlineData("starting")]
    [InlineData("off")]
    [InlineData("nonsense")]
    [InlineData(null)]
    public void Other_codes_give_nothing(string? code)
    {
        Assert.Null(HubTexts.Reason(code,null));
        Assert.Null(HubTexts.When(code,null,true));
        Assert.Null(HubTexts.When(code,null,false));
        Assert.Null(HubTexts.LetGoCaution(code,null));
        Assert.Null(HubTexts.ReasonLine(new HubView("letGo",code),false));
    }
    [Fact] public void No_lighting_reads_differently_when_blocked()=>Assert.Equal("lighting is not running",HubTexts.Reason("noLighting",null,true));
    [Fact] public void An_unknown_part_shows_its_detail_unless_none_is_given()
    {
        Assert.Equal("the hub has a part DeadWare doesn't know (type 0x1A, model 0x01)",HubTexts.Reason("unknownPart","type 0x1A, model 0x01"));
        Assert.Equal("the hub has a part DeadWare doesn't know",HubTexts.Reason("unknownPart",null));
    }
    [Fact] public void The_four_open_rgb_has_it_phrases()
    {
        Assert.Equal("once OpenRGB has restarted without it",HubTexts.When("openRgbHasIt",null,true));
        Assert.Equal("OpenRGB has restarted without it",HubTexts.When("openRgbHasIt",null,false));
        Assert.Equal("when DeadWare is restarted",HubTexts.When("openRgbHasIt","restartSpent",true));
        Assert.Equal("DeadWare is restarted",HubTexts.When("openRgbHasIt","restartSpent",false));
        Assert.Equal("restartSpent",HubReasons.RestartSpent);
        Assert.Equal("DeadWare has let go of the hub because OpenRGB still has the hub. The hub is on its own built-in lights and fan settings. DeadWare takes it again when DeadWare is restarted.",HubTexts.LetGoCaution("openRgbHasIt","restartSpent"));
        Assert.Equal("DeadWare has let go of the Corsair hub because OpenRGB still has the hub. The hub shows its own built-in effect until DeadWare is restarted.",
            HubTexts.ReasonLine(new HubView("letGo","openRgbHasIt","restartSpent"),false));
        Assert.Equal("DeadWare has let go of the Corsair hub because OpenRGB still has the hub. The hub shows its own built-in effect until OpenRGB has restarted without it.",
            HubTexts.ReasonLine(new HubView("letGo","openRgbHasIt"),false));
    }
    [Fact] public void Starting_and_waking_show_no_hub_line()
    {
        foreach(var r in new[]{"starting","waking"})
        {
            Assert.Null(HubTexts.LetGoCaution(r,null));
            Assert.Null(HubTexts.ReasonLine(new HubView("letGo",r),true));
        }
    }
    [Fact] public void Starting_and_waking_read_takes_the_hub_in_a_moment()
    {
        Assert.Equal("On. DeadWare takes the hub in a moment.",HubTexts.TakesLine);
        Assert.True(HubTexts.TakesInAMoment("starting"));
        Assert.True(HubTexts.TakesInAMoment("waking"));
        Assert.False(HubTexts.TakesInAMoment("icue"));
        Assert.False(HubTexts.TakesInAMoment(null));
        Assert.Equal("starting",HubReasons.Starting);
    }
    [Fact] public void Numbers_and_unknown_parts_read_as_text()
    {
        Assert.Equal("2,709 RPM",HubTexts.Rpm(2709));
        Assert.Equal("—",HubTexts.Rpm(null));
        Assert.Equal("38.5 °C",HubTexts.Celsius(38.5));
        Assert.Equal("—",HubTexts.Celsius(null));
        Assert.Equal("1,331 · 1,326 · 1,340 RPM",HubTexts.Fans([1331,1326,1340]));
        Assert.Equal("1,331 · — · 1,340 RPM",HubTexts.Fans([1331,null,1340]));
        Assert.Equal("type 0x1A, model 0x01",HubTexts.UnknownPart(0x1A,1));
    }
    static HubReadings Now()=>new([new HubPartReading(1,"pump","Pump",2709,38.5),new HubPartReading(2,"fan","Fan",1331),new HubPartReading(3,"fan","Fan",1326),new HubPartReading(4,"fan","Fan",1340)]);
    [Fact] public void The_driving_line()
    {
        Assert.Equal("On. DeadWare is driving the hub.  Coolant 38.5 °C · Pump 2,709 RPM · Fans 1,331 · 1,326 · 1,340 RPM",HubTexts.Driving(Now()));
        Assert.Equal("On. DeadWare is driving the hub.",HubTexts.Driving(null));
    }
    [Fact] public void The_taken_line_gives_today_or_the_date()
    {
        var before=new HubReadings([new HubPartReading(1,"pump","Pump",2716,36.0),new HubPartReading(2,"fan","Fan",1337),new HubPartReading(3,"fan","Fan",1326),new HubPartReading(4,"fan","Fan",1337)]);
        var at=new DateTimeOffset(2026,11,25,18,42,0,TimeSpan.Zero);
        var now=new DateTimeOffset(2026,11,25,21,0,0,TimeSpan.FromHours(1));
        Assert.Equal("Taken over today at 19:42. Before: pump 2,716 RPM, fans 1,337 · 1,326 · 1,337 RPM.",HubTexts.TakenLine(before,at,now));
        Assert.Equal("Taken over 25 November at 19:42. Before: pump 2,716 RPM, fans 1,337 · 1,326 · 1,337 RPM.",HubTexts.TakenLine(before,at,now.AddDays(1)));
        Assert.Null(HubTexts.TakenLine(null,at,now));
    }
    [Fact] public void The_let_go_caution()=>Assert.Equal(
        "DeadWare has let go of the hub because iCUE is running. The hub is on its own built-in lights and fan settings. DeadWare takes it again once iCUE has quit.",HubTexts.LetGoCaution("icue",null));
    [Fact] public void What_happened()
    {
        Assert.Equal("After the take-over the pump ran at 2,010 RPM, down from 2,716 RPM before.",HubTexts.WhatHappened(new HubStop{Kind="slower",Part="pump",Number=1,Before=2716,After=2010},false));
        Assert.Equal("After the take-over pump 2 ran at 2,010 RPM, down from 2,716 RPM before.",HubTexts.WhatHappened(new HubStop{Kind="slower",Part="pump",Number=2,Before=2716,After=2010},false));
        Assert.Equal("After the take-over fan 2 ran at 1,193 RPM, down from 1,326 RPM before.",HubTexts.WhatHappened(new HubStop{Kind="slower",Part="fan",Number=2,Before=1326,After=1193},false));
        Assert.Equal("After the take-over radiator fan 2 ran at 1,193 RPM, down from 1,326 RPM before.",HubTexts.WhatHappened(new HubStop{Kind="slower",Part="fan",Number=2,Before=1326,After=1193},true));
        Assert.Equal("After the take-over DeadWare got no speed reading for the pump (2,716 RPM before).",HubTexts.WhatHappened(new HubStop{Kind="noReading",Part="pump",Number=1,Before=2716},false));
        Assert.Equal("While DeadWare was driving the hub the pump read 0 RPM for 10 seconds.",HubTexts.WhatHappened(new HubStop{Kind="pumpStopped",Part="pump",Number=1,Before=2716,After=0},false));
    }
    [Fact] public void Blocked_lists_phrases_in_a_fixed_order()
    {
        Assert.Equal("DeadWare can't take the hub yet: iCUE is running, and the Corsair service is running. See below for what to do, then try again.",
            HubTexts.Blocked([("service",null),("icue",null)]));
        Assert.Equal("DeadWare can't take the hub yet: DeadLight is running, and the hub has a part DeadWare doesn't know (type 0x1A, model 0x01), and lighting is not running, and DeadWare can't use the lock Corsair programs share. See below for what to do, then try again.",
            HubTexts.Blocked([("noLock",null),("noLighting",null),("unknownPart","type 0x1A, model 0x01"),("deadlight",null)]));
    }
    [Fact] public void Interrupted_leaves_out_the_unknown_parts_detail()
    {
        Assert.Equal("The take-over did not finish (the hub has a part DeadWare doesn't know). The switch is off and the hub's lights stay with OpenRGB.",HubTexts.Interrupted("unknownPart","type 0x1A, model 0x01"));
        Assert.Equal("The take-over did not finish (the hub stopped answering). The switch is off and the hub's lights stay with OpenRGB.",HubTexts.Interrupted("silent",null));
    }
    [Fact] public void The_reason_line_for_a_hub_that_is_let_go_or_given_back()
    {
        Assert.Equal("DeadWare has let go of the Corsair hub because iCUE is running. The hub shows its own built-in effect until iCUE has quit.",HubTexts.ReasonLine(new HubView("letGo","icue"),false));
        Assert.Null(HubTexts.ReasonLine(new HubView("letGo","asleep"),false));
        Assert.Equal("DeadWare gave the Corsair hub back because the pump ran slower after the take-over. Choose what to do in Settings → Lighting.",
            HubTexts.ReasonLine(new HubView("gaveBack",Stop:new HubStop{Kind="slower"}),false));
        Assert.Equal("DeadWare gave the Corsair hub back because it got no speed reading for the pump after the take-over. Choose what to do in Settings → Lighting.",
            HubTexts.ReasonLine(new HubView("gaveBack",Stop:new HubStop{Kind="noReading"}),false));
        Assert.Equal("DeadWare gave the Corsair hub back because the pump stopped. Choose what to do in Settings → Lighting.",
            HubTexts.ReasonLine(new HubView("gaveBack",Stop:new HubStop{Kind="pumpStopped"}),false));
        Assert.Null(HubTexts.ReasonLine(new HubView("driving"),false));
        Assert.Null(HubTexts.ReasonLine(null,false));
    }
    [Fact] public void Card_and_legend_texts()
    {
        Assert.Equal("lit through OpenRGB",HubTexts.CardSub("openrgb"));
        Assert.Equal("driven by DeadWare",HubTexts.CardSub("driven"));
        Assert.Equal("on its own · DeadWare is not lighting it",HubTexts.CardSub("own"));
        Assert.Equal("Radiator fan 2",HubTexts.FanName(2,true));
        Assert.Equal("Fan 2",HubTexts.FanName(2,false));
        Assert.Equal("Radiator fans",HubTexts.FansLabel(true));
        Assert.Equal("Fans",HubTexts.FansLabel(false));
        Assert.Equal("Coolant · 38.5 °C · dashed",HubTexts.CoolantLegend(38.5,"driving","dashed"));
        Assert.Equal("Coolant · no reading (DeadWare is not driving the hub)",HubTexts.CoolantLegend(null,"off","dashed"));
        Assert.Equal("Coolant · no reading (the hub is on its own)",HubTexts.CoolantLegend(null,"letGo","dashed"));
        Assert.Equal("Coolant · no reading (the hub is on its own)",HubTexts.CoolantLegend(null,"gaveBack","dashed"));
        Assert.Equal("Coolant · no reading",HubTexts.CoolantLegend(null,"driving","dashed"));
    }
    [Fact] public void The_fixed_texts()
    {
        var expected=new Dictionary<string,string>
        {
            ["Heading"]="Corsair iCUE LINK hub",
            ["SwitchLabel"]="DeadWare drives the hub",
            ["About"]="When this is on, DeadWare lights the hub itself and reads its coolant temperature and its fan and pump speeds. DeadWare never sets fan or pump speeds.",
            ["OffLine"]="Off. The hub's lights go through OpenRGB, as they do today.",
            ["SwitchingLine"]="Switching… the lights blink once.",
            ["LetGoLine"]="On, but DeadWare has let go for now.",
            ["TakesLine"]="On. DeadWare takes the hub in a moment.",
            ["GaveBackLine"]="Off. The hub is on its own.",
            ["GaveBackTitle"]="DeadWare gave the hub back.",
            ["GaveBackTail"]="The hub is now on its own built-in lights and fan settings, and stays that way until you choose.",
            ["BackButton"]="Back to OpenRGB, as before",
            ["RetryButton"]="Try the take-over again",
            ["NotBack"]="The hub has not come back through OpenRGB yet. Click the switch when its lights are back.",
            ["DialogTitle"]="Let DeadWare drive the Corsair hub?",
            ["DialogAsks"]="This changes which program is in charge of a cooling device, so DeadWare asks first.",
            ["DialogBlink"]="OpenRGB restarts without the hub, so all lights blink once. DeadWare then takes the hub and lights it.",
            ["DialogBoxHead"]="Read from the hub just now",
            ["DialogNoReading"]="DeadWare could not read the hub just now, so it cannot compare speeds afterwards. It will still give the hub back if the pump stops.",
            ["DialogCheck"]="After taking over, DeadWare reads the speeds again. If the pump or a fan runs more than 10% slower than above, DeadWare gives the hub back to its own built-in settings and tells you.",
            ["Never"]="DeadWare never sets fan or pump speeds.",
            ["TakeOver"]="Take over the hub",
            ["Cancel"]="Cancel",
            ["Coolant"]="Coolant",
            ["Pump"]="Pump",
            ["ReadingsHead"]="Readings",
            ["ReadingsNote"]="Read from the hub every few seconds. DeadWare shows these speeds; it never sets them.",
            ["ReadingsOff"]="Readings appear here once DeadWare drives the hub. Switch that on in Settings → Lighting.",
            ["ReadingsOwn"]="No readings while the hub is on its own.",
            ["OwnCaption"]="· own effect",
            ["CoolantOption"]="Show coolant temperature on CPU temperature graphs",
            ["CoolantHelp"]="Adds the Corsair hub's coolant temperature on the same °C scale, dashed and teal by default. It is drawn only while DeadWare drives the hub.",
        };
        var actual=typeof(HubTexts).GetFields().Where(f=>f.IsLiteral&&f.FieldType==typeof(string)).ToDictionary(f=>f.Name,f=>(string)f.GetRawConstantValue()!);
        Assert.Equal(expected.Keys.OrderBy(k=>k,StringComparer.Ordinal),actual.Keys.OrderBy(k=>k,StringComparer.Ordinal));
        foreach(var (name,text) in expected) Assert.Equal(text,actual[name]);
    }
    [Fact] public void Blocked_with_nothing_to_say_falls_back_to_lighting_not_running()
    {
        const string line="DeadWare can't take the hub yet: lighting is not running. See below for what to do, then try again.";
        Assert.Equal(line,HubTexts.Blocked([]));
        Assert.Equal(line,HubTexts.Blocked([("waking",null),("nonsense",null)]));
    }
    [Theory]
    [InlineData(null,false,0)]
    [InlineData("bogus",false,0)]
    [InlineData("openrgb",false,1)]
    [InlineData("own",false,1)]
    [InlineData("driven",false,3)]
    public void The_readings_rows_by_route(string? route,bool picture,int count)
    {
        var rows=HubTexts.ReadingRows(route,Reads(),TimeSpan.FromSeconds(1),picture);
        Assert.Equal(count,rows.Count);
        if(route=="openrgb") Assert.Equal(("Readings appear here once DeadWare drives the hub. Switch that on in Settings → Lighting.",(string?)null),rows[0]);
        if(route=="own") Assert.Equal(("No readings while the hub is on its own.",(string?)null),rows[0]);
    }
    static HubReadings Reads()=>new([new HubPartReading(1,"pump","Pump",2774,43.7),new HubPartReading(2,"fan","Fan",1660),new HubPartReading(3,"fan","Fan",1654),new HubPartReading(4,"fan","Fan",1663)]);
    [Fact] public void Driven_rows_hold_the_readings_up_to_ten_seconds()
    {
        var rows=HubTexts.ReadingRows("driven",Reads(),TimeSpan.FromSeconds(10),false);
        Assert.Equal([("Coolant","43.7 °C"),("Pump","2,774 RPM"),("Fans","1,660 · 1,654 · 1,663 RPM")],rows);
        Assert.Equal("Radiator fans",HubTexts.ReadingRows("driven",Reads(),TimeSpan.FromSeconds(10),true)[2].Label);
        var stale=new[]{("Coolant","—"),("Pump","—"),("Fans","—")};
        Assert.Equal(stale,HubTexts.ReadingRows("driven",Reads(),TimeSpan.FromMilliseconds(10001),false).Select(r=>(r.Label,r.Value!)));
        Assert.Equal(stale,HubTexts.ReadingRows("driven",null,TimeSpan.FromSeconds(1),false).Select(r=>(r.Label,r.Value!)));
        Assert.Equal(stale,HubTexts.ReadingRows("driven",Reads(),null,false).Select(r=>(r.Label,r.Value!)));
    }
    [Fact] public void The_coolant_is_the_first_pump_parts_reading()
    {
        // A fan part carrying a Celsius is not the coolant (a part has a coolant sensor exactly when it is a pump).
        var r=new HubReadings([new HubPartReading(1,"fan","Fan",1000,99.0),new HubPartReading(2,"pump","Pump",2000,40.0)]);
        Assert.Equal("40.0 °C",HubTexts.ReadingRows("driven",r,TimeSpan.Zero,false)[0].Value);
        Assert.StartsWith("On. DeadWare is driving the hub.  Coolant 40.0 °C",HubTexts.Driving(r));
    }

    static HostState State(int views,string? hubView,HubView? hub,string effect="Wave",bool lightsOn=true)
    {
        var devices=Enumerable.Range(0,views).Select(i=>new DeviceView("d"+i,"D",1,PaintKind.Direct,null,null,false,[],Hub:i==0?hubView:null)).ToList();
        var settings=new LightingSettings{LightsOn=lightsOn};
        settings.Effect.Kind=effect;
        return new HostState(HostStatus.Running,"",false,[],devices,settings,Hub:hub);
    }
    [Fact] public void The_status_line_counts_only_devices_through_OpenRGB()
    {
        Assert.Equal("8 devices through OpenRGB · Wave is running",HubTexts.Running(State(8,"openrgb",null)));
        Assert.Equal("7 devices through OpenRGB · the Corsair hub is driven by DeadWare · Wave is running",HubTexts.Running(State(8,"driven",new HubView("driving"))));
        Assert.Equal("7 devices through OpenRGB · the Corsair hub is on its own · Wave is running",HubTexts.Running(State(8,"own",new HubView("letGo"))));
        Assert.Equal("7 devices through OpenRGB · the Corsair hub is on its own · Wave is running",HubTexts.Running(State(8,"own",new HubView("gaveBack"))));
        Assert.Equal("7 devices through OpenRGB · the Corsair hub is driven by DeadWare · lights off",HubTexts.Running(State(8,"driven",new HubView("driving"),lightsOn:false)));
        Assert.Equal("1 device through OpenRGB · Static is on",HubTexts.Running(State(1,null,null,"Static")));
    }
    [Fact] public void A_mouse_not_on_its_cable_is_not_counted()
    {
        var s=State(2,null,null);
        s=s with{Devices=[s.Devices[0],s.Devices[1] with{Mouse=new MouseView(MouseConnections.Receiver,50,false,false,null,false)}]};
        Assert.Equal("1 device through OpenRGB · Wave is running",HubTexts.Running(s));
    }
}
