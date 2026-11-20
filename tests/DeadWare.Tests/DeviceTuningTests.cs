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
        s.Version=3;
        Assert.Equal(3,DeviceTunings.WrittenVersion(s));
    }
}
