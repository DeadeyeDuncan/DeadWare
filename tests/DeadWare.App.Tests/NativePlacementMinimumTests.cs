using System.Reflection;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class NativePlacementMinimumTests
{
    [Theory]
    [InlineData(320,96)] [InlineData(390,96)]
    [InlineData(320,144)] [InlineData(390,144)]
    [InlineData(320,192)] [InlineData(390,192)]
    public void Old_widget_sizes_grow_before_position_clamping_at_target_dpi(double oldHeight,int dpi)
    {
        var saved=new WindowPlacement(-600,1040-oldHeight,340,oldHeight,true);
        var actual=Constrain(saved,[new(-1600,0,1600,1040)],300,400,(uint)dpi);
        double scale=dpi/96d;
        Assert.Equal(Math.Max(340,300*scale),actual.Width);
        Assert.Equal(400*scale,actual.Height);
        Assert.Equal(1040-actual.Height,actual.Top);
        Assert.Equal(-600,actual.Left);Assert.True(actual.Topmost);
    }

    [Fact]
    public void Growing_an_edge_aligned_widget_keeps_the_monitor_used_to_choose_its_dpi()
    {
        var saved=new WindowPlacement(1260,650,340,390,true);
        WorkArea[] areas=[new(0,0,1600,1040),new(1600,0,2560,1440)];
        Assert.Equal(new WindowPlacement(1000,240,600,800,true),Constrain(saved,areas,300,400,192));
    }

    [Fact]
    public void An_already_large_saved_window_keeps_its_physical_bounds_and_pin_choice()
    {
        var saved=new WindowPlacement(20,30,800,900,false);
        Assert.Equal(saved,Constrain(saved,[new(0,0,1920,1040)],300,400,96));
    }

    private static WindowPlacement Constrain(WindowPlacement saved,WorkArea[] areas,double minWidth,double minHeight,uint dpi)
    {
        var type=typeof(Collector).Assembly.GetType("DeadWare.App.NativePlacement")!;
        var method=type.GetMethod("ClampMinimum",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (WindowPlacement)method.Invoke(null,[saved,areas,minWidth,minHeight,dpi])!;
    }
}
