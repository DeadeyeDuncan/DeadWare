using System.Reflection;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class NativePlacementMinimumTests
{
    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void Scrollable_widget_minimum_fits_a_short_target_work_area_at_actual_dpi(int dpi)
    {
        var saved=new WindowPlacement(1300,50,300,200,true);
        WorkArea[] areas=[new(0,0,1600,480),new(1600,0,2560,1440)];
        var type=typeof(Collector).Assembly.GetType("DeadWare.App.NativePlacement")!;
        double minimum=(double)type.GetMethod("MinimumHeightForWorkArea",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,[saved,areas,670d,(uint)dpi])!;
        Assert.Equal(480/(dpi/96d),minimum);
        var placement=Constrain(saved,areas,300,minimum,(uint)dpi);
        Assert.Equal(480,placement.Height);Assert.True(placement.Top>=0);Assert.True(placement.Left+placement.Width<=1600);
        Assert.True(placement.Topmost);
    }

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void Scrollable_widget_retains_readable_requested_minimum_on_a_tall_work_area(int dpi)
    {
        var type=typeof(Collector).Assembly.GetType("DeadWare.App.NativePlacement")!;
        double minimum=(double)type.GetMethod("MinimumHeightForWorkArea",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,[new WindowPlacement(20,20,340,300,false),new WorkArea[]{new(0,0,3840,2160)},670d,(uint)dpi])!;
        Assert.Equal(670,minimum);
    }

    [Theory]
    [InlineData(0,390,650)] [InlineData(1,400,640)] [InlineData(2,560,480)]
    [InlineData(3,800,240)] [InlineData(4,1040,0)]
    public void Selected_widget_count_keeps_live_minimum_growth_on_its_original_monitor(int count,double height,double top)
    {
        var saved=new WindowPlacement(1260,650,340,390,true);
        WorkArea[] areas=[new(0,0,1600,1040),new(1600,0,2560,1440)];
        Assert.Equal(new WindowPlacement(1000,top,600,height,true),Constrain(saved,areas,300,WidgetGraphSelection.MinimumHeight(count),192));
    }

    [Theory]
    [InlineData(400,96)] [InlineData(480,96)]
    [InlineData(400,144)] [InlineData(480,144)]
    [InlineData(400,192)] [InlineData(480,192)]
    public void Four_graph_widget_expands_older_saved_sizes_within_the_work_area(double oldHeight,int dpi)
    {
        double scale=dpi/96d;
        var saved=new WindowPlacement(100,2160-oldHeight*scale,340*scale,oldHeight*scale,true);
        var actual=Constrain(saved,[new(0,0,3840,2160)],300,520,(uint)dpi);
        Assert.Equal(340*scale,actual.Width); Assert.Equal(520*scale,actual.Height);
        Assert.Equal(2160-actual.Height,actual.Top); Assert.Equal(100,actual.Left); Assert.True(actual.Topmost);
    }

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
