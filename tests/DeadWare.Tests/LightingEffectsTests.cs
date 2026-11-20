using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class LightingEffectsTests
{
    private static readonly Rgb Red=new(255,0,0);
    private static readonly Rgb Green=new(0,255,0);
    // spread 100 => wavelength 0.2, so x = 0.1 is exactly half a wavelength.
    private static EffectParams P(double speed=0,double angle=0,double spread=100,params string[] colors)=>new(){Speed=speed,AngleDeg=angle,Spread=spread,Colors=colors.ToList()};

    [Theory][InlineData(0,0.05)][InlineData(100,2.0)][InlineData(-5,0.05)][InlineData(250,2.0)][InlineData(50,1.025)]
    public void Speed_maps_to_cycles_per_second(double speed,double hz)=>Assert.Equal(hz,LightingEffects.CyclesPerSecond(speed),6);
    [Theory][InlineData(0,3.0)][InlineData(100,0.2)][InlineData(50,1.6)][InlineData(-1,3.0)][InlineData(999,0.2)]
    public void Spread_is_inverted(double spread,double wavelength)=>Assert.Equal(wavelength,LightingEffects.Wavelength(spread),6);

    [Fact] public void Project_uses_the_axis_at_cardinal_angles()
    {
        Assert.Equal(1,LightingEffects.Project(new(1,0),0),10);
        Assert.Equal(1,LightingEffects.Project(new(0,1),90),10);
        Assert.Equal(-1,LightingEffects.Project(new(1,0),180),10);
        Assert.Equal(-1,LightingEffects.Project(new(0,1),270),10);
    }
    [Fact] public void Project_diagonal_is_a_dot_product()=>Assert.Equal(Math.Sqrt(2),LightingEffects.Project(new(1,1),45),10);

    [Fact] public void Phase_is_zero_at_the_origin_at_t0()=>Assert.Equal(0,LightingEffects.PhaseAt(new(0,0),0,P()),10);
    [Fact] public void Phase_advances_with_distance()=>Assert.Equal(0.5,LightingEffects.PhaseAt(new(0.1,0),0,P()),10);
    [Fact] public void Phase_wraps_a_negative_value_into_0_1()=>Assert.Equal(0.95,LightingEffects.PhaseAt(new(0,0),1000,P()),10);

    [Fact] public void Empty_palette_walks_the_hue_circle()
    {
        Assert.Equal(Red,LightingEffects.SamplePalette(new List<Rgb>(),0));
        Assert.Equal(new Rgb(0,255,255),LightingEffects.SamplePalette(new List<Rgb>(),0.5));
    }
    [Fact] public void Single_colour_palette_is_that_colour()=>Assert.Equal(Red,LightingEffects.SamplePalette(new[]{Red},0.3));
    [Fact] public void Palette_interpolates_and_wraps_back_to_the_first()
    {
        var c=new[]{Red,Green};
        Assert.Equal(Red,LightingEffects.SamplePalette(c,0));
        Assert.Equal(Green,LightingEffects.SamplePalette(c,0.5));
        Assert.Equal(new Rgb(128,128,0),LightingEffects.SamplePalette(c,0.75));
    }

    [Fact] public void Wave_picks_the_palette_colour_for_the_phase()
    {
        var q=P(colors:new[]{"#FF0000","#00FF00"});
        Assert.Equal(Red,LightingEffects.Evaluate(EffectKind.Wave,new(0,0),0,q,Rgb.Black));
        Assert.Equal(Green,LightingEffects.Evaluate(EffectKind.Wave,new(0.1,0),0,q,Rgb.Black));
    }
    [Fact] public void Wave_with_no_colours_is_the_hue_circle()=>Assert.Equal(LightingEffects.HsvToRgb(180,1,1),LightingEffects.Evaluate(EffectKind.Wave,new(0.8,0),0,new EffectParams{Spread=50},Rgb.Black)); // 0.8/1.6 = phase 0.5

    [Fact] public void Breathing_is_in_step_and_uses_the_zone_colour()
    {
        var q=new EffectParams{Speed=40};var red=new Rgb(255,0,0);
        var a=LightingEffects.Evaluate(EffectKind.Breathing,new(0.1,0.9),700,q,red);
        Assert.Equal(a,LightingEffects.Evaluate(EffectKind.Breathing,new(0.9,0.1),700,q,red));
        Assert.Equal(Rgb.Black,LightingEffects.Evaluate(EffectKind.Breathing,new(0.5,0.5),0,q,red));       // phase 0
        Assert.Equal(0,a.G);Assert.Equal(0,a.B);
    }
    [Fact] public void Spectrum_is_in_step_and_ignores_colours()
    {
        var q=new EffectParams{Colors={"#00FF00"}};
        Assert.Equal(new Rgb(255,0,0),LightingEffects.Evaluate(EffectKind.Spectrum,new(0.3,0.7),0,q,Rgb.Black)); // hue 0
        Assert.Equal(LightingEffects.Evaluate(EffectKind.Spectrum,new(0,0),900,q,Rgb.Black),LightingEffects.Evaluate(EffectKind.Spectrum,new(1,1),900,q,Rgb.Black));
    }
    [Fact] public void Static_returns_the_zone_colour()=>Assert.Equal(new Rgb(1,2,3),LightingEffects.Evaluate(EffectKind.Static,new(0,0),5,new(),new Rgb(1,2,3)));
    [Fact] public void Evaluate_is_deterministic()
    {
        var q=P(63,37,41,"#FF0000","#0000FF");
        Assert.Equal(LightingEffects.Evaluate(EffectKind.Wave,new(0.37,0.62),4321,q,Rgb.Black),LightingEffects.Evaluate(EffectKind.Wave,new(0.37,0.62),4321,q,Rgb.Black));
    }

    [Theory][InlineData("Wave",EffectKind.Wave)][InlineData("Plasma",EffectKind.Static)][InlineData(null,EffectKind.Static)][InlineData("1",EffectKind.Static)][InlineData("Wave, Static",EffectKind.Static)]
    public void Unknown_kinds_are_static(string? name,EffectKind kind)=>Assert.Equal(kind,LightingEffects.KindOf(name));
    [Fact] public void ParamsFor_returns_the_set_for_the_kind_and_null_for_static()
    {
        var s=new EffectSettings();
        Assert.Same(s.Wave,LightingEffects.ParamsFor(s,EffectKind.Wave));Assert.Same(s.Breathing,LightingEffects.ParamsFor(s,EffectKind.Breathing));Assert.Same(s.Spectrum,LightingEffects.ParamsFor(s,EffectKind.Spectrum));
        Assert.Null(LightingEffects.ParamsFor(s,EffectKind.Static));
    }
    [Fact] public void Clone_is_deep()
    {
        var s=new EffectSettings{Kind="Wave",Wave=new(){Speed=7,Colors={"#112233"}}};var c=LightingEffects.Clone(s);
        Assert.Equal("Wave",c.Kind);Assert.Equal(7,c.Wave.Speed);Assert.Equal(new[]{"#112233"},c.Wave.Colors);
        Assert.NotSame(s.Wave,c.Wave);Assert.NotSame(s.Wave.Colors,c.Wave.Colors);
        c.Wave.Colors.Add("#000000");Assert.Single(s.Wave.Colors);
    }
    [Fact] public void There_are_eight_directions_45_degrees_apart()
    {
        Assert.Equal(8,LightingEffects.Directions.Count);
        Assert.Equal(new[]{0d,45,90,135,180,225,270,315},LightingEffects.Directions.Select(d=>d.Angle).Order());
        Assert.Equal("Left to right",LightingEffects.Directions[LightingEffects.NearestDirection(359)].Name);
        Assert.Equal("Front to back",LightingEffects.Directions[LightingEffects.NearestDirection(270)].Name);
    }
    [Fact] public void Directions_are_named_in_order()=>Assert.Equal(new[]{"Left to right","Back-left to front-right","Back to front","Back-right to front-left","Right to left","Front-right to back-left","Front to back","Front-left to back-right"},LightingEffects.Directions.Select(d=>d.Name));

    [Theory][InlineData(double.NaN,0,50)][InlineData(40,360,50)][InlineData(40,-1,50)][InlineData(40,0,101)][InlineData(-1,0,50)]
    public void Validate_refuses_out_of_range_numbers(double speed,double angle,double spread)=>Assert.NotNull(LightingEffects.Validate(new EffectSettings{Kind="Wave",Wave=new(){Speed=speed,AngleDeg=angle,Spread=spread}}));
    [Fact] public void Validate_refuses_bad_colours_nine_colours_and_unknown_kinds()
    {
        Assert.NotNull(LightingEffects.Validate(new EffectSettings{Wave=new(){Colors={"red"}}}));
        Assert.NotNull(LightingEffects.Validate(new EffectSettings{Wave=new(){Colors=Enumerable.Repeat("#112233",9).ToList()}}));
        Assert.NotNull(LightingEffects.Validate(new EffectSettings{Kind="Plasma"}));
        Assert.Null(LightingEffects.Validate(new EffectSettings()));
    }
    [Theory][InlineData("1")][InlineData("Wave, Static")][InlineData("wave")]
    public void Validate_refuses_kinds_that_enum_parsing_would_accept(string kind)=>Assert.NotNull(LightingEffects.Validate(new EffectSettings{Kind=kind}));
    [Fact] public void Validate_refuses_null_kind()=>Assert.NotNull(LightingEffects.Validate(new EffectSettings{Kind=null!}));
    [Fact] public void Validate_refuses_null_settings()=>Assert.NotNull(LightingEffects.Validate(null!));
    [Fact] public void Validate_refuses_a_null_wave_set()=>Assert.NotNull(LightingEffects.Validate(new EffectSettings{Wave=null!}));
    [Fact] public void Validate_refuses_a_null_breathing_set()=>Assert.NotNull(LightingEffects.Validate(new EffectSettings{Breathing=null!}));
    [Fact] public void Validate_refuses_a_null_spectrum_set()=>Assert.NotNull(LightingEffects.Validate(new EffectSettings{Spectrum=null!}));
    [Fact] public void Validate_refuses_a_null_colours_list()=>Assert.NotNull(LightingEffects.Validate(new EffectSettings{Breathing=new(){Colors=null!}}));
    [Fact] public void Validate_refuses_a_null_colour_string()=>Assert.NotNull(LightingEffects.Validate(new EffectSettings{Spectrum=new(){Colors={null!}}}));
    [Fact] public void Normalize_clamps_wraps_and_resets_unknown_kinds()
    {
        var s=new EffectSettings{Kind="Plasma",Wave=new(){Speed=500,AngleDeg=-90,Spread=-3}};
        LightingEffects.Normalize(s);
        Assert.Equal("Static",s.Kind);Assert.Equal(100,s.Wave.Speed);Assert.Equal(270,s.Wave.AngleDeg);Assert.Equal(0,s.Wave.Spread);
    }
    [Fact] public void Normalize_replaces_nulls_and_nan_with_defaults_and_keeps_colours()
    {
        var s=new EffectSettings{Kind=null!,Wave=null!,Breathing=new(){Speed=double.NaN,AngleDeg=double.NaN,Spread=double.NaN,Colors=null!},Spectrum=new(){Colors={"#112233","bad"}}};
        LightingEffects.Normalize(s);
        Assert.Equal("Static",s.Kind);Assert.Equal(40,s.Wave.Speed);Assert.Equal(40,s.Breathing.Speed);Assert.Equal(0,s.Breathing.AngleDeg);Assert.Equal(50,s.Breathing.Spread);Assert.Empty(s.Breathing.Colors);
        Assert.Equal(new[]{"#112233","bad"},s.Spectrum.Colors);
    }
    [Fact] public void Normalize_wraps_angles_at_360()
    {
        var s=new EffectSettings{Wave=new(){AngleDeg=360},Breathing=new(){AngleDeg=725}};LightingEffects.Normalize(s);
        Assert.Equal(0,s.Wave.AngleDeg);Assert.Equal(5,s.Breathing.AngleDeg);
    }
}
