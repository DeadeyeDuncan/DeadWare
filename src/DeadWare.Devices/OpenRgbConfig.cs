using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeadWare.Devices;

// Ruling AR, ported from DeadLight's writeOpenRgbConfig (electron/openrgb-config.ts), which runs before every OpenRGB spawn: switch off
// OpenRGB's "SteelSeries Aerox 9 Wireless" detector in <config>\OpenRGB.json. Its firmware query can wedge device detection on a sleeping
// mouse, and the wireless mouse belongs to DeadWare's own driver later; the wired detector ("... Wireless Wired") stays on. The file is
// parsed and merged, keeping every other key (OpenRGB stores its whole detector list there), written to a temporary file and renamed
// into place. A file that can't be read as a JSON object is replaced with the minimal object and logged. A failure to write throws.
// F4 item 6: the same pass caps OpenRGB's own logs, one a start in <config>\logs, at LogLimit files, keeping every other LogManager key.
// OpenRGB 1.0rc3 reads LogManager.file_count_limit, as an integer only, from the OpenRGB.json it loads while it is built, which is this
// folder's since the engine sets APPDATA (F4 item 1), and before opening the new log removes the oldest files whose names match the log
// name's template, OpenRGB_<timestamp>.log by default (LogManager.cpp:65-160, rotate_logs at :405-510).
// Lighting 5: the same pass writes OpenRGB's detector for the iCUE LINK System Hub, off while DeadWare's own driver has the hub (two programs
// must never drive it at once) and on otherwise; HubDetectorOn puts it back on when DeadWare stops OpenRGB for good.
internal static class OpenRgbConfig
{
    public const string FileName="OpenRGB.json",AeroxDetector="SteelSeries Aerox 9 Wireless",HubDetectorName="Corsair iCUE Link System Hub";
    // 2, the largest limit 1.0rc3 keeps from an empty folder (LogManager.cpp:487-491): it counts the files to remove as size - limit + 1 in
    // unsigned arithmetic and caps that at size, so while fewer than limit - 1 logs are there it removes them all. 10 would keep only the
    // log of the run in progress; 2 keeps that one and the one before it, which a crash leaves, until the next start.
    public const int LogLimit=2;

    public static void Prepare(string configDir,HostLog log,bool hubDetector=true)
    {
        Directory.CreateDirectory(configDir);
        var target=Path.Combine(configDir,FileName);
        JsonObject root;
        try{root=JsonNode.Parse(File.ReadAllText(target)) as JsonObject??throw new JsonException("not a JSON object");}
        catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException){root=new();}
        catch(Exception e) when(e is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Write("OpenRGB.json couldn't be read as JSON, so it was replaced with DeadWare's own settings: "+e.Message);
            root=new();
        }
        var detectors=Detectors(root);
        detectors[AeroxDetector]=false;
        detectors[HubDetectorName]=hubDetector;
        if(root["LogManager"] is not JsonObject logs) root["LogManager"]=logs=new JsonObject();
        logs["file_count_limit"]=LogLimit;
        Write(target,root);
        log.Write("openrgb hub detector: "+(hubDetector?"on":"off"));
    }

    // Only ever changes an existing OpenRGB.json whose hub key is not already true: it makes no folder and no file, and never throws.
    public static void HubDetectorOn(string configDir,HostLog log)
    {
        try
        {
            if(configDir.Length==0) return;
            var target=Path.Combine(configDir,FileName);
            if(!File.Exists(target)) return;
            JsonObject root;
            try{root=JsonNode.Parse(File.ReadAllText(target)) as JsonObject??throw new JsonException("not a JSON object");}
            catch(Exception e) when(e is JsonException or FileNotFoundException or DirectoryNotFoundException){return;}
            var detectors=Detectors(root);
            if(detectors[HubDetectorName] is JsonValue current&&current.TryGetValue<bool>(out var already)&&already) return;
            detectors[HubDetectorName]=true;
            Write(target,root);
            log.Write("openrgb hub detector: on (at stop)");
        }
        catch(Exception e){try{log.Write("openrgb hub detector couldn't be set on: "+e.Message);}catch{}}
    }

    // Nested objects, as OpenRGB reads them: a flat "Detectors.<name>" key is ignored (found under DeadLight).
    static JsonObject Detectors(JsonObject root)
    {
        if(root["Detectors"] is not JsonObject section) root["Detectors"]=section=new JsonObject();
        if(section["detectors"] is not JsonObject detectors) section["detectors"]=detectors=new JsonObject();
        return detectors;
    }

    static void Write(string target,JsonObject root)
    {
        var tmp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(tmp,root.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
            File.Move(tmp,target,overwrite:true);
        }
        finally{if(File.Exists(tmp)) File.Delete(tmp);}
    }
}
