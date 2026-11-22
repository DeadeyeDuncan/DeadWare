using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class DeviceLayersTests
{
    static DeviceLook Look(string name="Gaming")=>new(){Name=name,Mode=new(){Kind="wave",Speed=50},Keys=new(){[3]=new(){Name="W",Color="#112233"}},Layers=[new(){Kind="fade",Lights=[new(){Index=3,Name="W"}]}]};
    static DeviceTuning TunedLike(DeviceLook l)=>new(){Mode=new(){Kind=l.Mode!.Kind,Speed=l.Mode.Speed},Keys=new(){[3]=new(){Name="W",Color="#112233"}},Layers=[new(){Kind="fade",Lights=[new(){Index=3,Name="W"}]}],Look=l.Name};
    static List<DeviceLayer> One(Action<DeviceLayer> edit){var l=new DeviceLayer();edit(l);return [l];}

    [Fact]
    public void Same_treats_null_and_empty_alike_and_compares_by_value()
    {
        var look=Look();var t=TunedLike(look);
        Assert.True(DeviceLayers.Same(t,look));
        var bare=new DeviceLook{Name="Bare"};
        Assert.True(DeviceLayers.Same(new DeviceTuning{Keys=new(),Layers=[]},bare));
        Assert.True(DeviceLayers.Same(new DeviceTuning(),bare));
        t.Keys![3].Color="#112234";Assert.False(DeviceLayers.Same(t,look));
        t=TunedLike(look);t.Mode!.Speed=51;Assert.False(DeviceLayers.Same(t,look));
        t=TunedLike(look);t.Mode=null;Assert.False(DeviceLayers.Same(t,look));
        t=TunedLike(look);t.Layers![0].Strength=99;Assert.False(DeviceLayers.Same(t,look));
        t=TunedLike(look);t.Layers![0].Lights![0].Name="X";Assert.False(DeviceLayers.Same(t,look));
        // a layer's null Lights is every key and an empty one is none: not equal
        t=TunedLike(look);t.Layers![0].Lights=null;Assert.False(DeviceLayers.Same(t,look));
        t=TunedLike(look);t.Layers![0].Lights=[];Assert.False(DeviceLayers.Same(t,look));
        var all=Look();all.Layers![0].Lights=null;var none=TunedLike(all);none.Layers![0].Lights=[];
        Assert.False(DeviceLayers.Same(none,all));
        var allT=TunedLike(all);allT.Layers![0].Lights=null;Assert.True(DeviceLayers.Same(allT,all));
    }
    [Fact]
    public void Changed_only_when_a_saved_look_is_named_and_differs()
    {
        var look=Look();var t=TunedLike(look);t.Looks=[look];
        Assert.False(DeviceLayers.Changed(t));
        t.Keys![3].Color="#000001";Assert.True(DeviceLayers.Changed(t));
        t.Look="Nope";Assert.False(DeviceLayers.Changed(t));
        t.Look=null;Assert.False(DeviceLayers.Changed(t));
        Assert.False(DeviceLayers.Changed(new DeviceTuning()));
    }
    [Fact]
    public void Find_ignores_case_and_outer_spaces()
    {
        var t=new DeviceTuning{Looks=[Look("Gaming"),Look("Work")]};
        Assert.Same(t.Looks[0],DeviceLayers.Find(t,"  gAMING "));
        Assert.Same(t.Looks[1],DeviceLayers.Find(t,"work"));
        Assert.Null(DeviceLayers.Find(t,"Film"));
        Assert.Null(DeviceLayers.Find(new DeviceTuning(),"Gaming"));
    }
    [Fact]
    public void CheckNew_refuses_unknown_kind_effect_and_mix()
    {
        const string said="That layer isn't of a kind DeadWare knows.";
        Assert.Null(DeviceLayers.CheckNew(null));
        Assert.Null(DeviceLayers.CheckNew([new()]));
        Assert.Null(DeviceLayers.CheckNew(One(l=>{l.Kind="ripple";l.Effect="spectrum";l.Mix="cover";})));
        Assert.Equal(said,DeviceLayers.CheckNew(One(l=>l.Kind="morrowind")));
        Assert.Equal(said,DeviceLayers.CheckNew(One(l=>l.Effect="plasma")));
        Assert.Equal(said,DeviceLayers.CheckNew(One(l=>l.Mix="xor")));
    }
    [Fact]
    public void ValidateSetup_bounds()
    {
        string? V(List<DeviceLayer>? layers)=>DeviceLayers.ValidateSetup(null,null,layers);
        string? K(Dictionary<int,PaintedKey>? keys)=>DeviceLayers.ValidateSetup(null,keys,null);
        Assert.Null(DeviceLayers.ValidateSetup(null,null,null));
        Assert.Null(V(Enumerable.Range(0,8).Select(_=>new DeviceLayer()).ToList()));
        Assert.Equal("A keyboard can have up to 8 layers.",V(Enumerable.Range(0,9).Select(_=>new DeviceLayer()).ToList()));
        Assert.Null(K(Enumerable.Range(0,4096).ToDictionary(i=>i,_=>new PaintedKey{Name="k"})));
        Assert.Equal("Too many painted keys.",K(Enumerable.Range(0,4097).ToDictionary(i=>i,_=>new PaintedKey{Name="k"})));
        Assert.Equal("A painted key is invalid.",K(new(){[-1]=new(){Name="k"}}));
        Assert.Equal("A painted key is invalid.",K(new(){[1]=null!}));
        Assert.Null(K(new(){[1]=new(){Name=new('n',128)}}));
        Assert.Equal("A light's name must be 1 to 128 characters.",K(new(){[1]=new(){Name=""}}));
        Assert.Equal("A light's name must be 1 to 128 characters.",K(new(){[1]=new(){Name=new('n',129)}}));
        Assert.Equal("A light's name must be 1 to 128 characters.",K(new(){[1]=new(){Name=null!}}));
        Assert.Equal("A colour is invalid.",K(new(){[1]=new(){Name="k",Color="red"}}));
        Assert.Equal("A colour is invalid.",K(new(){[1]=new(){Name="k",Color=null!}}));
        Assert.Equal("A colour is invalid.",V(One(l=>l.Color="#12")));
        Assert.Null(V(One(l=>l.Strength=0)));Assert.Null(V(One(l=>l.Strength=100)));
        Assert.Equal("Layer strength must be 0 to 100.",V(One(l=>l.Strength=-1)));
        Assert.Equal("Layer strength must be 0 to 100.",V(One(l=>l.Strength=101)));
        Assert.Null(V(One(l=>l.DurationMs=100)));Assert.Null(V(One(l=>l.DurationMs=3000)));
        Assert.Equal("A layer must last 0.1 to 3 seconds.",V(One(l=>l.DurationMs=99)));
        Assert.Equal("A layer must last 0.1 to 3 seconds.",V(One(l=>l.DurationMs=3001)));
        Assert.Null(V(One(l=>l.Speed=0)));Assert.Null(V(One(l=>l.Speed=100)));
        Assert.Equal("Layer speed must be 0 to 100.",V(One(l=>l.Speed=-1)));
        Assert.Equal("Layer speed must be 0 to 100.",V(One(l=>l.Speed=101)));
        foreach(var bad in new Action<DeviceLayer>[]{l=>l.Kind=null!,l=>l.Effect=null!,l=>l.Mix=null!,l=>l.Kind=new('k',33),l=>l.Effect=new('k',33),l=>l.Mix=new('k',33)})
            Assert.Equal("A layer is invalid.",V(One(bad)));
        Assert.Null(V(One(l=>l.Kind=new('k',32))));
        Assert.Equal("A layer is invalid.",V([null!]));
        Assert.Null(V(One(l=>l.Lights=Enumerable.Range(0,4096).Select(i=>new LayerLight{Index=i,Name="k"}).ToList())));
        Assert.Equal("A layer's keys are invalid.",V(One(l=>l.Lights=Enumerable.Range(0,4097).Select(i=>new LayerLight{Index=i,Name="k"}).ToList())));
        Assert.Equal("A layer's keys are invalid.",V(One(l=>l.Lights=[new(){Index=-1,Name="k"}])));
        // an unknown but well-formed kind is kept in files
        Assert.Null(V(One(l=>l.Kind="morrowind")));
        Assert.Null(V(One(l=>{l.Effect="plasma";l.Mix="xor";})));
    }
}
