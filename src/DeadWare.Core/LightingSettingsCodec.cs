using System.Text.Json;

namespace DeadWare.Core;

public static class LightingSettingsCodec
{
    public const int MaxZoneEntries=4096,MaxKeyLength=512;
    // Any failure in parseable data is an external edit: the caller quarantines the file, nothing is repaired. A later DeadWare's file is
    // the exception (Ruling AS): unknown members are skipped as always, values this build can't accept give defaults, and nothing is
    // quarantined, so the file survives for the build that wrote it.
    public static LightingRead Read(string json)
    {
        LightingSettings? s;
        try{s=JsonSerializer.Deserialize<LightingSettings>(json);}
        catch(JsonException e){return new(new(),$"Lighting settings are not valid JSON: {e.Message}",true);}
        if(s is null) return new(new(),"Lighting settings were empty.",true);
        var error=Validate(s);
        if(s.Version>LightingSettings.CurrentVersion) return new(error is null?s:new(),error,false,true);
        return error is null?new(s,null,false):new(new(),error,true);
    }
    public static string Write(LightingSettings settings)
    {
        var error=Validate(settings);
        if(error is null&&settings.Version>LightingSettings.CurrentVersion) error="These lighting settings were saved by a newer DeadWare.";
        if(error is not null) throw new InvalidDataException(error);
        return JsonSerializer.Serialize(settings,new JsonSerializerOptions{WriteIndented=true});
    }
    public static string? Validate(LightingSettings settings)
    {
        if(settings.Version<1) return "Version must be 1 or more.";
        if(settings.Brightness is<0 or>100) return "Brightness must be 0 to 100.";
        if(!Rgb.TryParse(settings.DefaultColor,out _)) return "DefaultColor must be #RRGGBB.";
        if(settings.ZoneColors is null) return "ZoneColors is missing.";
        if(settings.HardwareModes is null) return "HardwareModes is missing.";
        if(settings.ZoneColors.Count>MaxZoneEntries) return "Too many zone colours.";
        if(settings.HardwareModes.Count>MaxZoneEntries) return "Too many hardware modes.";
        foreach(var (key,entry) in settings.ZoneColors)
        {
            if(key.Length>MaxKeyLength) return "A zone key is too long.";
            if(entry is null||!Rgb.TryParse(entry.Color,out _)) return "A zone colour must be #RRGGBB.";
            if(entry.ZoneName is null||entry.ZoneName.Length>MaxKeyLength) return "A zone name is invalid.";
        }
        foreach(var (key,mode) in settings.HardwareModes)
            if(key.Length>MaxKeyLength||mode is null||mode.Length>MaxKeyLength) return "A hardware mode entry is invalid.";
        return null;
    }
}
