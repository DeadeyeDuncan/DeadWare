using System.Windows.Media;
using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public sealed class ThemeTokenTests
{
    [Fact]
    public void Derived_tokens_use_spec_alphas_and_small_surface_shifts()
    {
        var t=ThemeTokens.Derive(Color.FromRgb(0x35,0x41,0x50),Color.FromRgb(0x91,0xE7,0xCA),Color.FromRgb(0x15,0x1B,0x24),false,false);
        Assert.Equal(115,t.Hairline.A);Assert.Equal(0x35,t.Hairline.R);
        Assert.Equal(31,t.AccentSoft.A);Assert.Equal(0x91,t.AccentSoft.R);
        Assert.Equal(56,t.Glow.A);Assert.Equal(0xCA,t.Glow.B);
        Assert.True(t.Surface1.R>0x15&&t.Surface1.R<=0x15+8);
        Assert.True(t.Surface2.R<0x15&&t.Surface2.R>=0x15-8);
        Assert.Equal(255,t.Surface1.A);Assert.Equal(255,t.Surface2.A);
    }

    [Fact]
    public void High_contrast_tokens_are_opaque_and_glow_free()
    {
        var t=ThemeTokens.Derive(Colors.White,Colors.Yellow,Colors.Black,true,false);
        Assert.Equal(255,t.Hairline.A);Assert.Equal(Colors.White,t.Hairline);
        Assert.Equal(0,t.Glow.A);Assert.Equal(0,t.AccentSoft.A);
        Assert.Equal(Colors.Black,t.Surface1);Assert.Equal(Colors.Black,t.Surface2);
    }

    [Fact]
    public void Blackout_surfaces_stay_pure_black_but_keep_glow()
    {
        var t=ThemeTokens.Derive(Color.FromRgb(0x30,0x30,0x30),Color.FromRgb(0xD4,0xD4,0xD4),Colors.Black,false,true);
        Assert.Equal(Colors.Black,t.Surface1);Assert.Equal(Colors.Black,t.Surface2);
        Assert.Equal(56,t.Glow.A);Assert.Equal(115,t.Hairline.A);
    }

    [Fact] public void Core_tints_are_distinct_for_every_count_and_endpoint_colour()
    {
        foreach(var cpu in new[]{Color.FromRgb(0x5B,0xC8,0xFF),Color.FromRgb(0xFF,0x7A,0x3D),Colors.White,Colors.Black,Color.FromRgb(0x30,0xD1,0x58)})
        foreach(bool hc in new[]{false,true})
        foreach(int count in new[]{1,8,64})
        {
            var tints=Enumerable.Range(0,count).Select(i=>ThemeTokens.CoreTint(cpu,i,count,hc)).ToArray();
            Assert.Equal(count,tints.Distinct().Count());
            Assert.All(tints,t=>Assert.Equal(255,t.A));
        }
        Assert.Equal(Colors.White,ThemeTokens.CoreTint(Colors.White,0,1,false));
    }

    [Fact] public void High_contrast_tints_stay_off_white_and_black()
        =>Assert.All(Enumerable.Range(0,16).Select(i=>ThemeTokens.CoreTint(Colors.White,i,16,true)),t=>Assert.True(t.R<250&&t.R>40));

    [Fact] public void Core_tints_stay_distinct_for_near_grey_colours()
    {
        foreach(var cpu in new[]{Color.FromRgb(0x8A,0x80,0x78),Color.FromRgb(0x7F,0x80,0x81),Color.FromRgb(0x40,0x42,0x44),Color.FromRgb(0xC0,0xBF,0xBE),Color.FromRgb(0x10,0x10,0x10),Color.FromRgb(0x80,0x80,0x80)})
        foreach(bool hc in new[]{false,true})
        {
            var tints=Enumerable.Range(0,64).Select(i=>ThemeTokens.CoreTint(cpu,i,64,hc)).ToArray();
            Assert.True(64==tints.Distinct().Count(),$"{cpu} hc={hc} got {tints.Distinct().Count()}");
            Assert.All(tints,t=>Assert.Equal(255,t.A));
        }
    }
}
