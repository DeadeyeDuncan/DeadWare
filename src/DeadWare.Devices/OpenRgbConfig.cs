using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeadWare.Devices;

// Ruling AR, ported from DeadLight's writeOpenRgbConfig (electron/openrgb-config.ts), which runs before every OpenRGB spawn: switch off
// OpenRGB's "SteelSeries Aerox 9 Wireless" detector in <config>\OpenRGB.json. Its firmware query can wedge device detection on a sleeping
// mouse, and the wireless mouse belongs to DeadWare's own driver later; the wired detector ("... Wireless Wired") stays on. The file is
// parsed and merged, keeping every other key (OpenRGB stores its whole detector list there), written to a temporary file and renamed
// into place. A file that can't be read as a JSON object is replaced with the minimal object and logged. A failure to write throws.
internal static class OpenRgbConfig
{
    public const string FileName="OpenRGB.json",AeroxDetector="SteelSeries Aerox 9 Wireless";

    public static void DisableAeroxDetector(string configDir,HostLog log)
    {
        Directory.CreateDirectory(configDir);
        var target=Path.Combine(configDir,FileName);
        JsonObject root;
        try{root=JsonNode.Parse(File.ReadAllText(target)) as JsonObject??throw new JsonException("not a JSON object");}
        catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException){root=new();}
        catch(Exception e) when(e is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Write("OpenRGB.json couldn't be read as JSON, so it was replaced with DeadWare's detector setting: "+e.Message);
            root=new();
        }
        // Nested objects, as OpenRGB reads them: a flat "Detectors.<name>" key is ignored (found under DeadLight).
        if(root["Detectors"] is not JsonObject section) root["Detectors"]=section=new JsonObject();
        if(section["detectors"] is not JsonObject detectors) section["detectors"]=detectors=new JsonObject();
        detectors[AeroxDetector]=false;
        var tmp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(tmp,root.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
            File.Move(tmp,target,overwrite:true);
        }
        finally{if(File.Exists(tmp)) File.Delete(tmp);}
    }
}
