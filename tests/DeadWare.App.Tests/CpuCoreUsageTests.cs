using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class CpuCoreUsageTests
{
    [Fact] public void Plain_core_sensors_order_by_number_and_keep_null_gaps()
        =>Assert.Equal([10d,null,30.5d],CpuCoreUsage.FromSensors([Load("CPU Core #3",30.46),Load("CPU Core #1",10),Load("CPU Core #2",null)]));
    [Fact] public void Thread_sensors_are_ignored_for_a_core_that_also_has_a_plain_sensor()
        =>Assert.Equal([10d],CpuCoreUsage.FromSensors([Load("CPU Core #1",10),Load("CPU Core #1 Thread #1",9),Load("CPU Core #1 Thread #2",11)]));
    [Fact] public void Thread_only_cores_get_one_entry_per_thread()
        =>Assert.Equal([9d,11d,20d],CpuCoreUsage.FromSensors([Load("CPU Core #2 Thread #1",20),Load("CPU Core #1 Thread #2",11),Load("CPU Core #1 Thread #1",9)]));
    [Fact] public void Hybrid_threaded_p_cores_and_plain_e_cores_both_appear()
        =>Assert.Equal([1d,2d,3d,4d,50d,60d],CpuCoreUsage.FromSensors([Load("CPU Core #9",50),Load("CPU Core #1 Thread #1",1),Load("CPU Core #1 Thread #2",2),Load("CPU Core #10",60),Load("CPU Core #2 Thread #1",3),Load("CPU Core #2 Thread #2",4)]));
    [Fact] public void P_cores_come_before_e_cores_before_generic()
        =>Assert.Equal([5d,6d,1d,2d,9d],CpuCoreUsage.FromSensors([Load("CPU Core #1",9),Load("E-Core #2",2),Load("P-Core #2",6),Load("E-Core #1",1),Load("P-Core #1",5)]));
    [Fact] public void Single_core_without_a_number_is_core_one()
        =>Assert.Equal([42d],CpuCoreUsage.FromSensors([Load("CPU Core",42)]));
    [Fact] public void Total_max_cores_other_units_and_other_hardware_do_not_count()
        =>Assert.Empty(CpuCoreUsage.FromSensors([Load("CPU Total",50),Load("CPU Core Max",90),Load("CPU Cores",40),Load("CPU Core #1",3000) with {Unit="MHz",SensorType="Clock"},Load("CPU Core #1",40) with {HardwareType="GpuNvidia"}]));
    [Fact] public void More_than_sixty_four_entries_are_capped()
    {
        var cores=CpuCoreUsage.FromSensors(Enumerable.Range(1,70).Select(k=>Load($"CPU Core #{k}",k)).ToArray(),out int dropped);
        Assert.Equal(64,cores.Count);Assert.Equal(6,dropped);
        CpuCoreUsage.FromSensors([Load("CPU Core #1",1)],out dropped);Assert.Equal(0,dropped);
    }
    [Fact] public void Non_ascii_and_overlong_core_numbers_are_ignored_without_throwing()=>
        Assert.Equal(new double?[]{5},CpuCoreUsage.FromSensors([Load("CPU Core #٣",1),Load("CPU Core #99999999999",2),Load("CPU Core #1 Thread #99999999999",3),Load("CPU Core #2",5)]));
    [Fact] public void Key_is_zero_based()=>Assert.Equal("cpu/core/0/usage",CpuCoreUsage.Key(0));
    private static SensorReading Load(string name,double? value)=>new("/cpu/0/load/"+name,"Test CPU",name,"%",value,"/cpu/0","Cpu","Load");
}
