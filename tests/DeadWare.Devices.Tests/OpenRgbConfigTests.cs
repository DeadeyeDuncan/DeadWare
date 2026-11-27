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

    // F4 item 6: the same pass caps OpenRGB's own logs at 2 files, the run in progress and the one before it (LogManager.file_count_limit, which LogManager::configure reads only as
    // an integer), whether there is no file, a file without a LogManager section or one with other LogManager keys.
    const string Minimal="{\"Detectors\":{\"detectors\":{\"SteelSeries Aerox 9 Wireless\":false,\"Corsair iCUE Link System Hub\":true}},\"LogManager\":{\"file_count_limit\":2}}";
    int LogLimit()=>Saved()["LogManager"]!["file_count_limit"]!.GetValue<int>();

    [Fact] public void Fresh_folder_gets_only_the_disabled_detector_and_the_log_cap()
    {
        OpenRgbConfig.Prepare(dir,log);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Minimal),Saved()));
        Assert.Equal(2,LogLimit());
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
        Assert.Equal(2,LogLimit()); // a file without a LogManager section gets one
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
        Assert.Equal(2,LogLimit());
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

    // Lighting 5: the hub's detector is written on every launch, off while DeadWare drives the hub and on again otherwise.
    string LogText(){var path=Path.Combine(dir,"logs","devices.log");return File.Exists(path)?File.ReadAllText(path):"";}
    bool? HubKey()=>(bool?)Saved()["Detectors"]!["detectors"]!["Corsair iCUE Link System Hub"];

    [Fact] public void The_hub_detector_is_written_off_and_on_again()
    {
        OpenRgbConfig.Prepare(dir,log,false);
        Assert.False(HubKey());
        Assert.Contains("openrgb hub detector: off",LogText());
        OpenRgbConfig.Prepare(dir,log);
        Assert.True(HubKey());
        Assert.Contains("openrgb hub detector: on",LogText());
    }

    [Fact] public void The_hub_key_joins_the_other_detectors()
    {
        File.WriteAllText(File_,"{\"Detectors\":{\"detectors\":{\"Corsair Lighting Node Pro\":true,\"Some Other Device\":false}}}");
        OpenRgbConfig.Prepare(dir,log,false);
        var detectors=Saved()["Detectors"]!["detectors"]!;
        Assert.True((bool)detectors["Corsair Lighting Node Pro"]!);
        Assert.False((bool)detectors["Some Other Device"]!);
        Assert.False((bool)detectors["Corsair iCUE Link System Hub"]!);
        Assert.False((bool)detectors["SteelSeries Aerox 9 Wireless"]!);
    }

    [Fact] public void Hub_detector_on_writes_only_true_and_keeps_the_rest()
    {
        File.WriteAllText(File_,"{\"Theme\":\"dark\",\"Detectors\":{\"detectors\":{\"Corsair Lighting Node Pro\":true,\"Some Other Device\":false,\"Corsair iCUE Link System Hub\":false}},\"LogManager\":{\"file_count_limit\":2}}");
        OpenRgbConfig.HubDetectorOn(dir,log);
        var saved=Saved();
        Assert.True((bool)saved["Detectors"]!["detectors"]!["Corsair iCUE Link System Hub"]!);
        Assert.True((bool)saved["Detectors"]!["detectors"]!["Corsair Lighting Node Pro"]!);
        Assert.False((bool)saved["Detectors"]!["detectors"]!["Some Other Device"]!);
        Assert.Equal("dark",(string?)saved["Theme"]);
        Assert.Equal(2,LogLimit());
        Assert.Contains("openrgb hub detector: on (at stop)",LogText());
        Assert.Empty(Directory.GetFiles(dir,"*.tmp"));
    }

    [Fact] public void Hub_detector_on_with_no_folder_does_nothing()
    {
        // "" joins to the relative OpenRGB.json, the current directory's: without the guard a file there would be rewritten.
        var relative=Path.Combine(Environment.CurrentDirectory,OpenRgbConfig.FileName);
        Assert.False(File.Exists(relative)); // the test's own folder holds none
        const string text="{\"Detectors\":{\"detectors\":{\"Corsair iCUE Link System Hub\":false}}}";
        File.WriteAllText(relative,text);
        try
        {
            OpenRgbConfig.HubDetectorOn("",log);
            Assert.Equal(text,File.ReadAllText(relative));
            Assert.DoesNotContain("hub detector",LogText());
        }
        finally{File.Delete(relative);}
        OpenRgbConfig.HubDetectorOn("",log); // and with no such file, nothing appears
        Assert.False(File.Exists(relative));
    }

    [Fact] public void Hub_detector_on_with_no_file_creates_nothing()
    {
        var missing=Path.Combine(dir,"nope");
        OpenRgbConfig.HubDetectorOn(missing,log);
        Assert.False(Directory.Exists(missing));
        var empty=Directory.CreateDirectory(Path.Combine(dir,"empty")).FullName;
        OpenRgbConfig.HubDetectorOn(empty,log);
        Assert.Empty(Directory.GetFileSystemEntries(empty));
        Assert.DoesNotContain("hub detector",LogText());
    }

    [Fact] public void Hub_detector_on_leaves_a_true_key_untouched()
    {
        File.WriteAllText(File_,"{\"Detectors\":{\"detectors\":{\"Corsair iCUE Link System Hub\":true}}}");
        var stamp=new DateTime(2020,1,2,3,4,5,DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(File_,stamp);
        OpenRgbConfig.HubDetectorOn(dir,log);
        Assert.Equal(stamp,File.GetLastWriteTimeUtc(File_));
        Assert.DoesNotContain("hub detector",LogText());
    }

    [Fact] public void A_write_that_fails_is_logged_not_thrown()
    {
        File.WriteAllText(File_,"{\"Detectors\":{\"detectors\":{\"Corsair iCUE Link System Hub\":false}}}");
        using(new FileStream(File_,FileMode.Open,FileAccess.Read,FileShare.Read)) // readable, but nothing can replace it
            OpenRgbConfig.HubDetectorOn(dir,log);
        Assert.Contains("openrgb hub detector couldn't be set on",LogText());
        Assert.False(HubKey());
        Assert.Empty(Directory.GetFiles(dir,"*.tmp"));
    }

    [Fact] public void The_detector_records_what_each_launch_used()
    {
        var detector=new HubDetector();
        Assert.True(detector.On);
        Assert.True(detector.LaunchedOn);
        detector.On=false;
        Assert.False(detector.Launching());
        Assert.False(detector.LaunchedOn);
        detector.On=true;
        Assert.False(detector.LaunchedOn); // no launch since
        Assert.True(detector.Launching());
        Assert.True(detector.LaunchedOn);
    }
}
