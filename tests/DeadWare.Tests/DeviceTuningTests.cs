using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class DeviceTuningTests
{
    static LightingSettings With(DeviceTuning? t,string key="k"){var s=new LightingSettings();s.Devices[key]=t!;return s;}

    [Fact]
    public void Normalize_drops_defaults_and_empty_entries()
    {
        var s=new LightingSettings();
        s.Devices["a"]=new DeviceTuning{Brightness=100,Correction=new()};
        s.Devices["b"]=new DeviceTuning{Brightness=70,Correction=new()};
        DeviceTunings.Normalize(s);
        Assert.DoesNotContain("a",s.Devices.Keys);
        Assert.Equal(70,s.Devices["b"].Brightness);Assert.Null(s.Devices["b"].Correction);
    }
    [Fact]
    public void Normalize_leaves_a_null_entry_for_Validate()
    {
        var s=With(null);
        DeviceTunings.Normalize(s);
        Assert.True(s.Devices.ContainsKey("k"));Assert.NotNull(DeviceTunings.Validate(s));
    }
    [Fact]
    public void Validate_bounds()
    {
        Assert.Equal("Device brightness must be 0 to 100.",DeviceTunings.Validate(With(new(){Brightness=-1})));
        Assert.Equal("Device brightness must be 0 to 100.",DeviceTunings.Validate(With(new(){Brightness=101})));
        Assert.Equal("Colour correction must be 0 to 200.",DeviceTunings.Validate(With(new(){Correction=new(){R=-1}})));
        Assert.Equal("Colour correction must be 0 to 200.",DeviceTunings.Validate(With(new(){Correction=new(){R=201}})));
        Assert.Equal("Colour correction must be 0 to 200.",DeviceTunings.Validate(With(new(){Correction=new(){G=-1}})));
        Assert.Equal("Colour correction must be 0 to 200.",DeviceTunings.Validate(With(new(){Correction=new(){B=201}})));
        Assert.Equal("Mode speed must be 0 to 100.",DeviceTunings.Validate(With(new(){Mode=new(){Speed=-1}})));
        Assert.Equal("Mode speed must be 0 to 100.",DeviceTunings.Validate(With(new(){Mode=new(){Speed=101}})));
        Assert.Equal("Lighting mode is not known.",DeviceTunings.Validate(With(new(){Mode=new(){Kind="rainbow"}})));
        Assert.NotNull(DeviceTunings.Validate(With(null)));
        Assert.NotNull(DeviceTunings.Validate(With(new(){Brightness=5},new string('k',LightingSettingsCodec.MaxKeyLength+1))));
        var many=new LightingSettings();
        for(int i=0;i<257;i++) many.Devices["d"+i]=new(){Brightness=5};
        Assert.Equal("Too many device entries.",DeviceTunings.Validate(many));
        many.Devices.Remove("d0");
        Assert.Null(DeviceTunings.Validate(many));
        Assert.Null(DeviceTunings.Validate(With(new(){Brightness=0,Correction=new(){R=0,G=100,B=200},Mode=new(){Kind="wave",Speed=100}})));
        Assert.Null(DeviceTunings.Validate(With(new(){Brightness=100})));
    }
    [Fact]
    public void Validate_does_not_change_the_settings()
    {
        var s=With(new(){Brightness=100});s.Version=1;
        DeviceTunings.Validate(s);
        Assert.Equal(1,s.Version);Assert.Equal(100,s.Devices["k"].Brightness);
    }
    [Fact]
    public void Clone_is_deep()
    {
        var s=With(new(){Brightness=70,Correction=new(){G=88},Mode=new(){Kind="wave",Speed=55}});
        var c=DeviceTunings.Clone(s.Devices);
        s.Devices["k"].Correction!.G=1;s.Devices["k"].Mode!.Speed=1;
        Assert.Equal(88,c["k"].Correction!.G);Assert.Equal(55,c["k"].Mode!.Speed);Assert.Equal(70,c["k"].Brightness);
    }
    [Fact]
    public void WrittenVersion_is_2_only_with_entries()
    {
        var s=new LightingSettings();
        Assert.Equal(1,DeviceTunings.WrittenVersion(s));
        s.Devices["k"]=new(){Brightness=5};
        Assert.Equal(2,DeviceTunings.WrittenVersion(s));
        s.Version=4;
        Assert.Equal(4,DeviceTunings.WrittenVersion(s));
    }

    static DeviceLook Lk(string name)=>new(){Name=name,Keys=new(){[1]=new(){Name="W",Color="#010203"}}};
    [Fact]
    public void Normalize_drops_empty_paint_layers_and_looks()
    {
        var s=With(new DeviceTuning{Brightness=70,Keys=new(),Layers=[],Looks=[],Look="Gone"});
        DeviceTunings.Normalize(s);
        var t=s.Devices["k"];
        Assert.Null(t.Keys);Assert.Null(t.Layers);Assert.Null(t.Looks);Assert.Null(t.Look);Assert.Equal(70,t.Brightness);
        s=With(new DeviceTuning{Keys=new(),Layers=[],Looks=[],Look="Gone"});
        DeviceTunings.Normalize(s);
        Assert.Empty(s.Devices);
        s=With(new DeviceTuning{Keys=new(){[1]=new(){Name="W",Color="#010203"}}});
        DeviceTunings.Normalize(s);
        Assert.Single(s.Devices);Assert.Single(s.Devices["k"].Keys!);
        s=With(new DeviceTuning{Looks=[Lk("Gaming")],Look="gaming"});
        DeviceTunings.Normalize(s);
        Assert.Equal("gaming",s.Devices["k"].Look);
        s=With(new DeviceTuning{Layers=[new()],Looks=[Lk("A")],Look="B"});
        DeviceTunings.Normalize(s);
        Assert.Null(s.Devices["k"].Look);Assert.Single(s.Devices["k"].Layers!);
    }
    [Fact]
    public void Validate_looks()
    {
        string? V(params DeviceLook[] looks)=>DeviceTunings.Validate(With(new DeviceTuning{Looks=looks.ToList()}));
        Assert.Null(V(Enumerable.Range(0,12).Select(i=>Lk("L"+i)).ToArray()));
        Assert.Equal("A keyboard can have up to 12 saved looks.",V(Enumerable.Range(0,13).Select(i=>Lk("L"+i)).ToArray()));
        Assert.Null(V(Lk(new('n',40))));
        Assert.Equal("A look's name must be 1 to 40 characters.",V(Lk("")));
        Assert.Equal("A look's name must be 1 to 40 characters.",V(Lk(new('n',41))));
        Assert.Equal("A look's name must be 1 to 40 characters.",V(Lk("   ")));
        Assert.Equal("Two looks have the same name.",V(Lk("Gaming"),Lk("gaming")));
        var bad=Lk("Bad");bad.Layers=Enumerable.Range(0,9).Select(_=>new DeviceLayer()).ToList();
        Assert.Equal("A keyboard can have up to 8 layers.",V(bad));
        Assert.Equal("A keyboard can have up to 8 layers.",DeviceTunings.Validate(With(new DeviceTuning{Layers=bad.Layers})));
        Assert.Equal("A colour is invalid.",DeviceTunings.Validate(With(new DeviceTuning{Keys=new(){[1]=new(){Name="W",Color="x"}}})));
        Assert.Null(DeviceTunings.Validate(With(new DeviceTuning{Look="Anything"})));
    }
    [Fact]
    public void Clone_copies_paint_layers_and_looks_deeply()
    {
        PaintedKey Pk(string n,string c)=>new(){Name=n,Color=c};
        DeviceLayer Ly(int k)=>new(){Kind="ripple",Color="#010203",Effect="spectrum",Speed=11+k,DurationMs=700+k,Strength=55+k,Mix="cover",Lights=[new(){Index=4+k,Name="Q"+k}]};
        var s=With(new DeviceTuning
        {
            Brightness=70,Correction=new(){R=90,G=88,B=112},Mode=new(){Kind="wave",Speed=55},
            Keys=new(){[2]=Pk("A","#0A0B0C")},Layers=[Ly(0)],Look="Gaming",
            Looks=[new(){Name="Gaming",Mode=new(){Kind="cycle",Speed=9},Keys=new(){[5]=Pk("B","#0D0E0F")},Layers=[Ly(1)]}],
        });
        var json=System.Text.Json.JsonSerializer.Serialize(s.Devices);
        var c=DeviceTunings.Clone(s.Devices);
        Assert.Equal(json,System.Text.Json.JsonSerializer.Serialize(c));
        var o=s.Devices["k"];
        o.Brightness=1;o.Correction!.R=1;o.Mode!.Kind="static";o.Look="X";
        o.Keys![2].Name="Z";o.Keys[2].Color="#FFFFFF";o.Keys[9]=Pk("N","#000000");
        o.Layers![0].Kind="color";o.Layers[0].Color="#FFFFFF";o.Layers[0].Effect="wave";o.Layers[0].Speed=1;o.Layers[0].DurationMs=1;o.Layers[0].Strength=1;o.Layers[0].Mix="add";
        o.Layers[0].Lights![0].Index=99;o.Layers[0].Lights![0].Name="Z";o.Layers[0].Lights!.Add(new());o.Layers.Add(new());
        var lk=o.Looks![0];lk.Name="Other";lk.Mode!.Speed=1;lk.Keys![5].Color="#FFFFFF";lk.Keys[8]=Pk("N","#000000");
        lk.Layers![0].Lights![0].Name="Z";lk.Layers[0].Strength=2;lk.Layers.Add(new());o.Looks.Add(new());
        Assert.Equal(json,System.Text.Json.JsonSerializer.Serialize(c));
        // and the other way: changing the clone leaves the original
        var json2=System.Text.Json.JsonSerializer.Serialize(s.Devices);
        var t=c["k"];t.Keys![2].Name="Q";t.Layers![0].Lights![0].Name="Q";t.Looks![0].Keys![5].Name="Q";t.Looks[0].Layers![0].Lights![0].Index=77;
        Assert.Equal(json2,System.Text.Json.JsonSerializer.Serialize(s.Devices));
    }
    [Fact]
    public void WrittenVersion_is_3_only_with_paint_layers_or_looks()
    {
        var s=new LightingSettings();
        Assert.Equal(1,DeviceTunings.WrittenVersion(s));
        s.Devices["k"]=new(){Brightness=5};
        Assert.Equal(2,DeviceTunings.WrittenVersion(s));
        s.Devices["k"]=new(){Keys=new(){[1]=new(){Name="W",Color="#010203"}}};
        Assert.Equal(3,DeviceTunings.WrittenVersion(s));
        s.Devices["k"]=new(){Layers=[new()]};
        Assert.Equal(3,DeviceTunings.WrittenVersion(s));
        s.Devices["k"]=new(){Looks=[Lk("A")]};
        Assert.Equal(3,DeviceTunings.WrittenVersion(s));
        s.Devices["k"]=new(){Look="A"};
        Assert.Equal(3,DeviceTunings.WrittenVersion(s));
        s.Version=4;
        Assert.Equal(4,DeviceTunings.WrittenVersion(s));
        s.Version=1;s.Devices.Clear();
        Assert.Equal(1,DeviceTunings.WrittenVersion(s));
    }
}
