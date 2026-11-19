using System.Text.Json.Nodes;
using DeadWare.Devices;
using Xunit;

namespace DeadWare.Devices.Tests;

// Ruling AR, DeadLight's writeOpenRgbConfig (electron/openrgb-config.ts): before every OpenRGB start the "SteelSeries Aerox 9 Wireless"
// detector is switched off in OpenRGB.json, keeping every other key. Its firmware query can wedge OpenRGB's device detection on a
// sleeping mouse, and the wireless mouse belongs to DeadWare's own driver later; the wired detector stays on.
public sealed class OpenRgbConfigTests:IDisposable
{
    readonly string dir=Directory.CreateTempSubdirectory("dw-orgbcfg").FullName;
    readonly HostLog log;
    string File_=>Path.Combine(dir,OpenRgbConfig.FileName);
    public OpenRgbConfigTests(){log=new HostLog(Path.Combine(dir,"logs"));}
    public void Dispose(){log.Dispose();try{Directory.Delete(dir,true);}catch{}}
    JsonNode Saved()=>JsonNode.Parse(File.ReadAllText(File_))!;

    // F4 item 6: the same pass caps OpenRGB's own logs at 10 files (LogManager.file_count_limit, which LogManager::configure reads only as
    // an integer), whether there is no file, a file without a LogManager section or one with other LogManager keys.
    const string Minimal="{\"Detectors\":{\"detectors\":{\"SteelSeries Aerox 9 Wireless\":false}},\"LogManager\":{\"file_count_limit\":10}}";
    int LogLimit()=>Saved()["LogManager"]!["file_count_limit"]!.GetValue<int>();

    [Fact] public void Fresh_folder_gets_only_the_disabled_detector_and_the_log_cap()
    {
        OpenRgbConfig.Prepare(dir,log);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Minimal),Saved()));
        Assert.Equal(10,LogLimit());
    }

    [Fact] public void Every_other_key_and_detector_survives()
    {
        File.WriteAllText(File_,"{\"Theme\":\"dark\",\"Detectors\":{\"detectors\":{\"Corsair Lighting Node Pro\":true,\"SteelSeries Aerox 9 Wireless\":true,\"SteelSeries Aerox 9 Wireless Wired\":true},\"hid_safe_mode\":false},\"Plugins\":[1,2]}");
        OpenRgbConfig.Prepare(dir,log);
        var saved=Saved();
        Assert.Equal("dark",(string?)saved["Theme"]);
        Assert.Equal(2,saved["Plugins"]!.AsArray().Count);
        Assert.False((bool)saved["Detectors"]!["hid_safe_mode"]!);
        var detectors=saved["Detectors"]!["detectors"]!;
        Assert.False((bool)detectors["SteelSeries Aerox 9 Wireless"]!);
        Assert.True((bool)detectors["SteelSeries Aerox 9 Wireless Wired"]!);
        Assert.True((bool)detectors["Corsair Lighting Node Pro"]!);
        Assert.Equal(10,LogLimit()); // a file without a LogManager section gets one
        Assert.Empty(Directory.GetFiles(dir,"*.tmp"));
    }

    [Fact] public void The_log_cap_joins_the_other_LogManager_keys()
    {
        File.WriteAllText(File_,"{\"Theme\":\"dark\",\"LogManager\":{\"log_file\":true,\"loglevel\":6,\"file_count_limit\":0,\"log_console\":false}}");
        OpenRgbConfig.Prepare(dir,log);
        var saved=Saved();
        Assert.Equal("dark",(string?)saved["Theme"]);
        var logs=saved["LogManager"]!.AsObject();
        Assert.Equal(4,logs.Count);
        Assert.True((bool)logs["log_file"]!);
        Assert.Equal(6,(int)logs["loglevel"]!);
        Assert.False((bool)logs["log_console"]!);
        Assert.Equal(10,LogLimit());
        Assert.False((bool)saved["Detectors"]!["detectors"]!["SteelSeries Aerox 9 Wireless"]!);
    }

    [Fact] public void Unparseable_file_is_replaced_with_the_minimal_object_and_logged()
    {
        File.WriteAllText(File_,"{ not json");
        OpenRgbConfig.Prepare(dir,log);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Minimal),Saved()));
        Assert.Contains("OpenRGB.json",File.ReadAllText(Path.Combine(dir,"logs","devices.log")));
    }

    [Fact] public void A_file_that_cant_be_written_throws()
    {
        Directory.CreateDirectory(File_); // OpenRGB.json is a folder: nothing can replace it
        Assert.ThrowsAny<Exception>(()=>OpenRgbConfig.Prepare(dir,log));
    }
}
