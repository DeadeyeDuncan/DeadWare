using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace DeadWare.Core;
public enum HostStatus { Starting, Waiting, Running, Blocked, OpenRgbMissing, OpenRgbFailed, Stopping }
public sealed record ZoneView(string Key,int Index,string Name,int LedCount,string Color,bool OwnColor);
// Mode: what the device is set to now (a hardware device shows Static while lights are off). OwnEffect: its own hardware effect, and
// StaysLitWhenOff: whether it stays lit while lights are off (a hardware effect and no Static mode); both hold whatever Lights on is.
public sealed record DeviceView(string Key,string Name,int Type,PaintKind Kind,string? Mode,string? OwnEffect,bool StaysLitWhenOff,IReadOnlyList<ZoneView> Zones,
    int Brightness=100,DeviceCorrection? Correction=null,DeviceMode? LightingMode=null,string? ModeProblem=null,bool Keyboard=false,bool Wave=false,KeyMapView? Keys=null,
    // Lighting 3b: whether its keys can be painted one by one, its painted keys, layers, saved look names in saved order, the look last
    // loaded and whether the keyboard has changed since; the painted keys and each layer's listed lights that no longer match its lights.
    bool CanPaint=false,Dictionary<int,PaintedKey>? Paint=null,List<DeviceLayer>? Layers=null,IReadOnlyList<string>? Looks=null,string? Look=null,bool LookChanged=false,
    int SkippedKeys=0,IReadOnlyList<int>? SkippedLayerLights=null,
    // Lighting 4: the mouse's connection, battery and sleep time, for a device that is the Aerox 9 mouse.
    MouseView? Mouse=null,
    // Lighting 5: how the Corsair hub is lit, one of HubRoutes; null for any other device.
    string? Hub=null);
// Connection is one of MouseConnections. Battery is a percentage and Charging whether it is charging, null while unknown; Stale: the reading is old.
// SleepMinutes is the mouse's own sleep time when known; SleepSent: the host has sent the chosen one.
public sealed record MouseView(string Connection,int? Battery,bool? Charging,bool Stale,int? SleepMinutes,bool SleepSent);
public static class MouseConnections{public const string Cable="cable",Wireless="wireless",Receiver="receiver",None="none";}
// ConflictsChecked: the host has completed a conflict scan since it started, so an empty Conflicts list means nothing was found (polish
// item 2). One that never scans, such as one whose OpenRGB settings folder was refused, leaves it false. Optional on the wire, and false
// when absent, so the protocol stays version 1.
public sealed record HostState(HostStatus Status,string Message,bool Elevated,IReadOnlyList<LightingConflict> Conflicts,IReadOnlyList<DeviceView> Devices,LightingSettings Settings,bool ConflictsChecked=false,
    bool KeyHook=false,
    // Lighting 4: why the lights are off beyond the saved switch (LightsOff.Idle, Locked or Waking), and plain-words problems with the shortcut and with everyday rules.
    string? LightsOffReason=null,string? ShortcutProblem=null,string? EverydayProblem=null,
    // Lighting 5: the Corsair hub, null from a host that does not know it.
    HubView? Hub=null);
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
[JsonDerivedType(typeof(SetEffectRequest),"setEffect")]
[JsonDerivedType(typeof(SetLayoutRequest),"setLayout")]
[JsonDerivedType(typeof(SubscribePreviewRequest),"subscribePreview")]
[JsonDerivedType(typeof(PreviewEvent),"preview")]
[JsonDerivedType(typeof(SetDeviceTuningRequest),"setDeviceTuning")]
[JsonDerivedType(typeof(SetDeviceModeRequest),"setDeviceMode")]
[JsonDerivedType(typeof(SetDeviceKeysRequest),"setDeviceKeys")]
[JsonDerivedType(typeof(SetDeviceLayersRequest),"setDeviceLayers")]
[JsonDerivedType(typeof(SaveLookRequest),"saveLook")]
[JsonDerivedType(typeof(ChooseLookRequest),"chooseLook")]
[JsonDerivedType(typeof(RenameLookRequest),"renameLook")]
[JsonDerivedType(typeof(DeleteLookRequest),"deleteLook")]
[JsonDerivedType(typeof(SetEverydayRequest),"setEveryday")]
[JsonDerivedType(typeof(SetMouseSleepRequest),"setMouseSleep")]
[JsonDerivedType(typeof(ReadHubRequest),"readHub")]
[JsonDerivedType(typeof(SetHubModeRequest),"setHubMode")]
[JsonDerivedType(typeof(HubReadingsEvent),"hubReadings")]
public abstract record HostMessage
{
    [JsonRequired] public int Version { get; init; } = DeviceHostProtocol.Version;
    public long Id { get; init; }
}
public sealed record GetStateRequest : HostMessage;
public sealed record SetLightsOnRequest(bool On) : HostMessage;
public sealed record SetEverydayRequest(EverydaySettings Everyday) : HostMessage;
public sealed record SetMouseSleepRequest(int? Minutes) : HostMessage;
public sealed record ReadHubRequest : HostMessage;
// Mode: HubModes.OpenRgb or HubModes.Driven.
// Shown: the reading the take-over dialog showed (null: it showed the no-reading form). The helper compares the check against exactly this.
public sealed record SetHubModeRequest(string Mode,HubReading? Shown=null) : HostMessage;
public sealed record HubReadingsEvent(HubReadings Readings) : HostMessage;
public sealed record SetBrightnessRequest(int Percent) : HostMessage;
public sealed record SetAllColorsRequest(string Color) : HostMessage;
public sealed record SetZoneColorRequest(string ZoneKey,string? Color) : HostMessage;
public sealed record ReplaceColorsRequest(string DefaultColor,Dictionary<string,ZoneColorEntry> ZoneColors) : HostMessage;
public sealed record RescanRequest : HostMessage;
public sealed record StopRequest : HostMessage;
public sealed record Reply(bool Ok,string? Error) : HostMessage;
public sealed record StateEvent(HostState State) : HostMessage;
public sealed record SetEffectRequest(string Kind,EffectParams? Params) : HostMessage;
public sealed record SetLayoutRequest(DeskLayout Layout) : HostMessage;
public sealed record SubscribePreviewRequest(bool On) : HostMessage;
// Colors: zone key -> #RRGGBB. Lights (step 3a): device key -> its lights' colours packed as upper-case RRGGBB per light, no separator, for
// each painted device with a key map. Both are the colours before tuning and brightness.
public sealed record PreviewEvent(Dictionary<string,string> Colors,Dictionary<string,string>? Lights=null) : HostMessage;
// Step 3a. A null member leaves that setting as it is; ResetCorrection removes the correction (and wins over one sent with it).
public sealed record SetDeviceTuningRequest(string DeviceKey,int? Brightness=null,DeviceCorrection? Correction=null,bool ResetCorrection=false) : HostMessage;
// Kind null: follow the desk effect.
public sealed record SetDeviceModeRequest(string DeviceKey,string? Kind,int Speed=40) : HostMessage;
// Lighting 3b. Keys and Layers are the whole set; null or empty clears it. A look's name is trimmed and compared ignoring case.
public sealed record SetDeviceKeysRequest(string DeviceKey,Dictionary<int,PaintedKey>? Keys) : HostMessage;
public sealed record SetDeviceLayersRequest(string DeviceKey,List<DeviceLayer>? Layers) : HostMessage;
public sealed record SaveLookRequest(string DeviceKey,string Name,bool Overwrite=false) : HostMessage;
public sealed record ChooseLookRequest(string DeviceKey,string Name) : HostMessage;
public sealed record RenameLookRequest(string DeviceKey,string Name,string NewName) : HostMessage;
public sealed record DeleteLookRequest(string DeviceKey,string Name) : HostMessage;
[JsonSourceGenerationOptions(UseStringEnumConverter=true)]
[JsonSerializable(typeof(HostMessage))]
internal sealed partial class DeviceHostJsonContext : JsonSerializerContext;
// The short codes a failed Reply can carry (the host's own sentences, such as a validation message, are shown as they are), and their plain words.
public static class HostReplyCodes
{
    public const string Starting="starting",Unsupported="unsupported",RescanFailed="rescan failed",UnknownZone="unknown zone",CouldntSave="couldn't save settings",Version="version",InternalError="internal error",UnknownDevice="unknown device";
    public const string NotPaintable="not paintable",BadKey="bad key",BadLayer="bad layer",TooManyLayers="too many layers",TooManyLooks="too many looks",
        LookName="look name",LookTaken="look taken",NoLook="no look",NoRoom="no room";
    public const string BadEveryday="bad everyday",BadSleep="bad sleep time",ShortcutSlow="shortcut slow";
    public const string HubBusy="hub busy",HubStale="hub stale",BadHubMode="bad hub mode";
    public static string Explain(string why)=>why switch
    {
        Starting=>"The lighting helper is still starting. Try again in a moment.",
        UnknownZone=>"That zone isn't listed any more. Its device may have been unplugged.",
        UnknownDevice=>"That device isn't listed any more. It may have been unplugged.",
        CouldntSave=>"DeadWare couldn't save your lighting settings, so the change wasn't made.",
        InternalError=>"The lighting helper hit an error. Details are in its log, devices.log.",
        RescanFailed=>"The lighting helper lost its connection to OpenRGB and is reconnecting.",
        Unsupported=>"The lighting helper didn't recognise the change. Restart DeadWare.",
        Version=>"The lighting helper is a different version. Restart DeadWare.",
        NotPaintable=>"That device isn't a keyboard DeadWare can paint key by key.",
        BadKey=>"That key isn't on this keyboard.",
        BadLayer=>"That layer isn't of a kind DeadWare knows.",
        TooManyLayers=>"A keyboard can have up to 8 layers.",
        TooManyLooks=>"A keyboard can have up to 12 saved looks. Delete one first.",
        LookName=>"A look's name must be 1 to 40 characters.",
        LookTaken=>"There is already a look with that name.",
        NoLook=>"That look isn't saved any more.",
        NoRoom=>"There isn't room to save that. Delete a saved look first.",
        BadEveryday=>"Those everyday settings aren't valid.",
        BadSleep=>"That sleep time isn't one of the choices.",
        ShortcutSlow=>"Windows didn't answer about the shortcut in time. Try again.",
        HubBusy=>"DeadWare is still switching. Try again in a moment.",
        HubStale=>"That reading was more than two minutes old. Click the switch again.",
        BadHubMode=>"That hub mode isn't one DeadWare knows.",
        _=>why,
    };
}
public static class DeviceHostProtocol
{
    // 4 MiB (F4 item 2, whole-branch review D12): a state message carries the whole settings with the device list. The settings take at
    // most 1 MiB, since lighting.json's cap is on their written form, which escapes non-ASCII text just as this compact form does (up to six
    // bytes a character) and only adds indentation; the rest is room for a large device list. Window and host come from the same build.
    public const int Version = 1, MaxMessageBytes = 4 * 1_048_576;
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
