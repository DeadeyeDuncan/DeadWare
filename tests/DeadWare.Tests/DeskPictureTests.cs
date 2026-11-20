using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class DeskPictureTests
{
    static string Key(string name)=>name+"|loc";
    static DeviceView Dev(string key,string name,PaintKind kind,params (string Name,int Leds)[] zones)=>new(key,name,0,kind,null,null,false,zones.Select((z,i)=>new ZoneView(LightingKeys.Zone(key,i),i,z.Name,z.Leds,"#000000",false)).ToList());
    static DeviceView Dev(string name,params (string Name,int Leds)[] zones)=>Dev(Key(name),name,PaintKind.Direct,zones);
    // The eight devices of tests/DeadWare.Devices.Tests/Fixtures/controllers.json, in its order.
    static List<DeviceView> Rig(string mouse="SteelSeries Aerox 9 Wireless Wired")=>
    [
        Dev("RAM|B","Corsair Vengeance RGB DDR5",PaintKind.Direct,("Corsair DRAM",10)),
        Dev("RAM|A","Corsair Vengeance RGB DDR5",PaintKind.Direct,("Corsair DRAM",10)),
        Dev("Gigabyte Radeon RX 6800 XT GAMING OC",("GPU zone 1",1),("GPU zone 2",1),("GPU zone 3",1),("GPU zone 4",1),("GPU zone 5",1)),
        Dev("SteelSeries Apex Pro",("Keyboard",112)),
        Dev(mouse,("Front",1),("Middle",1),("Rear",1),("Reactive",1)),
        Dev("Corsair iCUE Link System Hub",("iCUE LINK RX RGB",8),("iCUE LINK RX RGB",8),("iCUE LINK RX RGB",8),("iCUE LINK TITAN 360",20)),
        Dev(Key("Corsair Lighting Node Pro"),"Corsair Lighting Node Pro",PaintKind.Hardware,("Corsair Channel 1",48),("Corsair Channel 2",0)),
        Dev("MSI MAG B650M MORTAR WIFI (MS-7D76)",("JRGB1",1),("JRGB2",1),("JRAINBOW1",0),("JRAINBOW2",0)),
    ];

    // Once saved, a picture is kept whatever devices are found: a PC can't lose its motherboard, only OpenRGB can miss it.
    [Fact] public void Applies_needs_both_signature_devices_unless_already_in_use()
    {
        var board="MSI MAG B650M MORTAR WIFI (MS-7D76)";var hub="Corsair iCUE Link System Hub";
        Assert.Same(DeskPictures.OwnerRig,DeskPictures.Applies([board,hub,"x"],null));
        Assert.Null(DeskPictures.Applies([board],null));
        Assert.Null(DeskPictures.Applies([hub],null));
        Assert.Null(DeskPictures.Applies(["x"],null));
        Assert.Same(DeskPictures.OwnerRig,DeskPictures.Applies([board],DeskPictures.OwnerRigName));
        Assert.Same(DeskPictures.OwnerRig,DeskPictures.Applies([hub],DeskPictures.OwnerRigName));
        Assert.Same(DeskPictures.OwnerRig,DeskPictures.Applies(["x"],DeskPictures.OwnerRigName));
        Assert.Same(DeskPictures.OwnerRig,DeskPictures.Applies([],DeskPictures.OwnerRigName));
        Assert.Null(DeskPictures.Applies([board],"some-other-picture"));
        Assert.Null(DeskPictures.Named("nope"));Assert.Null(DeskPictures.Named(null));
    }
    [Fact] public void Every_part_resolves_to_its_device_and_zone()
    {
        var s=DeskPictures.Resolve(DeskPictures.OwnerRig,Rig());
        ResolvedPart P(string id)=>s.Parts.Single(p=>p.Part.Id==id);
        Assert.Equal(12,s.Parts.Count);Assert.All(s.Parts,p=>Assert.True(p.Found));
        Assert.Equal(LightingKeys.Zone(Key("SteelSeries Apex Pro"),0),P("keyboard").ShownZoneKey);
        Assert.Equal(LightingKeys.Zone(Key("SteelSeries Aerox 9 Wireless Wired"),1),P("mouse").ShownZoneKey);Assert.Equal(4,P("mouse").ZoneKeys.Count);
        Assert.Equal(LightingKeys.Zone(Key("Corsair iCUE Link System Hub"),2),P("fan3").ShownZoneKey);
        Assert.Equal(LightingKeys.Zone(Key("Corsair iCUE Link System Hub"),3),P("pump").ShownZoneKey);
        Assert.Equal("RAM|A",P("ram1").DeviceKey);Assert.Equal("RAM|B",P("ram2").DeviceKey);        // by key order, not list order
        Assert.Equal(5,P("gpu").ZoneKeys.Count);Assert.Equal(LightingKeys.Zone(Key("Gigabyte Radeon RX 6800 XT GAMING OC"),0),P("gpu").ShownZoneKey);   // the card shows GPU zone 1
        Assert.True(P("front2").OwnEffect);Assert.False(P("fan1").OwnEffect);
        Assert.Equal(new[]{"MSI motherboard headers (2 zones)"},s.NotInPicture);
    }
    [Fact] public void A_missing_device_is_not_found_and_nothing_else_moves()
    {
        var rig=Rig().Where(d=>!d.Name.StartsWith("Gigabyte")&&d.Key!="RAM|A").ToList();
        var s=DeskPictures.Resolve(DeskPictures.OwnerRig,rig);
        Assert.False(s.Parts.Single(p=>p.Part.Id=="gpu").Found);
        Assert.Equal("RAM|B",s.Parts.Single(p=>p.Part.Id=="ram1").DeviceKey);Assert.False(s.Parts.Single(p=>p.Part.Id=="ram2").Found);
        Assert.Null(DeskPictures.Hit(s,PictureView.Inside,315,295));                                  // the card's box: not found, so not tappable
        var full=DeskPictures.Resolve(DeskPictures.OwnerRig,Rig());
        foreach(var r in s.Parts.Where(r=>r.Part.Id is not ("gpu" or "ram1" or "ram2"))) Assert.Equal(full.Parts.Single(f=>f.Part.Id==r.Part.Id).DeviceKey,r.DeviceKey);   // nothing else moves
        Assert.All(s.Parts.Where(r=>r.Part.Id is not ("gpu" or "ram1" or "ram2")),r=>Assert.True(r.Found));
    }
    [Fact] public void Exact_name_parts_do_not_match_by_prefix()
    {
        var rig=Rig();rig.Add(Dev("A|first","SteelSeries Apex Pro TKL",PaintKind.Direct,("Keyboard",87)));   // its key sorts before the real keyboard's
        Assert.Equal(Key("SteelSeries Apex Pro"),DeskPictures.Resolve(DeskPictures.OwnerRig,rig).Parts.Single(p=>p.Part.Id=="keyboard").DeviceKey);
        var only=Rig().Where(d=>d.Name!="SteelSeries Apex Pro").ToList();only.Add(rig[^1]);
        Assert.False(DeskPictures.Resolve(DeskPictures.OwnerRig,only).Parts.Single(p=>p.Part.Id=="keyboard").Found);
    }
    [Fact] public void Layout_writes_each_parts_own_point()
    {
        var r=DeskPictures.OwnerRig;var l=DeskPictures.Layout(r,Rig());DeskPoint At(string id)=>DeskPictures.PointOf(r,r.Parts.Single(p=>p.Id==id));
        var hub=Key("Corsair iCUE Link System Hub");var mouse=Key("SteelSeries Aerox 9 Wireless Wired");
        Assert.Equal(At("fan1"),l.Zones[LightingKeys.Zone(hub,0)]);Assert.Equal(At("fan2"),l.Zones[LightingKeys.Zone(hub,1)]);Assert.Equal(At("fan3"),l.Zones[LightingKeys.Zone(hub,2)]);
        Assert.Equal(At("pump"),l.Zones[LightingKeys.Zone(hub,3)]);Assert.Equal(At("keyboard"),l.Zones[LightingKeys.Zone(Key("SteelSeries Apex Pro"),0)]);
        Assert.Equal(At("ram1"),l.Zones[LightingKeys.Zone("RAM|A",0)]);Assert.Equal(At("ram2"),l.Zones[LightingKeys.Zone("RAM|B",0)]);
        Assert.Equal(At("mouse"),l.Zones[LightingKeys.Zone(mouse,3)]);Assert.Equal(At("gpu"),l.Zones[LightingKeys.Zone(Key("Gigabyte Radeon RX 6800 XT GAMING OC"),2)]);
    }
    [Fact] public void A_zone_lit_by_several_parts_takes_the_mean_whatever_the_part_order()
    {
        var r=DeskPictures.OwnerRig;var node=LightingKeys.Zone(Key("Corsair Lighting Node Pro"),0);
        var l=DeskPictures.Layout(r,Rig());var mid=DeskPictures.PointOf(r,r.Parts.Single(p=>p.Id=="front2"));
        Assert.Equal(mid.X,l.Zones[node].X,12);Assert.Equal(mid.Y,l.Zones[node].Y,12);
        var rev=DeskPictures.Layout(r with{Parts=r.Parts.Reverse().ToList()},Rig());
        Assert.True(DeskLayouts.Same(l,rev));
    }
    [Fact] public void A_part_only_matches_a_device_and_zones_the_desk_can_place()
    {
        var rig=Rig().Where(d=>d.Name!="SteelSeries Apex Pro").ToList();rig.Add(Dev("U|1","SteelSeries Apex Pro",PaintKind.Unsupported,("Keyboard",112)));
        Assert.False(DeskPictures.Resolve(DeskPictures.OwnerRig,rig).Parts.Single(p=>p.Part.Id=="keyboard").Found);
        var node=Key("Corsair Lighting Node Pro");
        var unsized=Rig().Select(d=>d.Key==node?Dev(node,d.Name,PaintKind.Hardware,("Corsair Channel 1",0),("Corsair Channel 2",0)):d).ToList();
        var s=DeskPictures.Resolve(DeskPictures.OwnerRig,unsized);
        Assert.All(s.Parts.Where(p=>p.Part.Id.StartsWith("front")),p=>Assert.False(p.Found));
        Assert.DoesNotContain(LightingKeys.Zone(node,0),DeskPictures.Layout(DeskPictures.OwnerRig,unsized).Zones.Keys);
        var shownDark=Rig().Select(d=>d.Name.StartsWith("SteelSeries Aerox")?Dev(d.Key,d.Name,PaintKind.Direct,("Front",1),("Middle",0),("Rear",1),("Reactive",1)):d).ToList();   // shown zone 1 has no LEDs
        Assert.False(DeskPictures.Resolve(DeskPictures.OwnerRig,shownDark).Parts.Single(p=>p.Part.Id=="mouse").Found);
        var mouse=DeskPictures.Resolve(DeskPictures.OwnerRig,Rig().Select(d=>d.Name.StartsWith("SteelSeries Aerox")?Dev(d.Key,d.Name,PaintKind.Direct,("Front",1),("Middle",1),("Rear",0),("Reactive",1)):d).ToList()).Parts.Single(p=>p.Part.Id=="mouse");
        Assert.Equal(3,mouse.ZoneKeys.Count);Assert.DoesNotContain(LightingKeys.Zone(mouse.DeviceKey!,2),mouse.ZoneKeys);   // only zones with LEDs
    }
    [Fact] public void Where_tap_areas_overlap_the_nearer_centre_wins()
    {
        var s=DeskPictures.Resolve(DeskPictures.OwnerRig,Rig());
        Assert.Equal("ram2",DeskPictures.Hit(s,PictureView.Inside,406,170)!.Part.Id);Assert.Equal("ram1",DeskPictures.Hit(s,PictureView.Inside,403.9,170)!.Part.Id);
    }
    [Fact] public void The_mouse_resolves_under_its_wireless_name()=>Assert.True(DeskPictures.Resolve(DeskPictures.OwnerRig,Rig("SteelSeries Aerox 9 Wireless")).Parts.Single(p=>p.Part.Id=="mouse").Found);
    [Fact] public void A_device_without_a_part_is_named_as_it_is_and_a_dark_device_is_not_listed()
    {
        var rig=Rig();rig.Add(Dev("Odd thing",("a",3)));rig.Add(Dev("Dark",("a",0)));
        Assert.Equal(new[]{"MSI motherboard headers (2 zones)","Odd thing (1 zone)"},DeskPictures.Resolve(DeskPictures.OwnerRig,rig).NotInPicture);
    }
    // A lit zone of a device in the picture that no part covers (a fifth zone on the hub, a second on the keyboard): named with the device,
    // given no point, while the device's covered zones keep their points and the device stays placed.
    [Fact] public void A_lit_zone_no_part_covers_is_named_and_unplaced_and_the_rest_of_its_device_stays()
    {
        var hub=Key("Corsair iCUE Link System Hub");var kb=Key("SteelSeries Apex Pro");var board=Key("MSI MAG B650M MORTAR WIFI (MS-7D76)");
        var rig=Rig().Select(d=>d.Key==hub?Dev("Corsair iCUE Link System Hub",("iCUE LINK RX RGB",8),("iCUE LINK RX RGB",8),("iCUE LINK RX RGB",8),("iCUE LINK TITAN 360",20),("Spare",4),("Dark",0),("Strip",6))
            :d.Key==kb?Dev("SteelSeries Apex Pro",("Keyboard",112),("Logo",1)):d).ToList();
        var s=DeskPictures.Resolve(DeskPictures.OwnerRig,rig);
        Assert.All(s.Parts,p=>Assert.True(p.Found));
        Assert.Equal(new[]{"SteelSeries Apex Pro (1 zone)","Corsair iCUE Link System Hub (2 zones)","MSI motherboard headers (2 zones)"},s.NotInPicture);   // in the device list's order
        var plain=DeskPictures.Layout(DeskPictures.OwnerRig,Rig());var l=DeskPictures.Layout(DeskPictures.OwnerRig,rig);
        Assert.Equal(plain.Zones,l.Zones);                                                           // the covered zones' points as before, none for the extra zones
        Assert.Equal(new[]{board},l.Unplaced);                                                        // device keys only: an uncovered zone has no point, which is what keeps its saved colour
        Assert.DoesNotContain(hub,l.Unplaced);Assert.DoesNotContain(kb,l.Unplaced);                    // a device key there would stop the effect on its covered zones too
        Assert.Null(DeskLayouts.Validate(l));
        rig.Reverse();Assert.True(DeskLayouts.Same(l,DeskPictures.Layout(DeskPictures.OwnerRig,rig)));
    }
    [Fact] public void Desk_parts_sit_at_their_centres_and_the_case_fills_its_wave_region()
    {
        var r=DeskPictures.OwnerRig;DeskPoint At(string id)=>DeskPictures.PointOf(r,r.Parts.Single(p=>p.Id==id));
        Assert.Equal(390/720.0,At("keyboard").X,6);Assert.Equal(125/450.0,At("keyboard").Y,6);
        Assert.Equal(0.03+0.27*(219-140)/440.0,At("fan1").X,6);Assert.Equal(0.60+0.38*(51-22)/418.0,At("fan1").Y,6);
        Assert.True(At("fan1").X<At("fan2").X&&At("fan2").X<At("fan3").X&&At("fan3").X<At("keyboard").X);
        Assert.All(r.Parts.Where(p=>p.View==PictureView.Inside),p=>{var a=DeskPictures.PointOf(r,p);Assert.InRange(a.X,0.03,0.30);Assert.InRange(a.Y,0.60,0.98);});
    }
    [Fact] public void Layout_is_exactly_the_pictures()
    {
        var l=DeskPictures.Layout(DeskPictures.OwnerRig,Rig());
        Assert.Equal(DeskPictures.OwnerRigName,l.Picture);Assert.Null(DeskLayouts.Validate(l));
        Assert.Equal(1+4+4+1+1+5+1,l.Zones.Count);                                                   // keyboard, mouse, hub, two sticks, card, Node Pro channel 1
        Assert.Equal(l.Zones[LightingKeys.Zone(Key("Gigabyte Radeon RX 6800 XT GAMING OC"),0)],l.Zones[LightingKeys.Zone(Key("Gigabyte Radeon RX 6800 XT GAMING OC"),4)]);
        Assert.Equal(new[]{Key("MSI MAG B650M MORTAR WIFI (MS-7D76)")},l.Unplaced);
        Assert.Empty(l.Tower.Members);Assert.Empty(l.Tower.Excluded);
        Assert.True(DeskLayouts.Same(l,DeskPictures.Layout(DeskPictures.OwnerRig,Rig())));
    }
    // Unplaced follows device key order, not OpenRGB's list order, so a reordered list never changes the layout.
    [Fact] public void Layout_lists_unplaced_devices_by_key_whatever_the_list_order()
    {
        var rig=Rig();rig.Add(Dev("Z|1","Odd Z",PaintKind.Direct,("a",3)));rig.Add(Dev("A|1","Odd A",PaintKind.Direct,("a",3)));
        var expected=new[]{"A|1",Key("MSI MAG B650M MORTAR WIFI (MS-7D76)"),"Z|1"};
        Assert.Equal(expected,DeskPictures.Layout(DeskPictures.OwnerRig,rig).Unplaced);
        rig.Reverse();
        Assert.Equal(expected,DeskPictures.Layout(DeskPictures.OwnerRig,rig).Unplaced);
    }
    // The wave runs in the unit square, where a box point is (x/720, y/450): across the drawing its colours travel along (cos/720, sin/450), not (cos, sin).
    [Theory]
    [InlineData(0,1,0)]
    [InlineData(90,0,1)]
    [InlineData(45,0.529998940,0.847998304)]                                                           // (450, 720) / 849.06: about 58 degrees in the drawing, not 45
    public void The_waves_direction_in_the_drawing_is_the_way_its_colours_travel(double angle,double x,double y)
    {
        var d=DeskPictures.WaveDirection(angle);
        Assert.Equal(x,d.X,6);Assert.Equal(y,d.Y,6);Assert.Equal(1,d.X*d.X+d.Y*d.Y,9);
        // Two points on a line at right angles to it are lit alike, and a step along it moves the phase forward.
        double Along(double px,double py)=>LightingEffects.Project(new(px/720,py/450),angle);
        Assert.Equal(Along(300,200),Along(300-80*d.Y,200+80*d.X),9);
        Assert.True(Along(300+80*d.X,200+80*d.Y)>Along(300,200));
    }
    [Fact] public void Taps_hit_found_parts_in_their_own_view_only()
    {
        var s=DeskPictures.Resolve(DeskPictures.OwnerRig,Rig());
        Assert.Equal("keyboard",DeskPictures.Hit(s,PictureView.Desk,390,125)!.Part.Id);
        Assert.Equal("ram1",DeskPictures.Hit(s,PictureView.Inside,396,170)!.Part.Id);Assert.Equal("ram2",DeskPictures.Hit(s,PictureView.Inside,412,170)!.Part.Id);
        Assert.Equal("ram1",DeskPictures.Hit(s,PictureView.Inside,386,170)!.Part.Id);                 // a thin part has a tap area at least 28 wide, split fairly with its neighbour
        Assert.Null(DeskPictures.Hit(s,PictureView.Inside,260,120));                                   // board area: clear of the pump, the fan bars and RAM stick 1's tap area
        foreach(var (x,y) in new[]{(390.0,125.0),(566.0,126.0)})                                       // the keyboard's and the mouse's desk coordinates, in the inside view
        {
            var hit=DeskPictures.Hit(s,PictureView.Inside,x,y);
            Assert.True(hit is null||hit.Part.View==PictureView.Inside);                               // desk parts are never hit in the inside view
        }
        Assert.Equal("front2",DeskPictures.Hit(s,PictureView.Inside,528,216)!.Part.Id);                // own-effect parts can be tapped
        Assert.Null(DeskPictures.Hit(s,PictureView.Desk,360,50));                                      // the main monitor: furniture
        Assert.True(DeskPictures.HitTower(DeskPictures.OwnerRig,PictureView.Desk,77,416));Assert.False(DeskPictures.HitTower(DeskPictures.OwnerRig,PictureView.Desk,390,125));
        Assert.False(DeskPictures.HitTower(DeskPictures.OwnerRig,PictureView.Inside,77,416));         // the tower is hit only in the desk view
    }
}
