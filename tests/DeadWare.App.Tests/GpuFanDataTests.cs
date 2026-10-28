using System.IO;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class GpuFanDataTests
{
    [Fact]
    public void Graph_mapping_adds_fan_to_the_existing_gpu_identity()
    {
        var mappings=GraphHistoryProjection.Mappings(Reading(DateTimeOffset.UtcNow,1,"/gpu/0",0),"machine");
        string temperatureKey=Assert.Single(mappings,p=>p.Value=="/gpu/0/temperature").Key;
        string prefix=temperatureKey[..^"temperature".Length];
        Assert.Equal("/gpu/0/fan",mappings[prefix+"fan"]);
        Assert.Equal("/gpu/0/usage",mappings[prefix+"usage"]);
        Assert.Equal(6,mappings.Count);
    }

    [Fact]
    public async Task Fan_zero_and_missing_roundtrip_and_gpu_renumbering_keeps_history()
    {
        using var fixture=new Fixture();var now=DateTimeOffset.UtcNow.AddSeconds(-2);
        var saved=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        saved.Start();
        saved.TryRecord(Reading(now.AddSeconds(-20),10,"/gpu/0",0));
        saved.TryRecord(Reading(now.AddSeconds(-19),11,"/gpu/0",null));
        await saved.StopAsync();
        var history=new SampleHistory();var reopened=new GraphHistoryService(fixture.Path,"machine",history);
        reopened.Start();reopened.TryRecord(Reading(now,1,"/gpu/1",1450));await reopened.StopAsync();
        Assert.Equal(new double?[]{0,null},history.Series("/gpu/1/fan",1,21600).Select(p=>p.Value));
        Assert.All(history.Series("/gpu/1/temperature",1,21600),p=>Assert.Equal(65,p.Value));
    }

    [Fact]
    public async Task Old_cache_without_fan_keeps_temperature_and_usage_and_leaves_fan_blank()
    {
        using var fixture=new Fixture();var now=DateTimeOffset.UtcNow.AddSeconds(-2);
        var reading=Reading(now,1,"/gpu/0",1500);
        // These are the graph-v1 keys emitted before fan history existed.
        const string gpu="75A21BAF245B00B60CFA71EEA6E854D8CEAF631D914DFD43C4CF351C4675C845";
        var values=new Dictionary<string,double?>{[gpu+"/usage"]=45,[gpu+"/temperature"]=65};
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();
        store.Append([new("old",now.AddSeconds(-10),10,values)],DateTimeOffset.UtcNow);
        var history=new SampleHistory();var service=new GraphHistoryService(fixture.Path,"machine",history);
        service.Start();service.TryRecord(reading);await service.StopAsync();
        Assert.Equal(65,Assert.Single(history.Series("/gpu/0/temperature",1,21600)).Value);
        Assert.Equal(45,Assert.Single(history.Series("/gpu/0/usage",1,21600)).Value);
        Assert.Null(Assert.Single(history.Series("/gpu/0/fan",1,21600)).Value);
    }

    private static Reading Reading(DateTimeOffset time,double seconds,string id,double? fan)=>new(
        new(time,seconds,new Dictionary<string,double?>{[id+"/usage"]=45,[id+"/temperature"]=65,[id+"/fan"]=fan}),
        [new(id,"Test GPU")],[new(id+"/temperature/0","Test GPU","GPU Core","°C",65,id,"GpuNvidia","Temperature")],
        [],"Test CPU","","","",false);

    private sealed class Fixture:IDisposable
    {
        private string Root {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-fan-history-"+Guid.NewGuid().ToString("N"));
        public string Path=>System.IO.Path.Combine(Root,"graphs.db");
        public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);}
    }
}
