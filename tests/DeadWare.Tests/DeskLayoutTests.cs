using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class DeskLayoutTests
{
    static DeviceView Dev(string key,string name,PaintKind kind,params int[] leds)=>new(key,name,0,kind,null,null,false,leds.Select((n,i)=>new ZoneView(LightingKeys.Zone(key,i),i,"z"+i,n,"#000000",false)).ToList());
    static ArrangeDevice A(string key,string name,params int[] idx)=>new(key,name,idx);
    static readonly ArrangeDevice Tower=A("t|1","Corsair Lighting Node Pro",0,1);
    static readonly ArrangeDevice Kb=A("k|1","SteelSeries Apex Pro",0);
    static readonly ArrangeDevice Gpu=A("g|1","Gigabyte Radeon RX 6800 XT GAMING OC",0);
    static readonly ArrangeDevice Ram=A("r|1","Corsair Vengeance Pro RGB",0);
    static DeskLayout Arr(DeskLayout l,params ArrangeDevice[] d)=>DeskLayouts.AutoArrange(l,d);
    static IEnumerable<KeyValuePair<string,DeskPoint>> Sorted(DeskLayout l)=>l.Zones.OrderBy(e=>e.Key,StringComparer.Ordinal);

    [Fact] public void Empty_starts_on_the_L_left_preset_with_an_empty_tower_and_no_zones()
    {
        var l=DeskLayouts.Empty();
        Assert.Equal(1,l.Version);Assert.Empty(l.Zones);Assert.Empty(l.Tower.Members);Assert.Empty(l.Tower.Excluded);Assert.Empty(l.Unplaced);
        Assert.Equal(new DeskPoint(0.14,0.3),l.Tower.At);
        Assert.Equal(2,l.Desk.Count);
        Assert.Equal(DeskShape.LLeft,DeskLayouts.ShapeOf(l));
        Assert.Equal((0.28,0.55,0.67,0.35),(l.Desk[0].X,l.Desk[0].Y,l.Desk[0].W,l.Desk[0].H));
        Assert.Equal((0.05,0.15,0.23,0.75),(l.Desk[1].X,l.Desk[1].Y,l.Desk[1].W,l.Desk[1].H));
    }
    [Fact] public void Editing_the_desk_of_one_layout_cannot_corrupt_a_preset()
    {
        var a=DeskLayouts.Empty();a.Desk[0].X=0.99;
        Assert.NotEqual(0.99,DeskLayouts.Preset(DeskShape.LLeft)[0].X);
        Assert.NotEqual(0.99,DeskLayouts.Empty().Desk[0].X);
    }
    [Fact] public void LRight_is_the_mirror_of_LLeft()
    {
        var left=DeskLayouts.Preset(DeskShape.LLeft);var right=DeskLayouts.Preset(DeskShape.LRight);
        Assert.Equal(left.Count,right.Count);
        for(int i=0;i<left.Count;i++){Assert.Equal(1-left[i].X-left[i].W,right[i].X,9);Assert.Equal(left[i].Y,right[i].Y,9);Assert.Equal(left[i].W,right[i].W,9);Assert.Equal(left[i].H,right[i].H,9);}
    }
    [Fact] public void Straight_preset_is_one_rectangle()
    {
        var s=DeskLayouts.Preset(DeskShape.Straight);
        Assert.Single(s);Assert.Equal((0.05,0.55,0.9,0.35),(s[0].X,s[0].Y,s[0].W,s[0].H));
        Assert.Equal(DeskShape.Straight,DeskLayouts.ShapeOf(new DeskLayout{Desk=s}));
    }
    [Fact] public void ShapeOf_is_null_for_a_custom_desk()
    {
        var l=DeskLayouts.Empty();l.Desk[0].X=0.3;Assert.Null(DeskLayouts.ShapeOf(l));
    }
    static DeskLayout Good()=>new(){Zones={["s|l#0"]=new(0.5,0.5)},Tower=new(){At=new(0.1,0.2),Members={"s|l"}},Desk=new(){new(){X=0,Y=0,W=1,H=1}}};
    [Fact] public void Validate_accepts_a_well_formed_layout()
    {
        Assert.Null(DeskLayouts.Validate(Good()));Assert.Null(DeskLayouts.Validate(DeskLayouts.Empty()));
    }
    [Fact] public void Validate_rejects_a_wrong_version()
    {
        var l=Good();l.Version=2;Assert.Contains("version",DeskLayouts.Validate(l));
    }
    [Fact] public void Validate_rejects_a_tower_anchor_outside_the_unit_square()
    {
        var l=Good();l.Tower.At=new(1.4,0);Assert.Contains("tower",DeskLayouts.Validate(l));
    }
    [Fact] public void Validate_rejects_coordinates_outside_the_unit_square_naming_the_key()
    {
        var l=Good();l.Zones["s|l#0"]=new(1.4,0);Assert.Contains("s|l#0",DeskLayouts.Validate(l));
        l=Good();l.Zones["s|l#0"]=new(0,-0.2);Assert.Contains("s|l#0",DeskLayouts.Validate(l));
    }
    [Fact] public void Validate_rejects_a_rectangle_that_leaves_the_unit_square_or_has_no_area()
    {
        var l=Good();l.Desk=new(){new(){X=0.9,Y=0,W=0.4,H=0.2}};Assert.Contains("desk",DeskLayouts.Validate(l));
        l=Good();l.Desk=new(){new(){X=0.1,Y=0,W=0,H=0.2}};Assert.Contains("desk",DeskLayouts.Validate(l));
    }
    [Fact] public void Clone_copies_the_picture()
    {
        var l=Good();l.Picture="b650m-link-hub";
        Assert.Equal("b650m-link-hub",DeskLayouts.Clone(l).Picture);Assert.Null(DeskLayouts.Clone(Good()).Picture);
    }
    [Fact] public void Validate_refuses_a_picture_name_over_64_characters_and_accepts_null_and_64()
    {
        var l=Good();l.Picture=null;Assert.Null(DeskLayouts.Validate(l));
        l.Picture=new string('a',64);Assert.Null(DeskLayouts.Validate(l));
        l.Picture=new string('a',65);Assert.Contains("picture",DeskLayouts.Validate(l));
    }
    [Fact] public void Same_compares_zones_as_a_map_and_the_lists_in_order()
    {
        DeskLayout Base(){var l=Good();l.Zones["a#0"]=new(0.1,0.2);l.Zones["b#0"]=new(0.3,0.4);l.Tower.Members=["x","y"];l.Unplaced=["u","v"];return l;}
        Assert.True(DeskLayouts.Same(Base(),Base()));Assert.True(DeskLayouts.Same(Base(),DeskLayouts.Clone(Base())));
        var reordered=Base();reordered.Zones=reordered.Zones.Reverse().ToDictionary(e=>e.Key,e=>e.Value);Assert.True(DeskLayouts.Same(Base(),reordered));
        var l2=Base();l2.Zones["a#0"]=new(0.1,0.2000001);Assert.False(DeskLayouts.Same(Base(),l2));
        l2=Base();l2.Zones["c#0"]=new(0.1,0.2);Assert.False(DeskLayouts.Same(Base(),l2));
        l2=Base();l2.Tower.Members=["y","x"];Assert.False(DeskLayouts.Same(Base(),l2));
        l2=Base();l2.Tower.Members.Add("z");Assert.False(DeskLayouts.Same(Base(),l2));
        l2=Base();l2.Unplaced=["v","u"];Assert.False(DeskLayouts.Same(Base(),l2));
        l2=Base();l2.Tower.At=new(0.2,0.3);Assert.False(DeskLayouts.Same(Base(),l2));
        l2=Base();l2.Desk[0].W+=0.01;Assert.False(DeskLayouts.Same(Base(),l2));
        l2=Base();l2.Desk.RemoveAt(0);Assert.False(DeskLayouts.Same(Base(),l2));
        l2=Base();l2.Picture="b650m-link-hub";Assert.False(DeskLayouts.Same(Base(),l2));
    }
    [Fact] public void Validate_checks_unplaced_and_the_entry_caps()
    {
        var l=DeskLayouts.Empty();l.Unplaced=null!;Assert.NotNull(DeskLayouts.Validate(l));
        l=DeskLayouts.Empty();for(int i=0;i<=DeskLayouts.MaxEntries;i++) l.Zones["k#"+i]=new(0.5,0.5);Assert.NotNull(DeskLayouts.Validate(l));
        l=DeskLayouts.Empty();l.Zones[new string('k',513)]=new(0.5,0.5);Assert.NotNull(DeskLayouts.Validate(l));
        l=DeskLayouts.Empty();l.Zones["k#0"]=new(1.2,0.5);Assert.NotNull(DeskLayouts.Validate(l));
        l=DeskLayouts.Empty();l.Zones["k#0"]=new(double.NaN,0.5);Assert.NotNull(DeskLayouts.Validate(l));
        l=DeskLayouts.Empty();l.Desk.Add(new(){X=0.1,Y=0.1,W=0.1,H=0.1});Assert.NotNull(DeskLayouts.Validate(l));   // three rectangles
        l=DeskLayouts.Empty();l.Version=2;Assert.NotNull(DeskLayouts.Validate(l));
    }
    [Fact] public void Validate_caps_members_excluded_and_unplaced_and_their_key_length()
    {
        foreach(var fill in new Action<DeskLayout,string>[]{(l,k)=>l.Tower.Members.Add(k),(l,k)=>l.Tower.Excluded.Add(k),(l,k)=>l.Unplaced.Add(k)})
        {
            var l=DeskLayouts.Empty();for(int i=0;i<=DeskLayouts.MaxEntries;i++) fill(l,"d"+i);Assert.NotNull(DeskLayouts.Validate(l));
            l=DeskLayouts.Empty();fill(l,new string('k',513));Assert.NotNull(DeskLayouts.Validate(l));
            l=DeskLayouts.Empty();fill(l,new string('k',512));Assert.Null(DeskLayouts.Validate(l));
        }
    }
    [Fact] public void Validate_refuses_nulls_without_throwing()
    {
        foreach(var broken in new Action<DeskLayout>[]{l=>l.Zones=null!,l=>l.Tower=null!,l=>l.Tower.Members=null!,l=>l.Tower.Excluded=null!,l=>l.Desk=null!,l=>l.Desk[0]=null!,l=>l.Tower.Members.Add(null!),l=>l.Unplaced.Add(null!)})
        {var l=DeskLayouts.Empty();broken(l);Assert.NotNull(DeskLayouts.Validate(l));}
    }
    [Fact] public void Presets_are_fresh_copies_and_ShapeOf_reads_them_back()
    {
        Assert.NotSame(DeskLayouts.Preset(DeskShape.LLeft)[0],DeskLayouts.Preset(DeskShape.LLeft)[0]);
        Assert.Equal(DeskShape.LRight,DeskLayouts.ShapeOf(new DeskLayout{Desk=DeskLayouts.Preset(DeskShape.LRight)}));
    }
    [Fact] public void Clone_is_deep()
    {
        var l=DeskLayouts.Empty();l.Zones["a#0"]=new(0.1,0.2);l.Tower.Members.Add("a");l.Unplaced.Add("u");
        var c=DeskLayouts.Clone(l);
        c.Zones["b#0"]=new(0.3,0.3);c.Tower.Members.Add("b");c.Desk[0].X=0.9;c.Unplaced.Add("v");c.Tower.At=new(0.9,0.9);
        Assert.Single(l.Zones);Assert.Single(l.Tower.Members);Assert.NotEqual(0.9,l.Desk[0].X);Assert.Single(l.Unplaced);Assert.Equal(new DeskPoint(0.14,0.3),l.Tower.At);
    }
    [Fact] public void IsTowerDevice_puts_internal_hardware_in_the_tower()
    {
        Assert.True(DeskLayouts.IsTowerDevice("Corsair Lighting Node Pro"));
        Assert.True(DeskLayouts.IsTowerDevice("Gigabyte Radeon RX 6800 XT GAMING OC"));
        Assert.True(DeskLayouts.IsTowerDevice("Corsair Vengeance Pro RGB"));
    }
    [Fact] public void IsTowerDevice_keeps_keyboards_and_mice_out_of_it()
    {
        Assert.False(DeskLayouts.IsTowerDevice("SteelSeries Apex Pro"));
        Assert.False(DeskLayouts.IsTowerDevice("SteelSeries Aerox 9 Wireless"));
        Assert.False(DeskLayouts.IsTowerDevice("Generic RGB KEYBOARD"));
    }
    [Fact] public void AutoArrange_gives_every_zone_a_position_inside_the_unit_square()
    {
        var l=Arr(DeskLayouts.Empty(),Tower,Kb);
        Assert.Equal(new[]{"k|1#0","t|1#0","t|1#1"},l.Zones.Keys.OrderBy(k=>k,StringComparer.Ordinal));
        foreach(var p in l.Zones.Values){Assert.InRange(p.X,0,1);Assert.InRange(p.Y,0,1);}
    }
    [Fact] public void AutoArrange_does_not_depend_on_enumeration_order()
    {
        var a=Arr(DeskLayouts.Empty(),Tower,Kb);var b=Arr(DeskLayouts.Empty(),Kb,Tower);
        Assert.Equal(Sorted(a),Sorted(b));
        Assert.Equal(a.Tower.Members,b.Tower.Members);
    }
    [Fact] public void AutoArrange_does_not_depend_on_the_order_of_several_tower_devices()
    {
        var all=new[]{Tower,Gpu,Ram};
        var orders=new[]{new[]{0,1,2},new[]{0,2,1},new[]{1,0,2},new[]{1,2,0},new[]{2,0,1},new[]{2,1,0}};
        var results=orders.Select(o=>Arr(DeskLayouts.Empty(),o.Select(i=>all[i]).ToArray())).ToList();
        foreach(var r in results.Skip(1))
        {
            Assert.Equal(Sorted(results[0]),Sorted(r));
            Assert.Equal(results[0].Tower.Members,r.Tower.Members);
        }
        Assert.Equal(new[]{"g|1","r|1","t|1"},results[0].Tower.Members);
    }
    [Fact] public void AutoArrange_keeps_an_overflowing_tower_together()
    {
        var many=A("m|1","Corsair Lighting Node Pro",Enumerable.Range(0,20).ToArray());
        var l=Arr(DeskLayouts.Empty(),many);
        Assert.Equal(20,l.Zones.Count);
        foreach(var p in l.Zones.Values){Assert.True(p.X<0.6);Assert.True(p.Y<0.8);}
        Assert.Equal(20,l.Zones.Values.Distinct().Count());
    }
    [Fact] public void AutoArrange_follows_the_tower_anchor()
    {
        var moved=DeskLayouts.Empty();moved.Tower.At=new(0.8,0.8);
        var l=Arr(moved,Tower);
        Assert.True(l.Zones["t|1#0"].X>0.5);Assert.True(l.Zones["t|1#0"].Y>0.5);
    }
    [Fact] public void AutoArrange_puts_tower_zones_at_the_back_and_peripherals_at_the_front()
    {
        var l=Arr(DeskLayouts.Empty(),Tower,Kb);
        Assert.True(l.Zones["t|1#0"].Y<0.5);Assert.True(l.Zones["k|1#0"].Y>0.75);
    }
    [Fact] public void AutoArrange_never_puts_two_zones_in_the_same_cell()
    {
        var l=Arr(DeskLayouts.Empty(),Tower);Assert.NotEqual(l.Zones["t|1#0"],l.Zones["t|1#1"]);
    }
    [Fact] public void AutoArrange_is_a_no_op_on_a_second_run_and_returns_the_same_object()
    {
        var first=Arr(DeskLayouts.Empty(),Tower,Kb);Assert.Same(first,Arr(first,Tower,Kb));
    }
    [Fact] public void AutoArrange_never_moves_a_position_that_already_exists()
    {
        var seeded=DeskLayouts.Empty();seeded.Zones["t|1#0"]=new(0.91,0.07);
        Assert.Equal(new DeskPoint(0.91,0.07),Arr(seeded,Tower,Kb).Zones["t|1#0"]);
    }
    [Fact] public void AutoArrange_places_a_device_that_appears_later_without_disturbing_the_others()
    {
        var first=Arr(DeskLayouts.Empty(),Tower);var second=Arr(first,Tower,Gpu);
        Assert.Equal(first.Zones["t|1#0"],second.Zones["t|1#0"]);
        Assert.True(second.Zones.ContainsKey("g|1#0"));Assert.Contains("g|1",second.Tower.Members);
    }
    [Fact] public void AutoArrange_respects_an_exclusion()
    {
        var seeded=DeskLayouts.Empty();seeded.Tower.Excluded.Add("t|1");
        var l=Arr(seeded,Tower);
        Assert.DoesNotContain("t|1",l.Tower.Members);Assert.True(l.Zones["t|1#0"].Y>0.75);
    }
    [Fact] public void AutoArrange_does_not_mutate_the_layout_it_was_given()
    {
        var start=DeskLayouts.Empty();Arr(start,Tower,Kb);
        Assert.Empty(start.Zones);Assert.Empty(start.Tower.Members);
    }
    [Fact] public void AutoArrange_places_only_the_listed_zone_indexes()
    {
        var l=Arr(DeskLayouts.Empty(),A("a|1","Fans",0,2));
        Assert.Equal(new[]{"a|1#0","a|1#2"},l.Zones.Keys.OrderBy(k=>k,StringComparer.Ordinal));
    }
    [Fact] public void Arrangeable_skips_unsupported_devices_and_zero_led_zones()
    {
        var list=DeskLayouts.Arrangeable([Dev("a|1","Fans",PaintKind.Direct,8,0,8),Dev("b|2","Odd",PaintKind.Unsupported,4),Dev("c|3","Node",PaintKind.Hardware,0,0),Dev("d|4","Node2",PaintKind.Hardware,48)]);
        Assert.Equal(new[]{"a|1","d|4"},list.Select(d=>d.Key));
        Assert.Equal(new[]{0,2},list[0].ZoneIndexes);
    }
    [Fact] public void Unplaced_devices_are_not_arranged_or_made_members()
    {
        var l=DeskLayouts.Empty();l.Unplaced.Add("a|1");
        Assert.Same(l,DeskLayouts.AutoArrange(l,[new("a|1","Fans",[0])]));
    }
    [Fact] public void A_device_that_is_not_arrangeable_never_joins_the_tower()
    {
        var next=DeskLayouts.AutoArrange(DeskLayouts.Empty(),DeskLayouts.Arrangeable([Dev("b|2","Odd",PaintKind.Unsupported,4)]));
        Assert.Empty(next.Tower.Members);
    }
}
