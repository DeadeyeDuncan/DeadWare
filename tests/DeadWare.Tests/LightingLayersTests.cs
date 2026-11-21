using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

// Lighting 3b, the paint stage: key paint then layers carried as fractions to one rounding. DeadLight's own compose cases
// (tests/painter-lighting.test.ts) are ported with the same inputs and expected values.
public class LightingLayersTests
{
    private const string K="kb|1";
    private static readonly Rgb Base=new(101,51,21),Red=new(200,0,0);
    private static LightingDevice Kb(string[]? names=null,int width=2,uint[]? cells=null,string[]? modes=null,int type=5)=>
        new(K,"Keyboard",type,[new LightingZone(0,"Keys",names?.Length??2,new ZoneMatrix(width,1,cells??[0,1]))],modes??["Direct"],0,names??["Key: A","Key: B"]);
    private readonly LightingSettings s=new();
    public LightingLayersTests(){s.ZoneColors[LightingKeys.Zone(K,0)]=new ZoneColorEntry{Color="#653315",ZoneName="Keys"};}
    private DeviceTuning T()=>s.Devices.TryGetValue(K,out var t)?t:s.Devices[K]=new DeviceTuning();
    private static DeviceLayer L(string kind,string color="#C80000",int strength=100,string mix="cover",int duration=1000,string effect="wave",List<LayerLight>? lights=null)=>
        new(){Kind=kind,Color=color,Strength=strength,Mix=mix,DurationMs=duration,Effect=effect,Lights=lights};
    private DeviceLayer Add(DeviceLayer l){(T().Layers??=[]).Add(l);return l;}
    private static LightPress Press(double at,params int[] lights)=>new(K,lights,at);
    private Rgb[] Painted(LightingDevice d,double t=1000,params LightPress[] presses)=>LightingPaint.Paint(s,d,t,presses).Colors;

    [Fact] public void The_DeadLight_compose_case()
    {
        s.Brightness=50;
        var t=T();t.Brightness=50;t.Correction=new(){R=200,G=100,B=50};
        t.Keys=new(){[0]=new(){Name="Key: A",Color="#C80000"}};
        Add(L("color","#646464",50,"cover"));
        Add(L("color","#141414",50,"add",lights:[new(){Index=0,Name="Key: A"}]));
        Assert.Equal([new Rgb(64,15,8),new Rgb(50,19,8)],Painted(Kb()));
    }
    [Fact] public void Layers_are_not_clamped_between_them()
    {
        s.ZoneColors[LightingKeys.Zone(K,0)]=new ZoneColorEntry{Color="#C8C8C8",ZoneName="Keys"};
        Add(L("color","#FFFFFF",100,"add"));Add(L("color","#000000",50,"cover"));
        Assert.All(Painted(Kb()),c=>Assert.Equal(new Rgb(228,228,228),c));   // (200+255)*0.5, not 255*0.5
    }
    [Fact] public void TuneExact_equals_Tune_for_whole_numbers()
    {
        var tunings=new DeviceTuning?[]{null,new(){Brightness=37},new(){Correction=new(){R=200,G=100,B=50}},new(){Brightness=70,Correction=new(){R=88,G=112,B=100}}};
        foreach(var t in tunings)
            foreach(var page in new[]{0,33,50,100})
                for(var v=0;v<=255;v++)
                {
                    var c=new Rgb((byte)v,(byte)(255-v),(byte)((v*7)%256));
                    var e=LightingLayers.TuneExact(c.R,c.G,c.B,t,page);
                    if(e!=LightingPaint.Tune(c,t,page)) Assert.Fail($"v {v}, page {page}, tuning {t?.Brightness}: {e} is not {LightingPaint.Tune(c,t,page)}");
                }
    }
    [Fact] public void Round_clamps_and_rounds_half_away_from_zero()
    {
        Assert.Equal(new Rgb(0,255,3),LightingLayers.Round(-4.2,300,2.5));
        Assert.Equal(new Rgb(1,2,0),LightingLayers.Round(0.5,1.5,0.49));
    }
    [Fact] public void Nothing_painted_is_the_byte_path()
    {
        var kb=Kb();var f=new Rgb[]{Base,Base};
        s.Devices[K]=new DeviceTuning{Brightness=50,Correction=new(){R=150}};
        Assert.Null(LightingLayers.Compose(s,kb,f,0,null));Assert.False(LightingLayers.Active(s,kb));
        Assert.Equal(new[]{Base,Base}.Select(c=>LightingPaint.Tune(c,s.Devices[K],s.Brightness)),Painted(kb,0));
        T().Keys=new(){[0]=new(){Name="Key: A",Color="#C80000"}};
        Assert.Null(LightingLayers.Compose(s,Kb(type:0),f,0,null));                       // not a keyboard
        Assert.Null(LightingLayers.Compose(s,Kb() with{LedNames=null},f,0,null));         // no names
        Assert.Null(LightingLayers.Compose(s,Kb() with{LedNames=["Key: A",""]},f,0,null));
        Assert.NotNull(LightingLayers.Compose(s,kb,f,0,null));                            // the same entry on a keyboard with names
    }
    [Fact] public void Fade_at_half_life()
    {
        Add(L("fade"));
        Assert.Equal([new Rgb(151,26,11),Base],Painted(Kb(),1000,Press(500,0)));
        Assert.Equal([Base,Base],Painted(Kb(),1000));
    }
    [Fact] public void Ripple_origins_and_expiry()
    {
        Add(L("ripple"));
        Assert.Equal([Red,Base],Painted(Kb(),500,Press(500,0)));
        Assert.Equal([Base,Base],Painted(Kb(),1500,Press(500,0)));            // e=1, expired
        Assert.Equal([Base,Base],Painted(Kb(),500,Press(500)));               // no origin light
        Assert.Equal([Base,Base],Painted(Kb(),500,new LightPress("other|1",[0],500)));
        Assert.Equal([Base,Base],Painted(Kb(),400,Press(500,0)));             // not yet pressed
        Assert.Equal([Base,Base],Painted(Kb(),500,Press(0,0)));               // the ring at e=0.5 is 0.29 from light 1, beyond its 0.18 width
        Assert.Equal(new Rgb(107,48,20),Painted(Kb(),600,Press(0,0))[1]);      // e=0.6: ring 0.158, fade 0.4
    }
    [Fact] public void A_ripple_ignores_a_light_with_no_position()
    {
        var kb=Kb(["Key: A","Key: B","Key: C"],2,[0,1]);                       // light 2 is in no cell
        Add(L("ripple"));
        var c=Painted(kb,500,Press(500,0));
        Assert.Equal(Red,c[0]);Assert.Equal(Base,c[2]);
        Assert.Equal(Base,Painted(kb,500,Press(500,2))[0]);                     // an origin with no position starts no ring
    }
    [Fact] public void Layer_keys_absent_empty_and_listed()
    {
        var l=Add(L("color","#C80000",100));
        Assert.Equal([Red,Red],Painted(Kb()));
        l.Lights=[];Assert.Equal([Base,Base],Painted(Kb()));
        l.Lights=[new(){Index=1,Name="Key: B"}];Assert.Equal([Base,Red],Painted(Kb()));
    }
    [Fact] public void A_reordered_keyboard_skips_paint_and_layer_keys()
    {
        var kb=Kb();
        T().Keys=new(){[0]=new(){Name="Different",Color="#C80000"},[7]=new(){Name="Key: A",Color="#C80000"}};
        Assert.Equal([Base,Base],Painted(kb));
        Assert.Equal(2,LightingLayers.SkippedKeys(kb,T()));
        T().Keys=null;
        Add(L("color","#C80000",100,lights:[new(){Index=0,Name="Key: B"}]));
        Add(L("color","#C80000",100,lights:[new(){Index=0,Name="Key: A"},new(){Index=1,Name="Key: A"},new(){Index=9,Name="Key: B"}]));
        Add(L("fade"));
        Assert.Equal([Red,Base],Painted(kb));
        Assert.Equal([1,2,0],LightingLayers.SkippedLayerLights(kb,T()));
    }
    [Fact] public void Presses_combine_by_the_largest()
    {
        Add(L("fade"));
        // ages 500 (0.5) and 100 (0.9): the larger, 0.9, gives 101*0.1+200*0.9; a sum would be 1.4
        Assert.Equal(new Rgb(190,5,2),Painted(Kb(),1000,Press(500,0),Press(900,0))[0]);
        Assert.Equal(new Rgb(190,5,2),Painted(Kb(),1000,Press(900,0),Press(500,0))[0]);
    }
    [Fact] public void Each_effect_name_runs_its_own_effect()
    {
        var kb=Kb();var f=new Rgb[]{Base,Base};var got=new List<Rgb>();
        foreach(var (name,kind) in new[]{("wave",EffectKind.Wave),("breathing",EffectKind.Breathing),("spectrum",EffectKind.Spectrum)})
        {
            s.Devices.Clear();Add(L("effect","#C80000",100,"cover",effect:name));
            var c=LightingLayers.Compose(s,kb,f,777,null)!;
            var q=new EffectParams{Speed=40};
            var want=LightingEffects.Evaluate(kind,new DeskPoint(1,0.5),777,q,Red);
            Assert.Equal(want,LightingLayers.Round(c[3],c[4],c[5]));
            Assert.NotEqual(LightingEffects.Evaluate(EffectKind.Static,new DeskPoint(1,0.5),777,q,Red),want);
            got.Add(want);
        }
        Assert.Equal(3,got.Distinct().Count());
    }
    [Fact] public void An_effect_layer_uses_key_positions()
    {
        var kb=Kb(["Key: A","Key: B","Key: C","Key: D"],3,[0,1,2]);          // lights 0,1,2 at x 0,0.5,1, all at y 0.5; light 3 in no cell
        Add(L("effect","#C80000",100,"cover",effect:"wave"));
        var c=LightingLayers.Compose(s,kb,Enumerable.Repeat(Base,4).ToArray(),500,null)!;
        Rgb At(int i)=>LightingLayers.Round(c[3*i],c[3*i+1],c[3*i+2]);
        Assert.NotEqual(At(0),At(2));
        Assert.Equal(At(1),At(3));                                              // no position is the centre, 0.5,0.5
    }
    [Fact] public void Two_lights_at_one_x_share_an_effect_colour()
    {
        var d=Kb(["Key: A","Key: B","Key: C"]) with{Zones=[new LightingZone(0,"Keys",3,new ZoneMatrix(2,2,[0,1,2,KeyMaps.Empty]))]};   // lights 0 and 2 at x 0, light 1 at x 1
        Add(L("effect","#C80000",100,"cover",effect:"wave"));
        var c=LightingLayers.Compose(s,d,Enumerable.Repeat(Base,3).ToArray(),500,null)!;
        Assert.Equal(c[0],c[6]);Assert.Equal(c[1],c[7]);Assert.Equal(c[2],c[8]);
        Assert.NotEqual(c[0],c[3]);
    }
    [Fact] public void Lights_off_is_black_with_paint_and_layers()
    {
        T().Keys=new(){[0]=new(){Name="Key: A",Color="#C80000"}};Add(L("color"));Add(L("fade"));
        s.LightsOn=false;
        Assert.All(Painted(Kb(),500,Press(500,0)),c=>Assert.Equal(Rgb.Black,c));
    }
    [Fact] public void NeedsKeys_cases()
    {
        var kb=Kb();var l=Add(L("ripple"));
        Assert.True(LightingLayers.NeedsKeys(s,kb));Assert.Equal(1000,LightingLayers.LongestReactionMs(s,kb));
        l.Strength=0;Assert.False(LightingLayers.NeedsKeys(s,kb));Assert.Equal(0,LightingLayers.LongestReactionMs(s,kb));l.Strength=100;
        l.Lights=[];Assert.False(LightingLayers.NeedsKeys(s,kb));
        l.Lights=[new(){Index=0,Name="Key: B"},new(){Index=5,Name="Key: A"}];Assert.False(LightingLayers.NeedsKeys(s,kb));
        l.Lights=[new(){Index=1,Name="Key: B"}];Assert.True(LightingLayers.NeedsKeys(s,kb));l.Lights=null;
        s.LightsOn=false;Assert.False(LightingLayers.NeedsKeys(s,kb));s.LightsOn=true;
        s.HardwareModes[K]="Rainbow";Assert.False(LightingLayers.NeedsKeys(s,Kb(modes:["Direct","Rainbow"])));s.HardwareModes.Clear();
        Assert.False(LightingLayers.NeedsKeys(s,Kb(modes:["Rainbow"])));          // not painted by us at all
        T().Layers=[L("color")];Assert.False(LightingLayers.NeedsKeys(s,kb));
        T().Layers=[L("fade",duration:300),L("ripple",duration:2000),L("fade",strength:0,duration:3000)];
        Assert.True(LightingLayers.NeedsKeys(s,kb));Assert.Equal(2000,LightingLayers.LongestReactionMs(s,kb));
    }
    [Fact] public void Animating_with_layers()
    {
        var kb=Kb();var devices=new[]{kb};s.Effect.Kind="Static";
        var l=Add(L("effect"));
        Assert.True(LightingPaint.Animating(s,devices));
        l.Lights=[];Assert.False(LightingPaint.Animating(s,devices));
        T().Layers=[L("ripple",duration:600)];
        Assert.False(LightingPaint.Animating(s,devices));
        Assert.True(LightingPaint.Animating(s,devices,[Press(1000,0)],1100));
        Assert.False(LightingPaint.Animating(s,devices,[Press(1000,0)],1700));
        Assert.False(LightingPaint.Animating(s,devices,[new LightPress("other|1",[0],1000)],1100));
        s.LightsOn=false;Assert.False(LightingPaint.Animating(s,devices,[Press(1000,0)],1100));
    }
    [Fact] public void Shown_is_stage_three()
    {
        var kb=Kb();var t=T();t.Brightness=10;t.Correction=new(){R=200,G=100,B=50};
        t.Keys=new(){[0]=new(){Name="Key: A",Color="#C80000"}};
        Assert.Equal(Red,LightingPaint.Shown(s,kb,0,null)[0]);
        Assert.Equal(Base,LightingPaint.Shown(s,kb,0,null)[1]);
        t.Keys=null;
        Assert.Equal(LightingPaint.Lights(s,kb,0),LightingPaint.Shown(s,kb,0,null));
    }
    [Fact] public void CanPaint_needs_a_keyboard_map_positions_and_a_name_per_light()
    {
        Assert.True(KeyMaps.CanPaint(Kb()));
        Assert.False(KeyMaps.CanPaint(Kb(type:0)));
        Assert.False(KeyMaps.CanPaint(Kb() with{LedNames=null}));
        Assert.False(KeyMaps.CanPaint(Kb() with{LedNames=["Key: A",""]}));
        Assert.False(KeyMaps.CanPaint(Kb() with{Zones=[new LightingZone(0,"Keys",2)]}));
    }
    [Fact] public void LightPress_has_exactly_three_members()
    {
        var names=typeof(LightPress).GetProperties(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance).Select(p=>p.Name).Order().ToArray();
        Assert.Equal(["AtMs","DeviceKey","Lights"],names);
    }
}
