using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class HubChecksTests
{
    static HubPartReading Pump(int ch,int? rpm)=>new(ch,"pump","Pump",rpm);
    static HubPartReading Fan(int ch,int? rpm)=>new(ch,"fan","Fan",rpm);
    static HubReadings R(params HubPartReading[] parts)=>new(parts);

    [Theory]
    [InlineData(2444,true)]
    [InlineData(2445,false)]
    [InlineData(3000,false)]
    public void The_pump_fails_below_ninety_percent(int after,bool fails)
    {
        var stop=HubChecks.Compare(R(Pump(1,2716)),R(Pump(1,after)));
        if(!fails){Assert.Null(stop);return;}
        Assert.NotNull(stop);
        Assert.Equal(("slower","pump",1,2716,2444),(stop!.Kind,stop.Part,stop.Number,stop.Before,stop.After));
    }
    [Theory]
    [InlineData(1800,false)]
    [InlineData(1799,true)]
    public void Exactly_ninety_percent_passes(int after,bool fails)=>Assert.Equal(fails,HubChecks.Compare(R(Pump(1,2000)),R(Pump(1,after))) is not null);
    [Fact]
    public void A_slower_fan_names_its_number()
    {
        var before=R(Pump(1,2716),Fan(2,1337),Fan(3,1326),Fan(4,1337));
        var stop=HubChecks.Compare(before,R(Pump(1,2716),Fan(2,1337),Fan(3,1193),Fan(4,1337)));
        Assert.Equal(("slower","fan",2,1326,1193),(stop!.Kind,stop.Part,stop.Number,stop.Before,stop.After));
        Assert.Null(HubChecks.Compare(before,R(Pump(1,2716),Fan(2,1337),Fan(3,1194),Fan(4,1337))));
    }
    [Fact]
    public void No_reading_after_fails()
    {
        var stop=HubChecks.Compare(R(Pump(1,2716)),R(Pump(1,null)));
        Assert.Equal(("noReading","pump",1,2716,(int?)null),(stop!.Kind,stop.Part,stop.Number,stop.Before,stop.After));
    }
    [Fact]
    public void A_part_missing_after_fails_as_no_reading()
    {
        var stop=HubChecks.Compare(R(Pump(1,2716),Fan(2,1337)),R(Pump(1,2716)));
        Assert.Equal(("noReading","fan",1,1337,(int?)null),(stop!.Kind,stop.Part,stop.Number,stop.Before,stop.After));
    }
    [Fact]
    public void The_pump_is_judged_before_the_fans()
    {
        // The fan sits on a lower channel and is slower too; the pump is still the failure returned.
        var stop=HubChecks.Compare(R(Fan(1,1300),Pump(2,2700)),R(Fan(1,100),Pump(2,100)));
        Assert.Equal(("pump",1),(stop!.Part,stop.Number));
    }
    [Fact]
    public void A_part_without_a_before_value_is_not_compared()
    {
        Assert.Null(HubChecks.Compare(R(Pump(1,2716),Fan(2,null),Fan(3,1326)),R(Pump(1,2716),Fan(2,0),Fan(3,1326))));
        // It still counts toward the number of the next fan.
        var stop=HubChecks.Compare(R(Pump(1,2716),Fan(2,null),Fan(3,1326)),R(Pump(1,2716),Fan(2,0),Fan(3,100)));
        Assert.Equal(2,stop!.Number);
    }
    [Theory]
    [InlineData(0,true)]
    [InlineData(null,true)]
    [InlineData(1,false)]
    public void Pump_stalled_means_zero_or_no_reading(int? rpm,bool stalled)=>Assert.Equal(stalled,HubChecks.PumpStalled(R(Pump(1,rpm))));
    [Fact]
    public void Fans_never_count_as_a_stalled_pump()
    {
        Assert.False(HubChecks.PumpStalled(R(Pump(1,2774),Fan(2,0),Fan(3,0),Fan(4,0))));
        Assert.False(HubChecks.PumpStalled(R(Fan(2,0))));
    }
    [Theory]
    [InlineData(120,false)]
    [InlineData(121,true)]
    public void A_reading_is_stale_after_two_minutes(int seconds,bool stale)
    {
        var at=new DateTimeOffset(2026,11,25,12,0,0,TimeSpan.Zero);
        Assert.Equal(stale,HubChecks.Stale(at,at.AddSeconds(seconds)));
    }
}
