using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

// Fix wave 1, D4: the collector with hub readings: the rows and the coolant value are there, the CPU pump selection is unchanged, a reading handed
// over too old gives neither, and clearing the readings drops them. The collector ages a reading on its own Stopwatch, not on a TimeProvider, so the
// ageing test waits a real minimum before the first poll (never a deadline): a slower machine only makes the reading older.
public class CollectorHubTests
{
    static HubReadings Owners()=>new([new(13,"pump","Pump",2774,43.7),new(1,"fan","Fan",1660),new(2,"fan","Fan",1654),new(3,"fan","Fan",1663)]);
    static Collector Isolated()=>new(new(HardwareEnabled:false,EtwEnabled:false,PollingIntervalSeconds:0.5));
    static async Task<Reading> Next(Collector c,Func<Reading,bool> match)
    {
        var tcs=new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Seen(Reading r){if(match(r))tcs.TrySetResult(r);}
        c.SampleAvailable+=Seen;
        try{return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));}
        finally{c.SampleAvailable-=Seen;}
    }
    static string[] HubRows(Reading r)=>r.Sensors.Where(s=>s.HardwareType=="CorsairLink").Select(s=>s.Name).ToArray();

    [Fact]
    public async Task Hub_readings_give_rows_and_the_coolant_and_leave_the_pump_alone()
    {
        await using var c=Isolated();
        c.SetHub((Owners(),TimeSpan.Zero),true);
        var first=Next(c,_=>true);
        c.Start();
        var r=await first;
        Assert.Equal(["Coolant","Pump","Radiator fan 1","Radiator fan 2","Radiator fan 3"],HubRows(r));
        Assert.Equal([43.7,2774,1660,1654,1663],r.Sensors.Where(s=>s.HardwareType=="CorsairLink").Select(s=>s.Value));
        Assert.Equal(43.7,r.Sample.Values["cpu/coolant"]);
        Assert.Null(r.Sample.Values["cpu/pump"]);
        // Cleared, the next poll has neither.
        c.SetHub(null,true);
        var cleared=await Next(c,x=>HubRows(x).Length==0);
        Assert.Null(cleared.Sample.Values["cpu/coolant"]);
    }

    [Fact]
    public async Task A_reading_that_ages_past_10_s_after_it_was_handed_over_gives_nothing()
    {
        await using var c=Isolated();
        c.SetHub((Owners(),TimeSpan.FromMilliseconds(9900)),false);
        await Task.Delay(200);
        var first=Next(c,_=>true);
        c.Start();
        var r=await first;
        Assert.Empty(HubRows(r));
        Assert.Null(r.Sample.Values["cpu/coolant"]);
    }
}
