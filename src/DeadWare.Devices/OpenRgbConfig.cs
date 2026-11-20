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
internal static class OpenRgbConfig
{
    public const string FileName="OpenRGB.json",AeroxDetector="SteelSeries Aerox 9 Wireless";
    // 2, the largest limit 1.0rc3 keeps from an empty folder (LogManager.cpp:487-491): it counts the files to remove as size - limit + 1 in
    // unsigned arithmetic and caps that at size, so while fewer than limit - 1 logs are there it removes them all. 10 would keep only the
    // log of the run in progress; 2 keeps that one and the one before it, which a crash leaves, until the next start.
    public const int LogLimit=2;

    public static void Prepare(string configDir,HostLog log)
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
        // Nested objects, as OpenRGB reads them: a flat "Detectors.<name>" key is ignored (found under DeadLight).
        if(root["Detectors"] is not JsonObject section) root["Detectors"]=section=new JsonObject();
        if(section["detectors"] is not JsonObject detectors) section["detectors"]=detectors=new JsonObject();
        detectors[AeroxDetector]=false;
        if(root["LogManager"] is not JsonObject logs) root["LogManager"]=logs=new JsonObject();
        logs["file_count_limit"]=LogLimit;
        var tmp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(tmp,root.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
            File.Move(tmp,target,overwrite:true);
        }
        finally{if(File.Exists(tmp)) File.Delete(tmp);}
    }
}
