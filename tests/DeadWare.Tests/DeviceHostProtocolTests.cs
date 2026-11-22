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
        yield return [new SetDeviceTuningRequest("k",50,new DeviceCorrection{R=150,G=90,B=0},true){Id=30}];
        yield return [new SetDeviceTuningRequest("k"){Id=31}];
        yield return [new SetDeviceModeRequest("k","wave",70){Id=32}];
        yield return [new SetDeviceModeRequest("k",null){Id=33}];
        yield return [new PreviewEvent(new(){["k#0"]="#010203"},new(){["k"]="0A0B0CFFFFFF"}){Id=34}];
        // Lighting 3b.
        yield return [new SetDeviceKeysRequest("k",new(){[3]=new PaintedKey{Name="Key: W",Color="#FF0000"},[17]=new PaintedKey{Name="Key: A",Color="#00FF00"}}){Id=40}];
        yield return [new SetDeviceKeysRequest("k",null){Id=41}];
        yield return [new SetDeviceLayersRequest("k",[new DeviceLayer{Kind="ripple",Color="#112233",DurationMs=900,Strength=70,Mix="cover",Lights=[new LayerLight{Index=3,Name="Key: W"}]},new DeviceLayer{Kind="effect",Effect="breathing",Speed=12}]){Id=42}];
        yield return [new SetDeviceLayersRequest("k",null){Id=43}];
        yield return [new SaveLookRequest("k","Gaming",true){Id=44}];
        yield return [new SaveLookRequest("k","Work"){Id=45}];
        yield return [new ChooseLookRequest("k","Gaming"){Id=46}];
        yield return [new RenameLookRequest("k","Gaming","Games"){Id=47}];
        yield return [new DeleteLookRequest("k","Games"){Id=48}];
        var st=State();
        yield return [new StateEvent(st with{KeyHook=true,Devices=[st.Devices[0] with{CanPaint=true,Paint=new(){[3]=new PaintedKey{Name="Key: W",Color="#FF0000"}},
            Layers=[new DeviceLayer{Kind="fade",Lights=[new LayerLight{Index=3,Name="Key: W"}]}],Looks=["Gaming","Work"],Look="Gaming",LookChanged=true,SkippedKeys=2,SkippedLayerLights=[1]}]}){Id=49}];
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
                // Lighting 3b: the state with every new member set.
                var sent=((StateEvent)m).State;
                Assert.Equal(sent.KeyHook,s.KeyHook);
                if(!sent.KeyHook){Assert.False(d.CanPaint);Assert.Null(d.Paint);Assert.Null(d.Layers);Assert.Null(d.Looks);Assert.Null(d.Look);Assert.False(d.LookChanged);Assert.Equal(0,d.SkippedKeys);Assert.Null(d.SkippedLayerLights);break;}
                Assert.True(d.CanPaint);
                var key=Assert.Single(d.Paint!);Assert.Equal((3,"Key: W","#FF0000"),(key.Key,key.Value.Name,key.Value.Color));
                var layer=Assert.Single(d.Layers!);Assert.Equal("fade",layer.Kind);Assert.Equal((3,"Key: W"),(Assert.Single(layer.Lights!).Index,layer.Lights![0].Name));
                Assert.Equal(["Gaming","Work"],d.Looks!);Assert.Equal("Gaming",d.Look);Assert.True(d.LookChanged);
                Assert.Equal(2,d.SkippedKeys);Assert.Equal([1],d.SkippedLayerLights!);
                break;
            case SetDeviceKeysRequest k:
                var kb=Assert.IsType<SetDeviceKeysRequest>(back);
                Assert.Equal(k.DeviceKey,kb.DeviceKey);
                Assert.Equal(k.Keys?.Select(e=>(e.Key,e.Value.Name,e.Value.Color)),kb.Keys?.Select(e=>(e.Key,e.Value.Name,e.Value.Color)));
                break;
            case SetDeviceLayersRequest l:
                var lb=Assert.IsType<SetDeviceLayersRequest>(back);
                Assert.Equal(l.DeviceKey,lb.DeviceKey);
                Assert.Equal(System.Text.Json.JsonSerializer.Serialize(l.Layers),System.Text.Json.JsonSerializer.Serialize(lb.Layers));
                break;
            // Step 3a: these carry a class or dictionaries, which a record compares by reference.
            case SetDeviceTuningRequest t:
                var tb=Assert.IsType<SetDeviceTuningRequest>(back);
                Assert.Equal((t.DeviceKey,t.Brightness,t.ResetCorrection),(tb.DeviceKey,tb.Brightness,tb.ResetCorrection));
                Assert.Equal((t.Correction?.R,t.Correction?.G,t.Correction?.B),(tb.Correction?.R,tb.Correction?.G,tb.Correction?.B));
                break;
            case PreviewEvent p:
                var pb=Assert.IsType<PreviewEvent>(back);
                Assert.Equal(p.Colors,pb.Colors);
                Assert.Equal(p.Lights,pb.Lights);
                break;
            default: Assert.Equal(m,back); break;
        }
    }
    static DeskLayout SampleLayout()
    {
        var l=new DeskLayout();
        l.Zones["k:0"]=new DeskPoint(0.25,0.5);
        l.Tower=new TowerGroup{At=new DeskPoint(0.2,0.35),Members=["t:0","t:1"],Excluded=["t:2"]};
        l.Desk=[new DeskRect{X=0.1,Y=0.2,W=0.6,H=0.3},new DeskRect{X=0.05,Y=0.5,W=0.25,H=0.4}];
        l.Unplaced=["u:9"];
        l.Picture="b650m-link-hub";
        return l;
    }
    static void AssertLayout(DeskLayout b)
    {
        Assert.Equal(1,b.Version);
        Assert.Equal(new DeskPoint(0.25,0.5),Assert.Single(b.Zones).Value);
        Assert.Equal("k:0",Assert.Single(b.Zones).Key);
        Assert.Equal(new DeskPoint(0.2,0.35),b.Tower.At);
        Assert.Equal(["t:0","t:1"],b.Tower.Members);
        Assert.Equal(["t:2"],b.Tower.Excluded);
        Assert.Equal(2,b.Desk.Count);
        Assert.Equal((0.1,0.2,0.6,0.3),(b.Desk[0].X,b.Desk[0].Y,b.Desk[0].W,b.Desk[0].H));
        Assert.Equal((0.05,0.5,0.25,0.4),(b.Desk[1].X,b.Desk[1].Y,b.Desk[1].W,b.Desk[1].H));
        Assert.Equal(["u:9"],b.Unplaced);
        Assert.Equal("b650m-link-hub",b.Picture);
    }
    [Fact] public async Task Set_effect_round_trips()
    {
        var p=new EffectParams{Speed=70,AngleDeg=90,Spread=20,Colors=["#112233","#445566"]};
        var b=Assert.IsType<SetEffectRequest>(await RoundTrip(new SetEffectRequest("Wave",p){Id=21}));
        Assert.Equal(21,b.Id);
        Assert.Equal("Wave",b.Kind);
        Assert.NotNull(b.Params);
        Assert.Equal((70.0,90.0,20.0),(b.Params!.Speed,b.Params.AngleDeg,b.Params.Spread));
        Assert.Equal(["#112233","#445566"],b.Params.Colors);
        var s=Assert.IsType<SetEffectRequest>(await RoundTrip(new SetEffectRequest("Static",null){Id=22}));
        Assert.Equal("Static",s.Kind);
        Assert.Null(s.Params);
    }
    [Fact] public async Task Set_layout_round_trips()
    {
        var b=Assert.IsType<SetLayoutRequest>(await RoundTrip(new SetLayoutRequest(SampleLayout()){Id=23}));
        Assert.Equal(23,b.Id);
        AssertLayout(b.Layout);
    }
    [Fact] public async Task Subscribe_preview_round_trips()
    {
        foreach(bool on in new[]{true,false})
        {
            var b=Assert.IsType<SubscribePreviewRequest>(await RoundTrip(new SubscribePreviewRequest(on){Id=24}));
            Assert.Equal(on,b.On);
            Assert.Equal(24,b.Id);
        }
    }
    [Fact] public async Task Preview_event_round_trips()
    {
        var b=Assert.IsType<PreviewEvent>(await RoundTrip(new PreviewEvent(new(){["k:0"]="#010203",["k:1"]="#FFFFFF"}){Id=25}));
        Assert.Equal(25,b.Id);
        Assert.Equal(2,b.Colors.Count);
        Assert.Equal("#010203",b.Colors["k:0"]);
        Assert.Equal("#FFFFFF",b.Colors["k:1"]);
    }
    // Step 3a: a frame from a host without per-light colours still reads, with none.
    [Fact] public async Task A_preview_without_lights_still_reads()
    {
        var b=Assert.IsType<PreviewEvent>(await DeviceHostProtocol.ReadAsync(Frame("""{"type":"preview","Version":1,"Id":35,"Colors":{"k#0":"#010203"}}"""),default));
        Assert.Equal(35,b.Id);
        Assert.Equal("#010203",b.Colors["k#0"]);
        Assert.Null(b.Lights);
    }
    [Fact] public async Task The_new_requests_use_their_discriminators()
    {
        var t=Assert.IsType<SetDeviceTuningRequest>(await DeviceHostProtocol.ReadAsync(Frame("""{"type":"setDeviceTuning","Version":1,"DeviceKey":"k","Brightness":20}"""),default));
        Assert.Equal(("k",(int?)20,(DeviceCorrection?)null,false),(t.DeviceKey,t.Brightness,t.Correction,t.ResetCorrection));
        var m=Assert.IsType<SetDeviceModeRequest>(await DeviceHostProtocol.ReadAsync(Frame("""{"type":"setDeviceMode","Version":1,"DeviceKey":"k","Kind":"cycle"}"""),default));
        Assert.Equal(("k","cycle",40),(m.DeviceKey,m.Kind,m.Speed));
    }
    [Fact] public void Unknown_device_has_its_plain_words()
        =>Assert.Equal("That device isn't listed any more. It may have been unplugged.",HostReplyCodes.Explain(HostReplyCodes.UnknownDevice));
    // Step 3a: the device's tuning, mode and key map travel with the state; a state from a host without them reads brightness 100 and nulls.
    [Fact] public async Task A_device_views_tuning_mode_and_key_map_round_trip()
    {
        var st=State();
        var v=st.Devices[0] with{Brightness=35,Correction=new DeviceCorrection{R=180,G=0,B=99},LightingMode=new DeviceMode{Kind="wave",Speed=12},
            ModeProblem="This keyboard doesn't report its key positions.",Keyboard=true,Wave=true,Keys=new KeyMapView(["Esc","F1"],[0.0,null],[0.5,1.0],0.04)};
        var b=Assert.Single(Assert.IsType<StateEvent>(await RoundTrip(new StateEvent(st with{Devices=[v]}){Id=36})).State.Devices);
        Assert.Equal(35,b.Brightness);
        Assert.Equal((180,0,99),(b.Correction!.R,b.Correction.G,b.Correction.B));
        Assert.Equal(("wave",12),(b.LightingMode!.Kind,b.LightingMode.Speed));
        Assert.Equal("This keyboard doesn't report its key positions.",b.ModeProblem);
        Assert.True(b.Keyboard);Assert.True(b.Wave);
        Assert.Equal(["Esc","F1"],b.Keys!.Names);
        Assert.Equal([0.0,null],b.Keys.X);
        Assert.Equal([0.5,1.0],b.Keys.Y);
        Assert.Equal(0.04,b.Keys.KeyWidth);
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,new StateEvent(State()){Id=37},default);
        var node=System.Text.Json.Nodes.JsonNode.Parse(ms.ToArray().AsSpan(4))!;
        var dv=node["State"]!["Devices"]![0]!.AsObject();
        foreach(var name in new[]{"Brightness","Correction","LightingMode","ModeProblem","Keyboard","Wave","Keys"}) dv.Remove(name);
        var old=Assert.Single(Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(Frame(node.ToJsonString()),default)).State.Devices);
        Assert.Equal(100,old.Brightness);
        Assert.Null(old.Correction);Assert.Null(old.LightingMode);Assert.Null(old.ModeProblem);Assert.Null(old.Keys);
        Assert.False(old.Keyboard);Assert.False(old.Wave);
    }
    // Lighting 3b: a state from a 3a host, without the new members, reads with their defaults.
    [Fact] public async Task Old_views_read_without_the_new_members()
    {
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,new StateEvent(State()){Id=50},default);
        var node=System.Text.Json.Nodes.JsonNode.Parse(ms.ToArray().AsSpan(4))!;
        var state=node["State"]!.AsObject();
        var dv=state["Devices"]![0]!.AsObject();
        // The defaults are written today, so each is removed to stand for the form a 3a host writes.
        Assert.True(state.Remove("KeyHook"));
        foreach(var name in new[]{"CanPaint","Paint","Layers","Looks","Look","LookChanged","SkippedKeys","SkippedLayerLights"}) Assert.True(dv.Remove(name),name);
        var back=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(Frame(node.ToJsonString()),default)).State;
        Assert.False(back.KeyHook);
        var d=Assert.Single(back.Devices);
        Assert.False(d.CanPaint);Assert.Null(d.Paint);Assert.Null(d.Layers);Assert.Null(d.Looks);Assert.Null(d.Look);Assert.False(d.LookChanged);
        Assert.Equal(0,d.SkippedKeys);Assert.Null(d.SkippedLayerLights);
        Assert.Equal("Static",d.Mode);
    }
    [Fact] public void The_3b_reply_codes_have_their_plain_words()
    {
        Assert.Equal("That device isn't a keyboard DeadWare can paint key by key.",HostReplyCodes.Explain(HostReplyCodes.NotPaintable));
        Assert.Equal("That key isn't on this keyboard.",HostReplyCodes.Explain(HostReplyCodes.BadKey));
        Assert.Equal("That layer isn't of a kind DeadWare knows.",HostReplyCodes.Explain(HostReplyCodes.BadLayer));
        Assert.Equal("A keyboard can have up to 8 layers.",HostReplyCodes.Explain(HostReplyCodes.TooManyLayers));
        Assert.Equal("A keyboard can have up to 12 saved looks. Delete one first.",HostReplyCodes.Explain(HostReplyCodes.TooManyLooks));
        Assert.Equal("A look's name must be 1 to 40 characters.",HostReplyCodes.Explain(HostReplyCodes.LookName));
        Assert.Equal("There is already a look with that name.",HostReplyCodes.Explain(HostReplyCodes.LookTaken));
        Assert.Equal("That look isn't saved any more.",HostReplyCodes.Explain(HostReplyCodes.NoLook));
        Assert.Equal("There isn't room to save that. Delete a saved look first.",HostReplyCodes.Explain(HostReplyCodes.NoRoom));
        Assert.Equal(("not paintable","bad key","bad layer","too many layers","too many looks"),(HostReplyCodes.NotPaintable,HostReplyCodes.BadKey,HostReplyCodes.BadLayer,HostReplyCodes.TooManyLayers,HostReplyCodes.TooManyLooks));
        Assert.Equal(("look name","look taken","no look","no room"),(HostReplyCodes.LookName,HostReplyCodes.LookTaken,HostReplyCodes.NoLook,HostReplyCodes.NoRoom));
    }
    [Fact] public async Task The_3b_requests_use_their_discriminators()
    {
        Assert.IsType<SetDeviceKeysRequest>(await DeviceHostProtocol.ReadAsync(Frame("""{"type":"setDeviceKeys","Version":1,"DeviceKey":"k","Keys":{"3":{"Name":"W","Color":"#FF0000"}}}"""),default));
        Assert.IsType<SetDeviceLayersRequest>(await DeviceHostProtocol.ReadAsync(Frame("""{"type":"setDeviceLayers","Version":1,"DeviceKey":"k","Layers":[]}"""),default));
        Assert.False(Assert.IsType<SaveLookRequest>(await DeviceHostProtocol.ReadAsync(Frame("""{"type":"saveLook","Version":1,"DeviceKey":"k","Name":"A"}"""),default)).Overwrite);
        Assert.IsType<ChooseLookRequest>(await DeviceHostProtocol.ReadAsync(Frame("""{"type":"chooseLook","Version":1,"DeviceKey":"k","Name":"A"}"""),default));
        Assert.IsType<RenameLookRequest>(await DeviceHostProtocol.ReadAsync(Frame("""{"type":"renameLook","Version":1,"DeviceKey":"k","Name":"A","NewName":"B"}"""),default));
        Assert.IsType<DeleteLookRequest>(await DeviceHostProtocol.ReadAsync(Frame("""{"type":"deleteLook","Version":1,"DeviceKey":"k","Name":"A"}"""),default));
    }
    [Fact] public async Task State_event_carries_effect_and_layout()
    {
        var st=State();
        st.Settings.Effect=new EffectSettings{Kind="Wave",Wave=new EffectParams{Speed=65,AngleDeg=45,Spread=10,Colors=["#AA0000","#00BB00"]},Breathing=new EffectParams{Speed=12,AngleDeg=3,Spread=88,Colors=["#0000CC"]},Spectrum=new EffectParams{Speed=99,AngleDeg=180,Spread=1}};
        st.Settings.Layout=SampleLayout();
        var e=Assert.IsType<StateEvent>(await RoundTrip(new StateEvent(st){Id=26})).State.Settings;
        Assert.Equal("Wave",e.Effect.Kind);
        Assert.Equal((65.0,45.0,10.0),(e.Effect.Wave.Speed,e.Effect.Wave.AngleDeg,e.Effect.Wave.Spread));
        Assert.Equal(["#AA0000","#00BB00"],e.Effect.Wave.Colors);
        Assert.Equal((12.0,3.0,88.0),(e.Effect.Breathing.Speed,e.Effect.Breathing.AngleDeg,e.Effect.Breathing.Spread));
        Assert.Equal(["#0000CC"],e.Effect.Breathing.Colors);
        Assert.Equal((99.0,180.0,1.0),(e.Effect.Spectrum.Speed,e.Effect.Spectrum.AngleDeg,e.Effect.Spectrum.Spread));
        Assert.Empty(e.Effect.Spectrum.Colors);
        AssertLayout(e.Layout);
    }
    [Fact] public async Task A_state_with_4096_layout_zones_fits_the_frame()
    {
        var st=State();
        for(int i=0;i<4096;i++) st.Settings.Layout.Zones[new string((char)('a'+i%26),50)+i.ToString("D10")]=new DeskPoint(i/4096.0,0.123456789);
        Assert.All(st.Settings.Layout.Zones.Keys,k=>Assert.Equal(60,k.Length));
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,new StateEvent(st){Id=27},default);
        Assert.True(ms.Length-4<=DeviceHostProtocol.MaxMessageBytes);
        ms.Position=0;
        var back=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(ms,default));
        Assert.Equal(4096,back.State.Settings.Layout.Zones.Count);
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
