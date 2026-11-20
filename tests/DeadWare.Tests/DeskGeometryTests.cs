using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class DeskGeometryTests
{
    static DeviceView Dev(string key,string name,int zones=1,PaintKind kind=PaintKind.Direct,int leds=4)=>new(key,name,0,kind,null,null,false,Enumerable.Range(0,zones).Select(i=>new ZoneView(LightingKeys.Zone(key,i),i,$"{name} z{i}",leds,"#000000",false)).ToList());
    static readonly DeviceView Kb=Dev("k|1","Apex Keyboard"), Gpu=Dev("g|1","Radeon GPU",2), Ram=Dev("r|1","Vengeance RAM");
    static readonly IReadOnlyList<DeviceView> All=new[]{Kb,Gpu,Ram};
    static readonly IReadOnlySet<string> NoSplit=new HashSet<string>();
    static string Z(DeviceView d,int i)=>LightingKeys.Zone(d.Key,i);
    static DeskLayout Arranged(params DeviceView[] d)=>DeskLayouts.AutoArrange(DeskLayouts.Empty(),DeskLayouts.Arrangeable(d));
    static DeskScene Scene(DeskLayout l,IReadOnlyList<DeviceView> d,bool open=false,IReadOnlySet<string>? split=null)=>DeskGeometry.Scene(l,d,open,split??NoSplit);
    static void Close(double e,double a)=>Assert.True(Math.Abs(e-a)<1e-9,$"expected {e} got {a}");

    [Fact] public void Scene_DeviceOnDesk_IsOneItemAtFirstPlacedZoneLabelledWithName()
    {
        var l=Arranged(Kb);var s=Scene(l,new[]{Kb});
        var it=Assert.Single(s.Items,i=>i.Kind==DeskItemKind.Device);
        Assert.Equal("Apex Keyboard",it.Label);Assert.Equal("k|1",it.DeviceKey);Assert.Null(it.ZoneKey);Assert.Equal(l.Zones[Z(Kb,0)],it.At);Assert.Equal(new[]{Z(Kb,0)},it.ZoneKeys);Assert.False(it.OwnEffect);
    }
    [Fact] public void Scene_DeviceItemAtFirstPlacedZone_AndHoldsAllPlacedZoneKeys()
    {
        var l=Arranged(Kb,Gpu);var s=Scene(l,All.Take(2).ToList(),open:true);
        var it=s.Items.Single(i=>i.DeviceKey==Gpu.Key);
        Assert.Equal(l.Zones[Z(Gpu,0)],it.At);Assert.Equal(new[]{Z(Gpu,0),Z(Gpu,1)},it.ZoneKeys);
    }
    [Fact] public void Scene_TowerMember_AbsentClosedPresentOpen()
    {
        var l=Arranged(Gpu);Assert.Contains(Gpu.Key,l.Tower.Members);
        Assert.DoesNotContain(Scene(l,new[]{Gpu},false).Items,i=>i.DeviceKey==Gpu.Key);
        Assert.Contains(Scene(l,new[]{Gpu},true).Items,i=>i.DeviceKey==Gpu.Key);
    }
    [Fact] public void Scene_TowerItem_IsAlwaysThereAtTheAnchor()
    {
        var l=Arranged(Gpu);
        foreach(var open in new[]{false,true}){var t=Assert.Single(Scene(l,new[]{Gpu},open).Items,i=>i.Kind==DeskItemKind.Tower);Assert.Equal("PC tower",t.Label);Assert.Equal(l.Tower.At,t.At);Assert.Empty(t.ZoneKeys);}
    }
    [Fact] public void Scene_SplitDevice_GivesOneZoneItemPerPlacedZone()
    {
        var l=Arranged(Gpu);var s=Scene(l,new[]{Gpu},true,new HashSet<string>{Gpu.Key});
        var zs=s.Items.Where(i=>i.Kind==DeskItemKind.Zone).ToList();
        Assert.Equal(2,zs.Count);Assert.DoesNotContain(s.Items,i=>i.Kind==DeskItemKind.Device);
        Assert.Equal(new[]{"Radeon GPU z0","Radeon GPU z1"},zs.Select(z=>z.Label));Assert.Equal(new[]{Z(Gpu,0),Z(Gpu,1)},zs.Select(z=>z.ZoneKey));
        Assert.Equal(l.Zones[Z(Gpu,1)],zs[1].At);Assert.Equal(new[]{Z(Gpu,1)},zs[1].ZoneKeys);
    }
    [Fact] public void Scene_HardwareDevice_HasOwnEffect()
    {
        var hw=Dev("h|1","Apex Mouse",kind:PaintKind.Hardware);var s=Scene(Arranged(hw),new[]{hw});
        Assert.True(Assert.Single(s.Items,i=>i.Kind==DeskItemKind.Device).OwnEffect);
    }
    [Fact] public void Scene_UnplacedDevice_IsInNotPlacedWithNoItem()
    {
        var l=Arranged(Kb,Ram);l=DeskGeometry.SetUnplaced(l,new[]{Kb,Ram},Kb.Key,true);
        var s=Scene(l,new[]{Kb,Ram},true);
        Assert.Equal(new[]{Kb.Key},s.NotPlaced.Select(d=>d.Key));Assert.DoesNotContain(s.Items,i=>i.DeviceKey==Kb.Key);Assert.Contains(s.Items,i=>i.DeviceKey==Ram.Key);
    }
    [Fact] public void Scene_ArrangeableDeviceWithNoPlacedZone_IsNowhere()
    {
        var s=Scene(DeskLayouts.Empty(),new[]{Kb});
        Assert.DoesNotContain(s.Items,i=>i.DeviceKey==Kb.Key);Assert.Empty(s.NotPlaced);
    }
    [Fact] public void Scene_NoLedsOrUnsupported_AppearNowhere()
    {
        var none=Dev("n|1","Apex Dead",leds:0);var uns=Dev("u|1","Apex Unsup",kind:PaintKind.Unsupported);
        var l=Arranged(Kb);l.Unplaced.Add(none.Key);l.Unplaced.Add(uns.Key);l.Tower.Members.Add(none.Key);l.Tower.Members.Add(uns.Key);
        var s=Scene(l,new[]{Kb,none,uns},true);
        Assert.DoesNotContain(s.Items,i=>i.DeviceKey==none.Key||i.DeviceKey==uns.Key);Assert.Empty(s.NotPlaced);Assert.Equal(0,s.TowerDevices);
    }
    [Fact] public void Scene_TowerDevices_CountsOnlyArrangeableMembers()
    {
        var none=Dev("n|1","Fan none",leds:0);var l=Arranged(Gpu,Ram);l.Tower.Members.Add(none.Key);l.Tower.Members.Add("gone|9");
        Assert.Equal(2,Scene(l,new[]{Gpu,Ram,none},false).TowerDevices);
    }
    [Fact] public void TowerBox_Closed_IsAnchorPlusMinusHalf()
    {
        var l=DeskLayouts.Empty();var b=Scene(l,new DeviceView[0]).Tower;
        Close(0.08,b.X0);Close(0.24,b.Y0);Close(0.20,b.X1);Close(0.36,b.Y1);
    }
    [Fact] public void TowerBox_Closed_IsClampedToTheSquare()
    {
        var l=DeskLayouts.Empty();l.Tower.At=new(0.02,0.99);var b=Scene(l,new DeviceView[0]).Tower;
        Close(0,b.X0);Close(0.93,b.Y0);Close(0.08,b.X1);Close(1,b.Y1);
    }
    static void Box(double x0,double y0,double x1,double y1,DeskBox b){Close(x0,b.X0);Close(y0,b.Y0);Close(x1,b.X1);Close(y1,b.Y1);}
    [Fact] public void TowerBox_Open_IsBoundsOfMemberZonePointsPlusMargin_IgnoringNonMembers()
    {
        var l=Arranged(Gpu,Ram,Kb);l.Zones[Z(Gpu,0)]=new(0.2,0.3);l.Zones[Z(Gpu,1)]=new(0.4,0.1);l.Zones[Z(Ram,0)]=new(0.3,0.5);l.Zones[Z(Kb,0)]=new(0.9,0.95);
        Assert.DoesNotContain(Kb.Key,l.Tower.Members);
        Box(0.14,0.04,0.46,0.56,Scene(l,All,true).Tower);
    }
    [Fact] public void TowerBox_Open_SingleMemberHasArea()
    {
        var l=Arranged(Ram);l.Zones[Z(Ram,0)]=new(0.5,0.5);var s=Scene(l,new[]{Ram},true);
        Box(0.44,0.44,0.56,0.56,s.Tower);Assert.True(DeskGeometry.InTower(s,new(0.55,0.5)));Assert.False(DeskGeometry.InTower(s,new(0.57,0.5)));
    }
    [Fact] public void TowerBox_Open_MarginIsClampedToTheSquare()
    {
        var a=Dev("a|1","Ram a");var b=Dev("b|1","Ram b");var l=Arranged(a,b);l.Zones[Z(a,0)]=new(0.02,0.5);l.Zones[Z(b,0)]=new(0.3,0.9);
        Box(0,0.44,0.36,0.96,Scene(l,new[]{a,b},true).Tower);
    }
    [Fact] public void TowerBox_Open_IgnoresZonesWithoutLeds()
    {
        var d=DevZ("m|1","Ram m",(0,4),(1,0));var l=Arranged(d);l.Zones[Z(d,0)]=new(0.5,0.5);l.Zones[Z(d,1)]=new(0.9,0.9);
        Box(0.44,0.44,0.56,0.56,Scene(l,new[]{d},true).Tower);
    }
    static DeviceView DevZ(string key,string name,params (int Index,int Leds)[] zones)=>new(key,name,0,PaintKind.Direct,null,null,false,zones.Select(z=>new ZoneView(LightingKeys.Zone(key,z.Index),z.Index,$"{name} z{z.Index}",z.Leds,"#000000",false)).ToList());
    [Fact] public void Scene_ZoneWithoutLeds_IsNotInZoneKeysNorSplitItems()
    {
        var d=DevZ("m|1","Apex m",(0,4),(1,0));var l=Arranged(d);l.Zones[Z(d,1)]=new(0.9,0.9);
        var s=Scene(l,new[]{d});var it=Assert.Single(s.Items,i=>i.Kind==DeskItemKind.Device);
        Assert.Equal(new[]{Z(d,0)},it.ZoneKeys);
        var sp=Scene(l,new[]{d},false,new HashSet<string>{d.Key});
        Assert.Equal(new[]{Z(d,0)},sp.Items.Where(i=>i.Kind==DeskItemKind.Zone).Select(i=>i.ZoneKey));
    }
    [Fact] public void Scene_ZoneZeroWithoutPoint_DeviceItemSitsAtZoneOne()
    {
        var d=Dev("m|1","Apex m",2);var l=Arranged(d);l.Zones.Remove(Z(d,0));
        var it=Assert.Single(Scene(l,new[]{d}).Items,i=>i.Kind==DeskItemKind.Device);
        Assert.Equal(l.Zones[Z(d,1)],it.At);Assert.Equal(new[]{Z(d,1)},it.ZoneKeys);
    }
    [Fact] public void Scene_DeviceBothUnplacedAndMember_CountsZeroAndHasNoItem()
    {
        var l=Arranged(Gpu);l.Unplaced.Add(Gpu.Key);Assert.Contains(Gpu.Key,l.Tower.Members);
        var s=Scene(l,new[]{Gpu},true);
        Assert.Equal(0,s.TowerDevices);Assert.DoesNotContain(s.Items,i=>i.DeviceKey==Gpu.Key);Assert.Equal(new[]{Gpu.Key},s.NotPlaced.Select(d=>d.Key));Assert.Equal("Not placed",DeskGeometry.Where(l,Gpu));
    }
    [Fact] public void MoveDevice_SnapRounding_BelowHalfStaysAndExactHalfGoesUp()
    {
        var l=Arranged(Ram);l.Zones[Z(Ram,0)]=new(0.5,0.5);
        Close(24.0/48,DeskGeometry.MoveDevice(l,new[]{Ram},Ram.Key,0.004,0).Zones[Z(Ram,0)].X);
        Close(25.0/48,DeskGeometry.MoveDevice(l,new[]{Ram},Ram.Key,1.0/96,0).Zones[Z(Ram,0)].X);
        Close(24.0/48,DeskGeometry.MoveDevice(l,new[]{Ram},Ram.Key,-0.004,0).Zones[Z(Ram,0)].X);
    }
    [Fact] public void Hit_OpenTower_DeviceInsideTheBoxBeatsTheTower()
    {
        var l=Arranged(Ram);l.Zones[Z(Ram,0)]=new(0.5,0.5);var s=Scene(l,new[]{Ram},true);
        Assert.True(DeskGeometry.InTower(s,new(0.5,0.5)));
        Assert.Equal(DeskItemKind.Device,DeskGeometry.Hit(s,new(0.5,0.5),0.02,0.02)!.Kind);
        Assert.Equal(DeskItemKind.Tower,DeskGeometry.Hit(s,new(0.55,0.5),0.02,0.02)!.Kind);
    }
    [Fact] public void TowerBox_OpenWithNoMembers_IsTheClosedBox()
    {
        var l=Arranged(Kb);var c=Scene(l,new[]{Kb},false).Tower;
        Assert.Equal(c,Scene(l,new[]{Kb},true).Tower);
    }

    [Fact] public void Hit_PointInsideDeviceHalfExtents_ReturnsIt()
    {
        var l=Arranged(Kb);var s=Scene(l,new[]{Kb});var p=l.Zones[Z(Kb,0)];
        Assert.Equal("k|1",DeskGeometry.Hit(s,new(p.X+0.04,p.Y-0.02),0.05,0.03)!.DeviceKey);
        Assert.Null(DeskGeometry.Hit(s,new(p.X+0.06,p.Y),0.05,0.03));
    }
    [Fact] public void Hit_ZoneDotBeatsDeviceUnderIt()
    {
        var l=Arranged(Kb);var s=Scene(l,new[]{Kb});var p=l.Zones[Z(Kb,0)];
        var devOnly=DeskGeometry.Hit(s,p,0.05,0.05)!;Assert.Equal(DeskItemKind.Device,devOnly.Kind);
        var scene=new DeskScene(new[]{new DeskItem(DeskItemKind.Zone,Kb.Key,Z(Kb,0),p,"z",false,new[]{Z(Kb,0)})}.Concat(s.Items).ToList(),s.Tower,0,s.NotPlaced);
        Assert.Equal(DeskItemKind.Zone,DeskGeometry.Hit(scene,p,0.05,0.05)!.Kind);
    }
    [Fact] public void Hit_SplitDeviceWinsZoneDotUnderPointer()
    {
        var l=Arranged(Kb);var s=Scene(l,new[]{Kb},false,new HashSet<string>{Kb.Key});var p=l.Zones[Z(Kb,0)];
        Assert.Equal(Z(Kb,0),DeskGeometry.Hit(s,p,0.02,0.02)!.ZoneKey);
    }
    [Fact] public void Hit_TowerIsHitByItsBox_AndMissIsNull()
    {
        var s=Scene(DeskLayouts.Empty(),new DeviceView[0]);
        Assert.Equal(DeskItemKind.Tower,DeskGeometry.Hit(s,new(0.19,0.35),0.001,0.001)!.Kind);
        Assert.Null(DeskGeometry.Hit(s,new(0.9,0.9),0.02,0.02));
        Assert.True(DeskGeometry.InTower(s,new(0.14,0.3)));Assert.False(DeskGeometry.InTower(s,new(0.21,0.3)));
    }

    [Fact] public void MoveDevice_ShiftsAllPointsAndSnapsToFortyEighths()
    {
        var l=Arranged(Gpu);l.Zones[Z(Gpu,0)]=new(0.5,0.5);l.Zones[Z(Gpu,1)]=new(0.25,0.75);
        var n=DeskGeometry.MoveDevice(l,new[]{Gpu},Gpu.Key,0.013,0);
        Close(25.0/48,n.Zones[Z(Gpu,0)].X);Close(0.5,n.Zones[Z(Gpu,0)].Y);Close(13.0/48,n.Zones[Z(Gpu,1)].X);Close(0.75,n.Zones[Z(Gpu,1)].Y);
    }
    [Fact] public void MoveDevice_ClampsToTheSquare_AndLeavesOtherDevices()
    {
        var l=Arranged(Gpu,Kb);var kb=l.Zones[Z(Kb,0)];l.Zones[Z(Gpu,0)]=new(0.9,0.1);
        var n=DeskGeometry.MoveDevice(l,new[]{Gpu,Kb},Gpu.Key,0.5,-0.5);
        Assert.Equal(new DeskPoint(1,0),n.Zones[Z(Gpu,0)]);Assert.Equal(kb,n.Zones[Z(Kb,0)]);Assert.Equal(l.Tower.At,n.Tower.At);
    }
    [Fact] public void MoveZone_MovesOnlyThatZone()
    {
        var l=Arranged(Gpu);l.Zones[Z(Gpu,0)]=new(0.5,0.5);var other=l.Zones[Z(Gpu,1)];
        var n=DeskGeometry.MoveZone(l,Z(Gpu,0),0,0.013);
        Close(0.5,n.Zones[Z(Gpu,0)].X);Close(25.0/48,n.Zones[Z(Gpu,0)].Y);Assert.Equal(other,n.Zones[Z(Gpu,1)]);
        Assert.Equal(l.Zones.Count,DeskGeometry.MoveZone(l,"nope#0",0.1,0.1).Zones.Count);
    }
    [Fact] public void MoveTower_MovesAnchorAndMembersOnly()
    {
        var l=Arranged(Gpu,Kb);l.Zones[Z(Gpu,0)]=new(0.5,0.5);l.Zones[Z(Gpu,1)]=new(0.25,0.25);var kb=l.Zones[Z(Kb,0)];l.Tower.At=new(0.5,0.5);
        var n=DeskGeometry.MoveTower(l,new[]{Gpu,Kb},0.1,0);
        Close(29.0/48,n.Tower.At.X);Close(0.5,n.Tower.At.Y);Close(29.0/48,n.Zones[Z(Gpu,0)].X);Close(17.0/48,n.Zones[Z(Gpu,1)].X);Close(0.25,n.Zones[Z(Gpu,1)].Y);
        Assert.Equal(kb,n.Zones[Z(Kb,0)]);
    }
    [Fact] public void SetInTower_False_RemovesMemberAndExcludes_TrueReverses_RepeatsChangeNothing()
    {
        var l=Arranged(Gpu);
        var o=DeskGeometry.SetInTower(l,Gpu.Key,false);
        Assert.DoesNotContain(Gpu.Key,o.Tower.Members);Assert.Equal(new[]{Gpu.Key},o.Tower.Excluded);
        var o2=DeskGeometry.SetInTower(o,Gpu.Key,false);Assert.Empty(o2.Tower.Members);Assert.Equal(new[]{Gpu.Key},o2.Tower.Excluded);
        var i=DeskGeometry.SetInTower(o,Gpu.Key,true);
        Assert.Equal(new[]{Gpu.Key},i.Tower.Members);Assert.Empty(i.Tower.Excluded);
        var i2=DeskGeometry.SetInTower(i,Gpu.Key,true);Assert.Equal(new[]{Gpu.Key},i2.Tower.Members);Assert.Empty(i2.Tower.Excluded);
    }
    [Fact] public void SetUnplaced_True_AddsKeyRemovesPointsAndMembership()
    {
        var l=Arranged(Gpu,Kb);var n=DeskGeometry.SetUnplaced(l,new[]{Gpu,Kb},Gpu.Key,true);
        Assert.Equal(new[]{Gpu.Key},n.Unplaced);Assert.False(n.Zones.ContainsKey(Z(Gpu,0)));Assert.False(n.Zones.ContainsKey(Z(Gpu,1)));Assert.True(n.Zones.ContainsKey(Z(Kb,0)));Assert.DoesNotContain(Gpu.Key,n.Tower.Members);
        Assert.Equal(new[]{Gpu.Key},DeskGeometry.SetUnplaced(n,new[]{Gpu,Kb},Gpu.Key,true).Unplaced);
    }
    [Fact] public void SetUnplaced_False_RemovesKeyAndArrangesAgain()
    {
        var l=DeskGeometry.SetUnplaced(Arranged(Gpu),new[]{Gpu},Gpu.Key,true);
        var n=DeskGeometry.SetUnplaced(l,new[]{Gpu},Gpu.Key,false);
        Assert.Empty(n.Unplaced);Assert.True(n.Zones.ContainsKey(Z(Gpu,0)));Assert.True(n.Zones.ContainsKey(Z(Gpu,1)));Assert.Contains(Gpu.Key,n.Tower.Members);
    }
    [Fact] public void WithShape_ReplacesOnlyDesk()
    {
        var l=Arranged(Gpu,Kb);var n=DeskGeometry.WithShape(l,DeskShape.Straight);
        Assert.Equal(DeskShape.Straight,DeskLayouts.ShapeOf(n));Assert.Equal(l.Zones,n.Zones);Assert.Equal(l.Tower.Members,n.Tower.Members);Assert.Equal(l.Tower.At,n.Tower.At);Assert.Equal(l.Unplaced,n.Unplaced);
    }
    [Fact] public void Reset_KeepsDeskAndEqualsFreshArrange()
    {
        var l=DeskGeometry.WithShape(Arranged(Gpu,Kb),DeskShape.LRight);l.Tower.At=new(0.7,0.7);l.Tower.Excluded.Add(Gpu.Key);l.Unplaced.Add(Ram.Key);l.Zones[Z(Kb,0)]=new(0.9,0.9);
        var n=DeskGeometry.Reset(l,All);
        Assert.Equal(DeskShape.LRight,DeskLayouts.ShapeOf(n));Assert.Equal(new DeskPoint(0.14,0.3),n.Tower.At);Assert.Empty(n.Tower.Excluded);Assert.Empty(n.Unplaced);
        var fresh=DeskLayouts.Empty();fresh.Desk=DeskLayouts.Preset(DeskShape.LRight);var exp=DeskLayouts.AutoArrange(fresh,DeskLayouts.Arrangeable(All));
        Assert.Equal(exp.Zones,n.Zones);Assert.Equal(exp.Tower.Members,n.Tower.Members);
        Assert.Contains(Ram.Key,n.Tower.Members);Assert.Contains(Gpu.Key,n.Tower.Members);
    }
    [Fact] public void Where_PicksOwnEffectThenNotPlacedThenInsidePcThenOnDesk()
    {
        var hw=Dev("h|1","Radeon Hw",kind:PaintKind.Hardware);var l=Arranged(Gpu,Kb);l.Unplaced.Add(hw.Key);l.Tower.Members.Add(hw.Key);
        Assert.Equal("Own effect",DeskGeometry.Where(l,hw));
        Assert.Equal("Inside PC",DeskGeometry.Where(l,Gpu));Assert.Equal("On desk",DeskGeometry.Where(l,Kb));
        l.Unplaced.Add(Kb.Key);Assert.Equal("Not placed",DeskGeometry.Where(l,Kb));
    }
    [Fact] public void NoFunction_MutatesItsInput()
    {
        var l=Arranged(Gpu,Kb);l.Zones[Z(Gpu,0)]=new(0.5,0.5);var count=l.Zones.Count;var d=new[]{Gpu,Kb};
        void Check(){Assert.Equal(count,l.Zones.Count);Assert.Equal(new DeskPoint(0.5,0.5),l.Zones[Z(Gpu,0)]);Assert.Equal(new[]{Gpu.Key},l.Tower.Members);Assert.Empty(l.Unplaced);Assert.Empty(l.Tower.Excluded);Assert.Equal(new DeskPoint(0.14,0.3),l.Tower.At);Assert.Equal(DeskShape.LLeft,DeskLayouts.ShapeOf(l));}
        DeskGeometry.MoveDevice(l,d,Gpu.Key,0.2,0.2);Check();DeskGeometry.MoveZone(l,Z(Gpu,0),0.2,0.2);Check();DeskGeometry.MoveTower(l,d,0.2,0.2);Check();
        DeskGeometry.SetInTower(l,Gpu.Key,false);Check();DeskGeometry.SetInTower(l,Gpu.Key,true);Check();DeskGeometry.SetInTower(l,Kb.Key,true);Check();DeskGeometry.SetUnplaced(l,d,Gpu.Key,false);Check();DeskGeometry.SetUnplaced(l,d,Gpu.Key,true);Check();DeskGeometry.WithShape(l,DeskShape.Straight);Check();DeskGeometry.Reset(l,d);Check();Scene(l,d,true);Check();
    }
}
