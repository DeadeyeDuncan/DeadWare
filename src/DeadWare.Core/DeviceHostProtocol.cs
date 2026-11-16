using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace DeadWare.Core;
public enum HostStatus { Starting, Waiting, Running, Blocked, OpenRgbMissing, OpenRgbFailed, Stopping }
public sealed record ZoneView(string Key,int Index,string Name,int LedCount,string Color,bool OwnColor);
public sealed record DeviceView(string Key,string Name,int Type,PaintKind Kind,string? Mode,bool StaysLitWhenOff,IReadOnlyList<ZoneView> Zones);
public sealed record HostState(HostStatus Status,string Message,bool Elevated,IReadOnlyList<LightingConflict> Conflicts,IReadOnlyList<DeviceView> Devices,LightingSettings Settings);
[JsonPolymorphic(TypeDiscriminatorPropertyName="type")]
[JsonDerivedType(typeof(GetStateRequest),"getState")]
[JsonDerivedType(typeof(SetLightsOnRequest),"setLightsOn")]
[JsonDerivedType(typeof(SetBrightnessRequest),"setBrightness")]
[JsonDerivedType(typeof(SetAllColorsRequest),"setAllColors")]
[JsonDerivedType(typeof(SetZoneColorRequest),"setZoneColor")]
[JsonDerivedType(typeof(ReplaceColorsRequest),"replaceColors")]
[JsonDerivedType(typeof(RescanRequest),"rescan")]
[JsonDerivedType(typeof(StopRequest),"stop")]
[JsonDerivedType(typeof(Reply),"reply")]
[JsonDerivedType(typeof(StateEvent),"state")]
public abstract record HostMessage
{
    [JsonRequired] public int Version { get; init; } = DeviceHostProtocol.Version;
    public long Id { get; init; }
}
public sealed record GetStateRequest : HostMessage;
public sealed record SetLightsOnRequest(bool On) : HostMessage;
public sealed record SetBrightnessRequest(int Percent) : HostMessage;
public sealed record SetAllColorsRequest(string Color) : HostMessage;
public sealed record SetZoneColorRequest(string ZoneKey,string? Color) : HostMessage;
public sealed record ReplaceColorsRequest(string DefaultColor,Dictionary<string,ZoneColorEntry> ZoneColors) : HostMessage;
public sealed record RescanRequest : HostMessage;
public sealed record StopRequest : HostMessage;
public sealed record Reply(bool Ok,string? Error) : HostMessage;
public sealed record StateEvent(HostState State) : HostMessage;
[JsonSourceGenerationOptions(UseStringEnumConverter=true)]
[JsonSerializable(typeof(HostMessage))]
internal sealed partial class DeviceHostJsonContext : JsonSerializerContext;
public static class DeviceHostProtocol
{
    public const int Version = 1, MaxMessageBytes = 1_048_576;
    public static string PipeName(string sid)=>"DeadWare.Devices."+sid;
    public static string InstanceName(string sid,int session)=>$@"Local\DeadWare.Devices.{sid}.{session}";
    public static async Task WriteAsync(Stream stream,HostMessage message,CancellationToken token)
    {
        byte[] body=JsonSerializer.SerializeToUtf8Bytes(message,DeviceHostJsonContext.Default.HostMessage);
        if(body.Length>MaxMessageBytes) throw new InvalidDataException("Message too large.");
        var frame=new byte[4+body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame,body.Length);
        body.CopyTo(frame,4);
        await stream.WriteAsync(frame,token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task<HostMessage?> ReadAsync(Stream stream,CancellationToken token)
    {
        var head=new byte[4];
        int got=await ReadFullAsync(stream,head,token).ConfigureAwait(false);
        if(got==0) return null;
        if(got<4) throw new InvalidDataException("Truncated length prefix.");
        int length=BinaryPrimitives.ReadInt32LittleEndian(head);
        if(length<=0||length>MaxMessageBytes) throw new InvalidDataException($"Invalid message length {length}.");
        var body=new byte[length];
        if(await ReadFullAsync(stream,body,token).ConfigureAwait(false)<length) throw new InvalidDataException("Truncated message body.");
        try { return JsonSerializer.Deserialize(body,DeviceHostJsonContext.Default.HostMessage) ?? throw new InvalidDataException("Empty message."); }
        catch(Exception e) when(e is JsonException or NotSupportedException) { throw new InvalidDataException("Malformed message.",e); }
    }
    static async Task<int> ReadFullAsync(Stream stream,byte[] buffer,CancellationToken token)
    {
        int total=0;
        while(total<buffer.Length)
        {
            int n=await stream.ReadAsync(buffer.AsMemory(total),token).ConfigureAwait(false);
            if(n==0) break;
            total+=n;
        }
        return total;
    }
}
