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
    public int Version {get;set;}=1;
    public bool LightsOn {get;set;}=true;
    public int Brightness {get;set;}=100;
    public string DefaultColor {get;set;}="#4682B4";
    public Dictionary<string,ZoneColorEntry> ZoneColors {get;set;}=new();
    public Dictionary<string,string> HardwareModes {get;set;}=new();
}
public sealed record LightingRead(LightingSettings Settings,string? Error,bool Quarantine);
