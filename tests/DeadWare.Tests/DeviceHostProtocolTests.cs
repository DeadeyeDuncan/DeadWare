using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class DeviceHostProtocolTests
{
    static HostState State()
    {
        var s=new LightingSettings{LightsOn=true,Brightness=40,DefaultColor="#112233"};
        s.ZoneColors["k:0"]=new ZoneColorEntry{Color="#010203",ZoneName="Keyboard"};
        s.HardwareModes["k"]="Direct";
        return new HostState(HostStatus.Blocked,"msg",true,
            [new LightingConflict(ConflictKind.ICue,"iCUE is running.",true)],
            [new DeviceView("k","Keys",5,PaintKind.Hardware,"Static","Rainbow",true,[new ZoneView("k:0",0,"Keyboard",104,"#010203",true)])],s);
    }
    public static IEnumerable<object[]> Messages()
    {
        yield return [new GetStateRequest{Id=1}];
        yield return [new SetLightsOnRequest(true){Id=2}];
        yield return [new SetBrightnessRequest(55){Id=3}];
        yield return [new SetAllColorsRequest("#ABCDEF"){Id=4}];
        yield return [new SetZoneColorRequest("k:0","#000001"){Id=5}];
        yield return [new SetZoneColorRequest("k:0",null){Id=6}];
        yield return [new ReplaceColorsRequest("#111111",new(){["k:0"]=new ZoneColorEntry{Color="#222222",ZoneName="Z"}}){Id=7}];
        yield return [new RescanRequest{Id=8}];
        yield return [new StopRequest{Id=9}];
        yield return [new Reply(false,"nope"){Id=10}];
        yield return [new StateEvent(State()){Id=11}];
    }
    static async Task<HostMessage?> RoundTrip(HostMessage m)
    {
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,m,default);
        ms.Position=0;
        return await DeviceHostProtocol.ReadAsync(ms,default);
    }
    [Theory] [MemberData(nameof(Messages))]
    public async Task Every_message_type_round_trips(HostMessage m)
    {
        var back=await RoundTrip(m);
        Assert.NotNull(back);
        Assert.Equal(m.GetType(),back!.GetType());
        Assert.Equal(m.Id,back.Id);
        switch(m)
        {
            case ReplaceColorsRequest r:
                var rb=Assert.IsType<ReplaceColorsRequest>(back);
                Assert.Equal(r.DefaultColor,rb.DefaultColor);
                Assert.Equal("#222222",rb.ZoneColors["k:0"].Color);
                Assert.Equal("Z",rb.ZoneColors["k:0"].ZoneName);
                break;
            case StateEvent:
                var s=Assert.IsType<StateEvent>(back).State;
                Assert.Equal(HostStatus.Blocked,s.Status);
                Assert.True(s.Elevated);
                Assert.Equal(ConflictKind.ICue,Assert.Single(s.Conflicts).Kind);
                var d=Assert.Single(s.Devices);
                Assert.Equal(PaintKind.Hardware,d.Kind);
                Assert.Equal("Static",d.Mode);
                Assert.Equal("Rainbow",d.OwnEffect);
                Assert.True(d.StaysLitWhenOff);
                Assert.Equal(104,Assert.Single(d.Zones).LedCount);
                Assert.Equal(40,s.Settings.Brightness);
                Assert.Equal("#010203",s.Settings.ZoneColors["k:0"].Color);
                Assert.Equal("Direct",s.Settings.HardwareModes["k"]);
                break;
            default: Assert.Equal(m,back); break;
        }
    }
    // Polish item 2: ConflictsChecked travels with the state, and a state without it reads false, so an empty list from a host that never
    // said it scanned is no all-clear.
    [Fact] public async Task Conflicts_checked_round_trips_and_reads_false_when_absent()
    {
        var checkedState=State() with{ConflictsChecked=true};
        Assert.True(Assert.IsType<StateEvent>(await RoundTrip(new StateEvent(checkedState){Id=12})).State.ConflictsChecked);
        Assert.False(Assert.IsType<StateEvent>(await RoundTrip(new StateEvent(State()){Id=13})).State.ConflictsChecked);
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,new StateEvent(checkedState){Id=14},default);
        var node=System.Text.Json.Nodes.JsonNode.Parse(ms.ToArray().AsSpan(4))!;
        Assert.True(node["State"]!.AsObject().Remove("ConflictsChecked"));
        Assert.False(Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(Frame(node.ToJsonString()),default)).State.ConflictsChecked);
    }
    [Fact] public async Task Oversize_length_is_rejected_before_allocation()
    {
        foreach(int len in new[]{DeviceHostProtocol.MaxMessageBytes+1,int.MaxValue})
        {
            var ms=new MemoryStream(BitConverter.GetBytes(len));
            await Assert.ThrowsAsync<InvalidDataException>(()=>DeviceHostProtocol.ReadAsync(ms,default));
        }
    }
    static Stream Frame(string json)
    {
        var b=System.Text.Encoding.UTF8.GetBytes(json);
        return new MemoryStream([..BitConverter.GetBytes(b.Length),..b]);
    }
    [Theory]
    [InlineData("""{"type":"bogus","version":1,"id":1}""")]
    [InlineData("""{"version":1,"id":1}""")]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("""{"type":"stop","id":1}""")]
    public async Task Bad_json_bodies_throw(string json)
    {
        await Assert.ThrowsAsync<InvalidDataException>(()=>DeviceHostProtocol.ReadAsync(Frame(json),default));
    }
    [Theory] [InlineData(0)] [InlineData(-1)]
    public async Task Zero_or_negative_length_throws(int len)
    {
        await Assert.ThrowsAsync<InvalidDataException>(()=>DeviceHostProtocol.ReadAsync(new MemoryStream(BitConverter.GetBytes(len)),default));
    }
    [Fact] public async Task Truncated_body_throws()
    {
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,new StopRequest(),default);
        var cut=new MemoryStream(ms.ToArray()[..^2]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>DeviceHostProtocol.ReadAsync(cut,default));
    }
    [Fact] public async Task Partial_length_throws()
    {
        await Assert.ThrowsAsync<InvalidDataException>(()=>DeviceHostProtocol.ReadAsync(new MemoryStream([1,0]),default));
    }
    [Fact] public async Task Clean_end_of_stream_returns_null()
    {
        Assert.Null(await DeviceHostProtocol.ReadAsync(new MemoryStream(),default));
    }
    [Fact] public async Task Version_survives_round_trip()
    {
        Assert.Equal(DeviceHostProtocol.Version,(await RoundTrip(new StopRequest()))!.Version);
        Assert.Equal(7,(await RoundTrip(new StopRequest{Version=7}))!.Version);
    }
    [Fact] public async Task Short_reads_are_reassembled()
    {
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,new SetAllColorsRequest("#ABCDEF"){Id=3},default);
        var back=await DeviceHostProtocol.ReadAsync(new OneByteStream(ms.ToArray()),default);
        Assert.Equal(new SetAllColorsRequest("#ABCDEF"){Id=3},back);
    }
    [Fact] public void Names()
    {
        Assert.Equal("DeadWare.Devices.S-1",DeviceHostProtocol.PipeName("S-1"));
        Assert.Equal(@"Local\DeadWare.Devices.S-1.2",DeviceHostProtocol.InstanceName("S-1",2));
    }
    sealed class OneByteStream(byte[] data):MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)=>base.ReadAsync(buffer[..Math.Min(1,buffer.Length)],ct);
    }
}
