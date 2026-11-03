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
}
