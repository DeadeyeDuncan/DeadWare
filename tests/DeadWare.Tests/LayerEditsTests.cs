using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class LayerEditsTests
{
    static DeviceLayer L(string kind,int strength=100,params int[] lights)=>new(){Kind=kind,Strength=strength,Lights=lights.Length==0?null:lights.Select(i=>new LayerLight{Index=i,Name="Key "+i}).ToList()};
    static string[] Kinds(List<DeviceLayer> l)=>l.Select(x=>x.Kind+x.Strength).ToArray();
    static List<DeviceLayer> Three()=>[L("color",10),L("effect",20),L("ripple",30)];

    [Fact] public void Add_appends_the_default_fade_and_stops_at_8()
    {
        var one=LayerEdits.Add(null);
        var d=Assert.Single(one);
        Assert.Equal(DeviceLayers.Fade,d.Kind);Assert.Equal("#38E9FF",d.Color);Assert.Equal(600,d.DurationMs);
        Assert.Equal(100,d.Strength);Assert.Equal(DeviceLayers.Add,d.Mix);Assert.Null(d.Lights);
        var two=LayerEdits.Add([L("color",5)]);
        Assert.Equal(["color5","fade100"],Kinds(two));
        var full=Enumerable.Range(0,8).Select(i=>L("color",i)).ToList();
        var still=LayerEdits.Add(full);
        Assert.Equal(8,still.Count);
        Assert.Equal(Kinds(full),Kinds(still));
        Assert.NotSame(full,still);
    }

    [Fact] public void Remove_and_Move_keep_the_rest_in_order()
    {
        Assert.Equal(["color10","ripple30"],Kinds(LayerEdits.Remove(Three(),1)));
        Assert.Equal(["effect20","ripple30"],Kinds(LayerEdits.Remove(Three(),0)));
        Assert.Equal(["color10","ripple30","effect20"],Kinds(LayerEdits.Move(Three(),1,+1)));
        Assert.Equal(["effect20","color10","ripple30"],Kinds(LayerEdits.Move(Three(),1,-1)));
        Assert.Equal(["color10","effect20","ripple30"],Kinds(LayerEdits.Remove(Three(),3)));
        Assert.Empty(LayerEdits.Remove(null,0));
        var w=LayerEdits.With(Three(),2,l=>l.Strength=77);
        Assert.Equal(["color10","effect20","ripple77"],Kinds(w));
        Assert.Equal(["color10","effect20","ripple30"],Kinds(LayerEdits.With(Three(),5,l=>l.Strength=77)));
    }

    [Fact] public void Move_at_an_end_changes_nothing()
    {
        Assert.Equal(["color10","effect20","ripple30"],Kinds(LayerEdits.Move(Three(),0,-1)));
        Assert.Equal(["color10","effect20","ripple30"],Kinds(LayerEdits.Move(Three(),2,+1)));
        Assert.Equal(["color10","effect20","ripple30"],Kinds(LayerEdits.Move(Three(),1,2)));
        Assert.Equal(["color10","effect20","ripple30"],Kinds(LayerEdits.Move(Three(),0,2)));
        Assert.Empty(LayerEdits.Move(null,0,1));
    }

    [Fact] public void Edits_never_change_their_input()
    {
        var input=new List<DeviceLayer>{L("color",10,1,2),L("fade",20),L("ripple",30,4)};
        var before=DeviceLayers.Clone(input)!;
        void Unchanged()
        {
            Assert.Equal(3,input.Count);
            Assert.True(DeviceLayers.Same(new DeviceTuning{Layers=input},new DeviceLook{Layers=before}));
        }
        var added=LayerEdits.Add(input);Unchanged();
        var removed=LayerEdits.Remove(input,0);Unchanged();
        var moved=LayerEdits.Move(input,0,+1);Unchanged();
        var with=LayerEdits.With(input,0,l=>{l.Strength=99;l.Lights!.Add(new LayerLight{Index=9,Name="Key 9"});l.Lights[0].Name="changed";});Unchanged();
        Assert.Equal(2,input[0].Lights!.Count);Assert.Equal("Key 1",input[0].Lights![0].Name);
        // Each result is a deep copy: changing it leaves the input alone too.
        foreach(var r in new[]{added,removed,moved,with})
        {
            foreach(var l in r){Assert.DoesNotContain(l,input);if(l.Lights is{} g) foreach(var x in g) Assert.DoesNotContain(x,input.SelectMany(i=>i.Lights??[]));}
            r[0].Strength=1;r.Add(new DeviceLayer());
            Unchanged();
        }
    }

    [Fact] public void Titles()
    {
        Assert.Equal("Key ripple · all keys",LayerEdits.Title(L("ripple")));
        Assert.Equal("Key fade · all keys",LayerEdits.Title(L("fade")));
        Assert.Equal("A colour · 4 keys",LayerEdits.Title(L("color",100,1,2,3,4)));
        Assert.Equal("A colour · 1 key",LayerEdits.Title(L("color",100,7)));
        Assert.Equal("An effect · no keys",LayerEdits.Title(new DeviceLayer{Kind="effect",Lights=[]}));
        Assert.Equal("A layer this version doesn't know",LayerEdits.Title(L("morrowind")));
        Assert.Equal("A layer this version doesn't know",LayerEdits.Title(new DeviceLayer{Kind="morrowind",Lights=[]}));
    }

    [Fact] public void Reacts_only_for_fade_and_ripple()
    {
        Assert.False(LayerEdits.Reacts(null));
        Assert.False(LayerEdits.Reacts([]));
        Assert.False(LayerEdits.Reacts([L("color"),L("effect"),L("morrowind")]));
        Assert.True(LayerEdits.Reacts([L("color"),L("fade")]));
        Assert.True(LayerEdits.Reacts([L("ripple")]));
    }
}
