using System.Text.Json.Serialization;

namespace DeadWare.Core;

public readonly record struct Rgb(byte R,byte G,byte B)
{
    public static Rgb Black=>new(0,0,0);
    public static bool TryParse(string? hex,out Rgb color)
    {
        color=default;
        if(hex is not {Length:7}||hex[0]!='#'||!hex.AsSpan(1).ToString().All(Uri.IsHexDigit)) return false;
        color=new(Convert.ToByte(hex.Substring(1,2),16),Convert.ToByte(hex.Substring(3,2),16),Convert.ToByte(hex.Substring(5,2),16));
        return true;
    }
    public string ToHex()=>$"#{R:X2}{G:X2}{B:X2}";
    public Rgb Scale(int percent){int p=Math.Clamp(percent,0,100);return new(Part(R,p),Part(G,p),Part(B,p));}
    private static byte Part(byte v,int p)=>(byte)Math.Round(v*p/100.0,MidpointRounding.AwayFromZero);
}
public sealed class ZoneColorEntry
{
    public string Color {get;set;}="#4682B4";
    public string ZoneName {get;set;}="";
}
public sealed class LightingSettings
{
    // The lighting.json format this build writes. A file with a higher Version comes from a later DeadWare (after a rollback, say).
    public const int CurrentVersion=5;
    public int Version {get;set;}=1;
    public bool LightsOn {get;set;}=true;
    public int Brightness {get;set;}=100;
    public string DefaultColor {get;set;}="#4682B4";
    public Dictionary<string,ZoneColorEntry> ZoneColors {get;set;}=new();
    public Dictionary<string,string> HardwareModes {get;set;}=new();
    public EffectSettings Effect {get;set;}=new();
    public DeskLayout Layout {get;set;}=DeskLayouts.Empty();
    [JsonIgnore] public Dictionary<string,DeviceTuning> Devices {get;set;}=new();
    // Devices as JSON (whole-branch review 1, D10): written only while it has entries, so an untuned lighting.json is byte for byte what
    // 0.19.0 wrote and a file near the 1 MiB cap from it stays under the cap here; a file without the member reads as an empty table.
    [JsonInclude,JsonPropertyName("Devices"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    internal Dictionary<string,DeviceTuning>? WrittenDevices {get=>Devices is{Count:>0}?Devices:null;set=>Devices=value!;}
    [JsonIgnore] public EverydaySettings Everyday {get;set;}=new();
    [JsonIgnore] public MouseSettings Mouse {get;set;}=new();
    [JsonIgnore] public HubSettings Hub {get;set;}=new();
    // Everyday and Mouse as JSON: written only while they differ from their defaults, so an untouched 0.21.0 file is written byte for byte as before.
    [JsonInclude,JsonPropertyName("Everyday"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    internal EverydaySettings? WrittenEveryday {get=>Everydays.IsDefault(Everyday)?null:Everyday;set=>Everyday=value??new();}
    [JsonInclude,JsonPropertyName("Mouse"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    internal MouseSettings? WrittenMouse {get=>Mouse.SleepMinutes is null?null:Mouse;set=>Mouse=value??new();}
    // Hub as JSON: written only while it differs from its default, so a file whose hub switch was never turned on is written byte for byte as 0.22.0 wrote it.
    [JsonInclude,JsonPropertyName("Hub"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    internal HubSettings? WrittenHub {get=>Hubs.IsDefault(Hub)?null:Hub;set=>Hub=value??new();}
    // A shallow copy sharing every table, with the lights off: the engine paints it for lights-off without touching the saved settings.
    public LightingSettings WithLightsOff(){var s=(LightingSettings)MemberwiseClone();s.LightsOn=false;return s;}
}
// Newer: the file came from a later DeadWare. It is used as far as this build understands it, never quarantined and never rewritten.
public sealed record LightingRead(LightingSettings Settings,string? Error,bool Quarantine,bool Newer=false);
