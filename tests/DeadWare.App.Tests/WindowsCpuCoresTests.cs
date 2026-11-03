using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class WindowsCpuCoresTests
{
    [Fact] public void Delta_gives_percent_busy_per_processor()
        =>Assert.Equal([25d,100d],WindowsCpuCores.Delta([(0,0,0),(0,0,0)],[(75,100,0),(0,50,50)]));
    [Fact] public void Backwards_or_zero_counters_yield_null()
        =>Assert.Equal([null,null],WindowsCpuCores.Delta([(10,10,10),(0,0,0)],[(5,10,10),(0,0,0)]));
    [Fact] public void Processor_count_change_yields_empty()
        =>Assert.Empty(WindowsCpuCores.Delta([(0,0,0)],[(0,1,1),(0,1,1)]));
    [Fact] public void Seventy_processors_cap_at_sixty_four_and_report_six_dropped()
    {   var p=new (ulong,ulong,ulong)[70];var c=Enumerable.Repeat<(ulong,ulong,ulong)>((0,1,1),70).ToArray();
        Assert.Equal(64,WindowsCpuCores.Delta(p,c).Count);Assert.Equal(6,WindowsCpuCores.Dropped(70));Assert.Equal(0,WindowsCpuCores.Dropped(64));
    }
    [Fact] public void Struct_is_forty_eight_bytes()
        =>Assert.Equal(48,System.Runtime.InteropServices.Marshal.SizeOf<WindowsCpuCores.ProcessorPerformance>());
    [Fact] public void First_read_is_empty_and_second_is_sized_like_the_machine()
    {   var cores=new WindowsCpuCores();
        Assert.Empty(cores.Read());Thread.Sleep(50);
        Assert.Equal(Math.Min(64,Environment.ProcessorCount),cores.Read().Count);
    }
}
